#region "copyright"

/*
    Copyright © 2026 Nico Trost <nico.trost57@gmail.com> and the PI.N.S. contributors

    This file is part of PI 'N' Stars.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Core.Utility;
using NINA.INDI.Enums;
using NINA.INDI.Protocol;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace NINA.INDI.Devices {

    /// <summary>How the driver answered a goto sent through EQUATORIAL_EOD_COORD.</summary>
    public enum GotoAcknowledgementKind {

        /// <summary>No update has been identified as the driver's reply to the goto.</summary>
        None,

        /// <summary>EQUATORIAL_EOD_COORD turned Busy: the driver started the slew.</summary>
        Busy,

        /// <summary>EQUATORIAL_EOD_COORD turned Alert with a new timestamp: the driver refused the goto.</summary>
        Rejected,

        /// <summary>
        /// TARGET_EOD_COORD echoed the requested coordinates and an EQUATORIAL_EOD_COORD update followed.
        /// OnStep answers a goto this way, without a Busy update of its own.
        /// </summary>
        TargetThenUpdate,
    }

    /// <summary>
    /// Picks the driver's reply to one goto out of the updates that arrive after it was sent.
    ///
    /// The generic rule in <see cref="INDIDevice.SetNumberValuesAsync"/> accepts any Ok whose timestamp
    /// changed. For a goto that is wrong: a libindi driver is single-threaded, and a goto that arrives
    /// while it is running a status poll is only processed after that poll has pushed an ordinary
    /// tracking update of EQUATORIAL_EOD_COORD (state Ok, new timestamp). Taking that update as the
    /// reply lets the slew wait see a mount that has not moved yet and return at once, so an exposure
    /// started next trails.
    ///
    /// What only the goto itself produces: <c>INDI::Telescope::ISNewNumber</c> applies TARGET_EOD_COORD
    /// with the requested coordinates after a successful Goto() and then EQUATORIAL_EOD_COORD Busy (or
    /// Alert when Goto() fails) in the same call. OnStep overrides that handler and echoes TARGET_EOD_COORD
    /// without Busy, so after the echo the next EQUATORIAL_EOD_COORD update counts as its reply. A status
    /// poll never applies TARGET_EOD_COORD.
    ///
    /// Updates are fed from the INDI receive thread; <see cref="Completion"/> is awaited by the slew.
    /// </summary>
    public sealed class GotoAcknowledgement {

        // NINA sends the coordinates losslessly (XElement) and libindi echoes TARGET_EOD_COORD with %.20g,
        // so a genuine echo matches almost exactly. One arcsecond leaves room for floating-point formatting
        // while still telling the echo apart from a TARGET_EOD_COORD set by stopping a manual move.
        private const double TargetToleranceDegrees = 1.0 / 3600.0;

        private readonly object sync = new();
        private readonly TaskCompletionSource<GotoAcknowledgementKind> completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly string deviceName;
        private readonly double targetRaHours;
        private readonly double targetDecDegrees;
        private readonly string preSendTimestamp;

        private bool targetEchoed;
        private int ignoredStatusUpdates;

        /// <param name="deviceName">Used only to prefix log lines.</param>
        /// <param name="targetRaHours">Requested RA in hours, as sent.</param>
        /// <param name="targetDecDegrees">Requested Dec in degrees, as sent.</param>
        /// <param name="preSendTimestamp">EQUATORIAL_EOD_COORD timestamp before sending, to tell a new Alert from a stale one.</param>
        public GotoAcknowledgement(string deviceName, double targetRaHours, double targetDecDegrees, string preSendTimestamp) {
            this.deviceName = deviceName;
            this.targetRaHours = targetRaHours;
            this.targetDecDegrees = targetDecDegrees;
            this.preSendTimestamp = preSendTimestamp ?? string.Empty;
        }

        /// <summary>Completes with the kind of reply once one has been identified. Never completes with <see cref="GotoAcknowledgementKind.None"/>.</summary>
        public Task<GotoAcknowledgementKind> Completion => completion.Task;

        /// <summary>Whether TARGET_EOD_COORD has echoed the requested coordinates.</summary>
        public bool TargetEchoed {
            get {
                lock (sync) {
                    return targetEchoed;
                }
            }
        }

        /// <summary>EQUATORIAL_EOD_COORD updates that arrived before the reply and were not taken as it.</summary>
        public int IgnoredStatusUpdates {
            get {
                lock (sync) {
                    return ignoredStatusUpdates;
                }
            }
        }

        /// <summary>Feeds one incoming number property update. Properties other than the two goto properties are ignored.</summary>
        public void Observe(INDINumberProperty property) {
            if (property == null) {
                return;
            }

            lock (sync) {
                if (completion.Task.IsCompleted) {
                    return;
                }

                switch (property.Name) {
                    case "TARGET_EOD_COORD":
                        ObserveTarget(property);
                        break;

                    case "EQUATORIAL_EOD_COORD":
                        ObserveCoordinates(property);
                        break;
                }
            }
        }

        private void ObserveTarget(INDINumberProperty property) {
            var ra = ValueOf(property, "RA");
            var dec = ValueOf(property, "DEC");
            if (ra == null || dec == null) {
                return;
            }

            if (MatchesTarget(ra.Value, dec.Value)) {
                targetEchoed = true;
            } else {
                Logger.Info($"[{deviceName}] Goto not yet acknowledged: ignoring TARGET_EOD_COORD RA={ra:F5}h Dec={dec:F5}°, " +
                            $"which is not the requested RA={targetRaHours:F5}h Dec={targetDecDegrees:F5}°");
            }
        }

        private void ObserveCoordinates(INDINumberProperty property) {
            if (property.State == PropertyState.Busy) {
                completion.TrySetResult(GotoAcknowledgementKind.Busy);
                return;
            }

            if (property.State == PropertyState.Alert && IsNewerThanPreSend(property.Timestamp)) {
                // Same rule as SetNumberValuesAsync. It also takes an Alert the driver raises because a
                // status read failed while the goto was queued (Telescope::TimerHit); the caller's single
                // retry absorbs that.
                completion.TrySetResult(GotoAcknowledgementKind.Rejected);
                return;
            }

            if (targetEchoed && property.State != PropertyState.Alert) {
                completion.TrySetResult(GotoAcknowledgementKind.TargetThenUpdate);
                return;
            }

            ignoredStatusUpdates++;
            Logger.Info($"[{deviceName}] Goto not yet acknowledged: ignoring EQUATORIAL_EOD_COORD {property.State} update " +
                        $"(timestamp {DisplayTimestamp(property.Timestamp)}, pre-send {DisplayTimestamp(preSendTimestamp)}), " +
                        "most likely the driver's status poll rather than its reply to this goto");
        }

        private bool IsNewerThanPreSend(string timestamp) {
            return string.IsNullOrEmpty(preSendTimestamp) || timestamp != preSendTimestamp;
        }

        private bool MatchesTarget(double raHours, double decDegrees) {
            var dRaHours = Math.Abs(raHours - targetRaHours);
            if (dRaHours > 12) {
                dRaHours = 24 - dRaHours; // RA wrap-around
            }
            return dRaHours * 15.0 <= TargetToleranceDegrees
                && Math.Abs(decDegrees - targetDecDegrees) <= TargetToleranceDegrees;
        }

        private static double? ValueOf(INDINumberProperty property, string elementName) {
            return property.Numbers.FirstOrDefault(n => n.Name == elementName)?.Value;
        }

        private static string DisplayTimestamp(string timestamp) {
            return string.IsNullOrEmpty(timestamp) ? "n/a" : timestamp;
        }
    }
}
