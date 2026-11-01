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
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Test.INDI {

    /// <summary>
    /// A dome motion (shutter, slew, park) only counts as done when the driver reports its end: an Alert (rejected or
    /// failed) and no end within the timeout throw, and a cancellation stops the dome. Before, all three returned as if
    /// the shutter were open or closed. Runs an INDIDome against a fake indiserver.
    /// </summary>
    [TestFixture]
    [NonParallelizable]
    public class INDIDomeMotionTest {
        private const string Dome = "Dome Simulator";
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
        public async Task Opening_the_shutter_succeeds_when_the_driver_reports_it_open() {
            var dome = await RegisterDomeAsync();

            var open = dome.OpenShutter(CancellationToken.None);
            await WaitForShutterCommandAsync();
            main.Send(SetShutter("Busy", open: true));
            await Task.Delay(300);
            main.Send(SetShutter("Ok", open: true));

            Assert.That(async () => await open.WaitAsync(Timeout), Throws.Nothing);
        }

        [Test]
        public async Task Opening_the_shutter_fails_when_the_driver_rejects_it() {
            var dome = await RegisterDomeAsync();

            var open = dome.OpenShutter(CancellationToken.None);
            await WaitForShutterCommandAsync();
            main.Send(SetShutter("Alert", open: false));

            Assert.That(async () => await open.WaitAsync(Timeout), Throws.TypeOf<InvalidOperationException>());
        }

        [Test]
        public async Task Opening_the_shutter_fails_when_it_doesnt_end_within_the_timeout() {
            var dome = await RegisterDomeAsync();
            dome.MoveCompletionTimeout = TimeSpan.FromSeconds(1);

            var open = dome.OpenShutter(CancellationToken.None);
            await WaitForShutterCommandAsync();
            main.Send(SetShutter("Busy", open: true));

            Assert.That(async () => await open.WaitAsync(Timeout), Throws.TypeOf<TimeoutException>());
        }

        [Test]
        public async Task Cancelling_the_shutter_aborts_the_dome() {
            var dome = await RegisterDomeAsync();
            using var cts = new CancellationTokenSource();

            var close = dome.CloseShutter(cts.Token);
            await WaitForShutterCommandAsync();
            main.Send(SetShutter("Busy", open: false));
            await Task.Delay(300);
            cts.Cancel();

            Assert.That(async () => await close.WaitAsync(Timeout), Throws.InstanceOf<OperationCanceledException>());
            await main.WaitUntilAsync(
                r => r.Contains("<newSwitchVector", StringComparison.Ordinal) && r.Contains("name=\"DOME_ABORT_MOTION\"", StringComparison.Ordinal),
                "the abort is sent",
                Timeout);
        }

        private async Task<INDIDome> RegisterDomeAsync() {
            var create = Task.Run(() => new INDIDome(new INDIDeviceInfo { Id = Dome, Name = Dome, Interface = DeviceInterface.DOME_INTERFACE }));
            // The new device asks for its properties and waits for CONNECTION.
            await main.WaitForAsync($"<getProperties version=\"1.7\" device=\"{Dome}\" />", Timeout);
            main.Send(
                $"<defSwitchVector device=\"{Dome}\" name=\"CONNECTION\" label=\"Connection\" group=\"Main\" state=\"Idle\" perm=\"rw\" rule=\"OneOfMany\" timeout=\"60\">"
                + "<defSwitch name=\"CONNECT\" label=\"Connect\">On</defSwitch><defSwitch name=\"DISCONNECT\" label=\"Disconnect\">Off</defSwitch></defSwitchVector>\n"
                + $"<defSwitchVector device=\"{Dome}\" name=\"DOME_SHUTTER\" label=\"Shutter\" group=\"Main Control\" state=\"Idle\" perm=\"rw\" rule=\"OneOfMany\" timeout=\"60\" timestamp=\"2026-11-01T20:00:00\">"
                + "<defSwitch name=\"SHUTTER_OPEN\" label=\"Open\">Off</defSwitch><defSwitch name=\"SHUTTER_CLOSE\" label=\"Close\">On</defSwitch></defSwitchVector>\n"
                + $"<defSwitchVector device=\"{Dome}\" name=\"DOME_ABORT_MOTION\" label=\"Abort Motion\" group=\"Main Control\" state=\"Idle\" perm=\"rw\" rule=\"AtMostOne\" timeout=\"60\">"
                + "<defSwitch name=\"ABORT\" label=\"Abort\">Off</defSwitch></defSwitchVector>\n"
                + $"<defNumberVector device=\"{Dome}\" name=\"ABS_DOME_POSITION\" label=\"Absolute Position\" group=\"Main Control\" state=\"Idle\" perm=\"rw\" timeout=\"60\">"
                + "<defNumber name=\"DOME_ABSOLUTE_POSITION\" label=\"Degrees\" format=\"%6.2f\" min=\"0\" max=\"360\" step=\"1\">0</defNumber></defNumberVector>\n");
            Assert.That(await Task.WhenAny(create, Task.Delay(Timeout)), Is.SameAs(create), "the dome registers");
            var dome = await create;

            var deadline = DateTime.UtcNow + Timeout;
            while (dome.GetSwitchProperty("DOME_SHUTTER") == null || dome.GetSwitchProperty("DOME_ABORT_MOTION") == null) {
                if (DateTime.UtcNow > deadline) {
                    Assert.Fail("Timed out waiting until the properties are defined");
                }

                await Task.Delay(10);
            }

            // The dome's commands only run on a connected device; the connect handshake is not under test here
            dome.Connected = true;
            return dome;
        }

        private Task WaitForShutterCommandAsync() {
            return main.WaitUntilAsync(
                r => r.Contains("<newSwitchVector", StringComparison.Ordinal) && r.Contains("name=\"DOME_SHUTTER\"", StringComparison.Ordinal),
                "the shutter command is sent",
                Timeout);
        }

        private static string SetShutter(string state, bool open) {
            var openValue = open ? "On" : "Off";
            var closeValue = open ? "Off" : "On";
            return $"<setSwitchVector device=\"{Dome}\" name=\"DOME_SHUTTER\" state=\"{state}\" timeout=\"60\" timestamp=\"2026-11-01T20:00:01\">"
                + $"<oneSwitch name=\"SHUTTER_OPEN\">{openValue}</oneSwitch><oneSwitch name=\"SHUTTER_CLOSE\">{closeValue}</oneSwitch></setSwitchVector>\n";
        }
    }
}
