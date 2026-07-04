#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Accord;
using CommunityToolkit.Mvvm.Input;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NINA.Astrometry;
using NINA.Core.Interfaces;
using NINA.Core.Locale;
using NINA.Core.Model;
using NINA.Core.Utility;
using NINA.Core.Utility.Notification;
using NINA.Core.Utility.WindowService;
using NINA.Equipment.Equipment.MyGuider.PHD2.PhdEvents;
using NINA.Equipment.Equipment.MyTelescope;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Profile.Interfaces;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Threading;

namespace NINA.Equipment.Equipment.MyGuider.PHD2 {

    public partial class PHD2Guider : BaseINPC, IGuider {

        public PHD2Guider(IProfileService profileService, IWindowServiceFactory windowServiceFactory, ITelescopeMediator telescopeMediator) {
            this.profileService = profileService;
            this.windowServiceFactory = windowServiceFactory;
            this.telescopeMediator = telescopeMediator;
        }

        private readonly IProfileService profileService;
        private readonly IWindowServiceFactory windowServiceFactory;
        private readonly ITelescopeMediator telescopeMediator;
        private TcpClient _client;
        private NetworkStream _stream;
        private StreamReader _reader;
        private StreamWriter _writer;
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private readonly ConcurrentDictionary<string, TaskCompletionSource<JObject>> _pending =
            new(StringComparer.Ordinal);

        private PhdEventVersion _version;

        public string Name => "PHD2";
        public string DisplayName => Name;

        public string Id => "PHD2_Single";

        public PhdEventVersion Version {
            get => _version;
            set {
                _version = value;
                RaisePropertyChanged();
            }
        }

        private ImageSource _image;

        public ImageSource Image {
            get => _image;
            set {
                _image = value;
                RaisePropertyChanged();
            }
        }

        private PhdEventAppState _appState;

        public PhdEventAppState AppState {
            get => _appState;
            set {
                _appState = value;
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(State));
            }
        }

        private bool settling;

        public bool Settling {
            get {
                lock (lockobj) {
                    return settling;
                }
            }
            private set {
                lock (lockobj) {
                    settling = value;
                }
            }
        }

        private PhdEventGuidingDithered _guidingDithered;

        public PhdEventGuidingDithered GuidingDithered {
            get => _guidingDithered;
            set {
                _guidingDithered = value;
                RaisePropertyChanged();
            }
        }

        private CancellationTokenSource _clientCTS;

        private static object lockobj = new object();

        private bool _connected;

        public bool Connected {
            get => _connected;
            private set {
                lock (lockobj) {
                    _connected = value;
                    RaisePropertyChanged();
                }
            }
        }

        private double _pixelScale;

        public double PixelScale {
            get => _pixelScale;
            set {
                _pixelScale = value;
                RaisePropertyChanged();
            }
        }

        public string State => AppState?.State ?? string.Empty;

        public bool HasSetupDialog => !Connected;

        public string Category => "Guiders";

        public string Description => "PHD2 Guider";

        public string DriverInfo => "PHD2 Guider";

        public string DriverVersion => "1.0";

        // _activeProfile represents whatever GetProfile last returned
        private Phd2ProfileResponse _activeProfile;

        private Phd2Profile _selectedProfile;

        public Phd2Profile SelectedProfile {
            get => _selectedProfile;
            set {
                if (value != _selectedProfile) {
                    _selectedProfile = value;
                    RaisePropertyChanged();
                }
            }
        }

        public AsyncObservableCollection<Phd2Profile> AvailableProfiles { get; private set; } = new AsyncObservableCollection<Phd2Profile>();

        private TaskCompletionSource<bool> _tcs;

        private bool initialized = false;

        public async Task<bool> Connect(CancellationToken token) {
            // Get the current telescope info from mediator
            var telescopeInfo = telescopeMediator.GetInfo();
            if (!telescopeInfo.Connected || string.IsNullOrEmpty(telescopeInfo.DeviceId)) {
                Logger.Error("No mount is connected in NINA. Cannot connect guider without a mount.");
                Notification.ShowError(Loc.Instance["LblPhd2NoMountConfigured"] ?? "No mount is connected in NINA");
                return false;
            }

            // Get the current guide camera from profile
            var phd2Camera = profileService.ActiveProfile.GuiderSettings.PHD2Camera;
            var phd2CameraId = profileService.ActiveProfile.GuiderSettings.PHD2CameraId;
            if (string.IsNullOrEmpty(phd2Camera) || phd2Camera == "None") {
                Logger.Error("No guide camera connected in NINA. Cannot connect guider without a camera.");
                Notification.ShowError(Loc.Instance["LblPhd2NoCameraConfigured"] ?? "No guide camera is connected in NINA");
                return false;
            }

            bool connected = false;
            IPHostEntry hostEntry;
            _tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            var serverHost = profileService.ActiveProfile.GuiderSettings.PHD2ServerUrl;
            var serverPort = profileService.ActiveProfile.GuiderSettings.PHD2ServerPort;

            if (string.IsNullOrEmpty(serverHost)) {
                Notification.ShowError(Loc.Instance["LblPhd2ServerHostNotSet"]);
                return connected;
            }

            try {
                hostEntry = DnsHelper.GetIPHostEntryByName(serverHost);
                phd2Ip = hostEntry.AddressList.First();
            } catch (Exception ex) {
                if (ex is SocketException se) {
                    // Error Code 11001 WSAHOST_NOT_FOUND - https://learn.microsoft.com/en-us/windows/win32/winsock/windows-sockets-error-codes-2
                    if (se.ErrorCode == 11001 && IPAddress.TryParse(profileService.ActiveProfile.GuiderSettings.PHD2ServerUrl, out var address)) {
                        phd2Ip = address;
                    }
                }
                Logger.Error($"Failed to resolve PHD2 server {serverHost}: {ex.Message}");
                Notification.ShowError(string.Format(Loc.Instance["LblPhd2ServerHostNotResolved"], serverHost));
                return connected;
            }

            Logger.Info($"Connecting to PHD2 server at {phd2Ip}:{serverPort}");

            // Start PHD2 if we are connecting to an instance on this machine
            if (IPAddress.IsLoopback(phd2Ip)) {
                var startedPHD2 = await StartPHD2Process();

                if (!startedPHD2) {
                    return connected;
                }
            }

            _ = Task.Run(RunListener);

            connected = await _tcs.Task;

            try {
                if (connected) {
                    await GetProfiles();
                    if (profileService.ActiveProfile.GuiderSettings.PHD2ProfileId.HasValue
                        && SelectedProfile?.Id != profileService.ActiveProfile.GuiderSettings.PHD2ProfileId) {
                        await ChangeProfile(profileService.ActiveProfile.GuiderSettings.PHD2ProfileId.Value);
                    }

                    string selectedMount = await GetSelectedMount() ?? "None";

                    // Validate that PHD2 mount matches NINA mount
                    if (!await ValidateMountMatch(telescopeInfo, selectedMount)) {
                        Logger.Warning($"Failed to synchronize mounts: NINA mount is '{telescopeInfo.Name}' but PHD2 has '{selectedMount}' selected");
                        Notification.ShowWarning(Loc.Instance["LblPhd2MountMismatch"] ?? $"Failed to synchronize PHD2 mount with NINA. NINA: '{telescopeInfo.Name}', PHD2: '{selectedMount}'");
                    }

                    string selectedCamera = await GetSelectedCamera() ?? "None";
                    string selectedCameraId = await GetSelectedCameraId();

                    // Set camera if it differs
                    if (!await ValidateCameraMatch(phd2Camera, phd2CameraId, selectedCamera, selectedCameraId)) {
                        Logger.Warning($"Failed to synchronize cameras: NINA camera is '{phd2CameraId} ({phd2Camera})' but PHD2 has '{selectedCameraId} ({selectedCamera})' selected");
                        Notification.ShowWarning(Loc.Instance["LblPhd2CameraMismatch"] ?? $"Failed to synchronize PHD2 camera with NINA. NINA: '{phd2CameraId}', PHD2: '{selectedCameraId}'");
                    }

                    // Fetch bit depth after camera selection may have been changed by ValidateCameraMatch,
                    // so the reading reflects the camera that will actually be used.
                    int selectedCameraDepth = await GetSelectedCameraBitDepth();

                    // Set camera bit depth
                    int bitDepth = profileService.ActiveProfile.GuiderSettings.PHD2CameraDepth;
                    bool bitDepthChanged = selectedCameraDepth != bitDepth;
                    if (!await ValidateCameraBitDepth(bitDepth, selectedCameraDepth)) {
                        Logger.Warning($"Failed to synchronize bit depth to {bitDepth} bit. Currently: {selectedCameraDepth} bit");
                        Notification.ShowWarning(Loc.Instance["LblPhd2DepthMismatch"] ?? $"Failed to set PHD2 camera to {bitDepth} bit");
                    } else if (bitDepthChanged && await IsPHD2EquipmentConnected()) {
                        // set_camera_bitdepth only writes the PHD2 profile; the new depth takes effect
                        // on the next camera connect. If the equipment is already connected, disconnect
                        // it here so EnsurePHD2EquipmentConnected below reconnects at the new depth.
                        Logger.Info($"PHD2 camera bit depth changed to {bitDepth} bit; reconnecting equipment so it takes effect");
                        await DisconnectPHD2Equipment();
                    }

                    // PHD2's auto-restore calibration fires only at equipment connect time (gear_dialog.cpp).
                    // Push auto_restore=true BEFORE connecting so PHD2 loads calibration at the connect.
                    // If equipment is already up but calibration isn't in memory, disconnect first so
                    // EnsurePHD2EquipmentConnected below re-connects and triggers the load.
                    if (profileService.ActiveProfile.GuiderSettings.PHD2AutoRestoreCalibration == true) {
                        Logger.Info("PHD2 - Pre-setting auto restore calibration before equipment connect");
                        var preMsg = new Phd2SetAutoRestoreCalibration() { Parameters = new object[] { true } };
                        var preResp = await SendMessage(preMsg);
                        if (preResp.error != null)
                            Logger.Warning($"PHD2 - Failed to pre-set auto restore calibration: {preResp.error.message}");

                        if (await IsPHD2EquipmentConnected() && !await IsCalibrated()) {
                            Logger.Info("PHD2 - Equipment connected but calibration not loaded; reconnecting to trigger auto-restore");
                            await DisconnectPHD2Equipment();
                        }
                    }

                    await EnsurePHD2EquipmentConnected();
                    await ApplyPhd2ProfileSettings();
                    await TryRefreshShiftLockParams();
                    await SetPixelScale();
                    initialized = true;
                }

            } catch (OperationCanceledException) {
            } catch (Exception ex) {
                Logger.Error(ex);
                Notification.ShowError(ex.Message);
            }

            return connected;
        }

        public Task SetPixelScale() {
            return Task.Run(async () => {
                try {
                    var msg = new Phd2GetPixelScale();
                    var resp = await SendMessage(msg);
                    if (resp.result != null) {
                        PixelScale = double.Parse(resp.result.ToString().Replace(",", "."), CultureInfo.InvariantCulture);
                    }
                } catch (Exception ex) {
                    Logger.Error(ex);
                }
            });
        }

        [RelayCommand]
        private async Task<bool> ProfileSelectionChanged() {
            if (SelectedProfile == null) {
                Logger.Error("No profile selected");
                return false;
            }

            if (SelectedProfile.Id == _activeProfile?.id) {
                return true;
            }

            return await ChangeProfile(SelectedProfile.Id);
        }

        private async Task<bool> ChangeProfile(int id) {
            // Trigger a GetProfiles operation in the background after either a success or failure, which will refresh the profile list and
            // set both SelectedProfile and _activeProfile to their latest values
            var targetProfile = AvailableProfiles.FirstOrDefault(x => x.Id == id);
            if (targetProfile == null) {
                Logger.Error($"PHD2 profile {id} could not be found");
                await GetProfiles();
                Notification.ShowWarning(String.Format(Loc.Instance["LblPhd2ProfileNotFound"], id, _activeProfile?.name));
                // Clear the saved id so we don't try and restore the missing profile next time
                profileService.ActiveProfile.GuiderSettings.PHD2ProfileId = null;
                return false;
            }

            await DisconnectPHD2Equipment();
            var setProfile = new Phd2SetProfile() { Parameters = new int[] { id } };
            var setProfileResponse = await SendMessage(setProfile);
            if (setProfileResponse.error != null) {
                Logger.Error($"Failed SetProfile({id}): {setProfileResponse.error}");
                Notification.ShowWarning(Loc.Instance["LblPhd2ProfileChangeFailed"]);
                await GetProfiles();
                return false;
            }

            profileService.ActiveProfile.GuiderSettings.PHD2ProfileId = id;
            await EnsurePHD2EquipmentConnected();
            await GetProfiles();
            return true;
        }

        public async Task<bool> Dither(IProgress<ApplicationStatus> progress, CancellationToken ct) {
            if (Connected) {
                var state = await GetAppState();
                if (state != PhdAppState.GUIDING) {
                    if (state == PhdAppState.LOSTLOCK) {
                        Notification.ShowWarning(Loc.Instance["LblDitherSkippedBecauseNotLostLock"]);
                    } else {
                        Notification.ShowWarning(Loc.Instance["LblDitherSkippedBecauseNotGuiding"]);
                    }

                    return false;
                }

                await WaitForSettling(progress, ct);

                var ditherMsg = new Phd2Dither() {
                    Parameters = new Phd2DitherParameter() {
                        Amount = profileService.ActiveProfile.GuiderSettings.DitherPixels,
                        RaOnly = profileService.ActiveProfile.GuiderSettings.DitherRAOnly,
                        Settle = new Phd2Settle() {
                            Pixels = profileService.ActiveProfile.GuiderSettings.SettlePixels,
                            Time = profileService.ActiveProfile.GuiderSettings.SettleTime,
                            Timeout = profileService.ActiveProfile.GuiderSettings.SettleTimeout
                        }
                    }
                };

                var ditherMsgResponse = await SendMessage(ditherMsg);
                if (ditherMsgResponse.error != null) {
                    /* Dither failed */
                    return false;
                }
                Settling = true;
                await WaitForSettling(progress, ct);
            }
            return true;
        }

        private async Task WaitForSettling(IProgress<ApplicationStatus> progress, CancellationToken ct) {
            try {
                await Task.Run<bool>(async () => {
                    var elapsed = new TimeSpan();
                    while (Settling == true) {
                        progress?.Report(new ApplicationStatus { Status = Loc.Instance["LblPHD2Settling"] });
                        elapsed += await CoreUtil.Delay(500, ct);

                        var timeout = profileService.ActiveProfile.GuiderSettings.SettleTimeout;
                        if (elapsed.TotalSeconds > (timeout + 10)) {
                            //Failsafe when phd is not sending settlingdone message
                            Notification.ShowWarning(string.Format(Loc.Instance["LblGuiderNoSettleDone"], timeout));
                            Logger.Warning($"Phd2 - Guider did not send SettleDone message in expected time  ({timeout}s + 10s). Skipping.");
                            Settling = false;
                        }
                    }
                    return true;
                });
            } catch (OperationCanceledException) {
                Settling = false;
            } finally {
                progress?.Report(new ApplicationStatus { Status = string.Empty });
            }
        }

        public async Task<bool> Pause(bool pause, CancellationToken ct) {
            if (Connected) {
                var msg = new Phd2Pause() { Parameters = new bool[] { pause } };
                await SendMessage(msg);

                if (pause) {
                    var elapsed = new TimeSpan();
                    while (!(AppState.State == PhdAppState.PAUSED)) {
                        elapsed += await CoreUtil.Delay(500, ct);
                    }
                } else {
                    var elapsed = new TimeSpan();
                    while ((AppState.State == PhdAppState.PAUSED)) {
                        elapsed += await CoreUtil.Delay(500, ct);
                        if (elapsed.TotalSeconds > 60) {
                            //Failsafe when phd is not sending resume message
                            Notification.ShowWarning(Loc.Instance["LblGuiderNoResume"]);
                            break;
                        }
                    }
                }
            }
            return true;
        }

        private static void CheckPhdError(PhdMethodResponse m) {
            if (m.error != null) {
                Notification.ShowError(String.Format(Loc.Instance["LblPHDError"], m.error.message, m.error.code));
                Logger.Warning("PHDError: " + m.error.message + " CODE: " + m.error.code);
            }
        }

        public async Task<bool> AutoSelectGuideStar() {
            if (Connected) {
                var state = await GetAppState();
                if (state != PhdAppState.LOOPING) {
                    var loopMsg = new Phd2Loop();
                    await SendMessage(loopMsg);
                    await Task.Delay(TimeSpan.FromSeconds(5));
                }

                // Wait for at least one exposure to finish
                var exposureDurationResponse = await SendMessage<GetExposureResponse>(new Phd2GetExposure());
                var durationMs = exposureDurationResponse.result;
                await Task.Delay(TimeSpan.FromMilliseconds(durationMs + 1000));

                var findStarMsg = new Phd2FindStar() {
                    Parameters = new Phd2FindStarParameter() {
                        Roi = await GetROI()
                    }
                };

                await SendMessage(findStarMsg);

                return true;
            }
            return false;
        }

        private async Task<int[]> GetROI() {
            if (profileService.ActiveProfile.GuiderSettings.PHD2ROIPct < 100) {
                var cameraSize = new Phd2GetCameraFrameSize();
                var size = await SendMessage<GetCameraFrameSizeResponse>(cameraSize);
                if (size.result.Length == 2) {
                    int width = size.result[0];
                    int height = size.result[1];
                    double pct = profileService.ActiveProfile.GuiderSettings.PHD2ROIPct / 100d;

                    int halfWidth = width / 2;
                    int halfHeight = height / 2;

                    int roiX = (int)(halfWidth - halfWidth * pct);
                    int roiY = (int)(halfHeight - halfHeight * pct);
                    int roiWidth = (int)(width * pct);
                    int roiHeight = (int)(height * pct);

                    return [roiX, roiY, roiWidth, roiHeight];
                }
            }
            return null;
        }

        public async Task<LockPosition> GetLockPosition() {
            return await GetLockPositionInternal(5000);
        }

        private async Task<LockPosition> GetLockPositionInternal(
            int receiveTimeout = 0) {
            var msg = new Phd2GetLockPosition();
            var lockPositionResponse = await SendMessage<GetLockPositionResponse>(
                msg,
                receiveTimeout);
            if (lockPositionResponse?.result != null && lockPositionResponse.result.Length == 2) {
                return new LockPosition(lockPositionResponse.result[0], lockPositionResponse.result[1]);
            }
            return null;
        }

        private async Task<string> GetAppState(
            int receiveTimeout = 0) {
            var msg = new Phd2GetAppState();
            var appStateResponse = await SendMessage(
                msg,
                receiveTimeout);
            return appStateResponse?.result?.ToString();
        }

        private async Task<bool> IsCalibrated() {
            var msg = new Phd2GetCalibrated();
            var response = await SendMessage<BooleanPhdMethodResponse>(msg, 5000);

            return response?.result ?? false;
        }

        private Task<bool> WaitForAppState(
            string targetState,
            CancellationToken ct,
            int receiveTimeout = 0) {
            return Task.Run(async () => {
                try {
                    var state = await GetAppState();
                    while (state != targetState) {
                        await Task.Delay(1000, ct);
                        state = await GetAppState();
                    }
                    return true;
                } catch (OperationCanceledException) {
                    return false;
                }
            });
        }

        public Task<bool> StartGuiding(bool forceCalibration, IProgress<ApplicationStatus> progress, CancellationToken ct) {
            return StartGuidingPrivate(forceCalibration, true, progress, ct);
        }

        private async Task<bool> StartGuidingPrivate(bool forceCalibration, bool waitForSettle, IProgress<ApplicationStatus> progress, CancellationToken ct) {
            if (!Connected)
                return false;

            string state = await GetAppState();
            if (state == PhdAppState.GUIDING) {
                Logger.Info("Phd2 - App is already guiding. Skipping start guiding");
                return true;
            }

            if (state == PhdAppState.LOSTLOCK) {
                Logger.Info("Phd2 - App has lost guide star and needs to stop before starting guiding again");
                await StopGuiding(ct);
            }

            if (state == PhdAppState.CALIBRATING) {
                Logger.Info("Phd2 - App is already calibrating. Waiting for calibration to finish");
                await WaitForCalibrationFinished(progress, ct);
            }

            var isCalibrated = forceCalibration ? false : await IsCalibrated();

            int retries = 1;
            int maxRetries = profileService.ActiveProfile.GuiderSettings.AutoRetryStartGuiding ? 3 : 1;
            var retryAfterSeconds = TimeSpan.FromSeconds(profileService.ActiveProfile.GuiderSettings.AutoRetryStartGuidingTimeoutSeconds);
            while (!ct.IsCancellationRequested) {
                if (!await TryStartGuideCommand(forceCalibration, progress, ct)) {
                    return false;
                }

                var starSelected = await WaitForStarSelected(progress, ct);
                if (starSelected) {
                    if (!isCalibrated) {
                        await Task.Delay(5000, ct);
                        await WaitForCalibrationFinished(progress, ct);
                    }

                    using var cancelOnTimeoutOrParent = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    var timeout = Task.Delay(
                        retryAfterSeconds,
                        cancelOnTimeoutOrParent.Token);
                    var guidingHasBegun = WaitForGuidingStarted(progress, cancelOnTimeoutOrParent.Token);

                    if ((await Task.WhenAny(timeout, guidingHasBegun)) == guidingHasBegun) {
                        // Guiding has been started successfully in time
                        // Wait for phd2 to settle and exit
                        if (waitForSettle) {
                            await WaitForSettling(progress, ct);
                        }
                        return true;
                    }
                    try { cancelOnTimeoutOrParent?.Cancel(); } catch { }
                }
                retries += 1;

                if (retries > maxRetries) {
                    // Max number of unsuccessful retries exceeded. Exit.
                    Logger.Warning($"Phd2 - Start guiding has failed after {maxRetries} retries");
                    return false;
                }

                Logger.Warning($"Phd2 - Start guiding has timed out after {retryAfterSeconds.TotalSeconds}s. Retrying to start guiding. Attempt {retries} / {maxRetries}");
                progress?.Report(new ApplicationStatus { Status = Loc.Instance["LblStartGuiding"], Status2 = Loc.Instance["LblPHD2StartGuidingTimeoutRetry"], Progress2 = retries, MaxProgress2 = maxRetries, ProgressType2 = ApplicationStatus.StatusProgressType.ValueOfMaxValue });

                await Task.Delay(1000, ct); // 1000ms sleep between retries

                await StopGuiding(ct); // used to visual inspect that the guider is in the stopped state before retrying.

                await Task.Delay(5000, ct); // 5000ms sleep between retries
            }
            return false;
        }

        private Task RestartForLostShiftLock() {
            return Task.Run(async () => {
                await this.StopGuiding(CancellationToken.None);

                // Don't wait for settling when restarting due to lost lock shift, which should minimize downtime
                if (!await StartGuidingPrivate(false, false, null, CancellationToken.None)) {
                    Notification.ShowError(Loc.Instance["LblRestartGuidingAfterLostShiftLockFailed"]);
                    Logger.Error("Failed to restart guiding after lost shift lock");
                    return;
                }

                if (!await SetShiftRate(ShiftRate, CancellationToken.None)) {
                    Notification.ShowError(Loc.Instance["LblPhd2GuiderRestartShiftLockFailed"]);
                    Logger.Error("Failed to set shift rate after lost shift lock");
                } else {
                    Notification.ShowInformation(Loc.Instance["LblPhd2GuiderRestartShiftLockSuccess"]);
                    Logger.Info("Successfully restarted shift lock after losing it");
                }
            });
        }

        private async Task<bool> WaitForStarSelected(IProgress<ApplicationStatus> progress, CancellationToken ct) {
            var lockPos = await GetLockPositionInternal(5000);
            if (lockPos == null) {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var timeoutTime = TimeSpan.FromSeconds(30);
                timeoutCts.CancelAfter(timeoutTime);
                try {
                    while (lockPos == null) {
                        await Task.Delay(1000, timeoutCts.Token);
                        lockPos = await GetLockPositionInternal(5000);
                    }
                    return true;
                } catch (OperationCanceledException) {
                    if (ct.IsCancellationRequested) {
                        throw;
                    } else {
                        //After {timeoutTime.TotalSeconds} the state is still in looping or stopped state, so selecting a guide star has failed
                        Logger.Error($"Failed to select guide star after {timeoutTime.TotalSeconds} seconds");
                        return false;
                    }
                }
            }
            return true;
        }

        private async Task WaitForCalibrationFinished(IProgress<ApplicationStatus> progress, CancellationToken ct) {
            string state = await GetAppState(); ;
            while (state == PhdAppState.CALIBRATING) {
                progress?.Report(new ApplicationStatus { Status = Loc.Instance["LblStartGuiding"], Status2 = Loc.Instance["LblPHD2Calibrating"] });
                state = await GetAppState();
                await Task.Delay(1000, ct);
            }
        }

        private async Task<bool> TryStartGuideCommand(bool forceCalibration, IProgress<ApplicationStatus> progress, CancellationToken ct) {
            await WaitForSettling(progress, ct);

            var guideMsg = new Phd2Guide() {
                Parameters = new Phd2GuideParameter() {
                    Settle = new Phd2Settle() {
                        Pixels = profileService.ActiveProfile.GuiderSettings.SettlePixels,
                        Time = profileService.ActiveProfile.GuiderSettings.SettleTime,
                        Timeout = profileService.ActiveProfile.GuiderSettings.SettleTimeout
                    },
                    Recalibrate = forceCalibration,
                    Roi = await GetROI()
                }
            };

            Logger.Info($"Phd2 - Requesting to start guiding. Recalibrate: {forceCalibration}");

            var guideMsgResponse = await SendMessage(guideMsg);
            if (guideMsgResponse.error == null) {
                await TryRefreshShiftLockParams();
                return true;
            }
            return false;
        }

        private async Task<bool> TryRefreshShiftLockParams() {
            var getShiftLockParamsMsg = new Phd2GetLockShiftParams();
            Logger.Trace($"Phd2 - Requesting shift lock params");

            try {
                var getShiftLockParamsResponse = await SendMessage<GetLockShiftParamsResponse>(getShiftLockParamsMsg);
                if (getShiftLockParamsResponse.error != null) {
                    Logger.Error($"Failed to get shift lock params. Code={getShiftLockParamsResponse.error.code}, Message={getShiftLockParamsResponse.error.message}");
                    return false;
                }

                var result = getShiftLockParamsResponse.result;
                if (result.Enabled) {
                    if (result.Units == "pixels/hr") {
                        var raShiftRate = result.Rate[0] * PixelScale / 3600.0d;
                        var decShiftRate = result.Rate[1] * PixelScale / 3600.0d;
                        ShiftRate = SiderealShiftTrackingRate.Create(raShiftRate, decShiftRate);
                    } else {
                        // already arcsec/hr, convert to deg/hr
                        var raShiftRate = result.Rate[0] / 3600.0d;
                        var decShiftRate = result.Rate[1] / 3600.0d;
                        ShiftRate = SiderealShiftTrackingRate.Create(raShiftRate, decShiftRate);
                    }
                    ShiftRateAxis = result.Axes;
                    ShiftEnabled = true;
                } else {
                    ShiftEnabled = false;
                }
                return true;
            } catch (Exception e) {
                ShiftEnabled = false;
                Logger.Error("Failed to get shift lock parameters", e);
                return false;
            }
        }

        private async Task<bool> WaitForGuidingStarted(IProgress<ApplicationStatus> progress, CancellationToken ct) {
            if (await WaitForAppState(PhdAppState.GUIDING, ct)) {
                progress?.Report(new ApplicationStatus { Status = Loc.Instance["LblStartGuiding"], Status2 = Loc.Instance["LblPHD2StartGuiding"] });
                Settling = true;
                return true;
            } else {
                return false;
            }
        }

        public async Task<bool> StopGuiding(CancellationToken token) {
            if (!Connected) {
                return false;
            }
            try {
                string state = await GetAppState(3000);
                if (state == PhdAppState.STOPPED) {
                    Logger.Info($"Phd2 - Stop Guiding skipped, as the app is already in state {state}");
                    return false;
                }
                return await StopCapture(token);
            } catch (IOException ee) // communication error with phd2
              {
                Logger.Error(ee);
                return false;
            }
        }

        private async Task<bool> StopCapture(CancellationToken token) {
            if (!Connected) {
                return false;
            }
            var stopCapture = new Phd2StopCapture();
            var stopCaptureResult = await SendMessage(
                stopCapture,
                10000); // triage: reported deadlock hanging of phd2+nina - 10s timeout

            if (stopCaptureResult == null || stopCaptureResult.error != null) {
                return false;
            }

            return await WaitForAppState(
                PhdAppState.STOPPED,
                token,
                10000);  // triage: reported deadlock hanging of phd2+nina - 10s timeout
        }

        public bool CanClearCalibration => true;

        public bool CanSetShiftRate => true;

        public bool CanGetLockPosition => true;

        private bool shiftEnabled;
        public bool ShiftEnabled {
            get => shiftEnabled;
            private set {
                shiftEnabled = value;
                RaisePropertyChanged();
            }
        }

        private SiderealShiftTrackingRate shiftRate = SiderealShiftTrackingRate.Disabled;
        public SiderealShiftTrackingRate ShiftRate {
            get => shiftRate;
            private set {
                shiftRate = value;
                RaisePropertyChanged();
            }
        }

        private string shiftRateAxis;
        private IPAddress phd2Ip;

        public string ShiftRateAxis {
            get => shiftRateAxis;
            private set {
                shiftRateAxis = value;
                RaisePropertyChanged();
            }
        }

        public async Task<bool> ClearCalibration(CancellationToken ct) {
            if (Connected) {
                var clearMessage = new Phd2ClearCalibration() {
                    Parameters = new string[] { "Both" }
                };
                var clearGuidance = await SendMessage(clearMessage, 10000);

                if (clearGuidance == null || clearGuidance.error != null) {
                    return false;
                }

                await Task.Delay(100, ct); // give time for PHD2 to clear the guidance
            }
            return true;
        }

        public Task<GenericPhdMethodResponse> SendMessage(Phd2Method msg, int receiveTimeout = 60000) {
            return SendMessage<GenericPhdMethodResponse>(msg, receiveTimeout);
        }

        public async Task<T> SendMessage<T>(Phd2Method msg, int receiveTimeout = 60000) where T : PhdMethodResponse {
            if (!Connected || _writer == null) {
                return MakeGenericError<T>(msg, "Not connected to PHD2");
            }

            var idKey = msg.Id ?? string.Empty;
            if (string.IsNullOrWhiteSpace(idKey)) {
                return MakeGenericError<T>(msg, "Invalid message id");
            }

            if (receiveTimeout <= 0) {
                receiveTimeout = 60000;
            }

            var tcs = new TaskCompletionSource<JObject>(TaskCreationOptions.RunContinuationsAsynchronously);

            // If a duplicate is pending, fail fast (shouldn't happen in normal use)
            if (!_pending.TryAdd(idKey, tcs)) {
                return MakeGenericError<T>(msg, $"A request with id '{idKey}' is already pending");
            }

            try {
                var serialized = JsonConvert.SerializeObject(
                    msg,
                    new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });

                Logger.Debug($"Phd2 - Sending message '{serialized}'");

                // Ensure line-delimited JSON over the single connection
                await _writeLock.WaitAsync().ConfigureAwait(false);
                try {
                    await _writer.WriteLineAsync(serialized).ConfigureAwait(false);
                    await _writer.FlushAsync().ConfigureAwait(false);
                } finally {
                    _writeLock.Release();
                }

                // Timeout handling
                using var timeoutCts = new CancellationTokenSource(receiveTimeout);
                using (timeoutCts.Token.Register(() => tcs.TrySetCanceled(timeoutCts.Token), useSynchronizationContext: false)) {
                    JObject responseObj;
                    try {
                        responseObj = await tcs.Task.ConfigureAwait(false);
                    } catch (OperationCanceledException) {
                        return MakeGenericError<T>(msg, "Timed out waiting for response from PHD2");
                    }

                    Logger.Debug($"Phd2 - Received message answer '{responseObj}'");

                    var response = responseObj.ToObject<T>();
                    CheckPhdError(response);
                    return response;
                }
            } catch (Exception ex) {
                Logger.Error("Phd2 error while sending message", ex);
                return MakeGenericError<T>(msg, "Unable to get response from PHD2");
            } finally {
                _pending.TryRemove(idKey, out _);
            }
        }

        private static T MakeGenericError<T>(Phd2Method msg, string message) where T : PhdMethodResponse {
            var err = (T)Activator.CreateInstance(typeof(T));
            err.id = msg.Id.ToString();
            err.error = new PhdError { code = -1, message = message };
            return err;
        }

        public async Task<bool> SetShiftRate(SiderealShiftTrackingRate shiftTrackingRate, CancellationToken ct) {
            if (!shiftTrackingRate.Enabled) {
                return await StopShifting(ct);
            }

            ShiftRate = shiftTrackingRate;
            double raArcsecPerHour = shiftTrackingRate.RAArcsecsPerHour;
            double decArcsecPerHour = shiftTrackingRate.DecArcsecsPerHour;
            Logger.Info($"Setting shift rate to RA={raArcsecPerHour}, Dec={decArcsecPerHour}");
            try {
                var setLockShiftMsg = new Phd2SetLockShiftParams() {
                    Parameters = new Phd2SetLockShiftParamsParameter() {
                        Axes = "RA/Dec",
                        Units = "arcsec/hr",
                        Rate = [raArcsecPerHour, decArcsecPerHour]
                    }
                };
                var lockShiftResponse = await SendMessage(setLockShiftMsg);
                if (lockShiftResponse.error != null) {
                    Logger.Error($"Failed to set shift rate to RA={raArcsecPerHour}, Dec={decArcsecPerHour}. Code={lockShiftResponse.error.code}, Message={lockShiftResponse.error.message}");
                    return false;
                }

                var setLockShiftEnabledMsg = new Phd2SetLockShiftEnabled() {
                    Parameters = new bool[] { true }
                };
                var setLockShiftEnabledResponse = await SendMessage(setLockShiftEnabledMsg);
                if (setLockShiftEnabledResponse.error != null) {
                    Logger.Error($"Failed to enable lock shift. Code={lockShiftResponse.error.code}, Message={lockShiftResponse.error.message}");
                    return false;
                }

                _ = TryRefreshShiftLockParams();
                return true;
            } catch (Exception e) {
                Logger.Error("Failed to set shift rate", e);
                return false;
            }
        }

        public async Task<bool> StopShifting(CancellationToken ct) {
            Logger.Info($"Stop shifting");
            try {
                if (!Connected || !ShiftEnabled) {
                    return true;
                }

                var setLockShiftEnabledMsg = new Phd2SetLockShiftEnabled() {
                    Parameters = new bool[] { false }
                };
                var setLockShiftEnabledResponse = await SendMessage(setLockShiftEnabledMsg);
                if (setLockShiftEnabledResponse.error != null) {
                    Logger.Error($"Failed to disable lock shift. Code={setLockShiftEnabledResponse.error.code}, Message={setLockShiftEnabledResponse.error.message}");
                    return false;
                }

                _ = TryRefreshShiftLockParams();
                return true;
            } catch (Exception e) {
                Logger.Error("Failed to disable shift", e);
                return false;
            }
        }

        public void Disconnect() {
            initialized = false;
            phd2Ip = null;
            try { _clientCTS?.Cancel(); } catch { }
            try { _client?.Close(); } catch { }
        }

        private async Task ProcessEvent(string phdevent, JObject message) {
            switch (phdevent) {
                case "Resumed": {
                        break;
                    }
                case "Version": {
                        Version = message.ToObject<PhdEventVersion>();
                        break;
                    }
                case "AppState": {
                        AppState = message.ToObject<PhdEventAppState>();
                        break;
                    }
                case "GuideStep": {
                        AppState = new PhdEventAppState() { State = "Guiding" };
                        var step = message.ToObject<PhdEventGuideStep>();
                        GuideEvent?.Invoke(this, step);
                        break;
                    }
                case "GuidingDithered": {
                        GuidingDithered = message.ToObject<PhdEventGuidingDithered>();
                        break;
                    }
                case "Settling": {
                        var settleInfo = message.ToObject<PhdEventSettling>();
                        Settling = true;
                        Logger.Debug($"PHD2 settling started. Time: {settleInfo.Time}, Distance: {settleInfo.Distance}");
                        break;
                    }
                case "SettleDone": {
                        GuidingDithered = null;
                        Settling = false;
                        var settleDone = message.ToObject<PhdEventSettleDone>();
                        if (settleDone.Error != null) {
                            Logger.Error("PHD2 error:" + settleDone.Error);
                            Notification.ShowExternalWarning(settleDone.Error, Loc.Instance["LblPhd2Warning"]);
                        } else {
                            Logger.Debug("PHD2 settle completed");
                        }
                        break;
                    }
                case "Paused": {
                        AppState = new PhdEventAppState() { State = "Paused" };
                        break;
                    }
                case "StartCalibration": {
                        AppState = new PhdEventAppState() { State = "Calibrating" };
                        break;
                    }
                case "LoopingExposures": {
                        AppState = new PhdEventAppState() { State = "Looping" };
                        break;
                    }
                case "LoopingExposuresStopped": {
                        AppState = new PhdEventAppState() { State = "Stopped" };
                        break;
                    }
                case "CalibrationComplete": {
                        break;
                    }
                case "StarSelected": {
                        Logger.Debug($"PHD2 - Star selected");
                        break;
                    }
                case "StarLost": {
                        var starlost = message.ToObject<PhdEventStarLost>();
                        Logger.Debug($"PHD2 - Star lost! Status: {starlost.Status}");
                        AppState = new PhdEventAppState() { State = "LostLock" };
                        break;
                    }
                case "StartGuiding": {
                        break;
                    }
                case "LockPositionSet": {
                        var lockPosition = message.ToObject<PhdEventLockPositionSet>();
                        Logger.Debug($"PHD2 - Lock position set at x:{lockPosition.X} y:{lockPosition.Y}");
                        break;
                    }
                case "LockPositionLost": {
                        Logger.Debug($"PHD2 - Lock position lost!");
                        AppState = new PhdEventAppState() { State = "LostLock" };
                        break;
                    }
                case "LockPositionShiftLimitReached": {
                        Logger.Debug($"PHD2 - LockPositionShiftLimitReached!");
                        _ = RestartForLostShiftLock();
                        break;
                    }
                case "ConfigurationChange": {
                        if (initialized) {
                            Logger.Debug($"PHD2 - ConfigurationChange!");
                            _ = SetPixelScale();
                        }
                        break;
                    }
                case "Alert": {
                        var alert = message.ToObject<PhdEventAlert>();
                        var msg = $"PHD2: {alert.Msg}";
                        Logger.Warning($"PHD2 - Alert ({alert.Type}): {alert.Msg}");
                        switch (alert.Type) {
                            case "error":
                                Notification.ShowError(msg);
                                break;
                            case "warning":
                                Notification.ShowWarning(msg);
                                break;
                            default:
                                Notification.ShowInformation(msg);
                                break;
                        }
                        break;
                    }
                default: {
                        break;
                    }
            }
        }

        private static TcpState GetState(TcpClient tcpClient) {
            var foo = IPGlobalProperties.GetIPGlobalProperties()
              .GetActiveTcpConnections()
              .SingleOrDefault(x => x.LocalEndPoint.Equals(tcpClient.Client.LocalEndPoint));
            return foo != null ? foo.State : TcpState.Unknown;
        }

        private async Task ApplyPhd2ProfileSettings() {
            var s = profileService.ActiveProfile.GuiderSettings;
            bool anyStored = s.PHD2RAMinMove.HasValue || s.PHD2DecMinMove.HasValue
                || s.PHD2RAAggressiveness.HasValue || s.PHD2DecAggressiveness.HasValue
                || s.PHD2RAHysteresis.HasValue || s.PHD2DecFastSwitch.HasValue
                || !string.IsNullOrEmpty(s.PHD2DecGuideMode) || s.PHD2ExposureMs.HasValue
                || s.PHD2CalibrationStepMs.HasValue || s.PHD2CalibrationDistancePx.HasValue || s.PHD2SearchRegion.HasValue
                || s.PHD2MaxRADuration.HasValue || s.PHD2MaxDecDuration.HasValue
                || !string.IsNullOrEmpty(s.PHD2GuideAlgorithmRA) || !string.IsNullOrEmpty(s.PHD2GuideAlgorithmDec)
                || s.PHD2DitherScale.HasValue || s.PHD2DitherRAOnly.HasValue || !string.IsNullOrEmpty(s.PHD2DitherMode)
                || s.PHD2NoiseReductionMethod.HasValue || s.PHD2CameraGain.HasValue
                || s.PHD2CameraBinning.HasValue || s.PHD2UseSubframes.HasValue
                || s.PHD2FocalLength.HasValue || s.PHD2AutoRestoreCalibration.HasValue
                || s.PHD2AssumeDecOrthogonal.HasValue || s.PHD2UseDecCompensation.HasValue
                || s.PHD2ReverseDecOnFlip.HasValue || s.PHD2FastRecenter.HasValue
                || s.PHD2MinStarHFD.HasValue || s.PHD2MaxStarHFD.HasValue
                || s.PHD2BeepForLostStar.HasValue || s.PHD2MassChangeThresholdEnabled.HasValue
                || s.PHD2MassChangeThreshold.HasValue || s.PHD2UseMultipleStars.HasValue
                || s.PHD2TimeLapseMs.HasValue || s.PHD2VarDelayEnabled.HasValue
                || s.PHD2VarDelayShortSec.HasValue || s.PHD2VarDelayLongSec.HasValue
                || s.PHD2AfMinStarSnr.HasValue || !string.IsNullOrEmpty(s.PHD2AutoSelectDownsample)
                || s.PHD2SaturationByADU.HasValue || s.PHD2SaturationADUValue.HasValue
                || s.PHD2BacklashCompEnabled.HasValue || s.PHD2BacklashPulseWidth.HasValue
                || s.PHD2BacklashFloor.HasValue || s.PHD2BacklashCeiling.HasValue
                || s.PHD2DecHysteresis.HasValue || s.PHD2RAFastSwitch.HasValue
                || s.PHD2RASlopeWeight.HasValue || s.PHD2DecSlopeWeight.HasValue
                || s.PHD2RALowpass2Aggressiveness.HasValue || s.PHD2DecLowpass2Aggressiveness.HasValue
                || s.PHD2RAPredictiveWeight.HasValue || s.PHD2DecPredictiveWeight.HasValue
                || s.PHD2RAReactiveWeight.HasValue || s.PHD2DecReactiveWeight.HasValue
                || s.PHD2RAPeriodLength.HasValue || s.PHD2DecPeriodLength.HasValue
                || s.PHD2RAExpFactor.HasValue || s.PHD2DecExpFactor.HasValue;
            if (!anyStored) {
                Logger.Info("No PHD2 algo settings stored in NINA profile, skipping restore");
                return;
            }
            try {
                // Algorithm selection must come first — switching algo resets its params
                if (!string.IsNullOrEmpty(s.PHD2GuideAlgorithmRA)) {
                    var msg = new Phd2SetGuideAlgorithmRA() { Parameters = new object[] { s.PHD2GuideAlgorithmRA } };
                    var resp = await SendMessage(msg);
                    if (resp.error != null)
                        Logger.Warning($"Failed to restore RA guide algorithm: {resp.error.message}");
                }
                if (!string.IsNullOrEmpty(s.PHD2GuideAlgorithmDec)) {
                    var msg = new Phd2SetGuideAlgorithmDec() { Parameters = new object[] { s.PHD2GuideAlgorithmDec } };
                    var resp = await SendMessage(msg);
                    if (resp.error != null)
                        Logger.Warning($"Failed to restore Dec guide algorithm: {resp.error.message}");
                }
                // Fetch valid param names for each axis so we only send params the active algorithm supports
                var raParams = await GetAlgoParamNames("ra");
                var decParams = await GetAlgoParamNames("dec");
                if (s.PHD2RAMinMove.HasValue && raParams.Contains("minMove"))
                    await SetAlgoParam("ra", "minMove", s.PHD2RAMinMove.Value);
                if (s.PHD2DecMinMove.HasValue && decParams.Contains("minMove"))
                    await SetAlgoParam("dec", "minMove", s.PHD2DecMinMove.Value);
                if (s.PHD2RAAggressiveness.HasValue && raParams.Contains("aggression"))
                    await SetAlgoParam("ra", "aggression", s.PHD2RAAggressiveness.Value);
                if (s.PHD2DecAggressiveness.HasValue && decParams.Contains("aggression"))
                    await SetAlgoParam("dec", "aggression", s.PHD2DecAggressiveness.Value);
                if (s.PHD2RAHysteresis.HasValue && raParams.Contains("hysteresis"))
                    await SetAlgoParam("ra", "hysteresis", s.PHD2RAHysteresis.Value);
                if (s.PHD2DecFastSwitch.HasValue && decParams.Contains("fastSwitch"))
                    await SetAlgoParam("dec", "fastSwitch", s.PHD2DecFastSwitch.Value ? 1.0 : 0.0);
                if (s.PHD2DecHysteresis.HasValue && decParams.Contains("hysteresis"))
                    await SetAlgoParam("dec", "hysteresis", s.PHD2DecHysteresis.Value);
                if (s.PHD2RAFastSwitch.HasValue && raParams.Contains("fastSwitch"))
                    await SetAlgoParam("ra", "fastSwitch", s.PHD2RAFastSwitch.Value ? 1.0 : 0.0);
                if (s.PHD2RASlopeWeight.HasValue && raParams.Contains("slopeWeight"))
                    await SetAlgoParam("ra", "slopeWeight", s.PHD2RASlopeWeight.Value);
                if (s.PHD2DecSlopeWeight.HasValue && decParams.Contains("slopeWeight"))
                    await SetAlgoParam("dec", "slopeWeight", s.PHD2DecSlopeWeight.Value);
                if (s.PHD2RALowpass2Aggressiveness.HasValue && raParams.Contains("aggressiveness"))
                    await SetAlgoParam("ra", "aggressiveness", s.PHD2RALowpass2Aggressiveness.Value);
                if (s.PHD2DecLowpass2Aggressiveness.HasValue && decParams.Contains("aggressiveness"))
                    await SetAlgoParam("dec", "aggressiveness", s.PHD2DecLowpass2Aggressiveness.Value);
                if (s.PHD2RAPredictiveWeight.HasValue && raParams.Contains("predictiveWeight"))
                    await SetAlgoParam("ra", "predictiveWeight", s.PHD2RAPredictiveWeight.Value);
                if (s.PHD2DecPredictiveWeight.HasValue && decParams.Contains("predictiveWeight"))
                    await SetAlgoParam("dec", "predictiveWeight", s.PHD2DecPredictiveWeight.Value);
                if (s.PHD2RAReactiveWeight.HasValue && raParams.Contains("reactiveWeight"))
                    await SetAlgoParam("ra", "reactiveWeight", s.PHD2RAReactiveWeight.Value);
                if (s.PHD2DecReactiveWeight.HasValue && decParams.Contains("reactiveWeight"))
                    await SetAlgoParam("dec", "reactiveWeight", s.PHD2DecReactiveWeight.Value);
                if (s.PHD2RAPeriodLength.HasValue && raParams.Contains("periodLength") && s.PHD2RAGPAutoAdjustPeriod != true)
                    await SetAlgoParam("ra", "periodLength", s.PHD2RAPeriodLength.Value);
                if (s.PHD2DecPeriodLength.HasValue && decParams.Contains("periodLength") && s.PHD2DecGPAutoAdjustPeriod != true)
                    await SetAlgoParam("dec", "periodLength", s.PHD2DecPeriodLength.Value);
                if (s.PHD2RAExpFactor.HasValue && raParams.Contains("expFactor"))
                    await SetAlgoParam("ra", "expFactor", s.PHD2RAExpFactor.Value);
                if (s.PHD2DecExpFactor.HasValue && decParams.Contains("expFactor"))
                    await SetAlgoParam("dec", "expFactor", s.PHD2DecExpFactor.Value);
                if (!string.IsNullOrEmpty(s.PHD2DecGuideMode)) {
                    var msg = new Phd2SetDecGuideMode() { Parameters = new object[] { s.PHD2DecGuideMode } };
                    var resp = await SendMessage(msg);
                    if (resp.error != null)
                        Logger.Warning($"Failed to restore dec guide mode: {resp.error.message}");
                }
                if (s.PHD2ExposureMs.HasValue) {
                    var msg = new Phd2SetExposure() { Parameters = new object[] { s.PHD2ExposureMs.Value } };
                    var resp = await SendMessage(msg);
                    if (resp.error != null)
                        Logger.Warning($"Failed to restore exposure: {resp.error.message}");
                }
                if (s.PHD2CalibrationStepMs.HasValue) {
                    var msg = new Phd2SetCalibrationStep() { Parameters = new object[] { s.PHD2CalibrationStepMs.Value } };
                    var resp = await SendMessage(msg);
                    if (resp.error != null)
                        Logger.Warning($"Failed to restore calibration step: {resp.error.message}");
                }
                if (s.PHD2CalibrationDistancePx.HasValue) {
                    var msg = new Phd2SetCalibrationDistance() { Parameters = new object[] { s.PHD2CalibrationDistancePx.Value } };
                    var resp = await SendMessage(msg);
                    if (resp.error != null)
                        Logger.Warning($"Failed to restore calibration distance: {resp.error.message}");
                }
                if (s.PHD2SearchRegion.HasValue) {
                    var msg = new Phd2SetSearchRegion() { Parameters = new object[] { s.PHD2SearchRegion.Value } };
                    var resp = await SendMessage(msg);
                    if (resp.error != null)
                        Logger.Warning($"Failed to restore search region: {resp.error.message}");
                }
                if (s.PHD2MaxRADuration.HasValue) {
                    var msg = new Phd2SetMaxRADuration() { Parameters = new object[] { s.PHD2MaxRADuration.Value } };
                    var resp = await SendMessage(msg);
                    if (resp.error != null)
                        Logger.Warning($"Failed to restore max RA duration: {resp.error.message}");
                }
                if (s.PHD2MaxDecDuration.HasValue) {
                    var msg = new Phd2SetMaxDecDuration() { Parameters = new object[] { s.PHD2MaxDecDuration.Value } };
                    var resp = await SendMessage(msg);
                    if (resp.error != null)
                        Logger.Warning($"Failed to restore max Dec duration: {resp.error.message}");
                }
                if (s.PHD2DitherScale.HasValue) {
                    var msg = new Phd2SetDitherScale() { Parameters = new object[] { s.PHD2DitherScale.Value } };
                    var resp = await SendMessage(msg);
                    if (resp.error != null)
                        Logger.Warning($"Failed to restore dither scale: {resp.error.message}");
                }
                if (s.PHD2DitherRAOnly.HasValue) {
                    var msg = new Phd2SetDitherRAOnly() { Parameters = new object[] { s.PHD2DitherRAOnly.Value } };
                    var resp = await SendMessage(msg);
                    if (resp.error != null)
                        Logger.Warning($"Failed to restore dither RA only: {resp.error.message}");
                }
                if (!string.IsNullOrEmpty(s.PHD2DitherMode)) {
                    var msg = new Phd2SetDitherMode() { Parameters = new object[] { s.PHD2DitherMode } };
                    var resp = await SendMessage(msg);
                    if (resp.error != null)
                        Logger.Warning($"Failed to restore dither mode: {resp.error.message}");
                }
                if (s.PHD2NoiseReductionMethod.HasValue) {
                    var msg = new Phd2SetNoiseReductionMethod() { Parameters = new object[] { s.PHD2NoiseReductionMethod.Value } };
                    var resp = await SendMessage(msg);
                    if (resp.error != null)
                        Logger.Warning($"Failed to restore noise reduction method: {resp.error.message}");
                }
                if (s.PHD2CameraGain.HasValue) {
                    var msg = new Phd2SetCameraGain() { Parameters = new object[] { s.PHD2CameraGain.Value } };
                    var resp = await SendMessage(msg);
                    if (resp.error != null)
                        Logger.Warning($"Failed to restore camera gain: {resp.error.message}");
                }
                if (s.PHD2CameraBinning.HasValue) {
                    var msg = new Phd2SetCameraBinning() { Parameters = new object[] { s.PHD2CameraBinning.Value } };
                    var resp = await SendMessage(msg);
                    if (resp.error != null)
                        Logger.Warning($"Failed to restore camera binning: {resp.error.message}");
                }
                if (s.PHD2UseSubframes.HasValue) {
                    var msg = new Phd2SetCameraUseSubframes() { Parameters = new object[] { s.PHD2UseSubframes.Value } };
                    var resp = await SendMessage(msg);
                    if (resp.error != null)
                        Logger.Warning($"Failed to restore use subframes: {resp.error.message}");
                }
                if (s.PHD2UseMultipleStars.HasValue) {
                    var msg = new Phd2SetUseMultipleStars() { Parameters = new object[] { s.PHD2UseMultipleStars.Value } };
                    var resp = await SendMessage(msg);
                    if (resp.error != null)
                        Logger.Warning($"Failed to restore use multiple stars: {resp.error.message}");
                }
                if (s.PHD2MassChangeThresholdEnabled.HasValue) {
                    var msg = new Phd2SetMassChangeThresholdEnabled() { Parameters = new object[] { s.PHD2MassChangeThresholdEnabled.Value } };
                    var resp = await SendMessage(msg);
                    if (resp.error != null)
                        Logger.Warning($"Failed to restore mass change threshold enabled: {resp.error.message}");
                }
                if (s.PHD2MassChangeThreshold.HasValue) {
                    var msg = new Phd2SetMassChangeThreshold() { Parameters = new object[] { s.PHD2MassChangeThreshold.Value } };
                    var resp = await SendMessage(msg);
                    if (resp.error != null)
                        Logger.Warning($"Failed to restore mass change threshold: {resp.error.message}");
                }
                if (s.PHD2MinStarHFD.HasValue) {
                    var msg = new Phd2SetMinStarHFD() { Parameters = new object[] { s.PHD2MinStarHFD.Value } };
                    var resp = await SendMessage(msg);
                    if (resp.error != null)
                        Logger.Warning($"Failed to restore min star HFD: {resp.error.message}");
                }
                if (s.PHD2MaxStarHFD.HasValue) {
                    var msg = new Phd2SetMaxStarHFD() { Parameters = new object[] { s.PHD2MaxStarHFD.Value } };
                    var resp = await SendMessage(msg);
                    if (resp.error != null)
                        Logger.Warning($"Failed to restore max star HFD: {resp.error.message}");
                }
                if (s.PHD2BeepForLostStar.HasValue) {
                    var msg = new Phd2SetBeepForLostStar() { Parameters = new object[] { s.PHD2BeepForLostStar.Value } };
                    var resp = await SendMessage(msg);
                    if (resp.error != null)
                        Logger.Warning($"Failed to restore beep for lost star: {resp.error.message}");
                }
                if (s.PHD2FocalLength.HasValue) {
                    var msg = new Phd2SetFocalLength() { Parameters = new object[] { s.PHD2FocalLength.Value } };
                    var resp = await SendMessage(msg);
                    if (resp.error != null)
                        Logger.Warning($"Failed to restore focal length: {resp.error.message}");
                }
                if (s.PHD2AutoRestoreCalibration.HasValue) {
                    Logger.Info($"Phd2 - Setting auto restore calibration: {s.PHD2AutoRestoreCalibration.Value}");
                    var msg = new Phd2SetAutoRestoreCalibration() { Parameters = new object[] { s.PHD2AutoRestoreCalibration.Value } };
                    var resp = await SendMessage(msg);
                    if (resp.error != null)
                        Logger.Warning($"Failed to restore auto restore calibration: {resp.error.message}");
                }
                if (s.PHD2AssumeDecOrthogonal.HasValue) {
                    var msg = new Phd2SetAssumeDecOrthogonal() { Parameters = new object[] { s.PHD2AssumeDecOrthogonal.Value } };
                    var resp = await SendMessage(msg);
                    if (resp.error != null)
                        Logger.Warning($"Failed to restore assume dec orthogonal: {resp.error.message}");
                }
                if (s.PHD2UseDecCompensation.HasValue) {
                    var msg = new Phd2SetUseDecCompensation() { Parameters = new object[] { s.PHD2UseDecCompensation.Value } };
                    var resp = await SendMessage(msg);
                    if (resp.error != null)
                        Logger.Warning($"Failed to restore use dec compensation: {resp.error.message}");
                }
                if (s.PHD2ReverseDecOnFlip.HasValue) {
                    var msg = new Phd2SetReverseDecOnFlip() { Parameters = new object[] { s.PHD2ReverseDecOnFlip.Value } };
                    var resp = await SendMessage(msg);
                    if (resp.error != null)
                        Logger.Warning($"Failed to restore reverse dec on flip: {resp.error.message}");
                }
                if (s.PHD2FastRecenter.HasValue) {
                    var msg = new Phd2SetFastRecenterEnabled() { Parameters = new object[] { s.PHD2FastRecenter.Value } };
                    var resp = await SendMessage(msg);
                    if (resp.error != null)
                        Logger.Warning($"Failed to restore fast recenter: {resp.error.message}");
                }
                if (s.PHD2TimeLapseMs.HasValue) {
                    var msg = new Phd2SetTimeLapse() { Parameters = new object[] { s.PHD2TimeLapseMs.Value } };
                    var resp = await SendMessage(msg);
                    if (resp.error != null)
                        Logger.Warning($"Failed to restore time lapse: {resp.error.message}");
                }
                if (s.PHD2VarDelayEnabled.HasValue && s.PHD2VarDelayShortSec.HasValue && s.PHD2VarDelayLongSec.HasValue) {
                    var msg = new Phd2SetVariableDelaySettings() {
                        Parameters = new Phd2SetVariableDelaySettingsParam {
                            Enabled = s.PHD2VarDelayEnabled.Value,
                            ShortDelaySeconds = s.PHD2VarDelayShortSec.Value,
                            LongDelaySeconds = s.PHD2VarDelayLongSec.Value
                        }
                    };
                    var resp = await SendMessage(msg);
                    if (resp.error != null)
                        Logger.Warning($"Failed to restore variable delay settings: {resp.error.message}");
                }
                if (s.PHD2AfMinStarSnr.HasValue) {
                    var msg = new Phd2SetAfMinStarSnr() { Parameters = new object[] { s.PHD2AfMinStarSnr.Value } };
                    var resp = await SendMessage(msg);
                    if (resp.error != null)
                        Logger.Warning($"Failed to restore AF min star SNR: {resp.error.message}");
                }
                if (!string.IsNullOrEmpty(s.PHD2AutoSelectDownsample)) {
                    var msg = new Phd2SetAutoSelectDownsample() { Parameters = new object[] { s.PHD2AutoSelectDownsample } };
                    var resp = await SendMessage(msg);
                    if (resp.error != null)
                        Logger.Warning($"Failed to restore auto select downsample: {resp.error.message}");
                }
                if (s.PHD2SaturationByADU.HasValue) {
                    var msg = new Phd2SetSaturationByADU() {
                        Parameters = new Phd2SetSaturationByADUParam {
                            ByADU = s.PHD2SaturationByADU.Value,
                            ADUValue = s.PHD2SaturationByADU.Value ? s.PHD2SaturationADUValue : null
                        }
                    };
                    var resp = await SendMessage(msg);
                    if (resp.error != null)
                        Logger.Warning($"Failed to restore saturation mode: {resp.error.message}");
                }
                if (s.PHD2BacklashCompEnabled.HasValue || s.PHD2BacklashPulseWidth.HasValue
                    || s.PHD2BacklashFloor.HasValue || s.PHD2BacklashCeiling.HasValue) {
                    var msg = new Phd2SetBacklashComp() {
                        Parameters = new Phd2SetBacklashCompParam {
                            Enable = s.PHD2BacklashCompEnabled,
                            Pulse = s.PHD2BacklashPulseWidth,
                            Floor = s.PHD2BacklashFloor,
                            Ceiling = s.PHD2BacklashCeiling
                        }
                    };
                    var resp = await SendMessage(msg);
                    if (resp.error != null)
                        Logger.Warning($"Failed to restore backlash compensation: {resp.error.message}");
                }
                Logger.Info("PHD2 algo settings restored from NINA profile");
            } catch (Exception ex) {
                Logger.Warning($"Failed to restore stored PHD2 algo settings: {ex.Message}");
            }
        }

        private async Task SetAlgoParam(string axis, string name, double value) {
            var msg = new Phd2SetAlgoParam() { Parameters = new object[] { axis, name, value } };
            var resp = await SendMessage(msg);
            if (resp.error != null)
                Logger.Warning($"Failed to set PHD2 {axis} {name}: {resp.error.message} (code {resp.error.code})");
        }

        private async Task<double?> GetAlgoParam(string axis, string name) {
            var msg = new Phd2GetAlgoParam() { Parameters = new object[] { axis, name } };
            var resp = await SendMessage<DoublePhdMethodResponse>(msg);
            if (resp.error != null) {
                Logger.Info($"PHD2 {axis} {name} not available: {resp.error.message}");
                return null;
            }
            return resp.result;
        }

        private async Task<HashSet<string>> GetAlgoParamNames(string axis) {
            var msg = new Phd2GetAlgoParamNames() { Parameters = new object[] { axis } };
            var resp = await SendMessage<StringArrayPhdMethodResponse>(msg);
            if (resp.error != null || resp.result == null) {
                Logger.Info($"PHD2 could not get algo param names for {axis}: {resp.error?.message}");
                return new HashSet<string>(StringComparer.Ordinal);
            }
            return new HashSet<string>(resp.result, StringComparer.Ordinal);
        }

        private async Task GetProfiles() {
            var getProfile = new Phd2GetProfile();
            var getProfileResponse = await SendMessage<GetProfileResponse>(getProfile);
            if (getProfileResponse.error != null) {
                Logger.Error($"Failed GetProfile: {getProfileResponse.error}");
                throw new Exception(Loc.Instance["LblPhd2FailedGetProfiles"]);
            }

            var getProfiles = new Phd2GetProfiles();
            var getProfilesResponse = await SendMessage<GetProfilesResponse>(getProfiles);
            if (getProfileResponse.error != null) {
                Logger.Error($"Failed GetProfiles: {getProfilesResponse.error}");
                throw new Exception(Loc.Instance["LblPhd2FailedGetProfiles"]);
            }

            _activeProfile = getProfileResponse.result;
            AvailableProfiles.Clear();
            foreach (var profile in getProfilesResponse.result) {
                AvailableProfiles.Add(new Phd2Profile { Name = profile.name, Id = profile.id });
            }
            SelectedProfile = AvailableProfiles.FirstOrDefault(x => x.Id == _activeProfile.id);
        }

        private async Task<string> GetSelectedMount() {
            try {
                var getSelectedMountMsg = new Phd2GetSelectedMount();
                var getSelectedMountResponse = await SendMessage<StringPhdMethodResponse>(getSelectedMountMsg);

                if (getSelectedMountResponse.error != null) {
                    Logger.Error($"Failed to get selected mount: {getSelectedMountResponse.error.message}");
                    return null;
                }

                string selectedMount = getSelectedMountResponse.result;
                Logger.Info($"Currently selected mount by PHD2: {selectedMount}");

                // If INDI mount is selected, also get the INDI driver name
                if (selectedMount == "INDI") {
                    var getINDIDriverMsg = new Phd2GetSelectedINDIMountDriver();
                    var getINDIDriverResponse = await SendMessage<StringPhdMethodResponse>(getINDIDriverMsg);

                    if (getINDIDriverResponse.error != null) {
                        Logger.Error($"Failed to get selected INDI mount driver: {getINDIDriverResponse.error.message}");
                        return selectedMount;
                    }

                    string indiDriver = getINDIDriverResponse.result;
                    Logger.Info($"Currently selected INDI driver: {indiDriver}");
                }

                return selectedMount;
            } catch (Exception ex) {
                Logger.Error($"Error getting PHD2 mount information: {ex.Message}");
                return null;
            }
        }

        private async Task<bool> ValidateMountMatch(TelescopeInfo telescopeInfo, string phdSelectedMount) {
            try {
                // Get NINA mount information
                string ninaMountName = telescopeInfo.Name;
                string ninaMountDisplayName = telescopeInfo.DisplayName;
                string ninaDeviceId = telescopeInfo.DeviceId;

                Logger.Info($"Validating mount match: NINA='{ninaMountName}' (ID: '{ninaDeviceId}'), PHD2='{phdSelectedMount}'");

                // Determine if NINA has an INDI mount - check DisplayName for INDI keyword
                bool isINDIMount = !string.IsNullOrEmpty(ninaMountDisplayName) &&
                    ninaMountDisplayName.Contains("INDI", StringComparison.OrdinalIgnoreCase);

                if (isINDIMount) {
                    // For INDI mounts, PHD2 format is "INDI Mount [$deviceId]"
                    string expectedPHD2Mount = $"INDI Mount [{ninaDeviceId}]";

                    if (phdSelectedMount.Equals(expectedPHD2Mount, StringComparison.OrdinalIgnoreCase)) {
                        Logger.Info($"Mount match confirmed: both use INDI mount '{expectedPHD2Mount}'");
                        return true;
                    }

                    // Mounts don't match - attempt to set PHD2 mount to match NINA
                    Logger.Warning($"Mount mismatch detected: NINA expects '{expectedPHD2Mount}', PHD2 has '{phdSelectedMount}' selected. Attempting to sync...");
                    return await SetPHD2MountToMatch(telescopeInfo);
                } else {
                    // For non-INDI mounts, check if the names match
                    if (!string.IsNullOrEmpty(ninaMountName) &&
                        ninaMountName.Equals(phdSelectedMount, StringComparison.OrdinalIgnoreCase)) {
                        Logger.Info($"Mount match confirmed: both use '{ninaMountName}'");
                        return true;
                    }

                    // Mounts don't match - attempt to set PHD2 mount to match NINA
                    Logger.Warning($"Mount mismatch detected: NINA='{ninaMountName}', PHD2='{phdSelectedMount}'. Attempting to sync...");
                    return await SetPHD2MountToMatch(telescopeInfo);
                }
            } catch (Exception ex) {
                Logger.Error($"Error validating mount match: {ex.Message}");
                return false;
            }
        }

        private async Task<string> GetSelectedCamera() {
            try {
                var getSelectedCameraMsg = new Phd2GetSelectedCamera();
                var getSelectedCameraResponse = await SendMessage<StringPhdMethodResponse>(getSelectedCameraMsg);

                if (getSelectedCameraResponse.error != null) {
                    Logger.Error($"Failed to get selected camera: {getSelectedCameraResponse.error.message}");
                    return null;
                }

                string selectedCamera = getSelectedCameraResponse.result;
                Logger.Info($"Currently selected camera by PHD2: {selectedCamera}");

                // If INDI camera is selected, also get the INDI driver name
                if (selectedCamera == "INDI") {
                    var getINDIDriverMsg = new Phd2GetSelectedINDICameraDriver();
                    var getINDIDriverResponse = await SendMessage<StringPhdMethodResponse>(getINDIDriverMsg);

                    if (getINDIDriverResponse.error != null) {
                        Logger.Error($"Failed to get selected INDI camera driver: {getINDIDriverResponse.error.message}");
                        return selectedCamera;
                    }

                    string indiDriver = getINDIDriverResponse.result;
                    Logger.Info($"Currently selected INDI driver: {indiDriver}");
                }

                return selectedCamera;
            } catch (Exception ex) {
                Logger.Error($"Error getting PHD2 camera information: {ex.Message}");
                return null;
            }
        }

        private async Task<string> GetSelectedCameraId() {
            try {
                var getSelectedCameraIdMsg = new Phd2GetSelectedCameraId();
                var getSelectedCameraIdResponse = await SendMessage<StringPhdMethodResponse>(getSelectedCameraIdMsg);

                if (getSelectedCameraIdResponse.error != null) {
                    Logger.Error($"Failed to get selected camera id: {getSelectedCameraIdResponse.error.message}");
                    return null;
                }

                string selectedCameraId = getSelectedCameraIdResponse.result;
                Logger.Info($"Currently selected camera id by PHD2: {selectedCameraId}");

                return selectedCameraId;
            } catch (Exception ex) {
                Logger.Error($"Error getting PHD2 camera id information: {ex.Message}");
                return null;
            }
        }

        private async Task<int> GetSelectedCameraBitDepth() {
            try {
                var getSelectedCameraBitDepthMsg = new Phd2GetCameraBitDepth();
                var getSelectedCameraBitDepthResponse = await SendMessage<IntegerPhdMethodResponse>(getSelectedCameraBitDepthMsg);

                if (getSelectedCameraBitDepthResponse.error != null) {
                    Logger.Error($"Failed to get selected camera bit depth: {getSelectedCameraBitDepthResponse.error.message}");
                    return 0;
                }

                int selectedCameraBitDepth = getSelectedCameraBitDepthResponse.result;
                Logger.Info($"Currently selected camera bit depth by PHD2: {selectedCameraBitDepth}");

                return selectedCameraBitDepth;
            } catch (Exception ex) {
                Logger.Error($"Error getting PHD2 camera bit depth information: {ex.Message}");
                return 0;
            }
        }

        private async Task<bool> ValidateCameraMatch(string ninaCamera, string ninaCameraId, string phdSelectedCamera, string phdSelectedCameraId) {
            try {
                Logger.Info($"Validating camera match: NINA='{ninaCameraId}' ({ninaCamera}), PHD2='{phdSelectedCameraId}' ({phdSelectedCamera})");

                if (phdSelectedCamera.Equals(ninaCamera, StringComparison.OrdinalIgnoreCase)) {
                    if (phdSelectedCameraId.Equals(ninaCameraId, StringComparison.OrdinalIgnoreCase)) {
                        Logger.Info($"Camera match confirmed: both use '{ninaCameraId}' ({ninaCamera}");
                        return true;
                    }

                    // Camera ids don't match - attempt to set PHD2 camera id to match NINA
                    Logger.Warning($"Camera id mismatch detected: NINA expects '{ninaCameraId}', PHD2 has '{phdSelectedCameraId}' selected. Attempting to sync...");
                    return await SetPHD2CameraIdToMatch(ninaCameraId);
                } else {
                    // Cameras don't match - attempt to set PHD2 camera to match NINA
                    Logger.Warning($"Camera mismatch detected: NINA='{ninaCamera}', PHD2='{phdSelectedCamera}'. Attempting to sync...");
                    return await SetPHD2CameraToMatch(ninaCamera, ninaCameraId);
                }
            } catch (Exception ex) {
                Logger.Error($"Error validating camera match: {ex.Message}");
                return false;
            }
        }

        private async Task<bool> ValidateCameraBitDepth(int ninaBitDepth, int phdBitDepth) {
            try {
                Logger.Info($"Validating camera bit depth: NINA={ninaBitDepth}, PHD2={phdBitDepth}");

                if (ninaBitDepth == phdBitDepth) {
                    Logger.Info($"Camera bit depth match confirmed: both use {ninaBitDepth} bit");
                    return true;
                }

                // Camera bit depth don't match
                Logger.Warning($"Camera bit depth mismatch detected: NINA expects {ninaBitDepth}, PHD2 has {phdBitDepth}. Attempting to sync...");
                return await SetPHD2CameraBitDepthToMatch(ninaBitDepth);
            } catch (Exception ex) {
                Logger.Error($"Error validating camera bit depth match: {ex.Message}");
                return false;
            }
        }

        private async Task<bool> SetPHD2MountToMatch(TelescopeInfo telescopeInfo) {
            try {
                string ninaMountName = telescopeInfo.Name;
                string ninaMountDisplayName = telescopeInfo.DisplayName;
                string ninaDeviceId = telescopeInfo.DeviceId;

                // Determine if we're setting an INDI mount or a regular mount
                // Check if DisplayName contains "INDI"
                bool isINDIMount = !string.IsNullOrEmpty(ninaMountDisplayName) &&
                    ninaMountDisplayName.Contains("INDI", StringComparison.OrdinalIgnoreCase);

                if (isINDIMount) {
                    // PHD2's set_selected_mount validates against Scope::MountList(), which builds the
                    // INDI entry by reading the "/indi/INDImount" profile config key via INDIMountName().
                    // If that key is not yet set to ninaDeviceId, the list only contains "INDI Mount"
                    // (without the driver suffix), so "INDI Mount [<driver>]" won't be found and the
                    // call fails.  We must therefore set the INDI driver first so that PHD2 reloads
                    // its mount list before we call set_selected_mount.
                    Logger.Info($"Setting PHD2 INDI mount driver to: {ninaDeviceId}");

                    var setDriverMsg = new Phd2SetSelectedINDIMountDriver();
                    setDriverMsg.Parameters = new JObject { ["driver"] = ninaDeviceId };
                    var setDriverResult = await SendMessage(setDriverMsg);

                    if (setDriverResult.error != null) {
                        Logger.Error($"Failed to set PHD2 INDI mount driver to '{ninaDeviceId}': {setDriverResult.error.message}");
                        return false;
                    }

                    // Now set_selected_mount will find "INDI Mount [<driver>]" in the refreshed list.
                    string phd2MountId = $"INDI Mount [{ninaDeviceId}]";
                    Logger.Info($"Setting PHD2 mount to INDI format: {phd2MountId}");

                    var setMountMsg = new Phd2SetSelectedMount();
                    setMountMsg.Parameters = new JObject { ["mount"] = phd2MountId };
                    var setMountResult = await SendMessage(setMountMsg);

                    if (setMountResult.error != null) {
                        Logger.Error($"Failed to set PHD2 mount to '{phd2MountId}': {setMountResult.error.message}");
                        return false;
                    }

                    Logger.Info($"Successfully synchronized PHD2 mount to INDI: {phd2MountId}");
                    return true;
                } else {
                    // Set regular mount by name
                    Logger.Info($"Setting PHD2 mount to: {ninaMountName}");

                    var setMountMsg = new Phd2SetSelectedMount();
                    setMountMsg.Parameters = new JObject { ["mount"] = ninaMountName };
                    var setMountResult = await SendMessage(setMountMsg);

                    if (setMountResult.error != null) {
                        Logger.Error($"Failed to set PHD2 mount to '{ninaMountName}': {setMountResult.error.message}");
                        return false;
                    }

                    Logger.Info($"Successfully synchronized PHD2 mount to: {ninaMountName}");
                    return true;
                }
            } catch (Exception ex) {
                Logger.Error($"Error setting PHD2 mount to match NINA: {ex.Message}");
                return false;
            }
        }

        private async Task<bool> SetPHD2CameraIdToMatch(string ninaCameraId) {
            try {
                // Set camera id
                Logger.Info($"Setting PHD2 camera id to: {ninaCameraId}");

                var setCameraIdMsg = new Phd2SetSelectedCameraId();
                setCameraIdMsg.Parameters = new JObject { ["camera_id"] = ninaCameraId };
                var setCameraIdResult = await SendMessage(setCameraIdMsg);

                if (setCameraIdResult.error != null) {
                    Logger.Error($"Failed to set PHD2 camera id to '{ninaCameraId}': {setCameraIdResult.error.message}");
                    return false;
                }

                Logger.Info($"Successfully synchronized PHD2 camera id to: {ninaCameraId}");
                return true;
            } catch (Exception ex) {
                Logger.Error($"Error setting PHD2 camera id to match NINA: {ex.Message}");
                return false;
            }
        }

        private async Task<bool> SetPHD2CameraToMatch(string ninaCamera, string ninaCameraId) {
            try {
                // Set camera
                Logger.Info($"Setting PHD2 camera to: {ninaCamera}");

                var setCameraMsg = new Phd2SetSelectedCamera();
                setCameraMsg.Parameters = new JObject { ["camera"] = ninaCamera };
                var setCameraResult = await SendMessage(setCameraMsg);

                if (setCameraResult.error != null) {
                    Logger.Error($"Failed to set PHD2 camera to '{ninaCamera}': {setCameraResult.error.message}");
                    return false;
                }

                Logger.Info($"Successfully synchronized PHD2 camera to: {ninaCamera}");

                // Set camera id
                return await SetPHD2CameraIdToMatch(ninaCameraId);
            } catch (Exception ex) {
                Logger.Error($"Error setting PHD2 camera to match NINA: {ex.Message}");
                return false;
            }
        }

        private async Task<bool> SetPHD2CameraBitDepthToMatch(int ninaBitDepth) {
            try {
                // Set camera bit depth
                Logger.Info($"Setting PHD2 camera bit depth to: {ninaBitDepth}");

                var setCameraBitDepthMsg = new Phd2SetCameraBitDepth();
                setCameraBitDepthMsg.Parameters = new JObject { ["bitdepth"] = ninaBitDepth };
                var setCameraBitDepthResult = await SendMessage(setCameraBitDepthMsg);

                if (setCameraBitDepthResult.error != null) {
                    // PHD2 returns "Camera does not support bitdepth setting" for cameras where the
                    // depth cannot be changed via config (e.g. INDI cameras).  That is not a real
                    // failure — the camera simply manages its own bit depth.  Log as info and report
                    // success so the caller does not surface a spurious warning to the user.
                    if (setCameraBitDepthResult.error.message?.Contains("does not support bitdepth") == true) {
                        Logger.Info($"PHD2 camera does not support bitdepth configuration; skipping sync.");
                        return true;
                    }
                    Logger.Error($"Failed to set PHD2 camera bit depth to '{ninaBitDepth}': {setCameraBitDepthResult.error.message}");
                    return false;
                }

                Logger.Info($"Successfully synchronized PHD2 camera bit depth to: {ninaBitDepth}");
                return true;
            } catch (Exception ex) {
                Logger.Error($"Error setting PHD2 camera bit depth to match NINA: {ex.Message}");
                return false;
            }
        }

        private async Task<bool> EnsurePHD2EquipmentConnected() {
            var getConnected = new Phd2GetConnected();
            var getConnectedResult = await SendMessage(getConnected);
            if (getConnectedResult.error != null) {
                Notification.ShowWarning(Loc.Instance["LblPhd2FailedEquipmentConnection"]);
                return false;
            }

            if (!(bool)getConnectedResult.result) {
                var setConnected = new Phd2SetConnected() {
                    Parameters = new bool[] { true }
                };
                var setConnectedResult = await SendMessage(setConnected);
                if (setConnectedResult.error != null) {
                    Notification.ShowWarning(Loc.Instance["LblPhd2FailedEquipmentConnection"]);
                    return false;
                }
            }

            return true;
        }

        private async Task<bool> IsPHD2EquipmentConnected() {
            var getConnected = new Phd2GetConnected();
            var getConnectedResult = await SendMessage(getConnected);
            if (getConnectedResult.error != null) {
                return false;
            }
            return (bool)getConnectedResult.result;
        }

        private async Task DisconnectPHD2Equipment() {
            await StopCapture(default);
            var setDisconnected = new Phd2SetConnected() {
                Parameters = new bool[] { false }
            };
            var setDisconnectedResult = await SendMessage(setDisconnected);
            if (setDisconnectedResult.error != null) {
                Logger.Error($"Failed to disconnect PHD2equipment: {setDisconnectedResult.error}");
            }
        }

        private static Process[] FindPhd2Processes() {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) {
                return Process.GetProcessesByName("PHD2");
            } else {
                // On Linux/macOS, check for 'phd2', 'phd2.bin', etc.
                var names = new[] { "phd2", "phd2.bin" };
                return Process.GetProcesses()
                    .Where(p => names.Contains(p.ProcessName, StringComparer.OrdinalIgnoreCase))
                    .ToArray();
            }
        }

        private async Task<bool> StartPHD2Process() {
            // If PHD2 instance is not running start it.
            try {
                var windowTitleRegex = PHD2WindowTitleRegex();

                // Check if PHD2 is already started with the expected instance number
                foreach (var p in FindPhd2Processes()) {
                    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) {
                        var match = windowTitleRegex.Match(p.MainWindowTitle);
                        if ((int.TryParse(match.Groups[1].Value, out int i) ? i : 1) == profileService.ActiveProfile.GuiderSettings.PHD2InstanceNumber) {
                            // PHD2 is already started
                            return true;
                        }
                    } else {
                        Logger.Info($"Found {p.ProcessName}");
                        return true;
                    }
                }

                if (!File.Exists(profileService.ActiveProfile.GuiderSettings.PHD2Path)) {
                    throw new FileNotFoundException();
                }

                var process = new Process {
                    StartInfo = {
                        FileName = profileService.ActiveProfile.GuiderSettings.PHD2Path,
                        Arguments = $"-i={profileService.ActiveProfile.GuiderSettings.PHD2InstanceNumber}"
                    }
                };
                process?.Start();
                process?.WaitForInputIdle();

                await Task.Delay(2000);

                bool socketReady = false;
                try {
                    var settings = profileService.ActiveProfile.GuiderSettings;
                    var instanceNumber = settings.PHD2InstanceNumber;
                    var phd2Path = settings.PHD2Path;
                    var port = settings.PHD2ServerPort;
                    var host = phd2Ip;
                    socketReady = await WaitForPhd2SocketAsync(
                        host: host,
                        port: port,
                        timeout: TimeSpan.FromSeconds(10),
                        pollInterval: TimeSpan.FromMilliseconds(500));
                } catch (Exception) {
                }

                return socketReady;
            } catch (FileNotFoundException ex) {
                Logger.Error(Loc.Instance["LblPhd2PathNotFound"], ex);
                Notification.ShowError(Loc.Instance["LblPhd2PathNotFound"]);
            } catch (Exception ex) {
                Logger.Error(ex);
                Notification.ShowError(Loc.Instance["LblPhd2StartProcessError"]);
            }

            return false;
        }

        private async Task<bool> WaitForPhd2SocketAsync(
                IPAddress host,
                int port,
                TimeSpan timeout,
                TimeSpan pollInterval,
                CancellationToken ct = default) {
            var sw = Stopwatch.StartNew();
            Exception lastException = null;

            while (sw.Elapsed < timeout) {
                ct.ThrowIfCancellationRequested();

                try {
                    using var probe = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };

                    await probe.ConnectAsync(host, port, ct);

                    if (probe.Connected) { 
                        return true;
                    }
                } catch (OperationCanceledException) {
                    throw;
                } catch (Exception ex) {
                    lastException = ex;
                }

                await Task.Delay(pollInterval, ct);
            }

            return false;
        }

        private async Task RunListener() {
            var jls = new JsonLoadSettings {
                LineInfoHandling = LineInfoHandling.Ignore,
                CommentHandling = CommentHandling.Ignore
            };

            _clientCTS?.Dispose();
            _clientCTS = new CancellationTokenSource();
            var ct = _clientCTS.Token;

            try {
                _client = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };

                await _client.ConnectAsync(phd2Ip, profileService.ActiveProfile.GuiderSettings.PHD2ServerPort, ct);

                _stream = _client.GetStream();
                _reader = new StreamReader(_stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: true);
                _writer = new StreamWriter(_stream, new UTF8Encoding(false), bufferSize: 4096, leaveOpen: true) {
                    NewLine = "\n",
                    AutoFlush = false
                };

                Connected = true;
                _tcs.TrySetResult(true);

                while (true) {
                    ct.ThrowIfCancellationRequested();

                    var state = GetState(_client);
                    if (state == TcpState.CloseWait)
                        throw new Exception(Loc.Instance["LblPhd2ServerConnectionLost"]);

                    string line = await _reader.ReadLineAsync(ct);
                    if (line is null)
                        throw new Exception(Loc.Instance["LblPhd2ServerConnectionLost"]);

                    if (line.Length == 0 || line[0] != '{')
                        continue;

                    JObject o;
                    try {
                        o = JObject.Parse(line, jls);
                    } catch {
                        continue;
                    }

                    // Event?
                    var ev = o["Event"];
                    if (ev != null) {
                        var phdevent = ev.ToString();
                        Logger.Trace($"PHD2 event received - {o}");
                        await ProcessEvent(phdevent, o);
                        continue;
                    }

                    // Response?
                    var idTok = o["id"];
                    if (idTok != null) {
                        var idKey = idTok.ToString();
                        if (_pending.TryRemove(idKey, out var waiter)) {
                            waiter.TrySetResult(o);
                        }
                        continue;
                    }

                    // else ignore
                }
            } catch (OperationCanceledException) {
                // normal
            } catch (Exception ex) {
                Logger.Error(ex);
                Notification.ShowError(string.Format(Loc.Instance["LblPHDErrorMsg"], ex.Message));
                throw;
            } finally {
                // Fail all pending SendMessage awaiters on connection teardown
                foreach (var kvp in _pending) {
                    if (_pending.TryRemove(kvp.Key, out var waiter)) {
                        waiter.TrySetException(new IOException("PHD2 connection closed"));
                    }
                }

                Settling = false;
                AppState = new PhdEventAppState { State = "" };
                PixelScale = 0.0d;
                Connected = false;

                _tcs.TrySetResult(false);

                try { _reader?.Dispose(); } catch { }
                try { _writer?.Dispose(); } catch { }
                try { _stream?.Dispose(); } catch { }

                _reader = null;
                _writer = null;
                _stream = null;
                _client = null;

                PHD2ConnectionLost?.Invoke(this, EventArgs.Empty);
            }
        }

        public void SetupDialog() {
            var windowService = windowServiceFactory.Create();
            windowService.ShowDialog(this, Loc.Instance["LblPHD2Setup"], System.Windows.ResizeMode.NoResize, System.Windows.WindowStyle.SingleBorderWindow);
        }

        [RelayCommand]
        private void OpenPHD2FileDialog(object o) {
            var dialog = CoreUtil.GetFilteredFileDialog(profileService.ActiveProfile.GuiderSettings.PHD2Path, "phd2.exe", "PHD2|phd2.exe");
            if (dialog.ShowDialog() == true) {
                this.profileService.ActiveProfile.GuiderSettings.PHD2Path = dialog.FileName;
            }
        }

        public event EventHandler PHD2ConnectionLost;

        public event EventHandler<IGuideStep> GuideEvent;

        public IList<string> SupportedActions => [];

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

        [GeneratedRegex(@"PHD2 Guiding\(?#?([0-9]*)\)?")]
        private static partial Regex PHD2WindowTitleRegex();
    }
}
