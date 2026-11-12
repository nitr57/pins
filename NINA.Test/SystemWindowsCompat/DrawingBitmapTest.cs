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
    public class DrawingBitmapTest {

        [Test]
        public void PixelFormatConstructor_ZeroInitializes() {
            using var bitmap = new Bitmap(4, 4, PixelFormat.Format24bppRgb);

            var pixel = bitmap.GetPixel(2, 2);
            Assert.That(pixel.R, Is.EqualTo(0));
            Assert.That(pixel.G, Is.EqualTo(0));
            Assert.That(pixel.B, Is.EqualTo(0));
        }

        [Test]
        public void SetGetPixel_24bpp_RoundTrips() {
            using var bitmap = new Bitmap(4, 4, PixelFormat.Format24bppRgb);
            bitmap.SetPixel(1, 2, Color.FromArgb(10, 20, 30));

            var pixel = bitmap.GetPixel(1, 2);
            Assert.That(pixel.A, Is.EqualTo(255));
            Assert.That(pixel.R, Is.EqualTo(10));
            Assert.That(pixel.G, Is.EqualTo(20));
            Assert.That(pixel.B, Is.EqualTo(30));
        }

        [Test]
        public void SetGetPixel_32bpp_PreservesAlpha() {
            using var bitmap = new Bitmap(4, 4, PixelFormat.Format32bppArgb);
            bitmap.SetPixel(0, 0, Color.FromArgb(128, 1, 2, 3));

            var pixel = bitmap.GetPixel(0, 0);
            Assert.That(pixel.A, Is.EqualTo(128));
            Assert.That(pixel.R, Is.EqualTo(1));
            Assert.That(pixel.G, Is.EqualTo(2));
            Assert.That(pixel.B, Is.EqualTo(3));
        }

        [Test]
        public void SetGetPixel_8bppGray_RoundTrips() {
            using var bitmap = new Bitmap(4, 4, PixelFormat.Format8bppIndexed);
            bitmap.SetPixel(3, 3, Color.FromArgb(50, 50, 50));

            var pixel = bitmap.GetPixel(3, 3);
            Assert.That(pixel.R, Is.EqualTo(50));
        }

        [Test]
        public void SetGetPixel_16bppGray_RoundTrips() {
            using var bitmap = new Bitmap(4, 4, PixelFormat.Format16bppGrayScale);
            bitmap.SetPixel(1, 1, Color.FromArgb(200, 200, 200));

            // Stored as value*257 (16 bit), read back as value>>8
            var pixel = bitmap.GetPixel(1, 1);
            Assert.That(pixel.R, Is.EqualTo(200));
        }

        [Test]
        public void GetPixel_OutOfBounds_Throws() {
            using var bitmap = new Bitmap(4, 4, PixelFormat.Format24bppRgb);

            Assert.Throws<ArgumentOutOfRangeException>(() => bitmap.GetPixel(4, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => bitmap.GetPixel(0, -1));
        }

        [Test]
        public void PixelFormat_ReflectsUnderlyingFormat() {
            using var gray8 = new Bitmap(2, 2, PixelFormat.Format8bppIndexed);
            using var gray16 = new Bitmap(2, 2, PixelFormat.Format16bppGrayScale);
            using var bgr = new Bitmap(2, 2, PixelFormat.Format24bppRgb);
            using var bgra = new Bitmap(2, 2, PixelFormat.Format32bppArgb);
            using var rgb48 = new Bitmap(2, 2, PixelFormat.Format48bppRgb);

            Assert.That(gray8.PixelFormat, Is.EqualTo(PixelFormat.Format8bppIndexed));
            Assert.That(gray16.PixelFormat, Is.EqualTo(PixelFormat.Format16bppGrayScale));
            Assert.That(bgr.PixelFormat, Is.EqualTo(PixelFormat.Format24bppRgb));
            Assert.That(bgra.PixelFormat, Is.EqualTo(PixelFormat.Format32bppArgb));
            Assert.That(rgb48.PixelFormat, Is.EqualTo(PixelFormat.Format48bppRgb));
        }

        [Test]
        public void Clone_IsIndependentOfOriginal() {
            using var original = new Bitmap(4, 4, PixelFormat.Format8bppIndexed);
            using var clone = original.Clone();

            original.SetPixel(0, 0, Color.FromArgb(255, 255, 255));

            Assert.That(clone.GetPixel(0, 0).R, Is.EqualTo(0));
        }

        [Test]
        public void CopyConstructor_IsIndependentOfSource() {
            using var original = new Bitmap(4, 4, PixelFormat.Format8bppIndexed);
            using var copy = new Bitmap(original);

            original.SetPixel(1, 1, Color.FromArgb(99, 99, 99));

            Assert.That(copy.GetPixel(1, 1).R, Is.EqualTo(0));
        }

        [Test]
        public void ResizeConstructor_ProducesRequestedDimensions() {
            using var original = new Bitmap(4, 4, PixelFormat.Format24bppRgb);
            using var resized = new Bitmap(original, 8, 2);

            Assert.That(resized.Width, Is.EqualTo(8));
            Assert.That(resized.Height, Is.EqualTo(2));
        }

        [Test]
        public void SaveAndReload_PreservesPixels() {
            var path = Path.Combine(Path.GetTempPath(), $"pins_compat_test_{Guid.NewGuid():N}.png");
            try {
                using (var bitmap = new Bitmap(4, 4, PixelFormat.Format24bppRgb)) {
                    bitmap.SetPixel(2, 1, Color.FromArgb(10, 20, 30));
                    bitmap.Save(path);
                }

                using var reloaded = new Bitmap(path);
                Assert.That(reloaded.Width, Is.EqualTo(4));
                Assert.That(reloaded.Height, Is.EqualTo(4));

                var pixel = reloaded.GetPixel(2, 1);
                Assert.That(pixel.R, Is.EqualTo(10));
                Assert.That(pixel.G, Is.EqualTo(20));
                Assert.That(pixel.B, Is.EqualTo(30));
            } finally {
                File.Delete(path);
            }
        }

        [Test]
        public void SaveToStream_Png_ProducesDecodableData() {
            using var bitmap = new Bitmap(4, 4, PixelFormat.Format24bppRgb);
            bitmap.SetPixel(0, 0, Color.FromArgb(255, 0, 0));

            using var stream = new MemoryStream();
            bitmap.Save(stream, ImageFormat.Png);

            Assert.That(stream.Length, Is.GreaterThan(0));
            // PNG signature
            var header = stream.ToArray();
            Assert.That(header[1], Is.EqualTo((byte)'P'));
            Assert.That(header[2], Is.EqualTo((byte)'N'));
            Assert.That(header[3], Is.EqualTo((byte)'G'));
        }

        // The static ImageFormat instances used to leave Guid at Guid.Empty, which made every
        // FormatID comparison in the encoder-based Save overloads match Jpeg first - a PNG
        // encoder silently produced JPEG bytes.
        [Test]
        public void ImageFormat_GuidsAreDistinctCanonicalGdiPlusValues() {
            Assert.That(ImageFormat.Png.Guid, Is.EqualTo(new Guid("b96b3caf-0728-11d3-9d7b-0000f81ef32e")));
            Assert.That(ImageFormat.Jpeg.Guid, Is.EqualTo(new Guid("b96b3cae-0728-11d3-9d7b-0000f81ef32e")));
            Assert.That(ImageFormat.Bmp.Guid, Is.EqualTo(new Guid("b96b3cab-0728-11d3-9d7b-0000f81ef32e")));
            Assert.That(ImageFormat.Tiff.Guid, Is.EqualTo(new Guid("b96b3cb1-0728-11d3-9d7b-0000f81ef32e")));
            Assert.That(ImageFormat.Gif.Guid, Is.EqualTo(new Guid("b96b3cb0-0728-11d3-9d7b-0000f81ef32e")));
        }

        [Test]
        public void SaveToStream_WithPngEncoder_ProducesPngNotJpeg() {
            var pngEncoder = ImageCodecInfo.GetImageEncoders()
                .First(c => c.FormatID == ImageFormat.Png.Guid);
            using var parameters = new EncoderParameters(1);
            parameters.Param[0] = new EncoderParameter(Encoder.Quality, 90L);

            using var bitmap = new Bitmap(4, 4, PixelFormat.Format24bppRgb);
            using var stream = new MemoryStream();
            bitmap.Save(stream, pngEncoder, parameters);

            var bytes = stream.ToArray();
            Assert.That(bytes.Length, Is.GreaterThan(8));
            // PNG signature, not JPEG's FF D8
            Assert.That(bytes[0], Is.EqualTo(0x89));
            Assert.That(bytes[1], Is.EqualTo((byte)'P'));
            Assert.That(bytes[2], Is.EqualTo((byte)'N'));
            Assert.That(bytes[3], Is.EqualTo((byte)'G'));
        }

        [Test]
        public void SaveToStream_WithJpegEncoder_ProducesJpeg() {
            var jpegEncoder = ImageCodecInfo.GetImageEncoders()
                .First(c => c.FormatID == ImageFormat.Jpeg.Guid);
            using var parameters = new EncoderParameters(1);
            parameters.Param[0] = new EncoderParameter(Encoder.Quality, 90L);

            using var bitmap = new Bitmap(4, 4, PixelFormat.Format24bppRgb);
            using var stream = new MemoryStream();
            bitmap.Save(stream, jpegEncoder, parameters);

            var bytes = stream.ToArray();
            Assert.That(bytes.Length, Is.GreaterThan(2));
            // JPEG SOI marker
            Assert.That(bytes[0], Is.EqualTo(0xFF));
            Assert.That(bytes[1], Is.EqualTo(0xD8));
        }

        [Test]
        public void LockBits_ExposesValidBufferGeometry() {
            using var bitmap = new Bitmap(4, 4, PixelFormat.Format24bppRgb);

            var data = bitmap.LockBits(
                new Rectangle(0, 0, 4, 4), ImageLockMode.ReadWrite, bitmap.PixelFormat);
            try {
                Assert.That(data.Width, Is.EqualTo(4));
                Assert.That(data.Height, Is.EqualTo(4));
                Assert.That(data.Stride, Is.GreaterThanOrEqualTo(4 * 3));
                Assert.That(data.Scan0, Is.Not.EqualTo(IntPtr.Zero));
                Assert.That(data.PixelFormat, Is.EqualTo(PixelFormat.Format24bppRgb));
            } finally {
                bitmap.UnlockBits(data);
            }
        }

        // Bitmap.Width/Height used to shadow (not override) Image's settable auto-properties,
        // so any access through an Image-typed reference read the never-assigned base backing
        // field and got 0.
        [Test]
        public void WidthHeight_ThroughImageTypedReference_ReturnMatDimensions() {
            using var bitmap = new Bitmap(7, 5, PixelFormat.Format24bppRgb);
            System.Drawing.Image image = bitmap;

            Assert.That(image.Width, Is.EqualTo(7));
            Assert.That(image.Height, Is.EqualTo(5));
        }

        [Test]
        public void GetMat_ReturnsIndependentClone() {
            using var bitmap = new Bitmap(2, 2, PixelFormat.Format8bppIndexed);

            using var mat = bitmap.GetMat();
            mat.Set<byte>(0, 0, 99);

            Assert.That(bitmap.GetPixel(0, 0).R, Is.EqualTo(0));
        }

        [Test]
        public void Save_IntoAMissingFolder_ThrowsAsGdiPlus() {
            var path = Path.Combine(Path.GetTempPath(), $"pins_missing_{Guid.NewGuid():N}", "image.png");
            using var bitmap = new Bitmap(4, 4, PixelFormat.Format24bppRgb);

            Assert.That(() => bitmap.Save(path), Throws.InstanceOf<System.Runtime.InteropServices.ExternalException>());
            Assert.That(() => bitmap.Save(path, ImageFormat.Png), Throws.InstanceOf<System.Runtime.InteropServices.ExternalException>());
        }

        [Test]
        public void SaveToStream_TakesTheQualityParameter_NotJustTheFirst() {
            using var bitmap = new Bitmap(64, 64, PixelFormat.Format24bppRgb);

            for (int y = 0; y < 64; y++) {
                for (int x = 0; x < 64; x++) {
                    bitmap.SetPixel(x, y, Color.FromArgb((x * 37) % 256, (y * 53) % 256, (x * y) % 256));
                }
            }

            ImageCodecInfo jpeg = ImageCodecInfo.GetImageEncoders().First(codec => codec.FormatID == ImageFormat.Jpeg.Guid);

            long Size(params EncoderParameter[] parameters) {
                var encoderParameters = new EncoderParameters(parameters.Length);

                for (int i = 0; i < parameters.Length; i++) {
                    encoderParameters.Param[i] = parameters[i];
                }

                using var stream = new MemoryStream();
                bitmap.Save(stream, jpeg, encoderParameters);

                return stream.Length;
            }

            // Another parameter first must not be read as the quality.
            long lowQuality = Size(new EncoderParameter(Encoder.Compression, 100), new EncoderParameter(Encoder.Quality, 5));
            long defaultQuality = Size(new EncoderParameter(Encoder.Compression, 5));

            Assert.That(lowQuality, Is.LessThan(defaultQuality));
        }
    }
}
