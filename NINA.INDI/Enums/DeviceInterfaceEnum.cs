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

namespace NINA.INDI.Enums {
    [Flags]
    public enum DeviceInterface {
        GENERAL_INTERFACE = 0,
        TELESCOPE_INTERFACE = (1 << 0),
        CCD_INTERFACE = (1 << 1),
        GUIDER_INTERFACE = (1 << 2),
        FOCUSER_INTERFACE = (1 << 3),
        FILTER_INTERFACE = (1 << 4),
        DOME_INTERFACE = (1 << 5),
        GPS_INTERFACE = (1 << 6),
        WEATHER_INTERFACE = (1 << 7),
        AO_INTERFACE = (1 << 8),
        DUSTCAP_INTERFACE = (1 << 9),
        LIGHTBOX_INTERFACE = (1 << 10),
        DETECTOR_INTERFACE = (1 << 11),
        ROTATOR_INTERFACE = (1 << 12),
        SPECTROGRAPH_INTERFACE = (1 << 13),
        AUX_INTERFACE = (1 << 15),
    }
}
