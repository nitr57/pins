#region "copyright"

/*
    Copyright © 2026 Nico Trost <nico.trost57@gmail.com> and the PI.N.S. contributors

    This file is part of PI 'N' Stars.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.INDI.Devices;
using NINA.INDI.Enums;
using NINA.INDI.Protocol;
using NUnit.Framework;

namespace NINA.Test.INDI {

    [TestFixture]
    public class GotoAcknowledgementTest {
        private const string Device = "10micron";
        private const double TargetRa = 4.250532298376259;
        private const double TargetDec = 65.33879066134072;
        private const string PreSend = "2026-09-14T19:28:44";

        private static GotoAcknowledgement NewAcknowledgement(string preSendTimestamp = PreSend) {
            return new GotoAcknowledgement(Device, TargetRa, TargetDec, preSendTimestamp);
        }

        private static INDINumberProperty Coordinates(PropertyState state, string timestamp, double ra = 2.196, double dec = 77.92) {
            return new INDINumberProperty {
                Name = "EQUATORIAL_EOD_COORD",
                State = state,
                Timestamp = timestamp,
                Numbers = { new INDINumber { Name = "RA", Value = ra }, new INDINumber { Name = "DEC", Value = dec } }
            };
        }

        private static INDINumberProperty Target(double ra, double dec, string timestamp = "2026-09-14T19:28:46") {
            return new INDINumberProperty {
                Name = "TARGET_EOD_COORD",
                State = PropertyState.Idle,
                Timestamp = timestamp,
                Numbers = { new INDINumber { Name = "RA", Value = ra }, new INDINumber { Name = "DEC", Value = dec } }
            };
        }

        [Test]
        public void StatusPollOkBeforeTheReply_IsNotTakenAsAcknowledgement() {
            // The 10micron failure: a tracking update with a new timestamp arrives before the driver
            // has processed the goto. The generic SetNumberValuesAsync rule accepted exactly this.
            var ack = NewAcknowledgement();

            ack.Observe(Coordinates(PropertyState.Ok, "2026-09-14T19:28:46"));

            Assert.That(ack.Completion.IsCompleted, Is.False);
            Assert.That(ack.IgnoredStatusUpdates, Is.EqualTo(1));
        }

        [Test]
        public void StatusPollOkThenBusy_AcknowledgesViaBusy() {
            var ack = NewAcknowledgement();

            ack.Observe(Coordinates(PropertyState.Ok, "2026-09-14T19:28:46"));
            ack.Observe(Coordinates(PropertyState.Busy, "2026-09-14T19:28:46"));

            Assert.That(ack.Completion.IsCompleted, Is.True);
            Assert.That(ack.Completion.Result, Is.EqualTo(GotoAcknowledgementKind.Busy));
            Assert.That(ack.IgnoredStatusUpdates, Is.EqualTo(1));
        }

        [Test]
        public void BaseDriverSequence_TargetThenBusy_AcknowledgesViaBusy() {
            // INDI::Telescope::ISNewNumber applies TARGET_EOD_COORD, then EQUATORIAL_EOD_COORD Busy.
            var ack = NewAcknowledgement();

            ack.Observe(Target(TargetRa, TargetDec));
            Assert.That(ack.TargetEchoed, Is.True);
            Assert.That(ack.Completion.IsCompleted, Is.False);

            ack.Observe(Coordinates(PropertyState.Busy, "2026-09-14T19:28:46"));

            Assert.That(ack.Completion.Result, Is.EqualTo(GotoAcknowledgementKind.Busy));
        }

        [Test]
        public void OnStepSequence_TargetThenOk_AcknowledgesViaTargetThenUpdate() {
            // OnStep echoes TARGET_EOD_COORD without Busy; its next status poll may report Ok.
            var ack = NewAcknowledgement();

            ack.Observe(Target(TargetRa, TargetDec));
            ack.Observe(Coordinates(PropertyState.Ok, "2026-09-14T19:28:47"));

            Assert.That(ack.Completion.Result, Is.EqualTo(GotoAcknowledgementKind.TargetThenUpdate));
        }

        [Test]
        public void OnStepSequence_TargetThenBusy_AcknowledgesViaBusy() {
            var ack = NewAcknowledgement();

            ack.Observe(Target(TargetRa, TargetDec));
            ack.Observe(Coordinates(PropertyState.Busy, "2026-09-14T19:28:47"));

            Assert.That(ack.Completion.Result, Is.EqualTo(GotoAcknowledgementKind.Busy));
        }

        [Test]
        public void TargetWithOtherCoordinates_IsNotAnEcho() {
            // Stopping a manual move applies TARGET_EOD_COORD with the current position, not the goto target.
            var ack = NewAcknowledgement();

            ack.Observe(Target(TargetRa + 0.1, TargetDec - 1.0));
            ack.Observe(Coordinates(PropertyState.Ok, "2026-09-14T19:28:46"));

            Assert.That(ack.TargetEchoed, Is.False);
            Assert.That(ack.Completion.IsCompleted, Is.False);
            Assert.That(ack.IgnoredStatusUpdates, Is.EqualTo(1));
        }

        [Test]
        public void TargetEchoWithFormattingNoise_Matches() {
            var ack = NewAcknowledgement();

            ack.Observe(Target(TargetRa + 1e-9, TargetDec - 1e-9));

            Assert.That(ack.TargetEchoed, Is.True);
        }

        [Test]
        public void TargetJustOutsideOneArcsecond_DoesNotMatch() {
            var ack = NewAcknowledgement();

            ack.Observe(Target(TargetRa, TargetDec + 2.0 / 3600.0));

            Assert.That(ack.TargetEchoed, Is.False);
        }

        [Test]
        public void TargetEchoAcrossTheRaWrap_Matches() {
            var ack = new GotoAcknowledgement(Device, 23.9999999, 10.0, PreSend);

            ack.Observe(Target(0.0000001, 10.0));

            Assert.That(ack.TargetEchoed, Is.True);
        }

        [Test]
        public void AlertWithNewTimestamp_IsRejected() {
            var ack = NewAcknowledgement();

            ack.Observe(Coordinates(PropertyState.Alert, "2026-09-14T19:28:45"));

            Assert.That(ack.Completion.Result, Is.EqualTo(GotoAcknowledgementKind.Rejected));
        }

        [Test]
        public void AlertWithPreSendTimestamp_IsStaleAndIgnored() {
            var ack = NewAcknowledgement();

            ack.Observe(Coordinates(PropertyState.Alert, PreSend));

            Assert.That(ack.Completion.IsCompleted, Is.False);
            Assert.That(ack.IgnoredStatusUpdates, Is.EqualTo(1));
        }

        [Test]
        public void StaleAlertAfterTargetEcho_IsNotTakenAsTheUpdateAfterTheEcho() {
            var ack = NewAcknowledgement();

            ack.Observe(Target(TargetRa, TargetDec));
            ack.Observe(Coordinates(PropertyState.Alert, PreSend));

            Assert.That(ack.Completion.IsCompleted, Is.False);
        }

        [Test]
        public void AlertWithoutPreSendTimestamp_IsRejected() {
            var ack = NewAcknowledgement(preSendTimestamp: string.Empty);

            ack.Observe(Coordinates(PropertyState.Alert, "2026-09-14T19:28:45"));

            Assert.That(ack.Completion.Result, Is.EqualTo(GotoAcknowledgementKind.Rejected));
        }

        [Test]
        public void IdleUpdate_IsIgnored() {
            // Sent by the base class when a goto aborts a running slew; not a reply on its own.
            var ack = NewAcknowledgement();

            ack.Observe(Coordinates(PropertyState.Idle, "2026-09-14T19:28:46"));

            Assert.That(ack.Completion.IsCompleted, Is.False);
            Assert.That(ack.IgnoredStatusUpdates, Is.EqualTo(1));
        }

        [Test]
        public void FirstReplyWins() {
            var ack = NewAcknowledgement();

            ack.Observe(Coordinates(PropertyState.Busy, "2026-09-14T19:28:46"));
            ack.Observe(Coordinates(PropertyState.Alert, "2026-09-14T19:28:47"));
            ack.Observe(Coordinates(PropertyState.Ok, "2026-09-14T19:28:58"));

            Assert.That(ack.Completion.Result, Is.EqualTo(GotoAcknowledgementKind.Busy));
            Assert.That(ack.IgnoredStatusUpdates, Is.EqualTo(0));
        }

        [Test]
        public void OtherProperties_AreIgnored() {
            var ack = NewAcknowledgement();

            ack.Observe(new INDINumberProperty { Name = "HORIZONTAL_COORD", State = PropertyState.Busy, Timestamp = "2026-09-14T19:28:46" });
            ack.Observe(null!);

            Assert.That(ack.Completion.IsCompleted, Is.False);
            Assert.That(ack.IgnoredStatusUpdates, Is.EqualTo(0));
        }
    }
}
