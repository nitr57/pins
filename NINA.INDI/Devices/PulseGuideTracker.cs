#region "copyright"

/*
    Copyright © 2026 Nico Trost <nico.trost57@gmail.com> and the PI.N.S. contributors

    This file is part of PI 'N' Stars.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.INDI.Enums;
using NINA.INDI.Protocol;
using System;
using System.Collections.Generic;
using System.Linq;

namespace NINA.INDI.Devices {

    /// <summary>
    /// Whether a timed guide pulse is in progress, per axis. INDI has no "is guiding" flag, and the driver's Busy
    /// state arrives only after the pulse was sent, so like PHD2's ScopeINDI an axis counts as guiding from the
    /// moment its pulse is sent until the first update of its TELESCOPE_TIMED_GUIDE_* property that is not Busy.
    /// Unlike PHD2 the wait is bounded: a driver that never reports the end of a pulse releases the axis
    /// <see cref="CompletionMargin"/> after the pulse should have ended, and <see cref="TimedOut"/> reports it.
    /// </summary>
    internal sealed class PulseGuideTracker {
        public const string NorthSouth = "TELESCOPE_TIMED_GUIDE_NS";
        public const string WestEast = "TELESCOPE_TIMED_GUIDE_WE";

        /// <summary>
        /// Added to a pulse before an unreported end counts as a timeout. Covers a driver that starts the pulse late,
        /// e.g. behind its own status poll on the serial port; the end merely reaching pins late does not matter, as
        /// the mount has stopped by then.
        /// </summary>
        public static readonly TimeSpan CompletionMargin = TimeSpan.FromMilliseconds(500);

        private readonly Func<DateTime> utcNow;
        private readonly object sync = new();
        private readonly Dictionary<string, (DateTime Deadline, int DurationMs)> active = [];

        public PulseGuideTracker(Func<DateTime> utcNow = null) {
            this.utcNow = utcNow ?? (() => DateTime.UtcNow);
        }

        /// <summary>
        /// A pulse the driver reports as ended before this fraction of its length, and at least
        /// <see cref="EarlyEndMinShortfall"/> before its end, counts as ended early.
        /// </summary>
        public const double EarlyEndFraction = 0.5;

        /// <summary>A pulse that ended less than this before its end did not end early, whatever its length.</summary>
        public static readonly TimeSpan EarlyEndMinShortfall = TimeSpan.FromMilliseconds(200);

        // The pulse sent here per axis, until the driver reports its end or it times out.
        private readonly Dictionary<string, (DateTime SentAt, int DurationMs)> sent = [];

        // The last pulse per axis that the driver rejected, until it is taken or the next pulse is sent.
        private readonly Dictionary<string, PulseGuideFailure> rejected = [];

        /// <summary>Raised outside the tracker's lock with the guide property and the pulse length in ms.</summary>
        public event Action<string, int> TimedOut;

        /// <summary>
        /// Raised outside the tracker's lock when the driver reports a pulse sent here as failed (Alert) or ends it well
        /// before its length. PHD2 guide logs on an OnStep mount over INDI (2026-09/10) show compensated Dec reversals
        /// ending at once without moving the star.
        /// </summary>
        public event Action<PulseGuideFailure> Failed;

        public bool IsPulseGuiding {
            get {
                bool guiding;
                List<(string, int)> expired;
                lock (sync) {
                    expired = ExpireOverdue();
                    guiding = active.Count > 0;
                }
                Report(expired);
                return guiding;
            }
        }

        /// <summary>Call right before sending the pulse, so the driver's reply can never arrive before the axis is marked.</summary>
        public void PulseSending(string property, int durationMs) {
            List<(string, int)> expired;
            lock (sync) {
                expired = ExpireOverdue();
                var now = utcNow();
                active[property] = (now + TimeSpan.FromMilliseconds(Math.Max(0, durationMs)) + CompletionMargin, durationMs);
                sent[property] = (now, durationMs);
                rejected.Remove(property);
            }
            Report(expired);
        }

        /// <summary>The pulse could not be sent, so no reply will come.</summary>
        public void PulseNotSent(string property) {
            lock (sync) {
                active.Remove(property);
                sent.Remove(property);
            }
        }

        /// <summary>
        /// The last pulse sent on this axis if the driver rejected it (Alert), else null; taken once. A pulse that only
        /// ended early is not returned: some drivers report the end as soon as the mount, which times the pulse itself,
        /// has accepted it.
        /// </summary>
        public PulseGuideFailure TakeRejected(string property) {
            lock (sync) {
                return rejected.Remove(property, out var failure) ? failure : null;
            }
        }

        /// <summary>Feed every update of the timed-guide properties.</summary>
        public void Observe(INDINumberProperty p) {
            if (p.Name != NorthSouth && p.Name != WestEast) {
                return;
            }

            PulseGuideFailure failure = null;
            lock (sync) {
                if (p.State != PropertyState.Busy) {
                    active.Remove(p.Name);
                    if (sent.Remove(p.Name, out var pulse)) {
                        failure = Classify(p.Name, p.State, pulse.DurationMs, utcNow() - pulse.SentAt);
                        if (failure is { Rejected: true }) {
                            rejected[p.Name] = failure;
                        }
                    }
                }
            }

            if (failure is not null) {
                Failed?.Invoke(failure);
            }

            if (p.State != PropertyState.Busy) {
                return;
            }

            lock (sync) {
                // Busy: the driver has started a pulse, ours or another client's. It ends one pulse length from
                // now at the latest, which also covers a driver that started ours late.
                var durationMs = (int)Math.Max(0, p.Numbers.Select(n => n.Value).DefaultIfEmpty(0).Max());
                var deadline = utcNow() + TimeSpan.FromMilliseconds(durationMs) + CompletionMargin;
                if (active.TryGetValue(p.Name, out var pending) && pending.Deadline > deadline) {
                    deadline = pending.Deadline;
                    durationMs = pending.DurationMs;
                }
                active[p.Name] = (deadline, durationMs);
            }
        }

        private static PulseGuideFailure Classify(string property, PropertyState state, int durationMs, TimeSpan elapsed) {
            if (state == PropertyState.Alert) {
                return new PulseGuideFailure(property, durationMs, elapsed.TotalMilliseconds, Rejected: true);
            }

            var length = TimeSpan.FromMilliseconds(durationMs);
            bool early = elapsed < length * EarlyEndFraction && length - elapsed >= EarlyEndMinShortfall;
            return early ? new PulseGuideFailure(property, durationMs, elapsed.TotalMilliseconds, Rejected: false) : null;
        }

        // Caller holds sync.
        private List<(string, int)> ExpireOverdue() {
            var now = utcNow();
            var expired = active.Where(a => a.Value.Deadline <= now).Select(a => (a.Key, a.Value.DurationMs)).ToList();
            foreach (var (property, _) in expired) {
                active.Remove(property);
                sent.Remove(property);
            }
            return expired;
        }

        private void Report(List<(string Property, int DurationMs)> expired) {
            foreach (var (property, durationMs) in expired) {
                TimedOut?.Invoke(property, durationMs);
            }
        }
    }

    /// <summary>
    /// A guide pulse sent here that the driver reported as failed (<paramref name="Rejected"/>, an Alert) or ended
    /// <paramref name="ElapsedMs"/> after it was sent, well before its <paramref name="DurationMs"/>.
    /// </summary>
    public sealed record PulseGuideFailure(string Property, int DurationMs, double ElapsedMs, bool Rejected);
}
