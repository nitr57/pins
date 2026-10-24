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
using System.Drawing;

namespace Accord.Imaging.Filters {
    /// <summary>
    /// Grayscale filter - converts color image to grayscale using weighted RGB coefficients
    /// </summary>
    public class Grayscale {
        private double redCoefficient;
        private double greenCoefficient;
        private double blueCoefficient;

        public Grayscale(double cr, double cg, double cb) {
            redCoefficient = cr;
            greenCoefficient = cg;
            blueCoefficient = cb;
        }

        public Bitmap Apply(Bitmap source) {
            Mat sourceMat = source;

            // If already grayscale, just clone
            if (sourceMat.Channels() == 1) {
                Mat clone = new Mat();
                sourceMat.CopyTo(clone);
                return new Bitmap(clone);
            }

            // Convert to grayscale using custom weights
            // OpenCV's cvtColor uses different default weights, so we need to do it manually
            if (sourceMat.Channels() < 3) {
                Mat clone = new Mat();
                sourceMat.CopyTo(clone);
                return new Bitmap(clone);
            }
            Mat[] channels = Cv2.Split(sourceMat);

            // Create weighted sum: gray = cr*R + cg*G + cb*B
            // Note: OpenCV uses BGR order, so channels[0]=B, [1]=G, [2]=R
            Mat weighted = new Mat();
            Cv2.AddWeighted(channels[2], redCoefficient, channels[1], greenCoefficient, 0.0, weighted);
            // R and G are no longer needed - release them before the next full-size allocation
            // to keep the peak memory down on large 16-bit frames.
            channels[2].Dispose();
            channels[1].Dispose();

            Mat temp = new Mat();
            Cv2.AddWeighted(weighted, 1.0, channels[0], blueCoefficient, 0.0, temp);
            weighted.Dispose();

            // Cleanup (Dispose is idempotent, so the channels released above are safe to repeat)
            foreach (var ch in channels) ch.Dispose();

            // Preserve the source bit depth. AddWeighted keeps the input depth, so for 8U/16U
            // input temp already has the target type and converting would only copy it; any
            // other depth still goes through ConvertTo, which rounds and saturates.
            MatType targetType = temp.Depth() == MatType.CV_16U ? MatType.CV_16UC1 : MatType.CV_8UC1;
            if (temp.Type() == targetType) {
                return new Bitmap(temp);
            }

            Mat result = new Mat();
            temp.ConvertTo(result, targetType);
            temp.Dispose();

            return new Bitmap(result);
        }

        public void ApplyInPlace(Bitmap image) {
            var result = Apply(image);
            Mat resultMat = result;
            Mat imageMat = image;
            // Convert back to source channel count if needed
            if (resultMat.Channels() != imageMat.Channels()) {
                using var converted = new Mat();
                if (imageMat.Channels() == 3) {
                    Cv2.CvtColor(resultMat, converted, ColorConversionCodes.GRAY2BGR);
                } else if (imageMat.Channels() == 4) {
                    Cv2.CvtColor(resultMat, converted, ColorConversionCodes.GRAY2BGRA);
                } else {
                    resultMat.CopyTo(imageMat);
                    result.Dispose();
                    return;
                }
                converted.CopyTo(imageMat);
            } else {
                resultMat.CopyTo(imageMat);
            }
            result.Dispose();
        }
    }

    /// <summary>
    /// Crop filter - crops a rectangular region from an image using OpenCV
    /// </summary>
    public class Crop {
        private Rectangle cropRectangle;

        public Crop(Rectangle rect) {
            cropRectangle = rect;
        }

        public Bitmap Apply(Bitmap source) {
            Mat sourceMat = source;

            // Create OpenCV Rect from System.Drawing.Rectangle
            Rect cvRect = new Rect(cropRectangle.X, cropRectangle.Y, cropRectangle.Width, cropRectangle.Height);

            // Ensure the crop rectangle is within bounds
            cvRect = cvRect.Intersect(new Rect(0, 0, sourceMat.Width, sourceMat.Height));

            // Crop the Mat and clone to avoid dangling reference to parent Mat
            // Creating Mat(sourceMat, cvRect) creates a ROI view that references sourceMat.
            // In concurrent scenarios, if sourceMat is disposed while this ROI is in use,
            // it will point to invalid data. Clone it for thread safety.
            using (Mat roiMat = new Mat(sourceMat, cvRect)) {
                Mat clonedMat = roiMat.Clone();
                return new Bitmap(clonedMat);
            }
        }
    }

    /// <summary>
    /// Median filter - applies median blur using OpenCV
    /// </summary>
    public class Median {
        public int KernelSize { get; set; } = 3;

        public Median() { }

        public Median(int kernelSize) {
            KernelSize = kernelSize | 1; // Ensure odd number
        }

        public void ApplyInPlace(Bitmap image) {
            Mat mat = image;
            Cv2.MedianBlur(mat, mat, KernelSize);
        }

        public Bitmap Apply(Bitmap source) {
            Mat sourceMat = source;
            Mat result = new Mat();
            Cv2.MedianBlur(sourceMat, result, KernelSize);
            return new Bitmap(result);
        }
    }

    /// <summary>
    /// Convolution filter - applies custom convolution kernel using OpenCV
    /// </summary>
    public class Convolution : System.IDisposable {
        private Mat kernel;

        public Convolution(int[,] customKernel) {
            int rows = customKernel.GetLength(0);
            int cols = customKernel.GetLength(1);

            kernel = new Mat(rows, cols, MatType.CV_32F);

            for (int i = 0; i < rows; i++) {
                for (int j = 0; j < cols; j++) {
                    kernel.Set(i, j, (float)customKernel[i, j]);
                }
            }
        }

        public void Dispose() {
            kernel?.Dispose();
            kernel = null;
        }

        public void ApplyInPlace(Bitmap image) {
            Mat mat = image;
            Mat temp = new Mat();
            Cv2.Filter2D(mat, temp, mat.Depth(), kernel);
            temp.CopyTo(mat);
            temp.Dispose();
        }

        public Bitmap Apply(Bitmap source) {
            Mat sourceMat = source;
            Mat result = new Mat();
            Cv2.Filter2D(sourceMat, result, sourceMat.Depth(), kernel);
            return new Bitmap(result);
        }
    }

    /// <summary>
    /// SIS Threshold - Simple Image Statistics global threshold, matching Accord's algorithm:
    /// T = sum(w * I) / sum(w) with per-pixel weight w = max(|dI/dx|, |dI/dy|) (central
    /// differences). Pixels >= T become white, all others black. This is a single global
    /// threshold - an earlier version used Cv2.AdaptiveThreshold (a *local* threshold), which
    /// diverged badly from Accord on images with background gradients (star detection input).
    /// </summary>
    public class SISThreshold {
        public void ApplyInPlace(Bitmap image) {
            Mat mat = image;
            double threshold = CalculateThreshold(mat);
            // OpenCV Binary compares src > thresh (strict); Accord uses >= threshold on integer
            // pixels, so shift by half an intensity step to get identical binarization.
            Cv2.Threshold(mat, mat, threshold - 0.5, MaxValueFor(mat), ThresholdTypes.Binary);
        }

        public Bitmap Apply(Bitmap source) {
            Mat sourceMat = source;
            double threshold = CalculateThreshold(sourceMat);
            Mat result = new Mat();
            Cv2.Threshold(sourceMat, result, threshold - 0.5, MaxValueFor(sourceMat), ThresholdTypes.Binary);
            return new Bitmap(result);
        }

        private static double MaxValueFor(Mat mat) {
            return mat.Depth() == MatType.CV_16U ? 65535 : 255;
        }

        private static double CalculateThreshold(Mat gray) {
            if (gray.Channels() != 1) {
                throw new InvalidImagePropertiesException("SISThreshold requires a single-channel grayscale image.");
            }
            // Accord iterates the interior only (borders have no central difference); mirror
            // that by computing gradients over the full image but summing the interior ROI.
            if (gray.Width < 3 || gray.Height < 3) {
                // No interior pixels -> Accord's weight total is 0 and the threshold is 0.
                return 0;
            }

            using Mat f = new Mat();
            gray.ConvertTo(f, MatType.CV_32F);

            // Sobel with ksize 1 is the plain central-difference kernel [-1 0 1], matching
            // Accord's I(x+1,y) - I(x-1,y) exactly (the constant factor cancels in the ratio).
            using Mat gx = new Mat();
            using Mat gy = new Mat();
            Cv2.Sobel(f, gx, MatType.CV_32F, 1, 0, 1);
            Cv2.Sobel(f, gy, MatType.CV_32F, 0, 1, 1);

            using Mat absX = Cv2.Abs(gx);
            using Mat absY = Cv2.Abs(gy);
            using Mat weights = new Mat();
            Cv2.Max(absX, absY, weights);

            var interior = new Rect(1, 1, gray.Width - 2, gray.Height - 2);
            using Mat weightsRoi = new Mat(weights, interior);
            using Mat fRoi = new Mat(f, interior);
            using Mat weighted = weightsRoi.Mul(fRoi);

            double weightTotal = Cv2.Sum(weightsRoi).Val0;
            double weightedTotal = Cv2.Sum(weighted).Val0;

            // Accord truncates the ratio to an integer threshold.
            return weightTotal == 0 ? 0 : System.Math.Floor(weightedTotal / weightTotal);
        }
    }

    /// <summary>
    /// Binary Dilation 3x3 - morphological dilation with 3x3 kernel using OpenCV
    /// </summary>
    public class BinaryDilation3x3 {
        public void ApplyInPlace(Bitmap image) {
            Mat mat = image;
            Mat kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new OpenCvSharp.Size(3, 3));
            Cv2.Dilate(mat, mat, kernel);
            kernel.Dispose();
        }

        public Bitmap Apply(Bitmap source) {
            Mat sourceMat = source;
            Mat result = new Mat();
            Mat kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new OpenCvSharp.Size(3, 3));
            Cv2.Dilate(sourceMat, result, kernel);
            kernel.Dispose();
            return new Bitmap(result);
        }
    }

    /// <summary>
    /// Gaussian sharpen filter - sharpens using Gaussian unsharp mask via OpenCV
    /// </summary>
    public class GaussianSharpen {
        private double sigma;
        private int kernelSize;

        public GaussianSharpen(double sigma, int kernelSize) {
            this.sigma = sigma;
            this.kernelSize = kernelSize | 1; // ensure odd
            if (this.kernelSize < 3) this.kernelSize = 3;
        }

        public void ApplyInPlace(Bitmap image) {
            Mat mat = image;
            using Mat blurred = new Mat();
            Cv2.GaussianBlur(mat, blurred, new OpenCvSharp.Size(kernelSize, kernelSize), sigma);
            Cv2.AddWeighted(mat, 1.5, blurred, -0.5, 0, mat);
        }
    }

    /// <summary>
    /// Gaussian blur filter - wraps OpenCV GaussianBlur
    /// </summary>
    public class GaussianBlur {
        private double sigma;
        private int kernelSize;

        public GaussianBlur(double sigma, int kernelSize) {
            this.sigma = sigma;
            this.kernelSize = kernelSize | 1; // ensure odd
            if (this.kernelSize < 3) this.kernelSize = 3;
        }

        public void ApplyInPlace(Bitmap image) {
            Mat mat = image;
            Cv2.GaussianBlur(mat, mat, new OpenCvSharp.Size(kernelSize, kernelSize), sigma);
        }
    }

    /// <summary>
    /// Base class for color remapping filters
    /// </summary>
    public abstract class ColorRemapping {
        /// <summary>
        /// Format translations dictionary
        /// </summary>
        protected System.Collections.Generic.Dictionary<System.Drawing.Imaging.PixelFormat, System.Drawing.Imaging.PixelFormat> FormatTranslations { get; set; }

        protected ColorRemapping() {
            FormatTranslations = new System.Collections.Generic.Dictionary<System.Drawing.Imaging.PixelFormat, System.Drawing.Imaging.PixelFormat>();
        }

        /// <summary>
        /// Process the filter on the specified image
        /// </summary>
        protected abstract void ProcessFilter(UnmanagedImage image, Rectangle rect);

        /// <summary>
        /// Apply filter to an image
        /// </summary>
        public Bitmap Apply(Bitmap image) {
            // Create output bitmap
            var output = new Bitmap(image.Width, image.Height, image.PixelFormat);

            // Lock both bitmaps
            var sourceData = image.LockBits(
                new Rectangle(0, 0, image.Width, image.Height),
                System.Drawing.Imaging.ImageLockMode.ReadOnly,
                image.PixelFormat);

            var destData = output.LockBits(
                new Rectangle(0, 0, output.Width, output.Height),
                System.Drawing.Imaging.ImageLockMode.WriteOnly,
                output.PixelFormat);

            try {
                // Copy source to destination first
                unsafe {
                    byte* src = (byte*)sourceData.Scan0;
                    byte* dst = (byte*)destData.Scan0;
                    int bytes = sourceData.Stride * sourceData.Height;
                    for (int i = 0; i < bytes; i++) {
                        dst[i] = src[i];
                    }
                }

                // Create unmanaged image wrapper for destination
                var unmanagedImage = new UnmanagedImage(destData);

                // Process the filter
                ProcessFilter(unmanagedImage, new Rectangle(0, 0, image.Width, image.Height));
            } finally {
                image.UnlockBits(sourceData);
                output.UnlockBits(destData);
            }

            return output;
        }

        /// <summary>
        /// Apply filter to an image in place (modifies the original image)
        /// </summary>
        public void ApplyInPlace(Bitmap image) {
            var bitmapData = image.LockBits(
                new Rectangle(0, 0, image.Width, image.Height),
                System.Drawing.Imaging.ImageLockMode.ReadWrite,
                image.PixelFormat);

            try {
                // Create unmanaged image wrapper
                var unmanagedImage = new UnmanagedImage(bitmapData);

                // Process the filter
                ProcessFilter(unmanagedImage, new Rectangle(0, 0, image.Width, image.Height));
            } finally {
                image.UnlockBits(bitmapData);
            }
        }
    }

    /// <summary>
    /// Base class for Bayer filter demosaicing
    /// </summary>
    public abstract class BayerFilter {
        /// <summary>
        /// Format translations dictionary
        /// </summary>
        protected System.Collections.Generic.Dictionary<System.Drawing.Imaging.PixelFormat, System.Drawing.Imaging.PixelFormat> FormatTranslations { get; set; }

        /// <summary>
        /// Bayer pattern matrix (2x2) - gets corrected from the inverted pattern in NINA.Image
        /// </summary>
        private int[,] bayerPattern;
        public int[,] BayerPattern {
            get => bayerPattern;
            set {
                // The patterns in NINA.Image are inverted (likely a historical bug)
                // We need to flip them: swap [0,0] with [1,1] and [0,1] with [1,0]
                if (value != null && value.GetLength(0) == 2 && value.GetLength(1) == 2) {
                    bayerPattern = new int[,] {
                        { value[1, 1], value[1, 0] },
                        { value[0, 1], value[0, 0] }
                    };
                } else {
                    bayerPattern = value;
                }
            }
        }

        /// <summary>
        /// Whether to perform full demosaicing or simple extraction
        /// </summary>
        public bool PerformDemosaicing { get; set; } = true;

        protected BayerFilter() {
            FormatTranslations = new System.Collections.Generic.Dictionary<System.Drawing.Imaging.PixelFormat, System.Drawing.Imaging.PixelFormat>();
        }

        /// <summary>
        /// Process the filter on the specified images
        /// </summary>
        protected abstract unsafe void ProcessFilter(UnmanagedImage sourceData, UnmanagedImage destinationData);

        /// <summary>
        /// Apply filter to create a new processed bitmap
        /// </summary>
        public virtual Bitmap Apply(Bitmap source) {
            // Get the target pixel format
            var sourceFormat = source.PixelFormat;
            var destFormat = FormatTranslations.ContainsKey(sourceFormat)
                ? FormatTranslations[sourceFormat]
                : sourceFormat;

            // Create destination bitmap
            var destination = new Bitmap(source.Width, source.Height, destFormat);

            // Lock both bitmaps
            var sourceData = source.LockBits(
                new Rectangle(0, 0, source.Width, source.Height),
                System.Drawing.Imaging.ImageLockMode.ReadOnly,
                sourceFormat);

            var destData = destination.LockBits(
                new Rectangle(0, 0, destination.Width, destination.Height),
                System.Drawing.Imaging.ImageLockMode.WriteOnly,
                destFormat);

            try {
                var unmanagedSource = new UnmanagedImage(sourceData);
                var unmanagedDest = new UnmanagedImage(destData);

                ProcessFilter(unmanagedSource, unmanagedDest);
            } finally {
                source.UnlockBits(sourceData);
                destination.UnlockBits(destData);
            }

            return destination;
        }
    }
}
