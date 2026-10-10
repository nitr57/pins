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

using System.Globalization;

namespace NINA.Equipment.SDK.TelescopeSDKs.OnStepXSDK {

    /// <summary>
    /// The controller's last command error as :GE# reports it: OnStepX's CommandError
    /// (src/lib/commands/CommandErrors.h, the same in 10.20a and 10.24c).
    /// </summary>
    public enum OnStepXCommandError {
        None = 0,
        Zero = 1,
        CommandUnknown = 2,
        ReplyUnknown = 3,
        ParameterRange = 4,
        ParameterForm = 5,
        AlignFailed = 6,
        AlignNotActive = 7,
        NotParkedOrAtHome = 8,
        Parked = 9,
        ParkFailed = 10,
        NotParked = 11,
        NoParkPositionSet = 12,
        GotoFailed = 13,
        LibraryFull = 14,
        BelowHorizon = 15,
        AboveOverhead = 16,
        Standby = 17,
        InPark = 18,
        InSlew = 19,
        OutsideLimits = 20,
        HardwareFault = 21,
        InMotion = 22,
        Unspecified = 23,
    }

    public static class OnStepXCommandErrorExtensions {

        /// <summary>
        /// A short reason for a refused command: OnStepX's own error texts (ProcessCmds.cpp), worded out where the park
        /// and home code gives the cause (standby: motors off, or date and time not set).
        /// </summary>
        public static string Describe(this OnStepXCommandError error) => error switch {
            OnStepXCommandError.None => "no error",
            OnStepXCommandError.Zero => "refused",
            OnStepXCommandError.CommandUnknown => "unknown command",
            OnStepXCommandError.ReplyUnknown => "invalid reply",
            OnStepXCommandError.ParameterRange => "parameter out of range",
            OnStepXCommandError.ParameterForm => "bad parameter format",
            OnStepXCommandError.AlignFailed => "align failed",
            OnStepXCommandError.AlignNotActive => "align not active",
            OnStepXCommandError.NotParkedOrAtHome => "not parked or at home",
            OnStepXCommandError.Parked => "already parked",
            OnStepXCommandError.ParkFailed => "park failed or already parking",
            OnStepXCommandError.NotParked => "not parked",
            OnStepXCommandError.NoParkPositionSet => "no park position set",
            OnStepXCommandError.GotoFailed => "goto failed",
            OnStepXCommandError.LibraryFull => "library full",
            OnStepXCommandError.BelowHorizon => "below the horizon limit",
            OnStepXCommandError.AboveOverhead => "above the overhead limit",
            OnStepXCommandError.Standby => "controller in standby (motors off, or date and time not set)",
            OnStepXCommandError.InPark => "mount is parked",
            OnStepXCommandError.InSlew => "a slew is in progress",
            OnStepXCommandError.OutsideLimits => "outside limits",
            OnStepXCommandError.HardwareFault => "hardware fault",
            OnStepXCommandError.InMotion => "mount in motion",
            OnStepXCommandError.Unspecified => "unspecified error",
            _ => $"error {((int)error).ToString(CultureInfo.InvariantCulture)}",
        };
    }

    /// <summary>A command the controller answered with '0'; <see cref="Error"/> is its reason from :GE#, when readable.</summary>
    public class OnStepXCommandRefusedException : OnStepXException {
        /// <param name="reason">Overrides the generic text for <paramref name="error"/> where a command gives it its own meaning.</param>
        public OnStepXCommandRefusedException(string command, OnStepXCommandError? error, string? reason = null)
            : base($"{command} refused: {reason ?? (error is { } e ? e.Describe() : "reason unknown")}") {
            Command = command;
            Error = error;
            reasonOverride = reason;
        }

        private readonly string? reasonOverride;

        public string Command { get; }

        public OnStepXCommandError? Error { get; }

        /// <summary>The reason alone, for messages that name the action themselves.</summary>
        public string Reason => reasonOverride ?? (Error is { } e ? e.Describe() : "reason unknown");
    }
}
