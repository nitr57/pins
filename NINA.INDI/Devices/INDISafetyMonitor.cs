#region "copyright"

/*
    Copyright © 2025-2026 Nico Trost <nico.trost57@gmail.com> and the PI.N.S. contributors

    This file is part of PI 'N' Stars.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.INDI.Enums;
using NINA.INDI.Interfaces;
using NINA.INDI.Protocol;
using System;
using System.Collections.Generic;

namespace NINA.INDI.Devices {

    public class INDISafetyMonitor : INDIDevice, IINDISafetyMonitor {

        public INDISafetyMonitor(INDIDeviceInfo device) : base(device) {
        }

        /// <summary>
        /// Specify critical properties that must arrive before Connect() completes.
        /// WEATHER_STATUS is the standard INDI light property used by safety monitors.
        /// </summary>
        protected override string[] GetRequiredConnectionProperties() {
            return ["WEATHER_STATUS"];
        }

        public override void OnTextPropertyUpdated(INDITextProperty p) {
            base.OnTextPropertyUpdated(p);
        }

        public override void OnNumberPropertyUpdated(INDINumberProperty p) {
            base.OnNumberPropertyUpdated(p);
        }

        public override void OnSwitchPropertyUpdated(INDISwitchProperty p) {
            base.OnSwitchPropertyUpdated(p);
        }

        public override void OnBlobPropertyUpdated(INDIBlobProperty p) {
            base.OnBlobPropertyUpdated(p);
        }

        public override void OnLightPropertyUpdated(INDILightProperty p) {
            base.OnLightPropertyUpdated(p);
        }

        /// <summary>
        /// Returns true when the WEATHER_STATUS light property state is OK, indicating
        /// that conditions are safe to observe. Any other state (Idle, Busy, Alert) is
        /// treated as unsafe.
        /// </summary>
        public bool IsSafe => GetLightPropertyState("WEATHER_STATUS") == PropertyState.Ok;

        // Action/Command* are inherited from INDIDevice — the redeclarations that used to
        // live here hid the base virtuals (CS0114) without changing behavior.
    }
}
