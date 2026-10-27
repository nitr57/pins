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
    /// A park only counts as done once the driver has reported PARK=On after the command. A driver
    /// Alert, a mount that stays Busy, stops without PARK=On or never answers fails the park, so
    /// TelescopeVM reports "failed to park" instead of "Mount has parked". Runs an INDITelescope
    /// against a fake indiserver.
    /// </summary>
    [TestFixture]
    [NonParallelizable]
    public class INDITelescopeParkTest {
        private const string Mount = "Telescope Simulator";
        private const string DefinedAt = "2026-10-27T20:00:00";
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan PhaseTimeout = TimeSpan.FromSeconds(1);

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
            // Keep the disposed test client rather than restoring null, as in INDITelescopePulseGuideTest:
            // a real client would kill the machine's indiserver.
            INDIClient.SetInstanceForTests(previousInstance ?? client);
            server.Dispose();
        }

        [Test]
        public async Task Park_Succeeds_WhenTheDriverReportsParkedAfterTheMotion() {
            var telescope = await RegisterTelescopeAsync();

            var park = telescope.ParkAsync();
            await WaitForParkCommandAsync();
            main.Send(SetParkVector("Busy", parked: true, "2026-10-27T20:00:01"));
            await Task.Delay(300);
            main.Send(SetParkVector("Ok", parked: true, "2026-10-27T20:00:05"));

            Assert.That(async () => await park, Throws.Nothing);
        }

        [Test]
        public async Task Park_Succeeds_WhenTheMountIsAlreadyAtItsParkPosition() {
            var telescope = await RegisterTelescopeAsync();

            var park = telescope.ParkAsync();
            await WaitForParkCommandAsync();
            main.Send(SetParkVector("Ok", parked: true, "2026-10-27T20:00:01"));

            Assert.That(async () => await park, Throws.Nothing);
        }

        [Test]
        public async Task Park_Fails_WhenTheDriverReportsAlert() {
            var telescope = await RegisterTelescopeAsync();

            var park = telescope.ParkAsync();
            await WaitForParkCommandAsync();
            main.Send(SetParkVector("Alert", parked: false, "2026-10-27T20:00:01"));

            Assert.That(async () => await park, Throws.TypeOf<InvalidOperationException>());
        }

        [Test]
        public async Task Park_Fails_WhenTheDriverReportsAlertAfterTheMotionStarted() {
            var telescope = await RegisterTelescopeAsync();

            var park = telescope.ParkAsync();
            await WaitForParkCommandAsync();
            main.Send(SetParkVector("Busy", parked: true, "2026-10-27T20:00:01"));
            await Task.Delay(300);
            main.Send(SetParkVector("Alert", parked: false, "2026-10-27T20:00:05"));

            Assert.That(async () => await park, Throws.TypeOf<InvalidOperationException>());
        }

        [Test]
        public async Task Park_Fails_WhenTheMountStopsWithoutReportingParked() {
            var telescope = await RegisterTelescopeAsync();

            var park = telescope.ParkAsync();
            await WaitForParkCommandAsync();
            main.Send(SetParkVector("Busy", parked: true, "2026-10-27T20:00:01"));
            await Task.Delay(300);
            main.Send(SetParkVector("Idle", parked: false, "2026-10-27T20:00:05"));

            Assert.That(async () => await park, Throws.TypeOf<InvalidOperationException>());
        }

        [Test]
        public async Task Park_Fails_WhenTheDriverNeverAnswers() {
            var telescope = await RegisterTelescopeAsync();

            // Sending the command already set PARK=On in the local copy; without a driver update
            // that value must not count as parked.
            var park = telescope.ParkAsync();
            await WaitForParkCommandAsync();

            Assert.That(async () => await park, Throws.TypeOf<InvalidOperationException>());
        }

        [Test]
        public async Task Park_TimesOut_WhenTheMountStaysBusy() {
            var telescope = await RegisterTelescopeAsync();

            var park = telescope.ParkAsync();
            await WaitForParkCommandAsync();
            main.Send(SetParkVector("Busy", parked: true, "2026-10-27T20:00:01"));

            Assert.That(async () => await park, Throws.TypeOf<TimeoutException>());
        }

        private async Task<INDITelescope> RegisterTelescopeAsync() {
            var create = Task.Run(() => new INDITelescope(new INDIDeviceInfo { Id = Mount, Name = Mount, Interface = DeviceInterface.TELESCOPE_INTERFACE }));
            // The new device asks for its properties and waits for CONNECTION.
            await main.WaitForAsync($"<getProperties version=\"1.7\" device=\"{Mount}\" />", Timeout);
            main.Send(
                $"<defSwitchVector device=\"{Mount}\" name=\"CONNECTION\" label=\"Connection\" group=\"Main\" state=\"Idle\" perm=\"rw\" rule=\"OneOfMany\" timeout=\"60\">"
                + "<defSwitch name=\"CONNECT\" label=\"Connect\">On</defSwitch><defSwitch name=\"DISCONNECT\" label=\"Disconnect\">Off</defSwitch></defSwitchVector>\n"
                + $"<defSwitchVector device=\"{Mount}\" name=\"TELESCOPE_PARK\" label=\"Parking\" group=\"Site Management\" state=\"Idle\" perm=\"rw\" rule=\"OneOfMany\" timeout=\"60\" timestamp=\"{DefinedAt}\">"
                + "<defSwitch name=\"PARK\" label=\"Park(ed)\">Off</defSwitch><defSwitch name=\"UNPARK\" label=\"UnPark(ed)\">On</defSwitch></defSwitchVector>\n");
            Assert.That(await Task.WhenAny(create, Task.Delay(Timeout)), Is.SameAs(create), "the telescope registers");
            var telescope = await create;

            var deadline = DateTime.UtcNow + Timeout;
            while (telescope.GetSwitchProperty("TELESCOPE_PARK") == null) {
                if (DateTime.UtcNow > deadline) {
                    Assert.Fail("Timed out waiting until TELESCOPE_PARK is defined");
                }

                await Task.Delay(10);
            }

            telescope.ParkAcknowledgeTimeout = PhaseTimeout;
            telescope.ParkMotionStartTimeout = PhaseTimeout;
            telescope.ParkCompletionTimeout = PhaseTimeout;
            telescope.ParkConfirmTimeout = PhaseTimeout;
            return telescope;
        }

        private Task WaitForParkCommandAsync() {
            return main.WaitUntilAsync(
                r => r.Contains("<newSwitchVector", StringComparison.Ordinal) && r.Contains("name=\"TELESCOPE_PARK\"", StringComparison.Ordinal),
                "the park command is sent",
                Timeout);
        }

        private static string SetParkVector(string state, bool parked, string timestamp) {
            var park = parked ? "On" : "Off";
            var unpark = parked ? "Off" : "On";
            return $"<setSwitchVector device=\"{Mount}\" name=\"TELESCOPE_PARK\" state=\"{state}\" timeout=\"60\" timestamp=\"{timestamp}\">"
                + $"<oneSwitch name=\"PARK\">{park}</oneSwitch><oneSwitch name=\"UNPARK\">{unpark}</oneSwitch></setSwitchVector>\n";
        }
    }
}
