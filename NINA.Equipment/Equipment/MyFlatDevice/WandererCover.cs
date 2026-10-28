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
            try {
                _ = CoverClose(_uniqueId);
            } catch (Exception ex) {
                Logger.Error($"WandererCover Disconnect crashed: {ex}");
            } finally {
                Connected = false;
            }
        }

        private void DisconnectOnRemovedError() {
            try {
                Notification.ShowWarning(Loc.Instance["LblCoverConnectionLost"]);
                Logger.Error($"WandererCover device was removed");
                Disconnect();
            } catch (Exception ex) {
                Logger.Error(ex);
            }
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
                    if (err == WC_ERROR_TYPE.WC_SUCCESS) {
                        return status.coverState switch {
                            0 => CoverState.Closed,
                            1 => CoverState.Open,
                            2 => CoverState.NeitherOpenNorClosed,
                            3 => CoverState.NeitherOpenNorClosed,
                            _ => CoverState.Error
                        };
                    } else {
                        if (err == WC_ERROR_TYPE.WC_ERROR_COMMUNICATION) {
                            Logger.Error($"WandererCover communication error to get brightness {err}");
                            DisconnectOnRemovedError();
                        } else {
                            Logger.Error($"WandererCover error to get brightness {err}");
                        }
                        return CoverState.Error;
                    }
                } catch (Exception ex) {
                    Logger.Error($"WandererCover CoverState crashed: {ex}");
                    DisconnectOnRemovedError();
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

        // Right after the command the cover can still report the end it is leaving until its motor starts
        private static readonly TimeSpan MotionStartGrace = TimeSpan.FromSeconds(5);

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
                    var err = command(_uniqueId);
                    if (err != WC_ERROR_TYPE.WC_SUCCESS) {
                        if (err == WC_ERROR_TYPE.WC_ERROR_COMMUNICATION) {
                            Logger.Error($"WandererCover communication error to {action} cover {err}");
                            DisconnectOnRemovedError();
                        } else {
                            Logger.Error($"WandererCover error to {action} cover {err}");
                        }
                        return false;
                    }

                    var (result, statusError, state) = await WaitForCoverStateAsync(
                        () => (CoverGetStatus(_uniqueId, out var status), status.coverState),
                        targetState,
                        TimeSpan.FromMilliseconds(delay),
                        MoveTimeout,
                        MotionStartGrace,
                        ct);

                    switch (result) {
                        case CoverMoveResult.Reached:
                            return true;

                        case CoverMoveResult.StatusError when statusError == WC_ERROR_TYPE.WC_ERROR_COMMUNICATION:
                            Logger.Error($"WandererCover communication error to get the cover state while it should {action} {statusError}");
                            DisconnectOnRemovedError();
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
                    Logger.Error($"WandererCover {action} crashed: {ex}");
                    DisconnectOnRemovedError();
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
        /// Polls the cover until it reports <paramref name="targetState"/> (0 = closed, 1 = open). Until it was seen moving
        /// (3) or <paramref name="startGrace"/> has passed it may still report the end it is leaving; after that any other
        /// state means it stopped short. Gives up after <paramref name="timeout"/>.
        /// </summary>
        internal static async Task<(CoverMoveResult Result, WC_ERROR_TYPE Error, int State)> WaitForCoverStateAsync(
            Func<(WC_ERROR_TYPE Error, int State)> readStatus,
            int targetState,
            TimeSpan pollInterval,
            TimeSpan timeout,
            TimeSpan startGrace,
            CancellationToken ct) {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var seenMoving = false;

            while (true) {
                await Task.Delay(pollInterval, ct);

                var (error, state) = readStatus();
                if (error != WC_ERROR_TYPE.WC_SUCCESS) {
                    return (CoverMoveResult.StatusError, error, state);
                }

                if (state == targetState) {
                    return (CoverMoveResult.Reached, error, state);
                }

                if (state == StateMoving) {
                    seenMoving = true;
                } else if (seenMoving || watch.Elapsed > startGrace) {
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
                    if (err == WC_ERROR_TYPE.WC_SUCCESS) {
                        return config.brightness > 0;
                    } else {
                        if (err == WC_ERROR_TYPE.WC_ERROR_COMMUNICATION) {
                            Logger.Error($"WandererCover communication error to get brightness {err}");
                            DisconnectOnRemovedError();
                        } else {
                            Logger.Error($"WandererCover error to get brightness {err}");
                        }
                        return false;
                    }
                } catch (Exception ex) {
                    Logger.Error($"WandererCover LightOn getter crashed: {ex}");
                    DisconnectOnRemovedError();
                    return false;
                }
            }
            set {
                try {
                    WC_COVER_CONFIG config = new() {
                        mask = MASK_COVER_BRIGHTNESS,
                        brightness = value ? (lastBrightness != 0 ? lastBrightness : MaxBrightness) : 0
                    };
                    _ = CoverSetConfig(_uniqueId, config);
                    RaisePropertyChanged(nameof(Brightness));
                } catch (Exception ex) {
                    Logger.Error($"WandererCover LightOn setter crashed: {ex}");
                    DisconnectOnRemovedError();
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
                    if (err == WC_ERROR_TYPE.WC_SUCCESS) {
                        return config.brightness;
                    } else {
                        if (err == WC_ERROR_TYPE.WC_ERROR_COMMUNICATION) {
                            Logger.Error($"WandererCover communication error to get brightness {err}");
                            DisconnectOnRemovedError();
                        } else {
                            Logger.Error($"WandererCover error to get brightness {err}");
                        }
                        return -1;
                    }
                } catch (Exception ex) {
                    Logger.Error($"WandererCover Brightness getter crashed: {ex}");
                    DisconnectOnRemovedError();
                    return -1;
                }
            }
            set {
                try {
                    WC_COVER_CONFIG config = new() {
                        mask = MASK_COVER_BRIGHTNESS,
                        brightness = value
                    };
                    Logger.Info($"Setting brightness to {value}");
                    _ = CoverSetConfig(_uniqueId, config);
                    lastBrightness = value;
                    RaisePropertyChanged(nameof(Brightness));
                } catch (Exception ex) {
                    Logger.Error($"WandererCover Brightness setter crashed: {ex}");
                    DisconnectOnRemovedError();
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
                    if (err == WC_ERROR_TYPE.WC_SUCCESS) {
                        return config.heaterPower;
                    } else {
                        if (err == WC_ERROR_TYPE.WC_ERROR_COMMUNICATION) {
                            Logger.Error($"WandererCover communication error to get heater power {err}");
                            DisconnectOnRemovedError();
                        } else {
                            Logger.Error($"WandererCover error to get heater power {err}");
                        }
                        return 0;
                    }
                } catch (Exception ex) {
                    Logger.Error($"WandererCover HeaterPower getter crashed: {ex}");
                    DisconnectOnRemovedError();
                    return 0;
                }
            }
            set {
                try {
                    WC_COVER_CONFIG config = new() {
                        mask = MASK_COVER_HEATER_POWER,
                        heaterPower = Math.Max(0, Math.Min(3, value))
                    };
                    Logger.Info($"Setting heater power to {value}");
                    _ = CoverSetConfig(_uniqueId, config);
                    RaisePropertyChanged(nameof(HeaterPower));
                } catch (Exception ex) {
                    Logger.Error($"WandererCover HeaterPower setter crashed: {ex}");
                    DisconnectOnRemovedError();
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
                    if (err == WC_ERROR_TYPE.WC_SUCCESS) {
                        return status.openPositionAngle;
                    } else {
                        if (err == WC_ERROR_TYPE.WC_ERROR_COMMUNICATION) {
                            Logger.Error($"WandererCover communication error to get open position angle {err}");
                            DisconnectOnRemovedError();
                        } else {
                            Logger.Error($"WandererCover error to get open position angle {err}");
                        }
                        return float.NaN;
                    }
                } catch (Exception ex) {
                    Logger.Error($"WandererCover OpenPositionAngle getter crashed: {ex}");
                    DisconnectOnRemovedError();
                    return float.NaN;
                }
            }
            set {
                try {
                    WC_COVER_CONFIG config = new() {
                        mask = MASK_COVER_OPEN_POSITION,
                        openPositionAngle = value
                    };
                    Logger.Info($"Setting open position angle to {value}");
                    _ = CoverSetConfig(_uniqueId, config);
                    RaisePropertyChanged(nameof(OpenPositionAngle));
                } catch (Exception ex) {
                    Logger.Error($"WandererCover OpenPositionAngle setter crashed: {ex}");
                    DisconnectOnRemovedError();
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
                    if (err == WC_ERROR_TYPE.WC_SUCCESS) {
                        return status.closePositionAngle;
                    } else {
                        if (err == WC_ERROR_TYPE.WC_ERROR_COMMUNICATION) {
                            Logger.Error($"WandererCover communication error to get close position angle {err}");
                            DisconnectOnRemovedError();
                        } else {
                            Logger.Error($"WandererCover error to get close position angle {err}");
                        }
                        return float.NaN;
                    }
                } catch (Exception ex) {
                    Logger.Error($"WandererCover ClosePositionAngle getter crashed: {ex}");
                    DisconnectOnRemovedError();
                    return float.NaN;
                }
            }
            set {
                try {
                    WC_COVER_CONFIG config = new() {
                        mask = MASK_COVER_CLOSE_POSITION,
                        closePositionAngle = value
                    };
                    Logger.Info($"Setting close position angle to {value}");
                    _ = CoverSetConfig(_uniqueId, config);
                    RaisePropertyChanged(nameof(ClosePositionAngle));
                } catch (Exception ex) {
                    Logger.Error($"WandererCover ClosePositionAngle setter crashed: {ex}");
                    DisconnectOnRemovedError();
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
                    if (err == WC_ERROR_TYPE.WC_SUCCESS) {
                        return status.currentPositionAngle;
                    } else {
                        if (err == WC_ERROR_TYPE.WC_ERROR_COMMUNICATION) {
                            Logger.Error($"WandererCover communication error to get current position angle {err}");
                            DisconnectOnRemovedError();
                        } else {
                            Logger.Error($"WandererCover error to get current position angle {err}");
                        }
                        return float.NaN;
                    }
                } catch (Exception ex) {
                    Logger.Error($"WandererCover CurrentPositionAngle crashed: {ex}");
                    DisconnectOnRemovedError();
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