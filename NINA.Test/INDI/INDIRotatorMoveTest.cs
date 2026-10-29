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
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Test.INDI {

    /// <summary>
    /// How INDIRotator.MoveAsync ends against a fake indiserver: at the driver's answer, never at a stale update that
    /// crossed the command, and with a failure (not a silent return RotatorVM would wait behind) when no answer ends it.
    /// </summary>
    [TestFixture]
    [NonParallelizable]
    public class INDIRotatorMoveTest {
        private const string Rotator = "Rotator Simulator";
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
            // Keep the disposed test client rather than restoring null, as in INDIFilterWheelMoveTest
            INDIClient.SetInstanceForTests(previousInstance ?? client);
            server.Dispose();
        }

        [Test]
        public async Task Move_Succeeds_AfterAStaleUpdate_WhenTheDriverReportsBusyAndTheTarget() {
            var rotator = await ConnectRotatorAsync();

            var move = rotator.MoveAsync(90);
            await WaitForMoveCommandsAsync(1);
            main.Send(SetAngleVector("Ok", 10));
            await Task.Delay(300);
            Assert.That(move.IsCompleted, Is.False, "the stale update does not end the move");

            main.Send(SetAngleVector("Busy", 50));
            await Task.Delay(100);
            main.Send(SetAngleVector("Ok", 90));

            Assert.That(async () => await move, Throws.Nothing);
            Assert.That(rotator.Position, Is.EqualTo(90).Within(0.01));
        }

        [Test]
        public async Task Move_Ends_AtTheDriversSecondAnswerAwayFromTheTarget() {
            var rotator = await ConnectRotatorAsync();

            // a driver that limits the target without an Alert, after a stale update crossed the command
            var move = rotator.MoveAsync(90);
            await WaitForMoveCommandsAsync(1);
            main.Send(SetAngleVector("Ok", 10));
            main.Send(SetAngleVector("Ok", 45));

            Assert.That(await Task.WhenAny(move, Task.Delay(MoveTimeout / 2)), Is.SameAs(move), "the move ends before its timeout");
            Assert.That(rotator.Position, Is.EqualTo(45).Within(0.01), "the rotator stays where the driver says it is");
        }

        [Test]
        public async Task Move_Fails_WhenNoAnswerEndsIt() {
            var rotator = await ConnectRotatorAsync();

            var move = rotator.MoveAsync(90);
            await WaitForMoveCommandsAsync(1);
            main.Send(SetAngleVector("Busy", 40));

            Assert.That(async () => await move, Throws.TypeOf<TimeoutException>());
            Assert.That(rotator.IsMoving, Is.False);
        }

        private async Task<INDIRotator> ConnectRotatorAsync() {
            var create = Task.Run(() => new INDIRotator(new INDIDeviceInfo { Id = Rotator, Name = Rotator, Interface = DeviceInterface.ROTATOR_INTERFACE }));
            // The new device asks for its properties and waits for CONNECTION. The driver is already
            // connected, so Connect sends no CONNECT and needs no answer.
            await main.WaitForAsync($"<getProperties version=\"1.7\" device=\"{Rotator}\" />", Timeout);
            main.Send(
                $"<defSwitchVector device=\"{Rotator}\" name=\"CONNECTION\" label=\"Connection\" group=\"Main\" state=\"Ok\" perm=\"rw\" rule=\"OneOfMany\" timeout=\"60\">"
                + "<defSwitch name=\"CONNECT\" label=\"Connect\">On</defSwitch><defSwitch name=\"DISCONNECT\" label=\"Disconnect\">Off</defSwitch></defSwitchVector>\n"
                + $"<defNumberVector device=\"{Rotator}\" name=\"ABS_ROTATOR_ANGLE\" label=\"Goto\" group=\"Main\" state=\"Ok\" perm=\"rw\" timeout=\"0\">"
                + "<defNumber name=\"ANGLE\" label=\"Angle\" format=\"%.2f\" min=\"0\" max=\"360\" step=\"10\">10</defNumber></defNumberVector>\n");
            Assert.That(await Task.WhenAny(create, Task.Delay(Timeout)), Is.SameAs(create), "the rotator registers");
            var rotator = await create;

            await WaitUntilAsync(() => rotator.GetNumberProperty("ABS_ROTATOR_ANGLE") != null, "ABS_ROTATOR_ANGLE is defined");
            Assert.That(await rotator.Connect(CancellationToken.None), Is.True);
            Assert.That(rotator.Position, Is.EqualTo(10).Within(0.01));

            rotator.MoveTimeout = MoveTimeout;
            return rotator;
        }

        private Task WaitForMoveCommandsAsync(int count) {
            return main.WaitUntilAsync(
                r => CountMoveCommands(r) >= count,
                $"{count} rotator move command(s) are sent",
                Timeout);
        }

        // The client sends ABS_ROTATOR_ANGLE in nothing but a move
        private static int CountMoveCommands(string received) {
            var count = 0;
            var index = 0;
            while ((index = received.IndexOf("name=\"ABS_ROTATOR_ANGLE\"", index, StringComparison.Ordinal)) >= 0) {
                count++;
                index++;
            }

            return count;
        }

        private static string SetAngleVector(string state, double angle) {
            return $"<setNumberVector device=\"{Rotator}\" name=\"ABS_ROTATOR_ANGLE\" state=\"{state}\" timeout=\"0\">"
                + $"<oneNumber name=\"ANGLE\">{angle.ToString(CultureInfo.InvariantCulture)}</oneNumber></setNumberVector>\n";
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
