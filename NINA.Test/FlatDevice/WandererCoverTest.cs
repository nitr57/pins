#region "copyright"

/*
    Copyright © 2026 Nico Trost <nico.trost57@gmail.com> and the PI.N.S. contributors

    This file is part of PI 'N' Stars.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Equipment.Equipment.MyFlatDevice;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using static Wanderer.WandererCoverSDK;

namespace NINA.Test.FlatDevice {

    /// <summary>
    /// WandererCover's open/close wait against scripted cover states (0 = closed, 1 = open, 2 = intermediate,
    /// 3 = moving): it succeeds only at the requested end, tolerates the old end within the start grace (before the motor
    /// starts and as a late status after it), and fails on a stop short of it, a cover that never arrives and a status
    /// read error.
    /// </summary>
    [TestFixture]
    public class WandererCoverTest {
        private const int Closed = 0;
        private const int Open = 1;
        private const int Intermediate = 2;
        private const int Moving = 3;

        private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(1);
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan StartGrace = TimeSpan.FromSeconds(2);

        [Test]
        public async Task Open_SucceedsOnlyWhenTheCoverReportsOpen() {
            var (result, _, state) = await WaitAsync(Open, Closed, Moving, Moving, Open);

            Assert.That(result, Is.EqualTo(WandererCover.CoverMoveResult.Reached));
            Assert.That(state, Is.EqualTo(Open));
        }

        [Test]
        public async Task Close_SucceedsOnlyWhenTheCoverReportsClosed() {
            var (result, _, state) = await WaitAsync(Closed, Open, Moving, Closed);

            Assert.That(result, Is.EqualTo(WandererCover.CoverMoveResult.Reached));
            Assert.That(state, Is.EqualTo(Closed));
        }

        [Test]
        public async Task Open_WaitsWhileTheMotorHasNotStartedYet() {
            // The old end is reported a few times before the cover starts moving
            var (result, _, _) = await WaitAsync(Open, Closed, Closed, Closed, Moving, Open);

            Assert.That(result, Is.EqualTo(WandererCover.CoverMoveResult.Reached));
        }

        [Test]
        public async Task Open_FailsWhenTheCoverStopsHalfway() {
            var (result, _, state) = await WaitAsync(Open, Moving, Moving, Intermediate);

            Assert.That(result, Is.EqualTo(WandererCover.CoverMoveResult.Stopped));
            Assert.That(state, Is.EqualTo(Intermediate));
        }

        [Test]
        public async Task Open_FailsWhenTheCoverReturnsToWhereItStarted_OnceTheStartGraceIsOver() {
            var sequence = new[] { Moving, Closed };
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var (result, _, _) = await WaitAsync(
                poll => (WC_ERROR_TYPE.WC_SUCCESS, sequence[Math.Min(poll, sequence.Length - 1)]),
                Open,
                Timeout,
                TimeSpan.FromMilliseconds(200));

            Assert.That(result, Is.EqualTo(WandererCover.CoverMoveResult.Stopped));
            Assert.That(watch.Elapsed, Is.GreaterThanOrEqualTo(TimeSpan.FromMilliseconds(190)), "the old end counts as a late status until the grace is over");
        }

        [Test]
        public async Task Open_WaitsThroughALateStatusOfTheOldEnd_AfterTheMoveStarted() {
            // The SDK reports moving at once; a status sent before the move arrives after the first poll and the old end
            // stays reported until the cover arrives
            var (result, _, state) = await WaitAsync(Open, Moving, Closed, Closed, Closed, Open);

            Assert.That(result, Is.EqualTo(WandererCover.CoverMoveResult.Reached));
            Assert.That(state, Is.EqualTo(Open));
        }

        [Test]
        public async Task Close_WaitsThroughALateStatusOfTheOldEnd_AfterTheMoveStarted() {
            var (result, _, _) = await WaitAsync(Closed, Moving, Open, Open, Closed);

            Assert.That(result, Is.EqualTo(WandererCover.CoverMoveResult.Reached));
        }

        [Test]
        public async Task Open_FailsWhenTheCoverNeverStartsMoving() {
            var result = await WaitAsync(_ => (WC_ERROR_TYPE.WC_SUCCESS, Closed), Open, timeout: Timeout, startGrace: TimeSpan.FromMilliseconds(50));

            Assert.That(result.Result, Is.EqualTo(WandererCover.CoverMoveResult.Stopped));
        }

        [Test]
        public async Task Open_TimesOutWhenTheCoverKeepsMoving() {
            var result = await WaitAsync(_ => (WC_ERROR_TYPE.WC_SUCCESS, Moving), Open, timeout: TimeSpan.FromMilliseconds(100), startGrace: StartGrace);

            Assert.That(result.Result, Is.EqualTo(WandererCover.CoverMoveResult.TimedOut));
        }

        [Test]
        public async Task Open_FailsOnAStatusReadError() {
            var result = await WaitAsync(
                poll => poll < 2 ? (WC_ERROR_TYPE.WC_SUCCESS, Moving) : (WC_ERROR_TYPE.WC_ERROR_COMMUNICATION, Closed),
                Open,
                Timeout,
                StartGrace);

            Assert.That(result.Result, Is.EqualTo(WandererCover.CoverMoveResult.StatusError));
            Assert.That(result.Error, Is.EqualTo(WC_ERROR_TYPE.WC_ERROR_COMMUNICATION));
        }

        // The states are reported in this order; the last one repeats
        private static Task<(WandererCover.CoverMoveResult Result, WC_ERROR_TYPE Error, int State)> WaitAsync(int target, params int[] states) {
            var sequence = new List<int>(states);
            return WaitAsync(poll => (WC_ERROR_TYPE.WC_SUCCESS, sequence[Math.Min(poll, sequence.Count - 1)]), target, Timeout, StartGrace);
        }

        private static Task<(WandererCover.CoverMoveResult Result, WC_ERROR_TYPE Error, int State)> WaitAsync(
            Func<int, (WC_ERROR_TYPE, int)> status,
            int target,
            TimeSpan timeout,
            TimeSpan startGrace) {
            var poll = 0;
            return WandererCover.WaitForCoverStateAsync(() => status(poll++), target, Poll, timeout, startGrace, CancellationToken.None);
        }
    }
}
