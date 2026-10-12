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

using NINA.Core.Utility;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;

namespace NINA.Equipment.SDK.TelescopeSDKs.OnStepXSDK {

    /// <summary>The on/off settings of a Proxisky UMi, INDI LX200_Proxisky's switches.</summary>
    public enum ProxiskySwitch {
        StatusLed,
        PowerLossMemory,
        AsiairHoming,
        SupervisedHome,
        DecSecondHome,
        AutoTracking,
        RaLimits,
        DecLimits,
        AntiCollision,
        SupervisedGoto,
    }

    /// <summary>The axis of the vendor's PID commands: 0 RA/Azm, 1 Dec/Alt.</summary>
    public enum ProxiskyAxis {
        Ra = 0,
        Dec = 1,
    }

    /// <summary>The servo gains of one axis (:Pnag0#, :Pnag1#).</summary>
    public sealed record ProxiskyPidGains(int AngleKp, int AngleKi, int SpeedKp, int SpeedKi);

    /// <summary>
    /// The vendor settings of a Proxisky UMi as <see cref="ProxiskyUmiDevice.ReadSettings"/> reads them. A null value is a
    /// feature the mount does not have or did not answer for. A setting that takes effect at the next power cycle shows
    /// the value last written and is named in <see cref="RestartPending"/>.
    /// </summary>
    /// <param name="StatusLed">The status LED (:PsLgb#), when the board has one (:PsLga#). Takes effect at the next power cycle.</param>
    /// <param name="PowerLossMemory">The position is kept over a power loss (:Pscg#), when the board can (:Pbc#).</param>
    /// <param name="AsiairHoming">Homing as an ASIAIR expects it (:PsAg#).</param>
    /// <param name="SupervisedHome">"Super Home" (:PsShg#); a UMi20S 1.0.6 refuses every change.</param>
    /// <param name="DecSecondHome">The Dec axis' second home position (:PsDgb#).</param>
    /// <param name="AutoTracking">Tracking starts at power-up (:Psag#). Takes effect at the next power cycle.</param>
    /// <param name="RaLimits">The RA travel limits are active (:PsSrg#).</param>
    /// <param name="RaLimitLeft">The RA travel limit on the vendor's "left" side in degrees (:Pnbg#).</param>
    /// <param name="RaLimitRight">The RA travel limit on the vendor's "right" side in degrees.</param>
    /// <param name="DecLimits">The Dec travel limits are active (:PsSdg#).</param>
    /// <param name="DecLimitCcw">The counter-clockwise Dec travel limit in degrees (:PsSdG#).</param>
    /// <param name="DecLimitCw">The clockwise Dec travel limit in degrees.</param>
    /// <param name="AntiCollision">The anti-collision system is on (:PsCg#), when the board has it (:PbC#).</param>
    /// <param name="AcsThresholdRa">The RA collision threshold, the vendor's "sensitivity" (:Pncg#).</param>
    /// <param name="AcsThresholdDec">The Dec collision threshold.</param>
    /// <param name="AcsCollisionsRa">Collisions counted on RA (:Pnccg#), as of this read.</param>
    /// <param name="AcsCollisionsDec">Collisions counted on Dec.</param>
    /// <param name="SupervisedGoto">"Super GOTO" (:Pndgo#).</param>
    /// <param name="SupervisedGotoTolerance">Its tolerance in degrees (:Pndgv#).</param>
    /// <param name="RaPid">The RA servo gains (:Pnag0#). Takes effect at the next power cycle.</param>
    /// <param name="DecPid">The Dec servo gains (:Pnag1#). Takes effect at the next power cycle.</param>
    /// <param name="CanSetDecLimits">Dec limits can be changed: the vendor's tool allows it on a UMi17X only.</param>
    /// <param name="CanSetDecSecondHome">The Dec second home can be changed: UMi17X only, as in the vendor's tool.</param>
    /// <param name="CanSetPid">The servo gains can be changed: UMi20S and UMi17X, as in the vendor's tool.</param>
    /// <param name="RestartPending">The settings written that the mount runs only after a power cycle: StatusLed, AutoTracking, RaPid, DecPid.</param>
    public sealed record ProxiskySettings(
        bool? StatusLed,
        bool? PowerLossMemory,
        bool? AsiairHoming,
        bool? SupervisedHome,
        bool? DecSecondHome,
        bool? AutoTracking,
        bool? RaLimits,
        int? RaLimitLeft,
        int? RaLimitRight,
        bool? DecLimits,
        int? DecLimitCcw,
        int? DecLimitCw,
        bool? AntiCollision,
        int? AcsThresholdRa,
        int? AcsThresholdDec,
        int? AcsCollisionsRa,
        int? AcsCollisionsDec,
        bool? SupervisedGoto,
        int? SupervisedGotoTolerance,
        ProxiskyPidGains? RaPid,
        ProxiskyPidGains? DecPid,
        bool CanSetDecLimits,
        bool CanSetDecSecondHome,
        bool CanSetPid,
        IReadOnlyList<string> RestartPending);

    /// <summary>
    /// The vendor settings, with the commands, ranges and replies of INDI's LX200_Proxisky (Nico's driver, measured on a
    /// UMi20S 1.0.6). Every setter is answered "1#" (accepted) or "0#" (refused).
    /// </summary>
    public partial class ProxiskyUmiDevice {

        /// <summary>The ranges the vendor's tool checks; the firmware's own are not known.</summary>
        public const int MinRaLimit = 1, MaxRaLimit = 180;

        public const int MinDecLimit = 90, MaxDecLimit = 200;

        /// <summary>RA takes 10, Dec only 100; 59999 is the tool's error text (it checks ≤ 60000).</summary>
        public const int MinAcsThresholdRa = 10, MinAcsThresholdDec = 100, MaxAcsThreshold = 59999;

        public const int MinSupervisedGotoTolerance = 5, MaxSupervisedGotoTolerance = 30;

        /// <summary>Speed Ki has its own ceiling of 80.</summary>
        public const int MinPidGain = 11, MaxPidGain = 1999, MaxSpeedKi = 80;

        /// <summary>
        /// How long the gains are read again after the PID commit (INDI PROXISKY_PID_RESTART_SECONDS): the commit restarts
        /// the axis, which reports every gain as 0 for about 70 ms.
        /// </summary>
        public static readonly TimeSpan PidRestartTimeout = TimeSpan.FromSeconds(1);

        private static readonly TimeSpan PidRestartPoll = TimeSpan.FromMilliseconds(20);

        // For the settings the mount runs only after a power cycle: the value it ran when this connection first changed
        // it (null when unreadable) and the value last written. It reads back the running value until the power cycle.
        private readonly object pendingLock = new();
        private readonly Dictionary<ProxiskySwitch, (bool? Running, bool Requested)> pendingSwitches = [];
        private readonly Dictionary<ProxiskyAxis, (ProxiskyPidGains? Running, ProxiskyPidGains Requested)> pendingPid = [];

        /// <summary>The vendor's tool enables Dec limits and Dec second home for a UMi17X only.</summary>
        public bool CanSetDecLimits => UmiModel.Contains("UMi17X", StringComparison.Ordinal);

        /// <summary>The vendor's tool enables the PID gains for a UMi20S and a UMi17X; other models show them read-only.</summary>
        public bool CanSetPid => UmiModel.Contains("UMi20S", StringComparison.Ordinal) || CanSetDecLimits;

        /// <summary>
        /// Reads every vendor setting, about 20 commands. A feature is there when the mount answers for it: an
        /// unsupported one and a lost reply look the same in this protocol, so the next read tries again.
        /// </summary>
        public ProxiskySettings ReadSettings() {
            HasPowerLossMemory ??= ReadVendorBool(":Pbc#");
            HasAntiCollision ??= ReadVendorBool(":PbC#");

            // :PsLga# asks whether there is an LED, :PsLgb# whether it is lit
            bool? led = ReadVendorBool(":PsLga#") == true ? ReadSwitch(ProxiskySwitch.StatusLed) : null;
            bool? powerLoss = HasPowerLossMemory == true ? ReadSwitch(ProxiskySwitch.PowerLossMemory) : null;
            bool? asiair = ReadSwitch(ProxiskySwitch.AsiairHoming);
            bool? superHome = ReadSwitch(ProxiskySwitch.SupervisedHome);
            bool? decSecondHome = ReadSwitch(ProxiskySwitch.DecSecondHome);
            bool? autoTrack = ReadSwitch(ProxiskySwitch.AutoTracking);

            // a bare "0" instead of the pair means no travel limits on this axis
            var raLimits = ReadVendorPair(":Pnbg#");
            bool? raLimitsOn = raLimits is null ? null : ReadSwitch(ProxiskySwitch.RaLimits);
            var decLimits = ReadVendorPair(":PsSdG#");
            bool? decLimitsOn = decLimits is null ? null : ReadSwitch(ProxiskySwitch.DecLimits);

            bool? acs = null;
            (int Ra, int Dec)? thresholds = null;
            (int Ra, int Dec)? collisions = null;
            if (HasAntiCollision == true) {
                acs = ReadSwitch(ProxiskySwitch.AntiCollision);
                thresholds = ReadVendorPair(":Pncg#");
                collisions = ReadVendorPair(":Pnccg#");
            }

            bool? superGoto = ReadSwitch(ProxiskySwitch.SupervisedGoto);
            int? tolerance = superGoto is null ? null : ReadSupervisedGotoTolerance();

            var raPid = ReadPidGains(ProxiskyAxis.Ra);
            var decPid = ReadPidGains(ProxiskyAxis.Dec);

            var restartPending = new List<string>();
            lock (pendingLock) {
                led = WithPending(ProxiskySwitch.StatusLed, led, restartPending);
                autoTrack = WithPending(ProxiskySwitch.AutoTracking, autoTrack, restartPending);
                raPid = WithPending(ProxiskyAxis.Ra, raPid, restartPending);
                decPid = WithPending(ProxiskyAxis.Dec, decPid, restartPending);
            }

            return new ProxiskySettings(
                led, powerLoss, asiair, superHome, decSecondHome, autoTrack,
                raLimitsOn, raLimits?.First, raLimits?.Second,
                decLimitsOn, decLimits?.First, decLimits?.Second,
                acs, thresholds?.Ra, thresholds?.Dec, collisions?.Ra, collisions?.Dec,
                superGoto, tolerance,
                raPid, decPid,
                CanSetDecLimits, CanSetDecLimits, CanSetPid,
                restartPending);
        }

        /// <summary>
        /// Switches a setting on or off; a refusal throws <see cref="OnStepXCommandRefusedException"/>. The status LED and
        /// auto tracking are stored at once but take effect at the next power cycle.
        /// </summary>
        public void SetSwitch(ProxiskySwitch setting, bool enabled) {
            if ((setting is ProxiskySwitch.DecLimits or ProxiskySwitch.DecSecondHome) && !CanSetDecLimits) {
                throw new OnStepXException($"{Describe(setting)} can only be changed on a UMi17X, not on a {UmiModel}");
            }

            var (read, setPrefix) = Commands(setting);
            bool restartBound = setting is ProxiskySwitch.StatusLed or ProxiskySwitch.AutoTracking;
            bool? running = null;
            if (restartBound) {
                lock (pendingLock) {
                    running = pendingSwitches.TryGetValue(setting, out var pending) ? pending.Running : ReadVendorBool(read);
                }
            }

            SendVendorSetter($"{setPrefix}{(enabled ? '1' : '0')}#");

            if (restartBound) {
                lock (pendingLock) {
                    pendingSwitches[setting] = (running, enabled);
                }
                Logger.Info($"Proxisky: {Describe(setting)} {(enabled ? "on" : "off")} after the next power cycle");
            } else {
                Logger.Info($"Proxisky: {Describe(setting)} {(enabled ? "on" : "off")}");
            }
        }

        /// <summary>:Pnbsl and :Pnbsr, degrees on the vendor's "left" and "right" side; both are sent even when one is refused.</summary>
        public void SetRaLimits(int left, int right) {
            CheckRange(nameof(left), left, MinRaLimit, MaxRaLimit, "the RA limits are");
            CheckRange(nameof(right), right, MinRaLimit, MaxRaLimit, "the RA limits are");
            SendVendorPair(("left", Command(":Pnbsl", left)), ("right", Command(":Pnbsr", right)));
            Logger.Info($"Proxisky: RA limits left {left}°, right {right}°");
        }

        /// <summary>:PsSdl and :PsSdr, counter-clockwise and clockwise degrees; UMi17X only, as in the vendor's tool.</summary>
        public void SetDecLimits(int ccw, int cw) {
            if (!CanSetDecLimits) {
                throw new OnStepXException($"the Dec limits can only be changed on a UMi17X, not on a {UmiModel}");
            }
            CheckRange(nameof(ccw), ccw, MinDecLimit, MaxDecLimit, "the Dec limits are");
            CheckRange(nameof(cw), cw, MinDecLimit, MaxDecLimit, "the Dec limits are");
            SendVendorPair(("counter-clockwise", Command(":PsSdl", ccw)), ("clockwise", Command(":PsSdr", cw)));
            Logger.Info($"Proxisky: Dec limits CCW {ccw}°, CW {cw}°");
        }

        /// <summary>:Pncsr and :Pncsd, the collision thresholds.</summary>
        public void SetAcsThresholds(int ra, int dec) {
            CheckRange(nameof(ra), ra, MinAcsThresholdRa, MaxAcsThreshold, "the RA collision threshold is");
            CheckRange(nameof(dec), dec, MinAcsThresholdDec, MaxAcsThreshold, "the Dec collision threshold is");
            SendVendorPair(("RA", Command(":Pncsr", ra)), ("Dec", Command(":Pncsd", dec)));
            Logger.Info($"Proxisky: collision thresholds RA {ra}, Dec {dec}");
        }

        /// <summary>:Pnccc#, sets both collision counters to 0.</summary>
        public void ClearCollisionCounters() {
            SendVendorSetter(":Pnccc#");
            Logger.Info("Proxisky: collision counters cleared");
        }

        /// <summary>:Pndsv, the supervised goto tolerance in degrees.</summary>
        public void SetSupervisedGotoTolerance(int degrees) {
            CheckRange(nameof(degrees), degrees, MinSupervisedGotoTolerance, MaxSupervisedGotoTolerance, "the supervised goto tolerance is");
            SendVendorSetter(Command(":Pndsv", degrees));
            Logger.Info($"Proxisky: supervised goto tolerance {degrees}°");
        }

        /// <summary>
        /// The four gains of one axis, then the commit (:Pnas&lt;axis&gt;r#), which restarts the axis: only while the mount
        /// stands still, which the caller checks. A refused gain stops before the commit, so the mount keeps its previous
        /// set. The mount runs the new gains after the next power cycle; until then it reads back the old ones.
        /// </summary>
        public void SetPidGains(ProxiskyAxis axis, ProxiskyPidGains gains) {
            if (!CanSetPid) {
                throw new OnStepXException($"the PID gains can only be changed on a UMi20S or UMi17X, not on a {UmiModel}");
            }
            CheckRange(nameof(gains.AngleKp), gains.AngleKp, MinPidGain, MaxPidGain, "PID gains are");
            CheckRange(nameof(gains.AngleKi), gains.AngleKi, MinPidGain, MaxPidGain, "PID gains are");
            CheckRange(nameof(gains.SpeedKp), gains.SpeedKp, MinPidGain, MaxPidGain, "PID gains are");
            CheckRange(nameof(gains.SpeedKi), gains.SpeedKi, MinPidGain, MaxSpeedKi, "speed Ki is");

            ProxiskyPidGains? running;
            lock (pendingLock) {
                running = pendingPid.TryGetValue(axis, out var pending) ? pending.Running : ReadPidGains(axis);
            }

            string prefix = $":Pnas{(int)axis}";
            SendVendorSetter(Command(prefix + "Ap", gains.AngleKp));
            SendVendorSetter(Command(prefix + "Ai", gains.AngleKi));
            SendVendorSetter(Command(prefix + "Sp", gains.SpeedKp));
            SendVendorSetter(Command(prefix + "Si", gains.SpeedKi));
            SendVendorSetter(prefix + "r#");

            // the commit was accepted, so the mount has the gains stored even when the readback below fails
            lock (pendingLock) {
                pendingPid[axis] = (running, gains);
            }

            // the axis restarts and reads all gains as 0 meanwhile; that is also the "unsupported" answer, but this axis
            // answered a moment ago
            var clock = Stopwatch.StartNew();
            ProxiskyPidGains? stored;
            while ((stored = ReadPidGains(axis)) is null && clock.Elapsed < PidRestartTimeout) {
                Thread.Sleep(PidRestartPoll);
            }
            if (stored is null) {
                throw new OnStepXException($"the mount accepted the {axis} PID gains but did not report them back (:Pnag{(int)axis}#); they take effect after the next power cycle");
            }
            Logger.Info($"Proxisky: {axis} PID gains {Describe(gains)} stored, the mount runs {Describe(stored)} until the next power cycle");
        }

        private static (string Read, string Set) Commands(ProxiskySwitch setting) => setting switch {
            ProxiskySwitch.StatusLed => (":PsLgb#", ":PsLs"),
            ProxiskySwitch.PowerLossMemory => (":Pscg#", ":Pscs"),
            ProxiskySwitch.AsiairHoming => (":PsAg#", ":PsAs"),
            ProxiskySwitch.SupervisedHome => (":PsShg#", ":PsShs"),
            // reads :PsDgb# but writes :PsDs, like Super GOTO below reads :Pndgo# and writes :Pndss
            ProxiskySwitch.DecSecondHome => (":PsDgb#", ":PsDs"),
            ProxiskySwitch.AutoTracking => (":Psag#", ":Psas"),
            ProxiskySwitch.RaLimits => (":PsSrg#", ":PsSrs"),
            ProxiskySwitch.DecLimits => (":PsSdg#", ":PsSds"),
            ProxiskySwitch.AntiCollision => (":PsCg#", ":PsCs"),
            ProxiskySwitch.SupervisedGoto => (":Pndgo#", ":Pndss"),
            _ => throw new ArgumentOutOfRangeException(nameof(setting), setting, null),
        };

        private static string Describe(ProxiskySwitch setting) => setting switch {
            ProxiskySwitch.StatusLed => "status LED",
            ProxiskySwitch.PowerLossMemory => "power-loss memory",
            ProxiskySwitch.AsiairHoming => "ASIAIR homing",
            ProxiskySwitch.SupervisedHome => "supervised home",
            ProxiskySwitch.DecSecondHome => "Dec second home",
            ProxiskySwitch.AutoTracking => "auto tracking",
            ProxiskySwitch.RaLimits => "RA limits",
            ProxiskySwitch.DecLimits => "Dec limits",
            ProxiskySwitch.AntiCollision => "anti-collision",
            _ => "supervised goto",
        };

        private static string Describe(ProxiskyPidGains? gains) =>
            gains is null ? "unknown" : $"{gains.AngleKp}|{gains.AngleKi}|{gains.SpeedKp}|{gains.SpeedKi}";

        private bool? ReadSwitch(ProxiskySwitch setting) => ReadVendorBool(Commands(setting).Read);

        /// <summary>"left|right" and the like; null for anything else, e.g. the bare "0" of a missing feature.</summary>
        private (int First, int Second)? ReadVendorPair(string command) {
            var reply = Transport.SendForString(command);
            string[] fields = reply.Text.Split('|');
            if (reply.Terminated && fields.Length >= 2 && TryParseNumber(fields[0], out int first) && TryParseNumber(fields[1], out int second)) {
                return (first, second);
            }
            Logger.Debug($"Proxisky: no pair in the reply to {command}: '{reply}'");
            return null;
        }

        /// <summary>
        /// At least four fields: a UMi20S answers seven, the last three always 0. All four 0 is null, the vendor's
        /// "unsupported" and also what an axis restarting after the PID commit reports.
        /// </summary>
        private ProxiskyPidGains? ReadPidGains(ProxiskyAxis axis) {
            string command = $":Pnag{(int)axis}#";
            var reply = Transport.SendForString(command);
            string[] fields = reply.Text.Split('|');
            int[] gains = new int[4];
            if (!reply.Terminated || fields.Length < 4) {
                Logger.Debug($"Proxisky: no PID gains in the reply to {command}: '{reply}'");
                return null;
            }
            for (int i = 0; i < 4; i++) {
                if (!TryParseNumber(fields[i], out gains[i])) {
                    Logger.Debug($"Proxisky: no PID gains in the reply to {command}: '{reply}'");
                    return null;
                }
            }
            return gains is [0, 0, 0, 0] ? null : new ProxiskyPidGains(gains[0], gains[1], gains[2], gains[3]);
        }

        /// <summary>Null outside the tool's range: a bare "0" is the vendor's "unsupported" here too.</summary>
        private int? ReadSupervisedGotoTolerance() {
            var reply = Transport.SendForString(":Pndgv#");
            if (reply.Terminated && TryParseNumber(reply.Text, out int degrees)
                && degrees is >= MinSupervisedGotoTolerance and <= MaxSupervisedGotoTolerance) {
                return degrees;
            }
            Logger.Debug($"Proxisky: no supervised goto tolerance in the reply to :Pndgv#: '{reply}'");
            return null;
        }

        private static bool TryParseNumber(string text, out int value) {
            bool ok = double.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double number)
                && Math.Abs(number) <= int.MaxValue;
            value = ok ? (int)Math.Round(number) : 0;
            return ok;
        }

        /// <summary>
        /// "1#" accepted, "0#" refused. The setters are answered although the vendor's tool sends them blind; reading the
        /// answer also keeps it from being read as the next command's reply. Like INDI's sendVendorSetter a "1" counts
        /// without its '#' too: the write has landed then.
        /// </summary>
        private void SendVendorSetter(string command) {
            var reply = Transport.SendForString(command);
            if (reply.Text == "1") {
                return;
            }
            if (reply.Text == "0") {
                throw new OnStepXCommandRefusedException(command, null, "not accepted by this model or firmware");
            }
            throw new OnStepXException($"No acknowledgement to {command}: '{reply}'");
        }

        /// <summary>Both commands go out even when the first is refused, as in INDI; the refusal names which half failed.</summary>
        private void SendVendorPair((string Name, string Command) first, (string Name, string Command) second) {
            var refused = new List<string>();
            foreach (var (name, command) in new[] { first, second }) {
                try {
                    SendVendorSetter(command);
                } catch (OnStepXCommandRefusedException) {
                    refused.Add(name);
                }
            }
            if (refused.Count > 0) {
                string refusedCommand = refused[0] == first.Name ? first.Command : second.Command;
                string reason = refused.Count == 2
                    ? $"the {first.Name} and {second.Name} values were not accepted"
                    : $"the {refused[0]} value was not accepted";
                throw new OnStepXCommandRefusedException(refusedCommand, null, reason);
            }
        }

        private static string Command(string prefix, int value) => string.Create(CultureInfo.InvariantCulture, $"{prefix}{value}#");

        private static void CheckRange(string name, int value, int min, int max, string what) {
            if (value < min || value > max) {
                throw new ArgumentOutOfRangeException(name, value, $"{what} {min} to {max}");
            }
        }

        // Caller holds pendingLock. Only a feature the mount answered for gets the written value.
        private bool? WithPending(ProxiskySwitch setting, bool? read, List<string> restartPending) {
            if (read is null || !pendingSwitches.TryGetValue(setting, out var pending)) {
                return read;
            }
            if (pending.Running != pending.Requested) {
                restartPending.Add(setting.ToString());
            }
            return pending.Requested;
        }

        // Caller holds pendingLock.
        private ProxiskyPidGains? WithPending(ProxiskyAxis axis, ProxiskyPidGains? read, List<string> restartPending) {
            if (read is null || !pendingPid.TryGetValue(axis, out var pending)) {
                return read;
            }
            if (pending.Running != pending.Requested) {
                restartPending.Add(axis == ProxiskyAxis.Ra ? "RaPid" : "DecPid");
            }
            return pending.Requested;
        }
    }
}
