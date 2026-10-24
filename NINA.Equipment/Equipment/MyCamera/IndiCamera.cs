#region "copyright"

/*
    Copyright © 2026 Nico Trost <nico.trost57@gmail.com> and the PI.N.S. contributors

    This file is part of PI 'N' Stars.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Core.Enum;
using NINA.Core.Model.Equipment;
using NINA.Core.Utility;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Model;
using NINA.Image.FileFormat.FITS;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;
using NINA.INDI;
using NINA.INDI.Devices;
using NINA.INDI.Interfaces;
using NINA.Profile.Interfaces;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Equipment.Equipment.MyCamera {

    public class IndiCamera : IndiDevice<IINDICamera>, ICamera {

        private readonly IProfileService profileService;
        private readonly IExposureDataFactory exposureDataFactory;
        private readonly IImageDataFactory imageDataFactory;
        private readonly bool logFramesAtDebug;

        private int _subSampleX;
        private int _subSampleY;
        private int _subSampleWidth;
        private int _subSampleHeight;
        private bool _enableSubSample;

        /// <param name="logFramesAtDebug">
        /// Log the messages that repeat for every frame at Debug instead of Info: a guide camera takes a frame every few
        /// seconds all night.
        /// </param>
        public IndiCamera(INDIDeviceInfo info, IProfileService profileService, IExposureDataFactory exposureDataFactory, IImageDataFactory imageDataFactory, bool logFramesAtDebug = false) : base(info) {
            this.profileService = profileService;
            this.exposureDataFactory = exposureDataFactory;
            this.imageDataFactory = imageDataFactory;
            this.logFramesAtDebug = logFramesAtDebug;
        }

        protected override string ConnectionLostMessage => "CameraConnectionLost";

        protected override IINDICamera GetInstance() {
            return device ??= new INDICamera(_device) { LogFramesAtDebug = logFramesAtDebug };
        }

        private void LogFrame(string message, [CallerMemberName] string memberName = "", [CallerFilePath] string sourceFilePath = "", [CallerLineNumber] int lineNumber = 0) {
            if (logFramesAtDebug) {
                Logger.Debug(message, memberName, sourceFilePath, lineNumber);
            } else {
                Logger.Info(message, memberName, sourceFilePath, lineNumber);
            }
        }

        protected override Task PostConnect() {
            // Enable BLOB delivery for this device so exposure images arrive
            INDIClient.Instance.EnableBLOB(device.Id);

            // The Toupcam SDK powers the TEC on by default on cooled models and the
            // INDI driver leaves it that way. Cooling is an explicit user action in
            // NINA, so start with the cooler off.
            if (device.CanSetTemperature) {
                device.CoolerOn = false;
            }

            _subSampleX = 0;
            _subSampleY = 0;
            _subSampleWidth = device.CameraXSize;
            _subSampleHeight = device.CameraYSize;

            // Apply toupbase-specific profile settings. These switches may not have arrived
            // yet when PostConnect runs — QueueProfileSwitch applies immediately if present,
            // or defers until the switch's defSwitchVector arrives.
            // Property names must match exactly what the toupbase INDI driver sends:
            // TC_HIGHFULLWELL and TC_TAILLIGHT have no internal underscore.
            var s = profileService.ActiveProfile.CameraSettings;
            device.QueueProfileSwitch("TC_LOW_NOISE", s.TouptekAlikeUltraMode);
            device.QueueProfileSwitch("TC_HIGHFULLWELL", s.TouptekAlikeHighFullwell);
            device.QueueProfileSwitch("TC_TAILLIGHT", s.TouptekAlikeLEDLights);

            return Task.CompletedTask;
        }

        // ── ICamera properties ──────────────────────────────────────────────

        public bool HasShutter => false;

        public double Temperature => GetProperty(nameof(IINDICamera.Temperature), double.NaN);

        public double TemperatureSetPoint {
            get => GetProperty(nameof(IINDICamera.TemperatureSetPoint), double.NaN);
            set {
                if (ShouldBeConnected) SetProperty(nameof(IINDICamera.TemperatureSetPoint), value);
            }
        }

        public short BinX {
            get => GetProperty(nameof(IINDICamera.BinX), (short)1);
            set {
                if (ShouldBeConnected) SetProperty(nameof(IINDICamera.BinX), value);
            }
        }

        public short BinY {
            get => GetProperty(nameof(IINDICamera.BinY), (short)1);
            set {
                if (ShouldBeConnected) SetProperty(nameof(IINDICamera.BinY), value);
            }
        }

        public string SensorName => string.Empty;

        public SensorType SensorType {
            get {
                // One-shot-color cameras announce their bayer matrix via CCD_CFA (e.g. "RGGB").
                // The pattern names map 1:1 onto the SensorType enum members.
                var cfa = GetProperty<string>(nameof(IINDICamera.CfaType), null);
                if (!string.IsNullOrEmpty(cfa) && Enum.TryParse<SensorType>(cfa, true, out var sensorType)) {
                    return sensorType;
                }
                return SensorType.Monochrome;
            }
        }

        public short BayerOffsetX => (short)GetProperty(nameof(IINDICamera.CfaOffsetX), 0);
        public short BayerOffsetY => (short)GetProperty(nameof(IINDICamera.CfaOffsetY), 0);

        public int CameraXSize => GetProperty(nameof(IINDICamera.CameraXSize), 0);
        public int CameraYSize => GetProperty(nameof(IINDICamera.CameraYSize), 0);

        public double ExposureMin => GetProperty(nameof(IINDICamera.ExposureMin), 0.001);
        public double ExposureMax => GetProperty(nameof(IINDICamera.ExposureMax), 3600.0);

        public short MaxBinX => GetProperty(nameof(IINDICamera.MaxBinX), (short)1);
        public short MaxBinY => GetProperty(nameof(IINDICamera.MaxBinY), (short)1);

        public double PixelSizeX => GetProperty(nameof(IINDICamera.PixelSizeX), double.NaN);
        public double PixelSizeY => GetProperty(nameof(IINDICamera.PixelSizeY), double.NaN);

        public bool CanSetTemperature => GetProperty(nameof(IINDICamera.CanSetTemperature), false);

        public bool CoolerOn {
            get => GetProperty(nameof(IINDICamera.CoolerOn), false);
            set {
                if (ShouldBeConnected) SetProperty(nameof(IINDICamera.CoolerOn), value);
            }
        }

        public double CoolerPower => GetProperty(nameof(IINDICamera.CoolerPower), double.NaN);

        public bool HasDewHeater => GetProperty(nameof(IINDICamera.HasDewHeater), false);

        public bool DewHeaterOn {
            get => GetProperty(nameof(IINDICamera.DewHeaterOn), false);
            set {
                if (ShouldBeConnected && HasDewHeater) {
                    device.SetDewHeater(value, TargetDewHeaterStrength);
                }
            }
        }

        // Surfaced for ninaAPI's reflection-based camera settings — Touch-N-Stars gates
        // its pins toggles on these exact names and writes them back via set-setting.
        public bool HasLowNoiseMode => GetProperty(nameof(IINDICamera.HasLowNoiseMode), false);

        public bool LowNoiseMode {
            // Profile is the source of truth — applied at PostConnect and saved on every
            // toggle, so it stays in sync even before the INDI echo updates the property cache.
            get => ShouldBeConnected && profileService.ActiveProfile.CameraSettings.TouptekAlikeUltraMode;
            set {
                if (ShouldBeConnected && HasLowNoiseMode) {
                    SetProperty(nameof(IINDICamera.LowNoiseMode), value);
                    profileService.ActiveProfile.CameraSettings.TouptekAlikeUltraMode = value;
                }
            }
        }

        public bool HasHighFullwell => GetProperty(nameof(IINDICamera.HasHighFullwell), false);

        public bool HighFullwellMode {
            get => ShouldBeConnected && profileService.ActiveProfile.CameraSettings.TouptekAlikeHighFullwell;
            set {
                if (ShouldBeConnected && HasHighFullwell) {
                    SetProperty(nameof(IINDICamera.HighFullwellMode), value);
                    profileService.ActiveProfile.CameraSettings.TouptekAlikeHighFullwell = value;
                }
            }
        }

        public bool CanSetLEDLights => GetProperty(nameof(IINDICamera.HasTailLight), false);

        public bool LEDLights {
            get => ShouldBeConnected && profileService.ActiveProfile.CameraSettings.TouptekAlikeLEDLights;
            set {
                if (ShouldBeConnected && CanSetLEDLights) {
                    SetProperty(nameof(IINDICamera.TailLight), value);
                    profileService.ActiveProfile.CameraSettings.TouptekAlikeLEDLights = value;
                }
            }
        }

        // Surfaced for ninaAPI's reflection-based camera settings, which the
        // Touch-N-Stars dew heater slider reads (MaxDewHeaterStrength for the scale)
        // and writes (TargetDewHeaterStrength via set-setting).
        public int MaxDewHeaterStrength => GetProperty(nameof(IINDICamera.DewHeaterMaxStrength), 0);

        public int TargetDewHeaterStrength {
            get {
                // Same hardware knob as the native ToupTek driver — reuse its profile
                // setting. Out-of-range/unset (-1) falls back to the highest level.
                var max = MaxDewHeaterStrength;
                var strength = profileService.ActiveProfile.CameraSettings.TouptekAlikeDewHeaterStrength;
                return strength > 0 && strength <= max ? strength : max;
            }
            set {
                var max = MaxDewHeaterStrength;
                if (max <= 0) return;
                var clamped = Math.Max(1, Math.Min(value, max));
                profileService.ActiveProfile.CameraSettings.TouptekAlikeDewHeaterStrength = clamped;
                // Apply immediately when the heater is already running
                if (ShouldBeConnected && DewHeaterOn) {
                    device.SetDewHeater(true, clamped);
                }
            }
        }

        public CameraStates CameraState => CameraStates.NoState;

        public bool CanSubSample => GetProperty(nameof(IINDICamera.CanSubSample), false);

        public bool EnableSubSample {
            get => _enableSubSample;
            set { _enableSubSample = value; }
        }

        public int SubSampleX {
            get => _subSampleX;
            set { _subSampleX = value; }
        }

        public int SubSampleY {
            get => _subSampleY;
            set { _subSampleY = value; }
        }

        public int SubSampleWidth {
            get => _subSampleWidth > 0 ? _subSampleWidth : CameraXSize;
            set { _subSampleWidth = value; }
        }

        public int SubSampleHeight {
            get => _subSampleHeight > 0 ? _subSampleHeight : CameraYSize;
            set { _subSampleHeight = value; }
        }

        public bool CanShowLiveView => false;
        public bool LiveViewEnabled => false;
        public bool HasBattery => false;
        public int BatteryLevel => -1;

        public int BitDepth => GetProperty(nameof(IINDICamera.BitDepth), 16);

        public bool CanSetOffset => GetProperty(nameof(IINDICamera.CanSetOffset), false);

        public int Offset {
            get => GetProperty(nameof(IINDICamera.Offset), 0);
            set {
                if (ShouldBeConnected && CanSetOffset) SetProperty(nameof(IINDICamera.Offset), value);
            }
        }

        public int OffsetMin => GetProperty(nameof(IINDICamera.OffsetMin), 0);
        public int OffsetMax => GetProperty(nameof(IINDICamera.OffsetMax), 0);

        public bool CanSetUSBLimit => GetProperty(nameof(IINDICamera.CanSetUSBLimit), false);

        public int USBLimit {
            get => GetProperty(nameof(IINDICamera.USBLimit), 0);
            set {
                if (ShouldBeConnected && CanSetUSBLimit) SetProperty(nameof(IINDICamera.USBLimit), value);
            }
        }

        public int USBLimitMin => GetProperty(nameof(IINDICamera.USBLimitMin), 0);
        public int USBLimitMax => GetProperty(nameof(IINDICamera.USBLimitMax), 0);
        public int USBLimitStep => GetProperty(nameof(IINDICamera.USBLimitStep), 1);

        public bool CanGetGain => GetProperty(nameof(IINDICamera.CanGetGain), false);
        public bool CanSetGain => GetProperty(nameof(IINDICamera.CanSetGain), false);

        public int GainMax => GetProperty(nameof(IINDICamera.GainMax), 0);
        public int GainMin => GetProperty(nameof(IINDICamera.GainMin), 0);

        public int Gain {
            get => GetProperty(nameof(IINDICamera.Gain), 0);
            set {
                if (ShouldBeConnected && CanSetGain) SetProperty(nameof(IINDICamera.Gain), value);
            }
        }

        public double ElectronsPerADU => double.NaN;

        public IList<string> ReadoutModes {
            get {
                // The protocol layer maps readout-mode-like switches (e.g. toupbase
                // conversion gain LCG/HCG/HDR) onto an indexed mode list.
                var modes = GetProperty<string[]>(nameof(IINDICamera.ReadoutModes), null);
                return modes is { Length: > 0 } ? new List<string>(modes) : new List<string> { "Default" };
            }
        }

        public short ReadoutMode {
            get => GetProperty(nameof(IINDICamera.ReadoutMode), (short)0);
            set {
                // Skip no-op writes — some drivers (e.g. indi_toupcam_ccd) react badly
                // to redundant vectors, and a conversion gain switch resets nothing anyway.
                if (ShouldBeConnected && value != ReadoutMode && value >= 0 && value < ReadoutModes.Count) {
                    SetProperty(nameof(IINDICamera.ReadoutMode), value);
                }
            }
        }

        private short readoutModeForSnapImages;
        public short ReadoutModeForSnapImages { get => readoutModeForSnapImages; set => readoutModeForSnapImages = value; }

        private short readoutModeForNormalImages;
        public short ReadoutModeForNormalImages { get => readoutModeForNormalImages; set => readoutModeForNormalImages = value; }

        public IList<int> Gains => new List<int>();

        public AsyncObservableCollection<BinningMode> BinningModes {
            get {
                var modes = new AsyncObservableCollection<BinningMode>();
                for (short b = 1; b <= MaxBinX; b++) {
                    modes.Add(new BinningMode(b, b));
                }
                return modes;
            }
        }

        // ── ICamera methods ─────────────────────────────────────────────────

        public void SetBinning(short x, short y) {
            if (!ShouldBeConnected) return;
            // Skip no-op updates — CameraVM sends binning before every capture, and
            // toupbase restarts the camera engine on ANY CCD_BINNING vector. An
            // exposure command racing that restart fails with E_UNEXPECTED.
            if (BinX == x && BinY == y) return;
            // Send both axes in a single CCD_BINNING command — sending them as two
            // separate SetProperty calls fires two newNumberVector messages which can
            // cause some drivers (e.g. indi_toupcam_ccd) to crash during exposure.
            device.SetBinning(x, y);
        }

        public void StartExposure(CaptureSequence sequence) {
            if (!ShouldBeConnected) return;

            // Apply the configured readout mode (mapped to e.g. toupbase conversion gain)
            // for this frame type — same convention as AscomCamera.StartExposure.
            ReadoutMode = sequence.ImageType == CaptureSequence.ImageTypes.SNAPSHOT
                ? readoutModeForSnapImages
                : readoutModeForNormalImages;

            var binX = sequence.Binning?.X ?? (short)1;
            var binY = sequence.Binning?.Y ?? (short)1;
            int gain = sequence.Gain >= 0 ? sequence.Gain : -1;
            int offset = sequence.Offset >= 0 ? sequence.Offset : -1;

            int subX = 0, subY = 0, subW = 0, subH = 0;
            bool subSample = sequence.EnableSubSample && sequence.SubSambleRectangle != null;
            if (subSample) {
                subX = (int)sequence.SubSambleRectangle.X;
                subY = (int)sequence.SubSambleRectangle.Y;
                subW = (int)sequence.SubSambleRectangle.Width;
                subH = (int)sequence.SubSambleRectangle.Height;
            }

            device.StartExposure(sequence.ExposureTime, binX, binY, subSample, subX, subY, subW, subH, gain, offset);
        }

        public async Task WaitUntilExposureIsReady(CancellationToken token) {
            if (ShouldBeConnected) {
                await device.WaitUntilExposureIsReady(token);
            }
        }

        public async Task<IExposureData> DownloadExposure(CancellationToken token) {
            try {
                var blobData = device.GetLastBlobData();
                var blobFormat = device.GetLastBlobFormat();

                if (blobData == null || blobData.Length == 0) {
                    Logger.Error("INDI camera: No BLOB data available for download");
                    return null;
                }

                token.ThrowIfCancellationRequested();

                // decompress .fits.z in memory; the FITS image is decoded in memory too, no temporary file
                LogFrame($"INDI camera: processing blob format='{blobFormat}' size={blobData.Length}");
                bool isCompressed = blobFormat != null && blobFormat.EndsWith(".z", StringComparison.OrdinalIgnoreCase);
                byte[] fits = blobData;
                if (isCompressed) {
                    // INDI drivers compress BLOBs with zlib compress2() (zlib framing,
                    // 0x78 header) — not gzip, so ZLibStream is required here.
                    using var ms = new MemoryStream(blobData);
                    using var zs = new ZLibStream(ms, CompressionMode.Decompress);
                    using var decompressed = new MemoryStream();
                    await zs.CopyToAsync(decompressed, token);
                    fits = decompressed.ToArray();
                }

                token.ThrowIfCancellationRequested();

                var imageData = await FITS.Load(fits, isBayered: SensorType != SensorType.Monochrome, imageDataFactory, token);
                if (imageData == null) {
                    Logger.Error("INDI camera: Failed to create image data from FITS blob");
                    return null;
                }

                return exposureDataFactory.CreateCachedExposureData(imageData);
            } catch (OperationCanceledException) {
                Logger.Info("INDI camera: Download cancelled");
                return null;
            } catch (Exception ex) {
                Logger.Error($"INDI camera: Exception during DownloadExposure: {ex.Message}");
                return null;
            }
        }

        public void AbortExposure() {
            if (ShouldBeConnected) device.AbortExposure();
        }

        public void StopExposure() {
            if (ShouldBeConnected) device.StopExposure();
        }

        public void StartLiveView(CaptureSequence sequence) {
            throw new NotImplementedException("INDI camera does not support live view");
        }

        public Task<IExposureData> DownloadLiveView(CancellationToken token) {
            throw new NotImplementedException("INDI camera does not support live view");
        }

        public void StopLiveView() {
            throw new NotImplementedException("INDI camera does not support live view");
        }

        public void UpdateSubSampleArea() {
            // Nothing to push to the device here — the CCD_FRAME region is applied
            // per exposure in StartExposure from the SubSample* properties.
        }
    }
}
