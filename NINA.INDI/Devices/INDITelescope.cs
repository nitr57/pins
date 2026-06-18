#region "copyright"

/*
    Copyright © 2025 Nico Trost <nico.trost57@gmail.com> and the PI.N.S. contributors

    This file is part of PI 'N' Stars.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Astrometry;
using NINA.Core.Enum;
using NINA.Core.Utility;
using NINA.INDI.Enums;
using NINA.INDI.Interfaces;
using NINA.INDI.Model;
using NINA.INDI.Protocol;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.INDI.Devices {

    public class INDITelescope : INDIDevice, IINDITelescope {

        /// <summary>
        /// Configures the actual maximum slew rate of the mount in degrees per second.
        /// INDI only exposes discrete named rates (e.g. "Max") with no associated °/s value.
        /// Set this so that PolarAlignment can calculate correct move timeouts.
        /// When > 0, AxisRates() returns (0, ActualMaxSlewRateDps) and SetSlewRateForMotion
        /// maps °/s values proportionally to INDI switch indices.
        /// </summary>
        public static double ActualMaxSlewRateDps { get; set; } = 4.0;

        // Sidereal tracking rate in °/s (≈ 15"/s)
        private const double SIDEREAL_RATE_DPS = 15.0 / 3600.0;

        /// <summary>
        /// Tries to parse an INDI switch Label or Name to a real °/s value.
        /// Handles: "Max"/"Maximum" → ActualMaxSlewRateDps, "Half"/"Half-Max" → max/2,
        /// "NNx" / "NN×" sidereal multiples (e.g. "48x" → 48 * 0.00417°/s).
        /// </summary>
        private double? TryParseSwitchRateDps(INDISwitch sw) {
            var text = string.IsNullOrWhiteSpace(sw.Label) ? sw.Name : sw.Label;
            var upper = text.ToUpperInvariant();

            if (upper.Contains("MAX") && !upper.Contains("HALF"))
                return ActualMaxSlewRateDps;

            if (upper.Contains("HALF"))
                return ActualMaxSlewRateDps / 2.0;

            var m = Regex.Match(text, @"([\d.]+)\s*[xX×]");
            if (m.Success && double.TryParse(m.Groups[1].Value,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var mult))
                return mult * SIDEREAL_RATE_DPS;

            return null;
        }

        /// <summary>
        /// Returns the switch index that best matches absRate (in °/s).
        /// Uses °/s nearest-neighbour matching only when *every* switch carries a parsable
        /// rate (e.g. OnStep's "48x"/"16x" labels). Named-only rates like Guide/Centering/
        /// Find/Max parse just "Max", which would otherwise always win — so when any switch
        /// is unparsable we fall back to proportional index mapping over the ordered list.
        /// </summary>
        private int FindBestSwitchIndex(double absRate, IList<INDISwitch> switches) {
            int maxIndex = switches.Count - 1;

            int bestIdx = -1;
            double bestDiff = double.MaxValue;
            bool allParsable = true;
            for (int i = 0; i <= maxIndex; i++) {
                var rate = TryParseSwitchRateDps(switches[i]);
                if (rate == null) { allParsable = false; continue; }
                var diff = Math.Abs(rate.Value - absRate);
                if (diff < bestDiff) { bestDiff = diff; bestIdx = i; }
            }

            if (allParsable && bestIdx >= 0) return bestIdx;

            // Not all switches expose a real °/s value (e.g. Guide/Centering/Find/Max) —
            // map proportionally across the ordered switch list instead.
            return Math.Max(0, Math.Min((int)Math.Round(absRate / ActualMaxSlewRateDps * maxIndex), maxIndex));
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



        /// <summary>
        /// Specify critical properties that must arrive before Connect() completes
        /// </summary>
        protected override string[] GetRequiredConnectionProperties() {
            return ["TELESCOPE_TRACK_MODE"];
        }

        public INDITelescope(INDIDeviceInfo device) : base(device) {
        }

        public AlignmentMode AlignmentMode { get; }
        public double Altitude {
            get {
                var altitude = GetNumberPropertyValue("HORIZONTAL_COORD", "ALT");
                if (!altitude.HasValue) {
                    var hourAngle = AstroUtil.GetHourAngle(SiderealTime, RightAscension);
                    var hourAngleDeg = AstroUtil.HoursToDegrees(hourAngle);
                    return AstroUtil.GetAltitude(hourAngleDeg, SiteLatitude, Declination);
                }
                return altitude.Value;
            }
        }
        public double ApertureArea => ApertureDiameter * ApertureDiameter * 0.25 * Math.PI;
        public double ApertureDiameter => GetNumberPropertyValue("TELESCOPE_INFO", "TELESCOPE_APERTURE") ?? double.NaN;

        private bool atHome = false;
        public bool AtHome => atHome;

        public bool AtPark => GetSwitchPropertyValue("TELESCOPE_PARK", "PARK") ?? false;
        public double Azimuth {
            get {
                var azimuth = GetNumberPropertyValue("HORIZONTAL_COORD", "AZ");
                if (!azimuth.HasValue) {
                    var hourAngle = AstroUtil.GetHourAngle(SiderealTime, RightAscension);
                    var hourAngleDeg = AstroUtil.HoursToDegrees(hourAngle);
                    return AstroUtil.GetAzimuth(hourAngleDeg, Altitude, SiteLatitude, Declination);
                }
                return azimuth.Value;
            }
        }
        public double Declination => GetNumberPropertyValue("EQUATORIAL_EOD_COORD", "DEC") ?? double.NaN;
        public double DeclinationRate { get; set; }
        public bool DoesRefraction { get; }
        public double FocalLength => GetNumberPropertyValue("TELESCOPE_INFO", "TELESCOPE_FOCAL_LENGTH") ?? double.NaN;

        // GUIDE_RATE values are sidereal multipliers (e.g. 0.5 = 0.5× sidereal).
        // Convert to deg/s so IndiTelescope can multiply by 3600 to get arcsec/s
        // as expected by the ITelescope GuideRate* contract.
        // Sidereal rate = 15 arcsec/s = 15/3600 deg/s.
        private const double SiderealDegPerSec = 15.0 / 3600.0;

        /// <summary>Returns true when the INDI driver exposes GUIDE_RATE as writable (rw/wo).</summary>
        public bool CanSetGuideRate =>
            GetNumberProperty("GUIDE_RATE") is { } p &&
            p.Permission != PropertyPermission.ReadOnly;

        /// <summary>
        /// Returns true only when the driver exposes HORIZONTAL_COORD as writable (rw/wo).
        /// Many mounts (e.g. EQMod and most GEMs) report HORIZONTAL_COORD read-only - they
        /// publish the current alt/az but reject gotos through it. Reporting false here lets
        /// callers fall back to an equatorial slew, which those mounts do accept.
        /// </summary>
        public bool CanSlewAltAz =>
            GetNumberProperty("HORIZONTAL_COORD") is { } p &&
            p.Permission != PropertyPermission.ReadOnly;

        public double GuideRateDeclination {
            get {
                var val = GetNumberPropertyValue("GUIDE_RATE", "GUIDE_RATE_NS");
                return val.HasValue ? val.Value * SiderealDegPerSec : double.NaN;
            }
            set {
                if (!CanSetGuideRate) return;
                // value is in deg/s; convert back to sidereal multiplier for INDI
                SetNumberValue("GUIDE_RATE", "GUIDE_RATE_NS", value / SiderealDegPerSec);
            }
        }
        public double GuideRateRightAscension {
            get {
                var val = GetNumberPropertyValue("GUIDE_RATE", "GUIDE_RATE_WE");
                return val.HasValue ? val.Value * SiderealDegPerSec : double.NaN;
            }
            set {
                if (!CanSetGuideRate) return;
                // value is in deg/s; convert back to sidereal multiplier for INDI
                SetNumberValue("GUIDE_RATE", "GUIDE_RATE_WE", value / SiderealDegPerSec);
            }
        }
        public bool IsPulseGuiding { get; }
        public double RightAscension => GetNumberPropertyValue("EQUATORIAL_EOD_COORD", "RA") ?? double.NaN;
        public double RightAscensionRate { get; set; }
        public PierSide SideOfPier {
            get {
                var pierSide = GetSwitchPropertyValue("TELESCOPE_PIER_SIDE", "PIER_EAST");
                if (pierSide.HasValue) {
                    return pierSide.Value ? PierSide.pierEast : PierSide.pierWest;
                }
                return PierSide.pierUnknown;
            }
        }
        public double SiderealTime {
            get {
                double? lst = GetNumberPropertyValue("TIME_LST", "LST");
                if (lst.HasValue) {
                    return lst.Value;
                }

                Logger.Debug("Mount does not supply sidereal time, falling back client computation");
                return AstroUtil.GetLocalSiderealTimeNow(SiteLongitude);
            }
        }
        public double SiteElevation {
            get => GetNumberPropertyValue("GEOGRAPHIC_COORD", "ELEV") ?? double.NaN;
            set {
                SetNumberValue("GEOGRAPHIC_COORD", "ELEV", value);
            }
        }
        public double SiteLatitude {
            get => GetNumberPropertyValue("GEOGRAPHIC_COORD", "LAT") ?? double.NaN;
            set {
                SetNumberValue("GEOGRAPHIC_COORD", "LAT", value);
            }
        }
        public double SiteLongitude {
            get => GetNumberPropertyValue("GEOGRAPHIC_COORD", "LONG") ?? double.NaN;
            set {
                SetNumberValue("GEOGRAPHIC_COORD", "LONG", value);
            }
        }
        public int SlewSettleTime { get; }
        public bool Slewing {
            get {
                bool motionWest = GetSwitchPropertyValue("TELESCOPE_MOTION_WE", "MOTION_WEST") ?? false;
                bool motionEast = GetSwitchPropertyValue("TELESCOPE_MOTION_WE", "MOTION_EAST") ?? false;
                bool motionNorth = GetSwitchPropertyValue("TELESCOPE_MOTION_NS", "MOTION_NORTH") ?? false;
                bool motionSouth = GetSwitchPropertyValue("TELESCOPE_MOTION_NS", "MOTION_SOUTH") ?? false;
                var motionRaDec = GetProperty("EQUATORIAL_EOD_COORD")?.State == PropertyState.Busy;
                var motionAltAz = GetProperty("HORIZONTAL_COORD")?.State == PropertyState.Busy;
                return motionWest || motionEast || motionNorth || motionSouth || motionRaDec || motionAltAz;
            }
        }
        public double TargetDeclination { get; }
        public double TargetRightAscension { get; }
        public bool Tracking {
            get => GetSwitchPropertyValue("TELESCOPE_TRACK_STATE", "TRACK_ON") ?? false;
            set {
                // Use SetSwitchProperty to respect OneOfMany rule
                var switchValues = new Dictionary<string, bool> {
                    { "TRACK_ON", value },
                    { "TRACK_OFF", !value }
                };
                SetSwitchProperty("TELESCOPE_TRACK_STATE", switchValues);

                // Negate atHome, if tracking was enabled
                if (value) {
                    atHome = false;
                }
            }
        }

        /// <summary>
        /// Set the telescope tracking mode (Sidereal, Lunar, Solar, Custom)
        /// Uses SetSwitchProperty to respect the OneOfMany rule for TELESCOPE_TRACK_MODE
        /// </summary>
        /// <param name="mode">Tracking mode: 0=Sidereal, 1=Lunar, 2=Solar, 3=King/Custom, 5=Stopped</param>
        public void SetTrackingMode(TrackingMode mode) {
            // If Stopped, just turn tracking off without modifying TELESCOPE_TRACK_MODE
            if (mode == TrackingMode.Stopped) {
                Tracking = false;
                return;
            }

            // Build the switch values dictionary based on the desired tracking mode
            var switchValues = new Dictionary<string, bool>();

            switch (mode) {
                case TrackingMode.Sidereal:
                    switchValues["TRACK_SIDEREAL"] = true;
                    switchValues["TRACK_LUNAR"] = false;
                    switchValues["TRACK_SOLAR"] = false;
                    switchValues["TRACK_CUSTOM"] = false;
                    break;
                case TrackingMode.Lunar:
                    switchValues["TRACK_SIDEREAL"] = false;
                    switchValues["TRACK_LUNAR"] = true;
                    switchValues["TRACK_SOLAR"] = false;
                    switchValues["TRACK_CUSTOM"] = false;
                    break;
                case TrackingMode.Solar:
                    switchValues["TRACK_SIDEREAL"] = false;
                    switchValues["TRACK_LUNAR"] = false;
                    switchValues["TRACK_SOLAR"] = true;
                    switchValues["TRACK_CUSTOM"] = false;
                    break;
                case TrackingMode.King:
                case TrackingMode.Custom:
                    switchValues["TRACK_SIDEREAL"] = false;
                    switchValues["TRACK_LUNAR"] = false;
                    switchValues["TRACK_SOLAR"] = false;
                    switchValues["TRACK_CUSTOM"] = true;
                    break;
            }

            // Use SetSwitchProperty to respect OneOfMany rule
            SetSwitchProperty("TELESCOPE_TRACK_MODE", switchValues);
            // Turn tracking on after setting the mode
            Tracking = true;
            atHome = false;
        }

        /// <summary>
        /// Get the current tracking mode from the TELESCOPE_TRACK_MODE property
        /// </summary>
        /// <returns>The current tracking mode</returns>
        public TrackingMode GetTrackingMode() {
            // If tracking is off, return Stopped
            if (!Tracking) {
                return TrackingMode.Stopped;
            }

            try {
                // Check which tracking mode switch is active
                if (GetSwitchPropertyValue("TELESCOPE_TRACK_MODE", "TRACK_SIDEREAL") == true) {
                    return TrackingMode.Sidereal;
                } else if (GetSwitchPropertyValue("TELESCOPE_TRACK_MODE", "TRACK_LUNAR") == true) {
                    return TrackingMode.Lunar;
                } else if (GetSwitchPropertyValue("TELESCOPE_TRACK_MODE", "TRACK_SOLAR") == true) {
                    return TrackingMode.Solar;
                } else if (GetSwitchPropertyValue("TELESCOPE_TRACK_MODE", "TRACK_CUSTOM") == true) {
                    return TrackingMode.King;
                }
            } catch (ArgumentException) {
                throw new NotImplementedException();
            }

            // Default to Sidereal if we can't determine the mode
            return TrackingMode.Sidereal;
        }

        /// <summary>
        /// Get the list of supported tracking modes from the TELESCOPE_TRACK_MODE property
        /// </summary>
        /// <returns>List of supported tracking modes</returns>
        public IList<TrackingMode> GetSupportedTrackingModes() {
            var modes = new List<TrackingMode>();

            // Sidereal is always supported
            modes.Add(TrackingMode.Sidereal);

            try {
                var trackModeProperty = GetSwitchProperty("TELESCOPE_TRACK_MODE");
                if (trackModeProperty != null) {
                    foreach (var sw in trackModeProperty.Switches) {
                        switch (sw.Name) {
                            case "TRACK_LUNAR":
                                modes.Add(TrackingMode.Lunar);
                                break;
                            case "TRACK_SOLAR":
                                modes.Add(TrackingMode.Solar);
                                break;
                            case "TRACK_CUSTOM":
                                modes.Add(TrackingMode.King);
                                break;
                        }
                    }
                }
            } catch (ArgumentException) {
                throw new NotImplementedException();
            }

            // Stopped is always available
            modes.Add(TrackingMode.Stopped);

            return modes;
        }

        public DateTime UTCDate {
            get {
                try {
                    // Read UTC from TIME_UTC property
                    var utcTime = GetTextPropertyValue("TIME_UTC", "UTC");
                    if (!string.IsNullOrEmpty(utcTime) && DateTime.TryParse(utcTime, out var parsedTime)) {
                        return DateTime.SpecifyKind(parsedTime, DateTimeKind.Utc);
                    }
                } catch (Exception ex) {
                    Logger.Warning($"Could not read TIME_UTC: {ex.Message}");
                }
                return DateTime.MinValue;
            }
            set {
                try {
                    // UTC and OFFSET must be sent together in ONE vector update.
                    // INDI drivers (e.g. indi_lx200am5 for the ZWO AM5) apply the whole
                    // TIME_UTC vector atomically. If only UTC is written, the cached OFFSET
                    // (0 after mount power-on — the AM5 has no RTC) is re-sent, the mount
                    // computes a wrong Local Sidereal Time and every GOTO fails.
                    //
                    // INDI convention: OFFSET = hours EAST of UTC (e.g. +2 for CEST).
                    // The LX200 sign inversion is handled inside the driver.
                    var utcString = value.ToString("yyyy-MM-ddTHH:mm:ss");
                    var utcOffsetHours = TimeZoneInfo.Local.GetUtcOffset(value).TotalHours;
                    var offsetString = utcOffsetHours.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
                    SetTextValues("TIME_UTC", ("UTC", utcString), ("OFFSET", offsetString));
                    Logger.Debug($"Set mount UTC time to {utcString}, UTC offset to {offsetString}h");
                } catch (Exception ex) {
                    Logger.Error($"Could not set TIME_UTC: {ex.Message}");
                    throw;
                }
            }
        }


        public void AbortSlew() {
            try {
                SetSwitchValue("TELESCOPE_ABORT_MOTION", "ABORT_MOTION", true);
            } catch (ArgumentException) {
                throw new NotImplementedException();
            }
        }

        public IAxisRates AxisRates(TelescopeAxes axis) {
            try {
                // Check if we have TELESCOPE_SLEW_RATE (OnStep style with discrete rates)
                var slewRateProp = GetSwitchProperty("TELESCOPE_SLEW_RATE");
                if (slewRateProp != null && slewRateProp.Switches.Count > 1) {
                    // Report real °/s so PolarAlignment calculates correct move timeouts
                    Logger.Debug($"TELESCOPE_SLEW_RATE: using configured ActualMaxSlewRateDps={ActualMaxSlewRateDps} as axis rate max");
                    return new AxisRates(0.0, ActualMaxSlewRateDps);
                }

                // Try to get the TELESCOPE_MOTION_RATE property to find min/max values
                var motionRateProperty = GetNumberProperty("TELESCOPE_MOTION_RATE");
                if (motionRateProperty != null) {
                    var motionRateElement = motionRateProperty.Numbers.FirstOrDefault(n => n.Name == "MOTION_RATE");
                    if (motionRateElement != null) {
                        return new AxisRates(motionRateElement.Min, motionRateElement.Max);
                    }
                }
            } catch (Exception ex) {
                Logger.Debug($"Error getting axis rates: {ex.Message}");
            }

            // Return a default continuous range of 0.0 to 9.0
            // For OnStep devices with 10 slew rate levels (0-9)
            return new AxisRates(0.0, 9.0);
        }

        public void ConfigureJNOW() {
            try {
                // INDI drivers may support different coordinate properties:
                // - EQUATORIAL_EOD_COORD: Epoch of Date (JNOW)
                // - EQUATORIAL_COORD: J2000
                // We want to ensure we're using EOD (JNOW)

                // Some drivers have TELESCOPE_EQUATORIAL_COORD property to select which system
                // Try to set it to EOD if available
                try {
                    var eqCoordProp = GetSwitchProperty("TELESCOPE_EQUATORIAL_COORD");
                    if (eqCoordProp != null) {
                        SetSwitchValue("TELESCOPE_EQUATORIAL_COORD", "EOD", true);
                    }
                } catch (ArgumentException) {
                    // Property doesn't exist, that's okay
                }

                Logger.Debug("INDI configured to use EQUATORIAL_EOD_COORD (JNOW)");
            } catch (Exception ex) {
                Logger.Warning($"Could not configure JNOW: {ex.Message}");
            }
        }

        private void SetSlewRateForMotion(double absRate) {
            // Different INDI drivers use different methods to set slew rate:
            // 1. TELESCOPE_MOTION_RATE (numeric) - used by some simulators
            // 2. TELESCOPE_SLEW_RATE (switch) - used by OnStep and others (GUIDE/CENTERING/FIND/MAX)

            // Try numeric TELESCOPE_MOTION_RATE first
            try {
                SetNumberValue("TELESCOPE_MOTION_RATE", "MOTION_RATE", absRate);
                Task.Delay(50).Wait();
                return;
            } catch { }

            // Try switch-based TELESCOPE_SLEW_RATE (OnStep style)
            try {
                var slewRateProp = GetSwitchProperty("TELESCOPE_SLEW_RATE");
                if (slewRateProp != null) {
                    int targetIndex = FindBestSwitchIndex(absRate, slewRateProp.Switches);
                    var targetSwitch = slewRateProp.Switches[targetIndex];
                    Logger.Debug($"SetSlewRateForMotion: {absRate}°/s → switch {targetIndex}/{slewRateProp.Switches.Count - 1} '{targetSwitch.Label}' ({targetSwitch.Name})");

                    foreach (var sw in slewRateProp.Switches) {
                        sw.Value = (sw.Name == targetSwitch.Name);
                    }
                    INDIClient.Instance.SendProperty(slewRateProp);
                    Task.Delay(100).Wait();
                    return;
                }
            } catch (Exception ex) {
                Logger.Debug($"TELESCOPE_SLEW_RATE not available: {ex.Message}");
            }

            Logger.Warning("No slew rate property available, using driver default");
        }

        /// <summary>Returns a string like "switch 7/9 'Half-Max'" for use in log messages.</summary>
        public string GetSwitchDescription(double absRate) {
            if (absRate == 0) return "";
            try {
                var slewRateProp = GetSwitchProperty("TELESCOPE_SLEW_RATE");
                if (slewRateProp != null && slewRateProp.Switches.Count > 1) {
                    int idx = FindBestSwitchIndex(absRate, slewRateProp.Switches);
                    var sw = slewRateProp.Switches[idx];
                    var label = string.IsNullOrWhiteSpace(sw.Label) ? sw.Name : sw.Label;
                    return $"switch {idx}/{slewRateProp.Switches.Count - 1} '{label}'";
                }
            } catch { }
            return "";
        }

        public void MoveAxis(TelescopeAxes axis, double rate) {
            try {
                // Rate is in degrees per second, sign indicates direction
                // Per NINA convention: 
                // Primary: negative=West, positive=East
                // Secondary: positive=North, negative=South
                double absRate = Math.Abs(rate);

                if (rate != 0) {
                    atHome = false;
                }

                Logger.Debug($"INDITelescope.MoveAxis: axis={axis}, rate={rate}, absRate={absRate}");

                switch (axis) {
                    case TelescopeAxes.Primary:
                        // Primary axis is RA/Azimuth - use West/East motion
                        if (rate != 0) {
                            // Try to set the motion rate - different drivers use different properties
                            SetSlewRateForMotion(absRate);

                            // Set the direction switch
                            var prop = GetSwitchProperty("TELESCOPE_MOTION_WE");
                            if (prop != null) {
                                if (rate < 0) {
                                    // Negative rate = West
                                    foreach (var sw in prop.Switches) {
                                        sw.Value = (sw.Name == "MOTION_WEST");
                                    }
                                } else {
                                    // Positive rate = East
                                    foreach (var sw in prop.Switches) {
                                        sw.Value = (sw.Name == "MOTION_EAST");
                                    }
                                }
                                INDIClient.Instance.SendProperty(prop);
                            }
                        } else {
                            // Stop motion - only send if actually moving
                            var prop = GetSwitchProperty("TELESCOPE_MOTION_WE");
                            if (prop != null) {
                                if (prop.Switches.Any(sw => sw.Value)) {
                                    foreach (var sw in prop.Switches) {
                                        sw.Value = false;
                                    }
                                    INDIClient.Instance.SendProperty(prop);
                                }
                            }
                        }
                        break;
                    case TelescopeAxes.Secondary:
                        // Secondary axis is Dec/Altitude - use North/South motion
                        if (rate != 0) {
                            // Try to set the motion rate - different drivers use different properties
                            SetSlewRateForMotion(absRate);

                            // Set the direction switch
                            var prop = GetSwitchProperty("TELESCOPE_MOTION_NS");
                            if (prop != null) {
                                if (rate > 0) {
                                    // Positive rate = North
                                    foreach (var sw in prop.Switches) {
                                        sw.Value = (sw.Name == "MOTION_NORTH");
                                    }
                                } else {
                                    // Negative rate = South
                                    foreach (var sw in prop.Switches) {
                                        sw.Value = (sw.Name == "MOTION_SOUTH");
                                    }
                                }
                                INDIClient.Instance.SendProperty(prop);
                            }
                        } else {
                            // Stop motion - only send if actually moving
                            var prop = GetSwitchProperty("TELESCOPE_MOTION_NS");
                            if (prop != null) {
                                if (prop.Switches.Any(sw => sw.Value)) {
                                    foreach (var sw in prop.Switches) {
                                        sw.Value = false;
                                    }
                                    INDIClient.Instance.SendProperty(prop);
                                }
                            }
                        }
                        break;
                }
            } catch (ArgumentException) {
                throw new NotImplementedException();
            }
        }

        public void PulseGuide(GuideDirections direction, int duration) {
            try {
                switch (direction) {
                    case GuideDirections.guideNorth:
                        SetNumberValue("TELESCOPE_TIMED_GUIDE_NS", "TIMED_GUIDE_N", duration);
                        break;
                    case GuideDirections.guideSouth:
                        SetNumberValue("TELESCOPE_TIMED_GUIDE_NS", "TIMED_GUIDE_S", duration);
                        break;
                    case GuideDirections.guideWest:
                        SetNumberValue("TELESCOPE_TIMED_GUIDE_WE", "TIMED_GUIDE_W", duration);
                        break;
                    case GuideDirections.guideEast:
                        SetNumberValue("TELESCOPE_TIMED_GUIDE_WE", "TIMED_GUIDE_E", duration);
                        break;
                }
            } catch (ArgumentException) {
                throw new NotImplementedException();
            }
        }

        public async Task ParkAsync(CancellationToken ct = default) {
            try {
                SetSwitchValue("TELESCOPE_PARK", "PARK", true);

                // Wait for property to become busy then return to idle/ok
                await Task.Delay(100, ct);

                var parkProp = GetProperty("TELESCOPE_PARK");
                while ((Slewing == true || parkProp?.State == PropertyState.Busy) && !ct.IsCancellationRequested) {
                    await Task.Delay(200, ct);
                    parkProp = GetProperty("TELESCOPE_PARK");
                }

                atHome = false;
            } catch (ArgumentException) {
                throw new NotImplementedException();
            }
        }

        public async Task UnparkAsync(CancellationToken ct = default) {
            try {
                SetSwitchValue("TELESCOPE_PARK", "UNPARK", true);

                // Wait for property to become busy then return to idle/ok
                await Task.Delay(100, ct);

                var parkProp = GetProperty("TELESCOPE_PARK");
                while (parkProp?.State == PropertyState.Busy && !ct.IsCancellationRequested) {
                    await Task.Delay(200, ct);
                    parkProp = GetProperty("TELESCOPE_PARK");
                }
            } catch (ArgumentException) {
                throw new NotImplementedException();
            }
        }

        public void SetPark() {
            try {
                SetSwitchValue("TELESCOPE_PARK_OPTION", "PARK_CURRENT", true);
            } catch (ArgumentException) {
                throw new NotImplementedException();
            }
        }

        public async Task SlewToCoordinates(double ra, double dec) {
            try {
                // Check mount state before slewing
                if (AtPark) {
                    Logger.Error("Cannot slew: Mount is parked");
                    throw new InvalidOperationException("Mount is parked");
                }

                atHome = false;

                // Enable slewing mode
                SetSwitchValue("ON_COORD_SET", "SLEW", true);

                // Send coordinates and wait for server acknowledgement (Busy state)
                if (!await SetNumberValuesAsync("EQUATORIAL_EOD_COORD", TimeSpan.FromSeconds(30), ("RA", ra), ("DEC", dec))) {
                    throw new InvalidOperationException("Mount rejected coordinates");
                }
            } catch (ArgumentException) {
                throw new NotImplementedException();
            } catch (Exception ex) {
                Logger.Error($"Error in SlewToCoordinates: {ex.Message}");
                throw;
            }
        }

        public async Task SlewToCoordinatesTaskAsync(double ra, double dec, CancellationToken ct = default) {
            try {
                // Slew
                await SlewToCoordinates(ra, dec);

                // Wait for slew to finish
                while (Slewing && !ct.IsCancellationRequested) {
                    var coordState = GetProperty("EQUATORIAL_EOD_COORD")?.State;
                    if (coordState == PropertyState.Alert) {
                        Logger.Error("EQUATORIAL_EOD_COORD in Alert state - slew rejected by mount");
                        throw new InvalidOperationException("Slew rejected by mount - check mount limits and target accessibility");
                    }

                    await Task.Delay(500, ct);
                }
            } catch (ArgumentException) {
                throw new NotImplementedException();
            } catch (Exception ex) {
                Logger.Error($"Error in SlewToCoordinatesTaskAsync: {ex.Message}");
                throw;
            }
        }

        public void SlewToAltAz(double azimuth, double altitude) {
            try {
                // Check mount state before slewing
                if (AtPark) {
                    Logger.Error("Cannot slew: Mount is parked");
                    throw new InvalidOperationException("Mount is parked");
                }

                atHome = false;

                // Enable slewing mode
                SetSwitchValue("ON_COORD_SET", "SLEW", true);

                // Send coordinates
                SetNumberValues("HORIZONTAL_COORD", ("ALT", altitude), ("AZ", azimuth));
            } catch (ArgumentException) {
                throw new NotImplementedException();
            } catch (Exception ex) {
                Logger.Error($"Error in SlewToCoordinates: {ex.Message}");
                throw;
            }
        }

        public async Task SlewToAltAzTaskAsync(double azimuth, double altitude, CancellationToken ct = default) {
            try {
                // Slew
                SlewToAltAz(azimuth, altitude);

                // Wait a bit for the slew to start
                await Task.Delay(1000, ct);

                // Check the actual property state
                var coordProp = GetProperty("HORIZONTAL_COORD");

                // Wait for slew to finish
                while (Slewing && !ct.IsCancellationRequested) {
                    // Check slewing status
                    if (coordProp?.State == PropertyState.Idle) {
                        // Done
                        break;
                    } else if (coordProp?.State == PropertyState.Alert) {
                        Logger.Error("HORIZONTAL_COORD in Alert state - slew rejected by mount");
                        throw new InvalidOperationException("Slew rejected by mount - check mount limits and target accessibility");
                    }

                    await Task.Delay(500, ct);
                }
            } catch (ArgumentException) {
                throw new NotImplementedException();
            } catch (Exception ex) {
                Logger.Error($"Error in SlewToCoordinatesTaskAsync: {ex.Message}");
                throw;
            }
        }

        public void SyncToCoordinates(double ra, double dec) {
            try {
                // Check mount state before slewing
                if (AtPark) {
                    Logger.Error("Cannot slew: Mount is parked");
                    throw new InvalidOperationException("Mount is parked");
                }

                // Enable sync mode
                SetSwitchValue("ON_COORD_SET", "SYNC", true);

                // Send coordinates
                SetNumberValues("EQUATORIAL_EOD_COORD", ("RA", ra), ("DEC", dec));
            } catch (ArgumentException) {
                throw new NotImplementedException();
            } catch (Exception ex) {
                Logger.Error($"Error in SlewToCoordinates: {ex.Message}");
                throw;
            }
        }

        public async Task FindHomeAsync(CancellationToken ct = default) {
            try {
                // If the telescope cannot park, throw exception
                var homeProp = GetSwitchProperty("TELESCOPE_HOME");
                if (homeProp == null) {
                    Logger.Warning("TELESCOPE_HOME property not found");
                    throw new NotImplementedException("TELESCOPE_HOME property not found");
                }

                // Drivers name the "go home" action element differently, e.g. the Starbook Ten
                // uses "FindHome" while libindi's standard uses "GoHome" and others use "FIND"/"GO".
                // Pick the first recognized action element; if the vector has a single switch, use
                // that (most TELESCOPE_HOME vectors expose only the go-home action).
                string[] homeActionNames = ["FindHome", "GoHome", "FIND", "GO", "HOME", "Home", "SLEW"];
                var homeSwitch = homeProp.Switches.FirstOrDefault(s => homeActionNames.Contains(s.Name, StringComparer.OrdinalIgnoreCase))
                                 ?? (homeProp.Switches.Count == 1 ? homeProp.Switches[0] : null);

                if (homeSwitch == null) {
                    Logger.Warning($"TELESCOPE_HOME has no recognized action switch (elements: {string.Join(", ", homeProp.Switches.Select(s => s.Name))})");
                    throw new NotImplementedException("TELESCOPE_HOME switch not found");
                }

                Logger.Info($"Sending home command via TELESCOPE_HOME.{homeSwitch.Name}");
                SetSwitchValue("TELESCOPE_HOME", homeSwitch.Name, true);

                // Wait for property to become busy then return to idle/ok
                await Task.Delay(1000, ct);

                homeProp = GetSwitchProperty("TELESCOPE_HOME");
                Logger.Debug($"Slewing state: {Slewing}");
                while (Slewing == true && !ct.IsCancellationRequested) {
                    await Task.Delay(500, ct);
                    homeProp = GetSwitchProperty("TELESCOPE_HOME");
                    Logger.Debug($"Waiting to reach home...");
                }

                Logger.Debug($"Reached home");
                atHome = true;
            } catch (ArgumentException) {
                throw new NotImplementedException();
            }
        }

        public bool CanMoveAxis(TelescopeAxes axis) {
            try {
                // Check if motion properties exist
                switch (axis) {
                    case TelescopeAxes.Primary:
                        GetProperty("TELESCOPE_MOTION_WE");
                        return true;
                    case TelescopeAxes.Secondary:
                        GetProperty("TELESCOPE_MOTION_NS");
                        return true;
                    default:
                        return false;
                }
            } catch {
                return false;
            }
        }

        public PierSide DestinationSideOfPier(double ra, double dec) {
            // INDI doesn't provide a standard way to predict pier side
            // Return unknown/current pier side as best guess
            return SideOfPier;
        }
    }
}
