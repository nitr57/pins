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
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Test.INDI {

    /// <summary>
    /// Kills a real indiserver under the client and checks the recovery end to end: the devices report the
    /// loss, the server and its drivers are restarted, and the equipment can be connected again without a
    /// rescan and gets its images. Needs indiserver and the INDI simulator drivers, and kills every indiserver
    /// on the machine (as INDIClient's start does), so it only runs when selected explicitly.
    /// </summary>
    [TestFixture]
    [Explicit("Starts and kills a real indiserver; needs indiserver and the INDI simulator drivers")]
    [NonParallelizable]
    public class INDIClientRealServerRecoveryTest {
        private const int Port = 7625;
        private const string FocuserDriver = "indi_simulator_focus";
        private const string CameraDriver = "indi_simulator_ccd";
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

        private INDIClient client = null!;
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Structure", "NUnit1032", Justification = "Borrowed singleton, restored in TearDown")]
        private INDIClient? previousInstance;

        [SetUp]
        public void SetUp() {
            client = new INDIClient(Port, startServer: true);
            // INDIDevice talks to INDIClient.Instance
            previousInstance = INDIClient.SetInstanceForTests(client);
        }

        [TearDown]
        public void TearDown() {
            client.Dispose();
            INDIClient.SetInstanceForTests(previousInstance ?? client);
        }

        [Test]
        public async Task KilledServer_IsRestartedWithItsDrivers_AndTheEquipmentConnectsAgain() {
            Assert.That(await client.WaitForServerReadyAsync(Timeout), Is.True, "indiserver started");
            var focuserInfo = (await client.GetDevices(DeviceInterface.FOCUSER_INTERFACE, FocuserDriver, "Focuser")).Single();
            var cameraInfo = (await client.GetDevices(DeviceInterface.CCD_INTERFACE, CameraDriver, "Camera")).Single();

            var focuser = new INDIFocuser(focuserInfo);
            var camera = new INDICamera(cameraInfo);
            await ConnectAndExposeAsync(focuser, camera, "before the loss");

            KillAllIndiServers();

            await WaitUntilAsync(() => !focuser.Connected && !camera.Connected, "both devices report the loss");
            Assert.That(focuser.ServerConnectionLost, Is.True);
            Assert.That(camera.ServerConnectionLost, Is.True);

            Assert.That(await client.WaitForServerReadyAsync(Timeout), Is.True, "indiserver was restarted");
            await WaitUntilAsync(() => focuser.GetSwitchProperty("CONNECTION") != null && camera.GetSwitchProperty("CONNECTION") != null,
                "the restarted drivers define their properties again");
            // The old drivers end once their server is gone; a second instance would fight the first for the device
            await WaitUntilAsync(() => CountProcesses(FocuserDriver) == 1, "the focuser driver runs once");
            await WaitUntilAsync(() => CountProcesses(CameraDriver) == 1, "the camera driver runs once");
            Assert.That(CountProcesses("indiserver"), Is.EqualTo(1), "one indiserver");

            // The same device objects, as the equipment layer keeps them, without a rescan
            await ConnectAndExposeAsync(focuser, camera, "after the recovery");
            Assert.That(focuser.ServerConnectionLost, Is.False);
            Assert.That(camera.ServerConnectionLost, Is.False);
        }

        private async Task ConnectAndExposeAsync(INDIFocuser focuser, INDICamera camera, string when) {
            Assert.That(await focuser.Connect(CancellationToken.None), Is.True, $"the focuser connects {when}");
            Assert.That(await camera.Connect(CancellationToken.None), Is.True, $"the camera connects {when}");
            await WaitUntilAsync(() => camera.GetNumberProperty("CCD_EXPOSURE") != null, $"CCD_EXPOSURE is defined {when}");

            // As IndiCamera.PostConnect does
            client.EnableBLOB(camera.DeviceName);
            camera.StartExposure(1, 1, 1, false, 0, 0, 0, 0, -1, -1);
            using var cts = new CancellationTokenSource(Timeout);
            await camera.WaitUntilExposureIsReady(cts.Token);
            Assert.That(camera.GetLastBlobData(), Is.Not.Null.And.Not.Empty, $"the image arrives {when}");
        }

        private static void KillAllIndiServers() {
            using var pkill = Process.Start(new ProcessStartInfo("pkill", "-9 -x indiserver") { UseShellExecute = false });
            pkill!.WaitForExit();
        }

        // By command line: Linux cuts process names to 15 characters, which both simulators share
        private static int CountProcesses(string name) {
            var startInfo = new ProcessStartInfo("pgrep", $"-c -f \"^{name}( |$)\"") {
                UseShellExecute = false,
                RedirectStandardOutput = true
            };
            using var pgrep = Process.Start(startInfo);
            var output = pgrep!.StandardOutput.ReadToEnd();
            pgrep.WaitForExit();
            return int.Parse(output.Trim());
        }

        private static async Task WaitUntilAsync(Func<bool> condition, string because) {
            var deadline = DateTime.UtcNow + Timeout;
            while (!condition()) {
                if (DateTime.UtcNow > deadline) {
                    Assert.Fail($"Timed out waiting until {because}");
                }

                await Task.Delay(50);
            }
        }
    }
}
