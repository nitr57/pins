#region "copyright"

/*
    Copyright © 2025 Nico Trost <nico.trost57@gmail.com> and the PI.N.S. contributors

    This file is part of PI 'N' Stars.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Accord.Imaging.Filters;
using NUnit.Framework;
using System.Drawing;
using System.Drawing.Imaging;

namespace NINA.Test.SystemWindowsCompat {

    [TestFixture]
    public class AccordFiltersTest {

        [Test]
        public void Grayscale_AppliesCustomChannelWeights() {
            // BT.709 weights as used by NINA's image pipeline
            var filter = new Grayscale(0.2125, 0.7154, 0.0721);

            using var source = new Bitmap(2, 2, PixelFormat.Format24bppRgb);
            for (int y = 0; y < 2; y++)
                for (int x = 0; x < 2; x++)
                    source.SetPixel(x, y, Color.FromArgb(100, 150, 200));

            using var result = filter.Apply(source);

            Assert.That(result.PixelFormat, Is.EqualTo(PixelFormat.Format8bppIndexed));
            // 0.2125*100 + 0.7154*150 + 0.0721*200 = 142.98
            Assert.That((int)result.GetPixel(0, 0).R, Is.EqualTo(143).Within(1));
        }

        // The 16-bit color path (Rgb48 star detection/annotation) must stay 16-bit and keep the
        // weighted values, and must not touch its source.
        [Test]
        public void Grayscale_On48bppInput_ReturnsWeighted16BitGray() {
            var filter = new Grayscale(0.2125, 0.7154, 0.0721);

            var mat = new OpenCvSharp.Mat(1, 2, OpenCvSharp.MatType.CV_16UC3);
            // OpenCV order is B, G, R
            mat.Set(0, 0, new OpenCvSharp.Vec3w(1000, 2000, 3000));
            mat.Set(0, 1, new OpenCvSharp.Vec3w(65535, 65535, 65535));
            using var source = new Bitmap(mat);

            using var result = filter.Apply(source);

            Assert.That(result.PixelFormat, Is.EqualTo(PixelFormat.Format16bppGrayScale));
            OpenCvSharp.Mat resultMat = result;
            // 0.2125*3000 + 0.7154*2000 + 0.0721*1000 = 2140.1
            Assert.That((int)resultMat.Get<ushort>(0, 0), Is.EqualTo(2140).Within(1));
            Assert.That((int)resultMat.Get<ushort>(0, 1), Is.EqualTo(65535));

            OpenCvSharp.Mat sourceMat = source;
            Assert.That(sourceMat.Get<OpenCvSharp.Vec3w>(0, 0), Is.EqualTo(new OpenCvSharp.Vec3w(1000, 2000, 3000)));
        }

        [Test]
        public void Grayscale_OnGrayInput_ReturnsIndependentCopy() {
            var filter = new Grayscale(0.2125, 0.7154, 0.0721);

            using var source = new Bitmap(2, 2, PixelFormat.Format8bppIndexed);
            source.SetPixel(0, 0, Color.FromArgb(77, 77, 77));

            using var result = filter.Apply(source);
            source.SetPixel(0, 0, Color.FromArgb(0, 0, 0));

            Assert.That(result.GetPixel(0, 0).R, Is.EqualTo(77));
        }

        [Test]
        public void Median_RemovesSaltNoise() {
            using var image = new Bitmap(5, 5, PixelFormat.Format8bppIndexed);
            for (int y = 0; y < 5; y++)
                for (int x = 0; x < 5; x++)
                    image.SetPixel(x, y, Color.FromArgb(100, 100, 100));
            image.SetPixel(2, 2, Color.FromArgb(255, 255, 255));

            new Median(3).ApplyInPlace(image);

            Assert.That(image.GetPixel(2, 2).R, Is.EqualTo(100));
        }

        [Test]
        public void Crop_ExtractsRegion() {
            using var source = new Bitmap(4, 4, PixelFormat.Format8bppIndexed);
            for (int y = 0; y < 4; y++)
                for (int x = 0; x < 4; x++) {
                    int value = (y * 4 + x) * 10;
                    source.SetPixel(x, y, Color.FromArgb(value, value, value));
                }

            using var cropped = new Crop(new Rectangle(1, 1, 2, 2)).Apply(source);

            Assert.That(cropped.Width, Is.EqualTo(2));
            Assert.That(cropped.Height, Is.EqualTo(2));
            // Source pixel (1,1) has value (1*4+1)*10 = 50
            Assert.That(cropped.GetPixel(0, 0).R, Is.EqualTo(50));
            // Source pixel (2,2) has value (2*4+2)*10 = 100
            Assert.That(cropped.GetPixel(1, 1).R, Is.EqualTo(100));
        }

        [Test]
        public void Crop_ClampsRectToImageBounds() {
            using var source = new Bitmap(4, 4, PixelFormat.Format8bppIndexed);

            using var cropped = new Crop(new Rectangle(2, 2, 10, 10)).Apply(source);

            Assert.That(cropped.Width, Is.EqualTo(2));
            Assert.That(cropped.Height, Is.EqualTo(2));
        }

        [Test]
        public void BinaryDilation3x3_GrowsSinglePixel() {
            using var image = new Bitmap(5, 5, PixelFormat.Format8bppIndexed);
            image.SetPixel(2, 2, Color.FromArgb(255, 255, 255));

            new BinaryDilation3x3().ApplyInPlace(image);

            Assert.That(image.GetPixel(2, 2).R, Is.EqualTo(255));
            Assert.That(image.GetPixel(1, 2).R, Is.EqualTo(255));
            Assert.That(image.GetPixel(2, 1).R, Is.EqualTo(255));
            Assert.That(image.GetPixel(0, 0).R, Is.EqualTo(0));
        }

        [Test]
        public void GaussianBlur_SpreadsIntensity() {
            using var image = new Bitmap(9, 9, PixelFormat.Format8bppIndexed);
            image.SetPixel(4, 4, Color.FromArgb(255, 255, 255));

            new GaussianBlur(1.0, 5).ApplyInPlace(image);

            // Peak drops below 255, direct neighbor gains intensity
            Assert.That(image.GetPixel(4, 4).R, Is.LessThan(255));
            Assert.That(image.GetPixel(3, 4).R, Is.GreaterThan(0));
        }

        [Test]
        public void Convert16bppTo8bpp_ScalesDown() {
            using var source = new Bitmap(2, 2, PixelFormat.Format16bppGrayScale);
            // SetPixel stores luminance*257: 100 -> 25700
            source.SetPixel(0, 0, Color.FromArgb(100, 100, 100));

            using var result = Accord.Imaging.Image.Convert16bppTo8bpp(source);

            Assert.That(result.PixelFormat, Is.EqualTo(PixelFormat.Format8bppIndexed));
            // 25700 / 256 = 100.4 -> 100
            Assert.That((int)result.GetPixel(0, 0).R, Is.EqualTo(100).Within(1));
        }

        [Test]
        public void CannyEdgeDetector_FindsEdgesOfSquare() {
            using var image = new Bitmap(20, 20, PixelFormat.Format8bppIndexed);
            for (int y = 5; y < 15; y++)
                for (int x = 5; x < 15; x++)
                    image.SetPixel(x, y, Color.FromArgb(255, 255, 255));

            new CannyEdgeDetector(20, 100).ApplyInPlace(image);

            // Something must be marked as an edge, and the flat interior must not be
            bool anyEdge = false;
            for (int y = 0; y < 20 && !anyEdge; y++)
                for (int x = 0; x < 20 && !anyEdge; x++)
                    anyEdge = image.GetPixel(x, y).R > 0;

            Assert.That(anyEdge, Is.True);
            Assert.That(image.GetPixel(10, 10).R, Is.EqualTo(0), "interior of a flat region is not an edge");
        }

        // SISThreshold must compute Accord's *global* threshold T = sum(w*I)/sum(w) with
        // w = max(|I(x+1,y)-I(x-1,y)|, |I(x,y+1)-I(x,y-1)|); an earlier implementation used a
        // local adaptive threshold, which diverged on star-detection input. The expected
        // threshold here is recomputed independently with the reference formula.
        [Test]
        public void SISThreshold_MatchesReferenceFormula() {
            const int size = 10;
            var pixels = new byte[size, size];
            using var image = new Bitmap(size, size, PixelFormat.Format8bppIndexed);
            for (int y = 0; y < size; y++) {
                for (int x = 0; x < size; x++) {
                    // background 50 with a bright 200 square, like a star on sky background
                    byte value = (x >= 3 && x <= 6 && y >= 3 && y <= 6) ? (byte)200 : (byte)50;
                    pixels[y, x] = value;
                    image.SetPixel(x, y, Color.FromArgb(value, value, value));
                }
            }

            // Reference implementation of Accord.Imaging.Filters.SISThreshold.CalculateThreshold
            double weightTotal = 0, total = 0;
            for (int y = 1; y < size - 1; y++) {
                for (int x = 1; x < size - 1; x++) {
                    double ex = System.Math.Abs(pixels[y, x + 1] - pixels[y, x - 1]);
                    double ey = System.Math.Abs(pixels[y + 1, x] - pixels[y - 1, x]);
                    double weight = System.Math.Max(ex, ey);
                    weightTotal += weight;
                    total += weight * pixels[y, x];
                }
            }
            int expectedThreshold = (int)(total / weightTotal);

            new SISThreshold().ApplyInPlace(image);

            for (int y = 0; y < size; y++) {
                for (int x = 0; x < size; x++) {
                    byte expected = pixels[y, x] >= expectedThreshold ? (byte)255 : (byte)0;
                    Assert.That(image.GetPixel(x, y).R, Is.EqualTo(expected),
                        $"pixel ({x},{y}) with value {pixels[y, x]} vs threshold {expectedThreshold}");
                }
            }
        }

        [Test]
        public void SISThreshold_IsGlobal_UniformRegionsFarFromEdgesStayUniform() {
            // A local/adaptive threshold marks pixels relative to their neighborhood, which
            // turns the interior of large flat regions white or speckled. A global threshold
            // must binarize purely by intensity: the whole dim background black, the whole
            // bright blob white.
            const int size = 32;
            using var image = new Bitmap(size, size, PixelFormat.Format8bppIndexed);
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                    image.SetPixel(x, y, Color.FromArgb(30, 30, 30));
            for (int y = 12; y < 20; y++)
                for (int x = 12; x < 20; x++)
                    image.SetPixel(x, y, Color.FromArgb(220, 220, 220));

            using var result = new SISThreshold().Apply(image);

            Assert.That(result.GetPixel(2, 2).R, Is.EqualTo(0), "dim background far from the blob");
            Assert.That(result.GetPixel(29, 29).R, Is.EqualTo(0), "dim background far from the blob");
            Assert.That(result.GetPixel(15, 15).R, Is.EqualTo(255), "center of the bright blob");
        }

        [Test]
        public void SISThreshold_UniformImage_BecomesAllWhite() {
            // No gradients -> weight total 0 -> Accord's threshold is 0 and every pixel
            // satisfies >= 0.
            using var image = new Bitmap(4, 4, PixelFormat.Format8bppIndexed);
            for (int y = 0; y < 4; y++)
                for (int x = 0; x < 4; x++)
                    image.SetPixel(x, y, Color.FromArgb(100, 100, 100));

            new SISThreshold().ApplyInPlace(image);

            Assert.That(image.GetPixel(0, 0).R, Is.EqualTo(255));
            Assert.That(image.GetPixel(3, 3).R, Is.EqualTo(255));
        }

        // BaseUsingCopyPartialFilter.ApplyInPlace used to wrap the same Mat as both source and
        // destination, so a filter reading a source pixel it had already written through the
        // destination saw corrupted data. The built-in Blur/Canny hid this by delegating
        // whole-image work to OpenCV; a plain per-pixel filter exposes it: mirroring each row
        // in-place re-reads overwritten pixels and un-mirrors the second half.
        private sealed class HorizontalMirrorTestFilter : Accord.Imaging.Filters.BaseUsingCopyPartialFilter {

            protected override void ProcessFilter(Accord.Imaging.UnmanagedImage source, Accord.Imaging.UnmanagedImage destination, Rectangle rect) {
                for (int y = 0; y < rect.Height; y++) {
                    for (int x = 0; x < rect.Width; x++) {
                        byte value = System.Runtime.InteropServices.Marshal.ReadByte(
                            source.ImageData, y * source.Stride + (rect.Width - 1 - x));
                        System.Runtime.InteropServices.Marshal.WriteByte(
                            destination.ImageData, y * destination.Stride + x, value);
                    }
                }
            }
        }

        [Test]
        public void ApplyInPlace_SourceIsIsolatedFromDestinationWrites() {
            const int size = 8;
            using var image = new Bitmap(size, size, PixelFormat.Format8bppIndexed);
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                    image.SetPixel(x, y, Color.FromArgb(x * 10, x * 10, x * 10));

            new HorizontalMirrorTestFilter().ApplyInPlace(image);

            for (int x = 0; x < size; x++) {
                Assert.That(image.GetPixel(x, 0).R, Is.EqualTo((size - 1 - x) * 10),
                    $"column {x} must hold the pre-filter value of column {size - 1 - x}");
            }
        }

        // REVIEW.md F18: UnmanagedImage(BitmapData) left its backing Mat null, so
        // ToManagedImage() returned a Bitmap that NREs on first use (Width/Height/GetPixel all
        // read through _mat).
        [Test]
        public void UnmanagedImage_FromBitmapData_ToManagedImage_ProducesUsableBitmap() {
            using var source = new Bitmap(2, 2, PixelFormat.Format8bppIndexed);
            source.SetPixel(0, 0, Color.FromArgb(10, 10, 10));
            source.SetPixel(1, 0, Color.FromArgb(20, 20, 20));
            source.SetPixel(0, 1, Color.FromArgb(30, 30, 30));
            source.SetPixel(1, 1, Color.FromArgb(40, 40, 40));

            var bitmapData = source.LockBits(new Rectangle(0, 0, 2, 2), ImageLockMode.ReadOnly, PixelFormat.Format8bppIndexed);
            try {
                using var unmanaged = new Accord.Imaging.UnmanagedImage(bitmapData);
                using var managed = unmanaged.ToManagedImage();

                Assert.That(managed.Width, Is.EqualTo(2));
                Assert.That(managed.Height, Is.EqualTo(2));
                Assert.That((int)managed.GetPixel(0, 0).R, Is.EqualTo(10));
                Assert.That((int)managed.GetPixel(1, 1).R, Is.EqualTo(40));
            } finally {
                source.UnlockBits(bitmapData);
            }
        }
    }
}
