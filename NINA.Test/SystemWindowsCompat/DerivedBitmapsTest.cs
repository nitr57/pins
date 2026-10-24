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
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace NINA.Test.SystemWindowsCompat {

    [TestFixture]
    public class DerivedBitmapsTest {

        private static BitmapSource CreateGray8(int width, int height, byte[] pixels) {
            return BitmapSource.Create(width, height, 96, 96, PixelFormats.Gray8, null, pixels, width);
        }

        private static BitmapSource CreateGray16(int width, int height, ushort[] pixels) {
            return BitmapSource.Create(width, height, 96, 96, PixelFormats.Gray16, null, pixels, width * 2);
        }

        private static byte[] ReadBytes(BitmapSource source) {
            var result = new byte[source.PixelWidth * source.PixelHeight * (source.Format.BitsPerPixel / 8)];
            source.CopyPixels(result, source.PixelWidth * (source.Format.BitsPerPixel / 8), 0);
            return result;
        }

        #region FormatConvertedBitmap

        [Test]
        public void FormatConverted_Gray16_To_Gray8_ScalesBy257() {
            using var source = CreateGray16(2, 1, new ushort[] { 25700, 514 });
            using var converted = new FormatConvertedBitmap(source, PixelFormats.Gray8, null, 0);

            Assert.That(converted.Format == PixelFormats.Gray8, Is.True);
            Assert.That(ReadBytes(converted), Is.EqualTo(new byte[] { 100, 2 }));
        }

        [Test]
        public void FormatConverted_Gray8_To_Gray16_ScalesBy257() {
            using var source = CreateGray8(2, 1, new byte[] { 100, 2 });
            using var converted = new FormatConvertedBitmap(source, PixelFormats.Gray16, null, 0);

            Assert.That(converted.Format == PixelFormats.Gray16, Is.True);

            var output = new ushort[2];
            converted.CopyPixels(output, 4, 0);
            Assert.That(output, Is.EqualTo(new ushort[] { 25700, 514 }));
        }

        [Test]
        public void FormatConverted_Bgr24_To_Gray8_UsesLuminanceWeights() {
            // One pixel B=200, G=150, R=100 -> gray = 0.299*100 + 0.587*150 + 0.114*200 = 140.75
            var pixels = new byte[] { 200, 150, 100 };
            using var source = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgr24, null, pixels, 3);
            using var converted = new FormatConvertedBitmap(source, PixelFormats.Gray8, null, 0);

            Assert.That(converted.Format == PixelFormats.Gray8, Is.True);
            Assert.That((int)ReadBytes(converted)[0], Is.EqualTo(141).Within(1));
        }

        [Test]
        public void FormatConverted_Gray8_To_Bgra32_ProducesOpaqueGrayPixels() {
            using var source = CreateGray8(1, 1, new byte[] { 77 });
            using var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

            Assert.That(converted.Format == PixelFormats.Bgra32, Is.True);
            Assert.That(ReadBytes(converted), Is.EqualTo(new byte[] { 77, 77, 77, 255 }));
        }

        [Test]
        public void FormatConverted_Bgra32_To_Bgr24_DropsAlpha() {
            var pixels = new byte[] { 1, 2, 3, 128 };
            using var source = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, pixels, 4);
            using var converted = new FormatConvertedBitmap(source, PixelFormats.Bgr24, null, 0);

            Assert.That(converted.Format == PixelFormats.Bgr24, Is.True);
            Assert.That(ReadBytes(converted), Is.EqualTo(new byte[] { 1, 2, 3 }));
        }

        [Test]
        public void FormatConverted_Bgr24_To_Gray16_ProducesGray16() {
            var pixels = new byte[] { 50, 50, 50 };
            using var source = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgr24, null, pixels, 3);
            using var converted = new FormatConvertedBitmap(source, PixelFormats.Gray16, null, 0);

            Assert.That(converted.Format == PixelFormats.Gray16, Is.True);
        }

        #endregion

        #region CroppedBitmap

        [Test]
        public void CroppedBitmap_ExtractsRequestedRegion() {
            // 4x4 with values 0..15 row-major
            var pixels = new byte[16];
            for (byte i = 0; i < 16; i++) pixels[i] = i;
            using var source = CreateGray8(4, 4, pixels);

            using var cropped = new CroppedBitmap(source, new Int32Rect(1, 1, 2, 2));

            Assert.That(cropped.PixelWidth, Is.EqualTo(2));
            Assert.That(cropped.PixelHeight, Is.EqualTo(2));
            Assert.That(ReadBytes(cropped), Is.EqualTo(new byte[] { 5, 6, 9, 10 }));
        }

        [Test]
        public void CroppedBitmap_ClampsRectToSourceBounds() {
            using var source = CreateGray8(4, 4, new byte[16]);

            using var cropped = new CroppedBitmap(source, new Int32Rect(2, 2, 10, 10));

            Assert.That(cropped.PixelWidth, Is.EqualTo(2));
            Assert.That(cropped.PixelHeight, Is.EqualTo(2));
        }

        [Test]
        public void CroppedBitmap_IsIndependentOfSourceDisposal() {
            var source = CreateGray8(4, 4, new byte[16]);
            var cropped = new CroppedBitmap(source, new Int32Rect(0, 0, 2, 2));

            source.Dispose();

            using (cropped) {
                Assert.That(cropped.PixelWidth, Is.EqualTo(2));
                Assert.DoesNotThrow(() => ReadBytes(cropped));
            }
        }

        #endregion

        #region Source borrowing

        // The derived bitmaps read their source without copying it first. They must neither
        // dispose nor modify it, and their result must not depend on it afterwards.
        [Test]
        public void DerivedBitmaps_LeaveSourceUsableAndUnchanged() {
            var pixels = new byte[] { 0, 10, 20, 30, 40, 50, 60, 70, 80, 90, 100, 110, 120, 130, 140, 150 };
            using var source = CreateGray8(4, 4, pixels);

            using (new CroppedBitmap(source, new Int32Rect(1, 1, 2, 2))) { }
            using (new TransformedBitmap(source, new ScaleTransform(0.5, 0.5))) { }
            using (new TransformedBitmap(source, new ScaleTransform(-1, 1))) { }
            using (new FormatConvertedBitmap(source, PixelFormats.Gray16, null, 0)) { }

            Assert.That(source.PixelWidth, Is.EqualTo(4));
            Assert.That(ReadBytes(source), Is.EqualTo(pixels));
        }

        [Test]
        public void DerivedBitmaps_AreIndependentOfSourceDisposal() {
            var source = CreateGray8(2, 2, new byte[] { 10, 20, 30, 40 });
            var transformed = new TransformedBitmap(source, new ScaleTransform(1, 1));
            var converted = new FormatConvertedBitmap(source, PixelFormats.Gray8, null, 0);

            source.Dispose();

            using (transformed)
            using (converted) {
                Assert.That(ReadBytes(transformed), Is.EqualTo(new byte[] { 10, 20, 30, 40 }));
                Assert.That(ReadBytes(converted), Is.EqualTo(new byte[] { 10, 20, 30, 40 }));
            }
        }

        [Test]
        public void TransformedBitmap_FromWriteableBitmap_LeavesSourceUsable() {
            using var source = new WriteableBitmap(CreateGray8(2, 2, new byte[] { 1, 2, 3, 4 }));
            using var transformed = new TransformedBitmap(source, new ScaleTransform(2, 2));

            Assert.That(transformed.PixelWidth, Is.EqualTo(4));
            Assert.That(ReadBytes(source), Is.EqualTo(new byte[] { 1, 2, 3, 4 }));
        }

        #endregion

        #region TransformedBitmap

        [Test]
        public void TransformedBitmap_ScaleUp_DoublesDimensions() {
            using var source = CreateGray8(2, 2, new byte[] { 0, 50, 100, 150 });
            using var transformed = new TransformedBitmap(source, new ScaleTransform(2, 2));

            Assert.That(transformed.PixelWidth, Is.EqualTo(4));
            Assert.That(transformed.PixelHeight, Is.EqualTo(4));
        }

        [Test]
        public void TransformedBitmap_ScaleDown_HalvesDimensions() {
            using var source = CreateGray8(4, 4, new byte[16]);
            using var transformed = new TransformedBitmap(source, new ScaleTransform(0.5, 0.5));

            Assert.That(transformed.PixelWidth, Is.EqualTo(2));
            Assert.That(transformed.PixelHeight, Is.EqualTo(2));
        }

        [Test]
        public void TransformedBitmap_ZeroScale_KeepsSourceDimensions() {
            using var source = CreateGray8(4, 4, new byte[16]);
            using var transformed = new TransformedBitmap(source, new ScaleTransform(0, 0));

            Assert.That(transformed.PixelWidth, Is.EqualTo(4));
            Assert.That(transformed.PixelHeight, Is.EqualTo(4));
        }

        [Test]
        public void TransformedBitmap_NegativeScaleX_MirrorsHorizontally() {
            // 2x1 image: a horizontal mirror must swap the two pixels.
            // A vertical flip of a single-row image is a no-op, so this
            // test discriminates between the two flip axes.
            using var source = CreateGray8(2, 1, new byte[] { 10, 200 });
            using var transformed = new TransformedBitmap(source, new ScaleTransform(-1, 1));

            Assert.That(ReadBytes(transformed), Is.EqualTo(new byte[] { 200, 10 }));
        }

        // REVIEW.md F12: the parameterless ctor + BeginInit/Source/Transform/EndInit idiom
        // left _mat null if EndInit ran before both Source and Transform were set, NREing on
        // the next PixelWidth/PixelHeight access instead of behaving like an empty image.
        // REVIEW.md F12: TransformedBitmap only ever handled ScaleTransform; a TranslateTransform
        // (a real, concrete Transform in this compat lib) silently copied the source unchanged.
        [Test]
        public void TransformedBitmap_TranslateTransform_ShiftsContent() {
            // 3x1 image; shifting right by 1px must clip the first pixel and pad a black pixel on the left.
            using var source = CreateGray8(3, 1, new byte[] { 10, 20, 30 });
            using var transformed = new TransformedBitmap(source, new TranslateTransform { X = 1, Y = 0 });

            Assert.That(ReadBytes(transformed), Is.EqualTo(new byte[] { 0, 10, 20 }));
        }

        // REVIEW.md F25 follow-up: transforms other than Scale/Translate (RotateTransform,
        // TransformGroup, MatrixTransform, custom subclasses) go through a general affine
        // fallback keyed off Transform.Value, which must size the output to the rotated
        // bounding box rather than leaving the bitmap partially off-canvas.
        [Test]
        public void TransformedBitmap_Rotate90_SwapsDimensions() {
            using var source = CreateGray8(4, 2, new byte[8]);
            using var transformed = new TransformedBitmap(source, new RotateTransform(90));

            Assert.That(transformed.PixelWidth, Is.EqualTo(2));
            Assert.That(transformed.PixelHeight, Is.EqualTo(4));
        }

        [Test]
        public void TransformedBitmap_EndInit_WithoutTransformSet_DoesNotThrow() {
            using var source = CreateGray8(2, 2, new byte[4]);
            using var transformed = new TransformedBitmap();
            transformed.BeginInit();
            transformed.Source = source;
            // Transform deliberately left unset.
            transformed.EndInit();

            Assert.That(transformed.PixelWidth, Is.EqualTo(0));
            Assert.That(transformed.PixelHeight, Is.EqualTo(0));
        }

        #endregion
    }
}
