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
        public bool Unpark() => SendAcceptedOrUnanswered(":hR#");

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
                throw new OnStepXCommandRefusedException(":hC#", ToCommandError(e));
            }
        }

        /// <summary>:Te# / :Td#; refused with the reason from :GE#.</summary>
        public void SetTracking(bool enabled) => SendExpectingAcceptance(enabled ? ":Te#" : ":Td#");

        /// <summary>:TQ#, :TL# or :TS#, no reply.</summary>
        public void SetTrackingRate(OnStepXTrackingRate rate) => Transport.SendBlind(rate switch {
            OnStepXTrackingRate.Lunar => ":TL#",
            OnStepXTrackingRate.Solar => ":TS#",
            _ => ":TQ#",
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
        /// LX200 protocol defines as what to add to local time to get UTC.
        /// </summary>
        public DateTime GetUtcDate() {
            string date = ReadText(Transport, ":GC#");
            string time = ReadText(Transport, ":GL#");
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

        private static OnStepXCommandError? ToCommandError(int? code) => code is { } c ? (OnStepXCommandError)c : null;

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

        /// <summary>INDI getSexComponents: whole degrees, minutes and seconds of |value|, the seconds rounded with carry.</summary>
        private static (int D, int M, int S) Split(double value) {
            double abs = Math.Abs(value);
            int d = (int)abs;
            int m = (int)((abs - d) * 60.0);
            int s = (int)Math.Round(((abs - d) * 60.0 - m) * 60.0, MidpointRounding.ToEven);
            if (s == 60) {
                s = 0;
                m++;
            }
            if (m == 60) {
                m = 0;
                d++;
            }
            return (d, m, s);
        }

        /// <summary>"HH:MM:SS", hours wrapped into 0-24.</summary>
        internal static string FormatRightAscension(double hours) {
            hours = ((hours % 24) + 24) % 24;
            var (h, m, s) = Split(hours);
            if (h == 24) {
                h = 0;
            }
            return string.Create(Inv, $"{h:00}:{m:00}:{s:00}");
        }

        /// <summary>"sDD*MM:SS", also "-00*30:00" (INDI's negative-zero case).</summary>
        internal static string FormatDeclination(double degrees) {
            var (d, m, s) = Split(degrees);
            return string.Create(Inv, $"{(degrees < 0 ? '-' : '+')}{d:00}*{m:00}:{s:00}");
        }

        /// <summary>
        /// INDI setSiteLatitude, high precision: "sDD:MM:S.SS". Unlike INDI's "%+.02d" the sign is written separately,
        /// so a latitude between 0 and -1 keeps it.
        /// </summary>
        internal static string FormatSiteLatitude(double latitude) {
            var (d, m, s) = SplitWithFraction(latitude);
            return string.Create(Inv, $"{(latitude < 0 ? '-' : '+')}{d:00}:{m:00}:{s:0.00}");
        }

        /// <summary>INDI setSiteLongitude, high precision: "DDD:MM:S.SS", 0-360 counted west.</summary>
        internal static string FormatSiteLongitude(double westLongitude) {
            var (d, m, s) = SplitWithFraction(westLongitude);
            return string.Create(Inv, $"{d:000}:{m:00}:{s:0.00}");
        }

        /// <summary>
        /// INDI LX200_OnStep::setUTCOffset: the hours negated ("%03d"), the minutes 0 or rounded up to 45 above 30.
        /// </summary>
        internal static string FormatUtcOffset(double offsetHours) {
            int hours = (int)offsetHours * -1;
            int minutes = (int)Math.Abs((offsetHours - (int)offsetHours) * 60);
            if (minutes > 30) {
                minutes = 45;
            }
            // "%03d": three characters including the sign, so -2 is "-02" and 5 is "005"
            string h = hours < 0 ? "-" + (-hours).ToString("00", Inv) : hours.ToString("000", Inv);
            return string.Create(Inv, $"{h}:{minutes:00}");
        }

        /// <summary>INDI getSexComponentsIID: whole degrees and minutes, seconds with fraction (rounded to 0.01 with carry).</summary>
        private static (int D, int M, double S) SplitWithFraction(double value) {
            double abs = Math.Abs(value);
            int d = (int)abs;
            int m = (int)((abs - d) * 60.0);
            double s = Math.Round(((abs - d) * 60.0 - m) * 60.0, 2);
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
    }
}
