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
                return mult * SiderealDegPerSec;

            return null;
        }

        /// <summary>
        /// Returns the index of the switch whose parsed rate is closest to absRate.
        /// Falls back to proportional mapping if no labels can be parsed.
        /// </summary>
        private int FindBestSwitchIndex(double absRate, IList<INDISwitch> switches) {
            int maxIndex = switches.Count - 1;

            int bestIdx = -1;
            double bestDiff = double.MaxValue;
            for (int i = 0; i <= maxIndex; i++) {
                var rate = TryParseSwitchRateDps(switches[i]);
                if (rate == null) continue;
                var diff = Math.Abs(rate.Value - absRate);
                if (diff < bestDiff) { bestDiff = diff; bestIdx = i; }
            }

            if (bestIdx >= 0) return bestIdx;

            // No labels parsed — fall back to proportional mapping
            return Math.Max(0, Math.Min((int)Math.Round(absRate / ActualMaxSlewRateDps * maxIndex), maxIndex));
        }



        public override void OnTextPropertyUpdated(INDITextProperty p) {
            base.OnTextPropertyUpdated(p);
        }

        private bool _isPulseGuidingNS;
        private bool _isPulseGuidingWE;

        // Coordinate-motion tracking for the Slewing property. Some firmwares (observed on
        // OnStep while homing) move the mount without reporting a slew on ANY property state,
        // but the driver still polls and pushes EQUATORIAL_EOD_COORD values every cycle — so
        // an angular RATE computed between consecutive updates is a reliable motion signal.
        // All three sample fields are touched only on the receive thread; the Slewing getter
        // reads only _lastCoordMotionAt (atomic 64-bit) cross-thread.
        private (double Ra, double Dec)? _lastCoordSample;
        private DateTime _lastCoordSampleAt;
        private DateTime _lastCoordMotionAt = DateTime.MinValue;
        private DateTime _suppressCoordMotionUntil = DateTime.MinValue;
        // How long after the last observed coordinate motion the mount still counts as
        // moving. Must cover at least one driver polling period (default 1s) plus margin,
        // since a mount that just stopped simply ceases to send coordinate updates.
        private static readonly TimeSpan CoordMotionWindow = TimeSpan.FromSeconds(3);

        public override void OnNumberPropertyUpdated(INDINumberProperty p) {
            base.OnNumberPropertyUpdated(p);

            switch (p.Name) {
                case "TELESCOPE_TIMED_GUIDE_NS":
                    _isPulseGuidingNS = p.State == PropertyState.Busy;
                    break;
                case "TELESCOPE_TIMED_GUIDE_WE":
                    _isPulseGuidingWE = p.State == PropertyState.Busy;
                    break;
                case "EQUATORIAL_EOD_COORD":
                    TrackCoordinateMotion(p);
                    break;
            }
        }

        private void TrackCoordinateMotion(INDINumberProperty p) {
            var ra = p.Numbers.FirstOrDefault(n => n.Name == "RA")?.Value;
            var dec = p.Numbers.FirstOrDefault(n => n.Name == "DEC")?.Value;
            if (ra == null || dec == null) {
                return;
            }

            var now = DateTime.UtcNow;
            var current = (Ra: ra.Value, Dec: dec.Value);

            if (_lastCoordSample is { } prev) {
                // Local receive time, not the INDI timestamp — that only has 1s resolution.
                var dt = (now - _lastCoordSampleAt).TotalSeconds;
                if (dt > 0.05) {
                    var dRaHours = Math.Abs(current.Ra - prev.Ra);
                    if (dRaHours > 12) {
                        dRaHours = 24 - dRaHours; // RA wrap-around
                    }
                    var deg = Math.Max(dRaHours * 15.0, Math.Abs(current.Dec - prev.Dec));
                    // Rate-based (deg/s) so the driver's polling period doesn't matter:
                    // 0.05°/s is ~12× the apparent sidereal RA drift of a stopped,
                    // non-tracking mount (0.0042°/s) and far below any slew/home rate.
                    // Suppressed briefly around a sync, whose instant coordinate jump is
                    // not physical motion.
                    if (deg / dt > 0.05 && now >= _suppressCoordMotionUntil) {
                        _lastCoordMotionAt = now;
                    }
                }
            }

            _lastCoordSample = current;
            _lastCoordSampleAt = now;
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

        // Set while ParkAsync is running. Needed because the vector-state check below is
        // not enough on its own: lx200_OnStep's Park() forgets TrackState=SCOPE_PARKING,
        // so the base class publishes TELESCOPE_PARK as Ok (not Busy) for the WHOLE park
        // motion — and the INDI tree is off-limits for local patches, so this must be
        // handled client-side.
        private volatile bool _parkInProgress;

        // PARK=On alone is not "at park": per libindi convention the switch shows the
        // TARGET while the vector state shows progress — parking publishes PARK=On the
        // moment the command is accepted (and our own optimistic send does too), while the
        // mount is still slewing to its park position. At park = target reached AND no
        // park operation in flight (vector Busy for well-behaved drivers, _parkInProgress
        // for pins-initiated parks on drivers that mis-report, Slewing for the motion
        // itself incl. externally-commanded parks).
        public bool AtPark =>
            (GetSwitchPropertyValue("TELESCOPE_PARK", "PARK") ?? false)
            && GetProperty("TELESCOPE_PARK")?.State != PropertyState.Busy
            && !_parkInProgress
            && !Slewing;
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
        // Tracked from TELESCOPE_TIMED_GUIDE_NS/WE Busy state (see OnNumberPropertyUpdated) —
        // INDI has no dedicated "is guiding" flag, so a pulse counts as in-progress for exactly
        // as long as the driver reports the corresponding guide vector as Busy.
        public bool IsPulseGuiding => _isPulseGuidingNS || _isPulseGuidingWE;
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
                // HORIZONTAL_COORD.State is deliberately excluded: AltAz mounts in tracking
                // mode keep it Busy indefinitely (both axes move continuously to compensate
                // for Earth's rotation), which would make Slewing permanently true.
                // Coordinate motion covers firmwares that move without reporting any slew
                // state (OnStep homing) — see TrackCoordinateMotion.
                var coordMotion = DateTime.UtcNow - _lastCoordMotionAt < CoordMotionWindow;
                return motionWest || motionEast || motionNorth || motionSouth || motionRaDec || coordMotion;
            }
        }
        public double TargetDeclination { get; }
        public double TargetRightAscension { get; }
        public bool Tracking {
            get => GetSwitchPropertyValue("TELESCOPE_TRACK_STATE", "TRACK_ON") ?? false;
            set {
                try {
                    // Use SetSwitchProperty to respect OneOfMany rule
                    var switchValues = new Dictionary<string, bool> {
                        { "TRACK_ON", value },
                        { "TRACK_OFF", !value }
                    };
                    SetSwitchProperty("TELESCOPE_TRACK_STATE", switchValues);
                } catch (ArgumentException ex) {
                    // Mounts without TELESCOPE_TRACK_STATE (and Stopped IS always advertised
                    // by GetSupportedTrackingModes) get the NotImplementedException contract
                    // like every other unsupported capability, not a raw ArgumentException.
                    throw new NotImplementedException(ex.Message, ex);
                }

                // Negate atHome, if tracking was enabled
                if (value) {
                    atHome = false;
                }
            }
        }

        public async Task<bool> EnableTrackingAsync(CancellationToken ct = default) {
            // Already tracking: nothing to transition. Skipping the round-trip also preserves the
            // fast path for gotos issued while the mount is already tracking.
            if (Tracking) {
                return true;
            }

            // Wait for the driver to acknowledge TRACK_ON rather than firing the switch and
            // returning immediately. A goto sent microseconds after a fire-and-forget enable races
            // the tracking transition, and OnStep (and similar mounts) reject that goto as "below
            // the horizon limit" until tracking has started and a valid position is established.
            var acknowledged = await SetSwitchValueAsync("TELESCOPE_TRACK_STATE", "TRACK_ON", true, TimeSpan.FromSeconds(10));

            if (acknowledged) {
                atHome = false;
                // Give the mount a beat to settle on a valid tracked position before the caller
                // issues the goto.
                await Task.Delay(500, ct);
            }

            return acknowledged;
        }

        /// <summary>
        /// Set the telescope tracking mode (Sidereal, Lunar, Solar, Custom)
        /// Uses SetSwitchValue, which applies the OneOfMany radio rule and skips (with a
        /// warning) when the mount does not expose the requested mode's switch — hand-building
        /// the whole vector here used to send an illegal all-off update in that case (e.g.
        /// King on a mount without TRACK_CUSTOM).
        /// </summary>
        /// <param name="mode">Tracking mode: 0=Sidereal, 1=Lunar, 2=Solar, 3=King/Custom, 5=Stopped</param>
        public void SetTrackingMode(TrackingMode mode) {
            // If Stopped, just turn tracking off without modifying TELESCOPE_TRACK_MODE
            if (mode == TrackingMode.Stopped) {
                Tracking = false;
                return;
            }

            var elementName = mode switch {
                TrackingMode.Sidereal => "TRACK_SIDEREAL",
                TrackingMode.Lunar => "TRACK_LUNAR",
                TrackingMode.Solar => "TRACK_SOLAR",
                TrackingMode.King or TrackingMode.Custom => "TRACK_CUSTOM",
                _ => null
            };

            if (elementName != null) {
                SetSwitchValue("TELESCOPE_TRACK_MODE", elementName, true);
            }

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

            // Stopped is always available
            modes.Add(TrackingMode.Stopped);

            return modes;
        }

        public DateTime UTCDate {
            get {
                try {
                    // Read UTC from TIME_UTC property. Must parse with InvariantCulture — the
                    // wire format is a fixed ISO-8601-like string, not locale-dependent — and
                    // AssumeUniversal so a value without a timezone offset isn't silently
                    // reinterpreted as local time under a non-UTC system locale.
                    var utcTime = GetTextPropertyValue("TIME_UTC", "UTC");
                    if (!string.IsNullOrEmpty(utcTime) && DateTime.TryParse(utcTime,
                            System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                            out var parsedTime)) {
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
                // libindi's standard element is TELESCOPE_ABORT_MOTION.ABORT (inditelescope.cpp:
                // AbortSP[0].fill("ABORT", ...)). The previous "ABORT_MOTION" element name exists
                // in no driver, so aborts were silently skipped by the element guard.
                SetSwitchValue("TELESCOPE_ABORT_MOTION", "ABORT", true);
            } catch (ArgumentException ex) {
                throw new NotImplementedException(ex.Message, ex);
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

        public SlewRateCapability GetSlewRateCapability() {
            try {
                // 1. Discrete switch-based rates (OnStep, EQMod, LX200, ...).
                //    Counts and labels vary per driver, so we surface them verbatim.
                var slewRateProp = GetSwitchProperty("TELESCOPE_SLEW_RATE");
                if (slewRateProp != null && slewRateProp.Switches.Count > 0) {
                    var options = new List<SlewRateOption>(slewRateProp.Switches.Count);
                    for (int i = 0; i < slewRateProp.Switches.Count; i++) {
                        var sw = slewRateProp.Switches[i];
                        options.Add(new SlewRateOption {
                            Index = i,
                            Name = sw.Name,
                            Label = string.IsNullOrWhiteSpace(sw.Label) ? sw.Name : sw.Label,
                            IsSelected = sw.Value,
                            EstimatedRateDps = TryParseSwitchRateDps(sw)
                        });
                    }
                    return new SlewRateCapability {
                        Kind = SlewRateKind.Discrete,
                        PropertyName = "TELESCOPE_SLEW_RATE",
                        Options = options
                    };
                }

                // 2. Continuous numeric rate in °/s (simulators and some drivers).
                var motionRateProp = GetNumberProperty("TELESCOPE_MOTION_RATE");
                var motionRate = motionRateProp?.Numbers.FirstOrDefault(n => n.Name == "MOTION_RATE");
                if (motionRate != null) {
                    return new SlewRateCapability {
                        Kind = SlewRateKind.Continuous,
                        PropertyName = "TELESCOPE_MOTION_RATE",
                        Min = motionRate.Min,
                        Max = motionRate.Max,
                        Step = motionRate.Step,
                        Unit = "°/s",
                        CurrentValue = motionRate.Value
                    };
                }
            } catch (Exception ex) {
                Logger.Debug($"GetSlewRateCapability failed: {ex.Message}");
            }

            // 3. Driver exposes no rate control; motion runs at its fixed internal rate.
            return SlewRateCapability.None();
        }

        public void SetSlewRateIndex(int index) {
            var prop = GetSwitchProperty("TELESCOPE_SLEW_RATE");
            if (prop == null || prop.Switches.Count == 0) {
                Logger.Warning("SetSlewRateIndex: TELESCOPE_SLEW_RATE not available");
                return;
            }

            int clamped = Math.Max(0, Math.Min(index, prop.Switches.Count - 1));
            foreach (var sw in prop.Switches) {
                sw.Value = false;
            }
            prop.Switches[clamped].Value = true;
            INDIClient.Instance.SendProperty(prop);
            Logger.Debug($"SetSlewRateIndex: selected {clamped}/{prop.Switches.Count - 1} '{prop.Switches[clamped].Label}'");
        }

        public void SetSlewRateValue(double rateDps) {
            try {
                SetNumberValue("TELESCOPE_MOTION_RATE", "MOTION_RATE", Math.Abs(rateDps));
            } catch (Exception ex) {
                Logger.Warning($"SetSlewRateValue: TELESCOPE_MOTION_RATE not available ({ex.Message})");
            }
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
            } catch (ArgumentException ex) {
                throw new NotImplementedException(ex.Message, ex);
            }
        }

        public void MoveAxisDirection(TelescopeAxes axis, int sign) {
            // Direction-only motion for manual slew. Unlike MoveAxis this never touches the
            // slew rate — the rate is selected separately via SetSlewRateIndex/SetSlewRateValue
            // (the capability model). This keeps a user's discrete rate choice (e.g. "Guide")
            // from being clobbered on every keepalive message.
            try {
                if (sign != 0) {
                    atHome = false;
                }

                string property = axis == TelescopeAxes.Primary ? "TELESCOPE_MOTION_WE" : "TELESCOPE_MOTION_NS";
                var prop = GetSwitchProperty(property);
                if (prop == null) {
                    return;
                }

                if (sign == 0) {
                    // Stop this axis — only send if it was actually moving.
                    if (prop.Switches.Any(sw => sw.Value)) {
                        foreach (var sw in prop.Switches) {
                            sw.Value = false;
                        }
                        INDIClient.Instance.SendProperty(prop);
                    }
                    return;
                }

                // Primary: positive=East, negative=West. Secondary: positive=North, negative=South.
                string target = axis == TelescopeAxes.Primary
                    ? (sign > 0 ? "MOTION_EAST" : "MOTION_WEST")
                    : (sign > 0 ? "MOTION_NORTH" : "MOTION_SOUTH");
                foreach (var sw in prop.Switches) {
                    sw.Value = (sw.Name == target);
                }
                INDIClient.Instance.SendProperty(prop);
            } catch (ArgumentException ex) {
                throw new NotImplementedException(ex.Message, ex);
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
            } catch (ArgumentException ex) {
                throw new NotImplementedException(ex.Message, ex);
            }
        }

        // Ceiling for the park/unpark/home poll loops below. They are cleared only by driver
        // updates, so a mount that stops responding mid-motion would otherwise leave the
        // caller polling forever with only its own cancellation token as a way out — the
        // same hardening the dome/focuser/rotator/filter-wheel MoveTimeout fixes applied.
        private static readonly TimeSpan MotionTimeout = TimeSpan.FromMinutes(5);

        public async Task ParkAsync(CancellationToken ct = default) {
            // Preserve the NotImplementedException contract for mounts without park support —
            // the equipment layer uses it to clear its CanPark flag.
            if (GetSwitchProperty("TELESCOPE_PARK") == null) {
                throw new NotImplementedException("TELESCOPE_PARK property not found");
            }

            _parkInProgress = true;
            try {
                await ParkCoreAsync(ct);
            } finally {
                _parkInProgress = false;
            }
        }

        private async Task ParkCoreAsync(CancellationToken ct) {
            var preSendTimestamp = GetProperty("TELESCOPE_PARK")?.Timestamp ?? string.Empty;
            var lastPos = SampleRaDec();

            // Acknowledged send (mirrors INDIDome.Park): the previous fire-and-forget write
            // plus a fixed 100ms grace could return before a slow mount even flipped the
            // property to Busy, reporting "parked" while the mount was still moving.
            if (!await SetSwitchValueAsync("TELESCOPE_PARK", "PARK", true, TimeSpan.FromSeconds(10))) {
                Logger.Warning($"[{DeviceName}] TELESCOPE_PARK was not acknowledged — polling for park completion anyway");
            }

            // Wait (bounded) for the park motion to START. Buggy drivers can publish
            // TELESCOPE_PARK as Ok with PARK=On the instant the command is accepted while
            // the mount is still slewing to its park position (lx200_OnStep's Park()
            // forgets TrackState=SCOPE_PARKING; the INDI tree is off-limits for local
            // patches, so this is handled entirely client-side). Without this phase
            // the completion wait below exits before the motion ever becomes visible.
            // A mount already at the park position legitimately never moves — that case
            // falls through after the timeout. A timestamp-fresh Alert means the driver
            // reported the park attempt as FAILED.
            var motionStartTimeout = TimeSpan.FromSeconds(10);
            var started = DateTime.UtcNow;
            var parkProp = GetProperty("TELESCOPE_PARK");
            while (!ct.IsCancellationRequested) {
                parkProp = GetProperty("TELESCOPE_PARK");
                if (parkProp?.State == PropertyState.Alert && parkProp.Timestamp != preSendTimestamp) {
                    Logger.Error($"[{DeviceName}] Mount reported the park attempt as failed (TELESCOPE_PARK Alert)");
                    throw new InvalidOperationException("Mount reported the park attempt as failed");
                }
                var pos = SampleRaDec();
                if (Slewing || parkProp?.State == PropertyState.Busy || PositionMoved(lastPos, pos)) {
                    break;
                }
                lastPos = pos;
                if (DateTime.UtcNow - started > motionStartTimeout) {
                    Logger.Warning($"[{DeviceName}] Mount did not start park motion within {motionStartTimeout.TotalSeconds:F0}s — assuming it was already at the park position");
                    break;
                }
                await Task.Delay(500, ct);
            }

            // Completion: motion has to stop (Slewing includes coordinate motion with its
            // ~3s decay window, which doubles as the settle) and TELESCOPE_PARK must leave
            // Busy on drivers that manage it properly.
            started = DateTime.UtcNow;
            while ((Slewing == true || parkProp?.State == PropertyState.Busy) && !ct.IsCancellationRequested) {
                if (DateTime.UtcNow - started > MotionTimeout) {
                    Logger.Warning($"[{DeviceName}] Mount did not report park completion within {MotionTimeout.TotalSeconds:F0}s — giving up waiting");
                    break;
                }
                await Task.Delay(200, ct);
                parkProp = GetProperty("TELESCOPE_PARK");
            }

            atHome = false;
        }

        public async Task UnparkAsync(CancellationToken ct = default) {
            if (GetSwitchProperty("TELESCOPE_PARK") == null) {
                throw new NotImplementedException("TELESCOPE_PARK property not found");
            }

            // The ack can be a FALSE Alert: OnStep's UnPark() reads a single-char reply to
            // :hR# and reports failure on any serial hiccup even though the controller does
            // unpark — the driver's status poll then reconciles via SetParked(false) a
            // moment later. So a rejected/missing ack is only a warning here; the real
            // outcome is read from the PARK switch below.
            if (!await SetSwitchValueAsync("TELESCOPE_PARK", "UNPARK", true, TimeSpan.FromSeconds(10))) {
                Logger.Warning($"[{DeviceName}] TELESCOPE_PARK unpark was not acknowledged — waiting for the unparked state anyway");
            }

            // Outcome wait: unparked = PARK switch off and no operation in flight. Reading
            // the switch is sound in the false-Alert case because the driver's Alert update
            // restores PARK=On in the cache (overwriting our optimistic UNPARK write) and
            // the eventual SetParked(false) flips it off with state Ok. Returning before
            // the mount is really unparked would make follow-up commands (the VM sends
            // TRACK_OFF right after) bounce off a still-parked driver.
            var unparkTimeout = TimeSpan.FromSeconds(60);
            var started = DateTime.UtcNow;
            var parkProp = GetProperty("TELESCOPE_PARK");
            while ((AtPark || parkProp?.State == PropertyState.Busy) && !ct.IsCancellationRequested) {
                if (DateTime.UtcNow - started > unparkTimeout) {
                    Logger.Error($"[{DeviceName}] Mount still reports parked {unparkTimeout.TotalSeconds:F0}s after the unpark command");
                    throw new InvalidOperationException("Mount did not unpark");
                }
                await Task.Delay(200, ct);
                parkProp = GetProperty("TELESCOPE_PARK");
            }
        }

        public void SetPark() {
            try {
                SetSwitchValue("TELESCOPE_PARK_OPTION", "PARK_CURRENT", true);
            } catch (ArgumentException ex) {
                throw new NotImplementedException(ex.Message, ex);
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
            } catch (ArgumentException ex) {
                throw new NotImplementedException(ex.Message, ex);
            } catch (Exception ex) {
                Logger.Error($"Error in SlewToCoordinates: {ex.Message}");
                throw;
            }
        }

        public async Task SlewToCoordinatesTaskAsync(double ra, double dec, CancellationToken ct = default) {
            const int maxSlewAttempts = 2;
            try {
                for (int attempt = 1; ; attempt++) {
                    try {
                        await SlewToCoordinates(ra, dec);
                        break;
                    } catch (InvalidOperationException) when (attempt < maxSlewAttempts && !ct.IsCancellationRequested && !AtPark) {
                        // Mounts like OnStep transiently reject a goto issued while their state is
                        // still settling (right after connect / a tracking-enable) as "below the
                        // horizon limit". A single retry after a short wait clears it; a genuinely
                        // unreachable target still fails on the second attempt.
                        Logger.Warning($"Slew rejected on attempt {attempt}/{maxSlewAttempts}; retrying after settle");
                        await Task.Delay(1000, ct);
                    }
                }

                // Watch EQUATORIAL_EOD_COORD.State directly rather than the composite
                // Slewing flag so that AltAz tracking motion cannot interfere. Bounded by
                // MotionTimeout like every other completion wait — a driver that stops
                // replying mid-slew must not park the caller forever on its own token.
                var started = DateTime.UtcNow;
                while (!ct.IsCancellationRequested) {
                    var coordState = GetProperty("EQUATORIAL_EOD_COORD")?.State;
                    if (coordState == PropertyState.Alert) {
                        Logger.Error("EQUATORIAL_EOD_COORD in Alert state - slew rejected by mount");
                        throw new InvalidOperationException("Slew rejected by mount - check mount limits and target accessibility");
                    }
                    if (coordState != PropertyState.Busy) {
                        break;
                    }
                    if (DateTime.UtcNow - started > MotionTimeout) {
                        Logger.Warning($"[{DeviceName}] Mount did not report slew completion within {MotionTimeout.TotalSeconds:F0}s — giving up waiting");
                        break;
                    }

                    await Task.Delay(500, ct);
                }
            } catch (ArgumentException ex) {
                throw new NotImplementedException(ex.Message, ex);
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
            } catch (ArgumentException ex) {
                throw new NotImplementedException(ex.Message, ex);
            } catch (Exception ex) {
                Logger.Error($"Error in SlewToCoordinates: {ex.Message}");
                throw;
            }
        }

        public async Task SlewToAltAzTaskAsync(double azimuth, double altitude, CancellationToken ct = default) {
            try {
                // Captured BEFORE the send: the busy-start loop below must distinguish a
                // fresh driver response from state left over by an earlier operation (same
                // stale-guard idiom as the async-ack machinery). INDI timestamps have
                // 1-second resolution, so a same-second response can still read as stale —
                // those cases fall back to the bounded timeout paths instead of misfiring.
                var preSendTimestamp = GetProperty("HORIZONTAL_COORD")?.Timestamp;

                SlewToAltAz(azimuth, altitude);

                // Wait (bounded) for the driver to flip HORIZONTAL_COORD to Busy.
                // SlewToAltAz is a fire-and-forget send (unlike the equatorial path, which
                // awaits the Busy ack), so a fixed grace period would let a driver that takes
                // longer than it to start reporting motion fall straight through the
                // completion loop below — returning while the mount is still moving (same
                // shape as the FindHomeAsync fix).
                var busyStartTimeout = TimeSpan.FromSeconds(10);
                var started = DateTime.UtcNow;
                var coordProp = GetProperty("HORIZONTAL_COORD");
                while (!ct.IsCancellationRequested) {
                    var state = coordProp?.State;
                    var fresh = coordProp?.Timestamp != preSendTimestamp;
                    if (state == PropertyState.Busy) {
                        break;
                    }
                    // Only a FRESH Alert is a rejection of THIS slew (the completion loop
                    // throws on it). A stale Alert left over from an earlier failed operation
                    // must be waited out here — reading it in the first ~200ms would reject
                    // the slew before the driver even processed the command.
                    if (state == PropertyState.Alert && fresh) {
                        break;
                    }
                    // A fresh Ok means the goto completed immediately (already at target) —
                    // exit now instead of paying the full fallthrough timeout.
                    if (state == PropertyState.Ok && fresh) {
                        break;
                    }
                    if (DateTime.UtcNow - started > busyStartTimeout) {
                        Logger.Warning($"[{DeviceName}] HORIZONTAL_COORD did not become Busy within {busyStartTimeout.TotalSeconds:F0}s of the slew command — assuming the mount was already at the target");
                        break;
                    }
                    await Task.Delay(200, ct);
                    coordProp = GetProperty("HORIZONTAL_COORD");
                }

                // Watch HORIZONTAL_COORD.State directly. Slewing no longer includes
                // HORIZONTAL_COORD.Busy (removed to fix AltAz tracking false-positive),
                // so we cannot rely on the composite Slewing flag here.
                // A completed goto reports Ok, not Idle, so we exit on any non-Busy state.
                // Bounded by MotionTimeout: an AltAz mount in tracking mode holds
                // HORIZONTAL_COORD Busy indefinitely (see the Slewing property comment), so
                // without a ceiling this wait could only ever exit via cancellation there.
                started = DateTime.UtcNow;
                while (!ct.IsCancellationRequested) {
                    var state = coordProp?.State;
                    if (state == PropertyState.Alert) {
                        Logger.Error("HORIZONTAL_COORD in Alert state - slew rejected by mount");
                        throw new InvalidOperationException("Slew rejected by mount - check mount limits and target accessibility");
                    }
                    if (state != PropertyState.Busy) {
                        break;
                    }
                    if (DateTime.UtcNow - started > MotionTimeout) {
                        Logger.Warning($"[{DeviceName}] Mount did not report AltAz slew completion within {MotionTimeout.TotalSeconds:F0}s — giving up waiting");
                        break;
                    }

                    await Task.Delay(500, ct);
                    coordProp = GetProperty("HORIZONTAL_COORD");
                }
            } catch (ArgumentException ex) {
                throw new NotImplementedException(ex.Message, ex);
            } catch (Exception ex) {
                Logger.Error($"Error in SlewToAltAzTaskAsync: {ex.Message}");
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

                // A sync makes the reported coordinates JUMP without any physical motion —
                // keep the coordinate-motion detector from reading that jump as a slew
                // (a platesolve sync would otherwise flag Slewing for a few seconds).
                _suppressCoordMotionUntil = DateTime.UtcNow.AddSeconds(2);

                // Enable sync mode
                SetSwitchValue("ON_COORD_SET", "SYNC", true);

                // Send coordinates
                SetNumberValues("EQUATORIAL_EOD_COORD", ("RA", ra), ("DEC", dec));
            } catch (ArgumentException ex) {
                throw new NotImplementedException(ex.Message, ex);
            } catch (Exception ex) {
                Logger.Error($"Error in SlewToCoordinates: {ex.Message}");
                throw;
            }
        }

        // Motion detection via the mount's actual position, for operations (homing) where
        // firmwares don't reliably report a slew state but the driver still polls and
        // pushes EQUATORIAL_EOD_COORD values every cycle.
        private (double Ra, double Dec)? SampleRaDec() {
            var ra = GetNumberPropertyValue("EQUATORIAL_EOD_COORD", "RA");
            var dec = GetNumberPropertyValue("EQUATORIAL_EOD_COORD", "DEC");
            if (!ra.HasValue || !dec.HasValue) {
                return null;
            }
            return (ra.Value, dec.Value);
        }

        // True when the position changed meaningfully between two samples. The 0.02°
        // threshold sits far below goto-rate motion (≥ ~0.5° per 500ms sample) but well
        // above the apparent RA drift of a stopped, non-tracking mount (~0.002° per
        // 500ms sample), so neither tracking state produces false positives.
        private static bool PositionMoved((double Ra, double Dec)? a, (double Ra, double Dec)? b) {
            if (a == null || b == null) {
                return false;
            }
            var dRaHours = Math.Abs(b.Value.Ra - a.Value.Ra);
            if (dRaHours > 12) {
                dRaHours = 24 - dRaHours; // RA wrap-around
            }
            var dRaDeg = dRaHours * 15.0;
            var dDecDeg = Math.Abs(b.Value.Dec - a.Value.Dec);
            return dRaDeg > 0.02 || dDecDeg > 0.02;
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
                var preSendTimestamp = homeProp.Timestamp;
                var lastPos = SampleRaDec();
                SetSwitchValue("TELESCOPE_HOME", homeSwitch.Name, true);

                // Wait (bounded) for the homing motion to START — via the composite Slewing
                // flag OR the mount position actually changing. The position check matters:
                // some firmwares (observed on OnStep) home without ever reporting a slew
                // state on any INDI property, so state-based detection alone concludes
                // "already at home" while the mount is in fact moving. A FRESH Alert on
                // TELESCOPE_HOME means the driver refused the command (libindi refuses
                // homing while parked) — surface that as an error instead of claiming home.
                var motionStartTimeout = TimeSpan.FromSeconds(10);
                var started = DateTime.UtcNow;
                while (!ct.IsCancellationRequested) {
                    homeProp = GetSwitchProperty("TELESCOPE_HOME");
                    if (homeProp?.State == PropertyState.Alert && homeProp.Timestamp != preSendTimestamp) {
                        Logger.Error($"[{DeviceName}] Mount refused the home command (TELESCOPE_HOME reported Alert)");
                        throw new InvalidOperationException("Mount refused the home command — is it parked?");
                    }
                    var pos = SampleRaDec();
                    if (Slewing || PositionMoved(lastPos, pos)) {
                        break;
                    }
                    lastPos = pos;
                    if (DateTime.UtcNow - started > motionStartTimeout) {
                        Logger.Warning($"[{DeviceName}] Mount did not start moving within {motionStartTimeout.TotalSeconds:F0}s of the home command — assuming it was already at home");
                        break;
                    }
                    await Task.Delay(500, ct);
                }

                // Completion is gated on MOTION only (Slewing flag + position settling), NOT
                // on the TELESCOPE_HOME property's own state. Many mounts never manage
                // TELESCOPE_HOME correctly while homing — libindi's framework sets it Busy on
                // accept and no driver we've seen ever completes it — so keying off
                // homeProp.State would hang. This was changed deliberately (commit d2954ec70,
                // "onstep homing"); do not re-add a TELESCOPE_HOME-state condition here.
                // "Home reached" = not Slewing AND position stable for a few samples (the
                // stability window also absorbs the settle right after motion stops).
                started = DateTime.UtcNow;
                var stableSamples = 0;
                lastPos = SampleRaDec();
                while (!ct.IsCancellationRequested) {
                    await Task.Delay(500, ct);
                    var pos = SampleRaDec();
                    if (!Slewing && !PositionMoved(lastPos, pos)) {
                        // ~2s of confirmed standstill
                        if (++stableSamples >= 4) {
                            break;
                        }
                    } else {
                        stableSamples = 0;
                        Logger.Debug($"Waiting to reach home...");
                    }
                    lastPos = pos;
                    if (DateTime.UtcNow - started > MotionTimeout) {
                        // Gave up waiting — the mount may still be moving; do NOT claim home.
                        Logger.Warning($"[{DeviceName}] Mount did not report homing completion within {MotionTimeout.TotalSeconds:F0}s — giving up waiting");
                        return;
                    }
                }

                // A cancelled wait is not a completed homing run either.
                if (ct.IsCancellationRequested) return;

                Logger.Debug($"Reached home");
                atHome = true;
            } catch (ArgumentException ex) {
                throw new NotImplementedException(ex.Message, ex);
            }
        }

        public bool CanMoveAxis(TelescopeAxes axis) {
            switch (axis) {
                case TelescopeAxes.Primary:
                    return GetProperty("TELESCOPE_MOTION_WE") != null;
                case TelescopeAxes.Secondary:
                    return GetProperty("TELESCOPE_MOTION_NS") != null;
                default:
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