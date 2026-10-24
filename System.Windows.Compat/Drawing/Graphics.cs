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
            throw new PlatformNotSupportedException("Screen capture (CopyFromScreen) is not supported on Linux. Use X11/Wayland APIs directly.");
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
        /// How drawn pixels combine with the canvas. SourceOver alpha-blends (the GDI+ default
        /// and the behaviour of this class before the property existed); SourceCopy overwrites.
        /// </summary>
        public CompositingMode CompositingMode { get; set; } = CompositingMode.SourceOver;

        /// <summary>
        /// Compositing quality hint (for compatibility)
        /// </summary>
        public CompositingQuality CompositingQuality { get; set; } = CompositingQuality.Default;

        /// <summary>
        /// Text rendering hint for text quality (for compatibility)
        /// </summary>
        public Text.TextRenderingHint TextRenderingHint { get; set; }

        /// <summary>
        /// Draws a bitmap onto the canvas
        /// </summary>
        public void DrawImage(Bitmap image, int x, int y) {
            if (image == null) return;

            if (_hasTransform) {
                // Route through the transform-aware overload so the image lands where the
                // current TranslateTransform/RotateTransform composition puts it.
                DrawImage(image,
                    new RectangleF(x, y, image.Width, image.Height),
                    new RectangleF(0, 0, image.Width, image.Height),
                    GraphicsUnit.Pixel);
                return;
            }

            using (var srcLease = LeaseSource(image)) {
                Mat srcMat = srcLease.Mat;
                if (srcMat == null || srcMat.Empty()) return;
                if (_canvas == null || _canvas.Empty()) return;

                // Clamp to canvas bounds
                int width = Math.Min(srcMat.Width, _canvas.Width - x);
                int height = Math.Min(srcMat.Height, _canvas.Height - y);
                if (width <= 0 || height <= 0 || x < 0 || y < 0) return;

                // Crop source if it extends beyond canvas
                Mat src = srcMat;
                bool srcOwned = false;
                if (srcMat.Width != width || srcMat.Height != height) {
                    src = new Mat(srcMat, new OpenCvSharp.Rect(0, 0, width, height));
                    srcOwned = true;
                }

                try {
                    var dstRoi = new OpenCvSharp.Rect(x, y, width, height);
                    using (var dstRegion = new Mat(_canvas, dstRoi)) {
                        if (dstRegion.Channels() == src.Channels()) {
                            src.CopyTo(dstRegion);
                        } else if (dstRegion.Channels() == 3 && src.Channels() == 1) {
                            using var converted = new Mat();
                            Cv2.CvtColor(src, converted, ColorConversionCodes.GRAY2BGR);
                            converted.CopyTo(dstRegion);
                        } else if (dstRegion.Channels() == 1 && src.Channels() == 3) {
                            using var converted = new Mat();
                            Cv2.CvtColor(src, converted, ColorConversionCodes.BGR2GRAY);
                            converted.CopyTo(dstRegion);
                        }
                    }
                } finally {
                    if (srcOwned) src.Dispose();
                }
            }
        }

        public void DrawImage(Image image, int x, int y, int width, int height) {
            if (image == null) return;

            // Convert Image to Bitmap if needed
            Bitmap bitmap = image as Bitmap;
            if (bitmap == null) return;

            if (_hasTransform) {
                // Route through the transform-aware overload so the image lands where the
                // current TranslateTransform/RotateTransform composition puts it.
                DrawImage(bitmap,
                    new RectangleF(x, y, width, height),
                    new RectangleF(0, 0, bitmap.Width, bitmap.Height),
                    GraphicsUnit.Pixel);
                return;
            }

            using (var srcLease = LeaseSource(bitmap)) {
                Mat srcMat = srcLease.Mat;
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
        /// Reads a source image without copying it. When its pixels share memory with this
        /// canvas (a bitmap drawn into itself, or two bitmaps over the same buffer), copying into
        /// the overlapping region would smear the image, so that case still takes a copy.
        /// </summary>
        private System.Windows.Media.Imaging.MatLease LeaseSource(Bitmap image) {
            Mat source = image;
            // Empty checks first, in the order the callers make them, so a disposed or empty
            // image fails (or returns early) exactly as it did before the overlap check existed.
            if (source != null && !source.Empty() && _canvas != null && !_canvas.Empty() && SharesMemory(source, _canvas)) {
                return new System.Windows.Media.Imaging.MatLease(image.GetMat(), owned: true);
            }
            return new System.Windows.Media.Imaging.MatLease(source, owned: false);
        }

        /// <summary>
        /// Whether two Mats' underlying buffers overlap. Compares the whole allocations
        /// (DataStart..DataLimit), not just the visible regions, so ROI views are caught too.
        /// Both Mats must be non-empty.
        /// </summary>
        private static bool SharesMemory(Mat a, Mat b) {
            return a.DataStart.ToInt64() < b.DataLimit.ToInt64() && b.DataStart.ToInt64() < a.DataLimit.ToInt64();
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
            if (!_hasTransform) {
                var rect = new Rect((int)x, (int)y, (int)width, (int)height);
                // Sub-pixel pen widths truncate to 0, which OpenCV rejects (and -1 would mean
                // "filled") - clamp to the 1px hairline GDI+ draws, like DrawLine does.
                Cv2.Rectangle(_canvas, rect, pen.Color, (int)Math.Max(1, pen.Width), pen.LineType);
                return;
            }

            // A rotated rectangle can't be represented as an axis-aligned Cv2.Rectangle - draw
            // the four transformed corners as a closed polyline instead.
            var corners = new[] {
                TransformPoint(x, y),
                TransformPoint(x + width, y),
                TransformPoint(x + width, y + height),
                TransformPoint(x, y + height)
            };
            DrawPolylineRaw(pen, corners, true);
        }

        /// <summary>
        /// Draws a line
        /// </summary>
        public void DrawLine(Pen pen, float x1, float y1, float x2, float y2) {
            var p1 = TransformPoint(x1, y1);
            var p2 = TransformPoint(x2, y2);
            Cv2.Line(_canvas, (int)p1.X, (int)p1.Y, (int)p2.X, (int)p2.Y, pen.Color, (int)Math.Max(1, pen.Width), pen.LineType);
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
        public void DrawString(string text, Font font, Brush brush, System.Drawing.PointF point) {
            if (string.IsNullOrEmpty(text)) return;
            if (_canvas == null || _canvas.Empty()) return;

            // Convert font size to OpenCV scale
            double fontScale = font.Size / 20.0; // Approximate scaling
            int thickness = Math.Max(1, (int)(font.Size / 12));

            // Map font style
            HersheyFonts fontFace = HersheyFonts.HersheySimplex;
            if (font.Style.HasFlag(FontStyle.Bold)) {
                fontFace = HersheyFonts.HersheyComplexSmall;
                thickness = Math.Max(2, thickness);
            }

            var transformedOrigin = TransformPoint(point.X, point.Y);
            float angle = TransformAngleDegrees();

            if (!_hasTransform || angle == 0f) {
                Cv2.PutText(_canvas, text, new OpenCvSharp.Point((int)transformedOrigin.X, (int)transformedOrigin.Y),
                    fontFace, fontScale, brush.ToScalar(), thickness, LineTypes.AntiAlias);
                return;
            }

            // Rotated text: Cv2.PutText has no rotation parameter, so render into a padded local
            // buffer at the untransformed origin, rotate that buffer about its own center, and
            // composite the buffer-sized result through a mask onto a canvas ROI. Keeping all
            // warp targets glyph-buffer-sized (instead of canvas-sized) avoids moving multiple
            // full frames of memory per label on large sensors.
            var textSize = Cv2.GetTextSize(text, fontFace, fontScale, thickness, out int baseline);
            // The farthest glyph pixel from the origin is at distance hypot(width, height +
            // baseline); pad by that so no rotation angle can clip it at the buffer edge.
            int pad = (int)Math.Ceiling(Math.Sqrt(
                (double)textSize.Width * textSize.Width +
                (double)(textSize.Height + baseline) * (textSize.Height + baseline))) + thickness * 4;
            int bufSize = pad * 2;
            var localOrigin = new OpenCvSharp.Point(pad, pad);
            var bufCvSize = new OpenCvSharp.Size(bufSize, bufSize);

            using var glyphs = new Mat(bufSize, bufSize, _canvas.Type(), Scalar.All(0));
            Cv2.PutText(glyphs, text, localOrigin, fontFace, fontScale, brush.ToScalar(), thickness, LineTypes.AntiAlias);

            using var glyphMask = new Mat(bufSize, bufSize, MatType.CV_8UC1, Scalar.All(0));
            Cv2.PutText(glyphMask, text, localOrigin, fontFace, fontScale, Scalar.All(255), thickness, LineTypes.AntiAlias);

            // GetRotationMatrix2D is counter-clockwise-positive; negate the clockwise-positive
            // transform angle. Rotate about the buffer center so the result stays in-buffer.
            using var rotation = Cv2.GetRotationMatrix2D(new Point2f(pad, pad), -angle, 1.0);

            using var warpedColor = new Mat(bufSize, bufSize, _canvas.Type(), Scalar.All(0));
            using var warpedMask = new Mat(bufSize, bufSize, MatType.CV_8UC1, Scalar.All(0));
            Cv2.WarpAffine(glyphs, warpedColor, rotation, bufCvSize, InterpolationFlags.Linear, BorderTypes.Constant, Scalar.All(0));
            Cv2.WarpAffine(glyphMask, warpedMask, rotation, bufCvSize, InterpolationFlags.Linear, BorderTypes.Constant, Scalar.All(0));

            // Composite so the buffer center (the text origin) lands on the transformed
            // destination position, clipping the ROI to the canvas bounds.
            int dstX = (int)transformedOrigin.X - pad;
            int dstY = (int)transformedOrigin.Y - pad;
            int srcX = Math.Max(0, -dstX);
            int srcY = Math.Max(0, -dstY);
            int roiX = Math.Max(0, dstX);
            int roiY = Math.Max(0, dstY);
            int roiWidth = Math.Min(bufSize - srcX, _canvas.Width - roiX);
            int roiHeight = Math.Min(bufSize - srcY, _canvas.Height - roiY);
            if (roiWidth <= 0 || roiHeight <= 0) return;

            using var colorRoi = new Mat(warpedColor, new Rect(srcX, srcY, roiWidth, roiHeight));
            using var maskRoi = new Mat(warpedMask, new Rect(srcX, srcY, roiWidth, roiHeight));
            using var canvasRoi = new Mat(_canvas, new Rect(roiX, roiY, roiWidth, roiHeight));
            colorRoi.CopyTo(canvasRoi, maskRoi);
        }

        /// <summary>
        /// Draws a string (text) with individual x and y coordinates
        /// </summary>
        public void DrawString(string text, Font font, Brush brush, float x, float y) {
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
            DrawEllipse(pen, new RectangleF(rect.X, rect.Y, rect.Width, rect.Height));
        }

        /// <summary>
        /// Draws an ellipse with floating point coordinates
        /// </summary>
        public void DrawEllipse(Pen pen, RectangleF rect) {
            var centerLocal = TransformPoint(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);
            var center = new OpenCvSharp.Point((int)centerLocal.X, (int)centerLocal.Y);
            var axes = new OpenCvSharp.Size((int)(rect.Width / 2), (int)(rect.Height / 2));
            // Same clamp as DrawRectangle: width < 1 must draw a hairline, not assert (or fill).
            Cv2.Ellipse(_canvas, center, axes, TransformAngleDegrees(), 0, 360, pen.Color, (int)Math.Max(1, pen.Width), pen.LineType);
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
        public void FillEllipse(Brush brush, Rectangle rect) {
            FillEllipse(brush, new RectangleF(rect.X, rect.Y, rect.Width, rect.Height));
        }

        /// <summary>
        /// Fills an ellipse with floating point coordinates
        /// </summary>
        public void FillEllipse(Brush brush, RectangleF rect) {
            var centerLocal = TransformPoint(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);
            var center = new OpenCvSharp.Point((int)centerLocal.X, (int)centerLocal.Y);
            var axes = new OpenCvSharp.Size((int)(rect.Width / 2), (int)(rect.Height / 2));
            Cv2.Ellipse(_canvas, center, axes, TransformAngleDegrees(), 0, 360, brush.ToScalar(), -1, LineTypes.AntiAlias);
        }

        /// <summary>
        /// Fills an ellipse with floating point coordinates using individual parameters
        /// </summary>
        public void FillEllipse(Brush brush, float x, float y, float width, float height) {
            FillEllipse(brush, new RectangleF(x, y, width, height));
        }

        /// <summary>
        /// Draws a polygon
        /// </summary>
        public void DrawPolygon(Pen pen, PointF[] points) {
            if (points == null || points.Length < 2) return;

            var transformed = new PointF[points.Length];
            for (int i = 0; i < points.Length; i++) {
                transformed[i] = TransformPoint(points[i].X, points[i].Y);
            }
            DrawPolylineRaw(pen, transformed, true);
        }

        /// <summary>
        /// Draws a series of connected line segments (an open polyline)
        /// </summary>
        public void DrawLines(Pen pen, PointF[] points) {
            if (points == null || points.Length < 2) return;

            var transformed = new PointF[points.Length];
            for (int i = 0; i < points.Length; i++) {
                transformed[i] = TransformPoint(points[i].X, points[i].Y);
            }
            DrawPolylineRaw(pen, transformed, false);
        }

        /// <summary>
        /// Fills a polygon with the specified brush. A HatchBrush is rendered as a real pattern
        /// clipped to the polygon; every other brush fills flat.
        /// </summary>
        public void FillPolygon(Brush brush, PointF[] points) {
            if (points == null || points.Length < 3) return;

            var cvPoints = new OpenCvSharp.Point[points.Length];
            for (int i = 0; i < points.Length; i++) {
                PointF transformed = TransformPoint(points[i].X, points[i].Y);
                cvPoints[i] = new OpenCvSharp.Point((int)transformed.X, (int)transformed.Y);
            }

            if (brush is Drawing2D.HatchBrush hatchBrush) {
                FillPolygonHatched(hatchBrush, cvPoints);
                return;
            }
            Cv2.FillPoly(_canvas, new OpenCvSharp.Point[][] { cvPoints }, brush.ToScalar(), LineTypes.AntiAlias);
        }

        /// <summary>
        /// Renders a hatch pattern into a scratch patch the size of the polygon's bounding box
        /// and copies it through a polygon-shaped mask. Going via the bounding box rather than
        /// the whole canvas keeps the cost proportional to the polygon; the patch's canvas
        /// position is handed to the brush so the pattern stays phase-aligned to the canvas.
        /// </summary>
        private void FillPolygonHatched(Drawing2D.HatchBrush brush, OpenCvSharp.Point[] points) {
            OpenCvSharp.Rect roi = Cv2.BoundingRect(points)
                .Intersect(new OpenCvSharp.Rect(0, 0, _canvas.Width, _canvas.Height));
            if (roi.Width <= 0 || roi.Height <= 0) {
                return;
            }

            var shifted = new OpenCvSharp.Point[points.Length];
            for (int i = 0; i < points.Length; i++) {
                shifted[i] = new OpenCvSharp.Point(points[i].X - roi.X, points[i].Y - roi.Y);
            }

            using var patch = new Mat(roi.Height, roi.Width, _canvas.Type());
            using var mask = new Mat(roi.Height, roi.Width, MatType.CV_8UC1, Scalar.All(0));
            using var destination = new Mat(_canvas, roi);
            brush.DrawPattern(patch, roi.X, roi.Y);
            // Link8, not AntiAlias: CopyTo treats any non-zero mask byte as fully opaque, so an
            // anti-aliased mask would widen the edge to a hard jagged fringe rather than soften it.
            Cv2.FillPoly(mask, new OpenCvSharp.Point[][] { shifted }, Scalar.All(255), LineTypes.Link8);
            patch.CopyTo(destination, mask);
        }

        /// <summary>
        /// Draws a closed or open polyline through already-transformed points, with no further
        /// transform applied - used internally by DrawPolygon/DrawRectangle once their input
        /// points have already been run through TransformPoint.
        /// </summary>
        private void DrawPolylineRaw(Pen pen, PointF[] points, bool closed) {
            OpenCvSharp.Point[] cvPoints = new OpenCvSharp.Point[points.Length];
            for (int i = 0; i < points.Length; i++) {
                cvPoints[i] = new OpenCvSharp.Point((int)points[i].X, (int)points[i].Y);
            }
            Cv2.Polylines(_canvas, new OpenCvSharp.Point[][] { cvPoints }, closed, pen.Color, (int)System.Math.Max(1, pen.Width), pen.LineType);
        }

        /// <summary>
        /// Applies the current transform (TranslateTransform/RotateTransform composition) to a
        /// single point. A no-op when no transform is active.
        /// </summary>
        private PointF TransformPoint(float x, float y) {
            if (!_hasTransform) {
                return new PointF(x, y);
            }
            double tx = _transform.At<double>(0, 0) * x + _transform.At<double>(0, 1) * y + _transform.At<double>(0, 2);
            double ty = _transform.At<double>(1, 0) * x + _transform.At<double>(1, 1) * y + _transform.At<double>(1, 2);
            return new PointF((float)tx, (float)ty);
        }

        /// <summary>
        /// Returns the current transform's rotation angle in this class's RotateTransform
        /// convention (positive = clockwise on screen, matching GDI+ in y-down image
        /// coordinates). Cv2.Ellipse's angle parameter shares that clockwise-positive
        /// convention, so the value can be passed to it directly; Cv2.GetRotationMatrix2D is
        /// counter-clockwise-positive and needs the negation. Since TranslateTransform and
        /// RotateTransform are the only two mutators of _transform, its linear part is always a
        /// pure rotation (or identity), so extracting a single angle is exact - never a skew.
        /// </summary>
        private float TransformAngleDegrees() {
            if (!_hasTransform) {
                return 0f;
            }
            double m00 = _transform.At<double>(0, 0);
            double m10 = _transform.At<double>(1, 0);
            return (float)(Math.Atan2(m10, m00) * 180.0 / Math.PI);
        }

        /// <summary>
        /// Draws a series of Bezier curves
        /// </summary>
        public void DrawBeziers(Pen pen, PointF[] points) {
            if (points == null || points.Length < 4 || (points.Length - 1) % 3 != 0) return;

            // An affine transform of a Bezier curve equals the Bezier of the transformed
            // control points, so applying the current transform up front is exact.
            if (_hasTransform) {
                var transformedPoints = new PointF[points.Length];
                for (int i = 0; i < points.Length; i++) {
                    transformedPoints[i] = TransformPoint(points[i].X, points[i].Y);
                }
                points = transformedPoints;
            }

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

            try {
                if (!_hasTransform) {
                    _transform?.Dispose();
                    _transform = translation.Clone();
                } else {
                    // Combine transformations (multiply matrices)
                    var newTransform = MultiplyTransforms(_transform, translation);
                    _transform?.Dispose();
                    _transform = newTransform;
                }
                _hasTransform = true;
            } finally {
                translation.Dispose();
            }
        }

        /// <summary>
        /// Scales the transformation matrix
        /// </summary>
        public void ScaleTransform(float sx, float sy) {
            var scale = new Mat(2, 3, MatType.CV_64F);
            scale.Set(0, 0, (double)sx);
            scale.Set(0, 1, 0.0);
            scale.Set(0, 2, 0.0);
            scale.Set(1, 0, 0.0);
            scale.Set(1, 1, (double)sy);
            scale.Set(1, 2, 0.0);

            try {
                if (!_hasTransform) {
                    _transform?.Dispose();
                    _transform = scale.Clone();
                } else {
                    var newTransform = MultiplyTransforms(_transform, scale);
                    _transform?.Dispose();
                    _transform = newTransform;
                }
                _hasTransform = true;
            } finally {
                scale.Dispose();
            }
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

            try {
                if (!_hasTransform) {
                    _transform?.Dispose();
                    _transform = rotation.Clone();
                } else {
                    var newTransform = MultiplyTransforms(_transform, rotation);
                    _transform?.Dispose();
                    _transform = newTransform;
                }
                _hasTransform = true;
            } finally {
                rotation.Dispose();
            }
        }

        /// <summary>
        /// Resets the transformation matrix to identity
        /// </summary>
        public void ResetTransform() {
            _transform?.Dispose();
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
        /// Draws the source rectangle of an image into the parallelogram described by three
        /// destination points (upper-left, upper-right, lower-left), as GDI+ does. The current
        /// transform is applied to those points, so callers can combine this with
        /// TranslateTransform/RotateTransform.
        /// </summary>
        public void DrawImage(Bitmap image, PointF[] destPoints, RectangleF srcRect, GraphicsUnit unit, ImageAttributes imageAttrs) {
            if (image == null) return;
            if (destPoints == null || destPoints.Length < 3) {
                throw new ArgumentException("Three destination points are required.", nameof(destPoints));
            }

            using (var srcLease = LeaseSource(image)) {
                Mat srcMat = srcLease.Mat;
                if (srcMat == null || srcMat.Empty()) return;
                if (_canvas == null || _canvas.Empty()) return;

                // Clamp the source rectangle to the image, then crop to it.
                int srcX = Math.Max(0, Math.Min((int)srcRect.X, srcMat.Width - 1));
                int srcY = Math.Max(0, Math.Min((int)srcRect.Y, srcMat.Height - 1));
                int srcWidth = Math.Min((int)srcRect.Width, srcMat.Width - srcX);
                int srcHeight = Math.Min((int)srcRect.Height, srcMat.Height - srcY);
                if (srcWidth <= 0 || srcHeight <= 0) return;

                Mat croppedSrc;
                bool croppedSrcCreated = false;
                if (srcX == 0 && srcY == 0 && srcWidth == srcMat.Width && srcHeight == srcMat.Height) {
                    croppedSrc = srcMat;
                } else {
                    using (Mat roiMat = new Mat(srcMat, new OpenCvSharp.Rect(srcX, srcY, srcWidth, srcHeight))) {
                        croppedSrc = roiMat.Clone();
                    }
                    croppedSrcCreated = true;
                }

                // Ensure source has alpha channel if canvas does
                Mat srcWithAlpha = croppedSrc;
                bool needsAlphaCleanup = false;
                if (_canvas.Channels() == 4 && croppedSrc.Channels() == 3) {
                    srcWithAlpha = new Mat();
                    Cv2.CvtColor(croppedSrc, srcWithAlpha, ColorConversionCodes.BGR2BGRA);
                    needsAlphaCleanup = true;
                }

                try {
                    // The three source corners matching destPoints' upper-left/upper-right/lower-left order
                    Point2f[] srcPoints = new Point2f[] {
                        new Point2f(0, 0),
                        new Point2f(srcWithAlpha.Width, 0),
                        new Point2f(0, srcWithAlpha.Height)
                    };

                    // Push the destination parallelogram through the current transform
                    Point2f[] dstPoints = new Point2f[3];
                    for (int i = 0; i < 3; i++) {
                        double x = destPoints[i].X;
                        double y = destPoints[i].Y;
                        if (_hasTransform) {
                            dstPoints[i].X = (float)(_transform.At<double>(0, 0) * x + _transform.At<double>(0, 1) * y + _transform.At<double>(0, 2));
                            dstPoints[i].Y = (float)(_transform.At<double>(1, 0) * x + _transform.At<double>(1, 1) * y + _transform.At<double>(1, 2));
                        } else {
                            dstPoints[i].X = (float)x;
                            dstPoints[i].Y = (float)y;
                        }
                    }

                    BorderTypes borderType = ToBorderType(imageAttrs);

                    using (Mat affineTransform = Cv2.GetAffineTransform(srcPoints, dstPoints)) {
                        using (var transformed = new Mat(_canvas.Size(), _canvas.Type(), Scalar.All(0))) {
                            Cv2.WarpAffine(srcWithAlpha, transformed, affineTransform, _canvas.Size(),
                                ToInterpolationFlags(this.InterpolationMode), borderType);

                            if (transformed.Channels() == 4 && _canvas.Channels() == 4) {
                                AlphaBlend(transformed, _canvas);
                            } else {
                                // Mask the warped area so genuinely black source pixels are not dropped
                                using var mask = new Mat(srcWithAlpha.Size(), MatType.CV_8UC1, Scalar.All(255));
                                using var transformedMask = new Mat(_canvas.Size(), MatType.CV_8UC1, Scalar.All(0));
                                Cv2.WarpAffine(mask, transformedMask, affineTransform, _canvas.Size(),
                                    InterpolationFlags.Nearest, BorderTypes.Constant, new Scalar(0));
                                transformed.CopyTo(_canvas, transformedMask);
                            }
                        }
                    }
                } finally {
                    if (needsAlphaCleanup) {
                        srcWithAlpha?.Dispose();
                    }
                    if (croppedSrcCreated) {
                        croppedSrc?.Dispose();
                    }
                }
            }
        }

        /// <summary>
        /// Maps a GDI+ wrap mode onto the closest OpenCV border mode. TileFlipXY mirrors the
        /// source at its edges, which Reflect reproduces; everything else falls back to the
        /// transparent border used elsewhere in this class.
        /// </summary>
        private static BorderTypes ToBorderType(ImageAttributes imageAttrs) {
            if (imageAttrs == null) {
                return BorderTypes.Transparent;
            }

            switch (imageAttrs.WrapMode) {
                case WrapMode.TileFlipX:
                case WrapMode.TileFlipY:
                case WrapMode.TileFlipXY:
                    return BorderTypes.Reflect;

                case WrapMode.Clamp:
                    return BorderTypes.Replicate;

                default:
                    // Tile maps to BORDER_WRAP, which warpAffine rejects, so it falls through here.
                    return BorderTypes.Transparent;
            }
        }

        /// <summary>
        /// Maps the configured InterpolationMode onto the matching OpenCV interpolation flag.
        /// </summary>
        private static InterpolationFlags ToInterpolationFlags(InterpolationMode mode) {
            switch (mode) {
                case InterpolationMode.NearestNeighbor:
                    return InterpolationFlags.Nearest;

                case InterpolationMode.Low:
                case InterpolationMode.Bilinear:
                case InterpolationMode.HighQualityBilinear:
                    return InterpolationFlags.Linear;

                default:
                    return InterpolationFlags.Cubic;
            }
        }

        /// <summary>
        /// Draws an image with transformation
        /// </summary>
        public void DrawImage(Bitmap image, RectangleF destRect, RectangleF srcRect, GraphicsUnit unit) {
            if (image == null) return;

            using (var srcLease = LeaseSource(image)) {
                Mat srcMat = srcLease.Mat;
                if (srcMat == null || srcMat.Empty()) return;
                if (_canvas == null || _canvas.Empty()) return;

                // Extract the source region
                Mat croppedSrc;
                bool croppedSrcCreated = false;
                if (srcRect.X == 0 && srcRect.Y == 0 && srcRect.Width == srcMat.Width && srcRect.Height == srcMat.Height) {
                    croppedSrc = srcMat;
                } else {
                    var srcRoi = new OpenCvSharp.Rect((int)srcRect.X, (int)srcRect.Y, (int)srcRect.Width, (int)srcRect.Height);
                    // Ensure ROI is within bounds
                    srcRoi.X = Math.Max(0, Math.Min(srcRoi.X, srcMat.Width - 1));
                    srcRoi.Y = Math.Max(0, Math.Min(srcRoi.Y, srcMat.Height - 1));
                    srcRoi.Width = Math.Min(srcRoi.Width, srcMat.Width - srcRoi.X);
                    srcRoi.Height = Math.Min(srcRoi.Height, srcMat.Height - srcRoi.Y);
                    // Clone the ROI to avoid dangling reference to parent Mat
                    using (Mat roiMat = new Mat(srcMat, srcRoi)) {
                        croppedSrc = roiMat.Clone();
                    }
                    croppedSrcCreated = true;
                }

                // Resize to destination size if needed
                Mat resizedSrc = croppedSrc;
                bool resizedSrcCreated = false;
                if ((int)destRect.Width != croppedSrc.Width || (int)destRect.Height != croppedSrc.Height) {
                    resizedSrc = new Mat();
                    Cv2.Resize(croppedSrc, resizedSrc, new OpenCvSharp.Size((int)destRect.Width, (int)destRect.Height));
                    resizedSrcCreated = true;
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
                        using (Mat affineTransform = Cv2.GetAffineTransform(srcPoints, dstPoints)) {
                            // Apply transformation directly to canvas
                            using (var transformed = new Mat(_canvas.Size(), _canvas.Type(), Scalar.All(0))) {
                                Cv2.WarpAffine(srcWithAlpha, transformed, affineTransform, _canvas.Size(), InterpolationFlags.Cubic, BorderTypes.Transparent);

                                // Blend transformed image with canvas using proper alpha compositing
                                if (transformed.Channels() == 4 && _canvas.Channels() == 4) {
                                    AlphaBlend(transformed, _canvas);
                                } else {
                                    // Use a mask to composite only the actual image area
                                    // (avoids incorrectly skipping genuinely black source pixels)
                                    using var mask = new Mat(srcWithAlpha.Size(), MatType.CV_8UC1, Scalar.All(255));
                                    using var transformedMask = new Mat(_canvas.Size(), MatType.CV_8UC1, Scalar.All(0));
                                    Cv2.WarpAffine(mask, transformedMask, affineTransform, _canvas.Size(),
                                        InterpolationFlags.Nearest, BorderTypes.Constant, new Scalar(0));
                                    transformed.CopyTo(_canvas, transformedMask);
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
                                    if (CompositingMode != CompositingMode.SourceCopy
                                            && srcWithAlpha.Channels() == 4 && dstRegion.Channels() == 4) {
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
                    if (resizedSrcCreated) {
                        resizedSrc?.Dispose();
                    }
                    if (croppedSrcCreated) {
                        croppedSrc?.Dispose();
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
                byte* srcBase = (byte*)src.DataPointer;
                byte* dstBase = (byte*)dst.DataPointer;
                int srcStep = (int)src.Step();
                int dstStep = (int)dst.Step();
                int width = src.Width;
                int height = src.Height;

                for (int row = 0; row < height; row++) {
                    byte* srcRow = srcBase + row * srcStep;
                    byte* dstRow = dstBase + row * dstStep;
                    for (int col = 0; col < width; col++) {
                        int idx = col * 4;
                        byte alpha = srcRow[idx + 3];

                        if (alpha > 0) {
                            float a = alpha / 255.0f;
                            float invA = 1.0f - a;

                            dstRow[idx + 0] = (byte)(srcRow[idx + 0] * a + dstRow[idx + 0] * invA);
                            dstRow[idx + 1] = (byte)(srcRow[idx + 1] * a + dstRow[idx + 1] * invA);
                            dstRow[idx + 2] = (byte)(srcRow[idx + 2] * a + dstRow[idx + 2] * invA);
                            dstRow[idx + 3] = (byte)Math.Min(255, alpha + dstRow[idx + 3] * invA);
                        }
                    }
                }
            }
        }

        public void Dispose() {
            if (!_disposed) {
                // Dispose the transform matrix
                _transform?.Dispose();
                _transform = null;
                _disposed = true;
            }
        }

        /// <summary>
        /// Saves the current state of the Graphics object (transform stack)
        /// </summary>
        public GraphicsState Save() {
            return new GraphicsState(
                _transform?.Clone(),
                _hasTransform);
        }

        /// <summary>
        /// Restores a previously saved Graphics state
        /// </summary>
        public void Restore(GraphicsState state) {
            if (state == null) return;
            _transform?.Dispose();
            _transform = state.Transform?.Clone() ?? new Mat(2, 3, MatType.CV_64F);
            _hasTransform = state.HasTransform;
            if (!_hasTransform) {
                _transform.Set(0, 0, 1.0); _transform.Set(0, 1, 0.0); _transform.Set(0, 2, 0.0);
                _transform.Set(1, 0, 0.0); _transform.Set(1, 1, 1.0); _transform.Set(1, 2, 0.0);
            }
            // Save() clones _transform into the state; that clone's job ends here, in the
            // Save/Restore pattern this mirrors. Dispose it now rather than waiting on the
            // finalizer for the native Mat buffer.
            state.Transform?.Dispose();
        }

        /// <summary>
        /// The bounding rectangle of the visible drawing surface. This shim tracks no clip
        /// region, so the answer is always the full canvas in device pixels. GDI+ reports this
        /// in world coordinates, so under a non-identity transform the two would disagree -
        /// callers here read it under the identity transform.
        /// </summary>
        public RectangleF VisibleClipBounds => new RectangleF(0, 0, _canvas.Width, _canvas.Height);

        /// <summary>
        /// Fills a rectangle with the specified brush
        /// </summary>
        public void FillRectangle(Brush brush, RectangleF rect) {
            FillRectangle(brush, rect.X, rect.Y, rect.Width, rect.Height);
        }

        /// <summary>
        /// Fills a rectangle with the specified brush using individual coordinates
        /// </summary>
        public void FillRectangle(Brush brush, float x, float y, float width, float height) {
            if (!_hasTransform) {
                var pt1 = new OpenCvSharp.Point((int)x, (int)y);
                var pt2 = new OpenCvSharp.Point((int)(x + width), (int)(y + height));
                Cv2.Rectangle(_canvas, pt1, pt2, brush.ToScalar(), -1, LineTypes.AntiAlias);
                return;
            }

            // A rotated rectangle can't be filled as an axis-aligned Cv2.Rectangle - fill the
            // four transformed corners as a convex polygon instead (mirrors DrawRectangle).
            var corners = new[] {
                TransformPoint(x, y),
                TransformPoint(x + width, y),
                TransformPoint(x + width, y + height),
                TransformPoint(x, y + height)
            };
            var cvCorners = new OpenCvSharp.Point[corners.Length];
            for (int i = 0; i < corners.Length; i++) {
                cvCorners[i] = new OpenCvSharp.Point((int)corners[i].X, (int)corners[i].Y);
            }
            Cv2.FillConvexPoly(_canvas, cvCorners, brush.ToScalar(), LineTypes.AntiAlias);
        }

        /// <summary>
        /// Fills a rectangle with the specified brush using integer coordinates
        /// </summary>
        public void FillRectangle(Brush brush, int x, int y, int width, int height) {
            FillRectangle(brush, (float)x, (float)y, (float)width, (float)height);
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
