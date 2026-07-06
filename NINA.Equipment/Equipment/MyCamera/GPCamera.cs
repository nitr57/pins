#region "copyright"

/*
    Copyright © 2025 Nico Trost <nico.trost57@gmail.com> and the PI.N.S. contributors

    This file is part of PI 'N' Stars.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using GPSDK;
using static GPSDK.GPSDK;
using NINA.Core.Enum;
using NINA.Image.ImageData;
using NINA.Profile.Interfaces;
using NINA.Core.Utility;
using NINA.Core.Utility.Notification;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using NINA.Core.Locale;
using NINA.Core.Model.Equipment;
using NINA.Core.MyMessageBox;
using NINA.Equipment.Model;
using NINA.Image.Interfaces;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Utility;
using System.Text;
using System.Runtime.InteropServices;
using System.Globalization;
using System.Text.RegularExpressions;

namespace NINA.Equipment.Equipment.MyCamera {

    public class GPCamera : BaseINPC, ICamera, IDisposable {

        private readonly IntPtr _context;
        private readonly IntPtr _camera;

        private readonly string _name;
        private readonly string _path;
        private bool _disposed;
        private bool _cameraExited;
        private System.Timers.Timer _batteryPolling;
        // gphoto2 is not thread-safe; all camera I/O must be serialised through this lock.
        private readonly object _gpLock = new object();
        private const double LongExposureThresholdSeconds = 30.0d;
        private const int BulbFileEventTimeoutMs = 20000;
        private static readonly string[] ShutterSpeedProperties = { "shutterspeed", "shutterspeed2" };
        private static readonly string[] IsoProperties = { "iso", "iso2", "isospeed" };
        private static readonly string[] RawFormatTokens = {
            "RAW", "NEF", "NRW", "CR3", "CR2", "ARW", "RAF", "ORF", "RW2", "PEF", "SRF", "SR2"
        };
        private (string Property, string Choice)? bulbShutterSpeed;
        private string shutterSpeedProperty = "shutterspeed";
        private string isoProperty = "iso";
        private BulbExposureControl activeBulbExposureControl = BulbExposureControl.None;

        private enum BulbExposureControl {
            None,
            EosRemoteRelease,
            BulbToggle
        }

        public GPCamera(string name, string path, IProfileService profileService, IExposureDataFactory exposureDataFactory) {
            _disposed = false;
            _name = name;
            _path = path;

            this.profileService = profileService;
            this.exposureDataFactory = exposureDataFactory;
            Id = $"{name}-{path}";

            // Create context
            _context = GpContextNew();

            // Build the camera handle. On any failure, release whatever was already
            // allocated and throw so the chooser does not list a half-constructed camera.
            IntPtr portInfoList = IntPtr.Zero;
            try {
                // Get port info list
                if (CheckError(GpPortInfoListNew(ref portInfoList))) {
                    throw new Exception("Failed to create port info list");
                }

                // Load port list
                if (CheckError(GpPortInfoListLoad(portInfoList))) {
                    throw new Exception("Failed to load port list");
                }

                // Look up our path
                var portNumber = GpPortInfoListLookupPath(portInfoList, _path);
                if (portNumber < 0) {
                    throw new Exception($"Failed to lookup port path '{_path}': {portNumber}");
                }

                // Fetch the corresponding port info. This points into portInfoList's memory;
                // gp_camera_set_port_info below copies what it needs, so it is only valid as a
                // local for the lifetime of portInfoList and must not be stored.
                if (CheckError(GpPortInfoListGetInfo(portInfoList, portNumber, out var portInfo))) {
                    throw new Exception($"Failed to get port info at index {portNumber}");
                }

                // Create camera
                if (CheckError(GpCameraNew(ref _camera))) {
                    throw new Exception("Failed to create camera");
                }

                // Set port info
                if (CheckError(GpCameraSetPortInfo(_camera, portInfo))) {
                    throw new Exception("Failed to set port info");
                }
            } catch {
                // Release native resources allocated so far. Mark disposed so the finalizer
                // does not attempt to free the same handles again.
                if (_camera != IntPtr.Zero) {
                    GpCameraFree(_camera);
                }
                if (_context != IntPtr.Zero) {
                    GpContextUnref(_context);
                }
                if (portInfoList != IntPtr.Zero) {
                    GpPortInfoListFree(portInfoList);
                }
                _disposed = true;
                throw;
            }

            if (CheckError(GpPortInfoListFree(portInfoList))) {
                Logger.Error("Failed to free port info list");
            }
        }

        public string Category { get; } = "libgphoto2";

        private IProfileService profileService;

        private readonly IExposureDataFactory exposureDataFactory;

        public bool HasShutter => true;

        private bool _connected;

        public bool Connected {
            get => _connected;
            set {
                _connected = value;
                RaisePropertyChanged();
            }
        }

        private bool canGetTemperature = true;
        public double Temperature {
            get {
                var temp = double.NaN;
                var value = string.Empty;
                if (canGetTemperature) {
                    if (GetProperty("sensortemperature", out value) == GP_ERROR_CODE.GP_OK) {
                        if (double.TryParse(value, out temp)) {
                            return temp;
                        }
                    } else if (GetProperty("sensor-temperature", out value) == GP_ERROR_CODE.GP_OK) {
                        if (double.TryParse(value, out temp)) {
                            return temp;
                        }
                    } else if (GetProperty("temp", out value) == GP_ERROR_CODE.GP_OK) {
                        if (double.TryParse(value, out temp)) {
                            return temp;
                        }
                    } else if (GetProperty("temperature", out value) == GP_ERROR_CODE.GP_OK) {
                        if (double.TryParse(value, out temp)) {
                            return temp;
                        }
                    } else if (GetProperty("cameracontrolmode", out value) == GP_ERROR_CODE.GP_OK) {
                        if (double.TryParse(value, out temp)) {
                            return temp;
                        }
                    }
                    Logger.Error("Cannot read temperature property");
                    canGetTemperature = false;
                }

                return double.NaN;
            }
        }

        public double TemperatureSetPoint {
            get => double.NaN;
            set {
            }
        }

        public short BinX {
            get => 1;
            set {
            }
        }

        public bool CanSubSample => false;

        public short BinY {
            get => 1;
            set {
            }
        }

        public bool EnableSubSample { get; set; }
        public int SubSampleX { get; set; }
        public int SubSampleY { get; set; }
        public int SubSampleWidth { get; set; }
        public int SubSampleHeight { get; set; }

        public string Name => _name;

        public string DisplayName => Name;

        public string Description => "libgphoto2 Camera";

        public string DriverInfo => "libgphoto2";

        public string DriverVersion => GpLibraryVersion();

        // Live view is not implemented yet (StartLiveView/StopLiveView/DownloadLiveView throw).
        // Always report false so the UI does not offer a button that would throw on click.
        public bool CanShowLiveView => false;

        public string SensorName => string.Empty;

        public SensorType SensorType => SensorType.RGGB;

        public short BayerOffsetX => 0;

        public short BayerOffsetY => 0;

        private (int width, int height) _cameraResolution = (-1, -1);

        public int CameraXSize => _cameraResolution.width;

        public int CameraYSize => _cameraResolution.height;

        public double ExposureMin => ShutterSpeeds.Count > 0 ? ShutterSpeeds.Min(v => (double?)v.Value).GetValueOrDefault(0) : 0;

        public double ExposureMax => double.PositiveInfinity;

        public double ElectronsPerADU => double.NaN;

        public short MaxBinX => 1;

        public short MaxBinY => 1;

        private (double pixelSizeX, double pixelSizeY) _pixelSizes = (double.NaN, double.NaN);
        public double PixelSizeX => _pixelSizes.pixelSizeX;
        public double PixelSizeY => _pixelSizes.pixelSizeY;

        public bool CanSetTemperature => false;

        public bool CoolerOn {
            get => false;
            set { }
        }

        public double CoolerPower => double.NaN;

        public bool HasDewHeater => false;

        public bool DewHeaterOn {
            get => false;
            set { }
        }

        public CameraStates CameraState => CameraStates.NoState;

        public IList<string> SupportedActions => new List<string>();

        public bool CanSetOffset => false;

        public int OffsetMin => 0;

        public int OffsetMax => 0;

        public bool CanSetUSBLimit => false;

        public bool CanGetGain => true;

        public bool CanSetGain => true;

        public int GainMax => ISOSpeeds.Count > 0 ? ISOSpeeds.Aggregate((l, r) => l.Value > r.Value ? l : r).Value : 0;

        public int GainMin => ISOSpeeds.Count > 0 ? ISOSpeeds.Aggregate((l, r) => l.Value < r.Value ? l : r).Value : 0;

        public int Gain {
            get {
                if (GetProperty(isoProperty, out var iso) != GP_ERROR_CODE.GP_OK) {
                    return -1;
                }

                return TranslateISOChoice(iso);
            }
            set {
                ValidateMode();
                string iso = FindISOChoice(value);
                if (string.IsNullOrEmpty(iso)) {
                    Logger.Warning($"libgphoto2: ISO value {value} is not in the camera ISO choices for {isoProperty}. Choices: {string.Join(", ", ISOSpeeds.Values.OrderBy(x => x))}");
                    Notification.ShowExternalError(Loc.Instance["LblUnableToSetISO"], "libgphoto2 Driver Error");
                    return;
                }

                if (CheckError(SetProperty(isoProperty, iso), $"{isoProperty}-{iso}")) {
                    Notification.ShowExternalError(Loc.Instance["LblUnableToSetISO"], "libgphoto2 Driver Error");
                    return;
                }

                if (GetProperty(isoProperty, out var readBackIso) == GP_ERROR_CODE.GP_OK) {
                    int readBackValue = TranslateISOChoice(readBackIso);
                    Logger.Info($"libgphoto2: Requested ISO {value} via {isoProperty}='{iso}', camera reads back '{readBackIso}' ({readBackValue})");
                    if (readBackValue > 0 && readBackValue != value) {
                        Logger.Warning($"libgphoto2: Camera ISO readback {readBackValue} does not match requested ISO {value}. Check camera Auto ISO and mode settings.");
                    }
                }

                RaisePropertyChanged();
            }
        }

        private IList<int> _gains;

        public IList<int> Gains {
            get {
                if (_gains == null) {
                    _gains = new List<int>();
                }
                return _gains;
            }
        }

        public IList<string> ReadoutModes => new List<string> { "Default" };

        public short ReadoutMode {
            get => 0;
            set { }
        }

        private short _readoutModeForSnapImages;

        public short ReadoutModeForSnapImages {
            get => _readoutModeForSnapImages;
            set {
                _readoutModeForSnapImages = value;
                RaisePropertyChanged();
            }
        }

        private short _readoutModeForNormalImages;

        public short ReadoutModeForNormalImages {
            get => _readoutModeForNormalImages;
            set {
                _readoutModeForNormalImages = value;
                RaisePropertyChanged();
            }
        }

        private AsyncObservableCollection<BinningMode> _binningModes;

        public AsyncObservableCollection<BinningMode> BinningModes {
            get {
                if (_binningModes == null) {
                    _binningModes = new AsyncObservableCollection<BinningMode>();
                    _binningModes.Add(new BinningMode(1, 1));
                }
                return _binningModes;
            }
            private set { }
        }

        public bool HasSetupDialog => false;

        private string _id;

        public string Id {
            get => _id;
            set {
                _id = value;
                RaisePropertyChanged();
            }
        }

        private TaskCompletionSource<object> downloadExposure;
        private string _lastCapturedFolder;
        private string _lastCapturedFilename;

        public void AbortExposure() {
            Logger.Debug("libgphoto2: AbortExposure");
            CancelDownloadExposure();
        }

        private bool Initialize() {
            ValidateMode();
            GetISOSpeeds();
            GetShutterSpeeds();
            GetBatteryLevel();
            ConfigureCaptureTarget();
            if (!SetRawFormat()) {
                Logger.Error("libgphoto2: Could not switch camera to a RAW image format");
                return false;
            }

            _cameraResolution = GetCameraResolution();
            _pixelSizes = GetPixelSizes();

            return true;
        }

        private bool SetRawFormat() {
            // Different camera vendors expose RAW selection under different gphoto2 widgets.
            foreach (var property in new[] { "imageformat", "imagequality", "imgquality" }) {
                if (TrySetRawFormat(property)) {
                    return true;
                }
            }

            return false;
        }

        private bool TrySetRawFormat(string property) {
            if (GetPropertyList(property, out var formats) == GP_ERROR_CODE.GP_OK) {
                // Prefer camera-reported choices because labels vary by model and firmware.
                string rawFormat = SelectRawFormat(formats);
                if (!string.IsNullOrEmpty(rawFormat)) {
                    var result = SetProperty(property, rawFormat);
                    if (result == GP_ERROR_CODE.GP_OK) {
                        Logger.Info($"libgphoto2: {property} set to RAW format '{rawFormat}'");
                        return true;
                    }
                    Logger.Warning($"libgphoto2: Could not set {property} to '{rawFormat}': {result}");
                }

                // If choices were available, trust them. Blind token writes here only
                // create slow failing round-trips and noisy logs on cameras that do not
                // offer RAW through this widget.
                return false;
            }

            // Fallback for cameras that accept a RAW token but do not report choices.
            foreach (var fmt in RawFormatTokens) {
                if (SetProperty(property, fmt) == GP_ERROR_CODE.GP_OK) {
                    Logger.Info($"libgphoto2: {property} set to RAW format '{fmt}'");
                    return true;
                }
            }

            return false;
        }

        private static string SelectRawFormat(IList<string> formats) {
            foreach (var token in RawFormatTokens) {
                string exactMatch = formats.FirstOrDefault(f =>
                    string.Equals(f?.Trim(), token, StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrEmpty(exactMatch)) {
                    return exactMatch;
                }
            }

            string rawOnly = formats.FirstOrDefault(f => IsRawFormatChoice(f) && !IsJpegFormatChoice(f));
            if (!string.IsNullOrEmpty(rawOnly)) {
                return rawOnly;
            }

            return formats.FirstOrDefault(IsRawFormatChoice);
        }

        private static bool IsRawFormatChoice(string format) {
            if (string.IsNullOrWhiteSpace(format)) {
                return false;
            }

            string normalized = format.ToLowerInvariant();
            return normalized.Contains("raw")
                || normalized.Contains("nef")
                || normalized.Contains("nrw")
                || normalized.Contains("cr2")
                || normalized.Contains("cr3")
                || normalized.Contains("arw")
                || normalized.Contains("raf")
                || normalized.Contains("orf")
                || normalized.Contains("rw2")
                || normalized.Contains("pef")
                || normalized.Contains("srf")
                || normalized.Contains("sr2");
        }

        private static bool IsJpegFormatChoice(string format) {
            if (string.IsNullOrWhiteSpace(format)) {
                return false;
            }

            string normalized = format.ToLowerInvariant();
            return normalized.Contains("jpg") || normalized.Contains("jpeg");
        }

        private void ConfigureCaptureTarget() {
            if (!IsKnownNikonCamera()) {
                return;
            }

            if (GetPropertyList("capturetarget", out var targets) != GP_ERROR_CODE.GP_OK) {
                return;
            }

            string target = targets.FirstOrDefault(IsMemoryCaptureTargetChoice);
            if (string.IsNullOrEmpty(target)) {
                return;
            }

            var result = SetProperty("capturetarget", target);
            if (result == GP_ERROR_CODE.GP_OK) {
                Logger.Info($"libgphoto2: Nikon capturetarget set to '{target}' for direct download");
            } else {
                Logger.Warning($"libgphoto2: Could not set Nikon capturetarget to '{target}': {result}");
            }
        }

        private static bool IsMemoryCaptureTargetChoice(string target) {
            if (string.IsNullOrWhiteSpace(target)) {
                return false;
            }

            string normalized = target.ToLowerInvariant();
            return normalized.Contains("sdram")
                || normalized.Contains("ram")
                || (normalized.Contains("memory") && !normalized.Contains("card"));
        }

        /// <summary>
        /// Internal ShutterSpeed Code -> ShutterSpeed Value
        /// e.g.: 0x10 -> 30
        /// </summary>
        private Dictionary<string, double> _shutterSpeeds = new Dictionary<string, double>();
        private Dictionary<string, double> ShutterSpeeds => _shutterSpeeds;

        private void GetShutterSpeeds() {
            ShutterSpeeds.Clear();
            bulbShutterSpeed = null;
            shutterSpeedProperty = ShutterSpeedProperties[0];

            foreach (var property in ShutterSpeedProperties) {
                if (GetPropertyList(property, out var list) != GP_ERROR_CODE.GP_OK) {
                    continue;
                }

                bool parsedAnyChoice = false;
                foreach (var prop in list) {
                    if (IsBulbShutterSpeedChoice(prop)) {
                        bulbShutterSpeed ??= (property, prop);
                        continue;
                    }

                    try {
                        if (TryParseShutterSpeed(prop, out double speed)) {
                            ShutterSpeeds[prop] = speed;
                            parsedAnyChoice = true;
                        }
                    } catch (Exception ex) {
                        Logger.Warning($"Failed to parse shutter speed '{prop}': {ex.Message}");
                    }
                }

                if (parsedAnyChoice) {
                    shutterSpeedProperty = property;
                    Logger.Debug($"libgphoto2: Using {property} for shutter speed control");
                    return;
                }
            }

            Logger.Warning("libgphoto2: No usable shutter-speed choices were reported by the camera");
        }

        private static bool TryParseShutterSpeed(string value, out double seconds) {
            seconds = 0;
            if (string.IsNullOrWhiteSpace(value)) {
                return false;
            }

            // gphoto2 labels vary: Canon often reports "1/2000" or "0.5", while
            // Nikon bodies may report values such as "2s" or "0.2s".
            string normalized = value.Trim().Trim('"').Replace(',', '.');
            if (normalized.EndsWith("s", StringComparison.OrdinalIgnoreCase)) {
                normalized = normalized.Substring(0, normalized.Length - 1).Trim();
            }

            if (normalized.Contains('/')) {
                var parts = normalized.Split('/');
                if (parts.Length == 2
                    && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator)
                    && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var denominator)
                    && denominator != 0) {
                    seconds = numerator / denominator;
                    return seconds > 0;
                }
                return false;
            }

            return double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out seconds) && seconds > 0;
        }

        private bool IsManualMode() {
            // gphoto2 uses different mode widget names across vendors and bodies.
            foreach (var property in new[] { "exposuremode", "autoexposuremode", "expprogram", "capturemode" }) {
                if (GetProperty(property, out var mode) == GP_ERROR_CODE.GP_OK && IsManualModeValue(mode)) {
                    return true;
                }
            }

            return false;
        }

        private bool IsBulbMode() {
            // Some bodies expose Bulb as a mode, while others expose it as a shutter-speed choice.
            foreach (var property in new[] { "exposuremode", "autoexposuremode", "expprogram", "capturemode" }) {
                if (GetProperty(property, out var mode) == GP_ERROR_CODE.GP_OK && IsBulbModeValue(mode)) {
                    return true;
                }
            }

            // Use plain status checks so cameras without a shutter-speed widget do not spam error logs.
            foreach (var property in ShutterSpeedProperties) {
                if (GetProperty(property, out var shutterspeed) == GP_ERROR_CODE.GP_OK && IsBulbShutterSpeedChoice(shutterspeed)) {
                    return true;
                }
            }

            return false;
        }

        private static bool IsManualModeValue(string value) {
            if (string.IsNullOrWhiteSpace(value)) {
                return false;
            }

            string normalized = value.Trim();
            return normalized.Equals("M", StringComparison.OrdinalIgnoreCase)
                || normalized.StartsWith("M ", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("Manual", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsBulbModeValue(string value) {
            return !string.IsNullOrWhiteSpace(value)
                && value.Contains("Bulb", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsBulbShutterSpeedChoice(string value) {
            if (string.IsNullOrWhiteSpace(value)) {
                return false;
            }

            string normalized = value.Trim();
            return normalized.Equals("bulb", StringComparison.OrdinalIgnoreCase)
                || normalized.Equals("b", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("Bulb", StringComparison.OrdinalIgnoreCase);
        }

        private Dictionary<string, int> ISOSpeeds = new Dictionary<string, int>();

        private void GetISOSpeeds() {
            ISOSpeeds.Clear();
            Gains.Clear();

            IList<string> list = Array.Empty<string>();
            foreach (var property in IsoProperties) {
                if (GetPropertyList(property, out list) == GP_ERROR_CODE.GP_OK && list.Count > 0) {
                    isoProperty = property;
                    break;
                }
            }

            foreach (var prop in list) {
                // Try to parse as integer
                try {
                    var match = Regex.Match(prop, @"\d+");
                    if (match.Success && int.TryParse(match.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) && number > 0) {
                        ISOSpeeds.Add(prop, number);
                        Gains.Add(number);
                    }
                } catch (Exception ex) {
                    Logger.Warning($"Failed to parse iso speed '{prop}': {ex.Message}");
                }
            }

            if (ISOSpeeds.Count > 0) {
                Logger.Info($"libgphoto2: Using ISO property {isoProperty} with choices: {string.Join(", ", ISOSpeeds.Values.OrderBy(x => x))}");
            } else {
                Logger.Warning($"libgphoto2: Could not find parseable ISO choices using: {string.Join(", ", IsoProperties)}");
            }
        }

        private string FindISOChoice(int value) {
            return ISOSpeeds.Where(x => x.Value == value).Select(x => x.Key).FirstOrDefault();
        }

        private int TranslateISOChoice(string value) {
            if (string.IsNullOrWhiteSpace(value)) {
                return -1;
            }

            if (ISOSpeeds.TryGetValue(value, out var translatedISO)) {
                return translatedISO;
            }

            // Nikon labels can include units or extra text. Fall back to parsing the first
            // positive integer so metadata and readback still reflect the camera value.
            var match = Regex.Match(value, @"\d+");
            if (match.Success && int.TryParse(match.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedISO) && parsedISO > 0) {
                return parsedISO;
            }

            return -1;
        }

        private void AddISOMetadata(ImageMetaData metaData) {
            if (metaData.Camera.Gain <= 0) {
                return;
            }

            // NINA's generic DSLR control is named Gain, but for libgphoto2 cameras this
            // value is the camera ISO. Write explicit ISO keys so FITS/TIFF/XISF readers
            // do not have to guess that GAIN means ISO for Nikon/Canon DSLR captures.
            metaData.GenericHeaders.Add(new IntMetaDataHeader("ISO", metaData.Camera.Gain, "Camera ISO speed"));
            metaData.GenericHeaders.Add(new IntMetaDataHeader("ISOSPEED", metaData.Camera.Gain, "Camera ISO speed"));
            metaData.GenericHeaders.Add(new StringMetaDataHeader("ISOPROP", isoProperty, "libgphoto2 ISO property"));
        }

        private void GetBatteryLevel() {
            try {
                if (!CheckError(GetProperty("batterylevel", out string prop), "batterylevel-get")) {
                    // Parse battery level (typically a percentage like "90%")
                    prop = prop.Replace("%", "").Trim();
                    if (int.TryParse(prop, out var level)) {
                        BatteryLevel = level;
                    } else {
                        Logger.Warning($"Failed to parse battery level value '{prop}'");
                    }
                }
            } catch (Exception ex) {
                Logger.Error(ex);
                BatteryLevel = -1;
            }
        }

        public void Disconnect() {
            StopBatteryPolling();
            lock (_gpLock) {
                if (!_cameraExited) {
                    CheckError(GpCameraExit(_camera, _context));
                    _cameraExited = true;
                }
            }
            Connected = false;
        }

        ~GPCamera() {
            Dispose(false);
        }

        protected virtual void Dispose(bool disposing) {
            if (!_disposed) {
                StopBatteryPolling();
                lock (_gpLock) {
                    if (!_cameraExited && _camera != IntPtr.Zero) {
                        GpCameraExit(_camera, _context);
                    }
                    if (_camera != IntPtr.Zero) {
                        CheckError(GpCameraFree(_camera));
                    }
                }
                if (_context != IntPtr.Zero) {
                    GpContextUnref(_context);
                }
                _disposed = true;
            }
        }

        public void Dispose() {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        public async Task WaitUntilExposureIsReady(CancellationToken token) {
            using (token.Register(() => AbortExposure())) {
                await downloadExposure.Task;
            }
        }

        public byte[] DownloadFile(string folder, string filename) {
            IntPtr file = IntPtr.Zero;
            try {
                byte[] folderBytes = Encoding.UTF8.GetBytes(folder + "\0");
                byte[] filenameBytes = Encoding.UTF8.GetBytes(filename + "\0");

                lock (_gpLock) {
                    if (GpFileNew(out file) != (int)GP_ERROR_CODE.GP_OK) {
                        Logger.Error("Failed to create new file object");
                        return null;
                    }

                    var result = GpCameraFileGet(_camera, folderBytes, filenameBytes, CameraFileType.GP_FILE_TYPE_NORMAL, file, _context);
                    if (result != (int)GP_ERROR_CODE.GP_OK) {
                        Logger.Error($"Failed to download file {folder}/{filename}: {result}");
                        return null;
                    }

                    if (GpFileGetDataAndSize(file, out var dataPtr, out var size) != (int)GP_ERROR_CODE.GP_OK) {
                        Logger.Error("Failed to get file data");
                        return null;
                    }

                    // Marshal.Copy while still under lock so the CameraFile buffer stays valid
                    byte[] fileData = new byte[size];
                    Marshal.Copy(dataPtr, fileData, 0, (int)size);
                    Logger.Info($"Downloaded {filename} from {folder}, size: {size} bytes");

                    DeleteFileFromCamera(folder, filename);

                    return fileData;
                }
            } catch (Exception ex) {
                Logger.Error($"Exception during DownloadFile: {ex.Message}");
                return null;
            } finally {
                // GpFileFree only touches a memory-only refcount, safe outside lock
                if (file != IntPtr.Zero) {
                    GpFileFree(file);
                }
            }
        }

        private void DeleteFileFromCamera(string folder, string filename) {
            try {
                byte[] folderBytes = Encoding.UTF8.GetBytes(folder + "\0");
                byte[] filenameBytes = Encoding.UTF8.GetBytes(filename + "\0");
                GP_ERROR_CODE deleteResult;
                lock (_gpLock) {
                    deleteResult = GpCameraFileDelete(_camera, folderBytes, filenameBytes, _context);
                }
                if (deleteResult != GP_ERROR_CODE.GP_OK) {
                    Logger.Warning($"Failed to delete file {folder}/{filename} from camera: {deleteResult}");
                } else {
                    Logger.Debug($"Deleted {filename} from camera storage");
                }
            } catch (Exception ex) {
                Logger.Error($"Exception deleting file {folder}/{filename}: {ex.Message}");
            }
        }

        public Task<IExposureData> DownloadExposure(CancellationToken token) {
            return Task.Run<IExposureData>(async () => {

                if (downloadExposure.Task.IsCanceled) { return null; }

                byte[] rawImageData = null;

                try {
                    using (token.Register(() => CancelDownloadExposure())) {
                        await downloadExposure.Task;
                    }

                    using (MyStopWatch.Measure("libgphoto2 - Image Download")) {
                        if (string.IsNullOrEmpty(_lastCapturedFolder) || string.IsNullOrEmpty(_lastCapturedFilename)) {
                            Logger.Error("libgphoto2: No captured image available for download");
                            return null;
                        }

                        Logger.Debug($"Downloading {_lastCapturedFilename} from {_lastCapturedFolder}");

                        // Download file from camera
                        rawImageData = DownloadFile(_lastCapturedFolder, _lastCapturedFilename);
                        if (rawImageData == null || rawImageData.Length == 0) {
                            Logger.Error($"libgphoto2: Failed to download image data");
                            return null;
                        }

                        Logger.Debug($"libgphoto2: Image {_lastCapturedFilename} downloaded, size: {rawImageData.Length} bytes");

                        token.ThrowIfCancellationRequested();
                    }

                    using (MyStopWatch.Measure("libgphoto2 - Creating Image Array")) {
                        var metaData = new ImageMetaData();
                        metaData.FromCamera(this);
                        AddISOMetadata(metaData);

                        // Derive the file type from the actual filename extension
                        string fileType = System.IO.Path.GetExtension(_lastCapturedFilename)
                            .TrimStart('.')
                            .ToLowerInvariant();
                        if (string.IsNullOrEmpty(fileType)) {
                            // File events normally include an extension; if not, avoid assuming
                            // Canon CR2 for every camera.
                            fileType = GetDefaultRawExtension();
                        }

                        return this.exposureDataFactory.CreateRAWExposureData(
                            converter: RawConverterEnum.LIBRAW,
                            rawBytes: rawImageData,
                            rawType: fileType,
                            bitDepth: BitDepth,
                            metaData: metaData);
                    }
                } catch (OperationCanceledException) {
                    Logger.Info("libgphoto2: Image download canceled");
                    return null;
                } catch (Exception ex) {
                    Logger.Error($"Exception during DownloadExposure: {ex.Message}");
                    return null;
                }
            });
        }

        private void CancelDownloadExposure() {
            Logger.Debug("CancelDownloadExposure");
            try {
                // Release the shutter only if we're currently in a bulb mode exposure
                if (_currentExposureIsBulb) {
                    Logger.Debug("libgphoto2: Canceling bulb exposure - releasing shutter");
                    ReleaseShutter();

                    // Wait for the file event to get the image location, then delete it
                    Logger.Debug("libgphoto2: Waiting for file event to delete aborted image...");
                    SetCapturedFileFromEvent(BulbFileEventTimeoutMs);
                    if (!string.IsNullOrEmpty(_lastCapturedFolder) && !string.IsNullOrEmpty(_lastCapturedFilename)) {
                        Logger.Debug($"libgphoto2: Aborted image location: {_lastCapturedFolder}/{_lastCapturedFilename}");
                        DeleteFileFromCamera(_lastCapturedFolder, _lastCapturedFilename);
                    } else {
                        Logger.Warning("Could not get file event for aborted image to delete it");
                    }
                    _lastCapturedFolder = null;
                    _lastCapturedFilename = null;
                }
                _currentExposureIsBulb = false;
                activeBulbExposureControl = BulbExposureControl.None;
                bulbCompletionCTS?.Cancel();
            } catch (Exception ex) {
                Logger.Error($"Exception in CancelDownloadExposure: {ex.Message}");
            }
            downloadExposure?.TrySetCanceled();
        }

        public void SetBinning(short x, short y) {
        }

        public void SetupDialog() {
        }

        public (int width, int height) GetCameraResolution() {
            // Try to query from camera widgets
            if (GetProperty("imagesize", out var imageSize) == GP_ERROR_CODE.GP_OK) {
                // Try to parse resolution
                var parts = imageSize.Split(new[] { 'x', 'X', '×' });
                if (parts.Length == 2) {
                    if (int.TryParse(parts[0].Trim(), out int width) && int.TryParse(parts[1].Trim(), out int height)) {
                        return (width, height);
                    }
                }
            }

            // Check lookup table
            if (GpCameraSpecs.TryGetValue(_name, out var specs)) {
                return (specs.width, specs.height);
            }

            Logger.Warning($"Camera resolution unknown for '{_name}'");
            return (-1, -1);
        }

        public (double pixelSizeX, double pixelSizeY) GetPixelSizes() {
            // Check lookup table
            if (GpCameraSpecs.TryGetValue(_name, out var specs)) {
                return (specs.pixelSizeX, specs.pixelSizeY);
            }

            Logger.Warning($"Camera pixel sizes unknown for '{_name}'");
            return (double.NaN, double.NaN);
        }

        private void ValidateMode() {
            if (!IsManualMode() && !IsBulbMode()) {
                Notification.ShowError("Camera must be in MANUAL or BULB mode");
                Logger.Error("Camera must be in MANUAL or BULB mode");
                throw new Exception("Invalid camera mode");
            }
        }

        private void ValidateModeForExposure(double exposureTime) {
            bool isManualMode = IsManualMode();
            bool isBulbMode = IsBulbMode();

            if (!isManualMode && !isBulbMode) {
                Notification.ShowError("Camera must be in MANUAL or BULB mode");
                Logger.Error("Camera must be in MANUAL or BULB mode");
                throw new Exception("Invalid camera mode for taking exposures");
            }

            if (isManualMode && !isBulbMode && exposureTime > LongExposureThresholdSeconds) {
                // Canon EOS bodies can accept shutterspeed=bulb and read it back, yet still
                // expose only 30s unless the physical mode dial is set to Bulb. Fail fast
                // instead of capturing a truncated frame.
                if (IsKnownCanonCamera()) {
                    Notification.ShowError("For exposures > 30s, please switch the camera to BULB mode manually.");
                    Logger.Error($"Exposure time {exposureTime}s > 30s requested while a Canon camera is in Manual mode; the camera must be switched to BULB mode manually");
                    throw new Exception("Camera requires manual BULB mode for exposures > 30s");
                }

                // Some libgphoto2 cameras expose Bulb as a shutter-speed choice, so
                // select it before using the bulb toggle exposure path.
                if (!TryPrepareBulbShutterSpeed()) {
                    Notification.ShowError("For exposures > 30s, please switch the camera to BULB mode manually.");
                    Logger.Error($"Exposure time {exposureTime}s > 30s requested, but no usable bulb shutter speed could be selected");
                    throw new Exception("Camera requires BULB mode for exposures > 30s");
                }
            }

            if (isBulbMode && exposureTime < 1.0) {
                // Try to leave Bulb for short exposures; gp_camera_capture in Bulb can wait
                // for a release we never issue.
                Logger.Info($"Camera is in bulb mode but exposure time is {exposureTime}s (< 1s). Attempting to set shutter speed.");
                GetShutterSpeeds();
                if (!SetExposureTime(exposureTime)) {
                    // Fail fast instead of starting a timed capture that can block indefinitely.
                    Logger.Error($"Could not set exposure time to {exposureTime}s while camera is in Bulb mode.");
                    Notification.ShowError("Camera is in Bulb mode. For exposures < 1s, switch to Manual mode or use exposure time >= 1s.");
                    throw new Exception("Cannot take a sub-second exposure while the camera is in Bulb mode");
                }
            }
        }

        private bool TryPrepareBulbShutterSpeed() {
            if (IsBulbMode()) {
                return true;
            }

            if (bulbShutterSpeed == null) {
                GetShutterSpeeds();
            }

            if (bulbShutterSpeed == null) {
                Logger.Warning("libgphoto2: Camera did not report a bulb shutter-speed choice");
                return false;
            }

            var bulb = bulbShutterSpeed.Value;
            var result = SetProperty(bulb.Property, bulb.Choice);
            if (result == GP_ERROR_CODE.GP_OK) {
                Logger.Info($"libgphoto2: {bulb.Property} set to bulb choice '{bulb.Choice}'");
                return true;
            }

            Logger.Warning($"libgphoto2: Could not set {bulb.Property} to bulb choice '{bulb.Choice}': {result}");
            return false;
        }

        private bool ShouldUseBulbExposure(double exposureTime) {
            if (exposureTime > LongExposureThresholdSeconds) {
                return true;
            }

            return exposureTime >= 1.0 && IsBulbMode();
        }

        private Task bulbCompletionTask = null;
        private CancellationTokenSource bulbCompletionCTS = null;
        private bool _currentExposureIsBulb = false;
        // Guards against releasing the bulb shutter more than once per exposure (the completion
        // timer, StopExposure and an abort can all reach ReleaseShutter). Reset when pressed.
        private bool _shutterReleased = true;

        public void StartExposure(CaptureSequence sequence) {
            if (downloadExposure?.Task?.Status <= TaskStatus.Running) {
                Logger.Warning("An exposure was still in progress. Cancelling it to start another.");
                CancelDownloadExposure();
            }

            var exposureTime = sequence.ExposureTime;

            // Validate before any exposure state is touched: if this throws, no exposure has
            // started and a subsequent AbortExposure must find the camera idle instead of
            // releasing the shutter and hunting for a file event that will never come.
            ValidateModeForExposure(exposureTime);

            downloadExposure = new TaskCompletionSource<object>();
            _lastCapturedFolder = null;
            _lastCapturedFilename = null;
            activeBulbExposureControl = BulbExposureControl.None;
            bool useBulb = ShouldUseBulbExposure(exposureTime);
            _currentExposureIsBulb = useBulb;

            // Set exposure time first if not using bulb mode
            if (!useBulb) {
                if (!SetExposureTime(exposureTime)) {
                    Logger.Warning($"Could not set exposure time to {exposureTime}s, using nearest available");
                }
            }

            // Start exposure
            bool success = SendStartExposureCmd(useBulb);
            if (!success) {
                throw new Exception("Failed to start exposure");
            }

            /* MLU not supported
                        // Do mirror lockup
                        if (MirrorLockupDelay > 0d) {
                            Logger.Debug($"MLU: Releasing first shutter trigger");

                            // Release the shutter button after the first press. The mirror should remain flipped (locked) up.
                            SendStopExposureCmd(useBulb);

                            // Sleep for the user-specified delay
                            Logger.Debug($"MLU: Waiting {MirrorLockupDelay} seconds before 2nd trigger");
                            Thread.Sleep(Convert.ToInt32(MirrorLockupDelay * 1000d));

                            // Press the shutter button again to open the curtain and start the actual exposure
                            Logger.Debug($"MLU: Starting 2nd trigger");
                            SendStartExposureCmd(useBulb);
                        }
            */
            // Finish exposure
            if (useBulb) {
                /* Stop Exposure after exposure time */
                try { bulbCompletionCTS?.Cancel(); } catch { }
                bulbCompletionCTS = new CancellationTokenSource();
                bulbCompletionTask = Task.Run(async () => {
                    await CoreUtil.Wait(TimeSpan.FromSeconds(exposureTime), bulbCompletionCTS.Token);
                    if (!bulbCompletionCTS.IsCancellationRequested) {
                        SendStopExposureCmd(true);
                    }
                }, bulbCompletionCTS.Token);
            } else {
                // Immediately release shutter button when having a set exposure
                SendStopExposureCmd(false);
            }
        }

        public (bool success, string folder, string filename) TriggerCapture() {
            try {
                GP_ERROR_CODE result;
                CameraFilePath path;
                lock (_gpLock) {
                    result = GpCameraCapture(_camera, CameraCaptureType.GP_CAPTURE_IMAGE, out path, _context);
                }
                if (result != GP_ERROR_CODE.GP_OK) {
                    Logger.Error($"Failed to trigger capture: {result}");
                    return (false, null, null);
                }
                string folder = ReadNullTerminatedString(path.folder);
                string filename = ReadNullTerminatedString(path.name);
                Logger.Info($"Image captured: {folder}/{filename}");
                return (true, folder, filename);
            } catch (Exception ex) {
                Logger.Error($"Exception during TriggerCapture: {ex.Message}");
                return (false, null, null);
            }
        }

        private (bool success, string folder, string filename) WaitForFileEvent(int timeoutMs = 5000) {
            try {
                Logger.Debug($"WaitForFileEvent: Polling up to {timeoutMs}ms for GP_EVENT_FILE_ADDED...");
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();

                // Use short per-iteration timeouts so the lock window stays small,
                // allowing other threads (e.g. abort) to interleave between iterations.
                const int iterationTimeoutMs = 200;

                while (stopwatch.ElapsedMilliseconds < timeoutMs) {
                    int remainingMs = (int)Math.Min(iterationTimeoutMs,
                        Math.Max(0, timeoutMs - stopwatch.ElapsedMilliseconds));
                    GP_ERROR_CODE result;
                    CameraEventType eventType;
                    IntPtr eventData = IntPtr.Zero;
                    lock (_gpLock) {
                        result = GpCameraWaitForEvent(_camera, remainingMs, out eventType, out eventData, _context);
                    }

                    // For FILE_ADDED/FOLDER_ADDED/UNKNOWN, libgphoto2 malloc()s eventData and the
                    // caller must free() it; for TIMEOUT/CAPTURE_COMPLETE it is NULL. GpFree() no-ops
                    // on NULL, so freeing unconditionally is safe. Uses libc free(), matching the
                    // allocator the bundled libgphoto2 was built against.
                    try {
                        if (result == GP_ERROR_CODE.GP_OK && eventType == CameraEventType.GP_EVENT_FILE_ADDED) {
                            try {
                                var filePath = (CameraFilePath)Marshal.PtrToStructure(eventData, typeof(CameraFilePath));
                                string folder = ReadNullTerminatedString(filePath.folder);
                                string filename = ReadNullTerminatedString(filePath.name);
                                Logger.Debug($"WaitForFileEvent: File event detected: {folder}/{filename}");
                                stopwatch.Stop();
                                return (true, folder, filename);
                            } catch (Exception ex) {
                                Logger.Error($"Exception parsing file event: {ex.Message}");
                                stopwatch.Stop();
                                return (false, null, null);
                            }
                        } else if (result != GP_ERROR_CODE.GP_OK && result != GP_ERROR_CODE.GP_ERROR_TIMEOUT) {
                            // Real error (not just timeout)
                            Logger.Warning($"WaitForFileEvent: gp_camera_wait_for_event returned {result}");
                            stopwatch.Stop();
                            return (false, null, null);
                        }
                        // GP_EVENT_TIMEOUT or other benign events: continue polling
                    } finally {
                        GpFree(eventData);
                    }
                }

                stopwatch.Stop();
                Logger.Warning($"WaitForFileEvent: Timeout after {timeoutMs}ms without receiving GP_EVENT_FILE_ADDED");
                return (false, null, null);
            } catch (Exception ex) {
                Logger.Error($"Exception during WaitForFileEvent: {ex.Message}");
                return (false, null, null);
            }
        }

        private bool SendStartExposureCmd(bool useBulb) {
            try {
                if (useBulb) {
                    return TryStartBulbExposure();
                } else {
                    // Timed exposures use gp_camera_capture immediately and return the file path directly.
                    Logger.Debug("libgphoto2: Initiating timed exposure");
                    var (success, folder, filename) = TriggerCapture();
                    if (!success) {
                        Logger.Error($"libgphoto2: Error initiating timed exposure");
                        return false;
                    }
                    _lastCapturedFolder = folder;
                    _lastCapturedFilename = filename;
                    downloadExposure?.TrySetResult(true);
                    return true;
                }
            } catch (Exception ex) {
                Logger.Error($"Exception during SendStartExposureCmd: {ex.Message}");
                return false;
            }
        }

        private bool TryStartBulbExposure() {
            bool preferBulbToggle = IsKnownNikonCamera() || (HasProperty("bulb") && !IsKnownCanonCamera());

            if (preferBulbToggle && TryStartBulbToggleExposure()) {
                return true;
            }

            if (TryStartEosRemoteReleaseExposure()) {
                return true;
            }

            if (!preferBulbToggle && TryStartBulbToggleExposure()) {
                return true;
            }

            Logger.Error("libgphoto2: Could not initiate bulb exposure with either bulb toggle or eosremoterelease");
            return false;
        }

        private bool TryStartBulbToggleExposure() {
            Logger.Debug("libgphoto2: Initiating BULB exposure via bulb toggle");
            var pressResult = SetBooleanProperty("bulb", true);
            if (pressResult != GP_ERROR_CODE.GP_OK) {
                Logger.Warning($"libgphoto2: bulb=1 failed ({pressResult})");
                return false;
            }

            activeBulbExposureControl = BulbExposureControl.BulbToggle;
            _shutterReleased = false;
            Logger.Debug("libgphoto2: Bulb toggle opened shutter");
            return true;
        }

        private bool TryStartEosRemoteReleaseExposure() {
            // Canon EOS bodies vary in accepted eosremoterelease labels; try common
            // gphoto2 values.
            Logger.Debug("libgphoto2: Initiating BULB exposure via eosremoterelease");
            GP_ERROR_CODE pressResult = SetProperty("eosremoterelease", "Immediate");
            if (pressResult != GP_ERROR_CODE.GP_OK) {
                Logger.Warning($"eosremoterelease 'Immediate' failed ({pressResult}), trying 'Press Full'");
                pressResult = SetProperty("eosremoterelease", "Press Full");
            }
            if (pressResult != GP_ERROR_CODE.GP_OK) {
                Logger.Warning($"eosremoterelease 'Press Full' failed ({pressResult}), trying 'Press Full MF'");
                pressResult = SetProperty("eosremoterelease", "Press Full MF");
            }
            if (pressResult != GP_ERROR_CODE.GP_OK) {
                Logger.Warning($"libgphoto2: Failed to initiate bulb exposure via eosremoterelease: {pressResult}");
                return false;
            }

            activeBulbExposureControl = BulbExposureControl.EosRemoteRelease;
            _shutterReleased = false;
            Logger.Debug("libgphoto2: eosremoterelease opened shutter");
            return true;
        }

        private void SendStopExposureCmd(bool useBulb) {
            try {
                if (useBulb) {
                    Logger.Debug("libgphoto2: Stopping BULB exposure after timer expired");

                    ReleaseShutter();

                    // After bulb release, libgphoto2 reports the finalized image path
                    // through a FILE_ADDED event.
                    Logger.Debug($"libgphoto2: Waiting for file event after shutter release (up to {BulbFileEventTimeoutMs}ms)...");
                    SetCapturedFileFromEvent(BulbFileEventTimeoutMs);

                    activeBulbExposureControl = BulbExposureControl.None;
                    _currentExposureIsBulb = false;
                    downloadExposure?.TrySetResult(true);
                } else {
                    Logger.Debug("libgphoto2: Timed exposure complete");
                }
            } catch (Exception ex) {
                Logger.Error($"Exception during SendStopExposureCmd: {ex.Message}");
            }
        }

        private bool ReleaseShutter() {
            if (_shutterReleased) {
                Logger.Debug("libgphoto2: Shutter already released, skipping");
                return true;
            }
            _shutterReleased = true;

            // Release through the same control path that opened the shutter; fall back if
            // state was lost during abort/stop races.
            return activeBulbExposureControl switch {
                BulbExposureControl.BulbToggle => TryReleaseBulbToggle(),
                BulbExposureControl.EosRemoteRelease => TryReleaseEosRemoteRelease(),
                _ => TryReleaseEosRemoteRelease() || TryReleaseBulbToggle()
            };
        }

        private bool TryReleaseBulbToggle() {
            var releaseResult = SetBooleanProperty("bulb", false);
            if (releaseResult == GP_ERROR_CODE.GP_OK) {
                Logger.Debug("libgphoto2: Bulb toggle closed shutter");
                return true;
            }

            Logger.Warning($"libgphoto2: bulb=0 failed ({releaseResult})");
            return false;
        }

        private bool TryReleaseEosRemoteRelease() {
            // Use "Release" first; fall back to "Release Full" for models that expose
            // that value instead.
            var releaseResult = SetProperty("eosremoterelease", "Release");
            if (releaseResult != GP_ERROR_CODE.GP_OK) {
                Logger.Warning($"eosremoterelease 'Release' failed ({releaseResult}), trying 'Release Full'");
                releaseResult = SetProperty("eosremoterelease", "Release Full");
            }
            if (releaseResult != GP_ERROR_CODE.GP_OK) {
                Logger.Warning($"libgphoto2: Failed to release shutter via eosremoterelease: {releaseResult}");
                return false;
            }

            Logger.Debug("libgphoto2: eosremoterelease closed shutter");
            return true;
        }

        private string GetDefaultRawExtension() {
            if (IsKnownNikonCamera()) {
                return "nef";
            }
            if (IsKnownCanonCamera()) {
                return "cr2";
            }
            if (CameraNameContains("Sony")) {
                return "arw";
            }
            if (CameraNameContains("Fuji") || CameraNameContains("Fujifilm")) {
                return "raf";
            }
            if (CameraNameContains("Olympus") || CameraNameContains("OM System")) {
                return "orf";
            }
            if (CameraNameContains("Panasonic") || CameraNameContains("Lumix")) {
                return "rw2";
            }
            if (CameraNameContains("Pentax")) {
                return "pef";
            }

            return "raw";
        }

        private bool IsKnownNikonCamera() {
            return CameraNameContains("Nikon");
        }

        private bool IsKnownCanonCamera() {
            return CameraNameContains("Canon");
        }

        private bool CameraNameContains(string value) {
            return _name?.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private bool HasProperty(string property) {
            lock (_gpLock) {
                IntPtr widget = IntPtr.Zero;
                var err = GpCameraGetSingleConfig(_camera, property, out widget, _context);
                if (widget != IntPtr.Zero) {
                    GpWidgetFree(widget);
                }
                return err == GP_ERROR_CODE.GP_OK;
            }
        }

        private void SetCapturedFileFromEvent(int timeoutMs) {
            var (success, folder, filename) = WaitForFileEvent(timeoutMs);
            Logger.Debug($"libgphoto2: WaitForFileEvent returned - success={success}, folder='{folder}', filename='{filename}'");
            if (success && !string.IsNullOrEmpty(folder) && !string.IsNullOrEmpty(filename)) {
                _lastCapturedFolder = folder;
                _lastCapturedFilename = filename;
                Logger.Debug($"libgphoto2: Image location set: {folder}/{filename}");
            } else if (success) {
                Logger.Debug("libgphoto2: Event completed but file info not available in event");
            } else {
                Logger.Error("libgphoto2: Event wait failed");
            }
        }

        private bool SetExposureTime(double exposureTime) {
            double nearestExposureTime = double.MaxValue;
            if (exposureTime != double.MaxValue) {
                if (ShutterSpeeds.Count == 0) {
                    GetShutterSpeeds();
                }

                var l = new List<double>(ShutterSpeeds.Values);
                if (l.Count == 0) {
                    Logger.Warning("libgphoto2: Camera did not report any usable shutter-speed choices");
                    return false;
                }
                nearestExposureTime = l.Aggregate((x, y) => Math.Abs(x - exposureTime) < Math.Abs(y - exposureTime) ? x : y);
            } else {
                // For new do dont deal with maxValue exposures in manual mode, just return false
                return false;
            }

            var key = ShutterSpeeds.FirstOrDefault(x => x.Value == nearestExposureTime).Key;
            if (key == string.Empty) {
                return false;
            }

            // Set the shutter speed on the camera
            if (CheckError(SetProperty(shutterSpeedProperty, key), $"{shutterSpeedProperty}-{key}")) {
                Logger.Error($"Failed to set {shutterSpeedProperty} to {key}");
                Notification.ShowError("Switch to bulb mode for exposures longer than 30s");
                return false;
            }

            return true;
        }

        public int Offset {
            get => -1;
            set { }
        }

        public int USBLimit {
            get => -1;
            set { }
        }

        public int USBLimitMax => -1;
        public int USBLimitMin => -1;
        public int USBLimitStep => -1;

        private int batteryLevel = -1;

        public int BatteryLevel {
            get => batteryLevel;
            private set {
                batteryLevel = value;
                RaisePropertyChanged();
            }
        }

        public void StopExposure() {
            SendStopExposureCmd(_currentExposureIsBulb);
            _currentExposureIsBulb = false;
        }

        private GP_ERROR_CODE SetBooleanProperty(string property, bool value) {
            string[] candidates = value
                ? new[] { "1", "on", "true", "yes" }
                : new[] { "0", "off", "false", "no" };

            GP_ERROR_CODE lastResult = GP_ERROR_CODE.GP_ERROR_NOT_SUPPORTED;
            foreach (string candidate in candidates) {
                lastResult = SetProperty(property, candidate);
                if (lastResult == GP_ERROR_CODE.GP_OK) {
                    return GP_ERROR_CODE.GP_OK;
                }
            }

            return lastResult;
        }

        private GP_ERROR_CODE SetProperty(string property, string value) {
            lock (_gpLock) {
                IntPtr widget = IntPtr.Zero;
                GP_ERROR_CODE err = GpCameraGetSingleConfig(_camera, property, out widget, _context);
                if (err != GP_ERROR_CODE.GP_OK) {
                    return err;
                }

                try {
                    err = SetWidgetValue(widget, value);
                    if (err != GP_ERROR_CODE.GP_OK) {
                        return err;
                    }
                    return GpCameraSetSingleConfig(_camera, property, widget, _context);
                } finally {
                    GpWidgetFree(widget);
                }
            }
        }

        private GP_ERROR_CODE SetWidgetValue(IntPtr widget, string value) {
            // gp_widget_set_value takes a void*; Nikon bulb is commonly a toggle,
            // not a string.
            var typeResult = GpWidgetGetType(widget, out var type);
            if (typeResult != GP_ERROR_CODE.GP_OK) {
                return typeResult;
            }

            switch (type) {
                case CameraWidgetType.GP_WIDGET_TOGGLE:
                    if (TryParseBooleanPropertyValue(value, out int toggleValue)) {
                        return GpWidgetSetValue(widget, toggleValue);
                    }
                    return GP_ERROR_CODE.GP_ERROR_BAD_PARAMETERS;
                case CameraWidgetType.GP_WIDGET_RANGE:
                    if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float rangeValue)
                        || float.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out rangeValue)) {
                        return GpWidgetSetValue(widget, rangeValue);
                    }
                    return GP_ERROR_CODE.GP_ERROR_BAD_PARAMETERS;
                case CameraWidgetType.GP_WIDGET_DATE:
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int dateValue)) {
                        return GpWidgetSetValue(widget, dateValue);
                    }
                    return GP_ERROR_CODE.GP_ERROR_BAD_PARAMETERS;
                default:
                    return GpWidgetSetValue(widget, value);
            }
        }

        private GP_ERROR_CODE GetProperty(string property, out string data) {
            lock (_gpLock) {
                IntPtr widget = IntPtr.Zero;
                GP_ERROR_CODE err;
                data = string.Empty;

                err = GpCameraGetSingleConfig(_camera, property, out widget, _context);
                if (err != GP_ERROR_CODE.GP_OK) {
                    return err;
                }

                try {
                    err = GetWidgetValue(widget, out data);
                    if (err != GP_ERROR_CODE.GP_OK) {
                        return err;
                    }
                    return GP_ERROR_CODE.GP_OK;
                } finally {
                    GpWidgetFree(widget);
                }
            }
        }

        private static GP_ERROR_CODE GetWidgetValue(IntPtr widget, out string value) {
            value = string.Empty;
            var typeResult = GpWidgetGetType(widget, out var type);
            if (typeResult != GP_ERROR_CODE.GP_OK) {
                return typeResult;
            }

            switch (type) {
                case CameraWidgetType.GP_WIDGET_TOGGLE:
                    var toggleResult = GpWidgetGetValue(widget, out int toggleValue);
                    value = toggleValue == 0 ? "0" : "1";
                    return toggleResult;
                case CameraWidgetType.GP_WIDGET_RANGE:
                    var rangeResult = GpWidgetGetValue(widget, out float rangeValue);
                    value = rangeValue.ToString(CultureInfo.InvariantCulture);
                    return rangeResult;
                case CameraWidgetType.GP_WIDGET_DATE:
                    var dateResult = GpWidgetGetValue(widget, out int dateValue);
                    value = dateValue.ToString(CultureInfo.InvariantCulture);
                    return dateResult;
                default:
                    return GpWidgetGetValue(widget, out value);
            }
        }

        private static bool TryParseBooleanPropertyValue(string value, out int toggleValue) {
            toggleValue = 0;
            if (string.IsNullOrWhiteSpace(value)) {
                return false;
            }

            string normalized = value.Trim();
            if (int.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out toggleValue)) {
                toggleValue = toggleValue == 0 ? 0 : 1;
                return true;
            }

            if (normalized.Equals("true", StringComparison.OrdinalIgnoreCase)
                || normalized.Equals("on", StringComparison.OrdinalIgnoreCase)
                || normalized.Equals("yes", StringComparison.OrdinalIgnoreCase)) {
                toggleValue = 1;
                return true;
            }

            if (normalized.Equals("false", StringComparison.OrdinalIgnoreCase)
                || normalized.Equals("off", StringComparison.OrdinalIgnoreCase)
                || normalized.Equals("no", StringComparison.OrdinalIgnoreCase)) {
                toggleValue = 0;
                return true;
            }

            return false;
        }

        private GP_ERROR_CODE GetPropertyList(string property, out IList<string> list) {
            lock (_gpLock) {
                IntPtr widget = IntPtr.Zero;
                GP_ERROR_CODE err;
                list = [];

                err = GpCameraGetSingleConfig(_camera, property, out widget, _context);
                if (err != GP_ERROR_CODE.GP_OK) {
                    return err;
                }

                try {
                    // Get number of available choices
                    int count = GpWidgetCountChoices(widget);
                    if (count <= 0) {
                        Logger.Warning($"No choices available for {property}");
                        return GP_ERROR_CODE.GP_ERROR_NOT_SUPPORTED;
                    }

                    // Iterate through all choices
                    for (int i = 0; i < count; ++i) {
                        err = GpWidgetGetChoice(widget, i, out var choice);
                        if (err == GP_ERROR_CODE.GP_OK && !string.IsNullOrEmpty(choice)) {
                            list.Add(choice);
                        }
                    }

                    return err;
                } finally {
                    GpWidgetFree(widget);
                }
            } // lock (_gpLock)
        }

        private static string ReadNullTerminatedString(byte[] bytes) {
            if (bytes == null) return string.Empty;
            int len = Array.IndexOf(bytes, (byte)0);
            if (len < 0) len = bytes.Length;
            try {
                return Encoding.UTF8.GetString(bytes, 0, len).Trim();
            } catch {
                return Encoding.ASCII.GetString(bytes, 0, len).Trim();
            }
        }

        private static bool CheckError(GP_ERROR_CODE err, [CallerMemberName] string memberName = "") {
            if (err == GP_ERROR_CODE.GP_OK) {
                return false;
            } else {
                Logger.Error(new Exception(FormatGPhotoError(err)), memberName);
                return true;
            }
        }

        private static void CheckAndThrowError(GP_ERROR_CODE err, [CallerMemberName] string memberName = "") {
            if (err != GP_ERROR_CODE.GP_OK) {
                var ex = new Exception(FormatGPhotoError(err));
                Logger.Error(ex, memberName);
                throw ex;
            }
        }

        private static string FormatGPhotoError(GP_ERROR_CODE err) {
            return $"libgphoto2 camera error occurred: {err}";
        }

        public async Task<bool> Connect(CancellationToken token) {
            return await Task.Run(() => {
                try {
                    lock (_gpLock) {
                        CheckAndThrowError(GpCameraInit(_camera, _context));
                        _cameraExited = false;
                    }

                    if (!Initialize()) {
                        Disconnect();
                        return false;
                    }

                    StartBatteryPolling();

                    Connected = true;
                    RaiseAllPropertiesChanged();

                    return true;
                } catch (Exception ex) {
                    Logger.Error(ex);
                    Disconnect();
                    Notification.ShowExternalError(ex.Message, "libgphoto2 Driver Error");
                    return false;
                }
            });
        }

        private bool _liveViewEnabled;

        public bool LiveViewEnabled {
            get => _liveViewEnabled;
            set {
                _liveViewEnabled = value;
                RaisePropertyChanged();
            }
        }

        public int BitDepth => (int)profileService.ActiveProfile.CameraSettings.BitDepth;

        public bool HasBattery => true;

        public double MirrorLockupDelay {
            get => profileService.ActiveProfile.CameraSettings.MirrorLockupDelay;
            set => profileService.ActiveProfile.CameraSettings.MirrorLockupDelay = value;
        }

        public void StartLiveView(CaptureSequence sequence) {
            throw new NotImplementedException();
        }

        public void StopLiveView() {
            throw new NotImplementedException();
        }

        public Task<IExposureData> DownloadLiveView(CancellationToken token) {
            throw new NotImplementedException();
        }

        public string Action(string actionName, string actionParameters) {
            throw new NotImplementedException();
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

        public void UpdateSubSampleArea() {
            throw new NotImplementedException();
        }

        private void StartBatteryPolling() {
            if (_batteryPolling != null) {
                return; // Already running
            }

            // Poll once per minute
            _batteryPolling = new System.Timers.Timer(TimeSpan.FromMinutes(1));
            _batteryPolling.Elapsed += (sender, e) => {
                // Skip while an exposure is in progress. Reading camera config mid-capture
                // (in particular during an open-shutter bulb exposure) can disturb or fail it.
                var dl = downloadExposure;
                if (dl != null && !dl.Task.IsCompleted) {
                    return;
                }
                GetBatteryLevel();
            };
            _batteryPolling.AutoReset = true;
            _batteryPolling.Start();
            Logger.Info("Battery polling started");
        }

        private void StopBatteryPolling() {
            if (_batteryPolling != null) {
                _batteryPolling.Stop();
                _batteryPolling.Dispose();
                _batteryPolling = null;
                Logger.Info("Battery polling stopped");
            }
        }
    }
}
