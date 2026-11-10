#region "copyright"

/*
    Copyright © 2026 Nico Trost <nico.trost57@gmail.com> and the PI.N.S. contributors

    This file is part of PI 'N' Stars.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Equipment.Equipment {

    /// <summary>
    /// One read of a native SDK's move status. <see cref="Read"/> false is a read error the driver tolerates (a
    /// transient communication error): the move counts as running, with no position. An error the driver does not
    /// tolerate is thrown by the read itself.
    /// </summary>
    internal readonly record struct MoveStatus(bool Read, bool Moving, double Position) {
        public static MoveStatus Unread => new(false, true, double.NaN);
    }

    internal enum MoveWaitResult {
        /// <summary>The device reported that it stopped.</summary>
        Stopped,

        /// <summary>The device kept reporting a move without its position changing for the stall timeout.</summary>
        Stalled,
    }

    /// <summary>
    /// Waits for a move of a natively driven rotator or focuser (Wanderer, Nitecrawler) by polling its status, as their
    /// drivers did inline. With a stall timeout a blocked motor ends the wait: the SDK's moving flag alone may stay set
    /// for good. Only for SDKs that read the position from the device on every poll (Nitecrawler); the Wanderer SDK
    /// updates it only when a move ends, so a long move would look stalled.
    /// </summary>
    internal static class NativeMoveWait {

        public static async Task<(MoveWaitResult Result, MoveStatus Last)> WaitAsync(
            Func<MoveStatus> readStatus,
            TimeSpan pollInterval,
            TimeSpan? stallTimeout,
            Func<DateTime> utcNow,
            CancellationToken ct) {
            var lastChange = utcNow();
            double? lastPosition = null;
            MoveStatus? lastRead = null;

            while (true) {
                ct.ThrowIfCancellationRequested();

                var status = readStatus();
                if (status.Read && !status.Moving) {
                    return (MoveWaitResult.Stopped, status);
                }

                if (status.Read) {
                    lastRead = status;
                }

                var now = utcNow();
                if (status.Read && status.Position != lastPosition) {
                    // also the first position read: the stall time counts from the start of the wait until then
                    if (lastPosition is not null) {
                        lastChange = now;
                    }

                    lastPosition = status.Position;
                }

                if (stallTimeout is { } timeout && now - lastChange >= timeout) {
                    // the last status that was read, so the caller can name where the device stopped
                    return (MoveWaitResult.Stalled, lastRead ?? status);
                }

                await Task.Delay(pollInterval, ct);
            }
        }
    }
}
