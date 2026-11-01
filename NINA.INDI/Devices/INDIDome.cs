#region "copyright"

/*
    Copyright © 2025-2026 Nico Trost <nico.trost57@gmail.com> and the PI.N.S. contributors

    This file is part of PI 'N' Stars.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Core.Utility;
using NINA.INDI.Enums;
using NINA.INDI.Interfaces;
using NINA.INDI.Protocol;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.INDI.Devices {

    public class INDIDome : INDIDevice, IINDIDome {

        public INDIDome(INDIDeviceInfo device) : base(device) {
        }

        /// <summary>
        /// Wait for the azimuth position property to arrive before completing Connect().
        /// </summary>
        protected override string[] GetRequiredConnectionProperties() {
            return ["ABS_DOME_POSITION"];
        }

        public override void OnTextPropertyUpdated(INDITextProperty p) {
            base.OnTextPropertyUpdated(p);
        }

        // OpenShutter/CloseShutter wait on _shutterState, which only a DOME_SHUTTER update advances
        protected override void OnConnectionLost() {
            _shutterState = ShutterState.ShutterNone;
        }

        public override void OnNumberPropertyUpdated(INDINumberProperty p) {
            base.OnNumberPropertyUpdated(p);
        }

        public override void OnSwitchPropertyUpdated(INDISwitchProperty p) {
            // Device-local state must be updated BEFORE base resolves pending async acks:
            // TCS continuations run asynchronously, so a waiter (OpenShutter/CloseShutter)
            // can resume the moment base calls TrySetResult — updating _shutterState after
            // base would let it observe the pre-update state and skip its wait loop.
            if (p.Name == "DOME_SHUTTER") {
                UpdateShutterState(p);
            }

            base.OnSwitchPropertyUpdated(p);
        }

        public override void OnBlobPropertyUpdated(INDIBlobProperty p) {
            base.OnBlobPropertyUpdated(p);
        }

        // ── Capabilities ──────────────────────────────────────────────────────────

        public bool CanSetShutter => GetSwitchProperty("DOME_SHUTTER") != null;
        public bool CanSetAzimuth => GetNumberProperty("ABS_DOME_POSITION") != null;
        // Keyed on DOME_PARK_OPTION (what SetPark actually writes), not DOME_PARK — a driver
        // exposing only the latter would advertise a set-park capability that is a silent no-op.
        public bool CanSetPark => GetSwitchProperty("DOME_PARK_OPTION") != null;
        public bool CanSyncAzimuth => GetNumberProperty("DOME_SYNC") != null;
        public bool CanPark => GetSwitchProperty("DOME_PARK") != null;
        public bool CanFindHome {
            get {
                var sw = GetSwitchProperty("FIND_HOME") ?? GetSwitchProperty("HOME_PARK");
                return sw != null;
            }
        }
        public bool DriverCanFollow => GetSwitchProperty("DOME_AUTOSYNC") != null;

        // ── State ─────────────────────────────────────────────────────────────────

        private ShutterState _shutterState = ShutterState.ShutterNone;

        public ShutterState ShutterStatus => _shutterState;

        private void UpdateShutterState(INDISwitchProperty p) {
            if (p.State == PropertyState.Busy) {
                var openSwitch = p.Switches.FirstOrDefault(s => s.Name == "SHUTTER_OPEN");
                _shutterState = (openSwitch?.Value == true) ? ShutterState.ShutterOpening : ShutterState.ShutterClosing;
            } else if (p.State == PropertyState.Ok) {
                var openSwitch = p.Switches.FirstOrDefault(s => s.Name == "SHUTTER_OPEN");
                _shutterState = (openSwitch?.Value == true) ? ShutterState.ShutterOpen : ShutterState.ShutterClosed;
            } else if (p.State == PropertyState.Alert) {
                _shutterState = ShutterState.ShutterError;
            }
        }

        public double Azimuth => GetNumberPropertyValue("ABS_DOME_POSITION", "DOME_ABSOLUTE_POSITION") ?? double.NaN;

        // PARK=On alone is the TARGET, not the position: drivers publish it with state=Busy
        // while the park motion is still running (see INDITelescope.AtPark).
        public bool AtPark =>
            (GetSwitchPropertyValue("DOME_PARK", "PARK") ?? false)
            && GetProperty("DOME_PARK")?.State != PropertyState.Busy;

        public bool AtHome {
            get {
                // Some drivers expose AT_HOME, others HOME_PARK
                var val = GetSwitchPropertyValue("AT_HOME", "AT_HOME")
                       ?? GetSwitchPropertyValue("HOME_PARK", "AT_HOME");
                return val ?? false;
            }
        }

        public bool Slewing {
            get {
                var absProp = GetNumberProperty("ABS_DOME_POSITION");
                if (absProp?.State == PropertyState.Busy) return true;
                var motionProp = GetSwitchProperty("DOME_MOTION");
                return motionProp?.State == PropertyState.Busy;
            }
        }

        public bool DriverFollowing {
            get => GetSwitchPropertyValue("DOME_AUTOSYNC", "DOME_AUTOSYNC_ENABLE") ?? false;
            set {
                try {
                    if (value) {
                        SetSwitchValue("DOME_AUTOSYNC", "DOME_AUTOSYNC_ENABLE", true);
                    } else {
                        SetSwitchValue("DOME_AUTOSYNC", "DOME_AUTOSYNC_DISABLE", true);
                    }
                } catch (ArgumentException ex) {
                    Logger.Warning($"INDIDome: Cannot set DriverFollowing — {ex.Message}");
                }
            }
        }

        // ── Operations ────────────────────────────────────────────────────────────

        // Ceiling for the motion poll loops below (azimuth slew, shutter, park). All of
        // them are cleared only by driver updates, so a driver that stops replying
        // mid-motion would otherwise leave the caller polling forever with only its own
        // cancellation token as a way out.
        private static readonly TimeSpan MoveTimeout = TimeSpan.FromMinutes(5);

        // internal so tests can shorten it
        internal TimeSpan MoveCompletionTimeout { get; set; } = MoveTimeout;

        public async Task SlewToAzimuth(double azimuth, CancellationToken ct) {
            if (!Connected || !CanSetAzimuth) return;
            await SetNumberValuesAsync("ABS_DOME_POSITION", TimeSpan.FromSeconds(10),
                ("DOME_ABSOLUTE_POSITION", azimuth));
            await WaitForMotionEndAsync("slew", "ABS_DOME_POSITION", _ => Slewing, ct);

            ThrowIfServerConnectionLost("Dome slew");
        }

        public async Task OpenShutter(CancellationToken ct) {
            if (!Connected || !CanSetShutter) return;
            await SetSwitchValueAsync("DOME_SHUTTER", "SHUTTER_OPEN", true, TimeSpan.FromSeconds(10));

            // Gate on the property's own Busy state as well as the derived _shutterState:
            // if the ack timed out (a driver that flips to Busy late), _shutterState may
            // never have been set to Opening and the derived state alone would return while
            // the shutter is still moving (mirrors Park's state-based gating).
            await WaitForMotionEndAsync("opening of the shutter", "DOME_SHUTTER",
                p => _shutterState == ShutterState.ShutterOpening || p?.State == PropertyState.Busy, ct);

            ThrowIfServerConnectionLost("Opening the dome shutter");
        }

        public async Task CloseShutter(CancellationToken ct) {
            if (!Connected || !CanSetShutter) return;
            await SetSwitchValueAsync("DOME_SHUTTER", "SHUTTER_CLOSE", true, TimeSpan.FromSeconds(10));

            // See OpenShutter for why this also watches the property's own Busy state.
            await WaitForMotionEndAsync("closing of the shutter", "DOME_SHUTTER",
                p => _shutterState == ShutterState.ShutterClosing || p?.State == PropertyState.Busy, ct);

            ThrowIfServerConnectionLost("Closing the dome shutter");
        }

        public async Task Park(CancellationToken ct) {
            if (!Connected || !CanPark) return;
            await SetSwitchValueAsync("DOME_PARK", "PARK", true, TimeSpan.FromSeconds(10));

            // Wait for the park motion itself to finish, not just for the PARK switch value
            // to read true — libindi sets DOME_PARK's PARK switch ON with state=Busy as soon
            // as parking starts, so gating on the switch VALUE alone (AtPark) can return while
            // the dome is still moving. Mirrors INDITelescope.ParkAsync's state-based gating.
            await WaitForMotionEndAsync("park", "DOME_PARK", p => Slewing || p?.State == PropertyState.Busy, ct);

            ThrowIfServerConnectionLost("Dome park");
        }

        /// <summary>
        /// Waits until <paramref name="moving"/> no longer holds for the command's property. None of the endings that are
        /// not an arrival counts as one: the property in Alert (the driver rejected the command or the motion failed) and
        /// no end within <see cref="MoveCompletionTimeout"/> throw, and a cancellation stops the dome first, so a cancelled
        /// sequence doesn't leave the shutter or the dome moving.
        /// </summary>
        private async Task WaitForMotionEndAsync(string what, string propertyName, Func<INDIProperty, bool> moving, CancellationToken ct) {
            var started = DateTime.UtcNow;
            try {
                while (true) {
                    var prop = GetProperty(propertyName);
                    if (prop?.State == PropertyState.Alert) {
                        throw new InvalidOperationException($"The dome reported a failure of the {what} ({propertyName} in Alert state)");
                    }
                    if (!moving(prop)) {
                        break;
                    }
                    if (DateTime.UtcNow - started > MoveCompletionTimeout) {
                        throw new TimeoutException($"The dome did not report the end of the {what} within {MoveCompletionTimeout.TotalSeconds:F0} s");
                    }
                    await Task.Delay(500, ct);
                }

                ct.ThrowIfCancellationRequested();
            } catch (OperationCanceledException) {
                Logger.Info($"INDIDome: {what} cancelled, aborting the dome's motion");
                Abort();
                throw;
            }
        }

        public void FindHome() {
            if (!Connected || !CanFindHome) return;
            try {
                var prop = GetSwitchProperty("FIND_HOME") ?? GetSwitchProperty("HOME_PARK");
                if (prop != null) {
                    SetSwitchValue(prop.Name, prop.Switches.First().Name, true);
                }
            } catch (ArgumentException ex) {
                Logger.Warning($"INDIDome: Cannot find home — {ex.Message}");
            }
        }

        public void SetPark() {
            if (!Connected) return;
            try {
                // libindi's dome park-option vector is DOME_PARK_OPTION with PARK_CURRENT /
                // PARK_DEFAULT / PARK_WRITE_DATA (mirroring TELESCOPE_PARK_OPTION). The
                // previous DOME_PARK.DOME_PARK_WRITE target exists in no driver, so this
                // silently never worked.
                SetSwitchValue("DOME_PARK_OPTION", "PARK_CURRENT", true);
            } catch (ArgumentException ex) {
                Logger.Warning($"INDIDome: Cannot set park position — {ex.Message}");
            }
        }

        public void SyncToAzimuth(double azimuth) {
            if (!Connected || !CanSyncAzimuth) return;
            try {
                SetNumberValue("DOME_SYNC", "DOME_SYNC_VALUE", azimuth);
            } catch (ArgumentException ex) {
                Logger.Warning($"INDIDome: Cannot sync azimuth — {ex.Message}");
            }
        }

        public void Abort() {
            if (!Connected) return;
            try {
                SetSwitchValue("DOME_ABORT_MOTION", "ABORT", true);
            } catch (ArgumentException ex) {
                Logger.Warning($"INDIDome: Cannot abort — {ex.Message}");
            }
        }

        // Action/Command* are inherited from INDIDevice — the redeclarations that used to
        // live here hid the base virtuals (CS0114) without changing behavior.
    }
}
