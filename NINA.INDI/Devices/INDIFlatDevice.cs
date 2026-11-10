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

    public class INDIFlatDevice : INDIDevice, IINDIFlatDevice {
        public override void OnTextPropertyUpdated(INDITextProperty p) {
            base.OnTextPropertyUpdated(p);
        }

        // A lost connection ends a running Open/Close as not opened/closed
        protected override void OnConnectionLost() {
            lock (moveLock) {
                if (move is { } running) {
                    running.Ended = true;
                    move = null;
                }

                coverState = CoverState.Unknown;
            }
        }

        public override void OnNumberPropertyUpdated(INDINumberProperty p) {
            base.OnNumberPropertyUpdated(p);
        }

        public override void OnSwitchPropertyUpdated(INDISwitchProperty p) {
            base.OnSwitchPropertyUpdated(p);

            if (p.Name != "CAP_PARK") {
                return;
            }

            bool park = p.Switches.FirstOrDefault(s => s.Name == Park)?.Value ?? false;
            bool unpark = p.Switches.FirstOrDefault(s => s.Name == Unpark)?.Value ?? false;

            lock (moveLock) {
                if (move is { } running) {
                    if (p.State == PropertyState.Alert) {
                        running.Result = false;
                        running.Ended = true;
                        move = null;
                    } else if (p.State != PropertyState.Busy && (running.Target == Park ? park : unpark)) {
                        running.Result = true;
                        running.Ended = true;
                        move = null;
                    } else {
                        // Busy, in transit with neither switch on, or the old end once more: still moving
                        coverState = CoverState.NeitherOpenNorClosed;
                        return;
                    }
                }

                coverState = CoverStateOf(p.State, park, unpark);
            }
        }

        // The INDI dust cap interface: PARK or UNPARK on is that end, also with Alert (a refused move restores the
        // previous switch, so the cover is still there); neither on (the initial definition, an abort, a cover in
        // transit) is unknown. Busy is a move in progress, e.g. one started from the control panel.
        private static CoverState CoverStateOf(PropertyState state, bool park, bool unpark) {
            if (state == PropertyState.Busy) {
                return CoverState.NeitherOpenNorClosed;
            }

            if (park) {
                return CoverState.Closed;
            }

            return unpark ? CoverState.Open : CoverState.Unknown;
        }

        public override void OnBlobPropertyUpdated(INDIBlobProperty p) {
            base.OnBlobPropertyUpdated(p);
        }





        public INDIFlatDevice(INDIDeviceInfo device) : base(device) {
        }

        /// <summary>
        /// Specify critical properties that must arrive before Connect() completes
        /// </summary>
        protected override string[] GetRequiredConnectionProperties() {
            return ["FLAT_LIGHT_INTENSITY"];
        }

        private const string Park = "PARK";
        private const string Unpark = "UNPARK";

        // Only driver updates set the cover state: SetSwitchValue writes the target into the local copy of CAP_PARK
        // before sending it, so the local switch says nothing about where the cover is.
        private volatile CoverState coverState = CoverState.Unknown;

        // The commanded move until a driver update ends it. Stock drivers publish CAP_PARK with neither switch on while
        // the cover travels (Wanderer V4 EC: Idle) and may send the old end with Ok after their Busy, so only an Alert
        // or the target switch without Busy ends it (indidustcapinterface.cpp: "Unknown: PARK and UNPARK are either not
        // toggled or state is IPS_ALERT"). Each Open/Close has its own: FlatDeviceVM lets an Open and a Close overlap
        // (a client's request during a sequence's), and the newer one supersedes the older.
        private sealed class CoverMove(string target) {
            public string Target { get; } = target;

            // true at the target, false with Alert, null when it ended without the driver's answer
            public bool? Result { get; set; }

            public bool Ended { get; set; }

            public bool Superseded { get; set; }
        }

        private readonly object moveLock = new();
        private CoverMove move;

        public CoverState CoverState => SupportsOpenClose ? coverState : CoverState.NotPresent;

        public int MaxBrightness {
            get {
                var prop = GetNumberProperty("FLAT_LIGHT_INTENSITY");
                return (int)(prop?.Numbers.FirstOrDefault(n => n.Name == "FLAT_LIGHT_INTENSITY_VALUE")?.Max ?? 0);
            }
        }

        public int MinBrightness {
            get {
                var prop = GetNumberProperty("FLAT_LIGHT_INTENSITY");
                return (int)(prop?.Numbers.FirstOrDefault(n => n.Name == "FLAT_LIGHT_INTENSITY_VALUE")?.Min ?? 0);
            }
        }

        public bool SupportsOpenClose {
            get {
                var prop = GetSwitchProperty("CAP_PARK");
                return prop != null;
            }
        }

        // PARK=On alone is the TARGET, not the position: drivers publish it with state=Busy
        // while the cover is still moving (see INDITelescope.AtPark).
        public bool IsParked =>
            (GetSwitchPropertyValue("CAP_PARK", "PARK") ?? false)
            && GetProperty("CAP_PARK")?.State != PropertyState.Busy;

        // Ceiling for Open/Close's wait: only a driver update ends a commanded move, so a driver that stops replying
        // mid-motion would otherwise leave the caller waiting with only its own cancellation token as a way out.
        // Internal so tests can shorten it.
        internal TimeSpan MoveTimeout { get; set; } = TimeSpan.FromMinutes(2);

        /// <summary>
        /// Opens the cover: true once the driver reports it open, false when it does not within
        /// <see cref="MoveTimeout"/> or the connection ends. Throws <see cref="InvalidOperationException"/> when the
        /// driver ends the move with Alert (refused or failed).
        /// </summary>
        public Task<bool> Open(CancellationToken ct, int delay = 300) {
            return MoveCover(Unpark, "opening", ct, delay);
        }

        /// <summary>Closes the cover; see <see cref="Open"/>.</summary>
        public Task<bool> Close(CancellationToken ct, int delay = 300) {
            return MoveCover(Park, "closing", ct, delay);
        }

        private async Task<bool> MoveCover(string target, string verb, CancellationToken ct, int delay) {
            if (!Connected || !SupportsOpenClose) {
                return false;
            }

            if (GetSwitchProperty("CAP_PARK")?.Switches.Any(s => s.Name == target) != true) {
                Logger.Warning($"[{DeviceName}] CAP_PARK has no {target} switch; not {verb} the cover");
                return false;
            }

            var mine = new CoverMove(target);
            lock (moveLock) {
                if (move is { } older) {
                    older.Superseded = true;
                    older.Ended = true;
                }

                move = mine;
                coverState = CoverState.NeitherOpenNorClosed;
            }

            try {
                SetSwitchValue("CAP_PARK", target, true);
                Logger.Info($"[{DeviceName}] Commanded the flat device to {target.ToLowerInvariant()}");

                var started = DateTime.UtcNow;
                while (true) {
                    lock (moveLock) {
                        if (mine.Ended) {
                            break;
                        }
                    }

                    if (!Connected) {
                        Logger.Warning($"[{DeviceName}] The flat device disconnected while {verb} the cover");
                        break;
                    }

                    if (DateTime.UtcNow - started > MoveTimeout) {
                        Logger.Warning($"[{DeviceName}] The flat device did not report the end of {verb} the cover within {MoveTimeout.TotalSeconds:F0} s; giving up waiting");
                        break;
                    }

                    await Task.Delay(delay, ct);
                }
            } finally {
                lock (moveLock) {
                    if (ReferenceEquals(move, mine)) {
                        // timeout, cancellation, lost connection or a failed send: where the cover is, only the
                        // driver's next update tells
                        mine.Ended = true;
                        move = null;
                        coverState = CoverState.Unknown;
                    }
                }
            }

            bool? result;
            bool superseded;
            lock (moveLock) {
                // also an answer that came after the wait gave up
                result = mine.Result;
                superseded = mine.Superseded;
            }

            if (superseded) {
                Logger.Warning($"[{DeviceName}] {char.ToUpperInvariant(verb[0])}{verb[1..]} the cover was superseded by another cover command");
                return false;
            }

            if (result == false) {
                throw new InvalidOperationException($"The flat device driver reported an error (CAP_PARK Alert) while {verb} the cover");
            }

            Logger.Debug($"[{DeviceName}] Flat device {verb} the cover: {(result == true ? "done" : "not confirmed")}");
            return result == true;
        }

        public bool SupportsOnOff {
            get {
                var prop = GetSwitchProperty("FLAT_LIGHT_CONTROL");
                return prop != null;
            }
        }

        public bool LightOn {
            get => GetSwitchPropertyValue("FLAT_LIGHT_CONTROL", "FLAT_LIGHT_ON") ?? false;
            set {
                if (SupportsOnOff && Connected) {
                    try {
                        if (value) {
                            SetSwitchValue("FLAT_LIGHT_CONTROL", "FLAT_LIGHT_ON", true);
                        } else {
                            SetSwitchValue("FLAT_LIGHT_CONTROL", "FLAT_LIGHT_OFF", true);
                        }
                    } catch (ArgumentException ex) {
                        throw new NotImplementedException(ex.Message, ex);
                    }
                }
            }
        }

        public bool CanSetBrightness {
            get {
                var value = GetNumberPropertyValue("FLAT_LIGHT_INTENSITY", "FLAT_LIGHT_INTENSITY_VALUE");
                return value != null;
            }
        }

        public int Brightness {
            get => (int)(GetNumberPropertyValue("FLAT_LIGHT_INTENSITY", "FLAT_LIGHT_INTENSITY_VALUE") ?? 0);
            set {
                if (CanSetBrightness && Connected) {
                    try {
                        SetNumberValue("FLAT_LIGHT_INTENSITY", "FLAT_LIGHT_INTENSITY_VALUE", value);
                        Logger.Info($"Set brightness to {value}");
                    } catch (ArgumentException ex) {
                        throw new NotImplementedException(ex.Message, ex);
                    }
                }
            }
        }

        // Action/Command* are inherited from INDIDevice — the redeclarations that used to
        // live here hid the base virtuals (CS0114) without changing behavior.
    }
}