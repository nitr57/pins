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
    /// INDI states the site longitude as 0..360° east, NINA as -180..180°: a site west of Greenwich reads and writes in
    /// NINA's range, so the profile and the mount agree. Runs an INDITelescope against a fake indiserver.
    /// </summary>
    [TestFixture]
    [NonParallelizable]
    public class INDITelescopeLongitudeTest {
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

        [TestCase(355.0, -5.0)]
        [TestCase(13.4, 13.4)]
        [TestCase(180.0, 180.0)]
        [TestCase(0.0, 0.0)]
        public async Task TheDriversLongitude_ReadsInNinasRange(double indiLongitude, double ninaLongitude) {
            var telescope = await RegisterTelescopeAsync(indiLongitude);

            Assert.That(telescope.SiteLongitude, Is.EqualTo(ninaLongitude).Within(1e-9));
        }

        [TestCase(-5.0, "355")]
        [TestCase(13.4, "13.4")]
        public async Task A_longitude_is_sent_in_INDIs_range(double ninaLongitude, string sent) {
            var telescope = await RegisterTelescopeAsync(0);

            telescope.SiteLongitude = ninaLongitude;

            await main.WaitUntilAsync(
                r => r.Contains("name=\"GEOGRAPHIC_COORD\"", StringComparison.Ordinal) && r.Contains($"<oneNumber name=\"LONG\">{sent}</oneNumber>", StringComparison.Ordinal),
                $"LONG {sent} is sent",
                Timeout);
        }

        private async Task<INDITelescope> RegisterTelescopeAsync(double longitude) {
            var create = Task.Run(() => new INDITelescope(new INDIDeviceInfo { Id = Mount, Name = Mount, Interface = DeviceInterface.TELESCOPE_INTERFACE }));
            // The new device asks for its properties and waits for CONNECTION.
            await main.WaitForAsync($"<getProperties version=\"1.7\" device=\"{Mount}\" />", Timeout);
            main.Send(
                $"<defSwitchVector device=\"{Mount}\" name=\"CONNECTION\" label=\"Connection\" group=\"Main\" state=\"Idle\" perm=\"rw\" rule=\"OneOfMany\" timeout=\"60\">"
                + "<defSwitch name=\"CONNECT\" label=\"Connect\">On</defSwitch><defSwitch name=\"DISCONNECT\" label=\"Disconnect\">Off</defSwitch></defSwitchVector>\n"
                + $"<defNumberVector device=\"{Mount}\" name=\"GEOGRAPHIC_COORD\" label=\"Scope Location\" group=\"Site Management\" state=\"Ok\" perm=\"rw\" timeout=\"60\">"
                + "<defNumber name=\"LAT\" label=\"Lat (dd:mm:ss.s)\" format=\"%012.8m\" min=\"-90\" max=\"90\" step=\"0\">51.5</defNumber>"
                + $"<defNumber name=\"LONG\" label=\"Lon (dd:mm:ss.s)\" format=\"%012.8m\" min=\"0\" max=\"360\" step=\"0\">{longitude.ToString(System.Globalization.CultureInfo.InvariantCulture)}</defNumber>"
                + "<defNumber name=\"ELEV\" label=\"Elevation (m)\" format=\"%g\" min=\"-200\" max=\"10000\" step=\"0\">100</defNumber></defNumberVector>\n");
            Assert.That(await Task.WhenAny(create, Task.Delay(Timeout)), Is.SameAs(create), "the telescope registers");
            var telescope = await create;

            var deadline = DateTime.UtcNow + Timeout;
            while (telescope.GetNumberProperty("GEOGRAPHIC_COORD") == null) {
                if (DateTime.UtcNow > deadline) {
                    Assert.Fail("Timed out waiting until GEOGRAPHIC_COORD is defined");
                }

                await Task.Delay(10);
            }

            return telescope;
        }
    }
}
