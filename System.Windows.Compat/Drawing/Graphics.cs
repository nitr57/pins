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
using System.Collections.Generic;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace System.Drawing {
    /// <summary>
    /// Graphics class for drawing on images using OpenCV
    /// </summary>
    public class Graphics : IDisposable {
        private Mat _canvas;
        private bool _disposed = false;
        private Mat _transform; // 2x3 affine transform matrix
        private bool _hasTransform = false;

        private Graphics(Mat canvas) {
            _canvas = canvas;
            _transform = new Mat(2, 3, MatType.CV_64F);
            _transform.Set(0, 0, 1.0);
            _transform.Set(0, 1, 0.0);
            _transform.Set(0, 2, 0.0);
            _transform.Set(1, 0, 0.0);
            _transform.Set(1, 1, 1.0);
            _transform.Set(1, 2, 0.0);
        }

        /// <summary>
        /// Creates a Graphics object from a Bitmap
        /// </summary>
        public static Graphics FromImage(Bitmap bitmap) {
            Mat mat = bitmap;
            return new Graphics(mat);
        }

        /// <summary>
        /// Copies the screen to the graphics surface
        /// Note: On Linux, actual screen capture requires X11/Wayland integration
        /// This is a stub that creates a blank image for compatibility
        /// </summary>
        public void CopyFromScreen(int sourceX, int sourceY, int destX, int destY, Size blockRegionSize, CopyPixelOperation copyPixelOperation) {
            // On Linux, we would need to use X11 (XGetImage) or Wayland APIs for actual screen capture
            // For now, this is a stub that fills with black
            // TODO: Implement actual screen capture using native libraries

            Rectangle destRect = new Rectangle(destX, destY, blockRegionSize.Width, blockRegionSize.Height);

            // Fill the destination area with black (simulating a blank screenshot)
            Cv2.Rectangle(_canvas,
                new OpenCvSharp.Rect(destRect.X, destRect.Y, destRect.Width, destRect.Height),
                Scalar.Black, -1);

            // Log that screen capture is not fully implemented
            Console.WriteLine("Warning: Screen capture (CopyFromScreen) is not fully implemented on Linux. Returning blank image.");
        }

        /// <summary>
        /// Smoothing mode for rendering (for compatibility)
        /// </summary>
        public SmoothingMode SmoothingMode { get; set; }
        
        /// <summary>
        /// Interpolation mode for image scaling (for compatibility)
        /// </summary>
        public InterpolationMode InterpolationMode { get; set; }
        
        /// <summary>
        /// Pixel offset mode for rendering (for compatibility)
        /// </summary>
        public PixelOffsetMode PixelOffsetMode { get; set; }

        /// <summary>
        /// Text rendering hint for text quality (for compatibility)
        /// </summary>
        public Text.TextRenderingHint TextRenderingHint { get; set; }

        /// <summary>
        /// Draws a bitmap onto the canvas
        /// </summary>
        public void DrawImage(Bitmap image, int x, int y) {
            if (image == null) return;

            using (Mat srcMat = image.GetMat()) {
                if (srcMat == null || srcMat.Empty()) return;
                if (_canvas == null || _canvas.Empty()) return;

                // If canvas is grayscale and source is grayscale, or both are color, just copy
                if (_canvas.Channels() == srcMat.Channels()) {
                    srcMat.CopyTo(_canvas);
                } else if (_canvas.Channels() == 3 && srcMat.Channels() == 1) {
                    // Convert grayscale source to color canvas
                    Cv2.CvtColor(srcMat, _canvas, ColorConversionCodes.GRAY2BGR);
                } else if (_canvas.Channels() == 1 && srcMat.Channels() == 3) {
                    // Convert color source to grayscale canvas
                    Cv2.CvtColor(srcMat, _canvas, ColorConversionCodes.BGR2GRAY);
                }
            }
        }

        public void DrawImage(Image image, int x, int y, int width, int height) {
            if (image == null) return;
            
            // Convert Image to Bitmap if needed
            Bitmap bitmap = image as Bitmap;
            if (bitmap == null) return;

            using (Mat srcMat = bitmap.GetMat()) {
                if (srcMat == null || srcMat.Empty()) return;
                if (_canvas == null || _canvas.Empty()) return;

                // Create ROI on canvas
                var roi = new Rect(x, y, Math.Min(width, _canvas.Width - x), Math.Min(height, _canvas.Height - y));
                if (roi.Width <= 0 || roi.Height <= 0) return;

                // Resize source to fit the specified dimensions
                using (Mat resized = new Mat()) {
                    Cv2.Resize(srcMat, resized, new OpenCvSharp.Size(roi.Width, roi.Height));
                    
                    // Handle channel conversion if needed
                    if (_canvas.Channels() == resized.Channels()) {
                        resized.CopyTo(_canvas[roi]);
                    } else if (_canvas.Channels() == 3 && resized.Channels() == 1) {
                        using (Mat colorized = new Mat()) {
                            Cv2.CvtColor(resized, colorized, ColorConversionCodes.GRAY2BGR);
                            colorized.CopyTo(_canvas[roi]);
                        }
                    } else if (_canvas.Channels() == 1 && resized.Channels() == 3) {
                        using (Mat grayscale = new Mat()) {
                            Cv2.CvtColor(resized, grayscale, ColorConversionCodes.BGR2GRAY);
                            grayscale.CopyTo(_canvas[roi]);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Draws a rectangle
        /// </summary>
        public void DrawRectangle(Pen pen, Rectangle rect) {
            DrawRectangle(pen, rect.X, rect.Y, rect.Width, rect.Height);
        }

        /// <summary>
        /// Draws a rectangle
        /// </summary>
        public void DrawRectangle(Pen pen, float x, float y, float width, float height) {
            var rect = new Rect((int)x, (int)y, (int)width, (int)height);
            Cv2.Rectangle(_canvas, rect, pen.Color, (int)pen.Width, pen.LineType);
        }

        /// <summary>
        /// Draws a line
        /// </summary>
        public void DrawLine(Pen pen, float x1, float y1, float x2, float y2) {
            var pt1 = new Point2f(x1, y1);
            var Point2 = new Point2f(x2, y2);
            Cv2.Line(_canvas, (int)x1, (int)y1, (int)x2, (int)y2, pen.Color, (int)Math.Max(1, pen.Width), pen.LineType);
        }

        /// <summary>
        /// Draws a line between two points
        /// </summary>
        public void DrawLine(Pen pen, PointF pt1, PointF pt2) {
            DrawLine(pen, pt1.X, pt1.Y, pt2.X, pt2.Y);
        }

        /// <summary>
        /// Clears the graphics surface with the specified color
        /// </summary>
        public void Clear(Color color) {
            if (_canvas == null || _canvas.Empty()) return;

            Scalar cvColor = new Scalar(color.B, color.G, color.R, color.A);
            _canvas.SetTo(cvColor);
        }

        /// <summary>
        /// Draws a string (text)
        /// </summary>
        public void DrawString(string text, Font font, SolidBrush brush, System.Drawing.PointF point) {
            if (string.IsNullOrEmpty(text)) return;

            // Convert font size to OpenCV scale
            double fontScale = font.Size / 20.0; // Approximate scaling
            int thickness = Math.Max(1, (int)(font.Size / 12));

            // Map font style
            HersheyFonts fontFace = HersheyFonts.HersheySimplex;
            if (font.Style.HasFlag(FontStyle.Bold)) {
                fontFace = HersheyFonts.HersheyComplexSmall;
                thickness = Math.Max(2, thickness);
            }

            Cv2.PutText(_canvas, text, new OpenCvSharp.Point((int)point.X, (int)point.Y),
                fontFace, fontScale, brush.Color, thickness, LineTypes.AntiAlias);
        }

        /// <summary>
        /// Draws a string (text) with individual x and y coordinates
        /// </summary>
        public void DrawString(string text, Font font, SolidBrush brush, float x, float y) {
            DrawString(text, font, brush, new PointF(x, y));
        }

        /// <summary>
        /// Measures the size of a string when drawn with the specified font
        /// </summary>
        public SizeF MeasureString(string text, Font font) {
            if (string.IsNullOrEmpty(text)) {
                return new SizeF(0, 0);
            }

            // Convert font size to OpenCV scale
            double fontScale = font.Size / 20.0;
            int thickness = Math.Max(1, (int)(font.Size / 12));

            // Map font style
            HersheyFonts fontFace = HersheyFonts.HersheySimplex;
            if (font.Style.HasFlag(FontStyle.Bold)) {
                fontFace = HersheyFonts.HersheyComplexSmall;
                thickness = Math.Max(2, thickness);
            }

            // Get text size from OpenCV
            int baseline = 0;
            var size = Cv2.GetTextSize(text, fontFace, fontScale, thickness, out baseline);

            return new SizeF(size.Width, size.Height + baseline);
        }

        /// <summary>
        /// Draws an ellipse
        /// </summary>
        public void DrawEllipse(Pen pen, Rectangle rect) {
            var center = new OpenCvSharp.Point(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);
            var axes = new OpenCvSharp.Size(rect.Width / 2, rect.Height / 2);
            Cv2.Ellipse(_canvas, center, axes, 0, 0, 360, pen.Color, (int)pen.Width, pen.LineType);
        }

        /// <summary>
        /// Draws an ellipse with floating point coordinates
        /// </summary>
        public void DrawEllipse(Pen pen, RectangleF rect) {
            var center = new OpenCvSharp.Point((int)(rect.X + rect.Width / 2), (int)(rect.Y + rect.Height / 2));
            var axes = new OpenCvSharp.Size((int)(rect.Width / 2), (int)(rect.Height / 2));
            Cv2.Ellipse(_canvas, center, axes, 0, 0, 360, pen.Color, (int)pen.Width, pen.LineType);
        }

        /// <summary>
        /// Draws an ellipse with individual float parameters
        /// </summary>
        public void DrawEllipse(Pen pen, float x, float y, float width, float height) {
            DrawEllipse(pen, new RectangleF(x, y, width, height));
        }

        /// <summary>
        /// Fills an ellipse
        /// </summary>
        public void FillEllipse(SolidBrush brush, Rectangle rect) {
            var center = new OpenCvSharp.Point(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);
            var axes = new OpenCvSharp.Size(rect.Width / 2, rect.Height / 2);
            Cv2.Ellipse(_canvas, center, axes, 0, 0, 360, brush.Color, -1, LineTypes.AntiAlias);
        }

        /// <summary>
        /// Fills an ellipse with floating point coordinates
        /// </summary>
        public void FillEllipse(SolidBrush brush, RectangleF rect) {
            var center = new OpenCvSharp.Point((int)(rect.X + rect.Width / 2), (int)(rect.Y + rect.Height / 2));
            var axes = new OpenCvSharp.Size((int)(rect.Width / 2), (int)(rect.Height / 2));
            Cv2.Ellipse(_canvas, center, axes, 0, 0, 360, brush.Color, -1, LineTypes.AntiAlias);
        }

        /// <summary>
        /// Fills an ellipse with floating point coordinates using individual parameters
        /// </summary>
        public void FillEllipse(SolidBrush brush, float x, float y, float width, float height) {
            FillEllipse(brush, new RectangleF(x, y, width, height));
        }

        /// <summary>
        /// Draws a polygon
        /// </summary>
        public void DrawPolygon(Pen pen, PointF[] points) {
            if (points == null || points.Length < 2) return;

            // Convert PointF array to OpenCV Point array
            OpenCvSharp.Point[] cvPoints = new OpenCvSharp.Point[points.Length];
            for (int i = 0; i < points.Length; i++) {
                cvPoints[i] = new OpenCvSharp.Point((int)points[i].X, (int)points[i].Y);
            }

            // Draw polylines (closed polygon)
            Cv2.Polylines(_canvas, new OpenCvSharp.Point[][] { cvPoints }, true, pen.Color, (int)System.Math.Max(1, pen.Width), pen.LineType);
        }

        /// <summary>
        /// Draws a series of Bezier curves
        /// </summary>
        public void DrawBeziers(Pen pen, PointF[] points) {
            if (points == null || points.Length < 4 || (points.Length - 1) % 3 != 0) return;

            // Bezier curves require 4 points per curve (start, control1, control2, end)
            // For simplicity, approximate with polylines
            List<OpenCvSharp.Point> curvePoints = new List<OpenCvSharp.Point>();

            for (int i = 0; i < points.Length - 3; i += 3) {
                // Get the 4 control points for this Bezier segment
                PointF p0 = points[i];
                PointF p1 = points[i + 1];
                PointF p2 = points[i + 2];
                PointF p3 = points[i + 3];

                // Sample the Bezier curve with multiple points
                int segments = 20;
                for (int j = 0; j <= segments; j++) {
                    double t = j / (double)segments;
                    double u = 1 - t;
                    double tt = t * t;
                    double uu = u * u;
                    double uuu = uu * u;
                    double ttt = tt * t;

                    // Bezier curve formula: B(t) = (1-t)³P0 + 3(1-t)²tP1 + 3(1-t)t²P2 + t³P3
                    double x = uuu * p0.X + 3 * uu * t * p1.X + 3 * u * tt * p2.X + ttt * p3.X;
                    double y = uuu * p0.Y + 3 * uu * t * p1.Y + 3 * u * tt * p2.Y + ttt * p3.Y;

                    curvePoints.Add(new OpenCvSharp.Point((int)x, (int)y));
                }
            }

            if (curvePoints.Count > 1) {
                Cv2.Polylines(_canvas, new OpenCvSharp.Point[][] { curvePoints.ToArray() }, false, pen.Color, (int)System.Math.Max(1, pen.Width), pen.LineType);
            }
        }

        /// <summary>
        /// Translates the transformation matrix
        /// </summary>
        public void TranslateTransform(float dx, float dy) {
            var translation = new Mat(2, 3, MatType.CV_64F);
            translation.Set(0, 0, 1.0);
            translation.Set(0, 1, 0.0);
            translation.Set(0, 2, (double)dx);
            translation.Set(1, 0, 0.0);
            translation.Set(1, 1, 1.0);
            translation.Set(1, 2, (double)dy);

            if (!_hasTransform) {
                _transform = translation.Clone();
            } else {
                // Combine transformations (multiply matrices)
                _transform = MultiplyTransforms(_transform, translation);
            }
            _hasTransform = true;
        }

        /// <summary>
        /// Rotates the transformation matrix
        /// </summary>
        public void RotateTransform(float angle) {
            double radians = angle * Math.PI / 180.0;
            double cos = Math.Cos(radians);
            double sin = Math.Sin(radians);

            var rotation = new Mat(2, 3, MatType.CV_64F);
            rotation.Set(0, 0, cos);
            rotation.Set(0, 1, -sin);
            rotation.Set(0, 2, 0.0);
            rotation.Set(1, 0, sin);
            rotation.Set(1, 1, cos);
            rotation.Set(1, 2, 0.0);

            if (!_hasTransform) {
                _transform = rotation.Clone();
            } else {
                _transform = MultiplyTransforms(_transform, rotation);
            }
            _hasTransform = true;
        }

        /// <summary>
        /// Resets the transformation matrix to identity
        /// </summary>
        public void ResetTransform() {
            _transform = new Mat(2, 3, MatType.CV_64F);
            _transform.Set(0, 0, 1.0);
            _transform.Set(0, 1, 0.0);
            _transform.Set(0, 2, 0.0);
            _transform.Set(1, 0, 0.0);
            _transform.Set(1, 1, 1.0);
            _transform.Set(1, 2, 0.0);
            _hasTransform = false;
        }

        /// <summary>
        /// Draws an image with transformation
        /// </summary>
        public void DrawImage(Bitmap image, RectangleF destRect, RectangleF srcRect, GraphicsUnit unit) {
            if (image == null) return;

            using (Mat srcMat = image.GetMat()) {
                if (srcMat == null || srcMat.Empty()) return;
                if (_canvas == null || _canvas.Empty()) return;

                // Extract the source region
                Mat croppedSrc;
                if (srcRect.X == 0 && srcRect.Y == 0 && srcRect.Width == srcMat.Width && srcRect.Height == srcMat.Height) {
                    croppedSrc = srcMat;
                } else {
                    var srcRoi = new OpenCvSharp.Rect((int)srcRect.X, (int)srcRect.Y, (int)srcRect.Width, (int)srcRect.Height);
                    // Ensure ROI is within bounds
                    srcRoi.X = Math.Max(0, Math.Min(srcRoi.X, srcMat.Width - 1));
                    srcRoi.Y = Math.Max(0, Math.Min(srcRoi.Y, srcMat.Height - 1));
                    srcRoi.Width = Math.Min(srcRoi.Width, srcMat.Width - srcRoi.X);
                    srcRoi.Height = Math.Min(srcRoi.Height, srcMat.Height - srcRoi.Y);
                    croppedSrc = new Mat(srcMat, srcRoi);
                }

                // Resize to destination size if needed
                Mat resizedSrc = croppedSrc;
                if ((int)destRect.Width != croppedSrc.Width || (int)destRect.Height != croppedSrc.Height) {
                    resizedSrc = new Mat();
                    Cv2.Resize(croppedSrc, resizedSrc, new OpenCvSharp.Size((int)destRect.Width, (int)destRect.Height));
                }

                // Ensure source has alpha channel if canvas does
                Mat srcWithAlpha = resizedSrc;
                bool needsAlphaCleanup = false;
                if (_canvas.Channels() == 4 && resizedSrc.Channels() == 3) {
                    srcWithAlpha = new Mat();
                    Cv2.CvtColor(resizedSrc, srcWithAlpha, ColorConversionCodes.BGR2BGRA);
                    needsAlphaCleanup = true;
                }

                try {
                    // Apply transformation if any
                    if (_hasTransform) {
                        // The transform should be applied to the source image coordinates (destRect)
                        // Create points for the destination rectangle corners
                        Point2f[] srcPoints = new Point2f[] {
                            new Point2f(0, 0),
                            new Point2f(srcWithAlpha.Width, 0),
                            new Point2f(0, srcWithAlpha.Height)
                        };
                        
                        // Transform the dest rect corners using the current transform matrix
                        Point2f[] dstPoints = new Point2f[3];
                        for (int i = 0; i < 3; i++) {
                            double x = srcPoints[i].X + destRect.X;
                            double y = srcPoints[i].Y + destRect.Y;
                            dstPoints[i].X = (float)(_transform.At<double>(0, 0) * x + _transform.At<double>(0, 1) * y + _transform.At<double>(0, 2));
                            dstPoints[i].Y = (float)(_transform.At<double>(1, 0) * x + _transform.At<double>(1, 1) * y + _transform.At<double>(1, 2));
                        }
                        
                        // Get the affine transform from source to transformed destination
                        Mat affineTransform = Cv2.GetAffineTransform(srcPoints, dstPoints);
                        
                        // Apply transformation directly to canvas
                        using (var transformed = new Mat(_canvas.Size(), _canvas.Type(), Scalar.All(0))) {
                            Cv2.WarpAffine(srcWithAlpha, transformed, affineTransform, _canvas.Size(), InterpolationFlags.Cubic, BorderTypes.Transparent);

                            // Blend transformed image with canvas using proper alpha compositing
                            if (transformed.Channels() == 4 && _canvas.Channels() == 4) {
                                AlphaBlend(transformed, _canvas);
                            } else {
                                // For non-alpha channels, just copy non-zero pixels
                                for (int y = 0; y < _canvas.Height; y++) {
                                    for (int x = 0; x < _canvas.Width; x++) {
                                        if (_canvas.Channels() == 4) {
                                            var pixel = transformed.At<Vec4b>(y, x);
                                            if (pixel[3] > 0) { // Check alpha
                                                _canvas.Set(y, x, pixel);
                                            }
                                        } else if (_canvas.Channels() == 3) {
                                            var pixel = transformed.At<Vec3b>(y, x);
                                            if (pixel[0] > 0 || pixel[1] > 0 || pixel[2] > 0) {
                                                _canvas.Set(y, x, pixel);
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    } else {
                        // No transformation - simple copy to destination rectangle
                        var dstRoi = new OpenCvSharp.Rect((int)destRect.X, (int)destRect.Y, (int)destRect.Width, (int)destRect.Height);
                        // Ensure ROI is within bounds
                        dstRoi.X = Math.Max(0, Math.Min(dstRoi.X, _canvas.Width - 1));
                        dstRoi.Y = Math.Max(0, Math.Min(dstRoi.Y, _canvas.Height - 1));
                        dstRoi.Width = Math.Min(dstRoi.Width, _canvas.Width - dstRoi.X);
                        dstRoi.Height = Math.Min(dstRoi.Height, _canvas.Height - dstRoi.Y);

                        if (dstRoi.Width > 0 && dstRoi.Height > 0) {
                            using (var dstRegion = new Mat(_canvas, dstRoi)) {
                                if (srcWithAlpha.Size() == dstRegion.Size()) {
                                    if (srcWithAlpha.Channels() == 4 && dstRegion.Channels() == 4) {
                                        AlphaBlend(srcWithAlpha, dstRegion);
                                    } else {
                                        srcWithAlpha.CopyTo(dstRegion);
                                    }
                                }
                            }
                        }
                    }
                } finally {
                    if (needsAlphaCleanup) {
                        srcWithAlpha?.Dispose();
                    }
                    if (resizedSrc != croppedSrc) {
                        resizedSrc?.Dispose();
                    }
                    if (resizedSrc != croppedSrc) {
                        resizedSrc.Dispose();
                    }
                    if (croppedSrc != srcMat) {
                        croppedSrc.Dispose();
                    }
                }
            }
        }

        private Mat MultiplyTransforms(Mat a, Mat b) {
            // Matrix multiplication for 2x3 affine transform matrices
            // Extended to 3x3 with [0, 0, 1] row for multiplication
            var result = new Mat(2, 3, MatType.CV_64F, Scalar.All(0));

            double a00 = a.At<double>(0, 0), a01 = a.At<double>(0, 1), a02 = a.At<double>(0, 2);
            double a10 = a.At<double>(1, 0), a11 = a.At<double>(1, 1), a12 = a.At<double>(1, 2);
            double b00 = b.At<double>(0, 0), b01 = b.At<double>(0, 1), b02 = b.At<double>(0, 2);
            double b10 = b.At<double>(1, 0), b11 = b.At<double>(1, 1), b12 = b.At<double>(1, 2);

            result.Set(0, 0, a00 * b00 + a01 * b10);
            result.Set(0, 1, a00 * b01 + a01 * b11);
            result.Set(0, 2, a00 * b02 + a01 * b12 + a02);
            result.Set(1, 0, a10 * b00 + a11 * b10);
            result.Set(1, 1, a10 * b01 + a11 * b11);
            result.Set(1, 2, a10 * b02 + a11 * b12 + a12);

            return result;
        }

        private void AlphaBlend(Mat src, Mat dst) {
            // Alpha blending for BGRA images using pointer-based approach
            if (src.Channels() != 4 || dst.Channels() != 4) return;
            if (src.Width != dst.Width || src.Height != dst.Height) return;
            
            unsafe {
                byte* srcPtr = (byte*)src.DataPointer;
                byte* dstPtr = (byte*)dst.DataPointer;
                int totalPixels = src.Width * src.Height;
                
                for (int i = 0; i < totalPixels; i++) {
                    int idx = i * 4;
                    byte alpha = srcPtr[idx + 3];
                    
                    if (alpha > 0) {
                        float a = alpha / 255.0f;
                        float invA = 1.0f - a;
                        
                        dstPtr[idx + 0] = (byte)(srcPtr[idx + 0] * a + dstPtr[idx + 0] * invA);
                        dstPtr[idx + 1] = (byte)(srcPtr[idx + 1] * a + dstPtr[idx + 1] * invA);
                        dstPtr[idx + 2] = (byte)(srcPtr[idx + 2] * a + dstPtr[idx + 2] * invA);
                        dstPtr[idx + 3] = (byte)Math.Min(255, alpha + dstPtr[idx + 3] * invA);
                    }
                }
            }
        }

        public void Dispose() {
            if (!_disposed) {
                // Don't dispose the canvas Mat as it's owned by the Bitmap
                _disposed = true;
            }
        }
    }
}

namespace System.Drawing.Drawing2D {
    /// <summary>
    /// SmoothingMode enumeration for drawing quality
    /// </summary>
    public enum SmoothingMode {
        Default = 0,
        HighSpeed = 1,
        HighQuality = 2,
        None = 3,
        AntiAlias = 4
    }
}

namespace System.Drawing.Text {
    /// <summary>
    /// TextRenderingHint enumeration for text rendering quality
    /// </summary>
    public enum TextRenderingHint {
        SystemDefault = 0,
        SingleBitPerPixelGridFit = 1,
        SingleBitPerPixel = 2,
        AntiAliasGridFit = 3,
        AntiAlias = 4,
        ClearTypeGridFit = 5
    }
}
