#region "copyright"

/*
    Copyright © 2026 Nico Trost <nico.trost57@gmail.com> and the PI.N.S. contributors

    This file is part of PI 'N' Stars.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.INDI.Enums;
using NINA.INDI.Protocol;
using NUnit.Framework;
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml.Linq;

namespace NINA.Test.INDI {

    /// <summary>
    /// Decoding a setBLOBVector (DecodeBlobs) is split from applying it (ApplyBlobUpdate) so INDIClient can decode
    /// outside its lock; together they must behave exactly like the single UpdateBlobProperty did.
    /// </summary>
    [TestFixture]
    public class INDIProtocolParserBlobTest {

        [Test]
        public void UpdateBlobProperty_InflatesZlibPayloads() {
            var image = Enumerable.Range(0, 5000).Select(i => (byte)(i % 7)).ToArray();
            var compressed = new MemoryStream();
            using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true)) {
                zlib.Write(image);
            }
            var prop = new INDIBlobProperty { Name = "CCD1" };

            INDIProtocolParser.UpdateBlobProperty(prop, SetBlob(Convert.ToBase64String(compressed.ToArray()), ".fits.z", "Ok"));

            Assert.That(prop.State, Is.EqualTo(PropertyState.Ok));
            Assert.That(prop.Blobs, Has.Count.EqualTo(1));
            Assert.That(prop.Blobs[0].Format, Is.EqualTo(".fits"));
            Assert.That(prop.Blobs[0].Data, Is.EqualTo(image));
        }

        [Test]
        public void UpdateBlobProperty_InvalidPayload_EmptiesTheData() {
            var prop = new INDIBlobProperty { Name = "CCD1" };
            prop.Blobs.Add(new INDIBlob { Name = "CCD1", Data = [1, 2, 3] });

            INDIProtocolParser.UpdateBlobProperty(prop, SetBlob("not base64!", ".fits", "Ok"));

            Assert.That(prop.Blobs[0].Data, Is.Empty);
            Assert.That(prop.Blobs[0].Format, Is.EqualTo(".fits"));
        }

        [Test]
        public void UpdateBlobProperty_EmptyPayload_KeepsTheData() {
            var prop = new INDIBlobProperty { Name = "CCD1" };
            prop.Blobs.Add(new INDIBlob { Name = "CCD1", Format = ".fits", Data = [1, 2, 3] });

            INDIProtocolParser.UpdateBlobProperty(prop, SetBlob("", ".xisf", "Busy"));

            Assert.That(prop.State, Is.EqualTo(PropertyState.Busy));
            Assert.That(prop.Blobs[0].Data, Is.EqualTo(new byte[] { 1, 2, 3 }));
            Assert.That(prop.Blobs[0].Format, Is.EqualTo(".xisf"));
        }

        private static XElement SetBlob(string payload, string format, string state) {
            return XElement.Parse(
                $"<setBLOBVector device=\"Cam\" name=\"CCD1\" state=\"{state}\"><oneBLOB name=\"CCD1\" format=\"{format}\">{payload}</oneBLOB></setBLOBVector>");
        }
    }
}
