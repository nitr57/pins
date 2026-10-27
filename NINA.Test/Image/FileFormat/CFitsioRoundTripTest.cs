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
    }
}
