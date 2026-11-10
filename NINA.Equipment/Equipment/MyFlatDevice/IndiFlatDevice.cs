#region "copyright"

/*
    Copyright © 2025-2026 Nico Trost <nico.trost57@gmail.com> and the PI.N.S. contributors

    This file is part of PI 'N' Stars.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.INDI;
using NINA.INDI.Devices;
using NINA.INDI.Interfaces;
using NINA.Core.Locale;
using NINA.Core.Utility;
using NINA.Equipment.Exceptions;
using NINA.Equipment.Interfaces;
using NINA.Profile.Interfaces;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Equipment.Equipment.MyFlatDevice {

    public class IndiFlatDevice : IndiDevice<IINDIFlatDevice>, IFlatDevice, IDisposable {

        public IndiFlatDevice(INDIDeviceInfo info, IProfileService profileService = null) : base(info) {
            this.profileService = profileService;
        }

        private IProfileService profileService;

        private int lastBrightness = 0;

        public CoverState CoverState {
            get {
                var state = GetProperty(nameof(CoverState), INDI.Enums.CoverState.Unknown);
                switch (state) {
                    case INDI.Enums.CoverState.Unknown:
                        return CoverState.Unknown;

                    case INDI.Enums.CoverState.NotPresent:
                        return CoverState.NotPresent;

                    case INDI.Enums.CoverState.NeitherOpenNorClosed:
                        return CoverState.NeitherOpenNorClosed;

                    case INDI.Enums.CoverState.Closed:
                        return CoverState.Closed;

                    case INDI.Enums.CoverState.Open:
                        return CoverState.Open;

                    case INDI.Enums.CoverState.Error:
                        return CoverState.Error;

                    default:
                        return CoverState.Unknown;
                }
            }
        }

        public int MaxBrightness { get; private set; }

        public int MinBrightness { get; private set; }

        // after a disconnect device is null until the next connect
        public bool LightOn {
            get => device?.LightOn ?? false;
            set {
                try {
                    if (SupportsOnOff) {
                        if (value && SupportsOpenClose && CoverState != CoverState.Closed) {
                            Logger.Warning("Cannot turn light on while cover is not closed");
                            return;
                        }
                        if (value) {
                            Logger.Debug("Switching cover calibrator on");
                            // switch the light on with the last saved value, if any
                            device.Brightness = (lastBrightness != 0) ? lastBrightness : MaxBrightness;
                            device.LightOn = true;
                        } else {
                            Logger.Debug("Switching cover calibrator off");
                            device.LightOn = false;
                        }
                    }
                } catch (Exception ex) {
                    Logger.Error(ex);
                }
            }
        }

        public int Brightness {
            get => device?.Brightness ?? 0;
            set {
                try {
                    if (SupportsOnOff) {
                        if (value < MinBrightness) {
                            value = MinBrightness;
                        }

                        if (value > MaxBrightness) {
                            value = MaxBrightness;
                        }
                        Logger.Debug($"Setting cover calibrator brightness to {value}");
                        device.Brightness = value;
                        lastBrightness = value; // save brightness for next time the user toggles the light on
                        device.LightOn = value != 0;
                    }
                } catch (Exception ex) {
                    Logger.Error(ex);
                }
            }
        }

        public string PortName { get => string.Empty; set { } }

        public bool SupportsOpenClose => device?.SupportsOpenClose ?? false;

        public bool SupportsOnOff => device?.SupportsOnOff ?? false;

        protected override string ConnectionLostMessage => Loc.Instance["LblFlatDeviceConnectionLost"];

        private void Initialize() {
            if (device.SupportsOnOff == false) {
                MinBrightness = 0;
                MaxBrightness = 0;
            } else {
                try {
                    MinBrightness = 0;
                    MaxBrightness = device.MaxBrightness;
                } catch {
                    MinBrightness = 0;
                    MaxBrightness = 0;
                }
            }
        }

        // The result comes from the INDI device: CoverState goes through GetProperty's cache and can predate the end of
        // the move. A cover the driver reported in error is moved anyway, since an INDI Alert stays until the next
        // command; a move the driver ends with Alert throws FlatDeviceCoverErrorException, which FlatDeviceVM reports.
        public async Task<bool> Open(CancellationToken ct, int delay = 300) {
            if (device is not { } dev || !dev.SupportsOpenClose) {
                return false;
            }

            // Turn off lights before opening
            LightOn = false;

            try {
                return await dev.Open(ct, delay);
            } catch (InvalidOperationException ex) {
                // FlatDeviceVM logs and shows only that the cover is in error
                Logger.Error(ex.Message);
                throw new FlatDeviceCoverErrorException(ex.Message, ex);
            }
        }

        public async Task<bool> Close(CancellationToken ct, int delay = 300) {
            if (device is not { } dev || !dev.SupportsOpenClose) {
                return false;
            }

            try {
                return await dev.Close(ct, delay);
            } catch (InvalidOperationException ex) {
                // FlatDeviceVM logs and shows only that the cover is in error
                Logger.Error(ex.Message);
                throw new FlatDeviceCoverErrorException(ex.Message, ex);
            }
        }

        protected override Task PreConnect() {
            lastBrightness = 0;

            if (profileService != null) {
                var settings = profileService.ActiveProfile.FlatDeviceSettings;
                var instance = GetInstance();
                instance.ConfigureConnectionProperties(
                    settings.IndiConnectionMode,
                    settings.IndiAutoSearch,
                    settings.IndiAddress,
                    settings.IndiPort,
                    settings.IndiBaudRate
                );
            }

            return base.PreConnect();
        }

        protected override Task PostConnect() {
            Initialize();
            return Task.CompletedTask;
        }

        protected override IINDIFlatDevice GetInstance() {
            return device ??= new INDIFlatDevice(_device);
        }
    }
}