#region "copyright"

/*
    Copyright © 2026 Nico Trost <nico.trost57@gmail.com> and the PI.N.S. contributors

    This file is part of PI 'N' Stars.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Core.Enum;
using NINA.INDI;
using NINA.INDI.Devices;
using NINA.INDI.Enums;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace NINA.Test.INDI {

    /// <summary>
    /// libindi's GuiderInterface pulses N (or W) whenever that element is non-zero and GuideComplete
    /// never resets the values, so every pulse must send both elements of its axis, the opposite one
    /// as 0. Runs an INDITelescope against a fake indiserver and checks what goes out on the wire.
    /// </summary>
    [TestFixture]
    [NonParallelizable]
    public class INDITelescopePulseGuideTest {
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
            // Without a previous instance keep the disposed test client: restoring null would let a
            // device constructor still running after a timeout create a real client, which kills the
            // machine's indiserver.
            INDIClient.SetInstanceForTests(previousInstance ?? client);
            server.Dispose();
        }

        [TestCase(GuideDirections.guideNorth, GuideDirections.guideSouth, "TELESCOPE_TIMED_GUIDE_NS", "TIMED_GUIDE_S", "TIMED_GUIDE_N")]
        [TestCase(GuideDirections.guideSouth, GuideDirections.guideNorth, "TELESCOPE_TIMED_GUIDE_NS", "TIMED_GUIDE_N", "TIMED_GUIDE_S")]
        [TestCase(GuideDirections.guideWest, GuideDirections.guideEast, "TELESCOPE_TIMED_GUIDE_WE", "TIMED_GUIDE_E", "TIMED_GUIDE_W")]
        [TestCase(GuideDirections.guideEast, GuideDirections.guideWest, "TELESCOPE_TIMED_GUIDE_WE", "TIMED_GUIDE_W", "TIMED_GUIDE_E")]
        public async Task PulseAfterOppositePulse_SendsTheOppositeElementAsZero(
            GuideDirections first, GuideDirections second, string property, string pulsed, string opposite) {
            var telescope = await RegisterTelescopeAsync();

            telescope.PulseGuide(first, 700);
            await main.WaitUntilAsync(r => SentVectors(r, property).Count == 1, "the first pulse is sent", Timeout);
            // The driver runs the pulse and completes it like GuideComplete: back to Idle, values kept.
            var firstVector = SentVectors(main.Received, property)[0];
            await ApplyDriverUpdateAsync(telescope, SetGuideVector(property, "Busy", firstVector) + SetGuideVector(property, "Idle", firstVector));

            telescope.PulseGuide(second, 300);
            await main.WaitUntilAsync(r => SentVectors(r, property).Count == 2, "the second pulse is sent", Timeout);

            var sent = SentVectors(main.Received, property)[1];
            Assert.That(sent, Does.ContainKey(pulsed).WithValue(300));
            Assert.That(sent, Does.ContainKey(opposite).WithValue(0), "a non-zero opposite element makes the driver pulse the wrong way");
        }

        [TestCase(GuideDirections.guideNorth, "TELESCOPE_TIMED_GUIDE_NS", "TIMED_GUIDE_N", "TIMED_GUIDE_S")]
        [TestCase(GuideDirections.guideSouth, "TELESCOPE_TIMED_GUIDE_NS", "TIMED_GUIDE_S", "TIMED_GUIDE_N")]
        [TestCase(GuideDirections.guideWest, "TELESCOPE_TIMED_GUIDE_WE", "TIMED_GUIDE_W", "TIMED_GUIDE_E")]
        [TestCase(GuideDirections.guideEast, "TELESCOPE_TIMED_GUIDE_WE", "TIMED_GUIDE_E", "TIMED_GUIDE_W")]
        public async Task Pulse_SendsBothElementsOfItsAxisOnly(GuideDirections direction, string property, string pulsed, string opposite) {
            var telescope = await RegisterTelescopeAsync();

            telescope.PulseGuide(direction, 500);
            await main.WaitUntilAsync(r => SentVectors(r, property).Count == 1, "the pulse is sent", Timeout);

            Assert.That(SentVectors(main.Received, property)[0], Is.EquivalentTo(new Dictionary<string, double> { [pulsed] = 500, [opposite] = 0 }));
            var otherAxis = property == "TELESCOPE_TIMED_GUIDE_NS" ? "TELESCOPE_TIMED_GUIDE_WE" : "TELESCOPE_TIMED_GUIDE_NS";
            Assert.That(SentVectors(main.Received, otherAxis), Is.Empty);
        }

        [Test]
        public async Task IsPulseGuiding_FromSendingThePulse_UntilTheDriverReportsItsEnd() {
            var telescope = await RegisterTelescopeAsync();

            telescope.PulseGuide(GuideDirections.guideNorth, 2000);
            Assert.That(telescope.IsPulseGuiding, Is.True, "before the driver has answered");

            await main.WaitUntilAsync(r => SentVectors(r, "TELESCOPE_TIMED_GUIDE_NS").Count == 1, "the pulse is sent", Timeout);
            var sent = SentVectors(main.Received, "TELESCOPE_TIMED_GUIDE_NS")[0];
            await ApplyDriverUpdateAsync(telescope, SetGuideVector("TELESCOPE_TIMED_GUIDE_NS", "Busy", sent));
            Assert.That(telescope.IsPulseGuiding, Is.True, "while the driver reports Busy");

            await ApplyDriverUpdateAsync(telescope, SetGuideVector("TELESCOPE_TIMED_GUIDE_NS", "Idle", sent));
            Assert.That(telescope.IsPulseGuiding, Is.False, "once the driver reports the end");
        }

        private async Task<INDITelescope> RegisterTelescopeAsync() {
            var create = Task.Run(() => new INDITelescope(new INDIDeviceInfo { Id = Mount, Name = Mount, Interface = DeviceInterface.TELESCOPE_INTERFACE }));
            // The new device asks for its properties and waits for CONNECTION.
            await main.WaitForAsync($"<getProperties version=\"1.7\" device=\"{Mount}\" />", Timeout);
            main.Send(
                $"<defSwitchVector device=\"{Mount}\" name=\"CONNECTION\" label=\"Connection\" group=\"Main\" state=\"Idle\" perm=\"rw\" rule=\"OneOfMany\" timeout=\"60\">"
                + "<defSwitch name=\"CONNECT\" label=\"Connect\">On</defSwitch><defSwitch name=\"DISCONNECT\" label=\"Disconnect\">Off</defSwitch></defSwitchVector>\n"
                + DefGuideVector("TELESCOPE_TIMED_GUIDE_NS", "TIMED_GUIDE_N", "TIMED_GUIDE_S")
                + DefGuideVector("TELESCOPE_TIMED_GUIDE_WE", "TIMED_GUIDE_W", "TIMED_GUIDE_E"));
            Assert.That(await Task.WhenAny(create, Task.Delay(Timeout)), Is.SameAs(create), "the telescope registers");
            var telescope = await create;
            Assert.That(telescope.GetNumberProperty("TELESCOPE_TIMED_GUIDE_WE"), Is.Not.Null, "the guide properties are defined");
            return telescope;
        }

        // Elements are processed in wire order, so once the marker property defined after the update
        // has arrived, the update itself has been applied to the cached property.
        private int markers;

        private async Task ApplyDriverUpdateAsync(INDITelescope telescope, string xml) {
            var marker = $"TEST_MARKER_{++markers}";
            main.Send(xml + $"<defTextVector device=\"{Mount}\" name=\"{marker}\" label=\"Marker\" group=\"Test\" state=\"Idle\" perm=\"ro\" timeout=\"0\">"
                + "<defText name=\"VALUE\" label=\"Value\"></defText></defTextVector>\n");
            var deadline = DateTime.UtcNow + Timeout;
            while (telescope.GetProperty(marker) == null) {
                if (DateTime.UtcNow > deadline) {
                    Assert.Fail("Timed out waiting until the driver update is applied");
                }
                await Task.Delay(10);
            }
        }

        private static string DefGuideVector(string property, string first, string second) {
            return $"<defNumberVector device=\"{Mount}\" name=\"{property}\" label=\"Guide\" group=\"Guide\" state=\"Idle\" perm=\"rw\" timeout=\"0\">"
                + $"<defNumber name=\"{first}\" label=\"{first}\" format=\"%.f\" min=\"0\" max=\"60000\" step=\"100\">0</defNumber>"
                + $"<defNumber name=\"{second}\" label=\"{second}\" format=\"%.f\" min=\"0\" max=\"60000\" step=\"100\">0</defNumber></defNumberVector>\n";
        }

        private static string SetGuideVector(string property, string state, IReadOnlyDictionary<string, double> values) {
            var numbers = string.Concat(values.Select(v => $"<oneNumber name=\"{v.Key}\">{v.Value.ToString(CultureInfo.InvariantCulture)}</oneNumber>"));
            return $"<setNumberVector device=\"{Mount}\" name=\"{property}\" state=\"{state}\" timeout=\"0\">{numbers}</setNumberVector>\n";
        }

        /// <summary>The element values of every newNumberVector the client sent for <paramref name="property"/>, in send order.</summary>
        private static List<Dictionary<string, double>> SentVectors(string received, string property) {
            return Regex.Matches(received, "<newNumberVector\\b.*?</newNumberVector>", RegexOptions.Singleline)
                .Select(m => XElement.Parse(m.Value))
                .Where(e => (string?)e.Attribute("device") == Mount && (string?)e.Attribute("name") == property)
                .Select(e => e.Elements("oneNumber").ToDictionary(
                    n => n.Attribute("name")!.Value,
                    n => double.Parse(n.Value, CultureInfo.InvariantCulture)))
                .ToList();
        }
    }
}
