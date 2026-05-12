#region "copyright"

/*
    Copyright � 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

namespace NINA.Profile.Interfaces {

    public interface ISwitchSettings : ISettings {
        string Id { get; set; }
        string LastDeviceName { get; set; }
        string IndiDriver { get; set; }
        string IndiConnectionMode { get; set; }
        string IndiPort { get; set; }
        int IndiBaudRate { get; set; }
        bool IndiAutoSearch { get; set; }
        string IndiAddress { get; set; }
        int IndiPreConnectDelay { get; set; }
        int IndiPostConnectDelay { get; set; }
    }
}
