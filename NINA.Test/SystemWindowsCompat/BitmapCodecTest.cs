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

        // ----- failures throw as in WPF instead of giving an empty image or stream -----

        private static byte[] PngBytes() {
            using var source = CreateGray8(4, 4, new byte[16]);
            using var stream = Encode(new PngBitmapEncoder(), source);

            return stream.ToArray();
        }

        private static string TempFile(byte[] content) {
            string path = Path.Combine(Path.GetTempPath(), $"pins_codec_test_{Guid.NewGuid():N}.png");
            File.WriteAllBytes(path, content);

            return path;
        }

        [Test]
        public void BitmapImage_TruncatedStream_ThrowsFileFormatException() {
            byte[] png = PngBytes();
            using var truncated = new MemoryStream(png, 0, png.Length / 2);
            var image = new BitmapImage();

            image.BeginInit();
            image.StreamSource = truncated;

            Assert.That(() => image.EndInit(), Throws.InstanceOf<FileFormatException>());
        }

        [Test]
        public void BitmapImage_EmptyStream_ThrowsFileFormatException() {
            var image = new BitmapImage();

            Assert.That(() => image.SetSource(new MemoryStream()), Throws.InstanceOf<FileFormatException>());
        }

        [Test]
        public void BitmapImage_StreamThatCannotSeek_IsDecoded() {
            using var stream = new ForwardOnlyStream(PngBytes());
            var image = new BitmapImage();

            image.SetSource(stream);

            using (image) {
                Assert.That(image.PixelWidth, Is.EqualTo(4));
            }
        }

        [Test]
        public void JpegDecoder_ReadsFromTheStreamsPosition() {
            using var source = CreateGray8(4, 4, new byte[16]);
            using var jpeg = Encode(new JpegBitmapEncoder(), source);
            byte[] prefixed = [1, 2, 3, .. jpeg.ToArray()];

            using var stream = new MemoryStream(prefixed);
            stream.Position = 3;
            var fromMemory = new JpegBitmapDecoder(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);

            using var forwardOnly = new ForwardOnlyStream(prefixed);
            forwardOnly.ReadExactly(new byte[3]);
            var fromForwardOnly = new JpegBitmapDecoder(forwardOnly, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);

            Assert.That(fromMemory.Frames[0].PixelWidth, Is.EqualTo(4));
            Assert.That(fromForwardOnly.Frames[0].PixelWidth, Is.EqualTo(4));
        }

        [Test]
        public void BitmapImage_ZeroByteFile_ThrowsFileFormatException() {
            string path = TempFile([]);

            try {
                var image = new BitmapImage();
                image.BeginInit();
                image.UriSource = new Uri(path);

                Assert.That(() => image.EndInit(), Throws.InstanceOf<FileFormatException>());
            } finally {
                File.Delete(path);
            }
        }

        [Test]
        public void BitmapImage_MissingFile_ThrowsFileNotFoundException() {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(Path.Combine(Path.GetTempPath(), $"pins_missing_{Guid.NewGuid():N}.png"));

            Assert.That(() => image.EndInit(), Throws.InstanceOf<FileNotFoundException>());
        }

        [Test]
        public void BitmapImage_WebUri_IsNotSupported() {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri("https://example.invalid/tile.png");

            Assert.That(() => image.EndInit(), Throws.InstanceOf<NotSupportedException>());
        }

        [Test]
        public void Decoders_DataThatIsNoImage_ThrowFileFormatException() {
            string path = TempFile(System.Text.Encoding.ASCII.GetBytes("not an image"));

            try {
                var uri = new Uri(path);

                Assert.That(() => new PngBitmapDecoder(uri, BitmapCreateOptions.None, BitmapCacheOption.OnLoad), Throws.InstanceOf<FileFormatException>());
                Assert.That(() => new JpegBitmapDecoder(uri, BitmapCreateOptions.None, BitmapCacheOption.OnLoad), Throws.InstanceOf<FileFormatException>());
                Assert.That(() => new TiffBitmapDecoder(uri, BitmapCreateOptions.None, BitmapCacheOption.OnLoad), Throws.InstanceOf<FileFormatException>());
                Assert.That(() => new GifBitmapDecoder(uri, BitmapCreateOptions.None, BitmapCacheOption.OnLoad), Throws.InstanceOf<FileFormatException>());

                using var stream = new MemoryStream(File.ReadAllBytes(path));

                Assert.That(() => new JpegBitmapDecoder(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad), Throws.InstanceOf<FileFormatException>());
            } finally {
                File.Delete(path);
            }
        }

        [Test]
        public void Encoders_EmptyImage_ThrowInvalidOperationException() {
            using var empty = new BitmapSource(new OpenCvSharp.Mat());

            foreach (BitmapEncoder encoder in new BitmapEncoder[] { new PngBitmapEncoder(), new JpegBitmapEncoder(), new TiffBitmapEncoder() }) {
                encoder.Frames.Add(BitmapFrame.Create(empty));

                Assert.That(() => encoder.Save(new MemoryStream()), Throws.InstanceOf<InvalidOperationException>(), encoder.GetType().Name);
            }
        }

        /// <summary>A stream that can only be read forward and has no length, as a network response.</summary>
        private sealed class ForwardOnlyStream : Stream {

            private readonly MemoryStream inner;

            public ForwardOnlyStream(byte[] buffer) {
                inner = new MemoryStream(buffer);
            }

            public override bool CanRead => true;

            public override bool CanSeek => false;

            public override bool CanWrite => false;

            public override long Length => throw new NotSupportedException();

            public override long Position {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override int Read(byte[] buffer, int offset, int count) {
                // A network stream hands out what has arrived: at most a few bytes per read.
                return inner.Read(buffer, offset, Math.Min(count, 7));
            }

            public override void Flush() {
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            protected override void Dispose(bool disposing) {
                if (disposing) {
                    inner.Dispose();
                }

                base.Dispose(disposing);
            }
        }
    }
}
