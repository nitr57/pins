#region "copyright"

/*
    Copyright © 2026 Nico Trost <nico.trost57@gmail.com> and the PI.N.S. contributors

    This file is part of PI 'N' Stars.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Equipment.Equipment.MyRotator;
using NUnit.Framework;
using System;

namespace NINA.Test.Equipment.MyRotator {

    /// <summary>
    /// The Wanderer rotator's rule for a lost device: 3 communication errors within 30 s with no successful read in
    /// between, where the failed status reads of one poll (within a second) count as one.
    /// </summary>
    [TestFixture]
    public class CommunicationErrorCounterTest {
        private static readonly DateTime Start = new(2026, 10, 28, 22, 0, 0, DateTimeKind.Utc);

        private CommunicationErrorCounter counter = null!;

        [SetUp]
        public void SetUp() {
            counter = new CommunicationErrorCounter(3, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(1));
        }

        [Test]
        public void TheFailedReadsOfOnePoll_CountAsOneError() {
            Assert.That(counter.RecordError(Start, out var first), Is.True);
            Assert.That(counter.RecordError(Start.AddMilliseconds(200), out var second), Is.True);
            Assert.That(counter.RecordError(Start.AddMilliseconds(400), out var third), Is.True, "a single hang keeps the connection");

            Assert.That(new[] { first, second, third }, Is.EqualTo(new[] { 1, 1, 1 }));
        }

        [Test]
        public void ThreeFailedPolls_WithinTheWindow_LoseTheDevice() {
            Assert.That(counter.RecordError(Start, out _), Is.True);
            Assert.That(counter.RecordError(Start.AddSeconds(2), out var second), Is.True);
            Assert.That(counter.RecordError(Start.AddSeconds(4), out var third), Is.False);

            Assert.That(second, Is.EqualTo(2));
            Assert.That(third, Is.EqualTo(3));
        }

        [Test]
        public void ASuccessfulRead_StartsTheCountOver() {
            counter.RecordError(Start, out _);
            counter.RecordError(Start.AddSeconds(2), out _);

            counter.RecordSuccess();

            Assert.That(counter.RecordError(Start.AddSeconds(4), out var count), Is.True);
            Assert.That(count, Is.EqualTo(1));
        }

        [Test]
        public void ErrorsOlderThanTheWindow_NoLongerCount() {
            counter.RecordError(Start, out _);
            counter.RecordError(Start.AddSeconds(20), out _);

            Assert.That(counter.RecordError(Start.AddSeconds(40), out var count), Is.True);
            Assert.That(count, Is.EqualTo(2), "the error at 0 s is out of the window, the one at 20 s is not");
        }

        [Test]
        public void AfterTheDeviceWasLost_TheCountStartsOver() {
            counter.RecordError(Start, out _);
            counter.RecordError(Start.AddSeconds(2), out _);
            Assert.That(counter.RecordError(Start.AddSeconds(4), out _), Is.False);

            Assert.That(counter.RecordError(Start.AddSeconds(4.2), out var count), Is.True, "the rest of that poll after the disconnect");
            Assert.That(count, Is.EqualTo(1));
        }
    }
}
