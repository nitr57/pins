#region "copyright"

/*
    Copyright © 2025-2026 Nico Trost <nico.trost57@gmail.com> and the PI.N.S. contributors

    This file is part of PI 'N' Stars.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Newtonsoft.Json;
using NINA.Core.Model;
using NINA.Sequencer.Validations;
using NINA.Astrometry;
using NINA.Equipment.Interfaces.Mediator;
using System;
using System.Collections.Generic;
using System.Linq;
using System.ComponentModel.Composition;
using System.Threading;
using System.Threading.Tasks;
using NINA.Core.Locale;
using NINA.Core.Utility;
using NINA.Core.Utility.Notification;
using NINA.Equipment.Interfaces;
using NINA.Profile.Interfaces;
using System.Runtime.ExceptionServices;

namespace NINA.Sequencer.SequenceItem.Telescope {

    /// <summary>
    /// Which side of the meridian to point at for guider calibration.
    /// East/West refer to the pointing direction (sky side), matching PHD2's Calibration Assistant terminology.
    /// </summary>
    public enum CalibrationPointingSide {
        /// <summary>Stay on the same side as the current pointing direction.</summary>
        Stay = 0,
        /// <summary>Point east of the meridian (hour angle negative).</summary>
        East = 1,
        /// <summary>Point west of the meridian (hour angle positive).</summary>
        West = 2,
    }

    [ExportMetadata("Name", "Lbl_SequenceItem_Telescope_SlewToGuiderCalibrationPosition_Name")]
    [ExportMetadata("Description", "Lbl_SequenceItem_Telescope_SlewToGuiderCalibrationPosition_Description")]
    [ExportMetadata("Icon", "SlewToRaDecSVG")]
    [ExportMetadata("Category", "Lbl_SequenceCategory_Telescope")]
    [Export(typeof(ISequenceItem))]
    [JsonObject(MemberSerialization.OptIn)]
    public class SlewToGuiderCalibrationPosition : SequenceItem, IValidatable {
        /// <summary>Lowest altitude of the calibration position (and of the backlash-clearing point below it).</summary>
        public const double MinAltitudeDegrees = 10.0;

        private readonly ITelescopeMediator telescopeMediator;
        private readonly IGuiderMediator guiderMediator;
        private readonly IProfileService profileService;

        [ImportingConstructor]
        public SlewToGuiderCalibrationPosition(ITelescopeMediator telescopeMediator, IGuiderMediator guiderMediator, IProfileService profileService) {
            this.telescopeMediator = telescopeMediator;
            this.guiderMediator = guiderMediator;
            this.profileService = profileService;
        }

        private SlewToGuiderCalibrationPosition(SlewToGuiderCalibrationPosition cloneMe) : this(cloneMe.telescopeMediator, cloneMe.guiderMediator, cloneMe.profileService) {
            CopyMetaData(cloneMe);
        }

        public override object Clone() {
            return new SlewToGuiderCalibrationPosition(this) {
                HaOffsetDegrees = HaOffsetDegrees,
                TargetDec = TargetDec,
                PointingSide = PointingSide,
                ClearBacklash = ClearBacklash,
            };
        }

        /// <summary>Meridian offset in degrees. Default 5° matches PHD2's Calibration Assistant default.</summary>
        [JsonProperty]
        public double HaOffsetDegrees { get; set; } = 5.0;

        /// <summary>Target declination in degrees. Default 0° (celestial equator) is optimal for most sites.</summary>
        [JsonProperty]
        public double TargetDec { get; set; } = 0.0;

        /// <summary>Which side of the meridian to point at. Stay matches the current pointing direction.</summary>
        [JsonProperty]
        public CalibrationPointingSide PointingSide { get; set; } = CalibrationPointingSide.Stay;

        /// <summary>
        /// When enabled, slews 1° south of the target Dec first, then north to the target Dec,
        /// ensuring the final approach is northward and Dec backlash is pre-cleared.
        /// Mirrors PHD2 Calibration Assistant behaviour.
        /// </summary>
        [JsonProperty]
        public bool ClearBacklash { get; set; } = true;

        private IList<string> issues = new List<string>();

        public IList<string> Issues {
            get => issues;
            set {
                issues = value;
                RaisePropertyChanged();
            }
        }

        public override async Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token) {
            var info = telescopeMediator.GetInfo();

            if (!info.Connected) {
                throw new SequenceEntityFailedException(Loc.Instance["LblTelescopeNotConnected"]);
            }

            if (info.AtPark) {
                Notification.ShowError(Loc.Instance["LblTelescopeParkedWarning"]);
                throw new SequenceEntityFailedException(Loc.Instance["LblTelescopeParkedWarning"]);
            }

            // INDI reports -1 for an unknown sidereal time (only NaN was refused, and the slew went to a wrong RA): outside
            // 0..24 h it is computed from the mount's site longitude, as the internal guider does
            double lst = info.SiderealTime is >= 0 and < 24
                ? info.SiderealTime
                : double.IsFinite(info.SiteLongitude) ? AstroUtil.GetLocalSiderealTimeNow(info.SiteLongitude) : double.NaN;
            if (double.IsNaN(lst)) {
                // the mount has not reported its sidereal time or site yet, e.g. right after connecting
                throw new SequenceEntityFailedException("The mount's sidereal time is not known yet");
            }

            bool pointEast;

            if (PointingSide == CalibrationPointingSide.East) {
                pointEast = true;
            } else if (PointingSide == CalibrationPointingSide.West) {
                pointEast = false;
            } else {
                // Stay: determine current side from hour angle; negative HA = pointing east.
                double hourAngle = AstroUtil.EuclidianModulus(lst - info.RightAscension + 12.0, 24.0) - 12.0;
                pointEast = hourAngle <= 0.0;
            }

            // Pointing east (HA < 0): RA = LST + offset/15
            // Pointing west (HA > 0): RA = LST - offset/15
            double targetRa = pointEast
                ? AstroUtil.EuclidianModulus(lst + HaOffsetDegrees / 15.0, 24.0)
                : AstroUtil.EuclidianModulus(lst - HaOffsetDegrees / 15.0, 24.0);

            // the RA comes from the local sidereal time, so it is of the current epoch
            var coordinates = new Coordinates(targetRa, TargetDec, Epoch.JNOW, Coordinates.RAType.Hours);

            Logger.Info($"SlewToGuiderCalibrationPosition: LST={lst:F4}h pointEast={pointEast} → RA={targetRa:F4}h Dec={TargetDec:F1}° ClearBacklash={ClearBacklash}");

            if (AltitudeProblem(info.SiteLatitude, pointEast) is { } altitudeProblem) {
                throw new SequenceEntityFailedException(altitudeProblem);
            }

            // only guiding is resumed: StopGuiding is also true for a guider that was merely looping
            bool wasGuiding = !GuiderWasOnlyLooping();
            var stoppedGuiding = await guiderMediator.StopGuiding(token);
            ExceptionDispatchInfo slewError = null;
            try {
                if (ClearBacklash) {
                    // Pre-clear Dec backlash: slew 1° south of target, then north to target so the
                    // final approach is always northward. Matches PHD2 Calibration Assistant behaviour.
                    var southCoordinates = new Coordinates(targetRa, TargetDec - 1.0, Epoch.JNOW, Coordinates.RAType.Hours);
                    if (!await telescopeMediator.SlewToCoordinatesAsync(southCoordinates, token)) {
                        throw new SequenceEntityFailedException("The slew south of the calibration position failed");
                    }
                }

                if (!await telescopeMediator.SlewToCoordinatesAsync(coordinates, token)) {
                    throw new SequenceEntityFailedException("The slew to the calibration position failed");
                }
            } catch (Exception e) {
                slewError = ExceptionDispatchInfo.Capture(e);
            }

            // A failed slew must not leave the following items without guiding; a cancelled one stops them anyway. The
            // result of the restart was ignored: the following items exposed unguided without anyone noticing.
            if (stoppedGuiding && wasGuiding && !token.IsCancellationRequested) {
                bool restarted;
                try {
                    restarted = await guiderMediator.StartGuiding(false, progress, token);
                } catch (Exception e) when (slewError is not null) {
                    // the slew's error is the one to report
                    Logger.Error($"SlewToGuiderCalibrationPosition: guiding could not be restarted after the failed slew: {e.Message}");
                    restarted = true;
                }

                if (!restarted) {
                    if (slewError is null) {
                        throw new SequenceEntityFailedException("Guiding could not be restarted after the slew to the calibration position");
                    }

                    Logger.Error("SlewToGuiderCalibrationPosition: guiding could not be restarted after the failed slew");
                }
            }

            slewError?.Throw();
        }

        // States of a guider that is not guiding: PHD2's app states (the internal guider reports the same names) and
        // SkyGuard's "idle" and "looping", whose StopGuiding succeeds also then. Other guiders (MetaGuide) name their
        // states differently and are resumed whenever StopGuiding stopped them.
        private static readonly string[] NotGuidingStates = ["Looping", "Selected", "Stopped", "Idle"];

        private bool GuiderWasOnlyLooping() {
            var state = (guiderMediator.GetDevice() as IGuider)?.State;
            return state is not null && NotGuidingStates.Any(s => string.Equals(s, state, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Why the calibration position (and the backlash-clearing point 1° below it) cannot be reached safely: below
        /// <see cref="MinAltitudeDegrees"/> or the profile's custom horizon; null when it can, or when the site is unknown.
        /// The position is at a fixed hour angle, so its altitude does not depend on the time. <paramref name="pointEast"/>
        /// null (Stay, the side is known only when the item runs) reports a problem only when neither side can be reached.
        /// </summary>
        private string AltitudeProblem(double latitude, bool? pointEast) {
            if (!double.IsFinite(latitude)) {
                return null;
            }

            if (pointEast is { } east) {
                return SideAltitudeProblem(latitude, east);
            }

            var eastProblem = SideAltitudeProblem(latitude, true);
            var westProblem = SideAltitudeProblem(latitude, false);

            return eastProblem is not null && westProblem is not null ? $"{eastProblem}; {westProblem}" : null;
        }

        private string SideAltitudeProblem(double latitude, bool east) {
            var horizon = profileService?.ActiveProfile?.AstrometrySettings?.Horizon;
            var declinations = ClearBacklash ? new[] { TargetDec, TargetDec - 1.0 } : new[] { TargetDec };

            // east of the meridian the hour angle is negative
            double hourAngle = east ? -HaOffsetDegrees : HaOffsetDegrees;
            foreach (double dec in declinations) {
                double altitude = AstroUtil.GetAltitude(hourAngle, latitude, dec);
                double limit = MinAltitudeDegrees;
                if (horizon is not null) {
                    limit = Math.Max(limit, horizon.GetAltitude(AstroUtil.GetAzimuth(hourAngle, altitude, latitude, dec)));
                }

                if (altitude < limit) {
                    return $"The calibration position {(east ? "east" : "west")} of the meridian at Dec {dec:F1}° is {altitude:F0}° high, below the limit of {limit:F0}° (horizon or {MinAltitudeDegrees:F0}°)";
                }
            }

            return null;
        }

        public bool Validate() {
            var i = new List<string>();
            var info = telescopeMediator.GetInfo();
            if (info?.Connected != true) {
                i.Add(Loc.Instance["LblTelescopeNotConnected"]);
            }

            if (TargetDec < -90 || TargetDec > 90 || (ClearBacklash && TargetDec - 1.0 < -90)) {
                i.Add("The calibration declination must lie between -90° and 90°, and with backlash clearing 1° above -90°");
            }

            if (HaOffsetDegrees < 0 || HaOffsetDegrees > 90) {
                i.Add("The meridian offset must lie between 0° and 90°");
            }

            // shown in the sequence before the run (the altitude does not depend on the time)
            if (i.Count == 0) {
                bool? side = PointingSide switch {
                    CalibrationPointingSide.East => true,
                    CalibrationPointingSide.West => false,
                    _ => null,
                };

                if (AltitudeProblem(info.SiteLatitude, side) is { } altitudeProblem) {
                    i.Add(altitudeProblem);
                }
            }

            Issues = i;
            return i.Count == 0;
        }

        public override void AfterParentChanged() {
            Validate();
        }

        public override string ToString() {
            return $"Category: {Category}, Item: {nameof(SlewToGuiderCalibrationPosition)}, HAOffset: {HaOffsetDegrees}°, Dec: {TargetDec}°, Side: {PointingSide}, ClearBacklash: {ClearBacklash}";
        }
    }
}
