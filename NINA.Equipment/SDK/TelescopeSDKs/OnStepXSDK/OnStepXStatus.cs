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

using System.Diagnostics.CodeAnalysis;

namespace NINA.Equipment.SDK.TelescopeSDKs.OnStepXSDK {

    public enum OnStepXParkState {
        Unparked,
        Parking,
        Parked,
        ParkFailed,
    }

    public enum OnStepXMountType {
        Unknown,
        GermanEquatorial,
        Fork,
        ForkAlt,
        AltAz,
    }

    /// <summary>The general error in the last character of :GU#, numbered as INDI's LX200_OnStep Errors.</summary>
    public enum OnStepXError {
        None = 0,
        MotorFault = 1,
        AltitudeMin = 2,
        LimitSense = 3,
        DecLimit = 4,
        AzimuthLimit = 5,
        UnderPoleLimit = 6,
        MeridianLimit = 7,
        SyncLimit = 8,
        ParkFailed = 9,
        GotoSyncFailed = 10,
        Unspecified = 11,
        AltitudeMax = 12,
        WeatherInitFailed = 13,
        SiteNotInitialized = 14,
        NvInitFailed = 15,
    }

    /// <summary>
    /// The controller status from :GU#: flag characters, then the pulse-guide rate index, the move rate index and the
    /// error code as the last three characters. Decoded as OnStepX 10.20a writes it (Status.command.cpp); INDI's
    /// LX200_OnStep reads 'W' as a PEC state from OnStep 3.x, but in OnStepX it is the pier side.
    /// </summary>
    public sealed record OnStepXStatus {

        private OnStepXStatus(string raw) {
            Raw = raw;
        }

        /// <summary>The reply as received, without the '#'.</summary>
        public string Raw { get; }

        /// <summary>'n' is "not tracking".</summary>
        public bool Tracking { get; private init; }

        /// <summary>
        /// 'N' is "no goto" (goTo.state == GS_NONE); INDI reads its absence as a slew, with or without tracking. A manual
        /// move is not a goto: see <see cref="ManualMove"/>.
        /// </summary>
        public bool Slewing { get; private init; }

        public OnStepXParkState Park { get; private init; }

        /// <summary>'H': both axes within the home tolerance of the home position (Mount::isHome).</summary>
        public bool AtHome { get; private init; }

        /// <summary>'h': moving to home (HS_HOMING), cleared when the controller has finished homing.</summary>
        public bool Homing { get; private init; }

        /// <summary>'G': a pulse guide is running, on either axis.</summary>
        public bool PulseGuiding { get; private init; }

        /// <summary>
        /// 'g': a guide move other than a pulse is running (Guide::active), i.e. a manual move (:Mn#, :Me#, ...). Not a goto,
        /// so <see cref="Slewing"/> stays false.
        /// </summary>
        public bool ManualMove { get; private init; }

        /// <summary>'T' east, 'W' west, 'o' none (at home): the same value as :Gm# (both Mount::getMountPosition).</summary>
        public OnStepXPierSide PierSide { get; private init; }

        /// <summary>'w': paused at home during a goto, waiting to continue.</summary>
        public bool WaitingAtHome { get; private init; }

        public OnStepXMountType MountType { get; private init; }

        /// <summary>Index of the pulse-guide rate (0 = 0.25x, 1 = 0.5x, 2 = 1x, ...), or -1.</summary>
        public int PulseGuideRateIndex { get; private init; }

        /// <summary>Index of the manual move rate, or -1.</summary>
        public int MoveRateIndex { get; private init; }

        /// <summary>The error code; outside the enum's range when the firmware knows more errors.</summary>
        public OnStepXError Error { get; private init; }

        /// <summary>
        /// Like INDI, a status counts only with one of the park characters p/I/P/F and the three trailing characters.
        /// </summary>
        public static bool TryParse(string? reply, [NotNullWhen(true)] out OnStepXStatus? status) {
            status = null;
            if (reply is null || reply.Length < 4) {
                return false;
            }

            string flags = reply[..^3];
            OnStepXParkState? park = ParkStateOf(flags);
            if (park is null) {
                return false;
            }

            status = new OnStepXStatus(reply) {
                Tracking = !flags.Contains('n'),
                Slewing = !flags.Contains('N'),
                Park = park.Value,
                AtHome = flags.Contains('H'),
                Homing = flags.Contains('h'),
                PulseGuiding = flags.Contains('G'),
                ManualMove = flags.Contains('g'),
                PierSide = flags.Contains('T') ? OnStepXPierSide.East : flags.Contains('W') ? OnStepXPierSide.West : OnStepXPierSide.Unknown,
                WaitingAtHome = flags.Contains('w'),
                MountType = MountTypeOf(flags),
                PulseGuideRateIndex = DigitOf(reply[^3]),
                MoveRateIndex = DigitOf(reply[^2]),
                Error = (OnStepXError)(reply[^1] - '0'),
            };
            return true;
        }

        // The controller reports exactly one of them.
        private static OnStepXParkState? ParkStateOf(string flags) {
            if (flags.Contains('P')) {
                return OnStepXParkState.Parked;
            }
            if (flags.Contains('I')) {
                return OnStepXParkState.Parking;
            }
            if (flags.Contains('F')) {
                return OnStepXParkState.ParkFailed;
            }
            if (flags.Contains('p')) {
                return OnStepXParkState.Unparked;
            }
            return null;
        }

        private static OnStepXMountType MountTypeOf(string flags) {
            if (flags.Contains('E')) {
                return OnStepXMountType.GermanEquatorial;
            }
            if (flags.Contains('K')) {
                return OnStepXMountType.Fork;
            }
            if (flags.Contains('k')) {
                return OnStepXMountType.ForkAlt;
            }
            if (flags.Contains('A')) {
                return OnStepXMountType.AltAz;
            }
            return OnStepXMountType.Unknown;
        }

        private static int DigitOf(char c) => c is >= '0' and <= '9' ? c - '0' : -1;
    }
}
