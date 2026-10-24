#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Core.Enum;
using NINA.Core.Utility;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Image.RawConverter {

    internal class LibRawConverter : IRawConverter {
        private const string LibRawDllName = "libraw.so";

        // Unlike upstream N.I.N.A., which pins a bundled libraw_0_22_1.dll and reads rawdata.raw_image
        // through offsets valid only for that exact build, this converter may end up loading whatever
        // system libraw is installed (DllLoader falls back to LD_LIBRARY_PATH). It therefore relies only
        // on layout guarantees that hold across LibRaw releases:
        // - imgdata.image is the first member of libraw_data_t (offset 0), filled by libraw_raw2image
        // - the offsets below index the libraw_iparams_t returned by the libraw_get_iparams C getter;
        //   that layout is unchanged since LibRaw 0.20: https://github.com/LibRaw/LibRaw/blob/0.22.1/libraw/libraw_types.h
        // LibRaw iparams field semantics: https://www.libraw.org/docs/API-datastruct.html
        private const int ImageParamsColorsOffset = 340;
        private const int ImageParamsFiltersOffset = 344;
        private const int ImageParamsColorDescriptionOffset = 420;
        private const int ImageChannelCount = 4;
        private const uint LeafCatchlightFilters = 1;
        private const uint XTransFilters = 9;

        private static readonly object loadLock = new object();
        private static bool dllLoaded;

        private readonly IImageDataFactory imageDataFactory;

        public LibRawConverter(IImageDataFactory imageDataFactory) {
            this.imageDataFactory = imageDataFactory;
            EnsureDllLoaded();
        }

        public Task<IImageData> Convert(
            MemoryStream s,
            int bitDepth,
            bool bitScaling,
            string rawType,
            ImageMetaData metaData,
            CancellationToken token = default) {
            return Task.Run(() => {
                using (MyStopWatch.Measure()) {
                    token.ThrowIfCancellationRequested();

                    var rawBytes = s.ToArray();
                    var handle = GCHandle.Alloc(rawBytes, GCHandleType.Pinned);
                    var processor = IntPtr.Zero;
                    try {
                        processor = LibRawNative.Init(0);
                        if (processor == IntPtr.Zero) {
                            throw new InvalidOperationException("LibRaw initialization failed.");
                        }

                        ThrowIfError(
                            LibRawNative.OpenBuffer(processor, handle.AddrOfPinnedObject(), (UIntPtr)rawBytes.Length),
                            "LibRaw open buffer");

                        token.ThrowIfCancellationRequested();

                        ThrowIfError(LibRawNative.Unpack(processor), "LibRaw unpack");

                        token.ThrowIfCancellationRequested();

                        return CreateImageData(processor, rawBytes, rawType, bitDepth, bitScaling, metaData);
                    } finally {
                        if (processor != IntPtr.Zero) {
                            LibRawNative.Close(processor);
                        }

                        if (handle.IsAllocated) {
                            handle.Free();
                        }
                    }
                }
            }, token);
        }

        private static void EnsureDllLoaded() {
            lock (loadLock) {
                if (dllLoaded) {
                    return;
                }

                DllLoader.LoadDll(Path.Combine("Libraw", LibRawDllName));
                dllLoaded = true;
            }
        }

        private IImageData CreateImageData(IntPtr processor, byte[] rawBytes, string rawType, int bitDepth, bool bitScaling, ImageMetaData metaData) {
            ThrowIfError(LibRawNative.Raw2Image(processor), "LibRaw raw2image");

            var width = LibRawNative.GetIWidth(processor);
            var height = LibRawNative.GetIHeight(processor);
            if (width <= 0 || height <= 0) {
                throw new InvalidOperationException("LibRaw returned invalid RAW image dimensions.");
            }

            // raw2image fills imgdata.image with the visible area (margins already cropped) as 4 ushort
            // channel slots per pixel of which only the pixel's CFA channel is populated.
            var image = Marshal.ReadIntPtr(processor, 0);
            if (image == IntPtr.Zero) {
                throw new NotSupportedException("LibRaw did not return an unpacked RAW image buffer.");
            }

            var frame = new ActiveFrame(left: 0, top: 0, width: width, height: height, rowStride: width, pixelStride: ImageChannelCount);
            var copiedFrame = CopyUshortFrame(image, frame, bitDepth, bitScaling);
            LibRawNative.FreeImage(processor);

            if (copiedFrame.EffectiveBitDepth != bitDepth) {
                Logger.Warning($"LibRaw RAW bit depth setting {bitDepth} adjusted to {copiedFrame.EffectiveBitDepth}; maximum unpacked pixel value is {copiedFrame.MaxPixelValue}.");
            }

            var whiteLevel = TryGetWhiteLevel(processor);
            var dataBitDepth = GetDataBitDepth(
                copiedFrame.OutputBitDepth,
                copiedFrame.EffectiveBitDepth,
                copiedFrame.MaxPixelValue,
                whiteLevel);

            if (dataBitDepth != copiedFrame.OutputBitDepth) {
                Logger.Debug($"LibRaw RAW white level {whiteLevel} spans {dataBitDepth} bits; reporting {dataBitDepth} instead of {copiedFrame.OutputBitDepth} bits for the unchanged pixel data.");
            }

            ApplyBayerPatternMetadata(processor, metaData);
            return CreateImageData(copiedFrame.Pixels, rawBytes, rawType, frame.Width, frame.Height, dataBitDepth, metaData);
        }

        private static int TryGetWhiteLevel(IntPtr processor) {
            try {
                return LibRawNative.GetColorMaximum(processor);
            } catch (EntryPointNotFoundException ex) {
                Logger.Debug($"LibRaw does not export libraw_get_color_maximum; RAW bit depth follows the profile setting. {ex.Message}");
                return 0;
            }
        }

        // The bit depth reported for the copied pixels is the full range that statistics, the preview stretch
        // and the flat histogram targets measure against. It never changes the pixel values; it only stops a
        // profile bit depth above the camera's (16 by default) from claiming a range the RAW data cannot reach.
        // Otherwise a 14-bit NEF reads four times darker than it is and a saturated frame stretches to black.
        // LibRaw's white level (color.maximum, black level included) gives the reach of the RAW data. It only
        // ever lowers the bit depth: a profile setting below the data was already raised while copying, and a
        // pixel above the white level keeps the bit depth needed to hold it.
        private static int GetDataBitDepth(int outputBitDepth, int effectiveBitDepth, ushort maxPixelValue, int whiteLevel) {
            if (whiteLevel <= 0) {
                return outputBitDepth;
            }

            // Bit scaling shifted every pixel left by the difference; without scaling both are equal.
            var appliedShift = outputBitDepth - effectiveBitDepth;
            var reach = Math.Max((long)whiteLevel, maxPixelValue) << appliedShift;
            if (reach > ushort.MaxValue) {
                return outputBitDepth;
            }

            return Math.Min(outputBitDepth, GetRequiredBitDepth((ushort)reach));
        }

        private IImageData CreateImageData(ushort[] pixels, byte[] rawBytes, string rawType, int width, int height, int bitDepth, ImageMetaData metaData) {
            var imageArray = new ImageArray(flatArray: pixels, rawData: rawBytes, rawType: rawType);
            return imageDataFactory.CreateBaseImageData(
                imageArray: imageArray,
                width: width,
                height: height,
                bitDepth: bitDepth,
                isBayered: true,
                metaData: metaData);
        }

        private static void ApplyBayerPatternMetadata(IntPtr processor, ImageMetaData metaData) {
            if (metaData.Camera.BayerPattern == BayerPatternEnum.None) {
                return;
            }

            if (TryReadVisibleBayerPattern(processor, out var bayerPattern)) {
                metaData.Camera.SensorType = bayerPattern;
                metaData.Camera.BayerOffsetX = 0;
                metaData.Camera.BayerOffsetY = 0;
            }
        }

        private static bool TryReadVisibleBayerPattern(IntPtr processor, out SensorType bayerPattern) {
            bayerPattern = SensorType.Monochrome;

            var imageParams = LibRawNative.GetImageParams(processor);
            if (imageParams == IntPtr.Zero) {
                return false;
            }

            var colors = Marshal.ReadInt32(imageParams, ImageParamsColorsOffset);
            var filters = (uint)Marshal.ReadInt32(imageParams, ImageParamsFiltersOffset);
            // filters == 0 is full-color/monochrome data; 1 and 9 are special non-2x2 Bayer layouts.
            if (colors < 3 || filters == 0 || filters == LeafCatchlightFilters || filters == XTransFilters) {
                return false;
            }

            // cdesc maps COLOR's numeric index to a channel letter; it is not the Bayer pattern by itself.
            var colorDescription = new byte[5];
            Marshal.Copy(IntPtr.Add(imageParams, ImageParamsColorDescriptionOffset), colorDescription, 0, colorDescription.Length);

            // LibRaw COLOR(row,col) is defined relative to the visible image area, not the full sensor.
            // That matches the frame copied above after applying top/left margins and keeps odd-margin
            // cameras from reporting the wrong Bayer phase at N.I.N.A.'s pixel (0,0).
            // Source: https://www.libraw.org/node/2144
            Span<char> pattern = stackalloc char[4];
            var patternIndex = 0;
            for (var row = 0; row < 2; row++) {
                for (var column = 0; column < 2; column++) {
                    var colorIndex = LibRawNative.Color(processor, row, column);
                    if (colorIndex < 0 || colorIndex >= colorDescription.Length || colorDescription[colorIndex] == 0) {
                        return false;
                    }

                    pattern[patternIndex++] = char.ToUpperInvariant((char)colorDescription[colorIndex]);
                }
            }

            return TryGetBayerPattern(new string(pattern), out bayerPattern);
        }

        private static bool TryGetBayerPattern(string pattern, out SensorType bayerPattern) {
            bayerPattern = pattern switch {
                "RGGB" => SensorType.RGGB,
                "BGGR" => SensorType.BGGR,
                "GBRG" => SensorType.GBRG,
                "GRBG" => SensorType.GRBG,
                "GRGB" => SensorType.GRGB,
                "GBGR" => SensorType.GBGR,
                "RGBG" => SensorType.RGBG,
                "BGRG" => SensorType.BGRG,
                _ => SensorType.Monochrome
            };

            return bayerPattern != SensorType.Monochrome;
        }

        private static int NormalizeBitDepth(int configuredBitDepth) {
            return configuredBitDepth is >= 1 and <= 16 ? configuredBitDepth : 16;
        }

        private static int GetRequiredBitDepth(ushort value) {
            var bitDepth = 0;
            do {
                bitDepth++;
                value >>= 1;
            } while (value > 0);

            return bitDepth;
        }

        private static ushort GetMaxValueForBitDepth(int bitDepth) {
            return bitDepth >= 16 ? ushort.MaxValue : (ushort)((1 << bitDepth) - 1);
        }

        private static void ThrowIfError(int result, string operation) {
            if (result == 0) {
                return;
            }

            var message = Marshal.PtrToStringAnsi(LibRawNative.StrError(result));
            if (string.IsNullOrWhiteSpace(message)) {
                message = $"LibRaw error {result}";
            }

            throw new InvalidOperationException($"{operation} failed: {message}");
        }

        private static unsafe CopiedFrame CopyUshortFrame(IntPtr image, ActiveFrame frame, int bitDepth, bool bitScaling) {
            var pixels = new ushort[frame.Width * frame.Height];
            var source = (ushort*)image.ToPointer();
            var effectiveBitDepth = NormalizeBitDepth(bitDepth);
            var maxValueForEffectiveBitDepth = GetMaxValueForBitDepth(effectiveBitDepth);
            var shift = bitScaling ? 16 - effectiveBitDepth : 0;
            var writtenPixels = 0;
            ushort maxPixelValue = 0;

            fixed (ushort* destination = pixels) {
                for (var y = 0; y < frame.Height; y++) {
                    var sourceRow = source + ((long)(frame.Top + y) * frame.RowStride + frame.Left) * frame.PixelStride;
                    var destinationRow = destination + (y * frame.Width);
                    for (var x = 0; x < frame.Width; x++) {
                        // Only one channel slot per pixel carries the CFA sample, the others stay zero,
                        // so the maximum across the slots recovers the mosaiced value.
                        var sourcePixel = sourceRow + ((long)x * frame.PixelStride);
                        var value = sourcePixel[0];
                        for (var channel = 1; channel < frame.PixelStride; channel++) {
                            if (sourcePixel[channel] > value) {
                                value = sourcePixel[channel];
                            }
                        }
                        if (value > maxPixelValue) {
                            maxPixelValue = value;
                        }

                        // A too-low profile bit depth would shift real high-range RAW values too far left.
                        // Use the unpacked data as a hard lower bound while copying into managed memory.
                        if (value > maxValueForEffectiveBitDepth) {
                            var previousShift = shift;
                            effectiveBitDepth = GetRequiredBitDepth(value);
                            maxValueForEffectiveBitDepth = GetMaxValueForBitDepth(effectiveBitDepth);
                            shift = bitScaling ? 16 - effectiveBitDepth : 0;

                            if (bitScaling && previousShift > shift) {
                                ShiftCopiedPixelsRight(destination, writtenPixels, previousShift - shift);
                            }
                        }

                        destinationRow[x] = bitScaling && shift > 0 ? (ushort)(value << shift) : value;
                        writtenPixels++;
                    }
                }
            }

            return new CopiedFrame(pixels, maxPixelValue, effectiveBitDepth, bitScaling ? 16 : effectiveBitDepth);
        }

        private static unsafe void ShiftCopiedPixelsRight(ushort* pixels, int length, int shift) {
            for (var i = 0; i < length; i++) {
                pixels[i] = (ushort)(pixels[i] >> shift);
            }
        }

        private readonly struct ActiveFrame {
            public ActiveFrame(int left, int top, int width, int height, int rowStride)
                : this(left, top, width, height, rowStride, 1) {
            }

            public ActiveFrame(int left, int top, int width, int height, int rowStride, int pixelStride) {
                Left = left;
                Top = top;
                Width = width;
                Height = height;
                RowStride = rowStride;
                PixelStride = pixelStride;
            }

            public int Left { get; }
            public int Top { get; }
            public int Width { get; }
            public int Height { get; }
            public int RowStride { get; }

            // Number of ushort slots per pixel; 4 for libraw's imgdata.image channel layout.
            public int PixelStride { get; }
        }

        private readonly struct CopiedFrame {
            public CopiedFrame(ushort[] pixels, ushort maxPixelValue, int effectiveBitDepth, int outputBitDepth) {
                Pixels = pixels;
                MaxPixelValue = maxPixelValue;
                EffectiveBitDepth = effectiveBitDepth;
                OutputBitDepth = outputBitDepth;
            }

            public ushort[] Pixels { get; }
            public ushort MaxPixelValue { get; }
            public int EffectiveBitDepth { get; }
            public int OutputBitDepth { get; }
        }

        private static class LibRawNative {

            // C API documentation for these helpers:
            // https://www.libraw.org/docs/API-C.html

            [DllImport(LibRawDllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "libraw_init")]
            public static extern IntPtr Init(uint flags);

            [DllImport(LibRawDllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "libraw_open_buffer")]
            public static extern int OpenBuffer(IntPtr processor, IntPtr buffer, UIntPtr bufferSize);

            [DllImport(LibRawDllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "libraw_unpack")]
            public static extern int Unpack(IntPtr processor);

            [DllImport(LibRawDllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "libraw_raw2image")]
            public static extern int Raw2Image(IntPtr processor);

            [DllImport(LibRawDllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "libraw_free_image")]
            public static extern void FreeImage(IntPtr processor);

            [DllImport(LibRawDllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "libraw_get_iwidth")]
            public static extern int GetIWidth(IntPtr processor);

            [DllImport(LibRawDllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "libraw_get_iheight")]
            public static extern int GetIHeight(IntPtr processor);

            // Returns imgdata.color.maximum, the RAW white level including the black level.
            [DllImport(LibRawDllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "libraw_get_color_maximum")]
            public static extern int GetColorMaximum(IntPtr processor);

            [DllImport(LibRawDllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "libraw_get_iparams")]
            public static extern IntPtr GetImageParams(IntPtr processor);

            [DllImport(LibRawDllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "libraw_COLOR")]
            public static extern int Color(IntPtr processor, int row, int column);

            [DllImport(LibRawDllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "libraw_close")]
            public static extern void Close(IntPtr processor);

            [DllImport(LibRawDllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "libraw_strerror")]
            public static extern IntPtr StrError(int errorCode);
        }
    }
}
