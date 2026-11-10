#region "copyright"

/*
    Copyright © 2026 Nico Trost <nico.trost57@gmail.com> and the PI.N.S. contributors

    This file is part of PI 'N' Stars.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Equipment.Equipment;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Test.Equipment {

    /// <summary>
    /// The move wait of the Wanderer and Nitecrawler drivers against scripted status reads on a virtual clock that
    /// advances one second per read: it ends when the device stops, and with a stall timeout when the device keeps
    /// reporting a move without its position changing; a read error the driver tolerates neither ends nor resets it.
    /// </summary>
    [TestFixture]
    public class NativeMoveWaitTest {
        private static readonly TimeSpan Stall = TimeSpan.FromSeconds(10);

        [Test]
        public async Task Ends_WhenTheDeviceStops() {
            var (result, last, reads) = await WaitAsync(Stall, Moving(1), Moving(2), Moving(3), Stopped(4));

            Assert.That(result, Is.EqualTo(MoveWaitResult.Stopped));
            Assert.That(last.Position, Is.EqualTo(4));
            Assert.That(reads, Is.EqualTo(4));
        }

        [Test]
        public async Task KeepsWaiting_AsLongAsThePositionChanges() {
            var statuses = new List<MoveStatus>();
            for (int i = 0; i < 30; i++) {
                statuses.Add(Moving(i));
            }

            statuses.Add(Stopped(30));

            var (result, _, _) = await WaitAsync(Stall, statuses.ToArray());

            Assert.That(result, Is.EqualTo(MoveWaitResult.Stopped), "a long move whose position changes is not stalled");
        }

        [Test]
        public async Task Stalls_WhenThePositionStaysWhileMoving() {
            var statuses = new List<MoveStatus> { Moving(1), Moving(2) };
            for (int i = 0; i < 20; i++) {
                statuses.Add(Moving(2));
            }

            var (result, last, reads) = await WaitAsync(Stall, statuses.ToArray());

            Assert.That(result, Is.EqualTo(MoveWaitResult.Stalled));
            Assert.That(last.Position, Is.EqualTo(2));
            Assert.That(reads, Is.EqualTo(12), "10 s after the position last changed (read 2 at 2 s)");
        }

        [Test]
        public async Task AReadErrorItTolerates_DoesNotResetTheStallTime() {
            var statuses = new List<MoveStatus> { Moving(5) };
            for (int i = 0; i < 20; i++) {
                statuses.Add(i % 2 == 0 ? MoveStatus.Unread : Moving(5));
            }

            var (result, _, reads) = await WaitAsync(Stall, statuses.ToArray());

            Assert.That(result, Is.EqualTo(MoveWaitResult.Stalled));
            Assert.That(reads, Is.EqualTo(10), "10 s after the start: the first position read does not reset the time");
        }

        [Test]
        public async Task AStallAfterAReadError_ReportsTheLastPositionRead() {
            var statuses = new List<MoveStatus> { Moving(7) };
            for (int i = 0; i < 20; i++) {
                statuses.Add(MoveStatus.Unread);
            }

            var (result, last, _) = await WaitAsync(Stall, statuses.ToArray());

            Assert.That(result, Is.EqualTo(MoveWaitResult.Stalled));
            Assert.That(last.Position, Is.EqualTo(7));
        }

        [Test]
        public async Task AReadErrorItTolerates_DoesNotEndTheWait() {
            var (result, last, _) = await WaitAsync(Stall, Moving(1), MoveStatus.Unread, MoveStatus.Unread, Stopped(3));

            Assert.That(result, Is.EqualTo(MoveWaitResult.Stopped));
            Assert.That(last.Position, Is.EqualTo(3));
        }

        [Test]
        public async Task WithoutAStallTimeout_WaitsForTheDevice() {
            // the Wanderer SDK keeps its position until the move ends
            var statuses = new List<MoveStatus>();
            for (int i = 0; i < 200; i++) {
                statuses.Add(Moving(0));
            }

            statuses.Add(Stopped(270));

            var (result, last, _) = await WaitAsync(null, statuses.ToArray());

            Assert.That(result, Is.EqualTo(MoveWaitResult.Stopped));
            Assert.That(last.Position, Is.EqualTo(270));
        }

        [Test]
        public void AReadErrorItDoesNotTolerate_Throws() {
            Assert.That(
                async () => await NativeMoveWait.WaitAsync(() => throw new InvalidOperationException("Rotator error"), TimeSpan.Zero, Stall, () => DateTime.UtcNow, CancellationToken.None),
                Throws.InvalidOperationException);
        }

        [Test]
        public void Cancellation_EndsTheWait() {
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            Assert.That(
                async () => await NativeMoveWait.WaitAsync(() => Moving(1), TimeSpan.Zero, Stall, () => DateTime.UtcNow, cts.Token),
                Throws.InstanceOf<OperationCanceledException>());
        }

        private static MoveStatus Moving(double position) => new(true, true, position);

        private static MoveStatus Stopped(double position) => new(true, false, position);

        private static async Task<(MoveWaitResult Result, MoveStatus Last, int Reads)> WaitAsync(TimeSpan? stall, params MoveStatus[] statuses) {
            var start = new DateTime(2026, 11, 10, 20, 0, 0, DateTimeKind.Utc);
            int reads = 0;
            var (result, last) = await NativeMoveWait.WaitAsync(
                () => statuses[Math.Min(reads++, statuses.Length - 1)],
                TimeSpan.Zero,
                stall,
                () => start + TimeSpan.FromSeconds(reads),
                CancellationToken.None);

            return (result, last, reads);
        }
    }
}
