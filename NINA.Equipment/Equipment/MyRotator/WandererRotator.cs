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
using NINA.Astrometry;
using NINA.Core.Locale;
using NINA.Core.MyMessageBox;
using NINA.Core.Utility;
using NINA.Core.Utility.Notification;
using NINA.Equipment.Interfaces;
using NINA.Profile.Interfaces;
using static Wanderer.WandererRotatorSDK;


namespace NINA.Equipment.Equipment.MyRotator {
    public partial class WandererRotator : BaseINPC, IRotator {
        private readonly IProfileService _profileService;
        private readonly int _uniqueId;
        private IRotatorSettings _rotatorSettings;

        public WandererRotator(int id, string model, IProfileService profileService) {
            Id = $"{id}.{model}";
            _profileService = profileService;
            _uniqueId = id;

            Name = $"Wanderer.Rotator.{_uniqueId} ({model})";

            // Subscribe to profile/setting changes so toggling the profile entry immediately updates the device
            _rotatorSettings = _profileService?.ActiveProfile?.RotatorSettings;
            if (_rotatorSettings != null) {
                _rotatorSettings.PropertyChanged += RotatorSettingsChanged;
            }
            _profileService.ProfileChanged += ProfileChanged;
        }

        public bool IsMoving {
            get {
                if (!Connected) {
                    return false;
                }
                try {
                    var err = RotatorGetStatus(_uniqueId, out var status);
                    if (err == WR_ERROR_TYPE.WR_SUCCESS) {
                        return status.moving == 1;
                    } else {
                        if (err == WR_ERROR_TYPE.WR_ERROR_COMMUNICATION) {
                            Logger.Error($"WandererRotator communication error to get moving state {err}");
                            OnCommunicationError();
                        } else {
                            Logger.Error($"WandererRotator error to get moving state {err}");
                        }
                        return false;
                    }
                } catch (Exception ex) {
                    Logger.Error($"WandererRotator IsMoving crashed: {ex}");
                    OnCommunicationError();
                    return false;
                }
            }
        }

        public bool CanReverse => true;
        public bool Reverse {
            get {
                if (!Connected) {
                    return false;
                }
                try {
                    var err = RotatorGetConfig(_uniqueId, out var config);
                    if (err == WR_ERROR_TYPE.WR_SUCCESS) {
                        return config.reverseDirection != 0;
                    } else {
                        Logger.Error($"WandererRotator error to get reverse direction state {err}");
                        return false;
                    }
                } catch (Exception ex) {
                    Logger.Error($"WandererRotator Reverse getter crashed: {ex}");
                    OnCommunicationError();
                    return false;
                }
            }
            set {
                try {
                    WR_ROTATOR_CONFIG config = new() {
                        mask = (uint)MASK_ROTATOR_REVERSE_DIRECTION,
                        reverseDirection = value ? 1 : 0
                    };
                    _ = RotatorSetConfig(_uniqueId, config);
                    RaisePropertyChanged(nameof(Reverse));
                } catch (Exception ex) {
                    Logger.Error($"WandererRotator Reverse setter crashed: {ex}");
                    OnCommunicationError();
                }
            }
        }

        public bool CanOvershoot => true;
        public bool Overshoot {
            get {
                if (!Connected) {
                    return false;
                }
                try {
                    var err = RotatorGetConfig(_uniqueId, out var config);
                    if (err == WR_ERROR_TYPE.WR_SUCCESS) {
                        return config.overshoot != 0;
                    } else {
                        Logger.Error($"WandererRotator error to get overshoot state {err}");
                        return false;
                    }
                } catch (Exception ex) {
                    Logger.Error($"WandererRotator Overshoot getter crashed: {ex}");
                    OnCommunicationError();
                    return false;
                }
            }
            set {
                try {
                    WR_ROTATOR_CONFIG config = new() {
                        mask = (uint)MASK_ROTATOR_OVERSHOOT,
                        overshoot = value ? 1 : 0
                    };
                    _ = RotatorSetConfig(_uniqueId, config);
                    RaisePropertyChanged();
                } catch (Exception ex) {
                    Logger.Error($"WandererRotator Overshoot setter crashed: {ex}");
                    OnCommunicationError();
                }
            }
        }

        public float OvershootAngle {
            get {
                if (!Connected) {
                    return float.NaN;
                }
                try {
                    var err = RotatorGetConfig(_uniqueId, out var config);
                    if (err == WR_ERROR_TYPE.WR_SUCCESS) {
                        return config.overshootAngle;
                    } else {
                        Logger.Error($"WandererRotator error to get overshoot angle {err}");
                        return float.NaN;
                    }
                } catch (Exception ex) {
                    Logger.Error($"WandererRotator OvershootAngle getter crashed: {ex}");
                    OnCommunicationError();
                    return float.NaN;
                }
            }
            set {
                try {
                    WR_ROTATOR_CONFIG config = new() {
                        mask = (uint)MASK_ROTATOR_OVERSHOOT_ANGLE,
                        overshootAngle = value
                    };
                    _ = RotatorSetConfig(_uniqueId, config);
                    RaisePropertyChanged();
                } catch (Exception ex) {
                    Logger.Error($"WandererRotator OvershootAngle setter crashed: {ex}");
                    OnCommunicationError();
                }
            }
        }

        public bool OvershootDirection {
            get {
                if (!Connected) {
                    return false;
                }
                try {
                    var err = RotatorGetConfig(_uniqueId, out var config);
                    if (err == WR_ERROR_TYPE.WR_SUCCESS) {
                        return config.overshootDirection != 0;
                    } else {
                        Logger.Error($"WandererRotator error to get overshoot direction {err}");
                        return false;
                    }
                } catch (Exception ex) {
                    Logger.Error($"WandererRotator OvershootDirection getter crashed: {ex}");
                    OnCommunicationError();
                    return false;
                }
            }
            set {
                try {
                    WR_ROTATOR_CONFIG config = new() {
                        mask = (uint)MASK_ROTATOR_OVERSHOOT_DIRECTION,
                        overshootDirection = value ? 1 : 0
                    };
                    _ = RotatorSetConfig(_uniqueId, config);
                    RaisePropertyChanged();
                } catch (Exception ex) {
                    Logger.Error($"WandererRotator OvershootDirection setter crashed: {ex}");
                    OnCommunicationError();
                }
            }
        }

        public bool CanBacklash => true;
        public float Backlash {
            get {
                if (!Connected) {
                    return float.NaN;
                }
                try {
                    var err = RotatorGetConfig(_uniqueId, out var config);
                    if (err == WR_ERROR_TYPE.WR_SUCCESS) {
                        return config.backlash;
                    } else {
                        Logger.Error($"WandererRotator error to get backlash {err}");
                        return float.NaN;
                    }
                } catch (Exception ex) {
                    Logger.Error($"WandererRotator Backlash getter crashed: {ex}");
                    OnCommunicationError();
                    return float.NaN;
                }
            }
            set {
                try {
                    WR_ROTATOR_CONFIG config = new() {
                        mask = (uint)MASK_ROTATOR_BACKLASH,
                        backlash = value
                    };
                    _ = RotatorSetConfig(_uniqueId, config);
                    RaisePropertyChanged();
                } catch (Exception ex) {
                    Logger.Error($"WandererRotator Backlash setter crashed: {ex}");
                    OnCommunicationError();
                }
            }
        }

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

        public float MechanicalPosition {
            get {
                if (!Connected) {
                    return -1;
                }
                try {
                    var err = RotatorGetStatus(_uniqueId, out var status);
                    if (err == WR_ERROR_TYPE.WR_SUCCESS) {
                        return status.position;
                    } else {
                        if (err == WR_ERROR_TYPE.WR_ERROR_COMMUNICATION) {
                            Logger.Error($"WandererRotator communication error to get Position state {err}");
                            OnCommunicationError();
                        } else {
                            Logger.Error($"WandererRotator error to get Position {err}");
                        }
                        return -1;
                    }
                } catch (Exception ex) {
                    Logger.Error($"WandererRotator MechanicalPosition crashed: {ex}");
                    OnCommunicationError();
                    return -1;
                }
            }
        }

        public void Sync(float skyAngle) {
            offset = skyAngle - MechanicalPosition;
            RaisePropertyChanged(nameof(Position));
            Synced = true;
            Logger.Debug($"Mechanical Position is {MechanicalPosition}° - Sync Position to Sky Angle {skyAngle}° using offset {offset}");
        }

        // A single failed read (e.g. a status poll timing out under USB load) must not tear down the connection, as
        // for the Nitecrawler: the rotator only counts as lost after MaxCommunicationErrors within CommunicationErrorWindow.
        private const int MaxCommunicationErrors = 3;
        private static readonly TimeSpan CommunicationErrorWindow = TimeSpan.FromSeconds(30);
        private readonly Queue<DateTime> communicationErrors = new();

        /// <summary>
        /// Counts a communication error and disconnects once there were too many in a short time. Returns true while the
        /// connection is kept.
        /// </summary>
        private bool OnCommunicationError() {
            lock (communicationErrors) {
                var now = DateTime.UtcNow;
                communicationErrors.Enqueue(now);
                while (communicationErrors.Count > 0 && now - communicationErrors.Peek() > CommunicationErrorWindow) {
                    communicationErrors.Dequeue();
                }

                if (communicationErrors.Count < MaxCommunicationErrors) {
                    Logger.Warning($"WandererRotator communication error {communicationErrors.Count}/{MaxCommunicationErrors} within {CommunicationErrorWindow.TotalSeconds:F0}s, keeping the connection");
                    return true;
                }

                communicationErrors.Clear();
            }

            try {
                Notification.ShowWarning(Loc.Instance["LblRotatorConnectionLost"]);
                Logger.Error($"WandererRotator device was removed");
                Disconnect();
            } catch (Exception ex) {
                Logger.Error(ex);
            }

            return false;
        }

        public float StepSize {
            get {
                if (!Connected) {
                    return float.NaN;
                }
                try {
                    var err = RotatorGetStatus(_uniqueId, out var status);
                    if (err == WR_ERROR_TYPE.WR_SUCCESS) {
                        return status.stepSize;
                    } else {
                        if (err == WR_ERROR_TYPE.WR_ERROR_COMMUNICATION) {
                            Logger.Error($"WandererRotator communication error to get step size state {err}");
                            OnCommunicationError();
                        } else {
                            Logger.Error($"WandererRotator error to get Position {err}");
                        }
                        return -1;
                    }
                } catch (Exception ex) {
                    Logger.Error($"WandererRotator StepSize crashed: {ex}");
                    OnCommunicationError();
                    return -1;
                }
            }
        }
        public string Id { get; }
        public string Name { get; }
        public string DisplayName => Name;

        public string Category => "WandererRotator";

        [ObservableProperty]
        private bool connected = false;

        [RelayCommand]
        public void ResetPosition() {
            try {
                if (MyMessageBox.Show(Loc.Instance["LblZwoResetZeroPositionPrompt"], "", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxResult.No) == System.Windows.MessageBoxResult.Yes) {
                    if (Position > 0) {
                        RotatorSyncPosition(_uniqueId, 0);
                        RaisePropertyChanged(nameof(Position));
                    }
                }
            } catch (Exception ex) {
                Logger.Error($"WandererRotator ResetPosition crashed: {ex}");
                OnCommunicationError();
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
            try {
                if (MyMessageBox.Show(Loc.Instance["LblWandererRotatorSyncPositionPrompt"], "", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxResult.No) == System.Windows.MessageBoxResult.Yes) {
                    if (Position != SyncPosition) {
                        RotatorSyncPosition(_uniqueId, SyncPosition);
                        RaisePropertyChanged(nameof(Position));
                    }
                }
            } catch (Exception ex) {
                Logger.Error($"WandererRotator SyncToPosition crashed: {ex}");
                OnCommunicationError();
            }
        }

        public string Description => "Native driver for WandererRotator Rotators";

        public string DriverInfo { get; private set; } = string.Empty;

        public string DriverVersion => GetSDKVersion();

        public Task<bool> Connect(CancellationToken token) {
            return Task.Run(() => {
                try {
                    // Verify, the Rotator _uniqueId actually exists
                    int[] ids = new int[WR_MAX_NUM];
                    var scanErr = RotatorScan(out var count, ids);
                    if (scanErr != WR_ERROR_TYPE.WR_SUCCESS) {
                        Notification.ShowError(Loc.Instance["LblWandererRotatorNotAvailableError"]);
                        Logger.Error($"WandererRotator scan failed: {scanErr}");
                        return false;
                    }
                    if (!ids.Take(count).Contains(_uniqueId)) {
                        Notification.ShowError(Loc.Instance["LblWandererRotatorNotAvailableError"]);
                        Logger.Error("Selected WandererRotator Rotator not available (disconnected?)");
                        return false;
                    }
                    var openErr = RotatorOpen(_uniqueId);
                    if (openErr == WR_ERROR_TYPE.WR_SUCCESS) {
                        DriverInfo = $"SDK: {DriverVersion}; FW: {GetFwVersionString()}";

                        // Set overshoot settings from profile
                        var settings = _profileService.ActiveProfile.RotatorSettings;
                        Overshoot = settings.Overshoot;
                        OvershootDirection = settings.OvershootDirection;
                        OvershootAngle = settings.OvershootAngle;

                        Connected = true;
                        return true;
                    } else {
                        Logger.Error($"Failed to connect to WandererRotator Rotator: {openErr}");
                        return false;
                    }
                } catch (Exception ex) {
                    Logger.Error($"WandererRotator Connect crashed: {ex}");
                    Notification.ShowError($"WandererRotator connection error: {ex.Message}");
                    return false;
                }
            }, token);
        }

        // React to runtime changes of the active profile's rotator settings
        private void RotatorSettingsChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e) {
            try {
                // Apply only when device is connected
                if (!Connected) return;
                var settings = _profileService.ActiveProfile.RotatorSettings;
                switch (e.PropertyName) {
                    case nameof(IRotatorSettings.Overshoot):
                        Overshoot = settings.Overshoot;
                        break;
                    case nameof(IRotatorSettings.OvershootDirection):
                        OvershootDirection = settings.OvershootDirection;
                        break;
                    case nameof(IRotatorSettings.OvershootAngle):
                        OvershootAngle = settings.OvershootAngle;
                        break;
                }
            } catch (Exception ex) {
                Logger.Error(ex);
            }
        }

        private void ProfileChanged(object sender, EventArgs e) {
            try {
                // Re-hook new profile's settings and apply them if rotator is connected
                if (_rotatorSettings != null) _rotatorSettings.PropertyChanged -= RotatorSettingsChanged;
                _rotatorSettings = _profileService.ActiveProfile.RotatorSettings;
                if (_rotatorSettings != null) _rotatorSettings.PropertyChanged += RotatorSettingsChanged;

                if (Connected && _profileService.ActiveProfile?.RotatorSettings != null) {
                    var s = _profileService.ActiveProfile.RotatorSettings;
                    Overshoot = s.Overshoot;
                    OvershootDirection = s.OvershootDirection;
                    OvershootAngle = s.OvershootAngle;
                }
            } catch (Exception ex) {
                Logger.Error(ex);
            }
        }

        private string GetFwVersionString() {
            try {
                WR_VERSION version = new();
                _ = RotatorGetVersion(_uniqueId, out version);

                uint major = (version.firmware >> 24) & 0xFF;
                uint minor = (version.firmware >> 16) & 0xFF;
                uint patch = (version.firmware >> 8) & 0xFF;

                return $"{major}.{minor}.{patch}";
            } catch (Exception ex) {
                Logger.Error($"WandererRotator GetFwVersionString crashed: {ex}");
                return "Unknown";
            }
        }

        public void Disconnect() {
            try {
                _ = RotatorClose(_uniqueId);
            } catch (Exception ex) {
                Logger.Error($"WandererRotator Disconnect crashed: {ex}");
            } finally {
                Connected = false;
            }
        }

        public void Halt() {
            try {
                RotatorStopMove(_uniqueId);
            } catch (Exception ex) {
                Logger.Error($"WandererRotator Halt crashed: {ex}");
                OnCommunicationError();
            }
        }

        public async Task<bool> Move(float angle, CancellationToken ct) {
            if (!Connected) {
                return false;
            }

            try {
                if (angle >= 360) {
                    angle = AstroUtil.EuclidianModulus(angle, 360);
                }
                if (angle <= -360) {
                    angle = AstroUtil.EuclidianModulus(angle, -360);
                }

                Logger.Debug($"Move relative by {angle}° - Mechanical Position reported by rotator {MechanicalPosition}° and offset {offset}");

                var err = RotatorMove(_uniqueId, angle);
                if (err != WR_ERROR_TYPE.WR_SUCCESS) {
                    Logger.Error($"WandererRotator failed to issue move command {err}");
                    throw new Exception($"Failed to move Rotator {err}");
                }

                await CoreUtil.Wait(TimeSpan.FromMilliseconds(100), ct);
                WR_ROTATOR_STATUS status;
                do {
                    err = RotatorGetStatus(_uniqueId, out status);

                    var moving = status.moving == 1;
                    if (err != WR_ERROR_TYPE.WR_SUCCESS) {
                        if (err != WR_ERROR_TYPE.WR_ERROR_COMMUNICATION) {
                            Logger.Error($"WandererRotator error to get moving state {err}");
                            throw new Exception($"Rotator error {err}");
                        }

                        if (!OnCommunicationError()) {
                            throw new Exception($"Rotator error {err}");
                        }

                        // A single failed poll: keep waiting for the move rather than aborting it
                        moving = true;
                    }

                    await CoreUtil.Wait(TimeSpan.FromMilliseconds(100), ct);
                    if (!moving) {
                        break;
                    }
                } while (!ct.IsCancellationRequested);

                return true;
            } catch (OperationCanceledException) {
                Logger.Info("WandererRotator Move cancelled by user");
                Halt();
                return false;
            } catch (Exception ex) {
                Logger.Error($"WandererRotator Move crashed: {ex}");
                // Not again when the status poll's error already disconnected it
                if (Connected) {
                    OnCommunicationError();
                }

                return false;
            }
        }

        public async Task<bool> MoveAbsoluteMechanical(float targetPosition, CancellationToken ct) {
            if (!Connected) {
                return false;
            }

            var movement = targetPosition - MechanicalPosition;
            return await Move(movement, ct);
        }

        public async Task<bool> MoveAbsolute(float targetPosition, CancellationToken ct) {
            if (!Connected) {
                return false;
            }

            return await Move(targetPosition - Position, ct);
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
