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

namespace NINA.Equipment.Equipment.MyCamera {

    public class GPCamera : BaseINPC, ICamera, IDisposable {

        private readonly IntPtr _context;
        private readonly IntPtr _portInfo;
        private readonly IntPtr _camera;

        private readonly string _name;
        private readonly string _path;
        private bool _disposed;
        private System.Timers.Timer _batteryPolling;

        public GPCamera(string name, string path, IProfileService profileService, IExposureDataFactory exposureDataFactory) {
            _disposed = false;
            _name = name;
            _path = path;

            this.profileService = profileService;
            this.exposureDataFactory = exposureDataFactory;
            Id = $"{name}-{path}";

            // Create context
            _context = GpContextNew();

            // Get port info list
            IntPtr portInfoList = IntPtr.Zero;
            if (CheckError(GpPortInfoListNew(ref portInfoList))) {
                Logger.Error($"Failed to create port info list");
                return;
            }

            // Load port list
            if (CheckError(GpPortInfoListLoad(portInfoList))) {
                Logger.Error($"Failed to load port list");
                GpPortInfoListFree(portInfoList);
                return;
            }

            // Look up our path
            var portNumber = GpPortInfoListLookupPath(portInfoList, _path);
            if (portNumber < 0) {
                Logger.Error($"Failed to lookup port path '{_path}': {portNumber}");
                GpPortInfoListFree(portInfoList);
                return;
            }

            // Fetch the corresponding port info
            if (CheckError(GpPortInfoListGetInfo(portInfoList, portNumber, out _portInfo))) {
                Logger.Error($"Failed to get port info at index {portNumber}");
                GpPortInfoListFree(portInfoList);
                return;
            }

            // Create camera
            if (CheckError(GpCameraNew(ref _camera))) {
                Logger.Error("Failed to create camera");
                GpPortInfoListFree(portInfoList);
                return;
            }

            // Set port info
            if (CheckError(GpCameraSetPortInfo(_camera, _portInfo))) {
                Logger.Error($"Failed to set port info");
                GpPortInfoListFree(portInfoList);
                return;
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

        public bool CanShowLiveView {
            get {
                if (Connected) {
                    if (GetProperty("viewfinder", out _) == GP_ERROR_CODE.GP_OK) {
                        return true;
                    } else if (GetProperty("eosremoterelease", out _) == GP_ERROR_CODE.GP_OK) {
                        return true;
                    } else if (GetProperty("liveview", out _) == GP_ERROR_CODE.GP_OK) {
                        return true;
                    }
                }
                return false;
            }
        }

        public string SensorName => string.Empty;

        public SensorType SensorType => SensorType.RGGB;

        public short BayerOffsetX => 0;

        public short BayerOffsetY => 0;

        private (int width, int height) _cameraResolution = (-1, -1);

        public int CameraXSize => _cameraResolution.width;

        public int CameraYSize => _cameraResolution.height;

        public double ExposureMin => this.ShutterSpeeds.Min(v => (double?)v.Value).GetValueOrDefault(0);

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

        public int GainMax => ISOSpeeds.Aggregate((l, r) => l.Value > r.Value ? l : r).Value;

        public int GainMin => ISOSpeeds.Aggregate((l, r) => l.Value < r.Value ? l : r).Value;

        public int Gain {
            get {
                GetProperty("iso", out var iso);

                var translatediso = ISOSpeeds.Where(x => x.Key == iso).FirstOrDefault().Value;

                return translatediso;
            }
            set {
                ValidateMode();
                string iso = ISOSpeeds.Where((x) => x.Value == value).FirstOrDefault().Key;
                if (CheckError(SetProperty("iso", iso))) {
                    Notification.ShowExternalError(Loc.Instance["LblUnableToSetISO"], Loc.Instance["LblCanonDriverError"]);
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
            usesCameraCommandBulb = true;
            ValidateMode();
            GetISOSpeeds();
            GetShutterSpeeds();
            GetBatteryLevel();
            if (!SetRawFormat()) {
                return false;
            }

            _cameraResolution = GetCameraResolution();
            _pixelSizes = GetPixelSizes();

            return true;
        }

        private bool SetRawFormat() {
            if (SetProperty("imageformat", "RAW") == GP_ERROR_CODE.GP_OK) {
                return true;
            } else if (SetProperty("imageformat", "CR3") == GP_ERROR_CODE.GP_OK) {
                return true;
            } else if (SetProperty("imageformat", "CR2") == GP_ERROR_CODE.GP_OK) {
                return true;
            }
            return false;
        }

        /// <summary>
        /// Internal ShutterSpeed Code -> ShutterSpeed Value
        /// e.g.: 0x10 -> 30
        /// </summary>
        private Dictionary<string, double> _shutterSpeeds = new Dictionary<string, double>();
        private Dictionary<string, double> ShutterSpeeds => _shutterSpeeds;

        private void GetShutterSpeeds() {
            ShutterSpeeds.Clear();

            GetPropertyList("shutterspeed", out var list);

            foreach (var prop in list) {
                // Try to parse as double (shutter speeds are in seconds like "1/2000", "1", "0.5", etc.)
                try {
                    // Handle fractions like "1/2000"
                    if (prop.Contains('/')) {
                        var parts = prop.Split('/');
                        if (parts.Length == 2 && double.TryParse(parts[0], out var numerator) &&
                            double.TryParse(parts[1], out var denominator) && denominator != 0) {
                            ShutterSpeeds.Add(prop, numerator / denominator);
                        }
                    } else if (double.TryParse(prop, out double speed)) {
                        ShutterSpeeds.Add(prop, speed);
                    }
                } catch (Exception ex) {
                    Logger.Warning($"Failed to parse shutter speed '{prop}': {ex.Message}");
                }
            }
        }

        private bool IsManualMode() {
            var mode = string.Empty;
            // Check "exposuremode"
            if (GetProperty("exposuremode", out mode) != GP_ERROR_CODE.GP_OK) {
                // Check "autoexposuremode"
                if (GetProperty("autoexposuremode", out mode) != GP_ERROR_CODE.GP_OK) {
                    return false;
                }
            }
            return mode == "M" || mode.Contains("Manual");
        }

        private bool IsBulbMode() {
            // Check if camera is in dedicated Bulb mode (autoexposuremode = "Bulb")
            var mode = string.Empty;
            if (GetProperty("exposuremode", out mode) == GP_ERROR_CODE.GP_OK) {
                if (mode.Equals("Bulb", StringComparison.OrdinalIgnoreCase)) {
                    return true;
                }
            }
            if (GetProperty("autoexposuremode", out mode) == GP_ERROR_CODE.GP_OK) {
                if (mode.Equals("Bulb", StringComparison.OrdinalIgnoreCase)) {
                    return true;
                }
            }

            // Check if camera is in Manual mode with bulb shutter speed
            if (!CheckError(GetProperty("shutterspeed", out var shutterspeed))) {
                if (shutterspeed.Equals("bulb", StringComparison.OrdinalIgnoreCase)) {
                    return true;
                }
            }

            // If bulb mode is not set, try to set it
            if (!CheckError(SetProperty("shutterspeed", "bulb"))) {
                return true;
            }

            return false;
        }

        private Dictionary<string, int> ISOSpeeds = new Dictionary<string, int>();

        private void GetISOSpeeds() {
            ISOSpeeds.Clear();
            Gains.Clear();

            GetPropertyList("iso", out var list);

            foreach (var prop in list) {
                // Try to parse as integer
                try {
                    if (int.TryParse(prop, out var number)) {
                        if (number > 0) {
                            ISOSpeeds.Add(prop, number);
                            Gains.Add(number);
                        }
                    }
                } catch (Exception ex) {
                    Logger.Warning($"Failed to parse iso speed '{prop}': {ex.Message}");
                }
            }
        }

        private void GetBatteryLevel() {
            try {
                if (!CheckError(GetProperty("batterylevel", out string prop))) {
                    // Parse battery level (typically a percentage like "90%")
                    try {
                        // Remove % symbol if present
                        prop = prop.Replace("%", "").Trim();
                        if (int.TryParse(prop, out var level)) {
                            BatteryLevel = level;
                        }
                    } catch (Exception ex) {
                        Logger.Warning($"Failed to parse battery level '{prop}': {ex.Message}");
                        throw;
                    }
                }
            } catch (Exception ex) {
                Logger.Error(ex);
                BatteryLevel = -1;
            }
        }

        public void Disconnect() {
            StopBatteryPolling();
            CheckError(GpCameraExit(_camera, _context));
            Connected = false;
        }

        ~GPCamera() {
            Dispose(false);
        }

        protected virtual void Dispose(bool disposing) {
            if (!_disposed) {
                CheckError(GpCameraFree(_camera));
                GpContextUnref(_context);
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
                // Convert strings to UTF8 byte arrays for P/Invoke
                byte[] folderBytes = Encoding.UTF8.GetBytes(folder + "\0");
                byte[] filenameBytes = Encoding.UTF8.GetBytes(filename + "\0");

                // Create a new file object
                if (GpFileNew(out file) != (int)GP_ERROR_CODE.GP_OK) {
                    Logger.Error("Failed to create new file object");
                    return null;
                }

                // Download file from camera
                var result = GpCameraFileGet(_camera, folderBytes, filenameBytes, CameraFileType.GP_FILE_TYPE_NORMAL, file, _context);
                if (result != (int)GP_ERROR_CODE.GP_OK) {
                    Logger.Error($"Failed to download file {folder}/{filename}: {result}");
                    return null;
                }

                // Get file data
                if (GpFileGetDataAndSize(file, out var dataPtr, out var size) != (int)GP_ERROR_CODE.GP_OK) {
                    Logger.Error("Failed to get file data");
                    return null;
                }

                // Copy unmanaged data to managed byte array
                byte[] fileData = new byte[size];
                Marshal.Copy(dataPtr, fileData, 0, (int)size);

                Logger.Info($"Downloaded {filename} from {folder}, size: {size} bytes");

                // Delete file from camera after successful download
                DeleteFileFromCamera(folder, filename);

                return fileData;
            } catch (Exception ex) {
                Logger.Error($"Exception during DownloadFile: {ex.Message}");
                return null;
            } finally {
                if (file != IntPtr.Zero) {
                    GpFileFree(file);
                }
            }
        }

        private void DeleteFileFromCamera(string folder, string filename) {
            try {
                byte[] folderBytes = Encoding.UTF8.GetBytes(folder + "\0");
                byte[] filenameBytes = Encoding.UTF8.GetBytes(filename + "\0");
                var deleteResult = GpCameraFileDelete(_camera, folderBytes, filenameBytes, _context);
                if (deleteResult != (int)GP_ERROR_CODE.GP_OK) {
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

                        // Determine file type from extension
                        string fileType = "cr2";  // Default to Canon RAW
                        if (_lastCapturedFilename.EndsWith(".cr3", StringComparison.OrdinalIgnoreCase)) {
                            fileType = "cr3";
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
                if (bulbCompletionCTS != null && !bulbCompletionCTS.IsCancellationRequested) {
                    Logger.Debug("libgphoto2: Canceling bulb exposure - releasing shutter");
                    ReleaseShutter();

                    // Wait for the file event to get the image location, then delete it
                    Logger.Debug("libgphoto2: Waiting for file event to delete aborted image...");
                    SetCapturedFileFromEvent(5000);
                    if (!string.IsNullOrEmpty(_lastCapturedFolder) && !string.IsNullOrEmpty(_lastCapturedFilename)) {
                        Logger.Debug($"libgphoto2: Aborted image location: {_lastCapturedFolder}/{_lastCapturedFilename}");
                        DeleteFileFromCamera(_lastCapturedFolder, _lastCapturedFilename);
                    } else {
                        Logger.Warning("Could not get file event for aborted image to delete it");
                    }
                    _lastCapturedFolder = null;
                    _lastCapturedFilename = null;
                }
                bulbCompletionCTS?.Cancel();
            } catch (Exception ex) {
                Logger.Error($"Exception in CancelDownloadExposure: {ex.Message}");
            }
            downloadExposure.TrySetCanceled();
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
                var result = MyMessageBox.Show(
                    Loc.Instance["LblEDCameraNotInManualMode"],
                    Loc.Instance["LblInvalidMode"],
                    System.Windows.MessageBoxButton.OKCancel,
                    System.Windows.MessageBoxResult.OK);
                if (result == System.Windows.MessageBoxResult.OK) {
                    ValidateMode();
                } else {
                    Notification.ShowError("Camera must be in MANUAL or BULB mode");
                    Logger.Error("Camera must be in MANUAL or BULB mode");
                    throw new Exception("Invalid camera mode");
                }
            }
        }

        private void ValidateModeForExposure(double exposureTime) {
            if (!IsManualMode() && !IsBulbMode()) {
                var result = MyMessageBox.Show(
                    Loc.Instance["LblEDCameraNotInManualMode"],
                    Loc.Instance["LblInvalidMode"],
                    System.Windows.MessageBoxButton.OKCancel,
                    System.Windows.MessageBoxResult.OK);
                if (result == System.Windows.MessageBoxResult.OK) {
                    ValidateModeForExposure(exposureTime);
                } else {
                    Notification.ShowError("Camera must be in MANUAL or BULB mode");
                    Logger.Error("Camera must be in MANUAL or BULB mode");
                    throw new Exception("Invalid camera mode for taking exposures");
                }
            }

            if (IsManualMode() && !IsBulbMode()) {
                // Camera is in Manual mode but NOT in bulb shutter speed
                GetShutterSpeeds();
                if (exposureTime <= 30.0) {
                    SetExposureTime(exposureTime);
                } else {
                    // Need bulb mode for exposures > 30s
                    var success = SetExposureTime(double.MaxValue);
                    Logger.Info("CHECKING");
                    if (!success) {
                        Logger.Info("GOING IN THE NON SUCCESS PART");
                        var result = MyMessageBox.Show(
                            Loc.Instance["LblChangeToBulbMode"],
                            Loc.Instance["LblInvalidModeManual"],
                            System.Windows.MessageBoxButton.OKCancel,
                            System.Windows.MessageBoxResult.OK);
                        if (result == System.Windows.MessageBoxResult.OK) {
                            ValidateModeForExposure(exposureTime);
                        } else {
                            Notification.ShowError("Camera must be in MANUAL or BULB mode");
                            Logger.Error("Camera must be in MANUAL or BULB mode");
                            throw new Exception("Invalid camera mode [Manual] for taking bulb exposures");
                        }
                    }
                }
            }

            if (IsBulbMode() && exposureTime < 1.0) {
                var result = MyMessageBox.Show(
                    Loc.Instance["LblChangeToManualMode"],
                    Loc.Instance["LblInvalidModeBulb"],
                    System.Windows.MessageBoxButton.OKCancel,
                    System.Windows.MessageBoxResult.OK);
                if (result == System.Windows.MessageBoxResult.OK) {
                    ValidateModeForExposure(exposureTime);
                } else {
                    Notification.ShowError("Cannot use BULB mode for exposures less than 1s");
                    Logger.Error("Cannot use BULB mode for exposures less than 1s");
                    throw new Exception("Invalid camera mode [Bulb] for taking exposures < 1s");
                }
            }
        }

        private Task bulbCompletionTask = null;
        private CancellationTokenSource bulbCompletionCTS = null;

        public void StartExposure(CaptureSequence sequence) {
            if (downloadExposure?.Task?.Status <= TaskStatus.Running) {
                Logger.Warning("An exposure was still in progress. Cancelling it to start another.");
                CancelDownloadExposure();
            }

            downloadExposure = new TaskCompletionSource<object>();
            var exposureTime = sequence.ExposureTime;
            bool useBulb = (IsManualMode() && exposureTime > 30.0) || (IsBulbMode() && exposureTime >= 1.0);

            ValidateModeForExposure(exposureTime);

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
                var result = GpCameraCapture(_camera, CameraCaptureType.GP_CAPTURE_IMAGE, out var path, _context);
                if (result != GP_ERROR_CODE.GP_OK) {
                    Logger.Error($"Failed to trigger capture: {result}");
                    return (false, null, null);
                }
                string folder = Encoding.UTF8.GetString(path.folder).TrimEnd('\0');
                string filename = Encoding.UTF8.GetString(path.name).TrimEnd('\0');
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

                while (stopwatch.ElapsedMilliseconds < timeoutMs) {
                    var result = GpCameraWaitForEvent(_camera, timeoutMs, out var eventType, out var eventData, _context);

                    if (result == GP_ERROR_CODE.GP_OK && eventType == CameraEventType.GP_EVENT_FILE_ADDED) {
                        // Got the file added event!
                        try {
                            var filePath = (CameraFilePath)Marshal.PtrToStructure(eventData, typeof(CameraFilePath));
                            string folder = Encoding.UTF8.GetString(filePath.folder).TrimEnd('\0');
                            string filename = Encoding.UTF8.GetString(filePath.name).TrimEnd('\0');
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
                    Logger.Debug("libgphoto2: Initiating BULB mode exposure - pressing shutter via eosremoterelease");
                    // For bulb mode, use eosremoterelease to press the shutter button
                    // This will keep the shutter open as long as we don't release it
                    if (CheckError(SetProperty("eosremoterelease", "Immediate"))) {
                        Logger.Error("Failed to initiate bulb exposure via eosremoterelease");
                        return false;
                    }
                    Logger.Debug("libgphoto2: Shutter pressed for bulb exposure");
                    return true;
                } else {
                    Logger.Debug("libgphoto2: Initiating timed exposure");
                    // For timed mode, capture immediately
                    var (success, folder, filename) = TriggerCapture();
                    if (!success) {
                        Logger.Error($"libgphoto2: Error initiating timed exposure");
                        return false;
                    }
                    _lastCapturedFolder = folder;
                    _lastCapturedFilename = filename;
                    // Signal that image is ready for download (for timed exposures)
                    downloadExposure?.TrySetResult(true);
                    return true;
                }
            } catch (Exception ex) {
                Logger.Error($"Exception during SendStartExposureCmd: {ex.Message}");
                return false;
            }
        }

        private void SendStopExposureCmd(bool useBulb) {
            try {
                if (useBulb) {
                    Logger.Debug("libgphoto2: Stopping BULB mode exposure - releasing shutter after timer expired");

                    // Release the shutter button - use "Release Full" as per gphoto2 docs
                    ReleaseShutter();
                    Logger.Debug("libgphoto2: Shutter released, bulb exposure complete");

                    // Per gphoto2 docs, wait for event after release to let camera finalize the capture
                    Logger.Debug("libgphoto2: Waiting for event after shutter release (up to 5s)...");
                    SetCapturedFileFromEvent(5000);

                    downloadExposure?.TrySetResult(true);
                } else {
                    Logger.Debug("libgphoto2: Timed exposure complete");
                }
            } catch (Exception ex) {
                Logger.Error($"Exception during SendStopExposureCmd: {ex.Message}");
            }
        }

        private void ReleaseShutter() {
            try {
                if (CheckError(SetProperty("eosremoterelease", "Release Full"))) {
                    Logger.Warning("Failed to release shutter with 'Release Full', trying numeric value");
                    if (CheckError(SetProperty("eosremoterelease", "0"))) {
                        Logger.Error("Failed to release shutter");
                    }
                }
                Logger.Debug("libgphoto2: Shutter released");
            } catch (Exception ex) {
                Logger.Error($"Exception releasing shutter: {ex.Message}");
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
                var l = new List<double>(ShutterSpeeds.Values);
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
            if (CheckError(SetProperty("shutterspeed", key))) {
                Logger.Error($"Failed to set shutter speed to {key}");
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
            SendStopExposureCmd(false);
        }

        private GP_ERROR_CODE SetProperty(string property, string value) {
            IntPtr widget = IntPtr.Zero;
            GP_ERROR_CODE err;

            // Get widget
            err = GpCameraGetSingleConfig(_camera, property, out widget, _context);
            if (err != GP_ERROR_CODE.GP_OK) {
                return err;
            }

            try {
                // Try to set the value
                err = GpWidgetSetValue(widget, value);
                if (err != GP_ERROR_CODE.GP_OK) {
                    return err;
                }

                // Apply the change
                err = GpCameraSetSingleConfig(_camera, property, widget, _context);
                return err;
            } finally {
                // Free widget
                GpWidgetFree(widget);
            }
        }

        private GP_ERROR_CODE GetProperty(string property, out string data) {
            IntPtr widget = IntPtr.Zero;
            GP_ERROR_CODE err;
            data = string.Empty;

            // Get widget
            err = GpCameraGetSingleConfig(_camera, property, out widget, _context);
            if (err != GP_ERROR_CODE.GP_OK) {
                return err;
            }

            try {
                // Get data
                err = GpWidgetGetValue(widget, out data);
                if (err != GP_ERROR_CODE.GP_OK) {
                    GpWidgetFree(widget);
                    return err;
                }
                return GP_ERROR_CODE.GP_OK;
            } finally {
                // Free widget
                GpWidgetFree(widget);
            }
        }

        private GP_ERROR_CODE GetPropertyList(string property, out IList<string> list) {
            IntPtr widget = IntPtr.Zero;
            GP_ERROR_CODE err;
            list = [];

            // Get widget
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
                // Free widget
                GpWidgetFree(widget);
            }
        }

        private static bool CheckError(GP_ERROR_CODE err, [CallerMemberName] string memberName = "") {
            if (err == GP_ERROR_CODE.GP_OK) {
                return false;
            } else {
                Logger.Error(new Exception(string.Format(Loc.Instance["LblCanonErrorOccurred"], err)), memberName);
                return true;
            }
        }

        private static void CheckAndThrowError(GP_ERROR_CODE err, [CallerMemberName] string memberName = "") {
            if (err != GP_ERROR_CODE.GP_OK) {
                var ex = new Exception(string.Format(Loc.Instance["LblCanonErrorOccurred"], err));
                Logger.Error(ex, memberName);
                throw ex;
            }
        }

        public async Task<bool> Connect(CancellationToken token) {
            return await Task.Run(() => {
                try {
                    CheckAndThrowError(GpCameraInit(_camera, _context));

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
                    Notification.ShowExternalError(ex.Message, Loc.Instance["LblCanonDriverError"]);
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

        private bool usesCameraCommandBulb = true;
        private bool IsManualModeBulb { get; set; } = false;

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

            // Poll every 5 minutes
            _batteryPolling = new System.Timers.Timer(TimeSpan.FromMinutes(1));
            _batteryPolling.Elapsed += (sender, e) => {
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
