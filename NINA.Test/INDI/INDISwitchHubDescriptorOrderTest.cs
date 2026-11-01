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
    /// The switch hub's channels follow the order in which the driver defined its properties. An
    /// update of a property (setSwitchVector, setNumberVector) used to move its channels to the end,
    /// so the order depended on which properties had been updated before the channel list was built.
    /// Runs an INDISwitchHub against a fake indiserver.
    /// </summary>
    [TestFixture]
    [NonParallelizable]
    public class INDISwitchHubDescriptorOrderTest {
        private const string Hub = "Pegasus UPB";
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
        public async Task UpdatesKeepTheOrderOfDefinition() {
            var create = Task.Run(() => new INDISwitchHub(new INDIDeviceInfo { Id = Hub, Name = Hub, Interface = DeviceInterface.AUX_INTERFACE }));
            // The new device asks for its properties and waits for CONNECTION.
            await main.WaitForAsync($"<getProperties version=\"1.7\" device=\"{Hub}\" />", Timeout);
            main.Send(
                $"<defSwitchVector device=\"{Hub}\" name=\"CONNECTION\" label=\"Connection\" group=\"Main Control\" state=\"Idle\" perm=\"rw\" rule=\"OneOfMany\" timeout=\"60\">"
                + "<defSwitch name=\"CONNECT\" label=\"Connect\">On</defSwitch><defSwitch name=\"DISCONNECT\" label=\"Disconnect\">Off</defSwitch></defSwitchVector>\n"
                + $"<defSwitchVector device=\"{Hub}\" name=\"POWER_CHANNELS\" label=\"Power\" group=\"Power\" state=\"Idle\" perm=\"rw\" rule=\"AnyOfMany\" timeout=\"60\">"
                + "<defSwitch name=\"POWER_CHANNEL_1\" label=\"Port 1\">Off</defSwitch><defSwitch name=\"POWER_CHANNEL_2\" label=\"Port 2\">Off</defSwitch></defSwitchVector>\n"
                + $"<defNumberVector device=\"{Hub}\" name=\"DEW_PWM\" label=\"Dew\" group=\"Dew\" state=\"Idle\" perm=\"rw\" timeout=\"60\">"
                + "<defNumber name=\"DEW_A\" label=\"Dew A\" format=\"%.f\" min=\"0\" max=\"100\" step=\"1\">0</defNumber></defNumberVector>\n");
            Assert.That(await Task.WhenAny(create, Task.Delay(Timeout)), Is.SameAs(create), "the hub registers");
            var hub = await create;
            await WaitUntil(() => hub.GetDescriptors().Count == 3, "the channels are known");

            main.Send(
                $"<setSwitchVector device=\"{Hub}\" name=\"POWER_CHANNELS\" state=\"Ok\">"
                + "<oneSwitch name=\"POWER_CHANNEL_1\">On</oneSwitch></setSwitchVector>\n");
            await WaitUntil(() => hub.GetSwitchPropertyValue("POWER_CHANNELS", "POWER_CHANNEL_1") == true, "the update arrives");

            var order = hub.GetDescriptors().Select(d => $"{d.PropertyName}.{d.ElementName}").ToArray();
            Assert.That(order, Is.EqualTo(new[] {
                "POWER_CHANNELS.POWER_CHANNEL_1",
                "POWER_CHANNELS.POWER_CHANNEL_2",
                "DEW_PWM.DEW_A",
            }));
        }

        private static async Task WaitUntil(Func<bool> condition, string because) {
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
