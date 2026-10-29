#region "copyright"

/*
    Copyright © 2025-2026 Nico Trost <nico.trost57@gmail.com> and the PI.N.S. contributors

    This file is part of PI 'N' Stars.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NINA.Core.Model;
using NINA.Core.SignalR;

namespace NINA.Core.Utility {
    /// <summary>
    /// Static broadcaster for progress messages, similar to Notification
    /// </summary>
    public static class Progress {
        public delegate Task ProgressBroadcaster(ProgressMessage message);
        public static ProgressBroadcaster Broadcaster { get; set; }

        // Track last send time AND last status text per source to throttle updates
        private static readonly Dictionary<string, DateTime> lastSendTime = new Dictionary<string, DateTime>();
        private static readonly Dictionary<string, string> lastStatusText = new Dictionary<string, string>();
        private static readonly int throttleIntervalMs = 500;

        // The newest update a throttle held back per source, sent when the interval is over unless a newer message went
        // out first: otherwise the last tick of a progress (e.g. 100 %) could be the one that never arrives
        private static readonly Dictionary<string, (ProgressMessage Message, Timer Timer)> held = new();

        // Guards the throttle state and the order of the sends. Messages go out one after another in the order they were
        // published; sent in parallel, an update could reach the clients (and the broadcaster's list of active progresses,
        // which new clients get) after the delete of its source and leave a progress bar that never goes away.
        private static readonly object gate = new();
        private static Task sendChain = Task.CompletedTask;

        // Under gate
        private static void BroadcastProgress(ProgressMessage message) {
            var broadcaster = Broadcaster;
            if (broadcaster == null) {
                return;
            }

            sendChain = sendChain.ContinueWith(_ => Send(broadcaster, message), TaskScheduler.Default).Unwrap();
        }

        private static async Task Send(ProgressBroadcaster broadcaster, ProgressMessage message) {
            try {
                await broadcaster(message);
            } catch (Exception ex) {
                Logger.Error($"Failed to broadcast progress: {ex}");
            }
        }

        // Under gate
        private static bool ShouldThrottle(string source, string status) {
            if (string.IsNullOrEmpty(source)) {
                return false;
            }

            // Always send when the status text itself has changed
            if (!lastStatusText.TryGetValue(source, out var prevStatus) || prevStatus != status) {
                lastSendTime[source] = DateTime.UtcNow;
                lastStatusText[source] = status;
                return false;
            }

            // Same status text — apply time-based throttle to suppress rapid progress ticks
            if (!lastSendTime.ContainsKey(source)) {
                lastSendTime[source] = DateTime.UtcNow;
                return false;
            }

            var timeSinceLastSend = DateTime.UtcNow - lastSendTime[source];
            if (timeSinceLastSend.TotalMilliseconds >= throttleIntervalMs) {
                lastSendTime[source] = DateTime.UtcNow;
                return false;
            }

            return true; // Same status, not enough time has passed — throttle this message
        }

        // Under gate
        private static void Hold(ProgressMessage message) {
            if (held.TryGetValue(message.Source, out var existing)) {
                held[message.Source] = (message, existing.Timer);
                return;
            }

            var due = TimeSpan.FromMilliseconds(throttleIntervalMs) - (DateTime.UtcNow - lastSendTime[message.Source]);
            var timer = new Timer(SendHeld, message.Source, due < TimeSpan.Zero ? TimeSpan.Zero : due, Timeout.InfiniteTimeSpan);
            held[message.Source] = (message, timer);
        }

        private static void SendHeld(object state) {
            var source = (string)state;
            lock (gate) {
                if (!held.Remove(source, out var entry)) {
                    return;
                }

                entry.Timer.Dispose();
                lastSendTime[source] = DateTime.UtcNow;
                BroadcastProgress(entry.Message);
            }
        }

        // Under gate: a newer message of the source supersedes what was held back
        private static void DropHeld(string source) {
            if (!string.IsNullOrEmpty(source) && held.Remove(source, out var entry)) {
                entry.Timer.Dispose();
            }
        }

        // Under gate
        private static void ClearThrottleForSource(string source) {
            if (string.IsNullOrEmpty(source)) {
                return;
            }

            lastSendTime.Remove(source);
            lastStatusText.Remove(source);
        }

        public static void PublishNewStatus(ApplicationStatus status) {
            ProgressMessage message = new ProgressMessage {
                Source = status.Source,
                Status = status.Status,
                ProgressType = status.ProgressType,
                Progress = status.Progress,
                MaxProgress = status.MaxProgress,
                Status2 = status.Status2,
                ProgressType2 = status.ProgressType2,
                Progress2 = status.Progress2,
                MaxProgress2 = status.MaxProgress2,
                Status3 = status.Status3,
                ProgressType3 = status.ProgressType3,
                Progress3 = status.Progress3,
                MaxProgress3 = status.MaxProgress3,
                State = "create",
                Timestamp = DateTime.UtcNow,
            };
            lock (gate) {
                DropHeld(status.Source);
                BroadcastProgress(message);
            }
        }

        public static void PublishUpdateStatus(ApplicationStatus status) {
            ProgressMessage message = new ProgressMessage {
                Source = status.Source,
                Status = status.Status,
                ProgressType = status.ProgressType,
                Progress = status.Progress,
                MaxProgress = status.MaxProgress,
                Status2 = status.Status2,
                ProgressType2 = status.ProgressType2,
                Progress2 = status.Progress2,
                MaxProgress2 = status.MaxProgress2,
                Status3 = status.Status3,
                ProgressType3 = status.ProgressType3,
                Progress3 = status.Progress3,
                MaxProgress3 = status.MaxProgress3,
                State = "update",
                Timestamp = DateTime.UtcNow,
            };
            lock (gate) {
                // Throttle only when the same status text repeats rapidly (e.g. progress ticks); the newest is sent later
                if (ShouldThrottle(status.Source, status.Status)) {
                    Hold(message);
                    return;
                }

                DropHeld(status.Source);
                BroadcastProgress(message);
            }
        }
        
        public static void PublishRemoveStatus(ApplicationStatus status) {
            ProgressMessage message = new ProgressMessage {
                Source = status.Source,
                Status = string.Empty,
                State = "delete",
                Timestamp = DateTime.UtcNow,
            };
            lock (gate) {
                // Clear throttle tracking for this source when removing; nothing held back may follow the delete
                ClearThrottleForSource(status.Source);
                DropHeld(status.Source);
                BroadcastProgress(message);
            }
        }
    }
}