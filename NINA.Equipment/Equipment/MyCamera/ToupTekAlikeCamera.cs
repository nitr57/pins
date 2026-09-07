#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Altair;
using NINA.Core.Enum;
using NINA.Core.Locale;
using NINA.Core.Model.Equipment;
using NINA.Core.Utility;
using NINA.Core.Utility.Notification;
using NINA.Equipment.Equipment.MyCamera.ToupTekAlike;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Model;
using NINA.Equipment.Utility;
using NINA.Image.ImageAnalysis;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;
using NINA.Profile.Interfaces;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Equipment.Equipment.MyCamera {

    public partial class ToupTekAlikeCamera : BaseINPC, ICamera {
        private ToupTekAlikeFlag flags;
        private IToupTekAlikeCameraSDK sdk;
        private string internalId;

        public ToupTekAlikeCamera(ToupTekAlikeDeviceInfo deviceInfo, IToupTekAlikeCameraSDK sdk, IProfileService profileService, IExposureDataFactory exposureDataFactory) {
            Category = sdk.Category;

            this.profileService = profileService;
            this.exposureDataFactory = exposureDataFactory;
            this.sdk = sdk;
            this.internalId = deviceInfo.id;
            this.Id = Category + "_" + deviceInfo.id;

            this.Name = deviceInfo.displayname;


            var match = IdExtractorRegex().Match(deviceInfo.id);

            this.Description = $"{Category} camera.";
            if (match.Success) {
                var vid = match.Groups[1].Value;
                var pid = match.Groups[2].Value;
                var tail = match.Groups[3].Value;
                this.Description += $" Vendor ID: {vid}, Product ID: {pid}, Camera ID: {tail}";
            }
            
            this.MaxFanSpeed = (int)deviceInfo.model.maxfanspeed;
            this.PixelSizeX = Math.Round(deviceInfo.model.xpixsz, 2);
            this.PixelSizeY = Math.Round(deviceInfo.model.ypixsz, 2);

            this.flags = (ToupTekAlikeFlag)deviceInfo.model.flag;
        }

        [GeneratedRegex(@"vid_([0-9a-fA-F]+)&pid_([0-9a-fA-F]+)#([^\\]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
        private static partial Regex IdExtractorRegex();

        private IProfileService profileService;
        private readonly IExposureDataFactory exposureDataFactory;

        public string Category { get; }

        public bool HasShutter => false;

        public double Temperature {
            get {
                sdk.get_Temperature(out var temp);
                return temp / 10.0;
            }
        }

        public double TemperatureSetPoint {
            get {
                if (CanSetTemperature) {
                    sdk.get_Option(ToupTekAlikeOption.OPTION_TECTARGET, out var target);
                    return target / 10.0;
                } else {
                    return double.NaN;
                }
            }
            set {
                if (CanSetTemperature) {
                    if (!sdk.put_Option(ToupTekAlikeOption.OPTION_TECTARGET, (int)(value * 10))) {
                        Logger.Error($"{Category} - Could not set TemperatureSetPoint to {value * 10}");
                    } else {
                        RaisePropertyChanged();
                    }
                }
            }
        }

        public bool BinAverageEnabled {
            get => profileService.ActiveProfile.CameraSettings.BinAverageEnabled == true;
            set {
                if (profileService.ActiveProfile.CameraSettings.BinAverageEnabled != value) {
                    profileService.ActiveProfile.CameraSettings.BinAverageEnabled = value;
                    RaisePropertyChanged();
                    // Force binning mode to be set again
                    BinX = BinX;
                }
            }
        }

        public short BinX {
            get {
                sdk.get_Option(ToupTekAlikeOption.OPTION_BINNING, out var bin);
                return (short)(bin & 0x0F);
            }
            set {
                int maxBin = MaxBinX > 0 ? MaxBinX : 1;
                int binValue = Math.Max(1, Math.Min(maxBin, (int)value));
                if (binValue > 1 && BinAverageEnabled) {
                    binValue |= 0x80;
                }

                if (!sdk.put_Option(ToupTekAlikeOption.OPTION_BINNING, binValue)) {
                    Logger.Error($"{Category} - Could not set Binning to {binValue}");
                } else {
                    RaisePropertyChanged(nameof(BinX));
                    RaisePropertyChanged(nameof(BinY));
                }
            }
        }

        public short BinY {
            get => BinX;
            set => BinX = value;
        }

        public string SensorName => string.Empty;

        public SensorType SensorType { get; private set; }

        public short BayerOffsetX => 0;

        public short BayerOffsetY => 0;

        public int CameraXSize { get; private set; }

        public int CameraYSize { get; private set; }

        public double ExposureMin {
            get {
                sdk.get_ExpTimeRange(out var min, out var max, out var def);
                return min / 1000000.0;
            }
        }

        public double ExposureMax {
            get {
                sdk.get_ExpTimeRange(out var min, out var max, out var def);
                return max / 1000000.0;
            }
        }

        public IList<string> SupportedActions { get; } = new List<string>();

        public double ElectronsPerADU => double.NaN;

        public short MaxBinX { get; private set; }

        public short MaxBinY { get; private set; }

        public double PixelSizeX { get; }

        public double PixelSizeY { get; }

        public int MaxFanSpeed { get; }

        public int FanSpeed {
            get {
                sdk.get_Option(ToupTekAlikeOption.OPTION_FAN, out var fanSpeed);
                return fanSpeed;
            }
            set {
                var currentFanSpeed = FanSpeed;
                var targetFanSpeed = Math.Max(0, Math.Min(MaxFanSpeed, value));
                if (currentFanSpeed != targetFanSpeed) {
                    if (sdk.put_Option(ToupTekAlikeOption.OPTION_FAN, targetFanSpeed)) {
                        RaisePropertyChanged();
                    } else {
                        Logger.Error($"{Category} - Could not set Fan to {targetFanSpeed}");
                    }
                }
            }
        }

        private bool canGetTemperature;

        public bool CanGetTemperature {
            get => canGetTemperature;
            private set {
                canGetTemperature = value;
                RaisePropertyChanged();
            }
        }

        private bool canSetTemperature;

        public bool CanSetTemperature {
            get => canSetTemperature;
            private set {
                canSetTemperature = value;
                RaisePropertyChanged();
            }
        }

        public bool CoolerOn {
            get {
                sdk.get_Option(ToupTekAlikeOption.OPTION_TEC, out var cooler);
                return cooler == 1;
            }

            set {
                if (sdk.put_Option(ToupTekAlikeOption.OPTION_TEC, value ? 1 : 0)) {
                    if(value) {
                        // If fan is currently off, set it to its minimum speed
                        if (MaxFanSpeed > 0 && FanSpeed == 0) {
                            FanSpeed = 1;
                        }
                    } else {
                        // If turning the TEC off, turn the fan off too
                            FanSpeed = 0;
                    }
                    RaisePropertyChanged();
                    RaisePropertyChanged(nameof(FanSpeed));
                } else {
                    Logger.Error($"{Category} - Could not set Cooler to {value}");
                }
            }
        }

        private double coolerPower = 0.0;

        public double CoolerPower {
            get => coolerPower;
            private set {
                coolerPower = value;
                RaisePropertyChanged();
            }
        }

        private CancellationTokenSource coolerPowerReadoutCts;

        /// <summary>
        /// This task will update cooler power based on TEC Volatage readout every three seconds
        /// Due to the fact that this value must not be updated more than every two seconds according to the documentation
        /// a helper method is required in case the device polling interval is faster than that.
        /// </summary>
        private void CoolerPowerUpdateTask() {
            Task.Run(async () => {
                coolerPowerReadoutCts?.Dispose();
                coolerPowerReadoutCts = new CancellationTokenSource();
                try {
                    sdk.get_Option(ToupTekAlikeOption.OPTION_TEC_VOLTAGE_MAX, out var maxVoltage);
                    while (true) {
                        coolerPowerReadoutCts.Token.ThrowIfCancellationRequested();

                        sdk.get_Option(ToupTekAlikeOption.OPTION_TEC_VOLTAGE, out var voltage);

                        CoolerPower = 100 * voltage / (double)maxVoltage;

                        //Recommendation to not readout CoolerPower in less than two seconds.
                        await Task.Delay(TimeSpan.FromSeconds(3), coolerPowerReadoutCts.Token);
                    }
                } catch (OperationCanceledException) {
                }
            });
        }

        private bool hasDewHeater;

        public bool HasDewHeater {
            get => hasDewHeater;
            private set {
                hasDewHeater = value;
                RaisePropertyChanged();
            }
        }

        public int MaxDewHeaterStrength {
            get {
                if(HasDewHeater) {

                    sdk.get_Option(ToupTekAlikeOption.OPTION_HEAT_MAX, out var max);
                    return max;
                }
                return 0;
            }
        }

        public int TargetDewHeaterStrength {
            get => profileService.ActiveProfile.CameraSettings.TouptekAlikeDewHeaterStrength;
            set {
                var max = MaxDewHeaterStrength;
                if(value < 1) { value = 1; }
                if(value > max) { value = max; }
                profileService.ActiveProfile.CameraSettings.TouptekAlikeDewHeaterStrength = value;
                if (DewHeaterOn) {
                    sdk.put_Option(ToupTekAlikeOption.OPTION_HEAT, value);
                }
                RaisePropertyChanged();
            }
        }

        public bool DewHeaterOn {
            get {
                if (HasDewHeater) {
                    sdk.get_Option(ToupTekAlikeOption.OPTION_HEAT, out var heat);
                    return heat > 0;
                } else {
                    return false;
                }
            }
            set {
                if (HasDewHeater) {
                    if (value) {
                        sdk.put_Option(ToupTekAlikeOption.OPTION_HEAT, TargetDewHeaterStrength);
                    } else {
                        sdk.put_Option(ToupTekAlikeOption.OPTION_HEAT, 0);
                    }
                    RaisePropertyChanged();
                }
            }
        }

        public CameraStates CameraState => CameraStates.NoState;

        public bool CanSubSample => true;

        public bool EnableSubSample { get; set; }
        public int SubSampleX { get; set; }
        public int SubSampleY { get; set; }
        public int SubSampleWidth { get; set; }
        public int SubSampleHeight { get; set; }
        public bool CanShowLiveView => false;
        // Read on the SDK callback thread and the download thread, written by StartExposure/StartLiveView and
        // the StopLiveView continuation; keep it volatile like the other mode flags below
        private volatile bool liveViewEnabled;

        public bool LiveViewEnabled {
            get => liveViewEnabled;
            set => liveViewEnabled = value;
        }

        public bool HasBattery => false;

        public int BatteryLevel => -1;

        public int Offset {
            get {
                sdk.get_Option(ToupTekAlikeOption.OPTION_BLACKLEVEL, out var level);
                return level;
            }
            set {
                if (!sdk.put_Option(ToupTekAlikeOption.OPTION_BLACKLEVEL, value)) {
                    Logger.Error($"{Category} - Could not set Offset to {value}");
                } else {
                    RaisePropertyChanged();
                }
            }
        }

        public int OffsetMin => 0;

        public int OffsetMax => 31 * (1 << nativeBitDepth - 8);

        public int USBLimit {
            get {
                sdk.get_Speed(out var speed);
                return speed;
            }
            set {
                if (value >= USBLimitMin && value <= USBLimitMax) {
                    if (!sdk.put_Speed((ushort)value)) {
                        Logger.Error($"{Category} - Could not set USBLimit to {value}");
                    } else {
                        RaisePropertyChanged();
                    }
                }
            }
        }

        public int USBLimitMin => 0;

        public int USBLimitMax => (int)sdk.MaxSpeed;

        private bool canSetOffset;

        public bool CanSetOffset {
            get => canSetOffset;
            set {
                canSetOffset = value;
                RaisePropertyChanged();
            }
        }

        public bool CanSetUSBLimit => true;

        public bool CanGetGain => sdk.get_ExpoAGain(out var gain);

        public bool CanSetGain => GainMax != GainMin;

        public int GainMax {
            get {
                sdk.get_ExpoAGainRange(out var min, out var max, out var def);
                return max;
            }
        }

        public int GainMin {
            get {
                sdk.get_ExpoAGainRange(out var min, out var max, out var def);
                return min;
            }
        }

        public int Gain {
            get {
                sdk.get_ExpoAGain(out var gain);
                return gain;
            }

            set {
                if (value >= GainMin && value <= GainMax) {
                    if (!sdk.put_ExpoAGain((ushort)value)) {
                        Logger.Error($"{Category} - Could not set Gain to {value}");
                    } else {
                        RaisePropertyChanged();
                    }
                }
            }
        }

        public IList<string> ReadoutModes { get; private set; } = new List<string>();

        public short ReadoutMode {
            get {
                sdk.get_Option(ToupTekAlikeOption.OPTION_CG, out var value);
                Logger.Trace($"{Category} - Conversion Gain is set to {value}");
                return (short)value;            }
            set {
                Logger.Trace($"{Category} - Setting Conversion Gain to {value}");
                if (!sdk.put_Option(ToupTekAlikeOption.OPTION_CG, value)) {
                    Logger.Error($"{Category} - Could not set HighGainMode to {value}");
                } else {
                    RaisePropertyChanged();
                }
            }
        }


        private short _readoutModeForNormalImages = 0;
        public short ReadoutModeForNormalImages {
            get => _readoutModeForNormalImages;
            set {
                if (value >= 0 && value < ReadoutModes.Count) {
                    _readoutModeForNormalImages = value;
                } else {
                    _readoutModeForNormalImages = 0;
                }

                RaisePropertyChanged();
            }
        }

        private short _readoutModeForSnapImages = 0;
        public short ReadoutModeForSnapImages {
            get => _readoutModeForSnapImages;
            set {
                if (value >= 0 && value < ReadoutModes.Count) {
                    _readoutModeForSnapImages = value;
                } else {
                    _readoutModeForSnapImages = 0;
                }

                RaisePropertyChanged();
            }
        }

        public IList<int> Gains => new List<int>();

        private AsyncObservableCollection<BinningMode> binningModes;

        public AsyncObservableCollection<BinningMode> BinningModes {
            get {
                if (binningModes == null) {
                    binningModes = new AsyncObservableCollection<BinningMode>();
                }
                return binningModes;
            }
            private set {
                binningModes = value;
                RaisePropertyChanged();
            }
        }

        public bool HasSetupDialog => false;

        private string id;

        public string Id {
            get => id;
            set {
                id = value;
                RaisePropertyChanged();
            }
        }

        private string name;

        public string Name {
            get => name;
            set {
                name = value;
                RaisePropertyChanged();
            }
        }
        public string DisplayName => $"{Category} {Name} ({(Id.Length > 8 ? Id[^8..] : Id)})";

        private bool _connected;

        public bool Connected {
            get => _connected;
            set {
                _connected = value;
                if (!_connected) {
                    try { coolerPowerReadoutCts?.Cancel(); } catch { }
                }

                RaisePropertyChanged();
                RaisePropertyChanged(nameof(HasSetupDialog));
            }
        }

        private string description;

        public string Description {
            get => description;
            set {
                description = value;
                RaisePropertyChanged();
            }
        }

        public string DriverInfo => $"{Category} SDK";

        public string DriverVersion => sdk?.Version() ?? string.Empty;

        public void AbortExposure() {
            StopExposure();
        }

        private void ReadOutBinning() {
            /* Found no way to readout available binning modes. Assume 4x4 for all cams for now */
            BinningModes.Clear();
            MaxBinX = 4;
            MaxBinY = 4;
            for (short i = 1; i <= MaxBinX; i++) {
                BinningModes.Add(new BinningMode(i, i));
            }
        }

        public Task<bool> Connect(CancellationToken ct) {
            return Task<bool>.Run(() => {
                try {
                    SupportedActions.Clear();
                    imageReadyTCS?.TrySetCanceled();
                    imageReadyTCS = null;

                    var openedSdk = sdk.Open(this.internalId);
                    sdk = openedSdk ?? throw new Exception($"{Category} - Could not open camera");
                    var profile = profileService.ActiveProfile.CameraSettings;

                    /* Use maximum bit depth */
                    if (!sdk.put_Option(ToupTekAlikeOption.OPTION_BITDEPTH, 1)) {
                        throw new Exception($"{Category} - Could not set bit depth");
                    }

                    /* Use RAW Mode */
                    if (!sdk.put_Option(ToupTekAlikeOption.OPTION_RAW, 1)) {
                        throw new Exception($"{Category} - Could not set RAW mode");
                    }

                    if (!sdk.put_AutoExpoEnable(false)) {
                        Logger.Error($"{Category} - Could not disable Auto Exposure mode");
                    }

                    ReadOutBinning();
                    SupportedActions.Add(ToupTekActions.BinAverage);
                    if (MaxFanSpeed > 0) {
                        SupportedActions.Add(ToupTekActions.FanSpeed);
                    }

                    sdk.get_Size(out var width, out var height);
                    this.CameraXSize = width;
                    this.CameraYSize = height;

                    /* Readout flags */
                    if ((this.flags & ToupTekAlikeFlag.FLAG_TEC_ONOFF) != 0) {
                        /* Can set Target Temp */
                        CanSetTemperature = true;
                        sdk.get_Option(ToupTekAlikeOption.OPTION_TECTARGET, out var target);
                        if (target >= -280 && target <= 100) {
                            TemperatureSetPoint = target / 10.0;
                        } else {
                            TemperatureSetPoint = 20;
                        }
                        // Start with cooler disabled
                        CoolerOn = false;
                        CoolerPowerUpdateTask();
                    }

                    if ((this.flags & ToupTekAlikeFlag.FLAG_GETTEMPERATURE) != 0) {
                        /* Can get Target Temp */
                        CanGetTemperature = true;
                    }

                    if ((this.flags & ToupTekAlikeFlag.FLAG_BLACKLEVEL) != 0) {
                        CanSetOffset = true;
                    }

                    if ((this.flags & ToupTekAlikeFlag.FLAG_HEAT) != 0) {
                        HasDewHeater = true;
                        SupportedActions.Add(ToupTekActions.DewHeaterStrength);
                        if (profile.TouptekAlikeDewHeaterStrength < 0) {
                            TargetDewHeaterStrength = MaxDewHeaterStrength;
                        } else {
                            TargetDewHeaterStrength = profile.TouptekAlikeDewHeaterStrength;
                        }                        
                    }

                    if ((this.flags & ToupTekAlikeFlag.FLAG_LOW_NOISE) != 0) {
                        HasLowNoiseMode = true;
                        LowNoiseMode = profile.TouptekAlikeUltraMode;
                        SupportedActions.Add(ToupTekActions.LowNoiseMode);
                    }

                    if((this.flags & ToupTekAlikeFlag.FLAG_HIGH_FULLWELL) != 0) {
                        HasHighFullwell = true;
                        SupportedActions.Add(ToupTekActions.HighFullwellMode);
                        HighFullwellMode = profile.TouptekAlikeHighFullwell;
                    } else {
                        HasHighFullwell = false;
                    }

                    ReadoutModes = new List<string> { "Low Conversion Gain" };

                    if ((this.flags & ToupTekAlikeFlag.FLAG_CG) != 0) {
                        ReadoutModes.Add("High Conversion Gain");
                        Logger.Debug($"{Category} - Camera has High Conversion Gain option");

                        if ((this.flags & ToupTekAlikeFlag.FLAG_CGHDR) != 0) {
                            ReadoutModes.Add("High Dynamic Range");
                            Logger.Debug($"{Category} - Camera has HDR Gain option");
                        }
                    }  

                    if ((this.flags & ToupTekAlikeFlag.FLAG_TRIGGER_SOFTWARE) == 0) {
                        throw new Exception($"{Category} - This camera is not capable to be triggered by software and is not supported");
                    }

                    if (!sdk.put_Option(ToupTekAlikeOption.OPTION_FRAME_DEQUE_LENGTH, 2)) {
                        throw new Exception($"{Category} - Could not set deque length");
                    }

                    if (!sdk.put_Option(ToupTekAlikeOption.OPTION_TRIGGER, 1)) {
                        throw new Exception($"{Category} - Could not set Trigger manual mode");
                    }
                    softwareTriggerArmed = true;
                    flushPending = false;
                    LiveViewEnabled = false;

                    if (!sdk.StartPullModeWithCallback(new ToupTekAlikeCallback(OnEventCallback))) {
                        throw new Exception($"{Category} - Could not start pull mode");
                    }

                    if (CanSetLEDLights) {
                        SupportedActions.Add(ToupTekActions.LEDLights);
                        LEDLights = profile.TouptekAlikeLEDLights;
                    }

                    if (!sdk.put_Option(ToupTekAlikeOption.OPTION_FLUSH, 3)) {
                        Logger.Debug($"{Category} - Unable to flush camera");
                    }

                    if (!sdk.get_RawFormat(out var fourCC, out var bitDepth)) {
                        throw new Exception($"{Category} - Unable to get format information");
                    } else {
                        if (sdk.MonoMode) {
                            SensorType = SensorType.Monochrome;
                        } else {
                            SensorType = GetSensorType(fourCC);
                        }
                    }

                    this.nativeBitDepth = (int)bitDepth;

                    Connected = true;
                    RaiseAllPropertiesChanged();
                    return true;
                } catch (Exception ex) {
                    Connected = false;
                    try { sdk?.Close(); } catch { }
                    Logger.Error(ex);
                    Notification.ShowError(ex.Message);
                }
                return false;
            });
        }

        private SensorType GetSensorType(uint fourCC) {
            var bytes = BitConverter.GetBytes(fourCC);
            if (!BitConverter.IsLittleEndian) { Array.Reverse(bytes); }

            var sensor = System.Text.Encoding.ASCII.GetString(bytes);
            if (Enum.TryParse(sensor, true, out SensorType sensorType)) {
                return sensorType;
            }
            return SensorType.RGGB;
        }

        private bool _hasLowNoiseMode;

        public bool HasLowNoiseMode {
            get => _hasLowNoiseMode;
            set {
                _hasLowNoiseMode = value;
                RaisePropertyChanged();
            }
        }

        public bool LowNoiseMode {
            get {
                if (HasLowNoiseMode) {
                    sdk.get_Option(ToupTekAlikeOption.OPTION_LOW_NOISE, out var value);
                    Logger.Trace($"{Category} - Low Noise Mode is set to {value}");
                    return value == 1;
                } else {
                    return false;
                }
            }
            set {
                if (HasLowNoiseMode) {
                    Logger.Debug($"{Category} - Setting Low Noise Mode to {value}");
                    if (!sdk.put_Option(ToupTekAlikeOption.OPTION_LOW_NOISE, value ? 1 : 0)) {
                        Logger.Error($"{Category} - Could not set LowNoiseMode to {value}");
                    } else {
                        profileService.ActiveProfile.CameraSettings.TouptekAlikeUltraMode = value;
                        RaisePropertyChanged();
                    }
                }
            }
        }

        private bool hasHighFullwell;

        public bool HasHighFullwell {
            get => hasHighFullwell;
            set {
                hasHighFullwell = value;
                RaisePropertyChanged();
            }
        }

        public bool HighFullwellMode {
            get {
                if (HasHighFullwell) {
                    sdk.get_Option(ToupTekAlikeOption.OPTION_HIGH_FULLWELL, out var value);
                    Logger.Trace($"{Category} - High Fullwell mode is set to {value}");
                    return value == 1 ? true : false;
                } else {
                    return false;
                }
            }
            set {
                if (HasHighFullwell) {
                    Logger.Trace($"{Category} - High Fullwell mode to {value}");
                    if (!sdk.put_Option(ToupTekAlikeOption.OPTION_HIGH_FULLWELL, value ? 1 : 0)) {
                        Logger.Error($"{Category} - Could not set High Fullwell mode to {value}");
                    } else {
                        profileService.ActiveProfile.CameraSettings.TouptekAlikeHighFullwell = value;
                        RaisePropertyChanged();
                    }
                }
            }
        }

        public bool CanSetLEDLights {
            get => sdk is ToupTekSDKWrapper || sdk is OgmaSDKWrapper;
        }

        public bool LEDLights {
            get {
                sdk.get_Option(ToupTekAlikeOption.OPTION_TAILLIGHT, out var value);
                Logger.Trace($"{Category} - LED Lights option is set to {value}");
                return value == 1 ? true : false;
            }
            set {
                Logger.Trace($"{Category} - Set LED Lights option to {value}");
                if (!sdk.put_Option(ToupTekAlikeOption.OPTION_TAILLIGHT, value ? 1 : 0)) {
                    Logger.Error($"{Category} - Could not set LED Lights option to {value}");
                } else {
                    profileService.ActiveProfile.CameraSettings.TouptekAlikeLEDLights = value;
                    RaisePropertyChanged();
                }
            }
        }

        private void OnEventCallback(ToupTekAlikeEvent nEvent) {
            Logger.Trace($"{Category} - OnEventCallback {nEvent}");
            switch (nEvent) {
                // We should get an EVENT_IMAGE every time that the camera tells us an image is ready
                case ToupTekAlikeEvent.EVENT_IMAGE:
                    // Read the field once: this runs on the SDK callback thread while StartExposure may be
                    // swapping in a new source on another thread.
                    var tcs = imageReadyTCS;
                    if (tcs != null && !tcs.Task.IsCompleted) {
                        var id = tcs.Task.Id;
                        Logger.Trace($"{Category} - Setting DownloadExposure Result on Task {id}");
                        var success = tcs.TrySetResult(true);
                        Logger.Trace($"{Category} - DownloadExposure Result on Task {id} set successfully: {success}");
                        if (success) {
                            lastExposureEndTime = DateTime.UtcNow;
                        } else {
                            // The exposure was cancelled between the check above and here; the frame is in the deque anyway
                            flushPending = true;
                        }
                    } else {
                        // No exposure is waiting for this frame (e.g. a duplicate event from a buggy vendor SDK,
                        // or a frame that arrived after the exposure was aborted or timed out). Nothing pulls it,
                        // so it would stay in the SDK frame deque and be returned as the next exposure's image.
                        // Keep the callback minimal (no pull, no flush - a pull here would race a concurrent
                        // DownloadExposure): mark it and discard it before the next trigger instead. In live view
                        // this is expected between pull and the next wait.
                        flushPending = true;
                        if (LiveViewEnabled) {
                            Logger.Trace($"{Category} - EVENT_IMAGE without a pending live view frame");
                        } else {
                            Logger.Warning($"{Category} - unexpected EVENT_IMAGE with no exposure pending, frame will be discarded before the next exposure");
                        }
                    }
                    break;

                // This should never crop up - it's only for still images from live view
                case ToupTekAlikeEvent.EVENT_STILLIMAGE:
                    Logger.Warning($"{Category} - Still image event received, but not expected to get one!");
                    imageReadyTCS?.TrySetResult(true);
                    lastExposureEndTime = DateTime.UtcNow;
                    break;

                case ToupTekAlikeEvent.EVENT_NOFRAMETIMEOUT:
                    Logger.Error($"{Category} - Timout event occurred!");
                    break;

                case ToupTekAlikeEvent.EVENT_TRIGGERFAIL:
                    Logger.Error($"{Category} - Trigger Fail event received!");
                    break;

                case ToupTekAlikeEvent.EVENT_ERROR: // Error
                    Logger.Error($"{Category} - Camera reported a generic error!");
                    Notification.ShowError(Loc.Instance["LblGenericCameraError"]);
                    Disconnect();
                    break;

                case ToupTekAlikeEvent.EVENT_DISCONNECTED:
                    Logger.Warning($"{Category} - Camera disconnected! Maybe USB connection was interrupted.");
                    Notification.ShowError(Loc.Instance["LblCameraDisconnected"]);
                    OnEventDisconnected();
                    break;
            }
        }

        private IExposureData PullImage() {
            /* peek the width and height */
            var binning = BinX;
            var width = CameraXSize / binning;
            var height = CameraYSize / binning;

            if (roiInfo.HasValue) {
                width = roiInfo.Value.Width / binning;
                height = roiInfo.Value.Height / binning;
            }
            width -= width % 2;
            height -= height % 2;

            var size = width * height;
            var data = new ushort[size];

            if (!sdk.PullImage(data, nativeBitDepth, out var info)) {
                Logger.Error($"{Category} - Failed to pull image");
                // Discard whatever the SDK still holds so the camera is not left stuck on it
                if (!sdk.put_Option(ToupTekAlikeOption.OPTION_FLUSH, 3)) {
                    Logger.Error($"{Category} - Unable to flush camera after failed pull");
                }
                flushPending = true;
                return null;
            }

            // In video mode frames keep arriving between this pull and the next wait; discard them so every
            // live view pull returns a fresh frame instead of the oldest queued one. In trigger mode nothing
            // else is expected after the pull, and a stray frame is discarded before the next trigger instead.
            if (LiveViewEnabled) {
                if (!sdk.put_Option(ToupTekAlikeOption.OPTION_FLUSH, 2)) {
                    Logger.Error($"{Category} - Unable to flush camera");
                }
            }

            var bitScaling = this.profileService.ActiveProfile.CameraSettings.BitScaling;
            if (bitScaling) {
                var shift = 16 - nativeBitDepth;
                if (shift != 0) {
                    ImageUtility.BitShiftLeftInPlace(data, shift);
                }
            }

            var metaData = new ImageMetaData();
            metaData.FromCamera(this);
            metaData.Image.SetExposureTimes(lastExposureStartTime, lastExposureEndTime);
            if (info.hasgps) {
                var locked = info.gps.satellite >= 4;
                metaData.GenericHeaders.Add(new StringMetaDataHeader("GPS_EST", locked ? "locked and valid" : "not locked", "GPS status"));

                if (locked) {
                    var startTime = new DateTime(CoreUtil.UnixEpochTicks + (long)(info.gps.utcstart / 100L), DateTimeKind.Utc);
                    var endTime = new DateTime(CoreUtil.UnixEpochTicks + (long)(info.gps.utcend / 100L), DateTimeKind.Utc);
                    metaData.Image.SetExposureTimes(startTime, endTime);

                    metaData.GenericHeaders.Add(new StringMetaDataHeader("GPS_STAT", $"{info.gps.satellite} sats", "GPS status"));
                    metaData.GenericHeaders.Add(new DoubleMetaDataHeader("GPS_ALT", info.gps.altitude / 1000d, "Altitude (m)"));
                    metaData.GenericHeaders.Add(new DateTimeMetaDataHeader("GPS_EUTC", endTime, "End shutter time"));
                    metaData.GenericHeaders.Add(new DateTimeMetaDataHeader("GPS_ET", endTime, "End shutter time"));
                    metaData.GenericHeaders.Add(new DoubleMetaDataHeader("GPS_LAT", info.gps.latitude / 1000000d, "Latitude"));
                    metaData.GenericHeaders.Add(new DoubleMetaDataHeader("GPS_LON", info.gps.longitude / 1000000d, "Longitude"));
                    metaData.GenericHeaders.Add(new DateTimeMetaDataHeader("GPS_SUTC", startTime, "Start shutter time"));
                    metaData.GenericHeaders.Add(new DateTimeMetaDataHeader("GPS_ST", startTime, "Start shutter time"));
                    metaData.GenericHeaders.Add(new IntMetaDataHeader("GPS_SEQ", (int)info.seq, "Sequence number"));
                    metaData.GenericHeaders.Add(new IntMetaDataHeader("GPS_W", width, "Width"));
                    metaData.GenericHeaders.Add(new IntMetaDataHeader("GPS_H", height, "Height"));
                    if (info.hasexpotime) { 
                        metaData.GenericHeaders.Add(new IntMetaDataHeader("GPS_EXPU", (int)info.expotime, "Exposure (microseconds)"));
                    }
                    sdk.get_Option(ToupTekAlikeOption.OPTION_LINE_TIME, out int lineTime);
                    metaData.GenericHeaders.Add(new IntMetaDataHeader("GPS_LP", lineTime, "[ns] linePeriod"));
                }
            }  
            var imageData = exposureDataFactory.CreateImageArrayExposureData(
                    input: data,
                    width: width,
                    height: height,
                    bitDepth: this.BitDepth,
                    isBayered: this.SensorType != SensorType.Monochrome,
                    metaData: metaData);

            return imageData;
        }

        public void Disconnect() {
            try { coolerPowerReadoutCts?.Cancel(); } catch { }
            Connected = false;
            sdk.Close();
        }

        public async Task WaitUntilExposureIsReady(CancellationToken token) {
            using (token.Register(() => AbortExposure())) {
                await imageReadyTCS.Task;
            }
        }

        public async Task<IExposureData> DownloadExposure(CancellationToken token) {
            if (imageReadyTCS?.Task.IsCanceled != false) { return null; }
            IExposureData exposureData;
            using (token.Register(() => imageReadyTCS.TrySetCanceled())) {
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15))) {
                    using (cts.Token.Register(() => { Logger.Error($"{Category} - No Image Callback Event received"); imageReadyTCS.TrySetResult(true); })) {
                        var imageReady = await imageReadyTCS.Task;
                        exposureData = PullImage();
                    }
                }
            }
            if (exposureData == null) {
                // An abort that landed between the image event and the pull has flushed the frame away
                token.ThrowIfCancellationRequested();
            }
            if (LiveViewEnabled) {
                imageReadyTCS?.TrySetCanceled();
                imageReadyTCS = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            return exposureData;
        }

        public Task<IExposureData> DownloadLiveView(CancellationToken token) {
            var localCTS = CancellationTokenSource.CreateLinkedTokenSource(token);
            lastExposureStartTime = DateTime.UtcNow;
            return DownloadExposure(localCTS.Token);
        }

        public void SetBinning(short x, short y) {
            BinX = x;
            RaisePropertyChanged(nameof(BinY));
        }

        public void SetupDialog() {
        }

        /// <summary>
        /// Sets the exposure time. When given exposure time is out of bounds it will set it to nearest bound.
        /// </summary>
        /// <param name="time">Time in seconds</param>
        private void SetExposureTime(double time) {
            if (time < ExposureMin) {
                time = ExposureMin;
            }
            if (time > ExposureMax) {
                time = ExposureMax;
            }

            var usTime = (uint)(time * 1000000);
            if (!sdk.put_ExpoTime(usTime)) {
                throw new Exception($"{Category} - Could not set exposure time");
            }
        }

        private Rectangle GetROI() {
            var x = SubSampleX;
            x -= x % 2;
            var y = (CameraYSize - SubSampleY - SubSampleHeight);
            y -= y % 2;
            var width = Math.Max(SubSampleWidth, 16);
            width -= width % 2;
            var height = Math.Max(SubSampleHeight, 16);
            height -= height % 2;
            return new Rectangle(x, y, width, height);
        }

        private Rectangle? roiInfo;

        public void StartExposure(CaptureSequence sequence) {
            if (LiveViewEnabled) {
                // StopLiveView switches back to trigger mode asynchronously; take over here so its continuation
                // does not change the trigger mode again after the trigger below has been issued
                Logger.Debug($"{Category} - StartExposure while live view is still winding down");
                LiveViewEnabled = false;
                softwareTriggerArmed = false;
                flushPending = true;
            }

            var previous = imageReadyTCS;
            if (previous != null && !previous.Task.IsCompleted) {
                // The previous exposure is still running; cancel it before triggering again
                Logger.Warning($"{Category} - StartExposure while a previous exposure is still pending, cancelling it");
                if (!sdk.Trigger(0)) {
                    Logger.Warning($"{Category} - Could not cancel previous exposure");
                }
                flushPending = true;
            }
            previous?.TrySetCanceled();

            ReadoutMode = sequence.ImageType == CaptureSequence.ImageTypes.SNAPSHOT ? ReadoutModeForSnapImages : ReadoutModeForNormalImages;

            if (EnableSubSample) {
                var rect = GetROI();
                roiInfo = rect;
                if (!sdk.put_ROI((uint)rect.X, (uint)rect.Y, (uint)rect.Width, (uint)rect.Height)) {
                    throw new Exception($"{Category} - Failed to set ROI to {rect.X}x{rect.Y}x{rect.Width}x{rect.Height}");
                }
            } else {
                roiInfo = null;
                // 0,0,0,0 resets the ROI to original size
                if (!sdk.put_ROI(0, 0, 0, 0)) {
                    throw new Exception($"{Category} - Failed to reset ROI");
                }
            }

            SetExposureTime(sequence.ExposureTime);

            if (!softwareTriggerArmed) {
                if (!sdk.put_Option(ToupTekAlikeOption.OPTION_TRIGGER, 1)) {
                    throw new Exception($"{Category} - Could not set Trigger manual mode");
                }
                softwareTriggerArmed = true;
            }

            if (flushPending) {
                // Something is (or may be) left in the SDK frame deque: a stray frame, an aborted exposure, a failed
                // pull. Nothing is in flight right now, so this is the one point where a discard is always safe.
                // Clear the flag first: a frame arriving during the flush sets it again and is discarded next time,
                // clearing afterwards would lose that.
                flushPending = false;
                if (!sdk.put_Option(ToupTekAlikeOption.OPTION_FLUSH, 3)) {
                    Logger.Error($"{Category} - Unable to flush camera before exposure");
                }
            }

            // Arm the completion source as late as possible: while it is armed, any EVENT_IMAGE is taken as this
            // exposure's frame. A stray frame arriving during the SDK calls above finds the previous (completed)
            // source instead and is marked for discard.
            imageReadyTCS = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Logger.Trace($"{Category} - created new downloadExposure Task with Id {imageReadyTCS.Task.Id}");

            lastExposureStartTime = DateTime.UtcNow;
            if (!sdk.Trigger(1)) {
                throw new Exception($"{Category} - Failed to trigger camera");
            }
        }

        private volatile TaskCompletionSource<bool> imageReadyTCS;

        // Set whenever a frame may be left in the SDK frame deque without an exposure waiting for it. Only cleared
        // after the deque has been flushed with nothing in flight (connect, right before a trigger, entering live
        // view). Written from the SDK callback thread.
        private volatile bool flushPending;

        // Tracks whether the camera is in software trigger mode. It is set at connect and after live view, and
        // cleared when an exposure is stopped so the next StartExposure re-arms it. The SDK does not document
        // Trigger(0) leaving trigger mode; the re-arm is a cheap safeguard for cameras observed to stay
        // unresponsive after an abort.
        private volatile bool softwareTriggerArmed;
        private int nativeBitDepth;
        private DateTime lastExposureStartTime;
        private DateTime lastExposureEndTime;

        public int BitDepth => profileService.ActiveProfile.CameraSettings.BitScaling ? 16 : nativeBitDepth;

        private void OnEventDisconnected() {
            imageReadyTCS?.TrySetCanceled();
            try { coolerPowerReadoutCts?.Cancel(); } catch { }
            Connected = false;
        }

        public void StartLiveView(CaptureSequence sequence) {
            imageReadyTCS?.TrySetCanceled();

            if (EnableSubSample) {
                var rect = GetROI();
                roiInfo = rect;
                if (!sdk.put_ROI((uint)rect.X, (uint)rect.Y, (uint)rect.Width, (uint)rect.Height)) {
                    throw new Exception($"{Category} - Failed to set ROI to {rect.X}x{rect.Y}x{rect.Width}x{rect.Height}");
                }
            } else {
                roiInfo = null;
                // 0,0,0,0 resets the ROI to original size
                if (!sdk.put_ROI(0, 0, 0, 0)) {
                    throw new Exception($"{Category} - Failed to reset ROI");
                }
            }

            SetExposureTime(sequence.ExposureTime);

            // Drop anything left over from trigger mode; it may even have a different ROI than the live view frames
            flushPending = false;
            if (!sdk.put_Option(ToupTekAlikeOption.OPTION_FLUSH, 3)) {
                Logger.Error($"{Category} - Unable to flush camera before live view");
            }

            // Armed after the flush so a stray trigger mode frame cannot be taken as the first live view frame,
            // and before the mode switch so the first video frame is not missed
            imageReadyTCS = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Logger.Trace($"{Category} - starting live view Task with Id {imageReadyTCS.Task.Id}");
            LiveViewEnabled = true;

            softwareTriggerArmed = false;
            if (!sdk.put_Option(ToupTekAlikeOption.OPTION_TRIGGER, 0)) {
                throw new Exception("Could not set Trigger video mode");
            }
        }

        public void StopExposure() {
            if (!sdk.Trigger(0)) {
                Logger.Warning($"{Category} - Could not stop exposure");
            }
            imageReadyTCS?.TrySetCanceled();

            if (!LiveViewEnabled) {
                // The cancelled exposure may still deliver a frame (or already has). Discard it so it cannot be
                // returned by the next exposure, and let the next StartExposure re-arm the trigger mode.
                if (!sdk.put_Option(ToupTekAlikeOption.OPTION_FLUSH, 3)) {
                    Logger.Error($"{Category} - Unable to flush camera after stopping the exposure");
                }
                // A frame from the cancelled exposure can still arrive after this flush; flush again before the next trigger
                flushPending = true;
                softwareTriggerArmed = false;
            }
        }

        public void StopLiveView() {
            // The camera keeps streaming until the continuation below switches the mode back. The frame that
            // completes the pending source (and any after it) is never pulled, so the next exposure has to flush
            flushPending = true;
            imageReadyTCS.Task.ContinueWith((Task<bool> o) => {
                if (!LiveViewEnabled) {
                    // StartExposure already took over the switch back to trigger mode
                    return;
                }
                if (!sdk.put_Option(ToupTekAlikeOption.OPTION_TRIGGER, 1)) {
                    Disconnect();
                    throw new Exception("Could not set Trigger manual mode. Reconnect Camera!");
                }
                softwareTriggerArmed = true;
                LiveViewEnabled = false;
            });
        }

        public void UpdateSubSampleArea() {
            if (EnableSubSample) {
                var rect = GetROI();
                roiInfo = rect;
                if (!sdk.put_ROI((uint)rect.X, (uint)rect.Y, (uint)rect.Width, (uint)rect.Height)) {
                    throw new Exception($"{Category} - Failed to set ROI to {rect.X}x{rect.Y}x{rect.Width}x{rect.Height}");
                }
            } else {
                roiInfo = null;
                // 0,0,0,0 resets the ROI to original size
                if (!sdk.put_ROI(0, 0, 0, 0)) {
                    throw new Exception($"{Category} - Failed to reset ROI");
                }
            }
        }

        public int USBLimitStep => 1;

        public string Action(string actionName, string actionParameters) {
            switch (actionName) {
                case ToupTekActions.LowNoiseMode:
                    if (HasLowNoiseMode) {
                        var flag = StringToBoolean(actionParameters);
                        if(flag.HasValue) {
                            Logger.Info($"Device Action {actionName}: {flag.Value}");
                            LowNoiseMode = flag.Value;
                            return "1";
                        } else {
                            Logger.Error($"Unrecognized parameter [{actionParameters}] for action [{actionName}].");
                            return "0";
                        }
                    }
                    break;
                case ToupTekActions.HighFullwellMode: 
                    if (HasHighFullwell) {
                        var flag = StringToBoolean(actionParameters);
                        if (flag.HasValue) {
                            Logger.Info($"Device Action {actionName}: {flag.Value}");
                            HighFullwellMode = flag.Value;
                            return "1";
                        } else {
                            Logger.Error($"Unrecognized parameter [{actionParameters}] for action [{actionName}].");
                            return "0";
                        }
                    }
                    break;
                case ToupTekActions.BinAverage: { 
                        var flag = StringToBoolean(actionParameters);
                        if (flag.HasValue) {
                            Logger.Info($"Device Action {actionName}: {flag.Value}");
                            BinAverageEnabled = flag.Value;
                            return "1";
                        } else {
                            Logger.Error($"Unrecognized parameter [{actionParameters}] for action [{actionName}].");
                            return "0";
                        }
                    }
                case ToupTekActions.FanSpeed:
                    if (MaxFanSpeed > 0) {
                        if(int.TryParse(actionParameters, out var flag)) {
                            Logger.Info($"Device Action {actionName}: {flag}");
                            FanSpeed = flag;
                            return "1";

                        } else {
                            Logger.Error($"Unrecognized parameter [{actionParameters}] for action [{actionName}].");
                            return "0";
                        }
                    }
                    break;
                case ToupTekActions.DewHeaterStrength:
                    if (HasDewHeater) {
                        if (int.TryParse(actionParameters, out var flag)) {
                            Logger.Info($"Device Action {actionName}: {flag}");
                            TargetDewHeaterStrength = flag;
                            return "1";

                        } else {
                            Logger.Error($"Unrecognized parameter [{actionParameters}] for action [{actionName}].");
                            return "0";
                        }
                    }
                    break;
                case ToupTekActions.LEDLights:
                    if(CanSetLEDLights) {
                        var flag = StringToBoolean(actionParameters);
                        if (flag.HasValue) {
                            Logger.Info($"Device Action {actionName}: {flag.Value}");
                            LEDLights = flag.Value;
                            return "1";
                        } else {
                            Logger.Error($"Unrecognized parameter [{actionParameters}] for action [{actionName}].");
                            return "0";
                        }
                    }
                    break;
            }

            Logger.Error($"Unsupported action [{actionName}]");
            return "0";
            
        }

        private bool? StringToBoolean(string input) {
            if(string.IsNullOrWhiteSpace(input)) { return null; }

            string[] booleanFalse = { "0", "off", "no", "false", "f" };
            string[] booleanTrue = { "1", "on", "yes", "true", "t" };

            if(booleanFalse.Contains(input.ToLower())) {
                return false;
            }
            if(booleanTrue.Contains(input.ToLower())) {
                return true;
            }
            return null;
        }

        public string SendCommandString(string command, bool raw) {
            throw new NotImplementedException();
        }

        public bool SendCommandBool(string command, bool raw) {
            throw new NotImplementedException();
        }

        public void SendCommandBlind(string command, bool raw) {
            throw new NotImplementedException();
        }

        private static class ToupTekActions {
            public const string LowNoiseMode = "Ultra Mode";
            public const string HighFullwellMode = "High Fullwell Mode";
            public const string BinAverage = "Bin Average";
            public const string DewHeaterStrength = "Dew Heater Strength";
            public const string FanSpeed = "Fan Speed";
            public const string LEDLights = "LED Lights";
        }
    }
}
