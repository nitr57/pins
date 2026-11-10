#region "copyright"

/*
    Copyright © 2025 Nico Trost <nico.trost57@gmail.com> and the PI.N.S. contributors

    This file is part of PI 'N' Stars.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NINA.Core.Locale;
using NINA.Core.MyMessageBox;
using NINA.Core.Utility;
using NINA.Core.Utility.Notification;
using NINA.Equipment.Interfaces;
using NINA.Profile.Interfaces;
using static Nitecrawler.NitecrawlerSDK;


namespace NINA.Equipment.Equipment.MyFocuser {
    public partial class NitecrawlerFocuser : BaseINPC, IFocuser, IFocuserReportsMoveEnd {
        private readonly IProfileService _profileService;
        private readonly int _uniqueId;
        // a move that ends without progress is sent again for this long; also how long a move may report moving without
        // the position changing before it counts as blocked (the Nitecrawler moves slowly over long distances)
        private readonly static TimeSpan _sameFocuserPositionTimeout = TimeSpan.FromMinutes(3);

        public NitecrawlerFocuser(int id, uint serial, IProfileService profileService) {
            Id = $"{serial}";
            _profileService = profileService;
            _uniqueId = id;

            Name = $"Nitecrawler.Focuser.{_uniqueId} ({serial})";
        }

        public bool IsMoving {
            get {
                if (!Connected) {
                    return false;
                }
                if (!TryGetStatus("get moving state", out var status)) {
                    return false;
                }
                return status.moving == 1;
            }
        }

        public int MaxIncrement => MaxStep;

        public bool CanSetMaxStep => true;
        public int MaxStep {
            get {
                if (!Connected) {
                    return -1;
                }
                var err = GetFocuserConfig(_uniqueId, out var config);
                if (err == NC_ERROR_TYPE.NC_SUCCESS) {
                    return config.maxStep;
                } else {
                    Logger.Error($"Nitecrawler error to get MaxStep {err}");
                    return -1;
                }
            }
            set {
                NC_FOCUSER_CONFIG config = new() {
                    mask = (uint)MASK_FOCUSER_MAX_STEP,
                    maxStep = value
                };
                var err = SetFocuserConfig(_uniqueId, config);
                if (err != NC_ERROR_TYPE.NC_SUCCESS) {
                    Logger.Error($"Nitecrawler failed to set the max step to {value}: {err}");
                }

                RaisePropertyChanged(nameof(MaxStep));
                RaisePropertyChanged(nameof(MaxIncrement));
            }
        }

        public bool CanReverse => false;
        public bool Reverse { get => false; set { } }

        public int Position {
            get {
                if (!Connected) {
                    return -1;
                }
                if (!TryGetStatus("get Position", out var status)) {
                    return -1;
                }
                return status.position;
            }
        }

        private void DisconnectOnRemovedError() {
            try {
                Notification.ShowWarning(Loc.Instance["LblFocuserConnectionLost"]);
                Logger.Error($"Nitecrawler device was removed");
                Disconnect();
            } catch (Exception ex) {
                Logger.Error(ex);
            }
        }

        // A single transient serial glitch (e.g. a polled GP/GM/GT status read timing out under
        // USB load) must not tear down the whole connection. We only treat NC_ERROR_COMMUNICATION
        // as a removal once we can confirm it - either the device is no longer enumerated, or it
        // has failed this many consecutive times despite still being present (e.g. firmware hung).
        private const int _maxConsecutiveCommErrors = 3;
        private int _consecutiveCommErrors = 0;

        /// <summary>
        /// Reads the focuser status, tolerating transient communication errors instead of
        /// disconnecting on the first glitch. Returns true on success; on failure the caller
        /// should fall back to a safe value and the connection is only dropped on a confirmed removal.
        /// </summary>
        private bool TryGetStatus(string context, out NC_FOCUSER_STATUS status) {
            var err = GetFocuserStatus(_uniqueId, out status);
            if (err == NC_ERROR_TYPE.NC_SUCCESS) {
                System.Threading.Interlocked.Exchange(ref _consecutiveCommErrors, 0);
                return true;
            }

            if (err == NC_ERROR_TYPE.NC_ERROR_COMMUNICATION) {
                HandleCommunicationError(context);
            } else {
                Logger.Error($"Nitecrawler error during {context} {err}");
            }
            return false;
        }

        /// <summary>
        /// Decides whether an NC_ERROR_COMMUNICATION is transient or a genuine removal.
        /// Returns true if it was handled as transient (connection kept); false if the device
        /// was removed/disconnected.
        /// </summary>
        private bool HandleCommunicationError(string context) {
            // Definitive check: is the device still physically enumerated? ScanFocusers reports
            // already-open ports without re-handshaking, so it is safe to call while connected.
            int[] ids = new int[NC_MAX_NUM];
            var scanErr = ScanFocusers(out var count, ids);
            bool stillPresent = scanErr == NC_ERROR_TYPE.NC_SUCCESS && ids.Take(count).Contains(_uniqueId);

            if (!stillPresent) {
                Logger.Error($"Nitecrawler communication error during {context}; device no longer enumerated - treating as removed");
                DisconnectOnRemovedError();
                return false;
            }

            var consecutive = System.Threading.Interlocked.Increment(ref _consecutiveCommErrors);
            if (consecutive >= _maxConsecutiveCommErrors) {
                Logger.Error($"Nitecrawler communication error during {context}; {consecutive} consecutive failures while device still present - disconnecting");
                DisconnectOnRemovedError();
                return false;
            }

            Logger.Warning($"Nitecrawler transient communication error during {context} ({consecutive}/{_maxConsecutiveCommErrors}); device still present, keeping connection");
            return true;
        }

        public double StepSize {
            get {
                if (!Connected) {
                    return float.NaN;
                }
                if (!TryGetStatus("get step size", out var status)) {
                    return -1;
                }
                return status.micronsPerStep;
            }
        }

        public double Temperature {
            get {
                if (!Connected) {
                    return double.NaN;
                }
                if (!TryGetStatus("get Temperature", out var status)) {
                    return double.NaN;
                }
                if (status.temperatureDetection == 0 || (uint)status.temperatureExt == TEMPERATURE_INVALID) {
                    // Ambient probe not connected
                    return double.NaN;
                }
                return status.temperatureExt / 100.0;
            }
        }

        public string Id { get; }
        public string Name { get; }
        public string DisplayName => Name;

        public string Category => "Nitecrawler";

        [ObservableProperty]
        private bool connected = false;

        [ObservableProperty]
        private int targetMaxStep;

        // Set while TargetMaxStep takes the value read from the focuser, which must not be written back
        private bool readingMaxStepFromFocuser;

        partial void OnTargetMaxStepChanged(int value) {
            if (readingMaxStepFromFocuser) {
                return;
            }

            NC_FOCUSER_CONFIG config = new NC_FOCUSER_CONFIG();
            config.mask = (uint)MASK_FOCUSER_MAX_STEP;
            config.maxStep = value;
            var err = SetFocuserConfig(_uniqueId, config);
            if (err != NC_ERROR_TYPE.NC_SUCCESS) {
                Logger.Error($"Nitecrawler failed to set the max step to {value}: {err}");
            }

            RaisePropertyChanged(nameof(MaxStep));
            RaisePropertyChanged(nameof(MaxIncrement));
        }

        [RelayCommand]
        public void ResetPosition() {
            if (MyMessageBox.Show(Loc.Instance["LblZwoResetZeroPositionPrompt"], "", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxResult.No) == System.Windows.MessageBoxResult.Yes) {
                if (Position > 0) {
                    FocuserSyncPosition(_uniqueId, 0);
                    RaisePropertyChanged(nameof(Position));
                }
            }
        }

        private int syncPosition;
        public int SyncPosition {
            get => syncPosition;
            set {
                if (syncPosition != value) {
                    syncPosition = value;
                    RaisePropertyChanged();
                }
            }
        }

        [RelayCommand]
        public void SyncToPosition() {
            if (MyMessageBox.Show(Loc.Instance["LblNitecrawlerSyncPositionPrompt"], "", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxResult.No) == System.Windows.MessageBoxResult.Yes) {
                if (Position != SyncPosition) {
                    FocuserSyncPosition(_uniqueId, SyncPosition);
                    RaisePropertyChanged(nameof(Position));
                }
            }
        }

        public string Description => "Native driver for Nitecrawler focusers";

        public string DriverInfo { get; private set; } = string.Empty;

        public string DriverVersion => GetSDKVersion();

        public Task<bool> Connect(CancellationToken token) {
            return Task.Run(() => {
                try {
                    return ConnectCore();
                } catch (Exception ex) {
                    Logger.Error($"Nitecrawler focuser Connect crashed: {ex}");
                    Notification.ShowError($"Nitecrawler focuser connection error: {ex.Message}");
                    return false;
                }
            }, token);
        }

        private bool ConnectCore() {
            // Verify, the focuser _uniqueId actually exists
            int[] ids = new int[NC_MAX_NUM];
            var scanErr = ScanFocusers(out var count, ids);
            if (scanErr != NC_ERROR_TYPE.NC_SUCCESS || !ids.Take(count).Contains(_uniqueId)) {
                Notification.ShowError(Loc.Instance["LblNitecrawlerFocuserNotAvailableError"]);
                Logger.Error($"Selected Nitecrawler focuser not available (disconnected?), scan result {scanErr}");
                return false;
            }

            var openErr = FocuserOpen(_uniqueId);
            if (openErr == NC_ERROR_TYPE.NC_SUCCESS) {
                DriverInfo = $"SDK: {DriverVersion}; FW: {GetFwVersionString()}";

                // A failed read leaves maxStep 0, which written back would set the focuser's max step to 0
                var configErr = GetFocuserConfig(_uniqueId, out var config);
                if (configErr == NC_ERROR_TYPE.NC_SUCCESS) {
                    readingMaxStepFromFocuser = true;
                    try {
                        TargetMaxStep = config.maxStep;
                    } finally {
                        readingMaxStepFromFocuser = false;
                    }
                } else {
                    Logger.Error($"Nitecrawler could not read the focuser configuration: {configErr}; leaving its max step as it is");
                }

                RaisePropertyChanged(nameof(MaxStep));

                Connected = true;
                return true;
            } else {
                Logger.Error($"Failed to connect to Nitecrawler focuser: {openErr}");
                return false;
            }
        }

        private string GetFwVersionString() {
            NC_VERSION version = new();
            var err = GetVersion(_uniqueId, out version);
            if (err != NC_ERROR_TYPE.NC_SUCCESS) {
                Logger.Error($"Nitecrawler could not read the focuser firmware version: {err}");
                return "Unknown";
            }

            uint major = (version.firmware >> 24) & 0xFF;
            uint minor = (version.firmware >> 16) & 0xFF;
            uint patch = (version.firmware >> 8) & 0xFF;

            return $"{major}.{minor}.{patch}";
        }

        public void Disconnect() {
            // first: a status read failing during the close must not start a removal check that disconnects again
            Connected = false;
            try {
                var err = FocuserClose(_uniqueId);
                if (err != NC_ERROR_TYPE.NC_SUCCESS) {
                    Logger.Error($"Nitecrawler failed to close the focuser: {err}");
                }
            } catch (Exception ex) {
                Logger.Error($"Nitecrawler focuser Disconnect crashed: {ex}");
            }
        }

        // Also runs from FocuserVM's cancellation callback, where an exception would escape into Cancel(): only logged,
        // without the removal check (a scan) that would hold up the cancellation
        public void Halt() {
            try {
                var err = FocuserStopMove(_uniqueId);
                if (err != NC_ERROR_TYPE.NC_SUCCESS) {
                    Logger.Error($"Nitecrawler failed to stop the focuser: {err}");
                }
            } catch (Exception ex) {
                Logger.Error($"Nitecrawler focuser Halt crashed: {ex}");
            }
        }

        public async Task Move(int position, CancellationToken ct, int waitInMs = 1000) {
            var lastPosition = int.MinValue;
            int samePositionCount = 0;
            var lastMovementTime = DateTime.Now;
            while (position != Position && !ct.IsCancellationRequested) {
                // Issue move command
                var err = FocuserMoveTo(_uniqueId, position);
                if (err != NC_ERROR_TYPE.NC_SUCCESS) {
                    Logger.Error($"Nitecrawler failed to issue move command {err}");
                    throw new Exception($"Failed to move focuser {err}");
                }

                await CoreUtil.Wait(TimeSpan.FromMilliseconds(100), ct);
                var (result, last) = await NativeMoveWait.WaitAsync(ReadMoveStatus, TimeSpan.FromMilliseconds(100), _sameFocuserPositionTimeout, () => DateTime.UtcNow, ct);
                if (result == MoveWaitResult.Stalled) {
                    Halt();
                    throw new Exception($"Focuser stopped: it reported moving for {_sameFocuserPositionTimeout.TotalMinutes:F0} min without leaving position {last.Position}");
                }

                await CoreUtil.Wait(TimeSpan.FromMilliseconds(100), ct);

                if (lastPosition == Position) {
                    ++samePositionCount;
                    var samePositionTime = DateTime.Now - lastMovementTime;
                    if (samePositionTime >= _sameFocuserPositionTimeout) {
                        throw new Exception($"Focuser stuck at position {lastPosition} beyond {_sameFocuserPositionTimeout} timeout");
                    }

                    // Make sure we wait in between Move requests when no progress is being made
                    // to avoid spamming the driver and spiking the CPU
                    await CoreUtil.Wait(TimeSpan.FromSeconds(1), ct);
                } else {
                    lastMovementTime = DateTime.Now;
                }

                lastPosition = (int)last.Position;
            }
        }

        private MoveStatus ReadMoveStatus() {
            var err = GetFocuserStatus(_uniqueId, out var status);
            if (err == NC_ERROR_TYPE.NC_SUCCESS) {
                System.Threading.Interlocked.Exchange(ref _consecutiveCommErrors, 0);
                return new MoveStatus(true, status.moving == 1, status.position);
            }

            if (err == NC_ERROR_TYPE.NC_ERROR_COMMUNICATION && HandleCommunicationError("move status poll")) {
                // Transient glitch and the device is still present - keep polling rather than aborting the move
                return MoveStatus.Unread;
            }

            if (err != NC_ERROR_TYPE.NC_ERROR_COMMUNICATION) {
                Logger.Error($"Nitecrawler error to get moving state {err}");
            }

            throw new Exception($"Focuser error {err}");
        }

        #region Unsupported
        public bool TempCompAvailable => false;
        public bool TempComp { get; set; }
        public bool HasSetupDialog => false;

        public IList<string> SupportedActions => new List<string>();
        public void SetupDialog() {
        }

        public string Action(string actionName, string actionParameters) {
            throw new NotImplementedException();
        }
        public void SendCommandBlind(string command, bool raw = true) {
            throw new NotImplementedException();
        }

        public bool SendCommandBool(string command, bool raw = true) {
            throw new NotImplementedException();
        }

        public string SendCommandString(string command, bool raw = true) {
            throw new NotImplementedException();
        }
        #endregion
    }
}
