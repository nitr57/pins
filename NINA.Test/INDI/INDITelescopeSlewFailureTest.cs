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
    /// A goto only counts as done when the mount reports its end. A mount that stays Busy past the slew timeout fails the
    /// slew instead of letting the sequence expose wherever the mount is, and a cancelled slew stops the mount instead of
    /// leaving it driving to the old target. Runs an INDITelescope against a fake indiserver.
    /// </summary>
    [TestFixture]
    [NonParallelizable]
    public class INDITelescopeSlewFailureTest {
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
            // Keep the disposed test client rather than restoring null, as in INDITelescopeParkTest:
            // a real client would kill the machine's indiserver.
            INDIClient.SetInstanceForTests(previousInstance ?? client);
            server.Dispose();
        }

        [Test]
        public async Task A_slew_that_never_ends_fails_after_the_timeout() {
            var telescope = await RegisterTelescopeAsync();
            telescope.SlewCompletionTimeout = TimeSpan.FromSeconds(1);

            var slew = telescope.SlewToCoordinatesTaskAsync(5.5, 22.0);
            await WaitForGotoAsync();
            main.Send(SetCoordinates("Busy", "2026-11-01T20:00:01"));

            Assert.That(async () => await slew.WaitAsync(Timeout), Throws.TypeOf<TimeoutException>());
        }

        [Test]
        public async Task A_slew_that_ends_succeeds() {
            var telescope = await RegisterTelescopeAsync();

            var slew = telescope.SlewToCoordinatesTaskAsync(5.5, 22.0);
            await WaitForGotoAsync();
            main.Send(SetCoordinates("Busy", "2026-11-01T20:00:01"));
            await Task.Delay(300);
            main.Send(SetCoordinates("Ok", "2026-11-01T20:00:02"));

            Assert.That(async () => await slew.WaitAsync(Timeout), Throws.Nothing);
        }

        [Test]
        public async Task A_cancelled_slew_aborts_the_mount() {
            var telescope = await RegisterTelescopeAsync();
            using var cts = new CancellationTokenSource();

            var slew = telescope.SlewToCoordinatesTaskAsync(5.5, 22.0, cts.Token);
            await WaitForGotoAsync();
            main.Send(SetCoordinates("Busy", "2026-11-01T20:00:01"));
            await Task.Delay(300);
            cts.Cancel();

            Assert.That(async () => await slew.WaitAsync(Timeout), Throws.InstanceOf<OperationCanceledException>());
            await main.WaitUntilAsync(
                r => r.Contains("<newSwitchVector", StringComparison.Ordinal) && r.Contains("name=\"TELESCOPE_ABORT_MOTION\"", StringComparison.Ordinal),
                "the abort is sent",
                Timeout);
        }

        private async Task<INDITelescope> RegisterTelescopeAsync() {
            var create = Task.Run(() => new INDITelescope(new INDIDeviceInfo { Id = Mount, Name = Mount, Interface = DeviceInterface.TELESCOPE_INTERFACE }));
            // The new device asks for its properties and waits for CONNECTION.
            await main.WaitForAsync($"<getProperties version=\"1.7\" device=\"{Mount}\" />", Timeout);
            main.Send(
                $"<defSwitchVector device=\"{Mount}\" name=\"CONNECTION\" label=\"Connection\" group=\"Main\" state=\"Idle\" perm=\"rw\" rule=\"OneOfMany\" timeout=\"60\">"
                + "<defSwitch name=\"CONNECT\" label=\"Connect\">On</defSwitch><defSwitch name=\"DISCONNECT\" label=\"Disconnect\">Off</defSwitch></defSwitchVector>\n"
                + $"<defSwitchVector device=\"{Mount}\" name=\"TELESCOPE_PARK\" label=\"Parking\" group=\"Site Management\" state=\"Idle\" perm=\"rw\" rule=\"OneOfMany\" timeout=\"60\">"
                + "<defSwitch name=\"PARK\" label=\"Park(ed)\">Off</defSwitch><defSwitch name=\"UNPARK\" label=\"UnPark(ed)\">On</defSwitch></defSwitchVector>\n"
                + $"<defSwitchVector device=\"{Mount}\" name=\"ON_COORD_SET\" label=\"On Set\" group=\"Main Control\" state=\"Idle\" perm=\"rw\" rule=\"OneOfMany\" timeout=\"60\">"
                + "<defSwitch name=\"TRACK\" label=\"Track\">On</defSwitch><defSwitch name=\"SLEW\" label=\"Slew\">Off</defSwitch><defSwitch name=\"SYNC\" label=\"Sync\">Off</defSwitch></defSwitchVector>\n"
                + $"<defSwitchVector device=\"{Mount}\" name=\"TELESCOPE_ABORT_MOTION\" label=\"Abort Motion\" group=\"Main Control\" state=\"Idle\" perm=\"rw\" rule=\"AtMostOne\" timeout=\"60\">"
                + "<defSwitch name=\"ABORT\" label=\"Abort\">Off</defSwitch></defSwitchVector>\n"
                + $"<defNumberVector device=\"{Mount}\" name=\"EQUATORIAL_EOD_COORD\" label=\"Eq. Coordinates\" group=\"Main Control\" state=\"Idle\" perm=\"rw\" timeout=\"60\" timestamp=\"2026-11-01T20:00:00\">"
                + "<defNumber name=\"RA\" label=\"RA (hh:mm:ss)\" format=\"%010.6m\" min=\"0\" max=\"24\" step=\"0\">5.5</defNumber>"
                + "<defNumber name=\"DEC\" label=\"DEC (dd:mm:ss)\" format=\"%010.6m\" min=\"-90\" max=\"90\" step=\"0\">22</defNumber></defNumberVector>\n");
            Assert.That(await Task.WhenAny(create, Task.Delay(Timeout)), Is.SameAs(create), "the telescope registers");
            var telescope = await create;

            var deadline = DateTime.UtcNow + Timeout;
            while (telescope.GetNumberProperty("EQUATORIAL_EOD_COORD") == null || telescope.GetSwitchProperty("TELESCOPE_ABORT_MOTION") == null) {
                if (DateTime.UtcNow > deadline) {
                    Assert.Fail("Timed out waiting until the properties are defined");
                }

                await Task.Delay(10);
            }

            return telescope;
        }

        private Task WaitForGotoAsync() {
            return main.WaitUntilAsync(
                r => r.Contains("<newNumberVector", StringComparison.Ordinal) && r.Contains("name=\"EQUATORIAL_EOD_COORD\"", StringComparison.Ordinal),
                "the goto is sent",
                Timeout);
        }

        // The mount keeps its position; only the state says whether it moves
        private static string SetCoordinates(string state, string timestamp) {
            return $"<setNumberVector device=\"{Mount}\" name=\"EQUATORIAL_EOD_COORD\" state=\"{state}\" timeout=\"60\" timestamp=\"{timestamp}\">"
                + "<oneNumber name=\"RA\">5.5</oneNumber><oneNumber name=\"DEC\">22</oneNumber></setNumberVector>\n";
        }
    }
}
