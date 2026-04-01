#region "copyright"

/*
    Copyright © 2025-2026 Nico Trost <nico.trost57@gmail.com> and the PI.N.S. contributors

    This file is part of PI 'N' Stars.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

namespace NINA.INDI {

    /// <summary>
    /// Describes a single controllable channel discovered from an INDI AUX device
    /// (e.g. a DC power port, USB port, or dew-heater duty-cycle on a Pegasus UPB).
    /// </summary>
    public sealed class INDISwitchDescriptor {

        /// <summary>INDI property vector name, e.g. "POWER_CHANNELS".</summary>
        public string PropertyName { get; set; } = string.Empty;

        /// <summary>Human-readable label for the property group, e.g. "DC Power Control".</summary>
        public string PropertyLabel { get; set; } = string.Empty;

        /// <summary>INDI element name within the vector, e.g. "POWER_CHANNEL_1".</summary>
        public string ElementName { get; set; } = string.Empty;

        /// <summary>Human-readable label for the element, e.g. "DC Port 1".</summary>
        public string ElementLabel { get; set; } = string.Empty;

        /// <summary>Whether the channel can be written (permissions include Write).</summary>
        public bool IsWritable { get; set; }

        /// <summary>
        /// True when the backing INDI property is a switch vector (boolean on/off),
        /// false when it is a number vector (e.g. dew-heater duty-cycle 0–100 %).
        /// </summary>
        public bool IsBoolSwitch { get; set; }

        /// <summary>Minimum allowed value (0 for boolean switches).</summary>
        public double Min { get; set; }

        /// <summary>Maximum allowed value (1 for boolean switches).</summary>
        public double Max { get; set; }

        /// <summary>Step size (1 for boolean switches).</summary>
        public double Step { get; set; }
    }
}