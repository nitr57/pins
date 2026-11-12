#region "copyright"

/*
    Copyright © 2025 Nico Trost <nico.trost57@gmail.com> and the PI.N.S. contributors

    This file is part of PI 'N' Stars.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using OpenCvSharp;

namespace System.Windows.Media.Imaging {
    /// <summary>
    /// Converts a BitmapSource to a different pixel format using OpenCV
    /// </summary>
    public class FormatConvertedBitmap : BitmapSource {
        private BitmapSource _source;
        private Media.PixelFormat _destinationFormat;

        public FormatConvertedBitmap() {
        }

        public FormatConvertedBitmap(BitmapSource source, Media.PixelFormat destinationFormat, BitmapPalette palette, double alphaThreshold) {
            if (source == null) {
                throw new ArgumentNullException(nameof(source));
            }

            _source = source;
            _destinationFormat = destinationFormat;
            Initialize();
        }

        public BitmapSource Source {
            get => _source;
            set => _source = value;
        }

        public Media.PixelFormat DestinationFormat {
            get => _destinationFormat;
            set => _destinationFormat = value;
        }

        public void BeginInit() {
            // Initialization started
        }

        public void EndInit() {
            // Initialization complete, now perform the conversion
            Initialize();
        }

        private void Initialize() {
            _mat?.Dispose();
            if (_source == null) {
                _mat = new Mat();
                AddMemoryPressure();
                return;
            }

            using var sourceLease = _source.LeaseMat();
            Mat sourceMat = sourceLease.Mat;
            if (sourceMat.Empty()) {
                _mat = new Mat();
                AddMemoryPressure();
                return;
            }

            _mat = ConvertFormat(sourceMat, _source.Format, _destinationFormat);
            AddMemoryPressure();
        }

        // 257 = 65535/255: the exact integer scale factor between an 8-bit and a 16-bit channel.
        private const double EightToSixteenBitScale = 257.0;
        private const double SixteenToEightBitScale = 1.0 / EightToSixteenBitScale;

        private Mat ConvertFormat(Mat source, Media.PixelFormat sourceFormat, Media.PixelFormat destFormat) {
            ColorConversionCodes? conversionCode = null;

            // The compat layer keeps every 32-bit image as straight BGRA (Graphics blends straight alpha, a Bitmap has
            // no premultiplied form), and a CV_8UC4 Mat always reports Bgra32. So Bgra32, Pbgra32 and Bgr32 are one
            // layout here: converting between them copies, and Bgr32, which has no alpha, makes every pixel opaque.
            if (source.Type() == MatType.CV_8UC4 && IsThirtyTwoBit(sourceFormat) && IsThirtyTwoBit(destFormat)) {
                Mat result = source.Clone();

                if (destFormat == Media.PixelFormats.Bgr32) {
                    using Mat opaque = new Mat(result.Size(), MatType.CV_8UC1, Scalar.All(255));
                    Cv2.InsertChannel(opaque, result, 3);
                }

                return result;
            }

            // Same format in and out: only when the Mat really has that layout, since Format reports Bgr24 for layouts
            // it does not know.
            if (sourceFormat == destFormat && MatTypeOf(destFormat) is MatType expected && source.Type() == expected) {
                return source.Clone();
            }

            if (destFormat == Media.PixelFormats.Gray8) {
                if (sourceFormat == Media.PixelFormats.Bgr24) {
                    conversionCode = ColorConversionCodes.BGR2GRAY;
                } else if (sourceFormat == Media.PixelFormats.Bgra32 || sourceFormat == Media.PixelFormats.Pbgra32 || sourceFormat == Media.PixelFormats.Bgr32) {
                    conversionCode = ColorConversionCodes.BGRA2GRAY;
                } else if (sourceFormat == Media.PixelFormats.Gray16) {
                    Mat result = new Mat();
                    source.ConvertTo(result, MatType.CV_8UC1, SixteenToEightBitScale);
                    return result;
                } else if (sourceFormat == Media.PixelFormats.Gray8 || sourceFormat == Media.PixelFormats.Indexed8) {
                    return source.Clone();
                } else if (sourceFormat == Media.PixelFormats.Rgb48) {
                    // Cv2.CvtColor preserves depth, so BGR2GRAY on a 16-bit source yields 16-bit gray;
                    // scale that down to 8-bit rather than losing color info by downscaling first.
                    using Mat gray16 = new Mat();
                    Cv2.CvtColor(source, gray16, ColorConversionCodes.BGR2GRAY);
                    Mat result = new Mat();
                    gray16.ConvertTo(result, MatType.CV_8UC1, SixteenToEightBitScale);
                    return result;
                }
            } else if (destFormat == Media.PixelFormats.Gray16) {
                if (sourceFormat == Media.PixelFormats.Gray16) {
                    return source.Clone();
                } else if (sourceFormat == Media.PixelFormats.Gray8 || sourceFormat == Media.PixelFormats.Indexed8) {
                    Mat result = new Mat();
                    source.ConvertTo(result, MatType.CV_16UC1, EightToSixteenBitScale);
                    return result;
                } else if (sourceFormat == Media.PixelFormats.Bgr24) {
                    using Mat gray8 = new Mat();
                    Cv2.CvtColor(source, gray8, ColorConversionCodes.BGR2GRAY);
                    Mat result = new Mat();
                    gray8.ConvertTo(result, MatType.CV_16UC1, EightToSixteenBitScale);
                    return result;
                } else if (sourceFormat == Media.PixelFormats.Bgra32 || sourceFormat == Media.PixelFormats.Pbgra32 || sourceFormat == Media.PixelFormats.Bgr32) {
                    using Mat gray8 = new Mat();
                    Cv2.CvtColor(source, gray8, ColorConversionCodes.BGRA2GRAY);
                    Mat result = new Mat();
                    gray8.ConvertTo(result, MatType.CV_16UC1, EightToSixteenBitScale);
                    return result;
                } else if (sourceFormat == Media.PixelFormats.Rgb48) {
                    // Already 16-bit; BGR2GRAY on a 16-bit source stays 16-bit, no rescale needed.
                    Mat result = new Mat();
                    Cv2.CvtColor(source, result, ColorConversionCodes.BGR2GRAY);
                    return result;
                }
            } else if (destFormat == Media.PixelFormats.Bgr24) {
                if (sourceFormat == Media.PixelFormats.Gray8 || sourceFormat == Media.PixelFormats.Indexed8) {
                    conversionCode = ColorConversionCodes.GRAY2BGR;
                } else if (sourceFormat == Media.PixelFormats.Bgra32 || sourceFormat == Media.PixelFormats.Pbgra32 || sourceFormat == Media.PixelFormats.Bgr32) {
                    conversionCode = ColorConversionCodes.BGRA2BGR;
                } else if (sourceFormat == Media.PixelFormats.Gray16) {
                    using Mat gray8 = new Mat();
                    source.ConvertTo(gray8, MatType.CV_8UC1, SixteenToEightBitScale);
                    Mat result = new Mat();
                    Cv2.CvtColor(gray8, result, ColorConversionCodes.GRAY2BGR);
                    return result;
                } else if (sourceFormat == Media.PixelFormats.Rgb48) {
                    Mat result = new Mat();
                    source.ConvertTo(result, MatType.CV_8UC3, SixteenToEightBitScale);
                    return result;
                }
            } else if (destFormat == Media.PixelFormats.Bgra32 || destFormat == Media.PixelFormats.Pbgra32 || destFormat == Media.PixelFormats.Bgr32) {
                if (sourceFormat == Media.PixelFormats.Gray8 || sourceFormat == Media.PixelFormats.Indexed8) {
                    conversionCode = ColorConversionCodes.GRAY2BGRA;
                } else if (sourceFormat == Media.PixelFormats.Bgr24) {
                    conversionCode = ColorConversionCodes.BGR2BGRA;
                } else if (sourceFormat == Media.PixelFormats.Gray16) {
                    using Mat gray8 = new Mat();
                    source.ConvertTo(gray8, MatType.CV_8UC1, SixteenToEightBitScale);
                    Mat result = new Mat();
                    Cv2.CvtColor(gray8, result, ColorConversionCodes.GRAY2BGRA);
                    return result;
                } else if (sourceFormat == Media.PixelFormats.Rgb48) {
                    using Mat bgr24 = new Mat();
                    source.ConvertTo(bgr24, MatType.CV_8UC3, SixteenToEightBitScale);
                    Mat result = new Mat();
                    Cv2.CvtColor(bgr24, result, ColorConversionCodes.BGR2BGRA);
                    return result;
                }
            }

            if (conversionCode.HasValue) {
                Mat result = new Mat();
                Cv2.CvtColor(source, result, conversionCode.Value);
                return result;
            }

            // No known conversion path - fail loudly (REVIEW.md F5) instead of silently returning an
            // unconverted clone that lies about its Format and corrupts anything reading it as destFormat.
            throw new NotSupportedException(
                $"FormatConvertedBitmap does not support converting {sourceFormat} to {destFormat}.");
        }

        private static bool IsThirtyTwoBit(Media.PixelFormat format) {
            return format == Media.PixelFormats.Bgra32 || format == Media.PixelFormats.Pbgra32 || format == Media.PixelFormats.Bgr32;
        }

        /// <summary>The Mat layout <see cref="BitmapSource.Format"/> reports as <paramref name="format"/>, or null.</summary>
        private static MatType? MatTypeOf(Media.PixelFormat format) {
            if (format == Media.PixelFormats.Gray8 || format == Media.PixelFormats.Indexed8) {
                return MatType.CV_8UC1;
            }

            if (format == Media.PixelFormats.Gray16) {
                return MatType.CV_16UC1;
            }

            if (format == Media.PixelFormats.Bgr24) {
                return MatType.CV_8UC3;
            }

            if (format == Media.PixelFormats.Rgb48) {
                return MatType.CV_16UC3;
            }

            return IsThirtyTwoBit(format) ? MatType.CV_8UC4 : null;
        }
    }
}
