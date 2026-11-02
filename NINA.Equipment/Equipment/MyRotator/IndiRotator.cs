#region "copyright"

/*
    Copyright © 2016 - 2025 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Core.Utility;
using NINA.Astrometry;
using System;
using System.Threading;
using System.Threading.Tasks;
using NINA.Core.Locale;
using NINA.Equipment.Interfaces;
using NINA.INDI.Interfaces;
using NINA.INDI;
using NINA.INDI.Devices;
using NINA.Profile.Interfaces;

namespace NINA.Equipment.Equipment.MyRotator {

    internal class IndiRotator : IndiDevice<IINDIRotator>, IRotator, IDisposable {

        public IndiRotator(INDIDeviceInfo info, IProfileService profileService) : base(info) {
            this.profileService = profileService;
        }

        private IProfileService profileService;

        public bool CanReverse => GetProperty(nameof(IINDIRotator.CanReverse), false);

        public bool Reverse {
            get {
                if (CanReverse) {
                    return GetProperty(nameof(IINDIRotator.Reverse), false);
                }
                return false;
            }
            set {
                if (CanReverse) {
                    SetProperty(nameof(IINDIRotator.Reverse), value);
                }
            }
        }

        public bool IsMoving => GetProperty(nameof(IINDIRotator.IsMoving), false);

        private bool synced;

        public bool Synced {
            get => synced;
            private set {
                synced = value;
                RaisePropertyChanged();
            }
        }

        private float offset = 0;

        public float Position => AstroUtil.EuclidianModulus(MechanicalPosition + offset, 360);

        public float MechanicalPosition => GetProperty(nameof(IINDIRotator.MechanicalPosition), float.NaN);

        public float StepSize => GetProperty(nameof(IINDIRotator.StepSize), float.NaN);

        protected override string ConnectionLostMessage => Loc.Instance["LblRotatorConnectionLost"];

        public void Halt() {
            if (IsMoving) {
                device?.Halt();
            }
        }

        // The position a sync or relative move starts from. An unreadable position (NaN, GetProperty retries already)
        // gave a NaN offset, so the rotator's sky angle stayed NaN until the next sync, or sent NaN as the move's target
        private float ReadMechanicalPositionForMove() {
            var position = MechanicalPosition;
            if (float.IsNaN(position)) {
                throw new InvalidOperationException("Could not read the position of the INDI rotator");
            }

            return position;
        }

        public void Sync(float skyAngle) {
            var mechanicalPosition = ReadMechanicalPositionForMove();
            offset = skyAngle - mechanicalPosition;
            RaisePropertyChanged(nameof(Position));
            Synced = true;
            Logger.Debug($"INDI - Mechanical Position is {mechanicalPosition}� - Sync Position to Sky Angle {skyAngle}� using offset {offset}");
        }

        public async Task<bool> Move(float angle, CancellationToken ct) {
            if (ShouldBeConnected) {
                if (angle >= 360) {
                    angle = AstroUtil.EuclidianModulus(angle, 360);
                }
                if (angle <= -360) {
                    angle = AstroUtil.EuclidianModulus(angle, -360);
                }

                var mechanicalPosition = ReadMechanicalPositionForMove();
                var targetMechanicalPosition = AstroUtil.EuclidianModulus(mechanicalPosition + angle, 360);
                Logger.Debug($"INDI - Move relative by {angle}� to mechanical position {targetMechanicalPosition}� - Mechanical Position reported by rotator {mechanicalPosition}� and offset {offset}");
                return await MoveAbsoluteMechanical(targetMechanicalPosition, ct);
            }
            return false;
        }

        public async Task<bool> MoveAbsoluteMechanical(float targetPosition, CancellationToken ct) {
            if (ShouldBeConnected) {
                // Unlike ASCOM, the INDI backend has no relative-move primitive: ABS_ROTATOR_ANGLE
                // is always an absolute target, so it is written directly here rather than via Move().
                Logger.Debug($"INDI - Move absolute mechanical to {targetPosition}�");
                await device.MoveAsync(targetPosition, ct);
                InvalidatePropertyCache();

                return true;
            }
            return false;
        }

        public async Task<bool> MoveAbsolute(float targetPosition, CancellationToken ct) {
            if (ShouldBeConnected) {
                var targetMechanicalPosition = AstroUtil.EuclidianModulus(targetPosition - offset, 360);
                Logger.Debug($"INDI - Move absolute sky angle to {targetPosition}� (mechanical {targetMechanicalPosition}�) using offset {offset}");
                return await MoveAbsoluteMechanical(targetMechanicalPosition, ct);
            }
            return false;
        }

        protected override Task PreConnect() {
            offset = 0;
            Synced = false;

            if (profileService != null) {
                var settings = profileService.ActiveProfile.RotatorSettings;
                var instance = GetInstance();
                instance.ConfigureConnectionProperties(
                    settings.IndiConnectionMode,
                    settings.IndiAutoSearch,
                    settings.IndiAddress,
                    settings.IndiPort,
                    settings.IndiBaudRate
                );
            }

            return Task.CompletedTask;
        }

        protected override IINDIRotator GetInstance() {
            return device ??= new INDIRotator(_device);
        }
    }
}
