#region "copyright"

/*
    Copyright © 2026 Nico Trost <nico.trost57@gmail.com> and the PI.N.S. contributors

    This file is part of PI 'N' Stars.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Image.FileFormat.FITS;

namespace NINA.Test.Image.FileFormat {

    [TestFixture]
    public class CFitsioRoundTripTest {
        private string path = null!;

        [SetUp]
        public void SetUp() {
            path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".fits");
        }

        [TearDown]
        public void TearDown() {
            if (File.Exists(path)) {
                File.Delete(path);
            }
        }

        /// <summary>
        /// The image size goes to ffcrim as C long. Passed as int on Linux (LP64), width and height
        /// arrive as one 64-bit value and the file gets NAXIS1 = width + (height &lt;&lt; 32).
        /// A compressed file is unpacked by the reader into a file it creates with ffdkinit.
        /// </summary>
        [TestCase(CfitsioNative.COMPRESSION.NOCOMPRESS)]
        [TestCase(CfitsioNative.COMPRESSION.RICE_1)]
        public void WriteAndRead_KeepsSizeAndPixels(CfitsioNative.COMPRESSION compression) {
            ushort[] pixels = [0, 1, 2, 1000, 32768, ushort.MaxValue];

            var fits = new CFitsioFITS(path, pixels, width: 3, height: 2, compression);
            fits.Close();

            using var reader = new CFitsioFITSReader(path);

            Assert.That(reader.Width, Is.EqualTo(3));
            Assert.That(reader.Height, Is.EqualTo(2));
            Assert.That(reader.ReadAllPixels<ushort>(), Is.EqualTo(pixels));
        }

        /// <summary>
        /// FITS.Load unpacks a compressed image into a temporary file. A failed decompression used to leave
        /// that file and its handle behind, and a failure while closing freed the source handle and buffer
        /// twice (in the finally), which glibc answers by aborting the process.
        /// </summary>
        [Test]
        [NonParallelizable]
        public void Load_FailsCleanly_AndRemovesItsTemporaryFile_WhenTheCompressedDataIsCut() {
            var pixels = Enumerable.Range(0, 64 * 64).Select(i => (ushort)(i * 7919 % 65536)).ToArray();
            var fits = new CFitsioFITS(path, pixels, width: 64, height: 64, CfitsioNative.COMPRESSION.RICE_1);
            fits.Close();

            var bytes = File.ReadAllBytes(path);
            Assert.That(bytes.Length % 2880, Is.Zero);
            // Keep the headers, drop the last data block with the compressed tiles
            var cut = bytes[..(bytes.Length - 2880)];

            var tempBefore = Directory.GetFiles(Path.GetTempPath(), "*.fits").ToHashSet();
            var factory = new ImageDataFactoryTestUtility().ImageDataFactory;

            Assert.That(
                async () => await FITS.Load(cut, isBayered: false, factory, CancellationToken.None),
                Throws.TypeOf<CfitsioNative.cfitsioException>().With.Property(nameof(CfitsioNative.cfitsioException.Op)).EqualTo("fits_img_decompress"));
            Assert.That(Directory.GetFiles(Path.GetTempPath(), "*.fits").Where(f => !tempBefore.Contains(f)), Is.Empty, "the temporary decompressed file is deleted");
        }
    }
}
