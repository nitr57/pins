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
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace NINA.Test.SystemWindowsCompat {

    [TestFixture]
    public class BitmapSourceTest {

        private static BitmapSource CreateGray8(int width, int height, byte[] pixels) {
            return BitmapSource.Create(width, height, 96, 96, PixelFormats.Gray8, null, pixels, width);
        }

        private static BitmapSource CreateGray16(int width, int height, ushort[] pixels) {
            return BitmapSource.Create(width, height, 96, 96, PixelFormats.Gray16, null, pixels, width * 2);
        }

        [Test]
        public void Create_Gray8_ReportsDimensionsAndFormat() {
            using var source = CreateGray8(4, 2, new byte[] { 0, 1, 2, 3, 4, 5, 6, 7 });

            Assert.That(source.PixelWidth, Is.EqualTo(4));
            Assert.That(source.PixelHeight, Is.EqualTo(2));
            Assert.That(source.Format == PixelFormats.Gray8, Is.True);
        }

        [Test]
        public void Create_Gray8_CopyPixels_RoundTrips() {
            var input = new byte[] { 10, 20, 30, 40, 50, 60, 70, 80 };
            using var source = CreateGray8(4, 2, input);

            var output = new byte[input.Length];
            source.CopyPixels(output, 4, 0);

            Assert.That(output, Is.EqualTo(input));
        }

        [Test]
        public void Create_Gray16_CopyPixels_RoundTrips() {
            var input = new ushort[] { 0, 1000, 40000, 65535 };
            using var source = CreateGray16(2, 2, input);

            Assert.That(source.Format == PixelFormats.Gray16, Is.True);

            var output = new ushort[input.Length];
            source.CopyPixels(output, 4, 0);

            Assert.That(output, Is.EqualTo(input));
        }

        [Test]
        public void Create_Bgr24_CopyPixels_RoundTrips() {
            // 2x1 image: pixel0 = (B=1,G=2,R=3), pixel1 = (B=4,G=5,R=6)
            var input = new byte[] { 1, 2, 3, 4, 5, 6 };
            using var source = BitmapSource.Create(2, 1, 96, 96, PixelFormats.Bgr24, null, input, 6);

            Assert.That(source.Format == PixelFormats.Bgr24, Is.True);

            var output = new byte[input.Length];
            source.CopyPixels(output, 6, 0);

            Assert.That(output, Is.EqualTo(input));
        }

        [Test]
        public void CopyPixels_IntPtrOverload_CopiesFullImage() {
            var input = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 };
            using var source = CreateGray8(4, 3, input);

            var buffer = Marshal.AllocHGlobal(input.Length);
            try {
                source.CopyPixels(Int32Rect.Empty, buffer, input.Length, 4);
                var output = new byte[input.Length];
                Marshal.Copy(buffer, output, 0, input.Length);

                Assert.That(output, Is.EqualTo(input));
            } finally {
                Marshal.FreeHGlobal(buffer);
            }
        }

        [Test]
        public void CopyPixels_TooSmallDestination_Throws() {
            using var source = CreateGray8(4, 2, new byte[8]);

            Assert.Throws<ArgumentException>(() => source.CopyPixels(new byte[4], 4, 0));
        }

        // CopyPixels(ushort[]) copies straight into the caller's array (no temporary byte[]);
        // offset counts elements, not bytes, and data before it must stay untouched.
        [Test]
        public void CopyPixels_Gray16_WithOffset_WritesAtElementOffset() {
            var input = new ushort[] { 1, 2, 60000, 65535 };
            using var source = CreateGray16(2, 2, input);

            var output = new ushort[] { 7, 7, 0, 0, 0, 0 };
            source.CopyPixels(output, 4, 2);

            Assert.That(output, Is.EqualTo(new ushort[] { 7, 7, 1, 2, 60000, 65535 }));
        }

        [Test]
        public void CopyPixels_Gray16_NonContinuousMat_CopiesCorrectly() {
            using var parent = new OpenCvSharp.Mat(3, 4, OpenCvSharp.MatType.CV_16UC1);
            for (int y = 0; y < 3; y++) {
                for (int x = 0; x < 4; x++) {
                    parent.Set(y, x, (ushort)(y * 1000 + x));
                }
            }

            // Owned by `source` below, see CopyPixels_NonContinuousMat_RowByRowFallback_CopiesCorrectly.
            var roi = new OpenCvSharp.Mat(parent, new OpenCvSharp.Rect(1, 1, 2, 2));
            Assert.That(roi.IsContinuous(), Is.False, "test setup must produce a non-continuous ROI");

            using var source = new BitmapSource(roi);
            var output = new ushort[4];
            source.CopyPixels(output, 4, 0);

            Assert.That(output, Is.EqualTo(new ushort[] { 1001, 1002, 2001, 2002 }));
        }

        [Test]
        public void Create_Gray16_IsIndependentOfInputArray() {
            var input = new ushort[] { 100, 200, 300, 400 };
            using var source = CreateGray16(2, 2, input);

            input[0] = 9999;

            var output = new ushort[4];
            source.CopyPixels(output, 4, 0);
            Assert.That(output, Is.EqualTo(new ushort[] { 100, 200, 300, 400 }));
        }

        // REVIEW.md F8: BitmapSource(Mat) takes ownership of the Mat directly without cloning
        // (unlike CroppedBitmap/BitmapSource(Mat,Rectangle), which always clone their ROI). A raw,
        // un-cloned OpenCvSharp ROI view is non-continuous - its rows have padding between them
        // equal to the parent's extra width - so CopyPixels must not assume one flat memcpy.
        [Test]
        public void CopyPixels_NonContinuousMat_RowByRowFallback_CopiesCorrectly() {
            using var parent = new OpenCvSharp.Mat(4, 6, OpenCvSharp.MatType.CV_8UC1);
            for (int y = 0; y < 4; y++) {
                for (int x = 0; x < 6; x++) {
                    parent.Set(y, x, (byte)(y * 10 + x));
                }
            }

            // A 3x2 ROI starting at (1,1) out of a 6-wide parent is never continuous.
            // Not disposed separately: BitmapSource(Mat) takes ownership without cloning, so
            // disposing `source` below disposes this same Mat (see the ownership convention above).
            var roi = new OpenCvSharp.Mat(parent, new OpenCvSharp.Rect(1, 1, 3, 2));
            Assert.That(roi.IsContinuous(), Is.False, "test setup must produce a non-continuous ROI");

            using var source = new BitmapSource(roi);
            var output = new byte[6];
            source.CopyPixels(output, 3, 0);

            Assert.That(output, Is.EqualTo(new byte[] { 11, 12, 13, 21, 22, 23 }));
        }

        [Test]
        public void Create_FromIntPtr_HonorsStride() {
            // Source buffer with stride 6 for a 4-wide image (2 padding bytes per row)
            var padded = new byte[] {
                1, 2, 3, 4, 99, 99,
                5, 6, 7, 8, 99, 99
            };
            var handle = GCHandle.Alloc(padded, GCHandleType.Pinned);
            try {
                using var source = BitmapSource.Create(4, 2, 96, 96, PixelFormats.Gray8, null,
                    handle.AddrOfPinnedObject(), padded.Length, 6);

                var output = new byte[8];
                source.CopyPixels(output, 4, 0);

                Assert.That(output, Is.EqualTo(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }));
            } finally {
                handle.Free();
            }
        }

        [Test]
        public void ImplicitMatConversion_ReturnsIndependentClone() {
            var input = new byte[] { 10, 20, 30, 40 };
            using var source = CreateGray8(2, 2, input);

            OpenCvSharp.Mat clone = source;
            using (clone) {
                clone.Set<byte>(0, 0, 99);

                var output = new byte[4];
                source.CopyPixels(output, 2, 0);

                // Mutating the clone must not affect the source
                Assert.That(output[0], Is.EqualTo(10));
            }
        }

        [Test]
        public void WriteableBitmap_CopiesSource_AndSurvivesSourceDisposal() {
            var input = new byte[] { 1, 2, 3, 4 };
            var source = CreateGray8(2, 2, input);
            using var copy = new WriteableBitmap(source);

            source.Dispose();

            Assert.That(copy.PixelWidth, Is.EqualTo(2));
            Assert.That(copy.PixelHeight, Is.EqualTo(2));

            var output = new byte[4];
            copy.CopyPixels(output, 2, 0);
            Assert.That(output, Is.EqualTo(input));
        }

        // These constructors keep the empty Mat base() created instead of replacing it, so it
        // must still be alive and usable.
        [Test]
        public void EmptySourceConstructors_ProduceUsableEmptyBitmaps() {
            using var fromNull = new WriteableBitmap((BitmapSource)null);
            using var fromEmpty = new WriteableBitmap(new BitmapSource());
            using var cropped = new CroppedBitmap(null, new Int32Rect(0, 0, 2, 2));

            Assert.That(fromNull.PixelWidth, Is.EqualTo(0));
            Assert.That(fromEmpty.PixelWidth, Is.EqualTo(0));
            Assert.That(cropped.PixelWidth, Is.EqualTo(0));
        }

        [Test]
        public void WriteableBitmap_SizeConstructor_CreatesRequestedDimensions() {
            using var bitmap = new WriteableBitmap(7, 5, 96, 96, PixelFormats.Gray16, null);

            Assert.That(bitmap.PixelWidth, Is.EqualTo(7));
            Assert.That(bitmap.PixelHeight, Is.EqualTo(5));
            Assert.That(bitmap.Format == PixelFormats.Gray16, Is.True);
        }

        [Test]
        public void BitmapImage_SetSource_DecodesPngStream() {
            // Encode a known 8-bit gray image to PNG, then decode via BitmapImage
            var input = new byte[] { 0, 64, 128, 255 };
            using var source = CreateGray8(2, 2, input);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            using var stream = new MemoryStream();
            encoder.Save(stream);

            var image = new BitmapImage();
            image.SetSource(stream);
            using (image) {
                Assert.That(image.PixelWidth, Is.EqualTo(2));
                Assert.That(image.PixelHeight, Is.EqualTo(2));

                var output = new byte[4];
                image.CopyPixels(output, 2, 0);
                Assert.That(output, Is.EqualTo(input));
            }
        }

        [Test]
        public void BitmapFrame_Create_IsIndependentOfSource() {
            var input = new byte[] { 5, 6, 7, 8 };
            var source = CreateGray8(2, 2, input);
            var frame = BitmapFrame.Create(source);

            source.Dispose();

            using (frame) {
                var output = new byte[4];
                frame.CopyPixels(output, 2, 0);
                Assert.That(output, Is.EqualTo(input));
            }
        }

        // A frozen source shares its pixel buffer with the frame instead of copying it. OpenCV's
        // reference count must keep those pixels alive after the source is disposed.
        [Test]
        public void BitmapFrame_Create_FromFrozenSource_SurvivesSourceDisposal() {
            var input = new byte[] { 5, 6, 7, 8 };
            var source = CreateGray8(2, 2, input);
            source.Freeze();
            var frame = BitmapFrame.Create(source);

            source.Dispose();

            using (frame) {
                var output = new byte[4];
                frame.CopyPixels(output, 2, 0);
                Assert.That(output, Is.EqualTo(input));

                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(frame);
                using var stream = new MemoryStream();
                encoder.Save(stream);
                Assert.That(stream.Length, Is.GreaterThan(0));
            }
        }

        // Freeze does not stop BackBuffer writes here, so a frozen WriteableBitmap is never shared.
        [Test]
        public void BitmapFrame_Create_FromFrozenWriteableBitmap_IgnoresLaterWrites() {
            using var source = new WriteableBitmap(CreateGray8(2, 2, new byte[] { 5, 6, 7, 8 }));
            source.Freeze();
            using var frame = BitmapFrame.Create(source);

            Marshal.WriteByte(source.BackBuffer, 0, 99);

            var output = new byte[4];
            frame.CopyPixels(output, 2, 0);
            Assert.That(output, Is.EqualTo(new byte[] { 5, 6, 7, 8 }));
        }

        // A Mat handed to the public constructor may wrap caller-owned memory that OpenCV does not
        // reference-count, so it is never shared either.
        [Test]
        public void BitmapFrame_Create_FromFrozenForeignBuffer_CopiesPixels() {
            var buffer = new byte[] { 5, 6, 7, 8 };
            var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            BitmapFrame frame;
            try {
                var foreign = OpenCvSharp.Mat.FromPixelData(2, 2, OpenCvSharp.MatType.CV_8UC1, handle.AddrOfPinnedObject(), 2);
                using var source = new BitmapSource(foreign);
                source.Freeze();
                frame = BitmapFrame.Create(source);
                buffer[0] = 99;
            } finally {
                handle.Free();
            }

            using (frame) {
                var output = new byte[4];
                frame.CopyPixels(output, 2, 0);
                Assert.That(output, Is.EqualTo(new byte[] { 5, 6, 7, 8 }));
            }
        }

        [Test]
        public void BitmapImage_UriConstructor_LoadsFile() {
            var path = Path.Combine(Path.GetTempPath(), $"pins_compat_test_{Guid.NewGuid():N}.png");
            try {
                var input = new byte[] { 0, 64, 128, 255 };
                using (var source = CreateGray8(2, 2, input)) {
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(source));
                    using var fileStream = File.Create(path);
                    encoder.Save(fileStream);
                }

                using var image = new BitmapImage(new Uri(path));
                Assert.That(image.PixelWidth, Is.EqualTo(2));
                Assert.That(image.PixelHeight, Is.EqualTo(2));
            } finally {
                File.Delete(path);
            }
        }
    }
}
