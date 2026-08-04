#region "copyright"

/*
    Copyright © 2025 Nico Trost <nico.trost57@gmail.com> and the PI.N.S. contributors

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
using System.Threading;
using System.Threading.Tasks;

namespace NINA.INDI.Devices {

    public class INDIFocuser : INDIDevice, IINDIFocuser {






        public override void OnTextPropertyUpdated(INDITextProperty p) {
            base.OnTextPropertyUpdated(p);
        }

        public override void OnNumberPropertyUpdated(INDINumberProperty p) {
            base.OnNumberPropertyUpdated(p);

            switch (p.Name) {
                case "ABS_FOCUS_POSITION":
                    // Absolute position
                    Absolute = true;

                    // Check if moving based on state
                    IsMoving = p.State == PropertyState.Busy;
                    break;
                case "REL_FOCUS_POSITION":
                    // Relative-only focusers (e.g. HitecAstro DC) never send ABS_FOCUS_POSITION,
                    // so Absolute stays false and motion must be tracked from this property's
                    // state instead. Guarded on !Absolute so it doesn't override the absolute
                    // path for drivers that expose both properties.
                    if (!Absolute) {
                        IsMoving = p.State == PropertyState.Busy;
                    }
                    break;
            }
        }

        public override void OnSwitchPropertyUpdated(INDISwitchProperty p) {
            base.OnSwitchPropertyUpdated(p);
        }

        public override void OnBlobPropertyUpdated(INDIBlobProperty p) {
            base.OnBlobPropertyUpdated(p);
        }





        public INDIFocuser(INDIDeviceInfo device) : base(device) {
        }

        /// <summary>
        /// Specify critical properties that must arrive before Connect() completes
        /// </summary>
        protected override string[] GetRequiredConnectionProperties() {
            return ["ABS_FOCUS_POSITION"];
        }

        public bool Absolute { get; private set; }
        public bool IsMoving { get; private set; }

        public int MaxIncrement {
            get {
                if (MaxStep > 0) {
                    return MaxStep;
                }
                // No FOCUS_MAX (MaxStep is the -1 "unknown" sentinel) doesn't mean a
                // relative move can safely be any size - REL_FOCUS_POSITION carries its
                // own driver-reported bound (e.g. a HitecAstro DC's own min/max step
                // range), and IUUpdateNumber rejects writes outside it. Use that instead
                // of leaving relative-move chunking uncapped.
                var relMax = GetNumberPropertyMax("REL_FOCUS_POSITION", "FOCUS_RELATIVE_POSITION");
                return relMax > 0 ? (int)relMax.Value : -1;
            }
        }

        public bool CanSetMaxStep {
            get {
                var prop = GetNumberProperty("FOCUS_MAX");
                return prop != null;
            }
        }

        public int MaxStep {
            // -1 (not 0) signals "no known limit" when FOCUS_MAX is absent, matching the
            // sentinel that CanSetMaxStep-gated callers already expect (e.g. IndiFocuser.MaxStep).
            // A 0 default here previously made relative-move step chunking collapse to 0 and
            // spin forever on focusers without FOCUS_MAX (e.g. HitecAstro DC).
            get => (int)(GetNumberPropertyValue("FOCUS_MAX", "FOCUS_MAX_VALUE") ?? -1.0);
            set {
                if (!Connected) {
                    Logger.Warning("Cannot set MaxStep: not connected");
                    return;
                }
                SetNumberValue("FOCUS_MAX", "FOCUS_MAX_VALUE", value);
                Logger.Info($"Set focuser MaxStep to {value}");
            }
        }

        public int Position => (int)(GetNumberPropertyValue("ABS_FOCUS_POSITION", "FOCUS_ABSOLUTE_POSITION") ?? 0.0);

        public double StepSize => double.NaN;
        public bool TempComp => false;
        public bool TempCompAvailable => false;
        public double Temperature => GetNumberPropertyValue("FOCUS_TEMPERATURE", "TEMPERATURE") ?? double.NaN;

        public bool CanReverse {
            get {
                var prop = GetSwitchProperty("FOCUS_REVERSE_MOTION");
                return prop != null;
            }
        }

        public bool Reverse {
            get => GetSwitchPropertyValue("FOCUS_REVERSE_MOTION", "INDI_ENABLED") ?? false;
            set {
                if (CanReverse && Connected) {
                    try {
                        if (value) {
                            SetSwitchValue("FOCUS_REVERSE_MOTION", "INDI_ENABLED", true);
                        } else {
                            SetSwitchValue("FOCUS_REVERSE_MOTION", "INDI_DISABLED", true);
                        }
                    } catch (ArgumentException ex) {
                        throw new NotImplementedException(ex.Message, ex);
                    }
                }
            }
        }

        public void SyncPosition(int position) {
            SetNumberValue("FOCUS_SYNC", "FOCUS_SYNC_VALUE", position);
        }


        public void Halt() {
            try {
                SetSwitchValue("FOCUS_ABORT_MOTION", "ABORT", true);
            } catch (ArgumentException ex) {
                throw new NotImplementedException(ex.Message, ex);
            }
        }

        public void Move(int position) {
            if (!Connected) {
                Logger.Warning("Cannot move focuser: not connected");
                return;
            }

            IsMoving = true;
            try {
                if (Absolute) {
                    SetNumberValue("ABS_FOCUS_POSITION", "FOCUS_ABSOLUTE_POSITION", position);
                    Logger.Info($"Commanded focuser to move to position {position}");
                } else {
                    // Relative-only focuser (no ABS_FOCUS_POSITION, e.g. HitecAstro DC).
                    // IndiFocuser.MoveInternalRelative already computes "position" as a signed
                    // step delta in this case, not an absolute target - see its Math.Min/sign
                    // handling. Translate that into this driver's two relative-move properties:
                    // a direction switch plus an unsigned step count.
                    if (position == 0) {
                        IsMoving = false;
                        return;
                    }
                    SetSwitchValue("FOCUS_MOTION", position > 0 ? "FOCUS_OUTWARD" : "FOCUS_INWARD", true);
                    SetNumberValue("REL_FOCUS_POSITION", "FOCUS_RELATIVE_POSITION", Math.Abs(position));
                    Logger.Info($"Commanded focuser to move {Math.Abs(position)} steps {(position > 0 ? "outward" : "inward")}");
                }
            } catch {
                // Don't strand IsMoving = true forever if the property write itself fails -
                // MoveAsync's poll loop has no other way to learn the move never started.
                IsMoving = false;
                throw;
            }
        }

        // Ceiling for MoveAsync's poll loop. IsMoving is only ever cleared by a driver update
        // (ABS_FOCUS_POSITION leaving Busy state), so a driver that stops replying mid-move
        // (crash, cable pull) would otherwise leave the caller polling forever with only its
        // own cancellation token as a way out.
        private static readonly TimeSpan MoveTimeout = TimeSpan.FromMinutes(5);

        public async Task MoveAsync(int position, CancellationToken ct = default) {
            if (!Connected) {
                throw new InvalidOperationException("Cannot move focuser: not connected");
            }

            // Initiate the move
            Move(position);

            var started = DateTime.UtcNow;
            while (IsMoving && !ct.IsCancellationRequested) {
                if (DateTime.UtcNow - started > MoveTimeout) {
                    Logger.Warning($"Focuser did not report move completion within {MoveTimeout.TotalSeconds:F0}s — giving up waiting");
                    break;
                }
                await Task.Delay(100, ct);
            }

            Logger.Debug($"Focuser reached position {Position}");
        }

        // Action/Command* are inherited from INDIDevice — the redeclarations that used to
        // live here hid the base virtuals (CS0114) without changing behavior.
    }
}
