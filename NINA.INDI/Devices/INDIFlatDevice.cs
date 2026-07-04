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

        public override void OnNumberPropertyUpdated(INDINumberProperty p) {
            base.OnNumberPropertyUpdated(p);
        }

        public override void OnSwitchPropertyUpdated(INDISwitchProperty p) {
            base.OnSwitchPropertyUpdated(p);

            if (p.Name == "CAP_PARK" && p.State != PropertyState.Busy) {
                var sw = GetSwitchProperty("CAP_PARK");
                coverState = (sw?.Switches.FirstOrDefault(s => s.Name == "PARK")?.Value ?? true) ? CoverState.Closed : CoverState.Open;
            }
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

        private CoverState coverState = CoverState.Unknown;
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

        // Ceiling for Open/Close's poll loops. coverState is only ever advanced by a driver
        // update (CAP_PARK leaving Busy state), so a driver that stops replying mid-motion
        // would otherwise leave the caller polling forever with only its own cancellation
        // token as a way out.
        private static readonly TimeSpan MoveTimeout = TimeSpan.FromMinutes(2);

        public async Task<bool> Open(CancellationToken ct, int delay = 300) {
            if (!Connected || !SupportsOpenClose) {
                return false;
            }

            // Initiate the move
            coverState = CoverState.NeitherOpenNorClosed;
            SetSwitchValue("CAP_PARK", "UNPARK", true);
            Logger.Info("Commanded flat device to unpark");

            var started = DateTime.UtcNow;
            while (coverState == CoverState.NeitherOpenNorClosed && !ct.IsCancellationRequested) {
                if (DateTime.UtcNow - started > MoveTimeout) {
                    Logger.Warning($"Flat device did not report unpark completion within {MoveTimeout.TotalSeconds:F0}s — giving up waiting");
                    break;
                }
                await Task.Delay(delay, ct);
            }

            Logger.Debug($"FlatDevice reached unpark position");

            return coverState == CoverState.Open;
        }

        public async Task<bool> Close(CancellationToken ct, int delay = 300) {
            if (!Connected || !SupportsOpenClose) {
                return false;
            }

            // Initiate the move
            coverState = CoverState.NeitherOpenNorClosed;
            SetSwitchValue("CAP_PARK", "PARK", true);
            Logger.Info("Commanded flat device to park");

            var started = DateTime.UtcNow;
            while (coverState == CoverState.NeitherOpenNorClosed && !ct.IsCancellationRequested) {
                if (DateTime.UtcNow - started > MoveTimeout) {
                    Logger.Warning($"Flat device did not report park completion within {MoveTimeout.TotalSeconds:F0}s — giving up waiting");
                    break;
                }
                await Task.Delay(delay, ct);
            }

            Logger.Debug($"FlatDevice reached park position");

            return coverState == CoverState.Closed;
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