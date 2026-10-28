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
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Test.INDI {

    /// <summary>
    /// When the connection to indiserver ends without a Disconnect, every registered device reports itself
    /// disconnected right away, operations waiting for a driver update fail at once, and the client
    /// reconnects to the server; a device only counts as connected again once it is connected again. After
    /// three recoveries within ten minutes the client gives up. A Disconnect ends the connection on purpose
    /// and is not recovered. Runs against a fake indiserver; restarting a real indiserver is not covered here.
    /// </summary>
    [TestFixture]
    [NonParallelizable]
    public class INDIClientServerLossTest {
        private const string Focuser = "Focuser Simulator";
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
        public async Task LostConnection_MarksConnectedDevicesDisconnected_AndDropsTheirStaleProperties() {
            var focuser = await ConnectFocuserAsync();

            main.Close();

            await WaitUntilAsync(() => !focuser.Connected, "the focuser reports itself disconnected");
            Assert.That(focuser.GetSwitchProperty("CONNECTION"), Is.Null, "a stale CONNECT=On would fake a reconnect");
        }

        [Test]
        public async Task LostConnection_FailsAMoveThatWaitsForADriverUpdate() {
            var focuser = await ConnectFocuserAsync();
            var move = focuser.MoveAsync(2000);
            await main.WaitUntilAsync(
                r => r.Contains("<newNumberVector", StringComparison.Ordinal) && r.Contains("name=\"ABS_FOCUS_POSITION\"", StringComparison.Ordinal),
                "the move is sent",
                Timeout);
            Assert.That(focuser.IsMoving, Is.True);

            var watch = Stopwatch.StartNew();
            main.Close();

            Assert.That(await Task.WhenAny(move, Task.Delay(Timeout)), Is.SameAs(move), "the move ends instead of waiting up to 5 minutes for a driver update");
            Assert.That(watch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(5)));
            Assert.That(async () => await move, Throws.InstanceOf<InvalidOperationException>(), "the move failed, it did not reach its target");
            Assert.That(focuser.IsMoving, Is.False);
        }

        [Test]
        public async Task LostConnection_ReconnectsToTheServer_AndAsksForAllProperties() {
            main.Close();

            var second = await server.NextConnectionAsync(Timeout);
            await second.WaitForAsync("<getProperties version=\"1.7\" />", Timeout);

            Assert.That(await client.WaitForServerReadyAsync(Timeout), Is.True);
            await WaitUntilAsync(() => client.IsConnected, "the client is connected again");
        }

        [Test]
        public async Task ReconnectToARunningServer_KeepsTheDeviceDisconnected_UntilItIsConnectedAgain() {
            var focuser = await ConnectFocuserAsync();

            main.Close();
            var second = await server.NextConnectionAsync(Timeout);
            await second.WaitForAsync("<getProperties version=\"1.7\" />", Timeout);
            // The driver kept running, so the server reports the device connected again
            second.Send(FocuserDefinitions());
            await WaitUntilAsync(() => focuser.GetSwitchProperty("CONNECTION") != null, "CONNECTION is defined again");

            Assert.That(focuser.Connected, Is.False);
            Assert.That(focuser.ServerConnectionLost, Is.True, "the equipment layer must not count CONNECT=On of the old session as connected");

            Assert.That(await focuser.Connect(CancellationToken.None), Is.True);
            Assert.That(focuser.Connected, Is.True);
            Assert.That(focuser.ServerConnectionLost, Is.False);
        }

        [Test]
        public async Task RepeatedLosses_GiveUpAfterThreeRecoveries() {
            var connection = main;
            for (int recovery = 1; recovery <= 3; recovery++) {
                connection.Close();
                connection = await server.NextConnectionAsync(Timeout);
                Assert.That(await client.WaitForServerReadyAsync(Timeout), Is.True, $"recovery {recovery} succeeded");
                await WaitUntilAsync(() => client.IsConnected, $"the client is connected after recovery {recovery}");
            }

            connection.Close();

            Assert.That(async () => await server.NextConnectionAsync(TimeSpan.FromSeconds(3)), Throws.InstanceOf<OperationCanceledException>(),
                "no fourth recovery within ten minutes");
            Assert.That(await client.WaitForServerReadyAsync(Timeout), Is.False);
            Assert.That(client.IsConnected, Is.False);
        }

        [Test]
        public async Task Disconnect_IsNotRecovered() {
            client.Disconnect();

            Assert.That(async () => await server.NextConnectionAsync(TimeSpan.FromSeconds(2)), Throws.InstanceOf<OperationCanceledException>(),
                "no reconnect after an intentional Disconnect");
            Assert.That(client.IsConnected, Is.False);
        }

        private async Task<INDIFocuser> ConnectFocuserAsync() {
            var create = Task.Run(() => new INDIFocuser(new INDIDeviceInfo { Id = Focuser, Name = Focuser, Interface = DeviceInterface.FOCUSER_INTERFACE }));
            // The new device asks for its properties and waits for CONNECTION. The driver is already
            // connected, so Connect sends no CONNECT and needs no answer.
            await main.WaitForAsync($"<getProperties version=\"1.7\" device=\"{Focuser}\" />", Timeout);
            main.Send(FocuserDefinitions());
            Assert.That(await Task.WhenAny(create, Task.Delay(Timeout)), Is.SameAs(create), "the focuser registers");
            var focuser = await create;

            await WaitUntilAsync(() => focuser.GetNumberProperty("ABS_FOCUS_POSITION") != null, "ABS_FOCUS_POSITION is defined");
            Assert.That(await focuser.Connect(CancellationToken.None), Is.True);
            Assert.That(focuser.Connected, Is.True);
            return focuser;
        }

        private static string FocuserDefinitions() {
            return $"<defSwitchVector device=\"{Focuser}\" name=\"CONNECTION\" label=\"Connection\" group=\"Main\" state=\"Ok\" perm=\"rw\" rule=\"OneOfMany\" timeout=\"60\">"
                + "<defSwitch name=\"CONNECT\" label=\"Connect\">On</defSwitch><defSwitch name=\"DISCONNECT\" label=\"Disconnect\">Off</defSwitch></defSwitchVector>\n"
                + $"<defNumberVector device=\"{Focuser}\" name=\"ABS_FOCUS_POSITION\" label=\"Absolute Position\" group=\"Main Control\" state=\"Ok\" perm=\"rw\" timeout=\"60\">"
                + "<defNumber name=\"FOCUS_ABSOLUTE_POSITION\" label=\"Steps\" format=\"%.f\" min=\"0\" max=\"100000\" step=\"1\">1000</defNumber></defNumberVector>\n";
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
