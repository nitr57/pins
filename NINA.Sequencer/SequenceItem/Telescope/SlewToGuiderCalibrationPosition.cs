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
using System.ComponentModel.Composition;
using System.Threading;
using System.Threading.Tasks;
using NINA.Core.Locale;
using NINA.Core.Utility;
using NINA.Core.Utility.Notification;

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
        private readonly ITelescopeMediator telescopeMediator;
        private readonly IGuiderMediator guiderMediator;

        [ImportingConstructor]
        public SlewToGuiderCalibrationPosition(ITelescopeMediator telescopeMediator, IGuiderMediator guiderMediator) {
            this.telescopeMediator = telescopeMediator;
            this.guiderMediator = guiderMediator;
        }

        private SlewToGuiderCalibrationPosition(SlewToGuiderCalibrationPosition cloneMe) : this(cloneMe.telescopeMediator, cloneMe.guiderMediator) {
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

            if (info.AtPark) {
                Notification.ShowError(Loc.Instance["LblTelescopeParkedWarning"]);
                throw new SequenceEntityFailedException(Loc.Instance["LblTelescopeParkedWarning"]);
            }

            double lst = info.SiderealTime;
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

            var coordinates = new Coordinates(targetRa, TargetDec, Epoch.J2000, Coordinates.RAType.Hours);

            Logger.Info($"SlewToGuiderCalibrationPosition: LST={lst:F4}h pointEast={pointEast} → RA={targetRa:F4}h Dec={TargetDec:F1}° ClearBacklash={ClearBacklash}");

            var stoppedGuiding = await guiderMediator.StopGuiding(token);

            if (ClearBacklash) {
                // Pre-clear Dec backlash: slew 1° south of target, then north to target so the
                // final approach is always northward. Matches PHD2 Calibration Assistant behaviour.
                var southCoordinates = new Coordinates(targetRa, TargetDec - 1.0, Epoch.J2000, Coordinates.RAType.Hours);
                await telescopeMediator.SlewToCoordinatesAsync(southCoordinates, token);
            }

            await telescopeMediator.SlewToCoordinatesAsync(coordinates, token);
            if (stoppedGuiding) {
                await guiderMediator.StartGuiding(false, progress, token);
            }
        }

        public bool Validate() {
            var i = new List<string>();
            if (!telescopeMediator.GetInfo().Connected) {
                i.Add(Loc.Instance["LblTelescopeNotConnected"]);
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
