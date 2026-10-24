#region "copyright"

/*
    Copyright © 2026 Nico Trost <nico.trost57@gmail.com> and the PI.N.S. contributors

    This file is part of PI 'N' Stars.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Moq;
using NINA.Equipment.Equipment.MyCamera;
using NINA.Image.FileFormat.FITS;
using NINA.INDI;
using NINA.INDI.Enums;
using NINA.INDI.Interfaces;
using NINA.Profile.Interfaces;
using NUnit.Framework;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Test.Equipment.Camera {

    /// <summary>
    /// An INDI camera's frame arrives as a FITS BLOB, optionally zlib-compressed (.fits.z), and is decoded in memory.
    /// </summary>
    [TestFixture]
    public class IndiCameraDownloadTest {
        private const int Width = 7;
        private const int Height = 5;

        private static ushort[] Pixels() {
            return Enumerable.Range(0, Width * Height).Select(i => (ushort)(i * 1000 + 3)).ToArray();
        }

        private static byte[] Fits(ushort[] pixels) {
            using var ms = new MemoryStream();
            new FITS(pixels, Width, Height).Write(ms);
            return ms.ToArray();
        }

        private static byte[] Zlib(byte[] data) {
            using var ms = new MemoryStream();
            using (var zs = new ZLibStream(ms, CompressionLevel.Optimal)) {
                zs.Write(data);
            }
            return ms.ToArray();
        }

        [Test]
        public async Task FitsBlob_IsDecoded() {
            var pixels = Pixels();
            var camera = new CameraWithBlob(Fits(pixels), ".fits");

            var image = await (await camera.DownloadExposure(CancellationToken.None)).ToImageData();

            Assert.That(image.Properties.Width, Is.EqualTo(Width));
            Assert.That(image.Properties.Height, Is.EqualTo(Height));
            Assert.That(image.Data.FlatArray, Is.EqualTo(pixels));
        }

        [Test]
        public async Task CompressedFitsBlob_IsDecompressedAndDecoded() {
            var pixels = Pixels();
            var camera = new CameraWithBlob(Zlib(Fits(pixels)), ".fits.z");

            var image = await (await camera.DownloadExposure(CancellationToken.None)).ToImageData();

            Assert.That(image.Properties.Width, Is.EqualTo(Width));
            Assert.That(image.Properties.Height, Is.EqualTo(Height));
            Assert.That(image.Data.FlatArray, Is.EqualTo(pixels));
        }

        [Test]
        public async Task NoBlob_ReturnsNull() {
            var camera = new CameraWithBlob([], ".fits");

            Assert.That(await camera.DownloadExposure(CancellationToken.None), Is.Null);
        }

        /// <summary>An INDI camera whose device holds a received BLOB, without a server.</summary>
        private sealed class CameraWithBlob : IndiCamera {

            public CameraWithBlob(byte[] blob, string format)
                : this(new ImageDataFactoryTestUtility(), blob, format) {
            }

            private CameraWithBlob(ImageDataFactoryTestUtility factories, byte[] blob, string format)
                : base(new INDIDeviceInfo { Id = "CCD Simulator", Name = "CCD Simulator", Interface = DeviceInterface.CCD_INTERFACE },
                       Mock.Of<IProfileService>(), factories.ExposureDataFactory, factories.ImageDataFactory) {
                var indiCamera = new Mock<IINDICamera>();
                indiCamera.Setup(c => c.GetLastBlobData()).Returns(blob);
                indiCamera.Setup(c => c.GetLastBlobFormat()).Returns(format);
                device = indiCamera.Object;
            }
        }
    }
}
