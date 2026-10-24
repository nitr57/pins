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
using System;
using System.Collections.Generic;

namespace NINA.Test.INDI {

    [TestFixture]
    public class PulseGuideTrackerTest {
        private const string NS = PulseGuideTracker.NorthSouth;
        private const string WE = PulseGuideTracker.WestEast;
        private static readonly TimeSpan Margin = PulseGuideTracker.CompletionMargin;

        private DateTime now;
        private PulseGuideTracker tracker = null!;
        private List<(string Property, int DurationMs)> timeouts = null!;

        [SetUp]
        public void SetUp() {
            now = new DateTime(2026, 10, 8, 22, 0, 0, DateTimeKind.Utc);
            tracker = new PulseGuideTracker(() => now);
            timeouts = [];
            tracker.TimedOut += (property, durationMs) => timeouts.Add((property, durationMs));
        }

        private static INDINumberProperty GuideVector(string name, PropertyState state, double first = 0, double second = 0) {
            var (a, b) = name == NS ? ("TIMED_GUIDE_N", "TIMED_GUIDE_S") : ("TIMED_GUIDE_W", "TIMED_GUIDE_E");
            return new INDINumberProperty {
                Name = name,
                State = state,
                Numbers = { new INDINumber { Name = a, Value = first }, new INDINumber { Name = b, Value = second } }
            };
        }

        [Test]
        public void SentPulse_IsGuiding_BeforeTheDriverReplies() {
            tracker.PulseSending(NS, 300);

            Assert.That(tracker.IsPulseGuiding, Is.True);
        }

        [TestCase(PropertyState.Ok)]
        [TestCase(PropertyState.Idle)]
        [TestCase(PropertyState.Alert)]
        public void Busy_KeepsGuiding_UntilTheFirstUpdateThatIsNotBusy(PropertyState end) {
            tracker.PulseSending(NS, 300);
            tracker.Observe(GuideVector(NS, PropertyState.Busy, 300));
            now += TimeSpan.FromMilliseconds(200);
            Assert.That(tracker.IsPulseGuiding, Is.True);

            tracker.Observe(GuideVector(NS, end, 300));

            Assert.That(tracker.IsPulseGuiding, Is.False);
            Assert.That(timeouts, Is.Empty);
        }

        [Test]
        public void DriverAnsweringOkWithoutBusy_EndsThePulse() {
            tracker.PulseSending(WE, 300);

            tracker.Observe(GuideVector(WE, PropertyState.Ok, 300));

            Assert.That(tracker.IsPulseGuiding, Is.False);
        }

        [Test]
        public void UpdateOfTheOtherAxis_DoesNotEndThePulse() {
            tracker.PulseSending(NS, 300);

            tracker.Observe(GuideVector(WE, PropertyState.Idle));

            Assert.That(tracker.IsPulseGuiding, Is.True);
        }

        [Test]
        public void NoReply_EndsAtPulseLengthPlusMargin_AndReportsATimeout() {
            tracker.PulseSending(NS, 300);

            now += TimeSpan.FromMilliseconds(300) + Margin - TimeSpan.FromMilliseconds(1);
            Assert.That(tracker.IsPulseGuiding, Is.True);
            Assert.That(timeouts, Is.Empty);

            now += TimeSpan.FromMilliseconds(1);
            Assert.That(tracker.IsPulseGuiding, Is.False);
            Assert.That(timeouts, Is.EqualTo(new[] { (NS, 300) }));

            Assert.That(tracker.IsPulseGuiding, Is.False);
            Assert.That(timeouts, Has.Count.EqualTo(1), "a timeout is reported once");
        }

        [Test]
        public void StuckBusy_EndsAtTheDeadline() {
            tracker.PulseSending(WE, 200);
            tracker.Observe(GuideVector(WE, PropertyState.Busy, 200));

            now += TimeSpan.FromMilliseconds(200) + Margin;

            Assert.That(tracker.IsPulseGuiding, Is.False);
            Assert.That(timeouts, Is.EqualTo(new[] { (WE, 200) }));
        }

        [Test]
        public void LateBusy_ExtendsTheDeadline_ForADriverThatStartedThePulseLate() {
            tracker.PulseSending(NS, 1000);
            now += TimeSpan.FromMilliseconds(800);
            tracker.Observe(GuideVector(NS, PropertyState.Busy, 1000));

            now += TimeSpan.FromMilliseconds(1000) + Margin - TimeSpan.FromMilliseconds(1);

            Assert.That(tracker.IsPulseGuiding, Is.True);
        }

        [Test]
        public void BusyFromTheDriver_WithoutAPulseSentHere_CountsAsGuiding() {
            tracker.Observe(GuideVector(NS, PropertyState.Busy, 0, 400));

            Assert.That(tracker.IsPulseGuiding, Is.True);
            now += TimeSpan.FromMilliseconds(400) + Margin;
            Assert.That(tracker.IsPulseGuiding, Is.False);
        }

        [Test]
        public void PulseNotSent_IsNotGuiding() {
            tracker.PulseSending(NS, 300);

            tracker.PulseNotSent(NS);

            Assert.That(tracker.IsPulseGuiding, Is.False);
            now += TimeSpan.FromSeconds(5);
            Assert.That(tracker.IsPulseGuiding, Is.False);
            Assert.That(timeouts, Is.Empty);
        }

        [Test]
        public void BothAxes_AreTrackedSeparately() {
            tracker.PulseSending(NS, 300);
            tracker.PulseSending(WE, 300);

            tracker.Observe(GuideVector(NS, PropertyState.Ok));
            Assert.That(tracker.IsPulseGuiding, Is.True, "the RA pulse is still running");

            tracker.Observe(GuideVector(WE, PropertyState.Ok));
            Assert.That(tracker.IsPulseGuiding, Is.False);
        }

        [Test]
        public void Alert_IsAFailure_AndTheRejectionIsTakenOnce() {
            var failures = new List<PulseGuideFailure>();
            tracker.Failed += failures.Add;
            tracker.PulseSending(NS, 2000);
            now += TimeSpan.FromMilliseconds(30);

            tracker.Observe(GuideVector(NS, PropertyState.Alert, 2000));

            Assert.That(failures, Is.EqualTo(new[] { new PulseGuideFailure(NS, 2000, 30, Rejected: true) }));
            Assert.That(tracker.TakeRejected(WE), Is.Null, "the other axis");
            Assert.That(tracker.TakeRejected(NS), Is.EqualTo(failures[0]));
            Assert.That(tracker.TakeRejected(NS), Is.Null, "taken once");
        }

        [Test]
        public void AlertAfterItsTime_IsStillARejection() {
            tracker.PulseSending(NS, 300);
            tracker.Observe(GuideVector(NS, PropertyState.Busy, 300));
            now += TimeSpan.FromMilliseconds(300);

            tracker.Observe(GuideVector(NS, PropertyState.Alert, 300));

            Assert.That(tracker.TakeRejected(NS), Is.Not.Null);
        }

        [Test]
        public void OkWellBeforeTheEnd_IsAFailure_ButNotARejection() {
            var failures = new List<PulseGuideFailure>();
            tracker.Failed += failures.Add;
            tracker.PulseSending(NS, 3000);
            now += TimeSpan.FromMilliseconds(40);

            tracker.Observe(GuideVector(NS, PropertyState.Ok, 3000));

            Assert.That(failures, Is.EqualTo(new[] { new PulseGuideFailure(NS, 3000, 40, Rejected: false) }));
            Assert.That(tracker.TakeRejected(NS), Is.Null, "a driver may end the pulse as soon as the mount, which times it, accepted it");
        }

        [TestCase(3000, 1600)]
        [TestCase(300, 120)]
        [TestCase(150, 0)]
        public void OkAtOrNearItsEnd_OrShortlyBeforeTheEndOfAShortPulse_IsNoFailure(int durationMs, int elapsedMs) {
            var failures = new List<PulseGuideFailure>();
            tracker.Failed += failures.Add;
            tracker.PulseSending(NS, durationMs);
            now += TimeSpan.FromMilliseconds(elapsedMs);

            tracker.Observe(GuideVector(NS, PropertyState.Ok, durationMs));

            Assert.That(failures, Is.Empty);
        }

        [Test]
        public void TheNextPulse_ClearsAnUntakenRejection() {
            tracker.PulseSending(NS, 300);
            tracker.Observe(GuideVector(NS, PropertyState.Alert, 300));

            tracker.PulseSending(NS, 300);

            Assert.That(tracker.TakeRejected(NS), Is.Null);
        }

        [Test]
        public void AnUpdateAfterATimeout_OrForAnotherClientsPulse_IsNoFailure() {
            var failures = new List<PulseGuideFailure>();
            tracker.Failed += failures.Add;
            tracker.PulseSending(NS, 300);
            now += TimeSpan.FromMilliseconds(300) + Margin;
            Assert.That(tracker.IsPulseGuiding, Is.False);

            tracker.Observe(GuideVector(NS, PropertyState.Alert, 300));
            tracker.Observe(GuideVector(WE, PropertyState.Alert, 300));

            Assert.That(failures, Is.Empty);
            Assert.That(tracker.TakeRejected(NS), Is.Null);
        }
    }
}
