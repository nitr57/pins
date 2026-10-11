#region "copyright"

/*
    Copyright © 2026 Nico Trost <nico.trost57@gmail.com> and the PI.N.S. contributors

    This file is part of PI 'N' Stars.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using FluentAssertions;
using NINA.Equipment.Utility;

namespace NINA.Test.Equipment {

    [TestFixture]
    public class PendingConnectsTest {

        /// <summary>
        /// Verifies that CancelAll reaches every connect call in flight, not just the latest one. A queued second
        /// connect must not make the hanging first attempt uncancellable.
        /// </summary>
        [Test]
        public void CancelAll_CancelsEveryPendingConnect() {
            PendingConnects sut = new PendingConnects();
            var running = sut.Begin();
            var queued = sut.Begin();

            sut.CancelAll();

            running.IsCancellationRequested.Should().BeTrue();
            queued.IsCancellationRequested.Should().BeTrue();
        }

        /// <summary>
        /// Verifies that a finished connect is no longer cancelled, so a token a driver keeps using after a successful
        /// connect is not cancelled by a later CancelConnect.
        /// </summary>
        [Test]
        public void CancelAll_DoesNotCancelFinishedConnect() {
            PendingConnects sut = new PendingConnects();
            var finished = sut.Begin();
            sut.End(finished);
            var running = sut.Begin();

            sut.CancelAll();

            finished.IsCancellationRequested.Should().BeFalse();
            running.IsCancellationRequested.Should().BeTrue();
        }

        /// <summary>
        /// Verifies that CancelAll and End are safe without any pending connect.
        /// </summary>
        [Test]
        public void CancelAll_WithoutPendingConnects_DoesNotThrow() {
            PendingConnects sut = new PendingConnects();

            Action act = () => {
                sut.CancelAll();
                sut.End(null);
            };

            act.Should().NotThrow();
        }
    }
}
