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
    }
}
