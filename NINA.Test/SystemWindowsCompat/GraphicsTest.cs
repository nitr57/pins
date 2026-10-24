#region "copyright"

/*
    Copyright © 2025 Nico Trost <nico.trost57@gmail.com> and the PI.N.S. contributors

    This file is part of PI 'N' Stars.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NUnit.Framework;
using System.Drawing;
using System.Drawing.Imaging;

namespace NINA.Test.SystemWindowsCompat {

    [TestFixture]
    public class GraphicsTest {

        private static Bitmap NewCanvas(int width = 20, int height = 20) {
            return new Bitmap(width, height, PixelFormat.Format24bppRgb);
        }

        [Test]
        public void Clear_FillsEntireCanvas() {
            using var bitmap = NewCanvas();
            using var graphics = Graphics.FromImage(bitmap);

            graphics.Clear(Color.FromArgb(255, 0, 0));

            var corner = bitmap.GetPixel(0, 0);
            var center = bitmap.GetPixel(10, 10);
            Assert.That(corner.R, Is.EqualTo(255));
            Assert.That(corner.G, Is.EqualTo(0));
            Assert.That(center.R, Is.EqualTo(255));
        }

        [Test]
        public void DrawLine_ColorsPixelsAlongTheLine() {
            using var bitmap = NewCanvas();
            using var graphics = Graphics.FromImage(bitmap);
            using var pen = new Pen(Color.FromArgb(0, 255, 0), 3);

            graphics.DrawLine(pen, 0, 10, 19, 10);

            // Center of a 3px-thick horizontal line is fully saturated
            var pixel = bitmap.GetPixel(10, 10);
            Assert.That(pixel.G, Is.EqualTo(255));
            Assert.That(pixel.R, Is.EqualTo(0));

            // Far away from the line stays background
            Assert.That(bitmap.GetPixel(10, 2).G, Is.EqualTo(0));
        }

        [Test]
        public void FillRectangle_FillsInterior_LeavesExterior() {
            using var bitmap = NewCanvas();
            using var graphics = Graphics.FromImage(bitmap);
            using var brush = new SolidBrush(Color.FromArgb(0, 0, 255));

            graphics.FillRectangle(brush, 5f, 5f, 10f, 10f);

            Assert.That(bitmap.GetPixel(10, 10).B, Is.EqualTo(255));
            Assert.That(bitmap.GetPixel(2, 2).B, Is.EqualTo(0));
        }

        [Test]
        public void DrawRectangle_ColorsBorder_NotInterior() {
            using var bitmap = NewCanvas();
            using var graphics = Graphics.FromImage(bitmap);
            using var pen = new Pen(Color.FromArgb(255, 255, 255), 1);

            graphics.DrawRectangle(pen, 5f, 5f, 8f, 8f);

            // Border pixel (left edge midpoint); anti-aliasing may soften it slightly
            Assert.That((int)bitmap.GetPixel(5, 9).R, Is.GreaterThan(200));
            // Interior stays background
            Assert.That(bitmap.GetPixel(9, 9).R, Is.EqualTo(0));
        }

        // Sub-pixel pen widths used to truncate to an OpenCV thickness of 0, which fails a
        // native assertion (and -1 would mean "filled"); GDI+ draws a 1px hairline instead.
        [Test]
        public void DrawRectangle_SubPixelPenWidth_DrawsHairline() {
            using var bitmap = NewCanvas();
            using var graphics = Graphics.FromImage(bitmap);
            using var pen = new Pen(Color.FromArgb(255, 255, 255), 0.5f);

            graphics.DrawRectangle(pen, 5f, 5f, 8f, 8f);

            Assert.That((int)bitmap.GetPixel(5, 9).R, Is.GreaterThan(200), "border must be drawn as a hairline");
            Assert.That(bitmap.GetPixel(9, 9).R, Is.EqualTo(0), "interior stays background");
        }

        [Test]
        public void DrawEllipse_SubPixelPenWidth_DrawsHairline() {
            using var bitmap = NewCanvas();
            using var graphics = Graphics.FromImage(bitmap);
            using var pen = new Pen(Color.FromArgb(255, 255, 255), 0.5f);

            graphics.DrawEllipse(pen, 4f, 4f, 12f, 12f);

            // Leftmost point of the outline; interior stays background
            Assert.That((int)bitmap.GetPixel(4, 10).R, Is.GreaterThan(200), "outline must be drawn as a hairline");
            Assert.That(bitmap.GetPixel(10, 10).R, Is.EqualTo(0), "interior stays background");
        }

        [Test]
        public void FillEllipse_FillsCenter() {
            using var bitmap = NewCanvas();
            using var graphics = Graphics.FromImage(bitmap);
            using var brush = new SolidBrush(Color.FromArgb(255, 0, 0));

            graphics.FillEllipse(brush, new Rectangle(4, 4, 12, 12));

            Assert.That(bitmap.GetPixel(10, 10).R, Is.EqualTo(255));
            Assert.That(bitmap.GetPixel(0, 0).R, Is.EqualTo(0));
        }

        [Test]
        public void DrawImage_CopiesSourceOntoCanvas() {
            using var canvas = NewCanvas();
            using var sprite = new Bitmap(4, 4, PixelFormat.Format24bppRgb);
            for (int y = 0; y < 4; y++)
                for (int x = 0; x < 4; x++)
                    sprite.SetPixel(x, y, Color.FromArgb(1, 2, 3));

            using var graphics = Graphics.FromImage(canvas);
            graphics.DrawImage(sprite, 5, 6);

            var inside = canvas.GetPixel(6, 7);
            Assert.That(inside.R, Is.EqualTo(1));
            Assert.That(inside.G, Is.EqualTo(2));
            Assert.That(inside.B, Is.EqualTo(3));
            Assert.That(canvas.GetPixel(0, 0).B, Is.EqualTo(0));
        }

        // DrawImage reads its source without copying it, except when source and canvas share
        // memory: copying into an overlapping region in place would smear the image.
        [Test]
        public void DrawImage_BitmapOntoItself_DrawsTheOriginalPixels() {
            using var bitmap = new Bitmap(4, 4, PixelFormat.Format8bppIndexed);
            OpenCvSharp.Mat pixels = bitmap;
            FillGradient(pixels);
            using var original = pixels.Clone();

            using (var graphics = Graphics.FromImage(bitmap)) {
                graphics.DrawImage(bitmap, 1, 1);
            }

            AssertShiftedByOne(original, pixels);
        }

        [Test]
        public void DrawImage_BetweenBitmapsOverTheSameBuffer_DrawsTheOriginalPixels() {
            var buffer = new byte[16];
            var handle = System.Runtime.InteropServices.GCHandle.Alloc(buffer, System.Runtime.InteropServices.GCHandleType.Pinned);
            try {
                var scan0 = handle.AddrOfPinnedObject();
                using var canvas = new Bitmap(4, 4, 4, PixelFormat.Format8bppIndexed, scan0);
                using var sprite = new Bitmap(4, 4, 4, PixelFormat.Format8bppIndexed, scan0);
                OpenCvSharp.Mat pixels = canvas;
                FillGradient(pixels);
                using var original = pixels.Clone();

                using (var graphics = Graphics.FromImage(canvas)) {
                    graphics.DrawImage(sprite, 1, 1);
                }

                AssertShiftedByOne(original, pixels);
            } finally {
                handle.Free();
            }
        }

        private static void FillGradient(OpenCvSharp.Mat mat) {
            for (int y = 0; y < 4; y++)
                for (int x = 0; x < 4; x++)
                    mat.Set(y, x, (byte)(y * 10 + x + 1));
        }

        // Row and column 0 keep their pixels; everything else is the original moved by (1, 1).
        private static void AssertShiftedByOne(OpenCvSharp.Mat original, OpenCvSharp.Mat actual) {
            for (int y = 0; y < 4; y++) {
                for (int x = 0; x < 4; x++) {
                    byte expected = (x == 0 || y == 0) ? original.Get<byte>(y, x) : original.Get<byte>(y - 1, x - 1);
                    Assert.That(actual.Get<byte>(y, x), Is.EqualTo(expected), $"pixel ({x}, {y})");
                }
            }
        }

        [Test]
        public void MeasureString_ReturnsPositiveSize() {
            using var bitmap = NewCanvas();
            using var graphics = Graphics.FromImage(bitmap);
            using var font = new Font("Arial", 12);

            var size = graphics.MeasureString("Hello", font);

            Assert.That(size.Width, Is.GreaterThan(0));
            Assert.That(size.Height, Is.GreaterThan(0));
        }

        [Test]
        public void MeasureString_EmptyText_ReturnsZero() {
            using var bitmap = NewCanvas();
            using var graphics = Graphics.FromImage(bitmap);
            using var font = new Font("Arial", 12);

            var size = graphics.MeasureString("", font);

            Assert.That(size.Width, Is.EqualTo(0));
            Assert.That(size.Height, Is.EqualTo(0));
        }

        // REVIEW.md F13: DrawString ignored the transform stack entirely (Cv2.PutText has no
        // rotation parameter). A structural check: the same text's colored-pixel bounding box
        // should be wide-and-short when unrotated, tall-and-narrow after a 90-degree rotation.
        [Test]
        public void RotateTransform_RotatesSubsequentDrawString() {
            using var font = new Font("Arial", 16);
            using var brush = new SolidBrush(Color.FromArgb(255, 255, 255));

            using var unrotatedBitmap = new Bitmap(60, 60, PixelFormat.Format24bppRgb);
            using (var graphics = Graphics.FromImage(unrotatedBitmap)) {
                graphics.DrawString("Hi", font, brush, 5, 30);
            }
            var (unrotatedWidth, unrotatedHeight) = BoundingBoxOfLitPixels(unrotatedBitmap);

            using var rotatedBitmap = new Bitmap(60, 60, PixelFormat.Format24bppRgb);
            using (var graphics = Graphics.FromImage(rotatedBitmap)) {
                graphics.TranslateTransform(30, 30);
                graphics.RotateTransform(90);
                graphics.DrawString("Hi", font, brush, -25, 0);
            }
            var (rotatedWidth, rotatedHeight) = BoundingBoxOfLitPixels(rotatedBitmap);

            Assert.That(unrotatedWidth, Is.GreaterThan(unrotatedHeight), "unrotated text is wider than it is tall");
            Assert.That(rotatedHeight, Is.GreaterThan(rotatedWidth), "90-degree rotated text is taller than it is wide");
        }

        private static (int width, int height) BoundingBoxOfLitPixels(Bitmap bitmap) {
            int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
            for (int y = 0; y < bitmap.Height; y++) {
                for (int x = 0; x < bitmap.Width; x++) {
                    if (bitmap.GetPixel(x, y).R > 0) {
                        minX = Math.Min(minX, x);
                        minY = Math.Min(minY, y);
                        maxX = Math.Max(maxX, x);
                        maxY = Math.Max(maxY, y);
                    }
                }
            }
            return maxX < minX ? (0, 0) : (maxX - minX + 1, maxY - minY + 1);
        }

        [Test]
        public void CopyFromScreen_ThrowsPlatformNotSupported() {
            using var bitmap = NewCanvas();
            using var graphics = Graphics.FromImage(bitmap);

            Assert.Throws<PlatformNotSupportedException>(() =>
                graphics.CopyFromScreen(0, 0, 0, 0, new Size(1, 1), CopyPixelOperation.SourceCopy));
        }

        [Test]
        public void TranslateTransform_ShiftsSubsequentDrawLine() {
            using var bitmap = NewCanvas();
            using var graphics = Graphics.FromImage(bitmap);
            using var pen = new Pen(Color.FromArgb(255, 255, 255), 3);

            graphics.TranslateTransform(0, 5);
            graphics.DrawLine(pen, 0, 5, 19, 5);

            // With the translation applied the line must land at y=10, not y=5
            Assert.That(bitmap.GetPixel(10, 10).R, Is.EqualTo(255));
            Assert.That(bitmap.GetPixel(10, 5).R, Is.EqualTo(0));
        }

        // REVIEW.md F13: TranslateTransform/RotateTransform were ignored by everything except
        // one DrawImage overload. A 90-degree rotation about the canvas center should turn a
        // horizontal line into a vertical one.
        [Test]
        public void RotateTransform_RotatesSubsequentDrawLine() {
            using var bitmap = NewCanvas();
            using var graphics = Graphics.FromImage(bitmap);
            using var pen = new Pen(Color.FromArgb(255, 255, 255), 3);

            graphics.TranslateTransform(10, 10);
            graphics.RotateTransform(90);
            // A horizontal segment from (-8,0) to (8,0) in the rotated/translated frame should
            // become a vertical segment through the canvas center after a 90-degree rotation.
            graphics.DrawLine(pen, -8, 0, 8, 0);

            // Center of the 3px-thick rotated (now vertical) line is fully saturated
            Assert.That(bitmap.GetPixel(10, 10).R, Is.EqualTo(255));
            Assert.That(bitmap.GetPixel(10, 5).R, Is.EqualTo(255));
            // The un-rotated horizontal position stays background
            Assert.That(bitmap.GetPixel(2, 10).R, Is.EqualTo(0));
        }

        [Test]
        public void RotateTransform_RotatesSubsequentDrawEllipse() {
            using var bitmap = NewCanvas();
            using var graphics = Graphics.FromImage(bitmap);
            using var pen = new Pen(Color.FromArgb(255, 255, 255), 1);

            graphics.TranslateTransform(10, 10);
            graphics.RotateTransform(90);
            // A wide, flat ellipse rotated 90 degrees becomes tall and narrow.
            graphics.DrawEllipse(pen, -8, -2, 16, 4);

            Assert.That((int)bitmap.GetPixel(10, 2).R, Is.GreaterThan(200), "top of the now-vertical ellipse");
            Assert.That((int)bitmap.GetPixel(10, 18).R, Is.GreaterThan(200), "bottom of the now-vertical ellipse");
        }

        [Test]
        public void Save_Restore_RoundTripsTransform() {
            using var bitmap = NewCanvas();
            using var graphics = Graphics.FromImage(bitmap);
            using var pen = new Pen(Color.FromArgb(255, 255, 255), 3);

            var state = graphics.Save();
            graphics.TranslateTransform(0, 5);
            graphics.Restore(state);

            // After Restore, the pre-translate (identity) transform must be back in effect.
            graphics.DrawLine(pen, 0, 5, 19, 5);
            Assert.That(bitmap.GetPixel(10, 5).R, Is.EqualTo(255));
            Assert.That(bitmap.GetPixel(10, 10).R, Is.EqualTo(0));
        }
    }
}
