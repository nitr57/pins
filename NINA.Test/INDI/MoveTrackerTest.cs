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
using NUnit.Framework;
using System;

namespace NINA.Test.INDI {

    [TestFixture]
    public class MoveTrackerTest {
        private MoveTracker tracker = null!;

        // As INDIRotator: within half a degree, also across 0°/360°
        [SetUp]
        public void SetUp() {
            tracker = new MoveTracker((angle, target) => Math.Abs(((angle - target) % 360 + 540) % 360 - 180) <= 0.5);
        }

        [Test]
        public void StaleOk_BeforeTheDriversAnswer_DoesNotEndTheMove() {
            tracker.Commanded(90);

            tracker.Updated(PropertyState.Ok, 10);

            Assert.That(tracker.IsMoving, Is.True);
        }

        [Test]
        public void SecondUpdateAwayFromTheTarget_WithoutBusy_EndsTheMove() {
            tracker.Commanded(90);
            tracker.Updated(PropertyState.Ok, 10);

            // the driver's own answer: it refused or limited the target without an Alert
            tracker.Updated(PropertyState.Ok, 10);

            Assert.That(tracker.IsMoving, Is.False);
        }

        [Test]
        public void ANewCommand_IgnoresAStaleUpdateAgain() {
            tracker.Commanded(90);
            tracker.Updated(PropertyState.Ok, 10);
            tracker.Updated(PropertyState.Ok, 10);

            tracker.Commanded(180);
            tracker.Updated(PropertyState.Ok, 10);

            Assert.That(tracker.IsMoving, Is.True);
        }

        [Test]
        public void OkAfterBusy_EndsTheMove_WhereverItStopped() {
            tracker.Commanded(90);
            tracker.Updated(PropertyState.Busy, 40);

            tracker.Updated(PropertyState.Ok, 89);

            Assert.That(tracker.IsMoving, Is.False);
        }

        [TestCase(90, 90.2)]
        [TestCase(0, 359.8)]
        [TestCase(359.9, 0.1)]
        public void OkAtTheTarget_WithoutBusy_EndsTheMove(double target, double reported) {
            tracker.Commanded(target);

            tracker.Updated(PropertyState.Ok, reported);

            Assert.That(tracker.IsMoving, Is.False);
        }

        [Test]
        public void Alert_EndsTheMove() {
            tracker.Commanded(90);

            tracker.Updated(PropertyState.Alert, 10);

            Assert.That(tracker.IsMoving, Is.False);
        }

        [Test]
        public void WithoutACommand_TheStateIsTheAnswer() {
            tracker.Updated(PropertyState.Busy, 10);
            Assert.That(tracker.IsMoving, Is.True, "a move started elsewhere, e.g. from the INDI control panel");

            tracker.Updated(PropertyState.Ok, 20);
            Assert.That(tracker.IsMoving, Is.False);
        }

        [Test]
        public void AfterTheMove_UpdatesAreTakenAsTheyCome() {
            tracker.Commanded(90);
            tracker.Updated(PropertyState.Ok, 90);

            tracker.Updated(PropertyState.Busy, 95);

            Assert.That(tracker.IsMoving, Is.True);
        }

        [Test]
        public void Ended_StopsWaitingForTheDriver() {
            tracker.Commanded(90);

            tracker.Ended();
            tracker.Updated(PropertyState.Ok, 10);

            Assert.That(tracker.IsMoving, Is.False);
        }
    }
}
