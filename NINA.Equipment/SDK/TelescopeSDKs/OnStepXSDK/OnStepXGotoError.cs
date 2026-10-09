#region "copyright"

/*
    Copyright © 2026 Nico Trost <nico.trost57@gmail.com> and the PI.N.S. contributors

    This file is part of PI 'N' Stars.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

#nullable enable

namespace NINA.Equipment.SDK.TelescopeSDKs.OnStepXSDK {

    /// <summary>The answer to :MS# (goto) and the code in :CM#'s "E&lt;n&gt;" (sync), as INDI's LX200_OnStep::slewError reads them.</summary>
    public enum OnStepXGotoError {
        None = 0,
        BelowHorizon = 1,
        AboveOverhead = 2,
        Standby = 3,
        Parked = 4,
        GotoInProgress = 5,
        OutsideLimits = 6,
        HardwareFault = 7,
        AlreadyInMotion = 8,
        Unspecified = 9,
    }

    public enum OnStepXDirection {
        North,
        South,
        East,
        West,
    }

    /// <summary>The tracking rates INDI's LX200_OnStep selects (:TQ#, :TL#, :TS#).</summary>
    public enum OnStepXTrackingRate {
        Sidereal,
        Lunar,
        Solar,
    }

    public static class OnStepXGotoErrorExtensions {

        /// <summary>INDI's slewError texts.</summary>
        public static string Describe(this OnStepXGotoError error) => error switch {
            OnStepXGotoError.None => "no error",
            OnStepXGotoError.BelowHorizon => "below the horizon limit",
            OnStepXGotoError.AboveOverhead => "above the overhead limit",
            OnStepXGotoError.Standby => "controller in standby (turn tracking on)",
            OnStepXGotoError.Parked => "mount is parked",
            OnStepXGotoError.GotoInProgress => "goto in progress",
            OnStepXGotoError.OutsideLimits => "outside limits (max/min Dec, under pole limit, meridian limit, sync to the wrong pier side)",
            OnStepXGotoError.HardwareFault => "hardware fault",
            OnStepXGotoError.AlreadyInMotion => "already in motion",
            _ => "unspecified error",
        };
    }
}
