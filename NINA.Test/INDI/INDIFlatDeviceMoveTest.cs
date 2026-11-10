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
    /// The cover moves of an INDIFlatDevice against a fake indiserver, with CAP_PARK as stock dust cap drivers publish
    /// it (indidustcapinterface.cpp): Busy while moving, the target switch with Ok at the end, the previous switch with
    /// Alert for a refused move, neither switch on for the initial definition, an abort and (Wanderer V4 EC) a cover in
    /// transit. Only an Alert or the target switch without Busy ends a commanded move.
    /// </summary>
    [TestFixture]
    [NonParallelizable]
    public class INDIFlatDeviceMoveTest {
        private const string Cover = "Flip Flat";
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
        public async Task Close_Succeeds_WhenTheDriverReportsBusyThenParked() {
            var cover = await ConnectCoverAsync(park: false, unpark: true);

            var close = cover.Close(CancellationToken.None, 10);
            await WaitForMoveCommandsAsync(1);
            main.Send(CapPark("Busy", park: true, unpark: false));
            await Task.Delay(100);
            main.Send(CapPark("Ok", park: true, unpark: false));

            Assert.That(await close, Is.True);
            Assert.That(cover.CoverState, Is.EqualTo(CoverState.Closed));
        }

        [Test]
        public async Task Close_Succeeds_WhenTheDriverAnswersAtTheTargetWithoutBusy() {
            var cover = await ConnectCoverAsync(park: false, unpark: true);

            var close = cover.Close(CancellationToken.None, 10);
            await WaitForMoveCommandsAsync(1);
            main.Send(CapPark("Ok", park: true, unpark: false));

            Assert.That(await close, Is.True);
        }

        [Test]
        public async Task Close_IgnoresTheOldEnd_ReportedBeforeOrAfterTheDriversBusy() {
            var cover = await ConnectCoverAsync(park: false, unpark: true);

            var close = cover.Close(CancellationToken.None, 10);
            await WaitForMoveCommandsAsync(1);
            main.Send(CapPark("Ok", park: false, unpark: true));
            await Task.Delay(200);
            Assert.That(close.IsCompleted, Is.False, "a stale update with the old end does not end the move");

            main.Send(CapPark("Busy", park: true, unpark: false));
            main.Send(CapPark("Ok", park: false, unpark: true));
            await Task.Delay(200);
            Assert.That(close.IsCompleted, Is.False, "nor does the old end after the Busy (a cover still within reach of it)");
            Assert.That(cover.CoverState, Is.EqualTo(CoverState.NeitherOpenNorClosed));

            main.Send(CapPark("Ok", park: true, unpark: false));

            Assert.That(await close, Is.True);
            Assert.That(cover.CoverState, Is.EqualTo(CoverState.Closed));
        }

        [Test]
        public async Task Open_WaitsWhileTheCoverIsInTransitWithNeitherSwitchOn() {
            // Wanderer V4 EC: Idle with both switches off between the ends
            var cover = await ConnectCoverAsync(park: true, unpark: false);

            var open = cover.Open(CancellationToken.None, 10);
            await WaitForMoveCommandsAsync(1);
            main.Send(CapPark("Busy", park: false, unpark: true));
            main.Send(CapPark("Idle", park: false, unpark: false));
            await Task.Delay(200);
            Assert.That(open.IsCompleted, Is.False, "neither switch on is not the open end");

            main.Send(CapPark("Ok", park: false, unpark: true));

            Assert.That(await open, Is.True);
            Assert.That(cover.CoverState, Is.EqualTo(CoverState.Open));
        }

        [Test]
        public async Task Close_RefusedWithAlert_Throws_AndTheCoverStaysAtTheOldEnd() {
            var cover = await ConnectCoverAsync(park: false, unpark: true);

            var close = cover.Close(CancellationToken.None, 10);
            await WaitForMoveCommandsAsync(1);
            // the dust cap interface restores the previous switch on a refused move
            main.Send(CapPark("Alert", park: false, unpark: true));

            Assert.That(async () => await close, Throws.InvalidOperationException.With.Message.Contains("Alert"));
            Assert.That(cover.CoverState, Is.EqualTo(CoverState.Open));
        }

        [Test]
        public async Task Close_AbortedWithAlertAndNeitherSwitchOn_Throws_AndTheCoverIsUnknown() {
            var cover = await ConnectCoverAsync(park: false, unpark: true);

            var close = cover.Close(CancellationToken.None, 10);
            await WaitForMoveCommandsAsync(1);
            main.Send(CapPark("Busy", park: true, unpark: false));
            main.Send(CapPark("Alert", park: false, unpark: false));

            Assert.That(async () => await close, Throws.InvalidOperationException);
            Assert.That(cover.CoverState, Is.EqualTo(CoverState.Unknown));
        }

        [Test]
        public async Task Close_TimesOut_ReturnsFalse_AndTheCoverIsUnknown() {
            // not Closed, although SetSwitchValue wrote PARK=On into the local CAP_PARK before sending it
            var cover = await ConnectCoverAsync(park: false, unpark: true);

            var close = cover.Close(CancellationToken.None, 10);
            await WaitForMoveCommandsAsync(1);

            Assert.That(await close, Is.False);
            Assert.That(cover.CoverState, Is.EqualTo(CoverState.Unknown));
        }

        [Test]
        public async Task AnOpenDuringAClose_SupersedesIt_AndOnlyTheOpenReportsTheDriversAnswer() {
            // FlatDeviceVM lets an Open (a client's) and a Close (a sequence's) overlap
            var cover = await ConnectCoverAsync(park: false, unpark: true);
            cover.MoveTimeout = TimeSpan.FromSeconds(30);

            var close = cover.Close(CancellationToken.None, 10);
            await WaitForMoveCommandsAsync(1);
            var open = cover.Open(CancellationToken.None, 10);
            await WaitForMoveCommandsAsync(2);

            Assert.That(await close, Is.False, "the superseded close did not close the cover");

            main.Send(CapPark("Ok", park: false, unpark: true));

            Assert.That(await open, Is.True);
            Assert.That(cover.CoverState, Is.EqualTo(CoverState.Open));
        }

        [Test]
        public async Task AnOpenSupersededByAClose_DoesNotTakeTheClosesAnswerForItsOwn() {
            var cover = await ConnectCoverAsync(park: true, unpark: false);
            cover.MoveTimeout = TimeSpan.FromSeconds(30);

            var open = cover.Open(CancellationToken.None, 10);
            await WaitForMoveCommandsAsync(1);
            var close = cover.Close(CancellationToken.None, 10);
            await WaitForMoveCommandsAsync(2);
            main.Send(CapPark("Ok", park: true, unpark: false));

            Assert.That(await open, Is.False, "the cover is closed, the open did not succeed");
            Assert.That(await close, Is.True);
        }

        [Test]
        public async Task ASecondCloseDuringAClose_JoinsIt_AndBothReportTheDriversAnswer() {
            // a client's Close during a sequence's: the sequence's must not fail as superseded
            var cover = await ConnectCoverAsync(park: false, unpark: true);
            cover.MoveTimeout = TimeSpan.FromSeconds(30);

            var first = cover.Close(CancellationToken.None, 10);
            await WaitForMoveCommandsAsync(1);
            var second = cover.Close(CancellationToken.None, 10);
            await Task.Delay(200);
            main.Send(CapPark("Ok", park: true, unpark: false));

            Assert.That(await first, Is.True);
            Assert.That(await second, Is.True);
            Assert.That(CountMoveCommands(main.Received), Is.EqualTo(1), "the second close is not sent again");
        }

        [Test]
        public async Task ATimeoutAfterTheDriverReportedTheOldEnd_ShowsThatEnd() {
            // Wanderer V4 EC sends only changes: a cover whose motor never moved keeps showing where the driver said
            var cover = await ConnectCoverAsync(park: false, unpark: true);

            var close = cover.Close(CancellationToken.None, 10);
            await WaitForMoveCommandsAsync(1);
            main.Send(CapPark("Busy", park: true, unpark: false));
            main.Send(CapPark("Ok", park: false, unpark: true));

            Assert.That(await close, Is.False);
            Assert.That(cover.CoverState, Is.EqualTo(CoverState.Open));
        }

        [Test]
        public async Task ALostServerConnection_EndsTheMove() {
            var cover = await ConnectCoverAsync(park: false, unpark: true);
            cover.MoveTimeout = TimeSpan.FromSeconds(30);

            var close = cover.Close(CancellationToken.None, 10);
            await WaitForMoveCommandsAsync(1);
            main.Close();

            Assert.That(await Task.WhenAny(close, Task.Delay(Timeout)), Is.SameAs(close), "the move ends instead of waiting for its timeout");
            Assert.That(await close, Is.False);
            // the lost connection drops the driver's properties, CAP_PARK with them
            Assert.That(cover.CoverState, Is.EqualTo(CoverState.NotPresent));
        }

        [Test]
        public async Task Close_Cancelled_LeavesTheCoverUnknown_UntilTheDriverReportsIt() {
            var cover = await ConnectCoverAsync(park: false, unpark: true);
            using var cts = new CancellationTokenSource();

            var close = cover.Close(cts.Token, 10);
            await WaitForMoveCommandsAsync(1);
            cts.Cancel();

            Assert.That(async () => await close, Throws.InstanceOf<OperationCanceledException>());
            Assert.That(cover.CoverState, Is.EqualTo(CoverState.Unknown));

            main.Send(CapPark("Ok", park: true, unpark: false));
            await WaitUntilAsync(() => cover.CoverState == CoverState.Closed, "the driver's update sets the state");
        }

        [Test]
        public async Task TheInitialDefinitionWithNeitherSwitchOn_IsUnknown() {
            var cover = await ConnectCoverAsync(park: false, unpark: false, state: "Idle");

            Assert.That(cover.CoverState, Is.EqualTo(CoverState.Unknown));
        }

        [Test]
        public async Task AMoveFromTheControlPanel_IsNeitherOpenNorClosedWhileBusy() {
            var cover = await ConnectCoverAsync(park: true, unpark: false);

            main.Send(CapPark("Busy", park: false, unpark: true));
            await WaitUntilAsync(() => cover.CoverState == CoverState.NeitherOpenNorClosed, "Busy is a move");
            main.Send(CapPark("Ok", park: false, unpark: true));
            await WaitUntilAsync(() => cover.CoverState == CoverState.Open, "the move ended open");
        }

        [Test]
        public async Task AnAlertAtConnect_ShowsTheEndTheSwitchSays_AndOpenIsStillSent() {
            // a refused move before NINA connected: the Alert stays in the driver until the next command
            var cover = await ConnectCoverAsync(park: true, unpark: false, state: "Alert");
            Assert.That(cover.CoverState, Is.EqualTo(CoverState.Closed));

            var open = cover.Open(CancellationToken.None, 10);
            await WaitForMoveCommandsAsync(1);
            main.Send(CapPark("Ok", park: false, unpark: true));

            Assert.That(await open, Is.True);
        }

        private async Task<INDIFlatDevice> ConnectCoverAsync(bool park, bool unpark, string state = "Ok") {
            var create = Task.Run(() => new INDIFlatDevice(new INDIDeviceInfo {
                Id = Cover,
                Name = Cover,
                Interface = DeviceInterface.DUSTCAP_INTERFACE | DeviceInterface.LIGHTBOX_INTERFACE
            }));
            // The new device asks for its properties and waits for CONNECTION. The driver is already
            // connected, so Connect sends no CONNECT and needs no answer.
            await main.WaitForAsync($"<getProperties version=\"1.7\" device=\"{Cover}\" />", Timeout);
            main.Send(
                $"<defSwitchVector device=\"{Cover}\" name=\"CONNECTION\" label=\"Connection\" group=\"Main\" state=\"Ok\" perm=\"rw\" rule=\"OneOfMany\" timeout=\"60\">"
                + "<defSwitch name=\"CONNECT\" label=\"Connect\">On</defSwitch><defSwitch name=\"DISCONNECT\" label=\"Disconnect\">Off</defSwitch></defSwitchVector>\n"
                + $"<defNumberVector device=\"{Cover}\" name=\"FLAT_LIGHT_INTENSITY\" label=\"Brightness\" group=\"Main\" state=\"Ok\" perm=\"rw\" timeout=\"60\">"
                + "<defNumber name=\"FLAT_LIGHT_INTENSITY_VALUE\" label=\"Brightness\" format=\"%3.0f\" min=\"0\" max=\"255\" step=\"1\">0</defNumber></defNumberVector>\n"
                + $"<defSwitchVector device=\"{Cover}\" name=\"CAP_PARK\" label=\"Dust Cover\" group=\"Main\" state=\"{state}\" perm=\"rw\" rule=\"OneOfMany\" timeout=\"60\">"
                + $"<defSwitch name=\"PARK\" label=\"Park\">{OnOff(park)}</defSwitch><defSwitch name=\"UNPARK\" label=\"Unpark\">{OnOff(unpark)}</defSwitch></defSwitchVector>\n");
            Assert.That(await Task.WhenAny(create, Task.Delay(Timeout)), Is.SameAs(create), "the flat device registers");
            var cover = await create;

            await WaitUntilAsync(() => cover.GetSwitchProperty("CAP_PARK") != null, "CAP_PARK is defined");
            Assert.That(await cover.Connect(CancellationToken.None), Is.True);

            cover.MoveTimeout = MoveTimeout;
            return cover;
        }

        private Task WaitForMoveCommandsAsync(int count) {
            return main.WaitUntilAsync(
                r => CountMoveCommands(r) >= count,
                $"{count} cover command(s) are sent",
                Timeout);
        }

        // The client sends CAP_PARK in nothing but a cover move
        private static int CountMoveCommands(string received) {
            var count = 0;
            var index = 0;
            while ((index = received.IndexOf("name=\"CAP_PARK\"", index, StringComparison.Ordinal)) >= 0) {
                count++;
                index++;
            }

            return count;
        }

        private static string OnOff(bool on) => on ? "On" : "Off";

        private static string CapPark(string state, bool park, bool unpark) {
            return $"<setSwitchVector device=\"{Cover}\" name=\"CAP_PARK\" state=\"{state}\" timeout=\"60\">"
                + $"<oneSwitch name=\"PARK\">{OnOff(park)}</oneSwitch><oneSwitch name=\"UNPARK\">{OnOff(unpark)}</oneSwitch></setSwitchVector>\n";
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
