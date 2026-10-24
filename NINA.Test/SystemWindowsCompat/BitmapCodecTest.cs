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
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace NINA.Test.SystemWindowsCompat {

    [TestFixture]
    public class BitmapCodecTest {

        private static BitmapSource CreateGray8(int width, int height, byte[] pixels) {
            return BitmapSource.Create(width, height, 96, 96, PixelFormats.Gray8, null, pixels, width);
        }

        private static BitmapSource CreateGray16(int width, int height, ushort[] pixels) {
            return BitmapSource.Create(width, height, 96, 96, PixelFormats.Gray16, null, pixels, width * 2);
        }

        private static MemoryStream Encode(BitmapEncoder encoder, BitmapSource source) {
            encoder.Frames.Add(BitmapFrame.Create(source));
            var stream = new MemoryStream();
            encoder.Save(stream);
            stream.Position = 0;
            return stream;
        }

        [Test]
        public void Png_Gray8_RoundTripsLosslessly() {
            var input = new byte[] { 0, 64, 128, 255 };
            using var source = CreateGray8(2, 2, input);
            using var stream = Encode(new PngBitmapEncoder(), source);

            var decoded = new BitmapImage();
            decoded.SetSource(stream);
            using (decoded) {
                Assert.That(decoded.Format == PixelFormats.Gray8, Is.True);
                var output = new byte[4];
                decoded.CopyPixels(output, 2, 0);
                Assert.That(output, Is.EqualTo(input));
            }
        }

        // The encoders read the frame without copying it first, so saving must leave the frame
        // (and the bitmap it was created from) intact and reusable.
        [Test]
        public void Encoders_LeaveFrameUsableAfterSave() {
            var pixels = new byte[] { 0, 64, 128, 255 };
            using var source = CreateGray8(2, 2, pixels);
            var frame = BitmapFrame.Create(source);

            foreach (var encoder in new BitmapEncoder[] { new PngBitmapEncoder(), new JpegBitmapEncoder(), new TiffBitmapEncoder() }) {
                encoder.Frames.Add(frame);
                using var stream = new MemoryStream();
                encoder.Save(stream);
                Assert.That(stream.Length, Is.GreaterThan(0));
            }

            using (frame) {
                var output = new byte[4];
                frame.CopyPixels(output, 2, 0);
                Assert.That(output, Is.EqualTo(pixels));
            }
        }

        [Test]
        public void Png_Gray16_RoundTripsLosslessly() {
            var input = new ushort[] { 0, 1000, 40000, 65535 };
            using var source = CreateGray16(2, 2, input);
            using var stream = Encode(new PngBitmapEncoder(), source);

            var decoded = new BitmapImage();
            decoded.SetSource(stream);
            using (decoded) {
                Assert.That(decoded.Format == PixelFormats.Gray16, Is.True,
                    "16-bit PNG must decode back as Gray16, not be silently truncated to 8 bit");
                var output = new ushort[4];
                decoded.CopyPixels(output, 4, 0);
                Assert.That(output, Is.EqualTo(input));
            }
        }

        [Test]
        public void Png_EmptyFrames_Throws() {
            using var stream = new MemoryStream();
            Assert.Throws<InvalidOperationException>(() => new PngBitmapEncoder().Save(stream));
        }

        [Test]
        public void Jpeg_Gray8_RoundTripsApproximately() {
            // Uniform image — JPEG is lossy, so allow a small tolerance
            var input = new byte[64];
            Array.Fill(input, (byte)128);
            using var source = CreateGray8(8, 8, input);
            using var stream = Encode(new JpegBitmapEncoder { QualityLevel = 95 }, source);

            var decoded = new BitmapImage();
            decoded.SetSource(stream);
            using (decoded) {
                Assert.That(decoded.PixelWidth, Is.EqualTo(8));
                var output = new byte[64];
                decoded.CopyPixels(output, 8, 0);
                foreach (var value in output) {
                    Assert.That((int)value, Is.EqualTo(128).Within(4));
                }
            }
        }

        [Test]
        public void Jpeg_Gray16Input_IsConvertedTo8Bit() {
            // 25700 / 256 ≈ 100 after the encoder's 16→8 down-conversion
            var input = new ushort[64];
            Array.Fill(input, (ushort)25700);
            using var source = CreateGray16(8, 8, input);
            using var stream = Encode(new JpegBitmapEncoder { QualityLevel = 95 }, source);

            var decoded = new BitmapImage();
            decoded.SetSource(stream);
            using (decoded) {
                Assert.That(decoded.Format == PixelFormats.Gray8, Is.True);
                var output = new byte[64];
                decoded.CopyPixels(output, 8, 0);
                Assert.That((int)output[0], Is.EqualTo(100).Within(4));
            }
        }

        // REVIEW.md F10: JpegBitmapDecoder(Stream) used a single unchecked stream.Read, which
        // truncates on a short read (network/chunked streams). This stream deliberately returns
        // at most one byte per call to force that condition.
        private sealed class OneByteAtATimeStream : MemoryStream {
            public OneByteAtATimeStream(byte[] buffer) : base(buffer) { }

            public override int Read(byte[] buffer, int offset, int count) =>
                base.Read(buffer, offset, count > 0 ? 1 : count);
        }

        [Test]
        public void JpegBitmapDecoder_StreamCtor_HandlesShortReads() {
            var input = new byte[64];
            Array.Fill(input, (byte)128);
            using var source = CreateGray8(8, 8, input);
            using var encodedStream = Encode(new JpegBitmapEncoder { QualityLevel = 95 }, source);
            var encodedBytes = encodedStream.ToArray();

            using var slowStream = new OneByteAtATimeStream(encodedBytes);
            var decoder = new JpegBitmapDecoder(slowStream, BitmapCreateOptions.None, BitmapCacheOption.Default);

            Assert.That(decoder.Frames, Has.Count.EqualTo(1), "a short-read stream must still decode a complete frame");
            using var frame = decoder.Frames[0];
            Assert.That(frame.PixelWidth, Is.EqualTo(8));
        }

        [Test]
        public void Tiff_Gray16_RoundTripsLosslessly() {
            var input = new ushort[] { 0, 1000, 40000, 65535 };
            using var source = CreateGray16(2, 2, input);
            using var stream = Encode(new TiffBitmapEncoder(), source);

            var decoder = new TiffBitmapDecoder(stream, BitmapCreateOptions.None, BitmapCacheOption.Default);

            Assert.That(decoder.Frames, Has.Count.EqualTo(1));
            using var frame = decoder.Frames[0];
            Assert.That(frame.PixelWidth, Is.EqualTo(2));
            Assert.That(frame.Format == PixelFormats.Gray16, Is.True);

            var output = new ushort[4];
            frame.CopyPixels(output, 4, 0);
            Assert.That(output, Is.EqualTo(input));
        }

        [Test]
        public void Tiff_Bgr24_RoundTripsLosslessly() {
            var input = new byte[] { 10, 20, 30, 40, 50, 60 };
            using var source = BitmapSource.Create(2, 1, 96, 96, PixelFormats.Bgr24, null, input, 6);
            using var stream = Encode(new TiffBitmapEncoder(), source);

            var decoder = new TiffBitmapDecoder(stream, BitmapCreateOptions.None, BitmapCacheOption.Default);

            using var frame = decoder.Frames[0];
            Assert.That(frame.Format == PixelFormats.Bgr24, Is.True);

            var output = new byte[6];
            frame.CopyPixels(output, 6, 0);
            Assert.That(output, Is.EqualTo(input));
        }
    }
}
