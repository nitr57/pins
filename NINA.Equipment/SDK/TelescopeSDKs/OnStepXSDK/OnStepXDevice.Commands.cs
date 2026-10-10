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

using System;
using System.Collections.Generic;
using System.Globalization;

namespace NINA.Equipment.SDK.TelescopeSDKs.OnStepXSDK {

    /// <summary>Motion, tracking, site and time commands, formatted and answered as in INDI's LX200_OnStep and lx200driver.</summary>
    public partial class OnStepXDevice {

        /// <summary>Longest pulse :Mg takes, from INDI's "%04d".</summary>
        public const int MaxPulseMs = 9999;

        /// <summary>
        /// The move rates :R0# to :R7# as multiples of sidereal (INDI initSlewRates); :R8# is half the slew speed, :R9#
        /// the slew speed.
        /// </summary>
        public static readonly IReadOnlyList<double> SiderealMoveRates = [0.25, 0.5, 1, 2, 4, 8, 20, 48];

        public const int HalfMaxMoveRateIndex = 8;
        public const int MaxMoveRateIndex = 9;

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /// <summary>:Sr and :Sd (INDI setObjectRA/setObjectDEC, long format); a '0' reply is a refusal.</summary>
        public void SetTarget(double raHours, double decDegrees) {
            SendExpectingAcceptance($":Sr{FormatRightAscension(raHours)}#");
            SendExpectingAcceptance($":Sd{FormatDeclination(decDegrees)}#");
        }

        /// <summary>Sets the target and starts the goto (:MS#), which is answered with '0' or the reason it cannot start.</summary>
        public OnStepXGotoError Goto(double raHours, double decDegrees) {
            SetTarget(raHours, decDegrees);
            char? reply = Transport.SendForChar(":MS#");
            if (reply is not { } c || c < '0' || c > '9') {
                throw new OnStepXException($"Invalid reply to :MS#: '{reply}'");
            }
            return (OnStepXGotoError)(c - '0');
        }

        /// <summary>Sets the target and syncs to it (:CM#): "N/A" is success, "E&lt;n&gt;" the reason it failed.</summary>
        public OnStepXGotoError Sync(double raHours, double decDegrees) {
            SetTarget(raHours, decDegrees);
            var reply = Transport.SendForString(":CM#");
            if (reply.Terminated && reply.Text == "N/A") {
                return OnStepXGotoError.None;
            }
            if (reply.Terminated && reply.Text.Length >= 2 && reply.Text[0] == 'E' && reply.Text[1] is >= '0' and <= '9') {
                return (OnStepXGotoError)(reply.Text[1] - '0');
            }
            throw new OnStepXException($"Invalid reply to :CM#: '{reply}'");
        }

        /// <summary>Stops any goto or move (:Q#).</summary>
        public void Abort() => Transport.SendBlind(":Q#");

        /// <summary>
        /// Starts the slew to the park position (:hP#). INDI sends it blind; OnStepX answers '1' when it starts and '0'
        /// when it refuses (Park.command.cpp), which throws <see cref="OnStepXCommandRefusedException"/> with the reason
        /// from :GE# (no park position set, standby, ...). False without an answer: the park may have started, the
        /// status decides. <see cref="OnStepXStatus.Park"/> tells when the mount is there.
        /// </summary>
        public bool Park() => SendAcceptedOrUnanswered(":hP#");

        /// <summary>
        /// :hR#: true when answered '1', false without an answer (INDI: the reply can get lost although the controller
        /// unparks, so the status decides); a '0' throws <see cref="OnStepXCommandRefusedException"/>.
        /// </summary>
        public bool Unpark() {
            try {
                return SendAcceptedOrUnanswered(":hR#");
            } catch (OnStepXCommandRefusedException ex) when (ex.Error == OnStepXCommandError.Parked) {
                // Park::restore answers "already parked" when date and time are not set yet (Park.cpp, "unpark postponed")
                throw new OnStepXCommandRefusedException(":hR#", ex.Error, "date and time not set, the controller postpones the unpark");
            }
        }

        /// <summary>'1' true, no answer false, '0' throws <see cref="OnStepXCommandRefusedException"/> with the reason.</summary>
        private bool SendAcceptedOrUnanswered(string command) {
            var (reply, error) = Transport.SendForCharWithError(command);
            return reply switch {
                '1' => true,
                '0' => throw new OnStepXCommandRefusedException(command, ToCommandError(error)),
                _ => false,
            };
        }

        /// <summary>Makes the current position the park position (:hQ#); refused with the reason from :GE#.</summary>
        public void SetParkPosition() => SendExpectingAcceptance(":hQ#");

        /// <summary>
        /// Starts the move to the home position (:hC#). It has no reply but records its error, so :GE# right after tells
        /// whether it started (Home.command.cpp); a refusal throws <see cref="OnStepXCommandRefusedException"/>.
        /// </summary>
        public void FindHome() {
            int? error = Transport.SendBlindThenError(":hC#");
            if (error is { } e && e != (int)OnStepXCommandError.None) {
                throw new OnStepXCommandRefusedException(":hC#", (OnStepXCommandError)e);
            }
        }

        /// <summary>:Te# / :Td#; refused with the reason from :GE#.</summary>
        public void SetTracking(bool enabled) => SendExpectingAcceptance(enabled ? ":Te#" : ":Td#");

        /// <summary>:TQ#, :TL#, :TS# or :TK# (King, 60.136 Hz), no reply. All but :TQ# turn rate compensation off.</summary>
        public void SetTrackingRate(OnStepXTrackingRate rate) => Transport.SendBlind(rate switch {
            OnStepXTrackingRate.Lunar => ":TL#",
            OnStepXTrackingRate.Solar => ":TS#",
            OnStepXTrackingRate.King => ":TK#",
            _ => ":TQ#",
        });

        /// <summary>
        /// Rate compensation: :Tn#, :Tr# or :To# (each also selects the sidereal rate), then :T1# or :T2# for one or both
        /// axes; each answered '1'/'0' (Mount.command.cpp).
        /// </summary>
        public void SetCompensation(OnStepXCompensation compensation) {
            switch (compensation) {
                case OnStepXCompensation.None:
                    SendExpectingAcceptance(":Tn#");
                    break;
                case OnStepXCompensation.Refraction:
                case OnStepXCompensation.RefractionDual:
                    SendExpectingAcceptance(":Tr#");
                    SendExpectingAcceptance(compensation == OnStepXCompensation.RefractionDual ? ":T2#" : ":T1#");
                    break;
                default:
                    SendExpectingAcceptance(":To#");
                    SendExpectingAcceptance(compensation == OnStepXCompensation.ModelDual ? ":T2#" : ":T1#");
                    break;
            }
        }

        /// <summary>Whether date and time are set (:GX89#, '0' ready, '1' not, Site.command.cpp); null without an answer.</summary>
        public bool? IsDateTimeReady() => Transport.SendForChar(":GX89#") switch {
            '0' => true,
            '1' => false,
            _ => null,
        };

        /// <summary>Resumes a goto paused at home during a meridian flip (:SX99,1#).</summary>
        public void ContinueFromHomePause() => SendExpectingAcceptance(":SX99,1#");

        /// <summary>The pier side gotos prefer (:GX96#), null when the reply is not E, W or B.</summary>
        public OnStepXPreferredPierSide? GetPreferredPierSide() {
            var reply = Transport.SendForString(":GX96#");
            return reply.Terminated ? reply.Text switch {
                "E" => OnStepXPreferredPierSide.East,
                "W" => OnStepXPreferredPierSide.West,
                "B" => OnStepXPreferredPierSide.Best,
                _ => null,
            } : null;
        }

        /// <summary>
        /// :SX96,E#, :SX96,W# or :SX96,B#. OnStepX keeps it across power cycles only with PIER_SIDE_PREFERRED_MEMORY (off
        /// by default), so it is set at every connect.
        /// </summary>
        public void SetPreferredPierSide(OnStepXPreferredPierSide side) => SendExpectingAcceptance(side switch {
            OnStepXPreferredPierSide.East => ":SX96,E#",
            OnStepXPreferredPierSide.West => ":SX96,W#",
            _ => ":SX96,B#",
        });

        /// <summary>
        /// The rate of <see cref="StartMove"/>: :R0# to :R9#, see <see cref="SiderealMoveRates"/>. OnStepX also makes
        /// :R0# to :R2# (0.25x to 1x) the pulse-guide rate and stores it (GUIDE_SEPARATE_PULSE_RATE, Guide.command.cpp).
        /// </summary>
        public void SetMoveRate(int index) {
            if (index is < 0 or > MaxMoveRateIndex) {
                throw new ArgumentOutOfRangeException(nameof(index), index, "move rate index 0-9");
            }
            Transport.SendBlind($":R{index}#");
        }

        /// <summary>Moves at the move rate until <see cref="StopMove"/> (:Mn#, :Ms#, :Me#, :Mw#).</summary>
        public void StartMove(OnStepXDirection direction) => Transport.SendBlind($":M{Letter(direction)}#");

        /// <summary>:Qn#, :Qs#, :Qe#, :Qw#.</summary>
        public void StopMove(OnStepXDirection direction) => Transport.SendBlind($":Q{Letter(direction)}#");

        /// <summary>
        /// A guide pulse (:Mg, no reply), written at once even while another command waits for its reply
        /// (<see cref="OnStepXTransport.SendBlindNow"/>). OnStepX runs a pulse of 0 ms for days (Guide::startAxis1 reads 0
        /// as unlimited), so a pulse shorter than 1 ms is not sent; longer ones are cut to <see cref="MaxPulseMs"/>.
        /// Returns when the command is on the line, the moment the controller starts the pulse; null when not sent.
        /// </summary>
        public DateTime? PulseGuide(OnStepXDirection direction, int durationMs) {
            if (durationMs < 1) {
                return null;
            }
            return Transport.SendBlindNow(string.Create(Inv, $":Mg{Letter(direction)}{Math.Min(durationMs, MaxPulseMs):0000}#"));
        }

        /// <summary>The largest tracking rate offset OnStepX takes; it clamps larger ones (Mount.command.cpp).</summary>
        public const double MaxTrackingRateOffset = 1800.0;

        /// <summary>
        /// The tracking rate offsets (:GXTR#, :GXTD#) in arc-seconds per sidereal second, RA counted in RA (15" per
        /// second of RA), positive with RA or Dec increasing; null when unreadable. Homing clears them.
        /// </summary>
        public (double Ra, double Dec)? GetTrackingRateOffsets() {
            var ra = Transport.SendForString(":GXTR#");
            var dec = Transport.SendForString(":GXTD#");
            return ra.Terminated && dec.Terminated
                && double.TryParse(ra.Text, NumberStyles.Float, Inv, out double raOffset)
                && double.TryParse(dec.Text, NumberStyles.Float, Inv, out double decOffset)
                ? (raOffset, decOffset)
                : null;
        }

        /// <summary>
        /// :SXTR,n.n# and :SXTD,n.n#: offsets added to the tracking rate, in the units of
        /// <see cref="GetTrackingRateOffsets"/>; each refused with the reason from :GE#.
        /// </summary>
        public void SetTrackingRateOffsets(double ra, double dec) {
            if (Math.Abs(ra) > MaxTrackingRateOffset || Math.Abs(dec) > MaxTrackingRateOffset) {
                throw new ArgumentOutOfRangeException(nameof(ra), $"tracking rate offsets are limited to ±{MaxTrackingRateOffset}\"/s");
            }
            SendExpectingAcceptance(string.Create(Inv, $":SXTR,{ra:0.000000}#"));
            SendExpectingAcceptance(string.Create(Inv, $":SXTD,{dec:0.000000}#"));
        }

        /// <summary>Site elevation in metres (:Gv#), NaN when unreadable.</summary>
        public double GetElevation() {
            var reply = Transport.SendForString(":Gv#");
            return reply.Terminated && double.TryParse(reply.Text, NumberStyles.Float, Inv, out double metres) ? metres : double.NaN;
        }

        /// <summary>:Sv[sn.n]#, refused with the reason from :GE#.</summary>
        public void SetElevation(double metres) => SendExpectingAcceptance(string.Create(Inv, $":Sv{metres:+0.0;-0.0}#"));

        /// <summary>Altitude in degrees (:GA#).</summary>
        public double GetAltitude() => ReadSexagesimal(":GA#");

        /// <summary>Azimuth in degrees (:GZ#).</summary>
        public double GetAzimuth() => ReadSexagesimal(":GZ#");

        /// <summary>Local sidereal time in hours (:GS#).</summary>
        public double GetSiderealTime() => ReadSexagesimal(":GS#");

        /// <summary>The goto speed in degrees per second (:GX97#), NaN when the controller does not say.</summary>
        public double GetSlewSpeed() {
            var reply = Transport.SendForString(":GX97#");
            return reply.Terminated && double.TryParse(reply.Text, NumberStyles.Float, Inv, out double speed) && speed > 0 ? speed : double.NaN;
        }

        /// <summary>
        /// Latitude and longitude in degrees, longitude positive east. Read with :GtH#/:GgH#, or :Gt#/:Gg# when those
        /// fail (INDI sendScopeLocation); the controller counts longitude positive west.
        /// </summary>
        public (double Latitude, double Longitude) GetSite() {
            double latitude = ReadSexagesimalWithFallback(":GtH#", ":Gt#");
            double westLongitude = ReadSexagesimalWithFallback(":GgH#", ":Gg#");
            return (latitude, -westLongitude);
        }

        /// <summary>INDI updateLocation: longitude as 360 - east longitude, then the latitude.</summary>
        public void SetSite(double latitude, double longitude) {
            double westLongitude = 360 - longitude;
            while (westLongitude < 0) {
                westLongitude += 360;
            }
            while (westLongitude > 360) {
                westLongitude -= 360;
            }

            SendExpectingAcceptance($":Sg{FormatSiteLongitude(westLongitude)}#");
            SendExpectingAcceptance($":St{FormatSiteLatitude(latitude)}#");
        }

        /// <summary>
        /// The controller's clock as UTC: local date (:GC#, MM/DD/YY) and time (:GL#) plus the offset (:GG#), which the
        /// LX200 protocol defines as what to add to local time to get UTC. The date is read again after the time, so a
        /// read across local midnight does not pair the old date with the new time.
        /// </summary>
        public DateTime GetUtcDate() {
            string date = ReadText(Transport, ":GC#");
            string time = ReadText(Transport, ":GL#");
            string dateAfter = ReadText(Transport, ":GC#");
            if (dateAfter != date) {
                date = dateAfter;
                time = ReadText(Transport, ":GL#");
            }
            string offset = ReadText(Transport, ":GG#");
            if (!DateTime.TryParseExact(date, "MM/dd/yy", Inv, DateTimeStyles.None, out var day)
                || !TryParseSexagesimal(time, out double hours)
                || !TryParseSexagesimal(offset, out double offsetHours)) {
                throw new OnStepXException($"Invalid controller time: date '{date}', time '{time}', offset '{offset}'");
            }
            // the controller's clock has whole seconds; summing hours as doubles would land a tick off
            return DateTime.SpecifyKind(day.AddSeconds(Math.Round((hours + offsetHours) * 3600)), DateTimeKind.Utc);
        }

        /// <summary>
        /// INDI LX200Telescope::updateTime: the UTC offset (:SG), local time (:SL) and local date (:SC), with
        /// <paramref name="utcOffset"/> the local time zone's offset from UTC (+2 h for CEST).
        /// </summary>
        public void SetUtcDate(DateTime utc, TimeSpan utcOffset) {
            var local = utc.ToUniversalTime() + utcOffset;
            SendExpectingAcceptance($":SG{FormatUtcOffset(utcOffset.TotalHours)}#");
            SendExpectingAcceptance(string.Create(Inv, $":SL{local:HH:mm:ss}#"));
            SendExpectingAcceptance(string.Create(Inv, $":SC{local:MM/dd/yy}#"));
        }

        /// <summary>A raw command read up to '#' (the reply without it), for <c>ITelescope.SendCommandString</c>.</summary>
        public string SendCommandString(string command) => Transport.SendForString(command).Text;

        /// <summary>A raw command answered with one character, for <c>ITelescope.SendCommandBool</c>.</summary>
        public char? SendCommandChar(string command) => Transport.SendForChar(command);

        /// <summary>A raw command without a reply.</summary>
        public void SendCommandBlind(string command) => Transport.SendBlind(command);

        /// <summary>
        /// Commands answered with '1' (accepted) or '0' (refused), INDI setStandardProcedure. A refusal throws
        /// <see cref="OnStepXCommandRefusedException"/> with the controller's reason (:GE#); no answer throws
        /// <see cref="OnStepXException"/>.
        /// </summary>
        protected void SendExpectingAcceptance(string command) {
            var (reply, error) = Transport.SendForCharWithError(command);
            if (reply == '0') {
                throw new OnStepXCommandRefusedException(command, ToCommandError(error));
            }
            if (reply is null) {
                throw new OnStepXException($"No reply to {command}");
            }
        }

        /// <summary>The reason for a '0' reply; "no error" there means :GE# could not tell (a later command reset it).</summary>
        private static OnStepXCommandError? ToCommandError(int? code) => code is { } c and not (int)OnStepXCommandError.None ? (OnStepXCommandError)c : null;

        private double ReadSexagesimalWithFallback(string command, string fallback) {
            var reply = Transport.SendForString(command);
            if (reply.Terminated && TryParseSexagesimal(reply.Text, out double value)) {
                return value;
            }
            return ReadSexagesimal(fallback);
        }

        private static char Letter(OnStepXDirection direction) => direction switch {
            OnStepXDirection.North => 'n',
            OnStepXDirection.South => 's',
            OnStepXDirection.East => 'e',
            _ => 'w',
        };

        /// <summary>
        /// "HH:MM:SS.SSSS", hours wrapped into 0-24: the finest form OnStepX parses (:Sr, Convert::hmsToDouble up to 13
        /// characters). INDI sends whole seconds, 7.5" in RA.
        /// </summary>
        internal static string FormatRightAscension(double hours) {
            hours = ((hours % 24) + 24) % 24;
            var (h, m, s) = SplitRounded(hours, 4);
            if (h == 24) {
                h = 0;
            }
            return string.Create(Inv, $"{h:00}:{m:00}:{s:00.0000}");
        }

        /// <summary>"sDD*MM:SS.SSS" (:Sd, up to 13 characters), also "-00*30:00.000" (INDI's negative-zero case).</summary>
        internal static string FormatDeclination(double degrees) {
            var (d, m, s) = SplitRounded(degrees, 3);
            return string.Create(Inv, $"{(degrees < 0 ? '-' : '+')}{d:00}*{m:00}:{s:00.000}");
        }

        /// <summary>Whole degrees and minutes of |value|, seconds rounded to <paramref name="decimals"/> with carry.</summary>
        private static (int D, int M, double S) SplitRounded(double value, int decimals) {
            double abs = Math.Abs(value);
            int d = (int)abs;
            int m = (int)((abs - d) * 60.0);
            double s = Math.Round(((abs - d) * 60.0 - m) * 60.0, decimals);
            if (s >= 60) {
                s = 0;
                m++;
            }
            if (m == 60) {
                m = 0;
                d++;
            }
            return (d, m, s);
        }

        /// <summary>
        /// INDI setSiteLatitude, high precision: "sDD:MM:S.SS". Unlike INDI's "%+.02d" the sign is written separately,
        /// so a latitude between 0 and -1 keeps it.
        /// </summary>
        internal static string FormatSiteLatitude(double latitude) {
            var (d, m, s) = SplitRounded(latitude, 2);
            return string.Create(Inv, $"{(latitude < 0 ? '-' : '+')}{d:00}:{m:00}:{s:0.00}");
        }

        /// <summary>INDI setSiteLongitude, high precision: "DDD:MM:S.SS", 0-360 counted west.</summary>
        internal static string FormatSiteLongitude(double westLongitude) {
            var (d, m, s) = SplitRounded(westLongitude, 2);
            return string.Create(Inv, $"{d:000}:{m:00}:{s:0.00}");
        }

        /// <summary>
        /// INDI LX200_OnStep::setUTCOffset: the offset negated (what to add to local time for UTC), hours as "%03d"
        /// (three characters including the sign: -2 is "-02", 5 is "005"), the minutes 0 or rounded up to 45 above 30.
        /// Unlike INDI the sign follows the whole offset, so +0:30 becomes "-00:30" and not "000:30".
        /// </summary>
        internal static string FormatUtcOffset(double offsetHours) {
            double toUtc = -offsetHours;
            int hours = (int)Math.Abs(toUtc);
            int minutes = (int)((Math.Abs(toUtc) - hours) * 60);
            if (minutes > 30) {
                minutes = 45;
            }
            string h = toUtc < 0 ? "-" + hours.ToString("00", Inv) : hours.ToString("000", Inv);
            return string.Create(Inv, $"{h}:{minutes:00}");
        }
    }
}
