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
using System.Linq;
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

        [Test]
        public async Task StartExposure_ResetsCcdFrameToTheFullSensor_AfterASubframe() {
            var camera = await RegisterCameraAsync(CcdInfo + CcdFrame(100, 200, 512, 512), "CCD_INFO", "CCD_FRAME");

            var start = Task.Run(() => camera.StartExposure(1, 1, 1, false, 0, 0, 0, 0, gain: -1, offset: -1));
            await WaitForFrameCommandAsync();
            main.Send(SetFrame(0, 0, 4144, 2822));
            await start.WaitAsync(Timeout);

            await main.WaitUntilAsync(
                r => r.Contains("name=\"CCD_EXPOSURE\"", StringComparison.Ordinal),
                "the exposure is started",
                Timeout);
            var received = main.Received;
            var frame = received.IndexOf("name=\"CCD_FRAME\"", StringComparison.Ordinal);
            Assert.That(frame, Is.GreaterThanOrEqualTo(0), "CCD_FRAME is sent");
            Assert.That(frame, Is.LessThan(received.IndexOf("name=\"CCD_EXPOSURE\"", StringComparison.Ordinal)), "CCD_FRAME is sent before CCD_EXPOSURE");
            var frameVector = received[frame..];
            Assert.That(frameVector, Does.Contain("<oneNumber name=\"X\">0</oneNumber>"));
            Assert.That(frameVector, Does.Contain("<oneNumber name=\"Y\">0</oneNumber>"));
            Assert.That(frameVector, Does.Contain("<oneNumber name=\"WIDTH\">4144</oneNumber>"));
            Assert.That(frameVector, Does.Contain("<oneNumber name=\"HEIGHT\">2822</oneNumber>"));
        }

        [Test]
        public async Task StartExposure_WaitsForTheDriversAnswerToCcdFrame_BeforeTheExposure() {
            var camera = await RegisterCameraAsync(CcdInfo + CcdFrame(100, 200, 512, 512), "CCD_INFO", "CCD_FRAME");

            var start = Task.Run(() => camera.StartExposure(1, 1, 1, false, 0, 0, 0, 0, gain: -1, offset: -1));
            await WaitForFrameCommandAsync();
            await Task.Delay(300);

            Assert.That(main.Received, Does.Not.Contain("name=\"CCD_EXPOSURE\""),
                "toupbase restarts its engine on CCD_FRAME and may refuse an exposure sent right behind it");

            main.Send(SetFrame(0, 0, 4144, 2822));
            await start.WaitAsync(Timeout);
            await WaitForExposureCommandAsync();
        }

        [Test]
        public async Task StartExposure_DoesNotResetAgain_WhenTheDriversFullFrameDiffersFromCcdInfo() {
            var camera = await RegisterCameraAsync(CcdInfo + CcdFrame(100, 200, 512, 512), "CCD_INFO", "CCD_FRAME");

            var start = Task.Run(() => camera.StartExposure(1, 1, 1, false, 0, 0, 0, 0, gain: -1, offset: -1));
            await WaitForFrameCommandAsync();
            // the driver trims a border off CCD_INFO's size
            main.Send(SetFrame(4, 3, 4136, 2816));
            await start.WaitAsync(Timeout);
            await WaitForExposureCommandAsync();

            camera.StartExposure(1, 1, 1, false, 0, 0, 0, 0, gain: -1, offset: -1);
            await main.WaitUntilAsync(
                r => r.Split("name=\"CCD_EXPOSURE\"").Length > 2,
                "the second exposure is started",
                Timeout);

            Assert.That(main.Received.Split("name=\"CCD_FRAME\"").Length - 1, Is.EqualTo(1),
                "the driver's own full frame is not reset before every frame");
        }

        [Test]
        public async Task StartExposure_ResetsAgain_AfterTheDriverRefusedTheReset() {
            var camera = await RegisterCameraAsync(CcdInfo + CcdFrame(100, 200, 512, 512), "CCD_INFO", "CCD_FRAME");

            var start = Task.Run(() => camera.StartExposure(1, 1, 1, false, 0, 0, 0, 0, gain: -1, offset: -1));
            await WaitForFrameCommandAsync();
            // a refusal comes back with the old subframe, which must not be taken as the driver's full frame
            main.Send(SetFrame(100, 200, 512, 512, "Alert"));
            await start.WaitAsync(Timeout);
            await WaitForExposureCommandAsync();

            start = Task.Run(() => camera.StartExposure(1, 1, 1, false, 0, 0, 0, 0, gain: -1, offset: -1));
            await main.WaitUntilAsync(
                r => r.Split("name=\"CCD_FRAME\"").Length > 2,
                "the reset is sent again for the second exposure",
                Timeout);
            main.Send(SetFrame(0, 0, 4144, 2822));
            await start.WaitAsync(Timeout);
        }

        [Test]
        public async Task StartExposure_WithAnEmptySubframe_ResetsToTheFullSensor() {
            var camera = await RegisterCameraAsync(CcdInfo + CcdFrame(100, 200, 512, 512), "CCD_INFO", "CCD_FRAME");

            var start = Task.Run(() => camera.StartExposure(1, 1, 1, true, 0, 0, 0, 0, gain: -1, offset: -1));
            await WaitForFrameCommandAsync();
            main.Send(SetFrame(0, 0, 4144, 2822));
            await start.WaitAsync(Timeout);

            Assert.That(main.Received, Does.Contain("<oneNumber name=\"WIDTH\">4144</oneNumber>"),
                "an autofocus crop of 0 must not keep the previous subframe");
        }

        [Test]
        public async Task StartExposure_DoesNotSendCcdFrame_WhenTheDriverIsAlreadyAtTheFullSensor() {
            var camera = await RegisterCameraAsync(CcdInfo + CcdFrame(0, 0, 4144, 2822), "CCD_INFO", "CCD_FRAME");

            camera.StartExposure(1, 1, 1, false, 0, 0, 0, 0, gain: -1, offset: -1);

            await main.WaitUntilAsync(
                r => r.Contains("name=\"CCD_EXPOSURE\"", StringComparison.Ordinal),
                "the exposure is started",
                Timeout);
            Assert.That(main.Received, Does.Not.Contain("name=\"CCD_FRAME\""), "a redundant full-frame reset crashes indi_toupcam_ccd");
        }

        [Test]
        public async Task AnExposureAlert_EndsTheWaitWithoutAnImage() {
            var camera = await RegisterCameraAsync("");

            camera.StartExposure(5, 1, 1, false, 0, 0, 0, 0, gain: -1, offset: -1);
            await WaitForExposureCommandAsync();
            main.Send(Exposure("Alert", "2026-11-01T20:00:01"));

            Assert.That(async () => await camera.WaitUntilExposureIsReady(System.Threading.CancellationToken.None).WaitAsync(Timeout), Throws.Nothing,
                "the frame fails at once instead of at the camera timeout");
            Assert.That(camera.GetLastBlobData(), Is.Null);
        }

        [Test]
        public async Task TheAlertOfThePreviousExposure_DoesNotEndTheNext() {
            var camera = await RegisterCameraInStateAsync("Alert", "");

            camera.StartExposure(5, 1, 1, false, 0, 0, 0, 0, gain: -1, offset: -1);
            await WaitForExposureCommandAsync();
            // The driver sends the failed exposure's Alert again, unchanged
            main.Send(Exposure("Alert", "2026-11-01T20:00:00"));
            var wait = camera.WaitUntilExposureIsReady(System.Threading.CancellationToken.None);

            Assert.That(async () => await wait.WaitAsync(TimeSpan.FromMilliseconds(500)), Throws.TypeOf<TimeoutException>(),
                "the new exposure keeps waiting for its image");

            main.Send(Exposure("Alert", "2026-11-01T20:00:01"));

            Assert.That(async () => await wait.WaitAsync(Timeout), Throws.Nothing, "its own Alert still ends it");
        }

        [Test]
        public async Task AnAlertAfterBusy_EndsTheExposure_InTheSameSecond() {
            var camera = await RegisterCameraInStateAsync("Alert", "");

            camera.StartExposure(5, 1, 1, false, 0, 0, 0, 0, gain: -1, offset: -1);
            await WaitForExposureCommandAsync();
            main.Send(Exposure("Busy", "2026-11-01T20:00:00"));
            main.Send(Exposure("Alert", "2026-11-01T20:00:00"));

            Assert.That(async () => await camera.WaitUntilExposureIsReady(System.Threading.CancellationToken.None).WaitAsync(Timeout), Throws.Nothing);
        }

        private static string Exposure(string state, string timestamp) =>
            $"<setNumberVector device=\"{Camera}\" name=\"CCD_EXPOSURE\" state=\"{state}\" timeout=\"60\" timestamp=\"{timestamp}\">"
            + "<oneNumber name=\"CCD_EXPOSURE_VALUE\">0</oneNumber></setNumberVector>\n";

        [Test]
        public async Task ALocalUpload_IsSwitchedToClientBeforeTheExposure() {
            var camera = await RegisterCameraAsync(UploadMode("UPLOAD_LOCAL"));
            await WaitForSwitchAsync(camera, "UPLOAD_MODE");

            camera.StartExposure(1, 1, 1, false, 0, 0, 0, 0, gain: -1, offset: -1);

            await WaitForExposureCommandAsync();
            var received = main.Received;
            var upload = received.IndexOf("name=\"UPLOAD_MODE\"", StringComparison.Ordinal);
            Assert.That(upload, Is.GreaterThanOrEqualTo(0), "UPLOAD_MODE is sent");
            Assert.That(upload, Is.LessThan(received.IndexOf("name=\"CCD_EXPOSURE\"", StringComparison.Ordinal)));
            Assert.That(received[upload..], Does.Contain("<oneSwitch name=\"UPLOAD_CLIENT\">On</oneSwitch>"));
        }

        [Test]
        public async Task ANativeTransferFormat_IsSwitchedToFitsBeforeTheExposure() {
            var camera = await RegisterCameraAsync(TransferFormat("FORMAT_NATIVE"));
            await WaitForSwitchAsync(camera, "CCD_TRANSFER_FORMAT");

            camera.StartExposure(1, 1, 1, false, 0, 0, 0, 0, gain: -1, offset: -1);

            await WaitForExposureCommandAsync();
            var received = main.Received;
            var format = received.IndexOf("name=\"CCD_TRANSFER_FORMAT\"", StringComparison.Ordinal);
            Assert.That(format, Is.GreaterThanOrEqualTo(0), "CCD_TRANSFER_FORMAT is sent");
            Assert.That(format, Is.LessThan(received.IndexOf("name=\"CCD_EXPOSURE\"", StringComparison.Ordinal)));
            Assert.That(received[format..], Does.Contain("<oneSwitch name=\"FORMAT_FITS\">On</oneSwitch>"));
        }

        [Test]
        public async Task AClientUploadInFits_SendsNothingExtra() {
            var camera = await RegisterCameraAsync(UploadMode("UPLOAD_CLIENT") + TransferFormat("FORMAT_FITS"));
            await WaitForSwitchAsync(camera, "UPLOAD_MODE");
            await WaitForSwitchAsync(camera, "CCD_TRANSFER_FORMAT");

            camera.StartExposure(1, 1, 1, false, 0, 0, 0, 0, gain: -1, offset: -1);

            await WaitForExposureCommandAsync();
            Assert.That(main.Received, Does.Not.Contain("name=\"UPLOAD_MODE\""));
            Assert.That(main.Received, Does.Not.Contain("name=\"CCD_TRANSFER_FORMAT\""));
        }

        private Task WaitForExposureCommandAsync() {
            return main.WaitUntilAsync(
                r => r.Contains("name=\"CCD_EXPOSURE\"", StringComparison.Ordinal),
                "the exposure is started",
                Timeout);
        }

        private static async Task WaitForSwitchAsync(INDICamera camera, string property) {
            var deadline = DateTime.UtcNow + Timeout;
            while (camera.GetSwitchProperty(property) == null) {
                if (DateTime.UtcNow > deadline) {
                    Assert.Fail($"Timed out waiting until {property} is defined");
                }

                await Task.Delay(10);
            }
        }

        private static string UploadMode(string on) =>
            $"<defSwitchVector device=\"{Camera}\" name=\"UPLOAD_MODE\" label=\"Upload\" group=\"Options\" state=\"Idle\" perm=\"rw\" rule=\"OneOfMany\" timeout=\"60\">"
            + string.Concat(new[] { "UPLOAD_CLIENT", "UPLOAD_LOCAL", "UPLOAD_BOTH" }.Select(e => $"<defSwitch name=\"{e}\" label=\"{e}\">{(e == on ? "On" : "Off")}</defSwitch>"))
            + "</defSwitchVector>\n";

        private static string TransferFormat(string on) =>
            $"<defSwitchVector device=\"{Camera}\" name=\"CCD_TRANSFER_FORMAT\" label=\"Encode\" group=\"Image Settings\" state=\"Idle\" perm=\"rw\" rule=\"OneOfMany\" timeout=\"60\">"
            + string.Concat(new[] { "FORMAT_FITS", "FORMAT_NATIVE", "FORMAT_XISF" }.Select(e => $"<defSwitch name=\"{e}\" label=\"{e}\">{(e == on ? "On" : "Off")}</defSwitch>"))
            + "</defSwitchVector>\n";

        private const string CcdInfo =
            $"<defNumberVector device=\"{Camera}\" name=\"CCD_INFO\" label=\"CCD Information\" group=\"Image Info\" state=\"Idle\" perm=\"ro\" timeout=\"60\">"
            + "<defNumber name=\"CCD_MAX_X\" label=\"Max. Width\" format=\"%.f\" min=\"1\" max=\"16000\" step=\"0\">4144</defNumber>"
            + "<defNumber name=\"CCD_MAX_Y\" label=\"Max. Height\" format=\"%.f\" min=\"1\" max=\"16000\" step=\"0\">2822</defNumber></defNumberVector>\n";

        private Task WaitForFrameCommandAsync() {
            return main.WaitUntilAsync(
                r => r.Contains("name=\"CCD_FRAME\"", StringComparison.Ordinal),
                "CCD_FRAME is sent",
                Timeout);
        }

        private static string SetFrame(int x, int y, int width, int height, string state = "Ok") =>
            $"<setNumberVector device=\"{Camera}\" name=\"CCD_FRAME\" state=\"{state}\" timeout=\"60\" timestamp=\"2026-11-01T20:00:01\">"
            + $"<oneNumber name=\"X\">{x}</oneNumber><oneNumber name=\"Y\">{y}</oneNumber>"
            + $"<oneNumber name=\"WIDTH\">{width}</oneNumber><oneNumber name=\"HEIGHT\">{height}</oneNumber></setNumberVector>\n";

        private static string CcdFrame(int x, int y, int width, int height) =>
            $"<defNumberVector device=\"{Camera}\" name=\"CCD_FRAME\" label=\"Frame\" group=\"Image Settings\" state=\"Idle\" perm=\"rw\" timeout=\"60\">"
            + $"<defNumber name=\"X\" label=\"Left\" format=\"%4.0f\" min=\"0\" max=\"4143\" step=\"0\">{x}</defNumber>"
            + $"<defNumber name=\"Y\" label=\"Top\" format=\"%4.0f\" min=\"0\" max=\"2821\" step=\"0\">{y}</defNumber>"
            + $"<defNumber name=\"WIDTH\" label=\"Width\" format=\"%4.0f\" min=\"1\" max=\"4144\" step=\"0\">{width}</defNumber>"
            + $"<defNumber name=\"HEIGHT\" label=\"Height\" format=\"%4.0f\" min=\"1\" max=\"2822\" step=\"0\">{height}</defNumber></defNumberVector>\n";

        private Task<INDICamera> RegisterCameraAsync(string extraVectors, params string[] extraProperties) =>
            RegisterCameraInStateAsync("Idle", extraVectors, extraProperties);

        private async Task<INDICamera> RegisterCameraInStateAsync(string exposureState, string extraVectors, params string[] extraProperties) {
            var create = Task.Run(() => new INDICamera(new INDIDeviceInfo { Id = Camera, Name = Camera, Interface = DeviceInterface.CCD_INTERFACE }));
            // The new device asks for its properties and waits for CONNECTION.
            await main.WaitForAsync($"<getProperties version=\"1.7\" device=\"{Camera}\" />", Timeout);
            main.Send(
                $"<defSwitchVector device=\"{Camera}\" name=\"CONNECTION\" label=\"Connection\" group=\"Main\" state=\"Idle\" perm=\"rw\" rule=\"OneOfMany\" timeout=\"60\">"
                + "<defSwitch name=\"CONNECT\" label=\"Connect\">On</defSwitch><defSwitch name=\"DISCONNECT\" label=\"Disconnect\">Off</defSwitch></defSwitchVector>\n"
                + $"<defNumberVector device=\"{Camera}\" name=\"CCD_EXPOSURE\" label=\"Expose\" group=\"Main Control\" state=\"{exposureState}\" perm=\"rw\" timeout=\"60\" timestamp=\"2026-11-01T20:00:00\">"
                + "<defNumber name=\"CCD_EXPOSURE_VALUE\" label=\"Duration (s)\" format=\"%5.2f\" min=\"0\" max=\"3600\" step=\"1\">1</defNumber></defNumberVector>\n"
                + extraVectors);
            Assert.That(await Task.WhenAny(create, Task.Delay(Timeout)), Is.SameAs(create), "the camera registers");
            var camera = await create;

            var deadline = DateTime.UtcNow + Timeout;
            while (camera.GetNumberProperty("CCD_EXPOSURE") == null || Array.Exists(extraProperties, p => camera.GetNumberProperty(p) == null)) {
                if (DateTime.UtcNow > deadline) {
                    Assert.Fail($"Timed out waiting until {string.Join(", ", extraProperties)} are defined");
                }

                await Task.Delay(10);
            }

            return camera;
        }
    }
}
