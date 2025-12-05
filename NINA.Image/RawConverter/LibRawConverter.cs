#region "copyright"

/*
    Copyright © 2025 Nico Trost <nico.trost57@gmail.com> and the PI.N.S. contributors

    This file is part of PI 'N' Stars.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Core.Utility;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace NINA.Image.RawConverter {

    /// <summary>
    /// RAW converter using LibRaw library
    /// LibRaw is specifically designed for RAW image processing and works reliably on Linux
    /// </summary>
    internal class LibRawConverter : IRawConverter {
        private readonly IImageDataFactory imageDataFactory;

        public LibRawConverter(IImageDataFactory imageDataFactory) {
            this.imageDataFactory = imageDataFactory;
            try {
                DllLoader.LoadDll(Path.Combine("LibRaw", "libraw.so"));
            } catch (Exception ex) {
                Logger.Error($"Failed to load LibRaw: {ex.Message}");
                throw;
            }
        }

        public Task<IImageData> Convert(
            MemoryStream s,
            int bitDepth,
            string rawType,
            ImageMetaData metaData,
            CancellationToken token = default) {
            return Task.Run(() => {
                using (MyStopWatch.Measure("LibRaw Conversion")) {
                    IntPtr processor = IntPtr.Zero;
                    try {
                        // Create LibRaw processor instance
                        processor = LibRawInterop.libraw_init(0);
                        if (processor == IntPtr.Zero) {
                            throw new Exception("Failed to initialize LibRaw processor");
                        }

                        // Get raw bytes from stream
                        byte[] rawBytes = s.ToArray();

                        Logger.Debug($"LibRaw: Processing {rawBytes.Length} bytes");

                        // Open RAW from buffer
                        int ret = LibRawInterop.libraw_open_buffer(processor, rawBytes, (uint)rawBytes.Length);
                        if (ret != 0) {
                            throw new Exception($"LibRaw open_buffer failed: {GetLibRawError(ret)}");
                        }

                        Logger.Debug("LibRaw: Buffer opened successfully");

                        // Unpack the RAW data
                        ret = LibRawInterop.libraw_unpack(processor);
                        if (ret != 0) {
                            throw new Exception($"LibRaw unpack failed: {GetLibRawError(ret)}");
                        }

                        Logger.Debug("LibRaw: Data unpacked successfully");

                        // Process the RAW data to get pixel data
                        ret = LibRawInterop.libraw_dcraw_process(processor);
                        if (ret != 0) {
                            throw new Exception($"LibRaw dcraw_process failed: {GetLibRawError(ret)}");
                        }

                        Logger.Debug("LibRaw: Data processed successfully");

                        // Create processed image in memory (LibRaw outputs TIFF format)
                        IntPtr memImage = LibRawInterop.libraw_dcraw_make_mem_image(processor, out int imgError);
                        if (memImage == IntPtr.Zero) {
                            throw new Exception($"LibRaw dcraw_make_mem_image failed: {GetLibRawError(imgError)}");
                        }

                        Logger.Debug("LibRaw: Memory image created successfully");

                        try {
                            // libraw_processed_image_t structure (from libraw_types.h):
                            // enum LibRaw_image_formats type
                            // ushort height
                            // ushort width 
                            // ushort colors
                            // ushort bits
                            // unsigned int data_size
                            // unsigned char data[1]  <- embedded data starts here
                            
                            // Read the structure fields
                            int type = Marshal.ReadInt32(memImage, 0);
                            ushort height = (ushort)Marshal.ReadInt16(memImage, 4);
                            ushort width = (ushort)Marshal.ReadInt16(memImage, 6);
                            ushort colors = (ushort)Marshal.ReadInt16(memImage, 8);
                            ushort bits = (ushort)Marshal.ReadInt16(memImage, 10);
                            uint dataSize = (uint)Marshal.ReadInt32(memImage, 12);

                            Logger.Debug($"LibRaw: Image {width}x{height}, type={type}, colors={colors}, bits={bits}, size={dataSize} bytes");

                            if (width == 0 || height == 0 || dataSize == 0) {
                                throw new Exception("Invalid image data from LibRaw");
                            }

                            // The pixel data is embedded directly after the header at offset 16
                            IntPtr pixelDataPtr = (IntPtr)((long)memImage + 16);
                            
                            Logger.Debug($"LibRaw: Pixel data pointer = {pixelDataPtr}");

                            // Copy pixel data from unmanaged memory
                            byte[] tiffData = new byte[(int)dataSize];
                            
                            unsafe {
                                fixed (byte* pData = tiffData) {
                                    Buffer.MemoryCopy((void*)pixelDataPtr, (void*)pData, dataSize, dataSize);
                                }
                            }

                            Logger.Debug($"LibRaw: Copied {dataSize} bytes of TIFF data");
                            
                            // Check first bytes to see if it's valid TIFF
                            string magicBytes = BitConverter.ToString(tiffData, 0, Math.Min(16, tiffData.Length));
                            Logger.Debug($"LibRaw: TIFF data starts with: {magicBytes}");

                            token.ThrowIfCancellationRequested();

                            // Decode the TIFF data
                            try {
                                using (var tiffStream = new MemoryStream(tiffData)) {
                                    var decoder = new TiffBitmapDecoder(
                                        tiffStream, 
                                        BitmapCreateOptions.PreservePixelFormat,
                                        BitmapCacheOption.OnLoad);

                                    if (decoder.Frames.Count == 0) {
                                        throw new Exception("TIFF from LibRaw has no frames");
                                    }

                                    var frame = decoder.Frames[0];
                                    ushort[] pixelArray = new ushort[frame.PixelWidth * frame.PixelHeight];
                                    frame.CopyPixels(pixelArray, frame.PixelWidth * sizeof(ushort), 0);

                                    Logger.Debug($"LibRaw: Decoded TIFF, got {pixelArray.Length} pixels from {frame.PixelWidth}x{frame.PixelHeight}");

                                    // Create image data
                                    var imageArray = new ImageArray(flatArray: pixelArray, rawData: rawBytes, rawType: rawType);
                                    var data = imageDataFactory.CreateBaseImageData(
                                        imageArray: imageArray,
                                        width: frame.PixelWidth,
                                        height: frame.PixelHeight,
                                        bitDepth: bitDepth,
                                        isBayered: true,
                                        metaData: metaData);

                                    Logger.Debug("LibRaw: Image data created successfully");

                                    return Task.FromResult<IImageData>(data);
                                }
                            } catch (Exception) {
                                // If TIFF decoding fails, the data might not be TIFF but raw RGB
                                // Try interpreting as raw RGB pixels
                                Logger.Debug("LibRaw: Attempting to interpret as raw RGB data");
                                
                                int expectedPixels = width * height;
                                int bytesPerPixel = (int)dataSize / expectedPixels;
                                
                                Logger.Debug($"LibRaw: Data appears to be {bytesPerPixel} bytes per pixel");
                                
                                // Convert RGB to grayscale by taking average or first channel
                                ushort[] grayPixels = new ushort[expectedPixels];
                                
                                for (int i = 0; i < expectedPixels; i++) {
                                    // Average R, G, B channels
                                    int baseIdx = i * bytesPerPixel;
                                    if (baseIdx + 2 < tiffData.Length) {
                                        int r = tiffData[baseIdx];
                                        int g = tiffData[baseIdx + 1];
                                        int b = tiffData[baseIdx + 2];
                                        grayPixels[i] = (ushort)((r + g + b) / 3);
                                    }
                                }
                                
                                Logger.Debug($"LibRaw: Converted RGB to grayscale, got {expectedPixels} pixels");
                                
                                // Create image data from grayscale
                                var imageArray = new ImageArray(flatArray: grayPixels, rawData: rawBytes, rawType: rawType);
                                var data = imageDataFactory.CreateBaseImageData(
                                    imageArray: imageArray,
                                    width: width,
                                    height: height,
                                    bitDepth: 8,
                                    isBayered: false,
                                    metaData: metaData);

                                Logger.Debug("LibRaw: Grayscale image data created successfully");

                                return Task.FromResult<IImageData>(data);
                            }
                        } finally {
                            // Clean up processed image memory
                            if (memImage != IntPtr.Zero) {
                                LibRawInterop.libraw_dcraw_clear_mem(memImage);
                            }
                        }
                    } catch (Exception ex) {
                        Logger.Error($"LibRaw conversion failed: {ex.Message}");
                        throw;
                    } finally {
                        if (processor != IntPtr.Zero) {
                            LibRawInterop.libraw_close(processor);
                        }
                    }
                }
            });
        }

        private static string GetLibRawError(int errorCode) {
            try {
                IntPtr errorPtr = LibRawInterop.libraw_strerror(errorCode);
                if (errorPtr != IntPtr.Zero) {
                    return Marshal.PtrToStringAnsi(errorPtr) ?? $"Unknown error code {errorCode}";
                }
            } catch {
                // Ignore
            }
            return $"Unknown error code {errorCode}";
        }
    }
}
