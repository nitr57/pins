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
using NINA.INDI.Protocol;
using NUnit.Framework;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace NINA.Test.INDI {

    /// <summary>
    /// Every camera receives its images on a connection of its own (enableBLOB Only), so the main connection
    /// carries the control traffic alone. Runs the client against a fake indiserver.
    /// </summary>
    [TestFixture]
    [NonParallelizable]
    public class INDIClientBlobConnectionTest {
        private const string Camera = "CCD Simulator";
        private const string GuideCamera = "Guide Simulator";
        private const string Mount = "Telescope Simulator";
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
            // Never restore null, see INDITelescopePulseGuideTest.
            INDIClient.SetInstanceForTests(previousInstance ?? client);
            server.Dispose();
        }

        [Test]
        public async Task EnableBLOB_OpensADedicatedConnection_ThatAsksForImagesOnly() {
            client.EnableBLOB(Camera);

            var images = await server.NextConnectionAsync(Timeout);
            await images.WaitForAsync($"<enableBLOB device=\"{Camera}\">Only</enableBLOB>", Timeout);
            Assert.That(main.Received, Does.Not.Contain("enableBLOB"), "the main connection keeps indiserver's default, Never");
        }

        [Test]
        public async Task EnableBLOB_ForAnAlreadyEnabledCamera_KeepsItsConnection() {
            client.EnableBLOB(Camera);
            var images = await server.NextConnectionAsync(Timeout);

            client.EnableBLOB(Camera);

            Assert.That(async () => await server.NextConnectionAsync(TimeSpan.FromMilliseconds(300)), Throws.InstanceOf<OperationCanceledException>());
            Assert.That(images.Closed.IsCompleted, Is.False);
        }

        [Test]
        public async Task EachCamera_GetsAConnectionOfItsOwn() {
            client.EnableBLOB(Camera);
            var first = await server.NextConnectionAsync(Timeout);
            client.EnableBLOB(GuideCamera);
            var second = await server.NextConnectionAsync(Timeout);

            await first.WaitForAsync($"<enableBLOB device=\"{Camera}\">Only</enableBLOB>", Timeout);
            await second.WaitForAsync($"<enableBLOB device=\"{GuideCamera}\">Only</enableBLOB>", Timeout);
            Assert.That(first.Received, Does.Not.Contain(GuideCamera));
            Assert.That(second.Received, Does.Not.Contain(Camera));
        }

        [Test]
        public async Task ImageOnItsConnection_IsAppliedToTheCamera() {
            var camera = await RegisterCameraAsync();
            client.EnableBLOB(Camera);
            var images = await server.NextConnectionAsync(Timeout);
            var image = Enumerable.Range(0, 100_000).Select(i => (byte)(i % 251)).ToArray();

            images.Send(SetBlob(image));

            await WaitUntilAsync(() => ImageOf(camera)?.Length == image.Length, "the image is applied");
            Assert.That(ImageOf(camera), Is.EqualTo(image));
        }

        [Test]
        public async Task UpdatesOnTheMainConnection_DoNotWaitForAnImageStillArriving() {
            var camera = await RegisterCameraAsync();
            main.Send(DefGuideNs());
            await WaitUntilAsync(() => GuideNsState() == "Idle", "the guide property is defined");
            client.EnableBLOB(Camera);
            var images = await server.NextConnectionAsync(Timeout);
            var image = Enumerable.Range(0, 2_000_000).Select(i => (byte)(i % 253)).ToArray();
            var xml = SetBlob(image);

            // Half an image is on the wire, then a guide pulse completes.
            images.Send(xml[..(xml.Length / 2)]);
            main.Send(SetGuideNs("Ok"));

            await WaitUntilAsync(() => GuideNsState() == "Ok", "the pulse completion is applied");
            Assert.That(ImageOf(camera), Is.Empty, "the image has not fully arrived yet");

            images.Send(xml[(xml.Length / 2)..]);
            await WaitUntilAsync(() => ImageOf(camera)?.Length == image.Length, "the image is applied once complete");
            Assert.That(ImageOf(camera), Is.EqualTo(image));
        }

        [Test]
        public async Task ImageConnectionClosedByTheServer_IsReopened() {
            client.BlobConnectionMinLifetime = TimeSpan.Zero;
            client.EnableBLOB(Camera);
            var images = await server.NextConnectionAsync(Timeout);
            await images.WaitForAsync("Only", Timeout);

            images.Close();

            var reopened = await server.NextConnectionAsync(Timeout);
            await reopened.WaitForAsync($"<enableBLOB device=\"{Camera}\">Only</enableBLOB>", Timeout);
            Assert.That(main.Received, Does.Not.Contain("enableBLOB"));
        }

        [Test]
        public async Task ImageConnectionClosedRightAfterOpening_FallsBackToTheMainConnection() {
            client.EnableBLOB(Camera);
            var images = await server.NextConnectionAsync(Timeout);
            await images.WaitForAsync("Only", Timeout);

            images.Close();

            await main.WaitForAsync($"<enableBLOB device=\"{Camera}\">Also</enableBLOB>", Timeout);
        }

        [Test]
        public async Task Disconnect_ClosesTheImageConnections() {
            client.EnableBLOB(Camera);
            var images = await server.NextConnectionAsync(Timeout);
            await images.WaitForAsync("Only", Timeout);

            client.Disconnect();

            Assert.That(await Task.WhenAny(images.Closed, Task.Delay(Timeout)), Is.SameAs(images.Closed));
        }

        private async Task<INDIDevice> RegisterCameraAsync() {
            var create = Task.Run(() => new INDIDevice(new INDIDeviceInfo { Id = Camera, Name = Camera, Interface = DeviceInterface.CCD_INTERFACE }));
            // The new device asks for its properties and waits for CONNECTION.
            await main.WaitForAsync($"<getProperties version=\"1.7\" device=\"{Camera}\" />", Timeout);
            main.Send(
                $"<defSwitchVector device=\"{Camera}\" name=\"CONNECTION\" label=\"Connection\" group=\"Main\" state=\"Idle\" perm=\"rw\" rule=\"OneOfMany\" timeout=\"60\">"
                + "<defSwitch name=\"CONNECT\" label=\"Connect\">On</defSwitch><defSwitch name=\"DISCONNECT\" label=\"Disconnect\">Off</defSwitch></defSwitchVector>\n"
                + $"<defBLOBVector device=\"{Camera}\" name=\"CCD1\" label=\"Image\" group=\"Image\" state=\"Idle\" perm=\"ro\" timeout=\"60\"><defBLOB name=\"CCD1\" label=\"Image\"/></defBLOBVector>\n");
            Assert.That(await Task.WhenAny(create, Task.Delay(Timeout)), Is.SameAs(create), "the camera registers");
            return await create;
        }

        private static byte[]? ImageOf(INDIDevice device) {
            return (device.GetProperty("CCD1") as INDIBlobProperty)?.Blobs.FirstOrDefault()?.Data;
        }

        private string? GuideNsState() {
            return client.GetDeviceSnapshots(Mount)
                .SelectMany(d => d.Properties)
                .FirstOrDefault(p => p.Name == "TELESCOPE_TIMED_GUIDE_NS")?.State;
        }

        private static string SetBlob(byte[] data) {
            // libindi wraps the base64 payload in lines
            var base64 = Convert.ToBase64String(data, Base64FormattingOptions.InsertLineBreaks);
            return $"<setBLOBVector device=\"{Camera}\" name=\"CCD1\" state=\"Ok\" timeout=\"60\">"
                + $"<oneBLOB name=\"CCD1\" size=\"{data.Length}\" enclen=\"{base64.Length}\" format=\".fits\">\n{base64}\n</oneBLOB></setBLOBVector>\n";
        }

        private static string DefGuideNs() {
            return $"<defNumberVector device=\"{Mount}\" name=\"TELESCOPE_TIMED_GUIDE_NS\" label=\"Guide N/S\" group=\"Guide\" state=\"Idle\" perm=\"rw\" timeout=\"0\">"
                + "<defNumber name=\"TIMED_GUIDE_N\" label=\"North\" format=\"%.f\" min=\"0\" max=\"60000\" step=\"100\">0</defNumber>"
                + "<defNumber name=\"TIMED_GUIDE_S\" label=\"South\" format=\"%.f\" min=\"0\" max=\"60000\" step=\"100\">0</defNumber></defNumberVector>\n";
        }

        private static string SetGuideNs(string state) {
            return $"<setNumberVector device=\"{Mount}\" name=\"TELESCOPE_TIMED_GUIDE_NS\" state=\"{state}\" timeout=\"0\">"
                + "<oneNumber name=\"TIMED_GUIDE_N\">0</oneNumber><oneNumber name=\"TIMED_GUIDE_S\">0</oneNumber></setNumberVector>\n";
        }

        private static async Task WaitUntilAsync(Func<bool> condition, string because) {
            var deadline = DateTime.UtcNow + Timeout;
            while (!condition()) {
                if (DateTime.UtcNow > deadline) {
                    Assert.Fail($"Timed out waiting until {because}");
                }
                await Task.Delay(10);
            }
        }
    }
}
