#region "copyright"

/*
    Copyright © 2026 Nico Trost <nico.trost57@gmail.com> and the PI.N.S. contributors

    This file is part of PI 'N' Stars.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.INDI;
using NINA.INDI.Devices;
using NINA.INDI.Enums;
using NUnit.Framework;
using System;
using System.Threading.Tasks;

namespace NINA.Test.INDI {

    /// <summary>
    /// The offset passed to StartExposure (per sequence item / image type) reaches the driver
    /// through whichever vector the camera publishes it in: the standard CCD_OFFSET/OFFSET, or
    /// the CCD_CONTROLS/Offset element of toupbase and older indi_asi_ccd. Runs an INDICamera
    /// against a fake indiserver.
    /// </summary>
    [TestFixture]
    [NonParallelizable]
    public class INDICameraStartExposureTest {
        private const string Camera = "CCD Simulator";
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

        private FakeIndiServer server = null!;
        private INDIClient client = null!;
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Structure", "NUnit1032", Justification = "Borrowed singleton, restored in TearDown")]
        private INDIClient? previousInstance;
        private FakeIndiServer.Connection main = null!;

        [SetUp]
        public async Task SetUp() {
            server = new FakeIndiServer();
            client = new INDIClient(server.Port, startServer: false);
            // INDIDevice talks to INDIClient.Instance
            previousInstance = INDIClient.SetInstanceForTests(client);
            Assert.That(await client.Connect(), Is.True);
            main = await server.NextConnectionAsync(Timeout);
        }

        [TearDown]
        public void TearDown() {
            client.Dispose();
            // Keep the disposed test client rather than restoring null, as in INDITelescopeParkTest:
            // a real client would kill the machine's indiserver.
            INDIClient.SetInstanceForTests(previousInstance ?? client);
            server.Dispose();
        }

        [Test]
        public async Task StartExposure_WritesTheOffset_IntoCcdControls_WhenTheDriverHasNoCcdOffset() {
            var camera = await RegisterCameraAsync(
                $"<defNumberVector device=\"{Camera}\" name=\"CCD_CONTROLS\" label=\"Controls\" group=\"Controls\" state=\"Idle\" perm=\"rw\" timeout=\"60\">"
                + "<defNumber name=\"Gain\" label=\"Gain\" format=\"%.f\" min=\"0\" max=\"5000\" step=\"1\">100</defNumber>"
                + "<defNumber name=\"Offset\" label=\"Offset\" format=\"%.f\" min=\"0\" max=\"255\" step=\"1\">10</defNumber></defNumberVector>\n",
                "CCD_CONTROLS");

            camera.StartExposure(1, 1, 1, false, 0, 0, 0, 0, gain: 100, offset: 30);

            await main.WaitUntilAsync(
                r => r.Contains("name=\"CCD_CONTROLS\"", StringComparison.Ordinal) && r.Contains("<oneNumber name=\"Offset\">30</oneNumber>", StringComparison.Ordinal),
                "the offset is sent in CCD_CONTROLS",
                Timeout);
        }

        [Test]
        public async Task StartExposure_WritesTheOffset_IntoCcdOffset_WhenTheDriverHasIt() {
            var camera = await RegisterCameraAsync(
                $"<defNumberVector device=\"{Camera}\" name=\"CCD_OFFSET\" label=\"Offset\" group=\"Main Control\" state=\"Idle\" perm=\"rw\" timeout=\"60\">"
                + "<defNumber name=\"OFFSET\" label=\"Offset\" format=\"%.f\" min=\"0\" max=\"255\" step=\"1\">10</defNumber></defNumberVector>\n",
                "CCD_OFFSET");

            camera.StartExposure(1, 1, 1, false, 0, 0, 0, 0, gain: -1, offset: 30);

            await main.WaitUntilAsync(
                r => r.Contains("name=\"CCD_OFFSET\"", StringComparison.Ordinal) && r.Contains("<oneNumber name=\"OFFSET\">30</oneNumber>", StringComparison.Ordinal),
                "the offset is sent in CCD_OFFSET",
                Timeout);
        }

        private async Task<INDICamera> RegisterCameraAsync(string offsetVector, string offsetProperty) {
            var create = Task.Run(() => new INDICamera(new INDIDeviceInfo { Id = Camera, Name = Camera, Interface = DeviceInterface.CCD_INTERFACE }));
            // The new device asks for its properties and waits for CONNECTION.
            await main.WaitForAsync($"<getProperties version=\"1.7\" device=\"{Camera}\" />", Timeout);
            main.Send(
                $"<defSwitchVector device=\"{Camera}\" name=\"CONNECTION\" label=\"Connection\" group=\"Main\" state=\"Idle\" perm=\"rw\" rule=\"OneOfMany\" timeout=\"60\">"
                + "<defSwitch name=\"CONNECT\" label=\"Connect\">On</defSwitch><defSwitch name=\"DISCONNECT\" label=\"Disconnect\">Off</defSwitch></defSwitchVector>\n"
                + $"<defNumberVector device=\"{Camera}\" name=\"CCD_EXPOSURE\" label=\"Expose\" group=\"Main Control\" state=\"Idle\" perm=\"rw\" timeout=\"60\">"
                + "<defNumber name=\"CCD_EXPOSURE_VALUE\" label=\"Duration (s)\" format=\"%5.2f\" min=\"0\" max=\"3600\" step=\"1\">1</defNumber></defNumberVector>\n"
                + offsetVector);
            Assert.That(await Task.WhenAny(create, Task.Delay(Timeout)), Is.SameAs(create), "the camera registers");
            var camera = await create;

            var deadline = DateTime.UtcNow + Timeout;
            while (camera.GetNumberProperty(offsetProperty) == null || camera.GetNumberProperty("CCD_EXPOSURE") == null) {
                if (DateTime.UtcNow > deadline) {
                    Assert.Fail($"Timed out waiting until {offsetProperty} is defined");
                }

                await Task.Delay(10);
            }

            return camera;
        }
    }
}
