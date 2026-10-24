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
using NINA.INDI.Enums;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace NINA.Test.INDI {

    /// <summary>
    /// GetDevices evicts the driver a device-type category used before, when the category switches to another
    /// driver. A driver another category still uses must stay loaded, e.g. a camera and a guide camera both on
    /// indi_asi_ccd. Runs against a fake indiserver that answers "start" with the driver's DRIVER_INFO.
    /// </summary>
    [TestFixture]
    [NonParallelizable]
    public class INDIClientDriverSharingTest {
        private const string Asi = "indi_asi_ccd";
        private const string ToupTek = "indi_toupcam_ccd";
        private const string PlayerOne = "indi_playerone_ccd";
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

        private FakeIndiServer server = null!;
        private INDIClient client = null!;
        private FakeIndiServer.Connection main = null!;
        private readonly List<string> commands = [];

        [SetUp]
        public async Task SetUp() {
            lock (commands) {
                commands.Clear();
            }
            server = new FakeIndiServer();
            client = new INDIClient(server.Port, startServer: false) { FifoCommandsForTests = OnFifoCommand };
            Assert.That(await client.Connect(), Is.True);
            main = await server.NextConnectionAsync(Timeout);
        }

        [TearDown]
        public void TearDown() {
            client.Dispose();
            server.Dispose();
        }

        [Test]
        public async Task SharedDriver_StaysLoaded_WhenOneCategorySwitchesAway() {
            await ScanAsync("Camera", Asi);
            await ScanAsync("GuideCamera", Asi);

            await ScanAsync("Camera", ToupTek);

            Assert.That(Commands(), Is.EqualTo(new[] { $"start {Asi}", $"start {ToupTek}" }));
            Assert.That(await ScanAsync("GuideCamera", Asi), Does.Contain(DeviceOf(Asi)), "the guide camera keeps its device");
        }

        [Test]
        public async Task SharedDriver_IsUnloaded_OnceNoCategoryUsesIt() {
            await ScanAsync("Camera", Asi);
            await ScanAsync("GuideCamera", Asi);
            await ScanAsync("Camera", ToupTek);

            await ScanAsync("GuideCamera", PlayerOne);

            Assert.That(Commands(), Is.EqualTo(new[] { $"start {Asi}", $"start {ToupTek}", $"stop {Asi}", $"start {PlayerOne}" }));
        }

        [Test]
        public async Task SwitchingToADriverAnotherCategoryLoaded_UnloadsThePreviousOne() {
            await ScanAsync("Camera", ToupTek);
            await ScanAsync("GuideCamera", Asi);

            var devices = await ScanAsync("Camera", Asi);

            Assert.That(Commands(), Is.EqualTo(new[] { $"start {ToupTek}", $"start {Asi}", $"stop {ToupTek}" }));
            Assert.That(devices, Does.Contain(DeviceOf(Asi)));
        }

        [Test]
        public async Task UnsharedDriver_IsUnloaded_WhenItsCategorySwitches() {
            await ScanAsync("Camera", Asi);
            await ScanAsync("Camera", Asi);

            await ScanAsync("Camera", ToupTek);

            Assert.That(Commands(), Is.EqualTo(new[] { $"start {Asi}", $"stop {Asi}", $"start {ToupTek}" }));
        }

        [Test]
        public async Task UnloadingADriver_ClosesItsDevicesImageConnections() {
            await ScanAsync("Camera", Asi);
            client.EnableBLOB(DeviceOf(Asi));
            var asiImages = await server.NextConnectionAsync(Timeout);
            await asiImages.WaitForAsync("Only", Timeout);
            await ScanAsync("GuideCamera", ToupTek);
            client.EnableBLOB(DeviceOf(ToupTek));
            var toupTekImages = await server.NextConnectionAsync(Timeout);
            await toupTekImages.WaitForAsync("Only", Timeout);

            await ScanAsync("Camera", PlayerOne);

            Assert.That(await Task.WhenAny(asiImages.Closed, Task.Delay(Timeout)), Is.SameAs(asiImages.Closed));
            Assert.That(async () => await server.NextConnectionAsync(TimeSpan.FromMilliseconds(500)),
                Throws.InstanceOf<OperationCanceledException>(), "the closed connection is not reopened");
            Assert.That(main.Received, Does.Not.Contain("enableBLOB"), "nor does it fall back to the main connection");
            Assert.That(toupTekImages.Closed.IsCompleted, Is.False, "the guide camera's driver is still loaded");
        }

        private async Task<List<string>> ScanAsync(string category, string driver) {
            var devices = await client.GetDevices(DeviceInterface.CCD_INTERFACE, driver, category);
            return devices.Select(d => d.Id).ToList();
        }

        private List<string> Commands() {
            lock (commands) {
                return [.. commands];
            }
        }

        private static string DeviceOf(string driver) => $"{driver} device";

        // Plays indiserver: a started driver describes its device, a stopped driver deletes it.
        private void OnFifoCommand(string command) {
            lock (commands) {
                commands.Add(command);
            }
            var parts = command.Split(' ', 2);
            var device = DeviceOf(parts[1]);
            if (parts[0] == "start") {
                main.Send(
                    $"<defTextVector device=\"{device}\" name=\"DRIVER_INFO\" label=\"Driver Info\" group=\"General Info\" state=\"Idle\" perm=\"ro\" timeout=\"0\">"
                    + $"<defText name=\"DRIVER_NAME\" label=\"Name\">{device}</defText>"
                    + $"<defText name=\"DRIVER_EXEC\" label=\"Exec\">{parts[1]}</defText>"
                    + "<defText name=\"DRIVER_VERSION\" label=\"Version\">1.0</defText>"
                    + $"<defText name=\"DRIVER_INTERFACE\" label=\"Interface\">{(int)DeviceInterface.CCD_INTERFACE}</defText></defTextVector>\n");
            } else {
                main.Send($"<delProperty device=\"{device}\" />\n");
            }
        }
    }
}
