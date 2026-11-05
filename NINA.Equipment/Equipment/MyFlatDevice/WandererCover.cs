#region "copyright"

/*
    Copyright © 2025 Nico Trost <nico.trost57@gmail.com> and the PI.N.S. contributors

    This file is part of PI 'N' Stars.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Core.Locale;
using NINA.Profile.Interfaces;
using NINA.Core.Utility;
using NINA.Core.Utility.Notification;
using System;
using System.Threading;
using System.Threading.Tasks;
using NINA.Equipment.Interfaces;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using static Wanderer.WandererCoverSDK;
using System.Linq;
using NINA.Equipment.Equipment.MyRotator;

namespace NINA.Equipment.Equipment.MyFlatDevice {

    public partial class WandererCover : BaseINPC, IFlatDevice {
        private readonly IProfileService _profileService;
        private readonly int _uniqueId;

        public WandererCover(int id, string model, IProfileService profileService) {
            Id = $"{id}.{model}";
            _profileService = profileService;
            _uniqueId = id;

            // Grab model
            Name = $"Wanderer.Cover.{_uniqueId} ({model})";
        }

        public bool HasSetupDialog => false;
        public string Id { get; }
        public string Name { get; }
        public string DisplayName => Name;
        public string Category => "WandererCover";

        [ObservableProperty]
        private bool connected = false;

        public string Description => "Native driver for WandererCover FlatDevices";

        public string DriverInfo { get; private set; } = string.Empty;
        public string DriverVersion => GetSDKVersion();

        public async Task<bool> Connect(CancellationToken token) {
            return await Task.Run(() => {
                try {
                    // Verify, the flat panel id actually exists
                    int[] ids = new int[WC_MAX_NUM];
                    var scanErr = CoverScan(out var count, ids);
                    if (scanErr != WC_ERROR_TYPE.WC_SUCCESS) {
                        Notification.ShowError(Loc.Instance["LblWandererCoverNotAvailableError"]);
                        Logger.Error($"WandererCover scan failed: {scanErr}");
                        return false;
                    }
                    if (!ids.Take(count).Contains(_uniqueId)) {
                        Notification.ShowError(Loc.Instance["LblWandererCoverNotAvailableError"]);
                        Logger.Error("Selected WandererCover FlatDevices not available (disconnected?)");
                        return false;
                    }
                    var openErr = CoverOpen(_uniqueId);
                    if (openErr == WC_ERROR_TYPE.WC_SUCCESS) {
                        DriverInfo = $"SDK: {DriverVersion}";
                        communicationErrors.RecordSuccess();
                        Connected = true;
                        return true;
                    } else {
                        Logger.Error($"Failed to connect to WandererCover: {openErr}");
                        return false;
                    }
                } catch (Exception ex) {
                    Logger.Error($"WandererCover Connect error: {ex}");
                    Notification.ShowError($"WandererCover connection error: {ex.Message}");
                    return false;
                }
            }, token);
        }

        public void Disconnect() {
            // not connected first: a getter's error on the handle being closed was counted and could report the cover as
            // lost (and close it again) while the user disconnected it
            Connected = false;
            try {
                _ = CoverClose(_uniqueId);
            } catch (Exception ex) {
                Logger.Error($"WandererCover Disconnect crashed: {ex}");
            }
        }

        // A single failed read or write (a USB hiccup at dawn, during the flats) must not tear down the connection, as for
        // the Wanderer rotator: the cover only counts as lost after 3 errors within 30 s with no successful exchange in
        // between. The SDK has no separate code for a removed device (WC_ERROR_COMMUNICATION covers both), so every
        // communication error counts; errors within a second count as one.
        private readonly CommunicationErrorCounter communicationErrors = new(3, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(1));

        /// <summary>
        /// Counts a communication error and disconnects once there were too many in a short time. Returns true while the
        /// connection is kept.
        /// </summary>
        private bool OnCommunicationError() {
            // already disconnected (e.g. by another thread's error): nothing to count, and no second notification
            if (!Connected) {
                return false;
            }

            if (communicationErrors.RecordError(DateTime.UtcNow, out var count)) {
                Logger.Warning($"WandererCover communication error {count}/{communicationErrors.MaxErrors} within {communicationErrors.Window.TotalSeconds:F0}s, keeping the connection");
                return true;
            }

            try {
                Notification.ShowWarning(Loc.Instance["LblCoverConnectionLost"]);
                Logger.Error($"WandererCover device was removed");
                Disconnect();
            } catch (Exception ex) {
                Logger.Error(ex);
            }

            return false;
        }

        /// <summary>The result of one exchange with the cover: logged and counted when it failed. True when it worked.</summary>
        private bool Succeeded(WC_ERROR_TYPE err, string what) {
            if (err == WC_ERROR_TYPE.WC_SUCCESS) {
                communicationErrors.RecordSuccess();
                return true;
            }

            if (err == WC_ERROR_TYPE.WC_ERROR_COMMUNICATION) {
                Logger.Error($"WandererCover communication error to {what}: {err}");
                OnCommunicationError();
            } else {
                Logger.Error($"WandererCover error to {what}: {err}");
            }

            return false;
        }

        private void Crashed(string what, Exception ex) {
            Logger.Error($"WandererCover {what} crashed: {ex}");
            OnCommunicationError();
        }

        public void SetupDialog() {
        }

        public CoverState CoverState {
            get {
                if (!Connected) {
                    return CoverState.Unknown;
                }

                try {
                    var err = CoverGetStatus(_uniqueId, out var status);
                    if (!Succeeded(err, "get the cover state")) {
                        return CoverState.Error;
                    }

                    return status.coverState switch {
                        0 => CoverState.Closed,
                        1 => CoverState.Open,
                        2 => CoverState.NeitherOpenNorClosed,
                        3 => CoverState.NeitherOpenNorClosed,
                        _ => CoverState.Error
                    };
                } catch (Exception ex) {
                    Crashed("CoverState", ex);
                    return CoverState.Error;
                }
            }
        }

        public int MaxBrightness => 255;
        public int MinBrightness => 0;

        // Cover states of WC_COVER_STATUS.coverState
        private const int StateClosed = 0;
        private const int StateOpen = 1;
        private const int StateMoving = 3;

        // The cover takes about 10 s from one end to the other
        private static readonly TimeSpan MoveTimeout = TimeSpan.FromSeconds(60);

        // How long the end the cover is leaving still counts as "not moving yet". The SDK reports moving (3) as soon as
        // the command is sent and clears it with the first status after the move, which the device only sends once it
        // stopped. A status sent before the move but arriving after the SDK's 150 ms grace would clear it early and leave
        // the old end reported for the whole move, so this is longer than one move (about 10 s).
        private static readonly TimeSpan MotionStartGrace = TimeSpan.FromSeconds(15);

        public async Task<bool> Open(CancellationToken ct, int delay = 300) {
            return await MoveCover(CoverOpenCover, StateOpen, "open", ct, delay);
        }

        public async Task<bool> Close(CancellationToken ct, int delay = 300) {
            return await MoveCover(CoverCloseCover, StateClosed, "close", ct, delay);
        }

        private async Task<bool> MoveCover(Func<int, WC_ERROR_TYPE> command, int targetState, string action, CancellationToken ct, int delay) {
            if (!Connected) return await Task.Run(() => false, ct);
            return await Task.Run(async () => {
                try {
                    if (!Succeeded(command(_uniqueId), $"{action} the cover")) {
                        return false;
                    }

                    // a failed status read is counted and the wait goes on while the connection is kept: one USB hiccup
                    // made the whole open or close fail
                    var (result, statusError, state) = await WaitForCoverStateAsync(
                        () => {
                            var statusErr = CoverGetStatus(_uniqueId, out var status);
                            if (statusErr == WC_ERROR_TYPE.WC_SUCCESS) {
                                communicationErrors.RecordSuccess();
                            }
                            return (statusErr, status.coverState);
                        },
                        targetState,
                        TimeSpan.FromMilliseconds(delay),
                        MoveTimeout,
                        MotionStartGrace,
                        ct,
                        statusErr => {
                            if (statusErr != WC_ERROR_TYPE.WC_ERROR_COMMUNICATION || !Connected) {
                                return false;
                            }

                            Logger.Error($"WandererCover communication error to get the cover state while it should {action}: {statusErr}");
                            return OnCommunicationError();
                        });

                    switch (result) {
                        case CoverMoveResult.Reached:
                            return true;

                        case CoverMoveResult.StatusError when statusError == WC_ERROR_TYPE.WC_ERROR_COMMUNICATION:
                            Logger.Error($"WandererCover lost while it should {action}");
                            return false;

                        case CoverMoveResult.StatusError:
                            Logger.Error($"WandererCover error to get the cover state while it should {action} {statusError}");
                            return false;

                        case CoverMoveResult.TimedOut:
                            Logger.Error($"WandererCover did not {action} within {MoveTimeout.TotalSeconds:F0}s (cover state {state})");
                            return false;

                        default:
                            Logger.Error($"WandererCover did not {action}: it stopped in cover state {state}");
                            return false;
                    }
                } catch (OperationCanceledException) {
                    Logger.Info($"WandererCover {action} cancelled by user");
                    return false;
                } catch (Exception ex) {
                    Crashed(action, ex);
                    return false;
                }
            }, ct);
        }

        internal enum CoverMoveResult {
            Reached,
            Stopped,
            TimedOut,
            StatusError
        }

        /// <summary>
        /// Polls the cover until it reports <paramref name="targetState"/> (0 = closed, 1 = open). Within
        /// <paramref name="startGrace"/> it may still report the end it is leaving, also after it was seen moving (3): a
        /// status from before the move that arrives late clears the SDK's moving state, and the old end then stays
        /// reported until the move is done. Any other state after it was seen moving, or anything but the target once
        /// the grace is over, means it stopped short; a cover that really returns to its start is therefore only reported
        /// at the end of the grace. Gives up after <paramref name="timeout"/>. A failed status read ends the wait unless
        /// <paramref name="keepPolling"/> says to go on (a transient communication error).
        /// </summary>
        internal static async Task<(CoverMoveResult Result, WC_ERROR_TYPE Error, int State)> WaitForCoverStateAsync(
            Func<(WC_ERROR_TYPE Error, int State)> readStatus,
            int targetState,
            TimeSpan pollInterval,
            TimeSpan timeout,
            TimeSpan startGrace,
            CancellationToken ct,
            Func<WC_ERROR_TYPE, bool> keepPolling = null) {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var seenMoving = false;
            var lastState = -1; // the last state read without an error, -1 before the first
            var leavingState = targetState == StateOpen ? StateClosed : StateOpen;

            while (true) {
                await Task.Delay(pollInterval, ct);

                var (error, state) = readStatus();
                if (error != WC_ERROR_TYPE.WC_SUCCESS) {
                    if (keepPolling?.Invoke(error) == true) {
                        if (watch.Elapsed > timeout) {
                            // the error was tolerated: the move took too long, the cover is not lost (the failed read's
                            // state means nothing)
                            return (CoverMoveResult.TimedOut, error, lastState);
                        }

                        continue;
                    }

                    return (CoverMoveResult.StatusError, error, state);
                }

                lastState = state;

                if (state == targetState) {
                    return (CoverMoveResult.Reached, error, state);
                }

                if (state == StateMoving) {
                    seenMoving = true;
                } else if (watch.Elapsed > startGrace || (seenMoving && state != leavingState)) {
                    return (CoverMoveResult.Stopped, error, state);
                }

                if (watch.Elapsed > timeout) {
                    return (CoverMoveResult.TimedOut, error, state);
                }
            }
        }

        public bool LightOn {
            get {
                if (!Connected) {
                    return false;
                }

                try {
                    var err = CoverGetConfig(_uniqueId, out var config);
                    if (!Succeeded(err, "get the light state")) {
                        return false;
                    }

                    return config.brightness > 0;
                } catch (Exception ex) {
                    Crashed("LightOn getter", ex);
                    return false;
                }
            }
            set {
                if (!Connected) {
                    return;
                }

                try {
                    WC_COVER_CONFIG config = new() {
                        mask = MASK_COVER_BRIGHTNESS,
                        brightness = value ? (lastBrightness != 0 ? lastBrightness : MaxBrightness) : 0
                    };
                    Succeeded(CoverSetConfig(_uniqueId, config), value ? "switch the light on" : "switch the light off");
                    RaisePropertyChanged(nameof(Brightness));
                } catch (Exception ex) {
                    Crashed("LightOn setter", ex);
                }
            }
        }

        private int lastBrightness = 0;

        public int Brightness {
            get {
                if (!Connected) {
                    return -1;
                }

                try {
                    var err = CoverGetConfig(_uniqueId, out var config);
                    if (!Succeeded(err, "get brightness")) {
                        return -1;
                    }

                    return config.brightness;
                } catch (Exception ex) {
                    Crashed("Brightness getter", ex);
                    return -1;
                }
            }
            set {
                if (!Connected) {
                    return;
                }

                try {
                    WC_COVER_CONFIG config = new() {
                        mask = MASK_COVER_BRIGHTNESS,
                        brightness = value
                    };
                    Logger.Info($"Setting brightness to {value}");
                    // the brightness the light comes back on with is only taken when the cover took it
                    if (Succeeded(CoverSetConfig(_uniqueId, config), "set the brightness")) {
                        lastBrightness = value;
                    }
                    RaisePropertyChanged(nameof(Brightness));
                } catch (Exception ex) {
                    Crashed("Brightness setter", ex);
                }
            }
        }

        public int HeaterPower {
            get {
                if (!Connected) {
                    return 0;
                }

                try {
                    var err = CoverGetConfig(_uniqueId, out var config);
                    if (!Succeeded(err, "get heater power")) {
                        return 0;
                    }

                    return config.heaterPower;
                } catch (Exception ex) {
                    Crashed("HeaterPower getter", ex);
                    return 0;
                }
            }
            set {
                if (!Connected) {
                    return;
                }

                try {
                    WC_COVER_CONFIG config = new() {
                        mask = MASK_COVER_HEATER_POWER,
                        heaterPower = Math.Max(0, Math.Min(3, value))
                    };
                    Logger.Info($"Setting heater power to {value}");
                    Succeeded(CoverSetConfig(_uniqueId, config), "set the heater power");
                    RaisePropertyChanged(nameof(HeaterPower));
                } catch (Exception ex) {
                    Crashed("HeaterPower setter", ex);
                }
            }
        }

        public float OpenPositionAngle {
            get {
                if (!Connected) {
                    return float.NaN;
                }

                try {
                    var err = CoverGetStatus(_uniqueId, out var status);
                    if (!Succeeded(err, "get open position angle")) {
                        return float.NaN;
                    }

                    return status.openPositionAngle;
                } catch (Exception ex) {
                    Crashed("OpenPositionAngle getter", ex);
                    return float.NaN;
                }
            }
            set {
                if (!Connected) {
                    return;
                }

                try {
                    WC_COVER_CONFIG config = new() {
                        mask = MASK_COVER_OPEN_POSITION,
                        openPositionAngle = value
                    };
                    Logger.Info($"Setting open position angle to {value}");
                    Succeeded(CoverSetConfig(_uniqueId, config), "set the open position angle");
                    RaisePropertyChanged(nameof(OpenPositionAngle));
                } catch (Exception ex) {
                    Crashed("OpenPositionAngle setter", ex);
                }
            }
        }

        public float ClosePositionAngle {
            get {
                if (!Connected) {
                    return float.NaN;
                }

                try {
                    var err = CoverGetStatus(_uniqueId, out var status);
                    if (!Succeeded(err, "get close position angle")) {
                        return float.NaN;
                    }

                    return status.closePositionAngle;
                } catch (Exception ex) {
                    Crashed("ClosePositionAngle getter", ex);
                    return float.NaN;
                }
            }
            set {
                if (!Connected) {
                    return;
                }

                try {
                    WC_COVER_CONFIG config = new() {
                        mask = MASK_COVER_CLOSE_POSITION,
                        closePositionAngle = value
                    };
                    Logger.Info($"Setting close position angle to {value}");
                    Succeeded(CoverSetConfig(_uniqueId, config), "set the close position angle");
                    RaisePropertyChanged(nameof(ClosePositionAngle));
                } catch (Exception ex) {
                    Crashed("ClosePositionAngle setter", ex);
                }
            }
        }

        public float CurrentPositionAngle {
            get {
                if (!Connected) {
                    return float.NaN;
                }

                try {
                    var err = CoverGetStatus(_uniqueId, out var status);
                    if (!Succeeded(err, "get current position angle")) {
                        return float.NaN;
                    }

                    return status.currentPositionAngle;
                } catch (Exception ex) {
                    Crashed("CurrentPositionAngle", ex);
                    return float.NaN;
                }
            }
        }

        public bool SupportsOpenClose => true;

        public bool SupportsOnOff => true;

        public string PortName { get => string.Empty; set { } }

        public IList<string> SupportedActions => new List<string>();

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
    }
}