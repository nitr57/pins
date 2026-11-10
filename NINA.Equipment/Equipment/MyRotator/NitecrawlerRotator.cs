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
using static Nitecrawler.NitecrawlerSDK;


namespace NINA.Equipment.Equipment.MyRotator {
    public partial class NitecrawlerRotator : BaseINPC, IRotator {
        private readonly IProfileService _profileService;
        private readonly int _uniqueId;

        public NitecrawlerRotator(int id, uint serial, IProfileService profileService) {
            Id = $"{serial}";
            _profileService = profileService;
            _uniqueId = id;

            Name = $"Nitecrawler Rotator.{_uniqueId} ({serial})";
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

        public bool CanReverse => true;
        public bool Reverse {
            get {
                if (!Connected) {
                    return false;
                }
                var err = GetRotatorConfig(_uniqueId, out var config);
                if (err == NC_ERROR_TYPE.NC_SUCCESS) {
                    return config.reverseDirection != 0;
                } else {
                    Logger.Error($"Nitecrawler error to get reverse direction state {err}");
                    return false;
                }
            }
            set {
                NC_ROTATOR_CONFIG config = new() {
                    mask = (uint)MASK_ROTATOR_REVERSE_DIRECTION,
                    reverseDirection = value ? 1 : 0
                };
                var err = SetRotatorConfig(_uniqueId, config);
                if (err != NC_ERROR_TYPE.NC_SUCCESS) {
                    // the getter reads the rotator again, so the change below shows the direction it kept
                    Logger.Error($"Nitecrawler failed to set the reverse direction to {value}: {err}");

                    if (err == NC_ERROR_TYPE.NC_ERROR_COMMUNICATION && Connected) {
                        HandleCommunicationError("set reverse direction");
                    }
                }

                RaisePropertyChanged(nameof(Reverse));
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
                if (!TryGetStatus("get Position", out var status)) {
                    return -1;
                }
                return status.position;
            }
        }

        /// <summary>
        /// The position a move or sync starts from. Since a single failed read keeps the connection, the -1 of a failed
        /// read turned into a move by the wrong angle or a wrong sync offset, so a failed read is tried once more and then
        /// the move or sync is refused (as for the Wanderer).
        /// </summary>
        private float ReadMechanicalPositionForMove() {
            if (Connected && TryGetStatus("get Position", out var status)) {
                return status.position;
            }

            Thread.Sleep(200);
            if (Connected && TryGetStatus("get Position", out status)) {
                return status.position;
            }

            throw new InvalidOperationException("Could not read the position of the Nitecrawler rotator");
        }

        public void Sync(float skyAngle) {
            var mechanicalPosition = ReadMechanicalPositionForMove();
            offset = skyAngle - mechanicalPosition;
            RaisePropertyChanged(nameof(Position));
            Synced = true;
            Logger.Debug($"Mechanical Position is {mechanicalPosition}° - Sync Position to Sky Angle {skyAngle}° using offset {offset}");
        }

        private void DisconnectOnRemovedError() {
            try {
                Notification.ShowWarning(Loc.Instance["LblRotatorConnectionLost"]);
                Logger.Error($"Nitecrawler device was removed");
                Disconnect();
            } catch (Exception ex) {
                Logger.Error(ex);
            }
        }

        // A single transient serial glitch (e.g. a polled status read timing out under USB load)
        // must not tear down the whole connection. We only treat NC_ERROR_COMMUNICATION as a
        // removal once we can confirm it - either the device is no longer enumerated, or it has
        // failed this many consecutive times despite still being present (e.g. firmware hung).
        private const int _maxConsecutiveCommErrors = 3;
        private int _consecutiveCommErrors = 0;

        /// <summary>
        /// Reads the rotator status, tolerating transient communication errors instead of
        /// disconnecting on the first glitch. Returns true on success; on failure the caller
        /// should fall back to a safe value and the connection is only dropped on a confirmed removal.
        /// </summary>
        private bool TryGetStatus(string context, out NC_ROTATOR_STATUS status) {
            var err = GetRotatorStatus(_uniqueId, out status);
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
            // Definitive check: is the device still physically enumerated? ScanRotators reports
            // already-open ports without re-handshaking, so it is safe to call while connected.
            int[] ids = new int[NC_MAX_NUM];
            var scanErr = ScanRotators(out var count, ids);
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

        public float StepSize {
            get {
                if (!Connected) {
                    return float.NaN;
                }
                if (!TryGetStatus("get step size", out var status)) {
                    return -1;
                }
                return status.stepSize;
            }
        }
        public string Id { get; }
        public string Name { get; }
        public string DisplayName => Name;

        public string Category => "Nitecrawler";

        [ObservableProperty]
        private bool connected = false;

        [RelayCommand]
        public void ResetPosition() {
            if (MyMessageBox.Show(Loc.Instance["LblZwoResetZeroPositionPrompt"], "", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxResult.No) == System.Windows.MessageBoxResult.Yes) {
                if (Position > 0) {
                    RotatorSyncPosition(_uniqueId, 0);
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
                    RotatorSyncPosition(_uniqueId, SyncPosition);
                    RaisePropertyChanged(nameof(Position));
                }
            }
        }

        public string Description => "Native driver for Nitecrawler Rotators";

        public string DriverInfo { get; private set; } = string.Empty;

        public string DriverVersion => GetSDKVersion();

        public Task<bool> Connect(CancellationToken token) {
            return Task.Run(() => {
                try {
                    // Verify, the Rotator _uniqueId actually exists
                    int[] ids = new int[NC_MAX_NUM];
                    var scanErr = ScanRotators(out var count, ids);
                    if (scanErr != NC_ERROR_TYPE.NC_SUCCESS || !ids.Take(count).Contains(_uniqueId)) {
                        Notification.ShowError(Loc.Instance["LblNitecrawlerRotatorNotAvailableError"]);
                        Logger.Error($"Selected Nitecrawler Rotator not available (disconnected?), scan result {scanErr}");
                        return false;
                    }

                    var openErr = RotatorOpen(_uniqueId);
                    if (openErr == NC_ERROR_TYPE.NC_SUCCESS) {
                        DriverInfo = $"SDK: {DriverVersion}; FW: {GetFwVersionString()}";

                        GetRotatorConfig(_uniqueId, out var config);

                        Connected = true;
                        return true;
                    } else {
                        Logger.Error($"Failed to connect to Nitecrawler Rotator: {openErr}");
                        return false;
                    }
                } catch (Exception ex) {
                    Logger.Error($"Nitecrawler Rotator Connect crashed: {ex}");
                    Notification.ShowError($"Nitecrawler Rotator connection error: {ex.Message}");
                    return false;
                }
            }, token);
        }

        private string GetFwVersionString() {
            NC_VERSION version = new();
            var err = GetVersion(_uniqueId, out version);
            if (err != NC_ERROR_TYPE.NC_SUCCESS) {
                Logger.Error($"Nitecrawler could not read the rotator firmware version: {err}");
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
                var err = RotatorClose(_uniqueId);
                if (err != NC_ERROR_TYPE.NC_SUCCESS) {
                    Logger.Error($"Nitecrawler failed to close the rotator: {err}");
                }
            } catch (Exception ex) {
                Logger.Error($"Nitecrawler Rotator Disconnect crashed: {ex}");
            }
        }

        // Also runs from RotatorVM's cancellation callback, where an exception would escape into Cancel(): only logged,
        // without the removal check (a scan) that would hold up the cancellation
        public void Halt() {
            try {
                var err = RotatorStopMove(_uniqueId);
                if (err != NC_ERROR_TYPE.NC_SUCCESS) {
                    Logger.Error($"Nitecrawler failed to stop the rotator: {err}");
                }
            } catch (Exception ex) {
                Logger.Error($"Nitecrawler Rotator Halt crashed: {ex}");
            }
        }

        // a rotator that keeps reporting a move without its position changing for this long is blocked
        private static readonly TimeSpan MoveStallTimeout = TimeSpan.FromMinutes(3);

        public async Task<bool> Move(float angle, CancellationToken ct) {
            if(!Connected) {
                return false;
            }

            if (angle >= 360) {
                angle = AstroUtil.EuclidianModulus(angle, 360);
            }
            if (angle <= -360) {
                angle = AstroUtil.EuclidianModulus(angle, -360);
            }

            Logger.Debug($"Move relative by {angle}° - Mechanical Position reported by rotator {MechanicalPosition}° and offset {offset}");

            var err = RotatorMove(_uniqueId, angle);
            if (err != NC_ERROR_TYPE.NC_SUCCESS) {
                Logger.Error($"Nitecrawler failed to issue move command {err}");
                throw new Exception($"Failed to move Rotator {err}");
            }

            await CoreUtil.Wait(TimeSpan.FromMilliseconds(100), ct);
            var (result, last) = await NativeMoveWait.WaitAsync(ReadMoveStatus, TimeSpan.FromMilliseconds(100), MoveStallTimeout, () => DateTime.UtcNow, ct);
            if (result == MoveWaitResult.Stalled) {
                Halt();
                throw new Exception($"Nitecrawler rotator stopped: it reported moving for {MoveStallTimeout.TotalMinutes:F0} min without leaving {last.Position:F2}°");
            }

            return true;
        }

        private MoveStatus ReadMoveStatus() {
            var err = GetRotatorStatus(_uniqueId, out var status);
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

            throw new Exception($"Rotator error {err}");
        }

        public async Task<bool> MoveAbsoluteMechanical(float targetPosition, CancellationToken ct) {
            if (!Connected) {
                return false;
            }

            var movement = targetPosition - ReadMechanicalPositionForMove();
            return await Move(movement, ct);
        }

        public async Task<bool> MoveAbsolute(float targetPosition, CancellationToken ct) {
            if (!Connected) {
                return false;
            }

            var position = AstroUtil.EuclidianModulus(ReadMechanicalPositionForMove() + offset, 360);
            return await Move(targetPosition - position, ct);
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
