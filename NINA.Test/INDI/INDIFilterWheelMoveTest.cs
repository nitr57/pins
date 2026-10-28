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
    /// Sending a filter change writes the target into the local FILTER_SLOT_VALUE, so a move the driver
    /// never answers must not leave the position at the target: FilterWheelVM's position check would then
    /// report the change as done and skip a retry to the same filter. Runs an INDIFilterWheel against a
    /// fake indiserver.
    /// </summary>
    [TestFixture]
    [NonParallelizable]
    public class INDIFilterWheelMoveTest {
        private const string Wheel = "Filter Simulator";
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan MoveTimeout = TimeSpan.FromSeconds(1);

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
        public async Task Move_Fails_AndKeepsTheOldPosition_WhenTheDriverNeverAnswers() {
            var wheel = await ConnectWheelAsync();

            var move = wheel.MoveToPositionAsync(2);
            await WaitForMoveCommandsAsync(1);

            Assert.That(async () => await move, Throws.TypeOf<TimeoutException>());
            Assert.That(wheel.Position, Is.EqualTo(0), "the target that was sent is not the wheel's position");
            Assert.That(wheel.IsMoving, Is.False);
        }

        [Test]
        public async Task Move_CanBeRetried_AfterTheDriverNeverAnswered() {
            var wheel = await ConnectWheelAsync();
            var move = wheel.MoveToPositionAsync(2);
            await WaitForMoveCommandsAsync(1);
            Assert.That(async () => await move, Throws.TypeOf<TimeoutException>());

            var retry = wheel.MoveToPositionAsync(2);
            await WaitForMoveCommandsAsync(2);
            main.Send(SetSlotVector("Busy", 1));
            await Task.Delay(200);
            main.Send(SetSlotVector("Ok", 3));

            Assert.That(async () => await retry, Throws.Nothing);
            Assert.That(wheel.Position, Is.EqualTo(2));
        }

        [Test]
        public async Task Move_Fails_AndKeepsTheOldPosition_WhenTheDriverStopsAfterReportingBusy() {
            var wheel = await ConnectWheelAsync();

            // libindi answers a filter change with Busy and the old slot, and only reports the new slot when done
            var move = wheel.MoveToPositionAsync(2);
            await WaitForMoveCommandsAsync(1);
            main.Send(SetSlotVector("Busy", 1));

            Assert.That(async () => await move, Throws.TypeOf<TimeoutException>());
            Assert.That(wheel.Position, Is.EqualTo(0));
        }

        [Test]
        public async Task Move_Succeeds_WhenTheDriverReportsTheNewSlot() {
            var wheel = await ConnectWheelAsync();

            var move = wheel.MoveToPositionAsync(2);
            await WaitForMoveCommandsAsync(1);
            main.Send(SetSlotVector("Busy", 1));
            await Task.Delay(200);
            main.Send(SetSlotVector("Ok", 3));

            Assert.That(async () => await move, Throws.Nothing);
            Assert.That(wheel.Position, Is.EqualTo(2));
        }

        [Test]
        public async Task AbandonMove_KeepsTheDriversPosition_OnceTheDriverAnswered() {
            var wheel = await ConnectWheelAsync();

            // A driver that reports the slot it is passing while still moving
            wheel.Position = 3;
            await WaitForMoveCommandsAsync(1);
            main.Send(SetSlotVector("Busy", 3));
            await WaitUntilAsync(() => wheel.Position == 2, "the driver's Busy update is applied");

            wheel.AbandonMove();

            Assert.That(wheel.Position, Is.EqualTo(2), "the driver's value is kept");
            Assert.That(wheel.IsMoving, Is.False);
        }

        private async Task<INDIFilterWheel> ConnectWheelAsync() {
            var create = Task.Run(() => new INDIFilterWheel(new INDIDeviceInfo { Id = Wheel, Name = Wheel, Interface = DeviceInterface.FILTER_INTERFACE }));
            // The new device asks for its properties and waits for CONNECTION. The driver is already
            // connected, so Connect sends no CONNECT and needs no answer.
            await main.WaitForAsync($"<getProperties version=\"1.7\" device=\"{Wheel}\" />", Timeout);
            main.Send(
                $"<defSwitchVector device=\"{Wheel}\" name=\"CONNECTION\" label=\"Connection\" group=\"Main\" state=\"Ok\" perm=\"rw\" rule=\"OneOfMany\" timeout=\"60\">"
                + "<defSwitch name=\"CONNECT\" label=\"Connect\">On</defSwitch><defSwitch name=\"DISCONNECT\" label=\"Disconnect\">Off</defSwitch></defSwitchVector>\n"
                + $"<defNumberVector device=\"{Wheel}\" name=\"FILTER_SLOT\" label=\"Filter Slot\" group=\"Filter Wheel\" state=\"Ok\" perm=\"rw\" timeout=\"60\">"
                + "<defNumber name=\"FILTER_SLOT_VALUE\" label=\"Filter\" format=\"%3.0f\" min=\"1\" max=\"5\" step=\"1\">1</defNumber></defNumberVector>\n");
            Assert.That(await Task.WhenAny(create, Task.Delay(Timeout)), Is.SameAs(create), "the filter wheel registers");
            var wheel = await create;

            await WaitUntilAsync(() => wheel.GetNumberProperty("FILTER_SLOT") != null, "FILTER_SLOT is defined");
            Assert.That(await wheel.Connect(CancellationToken.None), Is.True);
            Assert.That(wheel.Position, Is.EqualTo(0));

            wheel.MoveTimeout = MoveTimeout;
            return wheel;
        }

        private Task WaitForMoveCommandsAsync(int count) {
            return main.WaitUntilAsync(
                r => CountMoveCommands(r) >= count,
                $"{count} filter change command(s) are sent",
                Timeout);
        }

        // The client sends FILTER_SLOT in nothing but a filter change
        private static int CountMoveCommands(string received) {
            var count = 0;
            var index = 0;
            while ((index = received.IndexOf("name=\"FILTER_SLOT\"", index, StringComparison.Ordinal)) >= 0) {
                count++;
                index++;
            }

            return count;
        }

        private static string SetSlotVector(string state, int slot) {
            return $"<setNumberVector device=\"{Wheel}\" name=\"FILTER_SLOT\" state=\"{state}\" timeout=\"60\">"
                + $"<oneNumber name=\"FILTER_SLOT_VALUE\">{slot}</oneNumber></setNumberVector>\n";
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
