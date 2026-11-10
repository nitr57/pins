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
                        communicationErrors.RecordSuccess();
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
                    CheckConfigSet(RotatorSetConfig(_uniqueId, config), "reverse direction");
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
                    CheckConfigSet(RotatorSetConfig(_uniqueId, config), "overshoot");
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
                    CheckConfigSet(RotatorSetConfig(_uniqueId, config), "overshoot angle");
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
                    CheckConfigSet(RotatorSetConfig(_uniqueId, config), "overshoot direction");
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
                    CheckConfigSet(RotatorSetConfig(_uniqueId, config), "backlash");
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

        // -1 when the position can't be read, for the displayed values; a move or sync reads it through
        // ReadMechanicalPositionForMove instead
        public float MechanicalPosition => TryReadMechanicalPosition(out var position) ? position : -1;

        private bool TryReadMechanicalPosition(out float position) {
            position = -1;
            if (!Connected) {
                return false;
            }

            try {
                var err = RotatorGetStatus(_uniqueId, out var status);
                if (err == WR_ERROR_TYPE.WR_SUCCESS) {
                    communicationErrors.RecordSuccess();
                    position = status.position;
                    return true;
                }

                if (err == WR_ERROR_TYPE.WR_ERROR_COMMUNICATION) {
                    Logger.Error($"WandererRotator communication error to get Position state {err}");
                    OnCommunicationError();
                } else {
                    Logger.Error($"WandererRotator error to get Position {err}");
                }

                return false;
            } catch (Exception ex) {
                Logger.Error($"WandererRotator MechanicalPosition crashed: {ex}");
                OnCommunicationError();
                return false;
            }
        }

        /// <summary>
        /// The position a move or sync starts from. Now that a single failed read keeps the connection, the -1 of a
        /// failed read would turn into a move by the wrong angle or a wrong sync offset, so a failed read is tried once
        /// more and then the move or sync is refused.
        /// </summary>
        private float ReadMechanicalPositionForMove() {
            if (TryReadMechanicalPosition(out var position)) {
                return position;
            }

            Thread.Sleep(200);
            if (TryReadMechanicalPosition(out position)) {
                return position;
            }

            throw new InvalidOperationException("Could not read the position of the Wanderer rotator");
        }

        public void Sync(float skyAngle) {
            var mechanicalPosition = ReadMechanicalPositionForMove();
            offset = skyAngle - mechanicalPosition;
            RaisePropertyChanged(nameof(Position));
            Synced = true;
            Logger.Debug($"Mechanical Position is {mechanicalPosition}° - Sync Position to Sky Angle {skyAngle}° using offset {offset}");
        }

        // A single failed read (e.g. a status poll timing out under USB load) must not tear down the connection, as
        // for the Nitecrawler: the rotator only counts as lost after 3 errors within 30 s with no successful read in
        // between. The up to three status reads of one poll (Position, IsMoving, MechanicalPosition) fail together in
        // a hang, so errors within a second count as one.
        private readonly CommunicationErrorCounter communicationErrors = new(3, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(1));

        // A setting the rotator refused is logged; the getters read the rotator, so the change notification that
        // follows shows the value it kept. During Connect (not connected yet) a communication error is only logged.
        private void CheckConfigSet(WR_ERROR_TYPE err, string setting) {
            if (err == WR_ERROR_TYPE.WR_SUCCESS) {
                return;
            }

            Logger.Error($"WandererRotator failed to set the {setting}: {err}");

            if (err == WR_ERROR_TYPE.WR_ERROR_COMMUNICATION && Connected) {
                OnCommunicationError();
            }
        }

        /// <summary>
        /// Counts a communication error and disconnects once there were too many in a short time. Returns true while the
        /// connection is kept.
        /// </summary>
        private bool OnCommunicationError() {
            if (communicationErrors.RecordError(DateTime.UtcNow, out var count)) {
                Logger.Warning($"WandererRotator communication error {count}/{communicationErrors.MaxErrors} within {communicationErrors.Window.TotalSeconds:F0}s, keeping the connection");
                return true;
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
                var err = RotatorGetVersion(_uniqueId, out version);
                if (err != WR_ERROR_TYPE.WR_SUCCESS) {
                    Logger.Error($"WandererRotator could not read the firmware version: {err}");
                    return "Unknown";
                }

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
                bool wasMoving = RotatorGetStatus(_uniqueId, out var status) == WR_ERROR_TYPE.WR_SUCCESS && status.moving == 1;

                var err = RotatorStopMove(_uniqueId);
                if (err != WR_ERROR_TYPE.WR_SUCCESS) {
                    Logger.Error($"WandererRotator failed to stop the rotator: {err}");
                } else if (wasMoving) {
                    // timed from after the stop was sent, when the SDK's own wait for the reply starts
                    haltedMove = new HaltedMove(DateTime.UtcNow, status.position);
                }
            } catch (Exception ex) {
                Logger.Error($"WandererRotator Halt crashed: {ex}");
                OnCommunicationError();
            }
        }

        // How far the end of a move may be from start + angle (RotatorVM waits for 1° of the target)
        private const double MoveEndToleranceDegrees = 1.0;

        // A running move Halt stopped: the SDK reports the rotator stopped at once, but updates its position only when
        // its listener reads the stop reply (WandererRotatorProtocol.cpp, kStopReplyTimeoutMs). A move started right
        // after (ninaAPI cancels the running move and starts the next) would otherwise read the position from before
        // the stop. The SDK waits 3 s for the reply, then stops and queries the rotator again, hence the margin; a halt
        // before the motor moved leaves the position unchanged and costs the whole wait.
        private sealed record HaltedMove(DateTime At, float Position);

        private static readonly TimeSpan StopReplyTimeout = TimeSpan.FromSeconds(5);
        private volatile HaltedMove haltedMove;

        private async Task WaitForStopPositionAsync(CancellationToken ct) {
            if (haltedMove is not { } halted) {
                return;
            }

            while (DateTime.UtcNow - halted.At < StopReplyTimeout) {
                if (RotatorGetStatus(_uniqueId, out var status) == WR_ERROR_TYPE.WR_SUCCESS && status.position != halted.Position) {
                    break;
                }

                await CoreUtil.Wait(TimeSpan.FromMilliseconds(100), ct);
            }

            haltedMove = null;
        }

        /// <summary>How far <paramref name="end"/> is from <paramref name="start"/> + <paramref name="angle"/>, in degrees (0..180).</summary>
        internal static double EndOffset(double start, double angle, double end) {
            return Math.Abs(AstroUtil.EuclidianModulus(end - (start + angle) + 180, 360) - 180);
        }

        /// <summary>
        /// Moves by <paramref name="angle"/>. A move that fails throws: RotatorVM ignores the result and waits for the
        /// position to reach the target, so a false would leave the rotate step waiting until it is cancelled. The SDK
        /// ends a move that does not answer itself (at least 90 s, then a stop and a fresh status), but then reports it
        /// as stopped, so the position it ended at is checked against the target.
        /// </summary>
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

                await WaitForStopPositionAsync(ct);

                // the SDK's position of the same frame as the one the move ends at (null when it can't be read)
                float? startPosition = RotatorGetStatus(_uniqueId, out var before) == WR_ERROR_TYPE.WR_SUCCESS ? before.position : null;

                var err = RotatorMove(_uniqueId, angle);
                if (err != WR_ERROR_TYPE.WR_SUCCESS) {
                    Logger.Error($"WandererRotator failed to issue move command {err}");
                    if (err == WR_ERROR_TYPE.WR_ERROR_COMMUNICATION) {
                        OnCommunicationError();
                    }

                    throw new RotatorErrorException($"Failed to move Rotator {err}");
                }

                await CoreUtil.Wait(TimeSpan.FromMilliseconds(100), ct);

                // no stall timeout: the SDK updates the position only when a move ends (WandererRotatorSDK.cpp
                // WRRotatorGetStatus), and a 360° move takes about 100 s
                var (_, last) = await NativeMoveWait.WaitAsync(ReadMoveStatus, TimeSpan.FromMilliseconds(100), null, () => DateTime.UtcNow, ct);
                await CoreUtil.Wait(TimeSpan.FromMilliseconds(100), ct);

                if (startPosition is { } from) {
                    double off = EndOffset(from, angle, last.Position);
                    if (off > MoveEndToleranceDegrees) {
                        throw new RotatorErrorException($"Rotator stopped at {last.Position:F2}°, {off:F1}° from the target {AstroUtil.EuclidianModulus(from + angle, 360):F2}°");
                    }
                }

                return true;
            } catch (OperationCanceledException) {
                Logger.Info("WandererRotator Move cancelled by user");
                Halt();
                return false;
            } catch (RotatorErrorException ex) {
                // An error the rotator reported, already counted above when it was a communication error
                Logger.Error($"WandererRotator Move failed: {ex.Message}");
                throw;
            } catch (Exception ex) {
                Logger.Error($"WandererRotator Move crashed: {ex}");
                // Not again when the status poll's error already disconnected it
                if (Connected) {
                    OnCommunicationError();
                }

                throw new RotatorErrorException($"Rotator move failed: {ex.Message}", ex);
            }
        }

        private MoveStatus ReadMoveStatus() {
            var err = RotatorGetStatus(_uniqueId, out var status);
            if (err == WR_ERROR_TYPE.WR_SUCCESS) {
                communicationErrors.RecordSuccess();
                return new MoveStatus(true, status.moving == 1, status.position);
            }

            if (err != WR_ERROR_TYPE.WR_ERROR_COMMUNICATION) {
                Logger.Error($"WandererRotator error to get moving state {err}");
                throw new RotatorErrorException($"Rotator error {err}");
            }

            if (!OnCommunicationError()) {
                throw new RotatorErrorException($"Rotator error {err}");
            }

            // A single failed poll: keep waiting for the move rather than aborting it
            return MoveStatus.Unread;
        }

        public async Task<bool> MoveAbsoluteMechanical(float targetPosition, CancellationToken ct) {
            if (!Connected) {
                return false;
            }

            await WaitForStopPositionAsync(ct);

            var movement = targetPosition - ReadMechanicalPositionForMove();
            return await Move(movement, ct);
        }

        public async Task<bool> MoveAbsolute(float targetPosition, CancellationToken ct) {
            if (!Connected) {
                return false;
            }

            await WaitForStopPositionAsync(ct);

            var position = AstroUtil.EuclidianModulus(ReadMechanicalPositionForMove() + offset, 360);
            return await Move(targetPosition - position, ct);
        }

        private sealed class RotatorErrorException(string message, Exception innerException = null) : Exception(message, innerException);

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
