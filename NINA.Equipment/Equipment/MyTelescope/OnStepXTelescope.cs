#region "copyright"

/*
    Copyright © 2026 Nico Trost <nico.trost57@gmail.com> and the PI.N.S. contributors

    This file is part of PI 'N' Stars.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

#nullable enable

using NINA.Astrometry;
using NINA.Core.Enum;
using NINA.Core.Locale;
using NINA.Core.Utility;
using NINA.Core.Utility.Notification;
using NINA.Equipment.Interfaces;
using NINA.Equipment.SDK.TelescopeSDKs.OnStepXSDK;
using NINA.Profile.Interfaces;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Equipment.Equipment.MyTelescope {

    /// <summary>
    /// An OnStepX mount driven by pins over USB serial, without INDI, on the port in
    /// <see cref="ITelescopeSettings.SerialPort"/>. Behaves like <see cref="IndiTelescope"/> with INDI's LX200_OnStep driver.
    /// </summary>
    public class OnStepXTelescope : BaseINPC, ITelescope, IDisposable {

        public const string DeviceId = "OnStepX";

        /// <summary>Sidereal rate in degrees per second.</summary>
        private const double SiderealDegreesPerSecond = 15.0410686 / 3600.0;

        /// <summary>The UI reads about 30 properties per poll; they share one set of reads this old at most.</summary>
        private static readonly TimeSpan StateMaxAge = TimeSpan.FromMilliseconds(250);

        /// <summary>
        /// Consecutive unreadable states (the controller silent or garbled), spread over at least
        /// <see cref="StateFailureSpan"/>, before the connection counts as lost.
        /// </summary>
        private const int MaxStateFailures = 3;

        /// <summary>A short USB stall must not end the connection within one UI poll.</summary>
        private static readonly TimeSpan StateFailureSpan = TimeSpan.FromSeconds(2);

        /// <summary>
        /// Axis motion that :GU# does not show: a manual move braking after its stop (the guide action is GA_BREAK, which
        /// Guide::active() leaves out, so 'g' is gone at once), as INDITelescope detects it. 0.05°/s is about 12 times the
        /// sidereal RA drift of a mount that does not track. RA in hours times 15, not scaled by cos(Dec), is the axis 1
        /// angle, so a move near the pole counts too.
        /// </summary>
        private const double CoordinateMotionDegreesPerSecond = 0.05;

        /// <summary>
        /// The shortest span the coordinate rate is taken over: :GR# has whole seconds, and a tick of 15" between two
        /// reads 50 ms apart would look like motion.
        /// </summary>
        private static readonly TimeSpan CoordinateMotionSpan = TimeSpan.FromSeconds(0.5);

        /// <summary>How long a stopped goto may brake, or a move end, before a park or home gives up.</summary>
        private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(15);

        /// <summary>
        /// Added to a guide pulse counted from when its command is on the line. The controller ends a pulse exactly
        /// (its guide task runs without pause, Guide.cpp), but USB serial delays commands by a few ms and now and then by
        /// up to ~80 ms: measured on a UMi17S, most 300 ms pulses ended 0-15 ms after the estimate, one 33-59 ms after
        /// it. This margin lets the first 'G' check in <see cref="IsPulseGuiding"/> usually confirm the end; that check
        /// covers the rest. INDI times pulses from the write, with no margin.
        /// </summary>
        private static readonly TimeSpan PulseEndMargin = TimeSpan.FromMilliseconds(15);

        /// <summary>A pulse ending this much later than estimated is logged as a warning; a little late is normal (USB).</summary>
        private static readonly TimeSpan PulseLateWarning = TimeSpan.FromMilliseconds(50);

        /// <summary>How often the controller is asked whether a pulse is still running once its estimated end has passed.</summary>
        private static readonly TimeSpan PulseCheckInterval = TimeSpan.FromMilliseconds(15);

        /// <summary>A pulse still reported after this long past its estimated end counts as over, so the guider does not stall.</summary>
        private static readonly TimeSpan PulseEndTimeout = TimeSpan.FromSeconds(1);

        /// <summary>How old the last known status may be to refuse a pulse on it; reading it fresh would delay the pulse.</summary>
        private static readonly TimeSpan PulseRefusalStatusMaxAge = TimeSpan.FromSeconds(5);

        /// <summary>
        /// OnStep rejects a goto sent right after tracking was switched on as "below the horizon limit" (see
        /// INDITelescope); a wait before the goto and one retry clear it.
        /// </summary>
        private static readonly TimeSpan TrackingSettleTime = TimeSpan.FromSeconds(1);

        private static readonly TimeSpan MotionStartTimeout = TimeSpan.FromSeconds(10);

        /// <summary>For a firmware without the homing flag: how long the mount stands still before homing counts as done.</summary>
        private static readonly TimeSpan HomeStandstillTimeout = TimeSpan.FromSeconds(3);
        private static readonly TimeSpan MotionTimeout = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan MeridianFlipSlewRetryWait = TimeSpan.FromMinutes(1);
        private const int MeridianFlipSlewRetryAttempts = 20;

        /// <summary>Alt/Az change below which the mount counts as standing still while homing.</summary>
        private const double StandstillDegrees = 1.0 / 60.0;

        /// <summary>Reply timeout while scanning: an empty port should not hold up the device list.</summary>
        private static readonly TimeSpan ScanReplyTimeout = TimeSpan.FromMilliseconds(300);

        /// <summary>How long the scan asks for a Proxisky model; the UMi answers at once once it is up.</summary>
        private static readonly TimeSpan ScanProxiskyProbeBudget = TimeSpan.FromMilliseconds(500);

        /// <summary>The model last identified per port, for scans while the port is in use.</summary>
        private static readonly ConcurrentDictionary<string, string> KnownModels = new();

        private readonly IProfileService profileService;
        private string? scannedModel;
        private ITelescopeSettings? watchedSettings;
        private readonly Func<string, OnStepXDevice> connectDevice;
        private readonly object stateLock = new();

        private OnStepXDevice? device;
        private State? state;
        private int stateFailures;
        private TimeSpan clockOffset;
        private double siteLatitude;
        private double siteLongitude;
        private double siteElevation;
        private double guideRate = double.NaN;
        private double slewSpeed = double.NaN;
        private AlignmentMode alignmentMode = AlignmentMode.GermanPolar;
        private OnStepXCompensation? compensationBeforeRateChange;
        private DateTime firstStateFailureAt;
        private (double Ra, double Dec, DateTime At)? motionReference;
        private bool coordinatesMoving;
        private readonly object pulseLock = new();
        private DateTime motionCommandAt = DateTime.MinValue;
        private DateTime raPulseEnd = DateTime.MinValue;
        private DateTime decPulseEnd = DateTime.MinValue;
        private bool pulseEndConfirmed = true;
        private bool pulseLateLogged;
        private DateTime lastPulseCheck = DateTime.MinValue;
        private OnStepXStatus? latestStatus;
        private DateTime latestStatusAt = DateTime.MinValue;
        private int selectedMoveRate = DefaultMoveRateIndex;

        // The manual moves this driver started, under moveLock; never take stateLock or pulseLock while holding it.
        private readonly object moveLock = new();
        private OnStepXDirection? primaryMove;
        private OnStepXDirection? secondaryMove;
        private DateTime moveCommandAt = DateTime.MinValue;
        private double primaryMovingRate = double.NaN;
        private double secondaryMovingRate = double.NaN;
        private Coordinates? targetCoordinates;
        private PierSide? targetSideOfPier;

        public OnStepXTelescope(IProfileService profileService)
            : this(profileService, port => OnStepXDevice.Connect(new OnStepXTransport(new OnStepXSerialPort(port)))) {
        }

        internal OnStepXTelescope(IProfileService profileService, Func<string, OnStepXDevice> connectDevice) {
            this.profileService = profileService;
            this.connectDevice = connectDevice;
        }

        /// <summary>
        /// For the device list: the mount on the profile's serial port, named after the model it reports. The port is
        /// opened briefly unless this process has it open (then the model from that connection is used); a port held by
        /// another program (INDI opens it exclusively) or silent leaves the generic name.
        /// </summary>
        public static Task<OnStepXTelescope> Discover(IProfileService profileService) =>
            Discover(profileService,
                port => OnStepXDevice.Connect(new OnStepXTransport(new OnStepXSerialPort(port), ScanReplyTimeout), ScanProxiskyProbeBudget, TimeSpan.FromMilliseconds(100)),
                OnStepXSerialPort.IsOpenInProcess);

        internal static async Task<OnStepXTelescope> Discover(IProfileService profileService, Func<string, OnStepXDevice> identify, Func<string, bool> isOpenInProcess) {
            var telescope = new OnStepXTelescope(profileService);
            string? port = profileService.ActiveProfile.TelescopeSettings.SerialPort?.Trim();
            if (!string.IsNullOrEmpty(port)) {
                telescope.scannedModel = await Task.Run(() => IdentifyModel(port, identify, isOpenInProcess));
            }
            return telescope;
        }

        private static string? IdentifyModel(string port, Func<string, OnStepXDevice> identify, Func<string, bool> isOpenInProcess) {
            if (isOpenInProcess(port)) {
                return KnownModels.TryGetValue(port, out var known) ? known : null;
            }
            try {
                using var device = identify(port);
                // the UMi can answer :Pbvg# with "0" right after the port opens: a scan that just missed it keeps the
                // model it knew (a connect always replaces it)
                return KnownModels.AddOrUpdate(port, device.Model, (_, known) => device is ProxiskyUmiDevice ? device.Model : known);
            } catch (Exception ex) {
                Logger.Debug($"OnStepX: no mount identified on {port} while scanning: {ex.Message}");
                return null;
            }
        }

        /// <param name="CoordinatesMoving">RA or Dec changed faster than <see cref="CoordinateMotionDegreesPerSecond"/>.</param>
        private sealed record State(OnStepXStatus Status, double RightAscension, double Declination, double Altitude, double Azimuth, double SiderealTime, OnStepXPierSide PierSide,
            bool CoordinatesMoving);

        #region IDevice

        public string Id => DeviceId;

        public string Name => "OnStepX (serial)";

        public string DisplayName => device is { } d && Connected ? $"{d.Model} (OnStepX)"
            : scannedModel is { } model ? $"{model} (OnStepX)"
            : Name;

        public string Category => "OnStepX";

        private bool connected;

        public bool Connected {
            get => connected;
            private set {
                if (connected != value) {
                    connected = value;
                    RaisePropertyChanged();
                    RaisePropertyChanged(nameof(DisplayName));
                }
            }
        }

        public string Description => device is ProxiskyUmiDevice umi
            ? $"{umi.Model}, firmware {umi.VendorFirmware} on OnStepX {umi.Identification.Version}"
            : device is { } d ? $"{d.Model} {d.Identification.Version}" : "OnStepX mount over USB serial";

        public string DriverInfo => device is { } d ? $"Native OnStepX on {d.PortName}: {d.Identification}" : "Native OnStepX";

        public string DriverVersion => device?.Identification.Version ?? string.Empty;

        public bool HasSetupDialog => false;

        public void SetupDialog() {
        }

        public IList<string> SupportedActions => [];

        public string Action(string actionName, string actionParameters) => throw new NotImplementedException();

        public string SendCommandString(string command, bool raw = true) => Run(d => d.SendCommandString(Raw(command, raw)), string.Empty);

        public bool SendCommandBool(string command, bool raw = true) => Run(d => d.SendCommandChar(Raw(command, raw)) == '1', false);

        public void SendCommandBlind(string command, bool raw = true) => Run(d => {
            d.SendCommandBlind(Raw(command, raw));
            return true;
        }, false);

        private static string Raw(string command, bool raw) => raw ? command : $":{command}#";

        public Task<bool> Connect(CancellationToken token) {
            return Task.Run(() => {
                string port = profileService.ActiveProfile.TelescopeSettings.SerialPort;
                if (string.IsNullOrWhiteSpace(port)) {
                    Logger.Error("OnStepX: no serial port set (TelescopeSettings.SerialPort)");
                    Notification.ShowError("OnStepX: set the mount's serial port first (e.g. /dev/ttyUSB0)");
                    return false;
                }

                try {
                    device = ConnectWithRetry(port.Trim(), token);
                    token.ThrowIfCancellationRequested();
                    ReadMountSetup(device);
                    lock (stateLock) {
                        state = null;
                        stateFailures = 0;
                        motionReference = null;
                        coordinatesMoving = false;
                    }
                    connectionLost = 0;
                    KnownModels[port.Trim()] = device.Model;
                    Connected = true;
                    Logger.Info($"OnStepX: connected to {device.Model} on {port}, {device.Identification}");
                    CheckMountTime();
                    WatchSettings();
                    ApplyPreferredPierSide();
                    RaiseAllPropertiesChanged();
                    return true;
                } catch (OperationCanceledException) {
                    CloseDevice();
                    Connected = false;
                    throw;
                } catch (Exception ex) {
                    Logger.Error($"OnStepX: connecting on {port} failed", ex);
                    Notification.ShowError($"OnStepX: could not connect on {port}: {ex.Message}");
                    CloseDevice();
                    Connected = false;
                    return false;
                }
            }, token);
        }

        /// <summary>A device scan opens the port for a moment, and .NET opens it exclusively: try again shortly.</summary>
        private OnStepXDevice ConnectWithRetry(string port, CancellationToken token) {
            for (int attempt = 1; ; attempt++) {
                try {
                    return connectDevice(port);
                } catch (Exception ex) when (attempt < 3 && ex is IOException or UnauthorizedAccessException) {
                    Logger.Info($"OnStepX: {port} is busy ({ex.Message}), trying again");
                    token.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(500));
                    token.ThrowIfCancellationRequested();
                }
            }
        }

        public void Disconnect() {
            // commands still in flight fail on the closing port; that is no lost connection
            Interlocked.Exchange(ref connectionLost, 1);
            Connected = false;
            CloseDevice();
            RaiseAllPropertiesChanged();
        }

        public void Dispose() {
            CloseDevice();
            GC.SuppressFinalize(this);
        }

        private void CloseDevice() {
            UnwatchSettings();
            var d = Interlocked.Exchange(ref device, null);
            OnStepXDirection?[] moves;
            lock (moveLock) {
                moves = [primaryMove, secondaryMove];
                primaryMove = secondaryMove = null;
            }
            lock (stateLock) {
                state = null;
            }
            if (d is not null) {
                // a manual move runs on until the firmware's guide time limit, which can be days: stop it while the
                // port may still work
                foreach (var move in moves) {
                    if (move is { } m) {
                        try {
                            d.StopMove(m);
                        } catch (Exception ex) {
                            Logger.Debug($"OnStepX: stopping the {m} move on close failed: {ex.Message}");
                        }
                    }
                }
            }
            try {
                d?.Dispose();
            } catch (Exception ex) {
                Logger.Error("OnStepX: closing the port failed", ex);
            }
        }

        private void ReadMountSetup(OnStepXDevice d) {
            (siteLatitude, siteLongitude) = d.GetSite();
            double elevation = d.GetElevation();
            siteElevation = double.IsNaN(elevation) ? profileService.ActiveProfile.AstrometrySettings.Elevation : elevation;
            slewSpeed = d.GetSlewSpeed();
            try {
                guideRate = d.GetPulseGuideRate();
            } catch (OnStepXException ex) {
                Logger.Warning($"OnStepX: guide rate unknown: {ex.Message}");
                guideRate = double.NaN;
            }
            alignmentMode = d.GetStatus().MountType switch {
                OnStepXMountType.AltAz or OnStepXMountType.AltAlt => AlignmentMode.AltAz,
                OnStepXMountType.Fork => AlignmentMode.Polar,
                _ => AlignmentMode.GermanPolar,
            };
            compensationBeforeRateChange = null;
            int currentMoveRate = d.GetStatus().MoveRateIndex;
            selectedMoveRate = currentMoveRate is >= 0 and <= OnStepXDevice.MaxMoveRateIndex ? currentMoveRate : DefaultMoveRateIndex;
            Logger.Info($"OnStepX: site {siteLatitude:F4}, {siteLongitude:F4}, {siteElevation:F0} m, slew speed {slewSpeed:F2}°/s, guide rate {guideRate:F2}x, {alignmentMode}, preferred pier side {d.GetPreferredPierSide()?.ToString() ?? "unknown"}");
        }

        /// <summary>
        /// Sets the preferred pier side from <see cref="ITelescopeSettings.PreferredPierSide"/>, at connect and when
        /// the setting changes; empty leaves the mount's own. OnStepX forgets it at power off unless built with
        /// PIER_SIDE_PREFERRED_MEMORY.
        /// </summary>
        private void ApplyPreferredPierSide() {
            string? value = profileService.ActiveProfile.TelescopeSettings.PreferredPierSide?.Trim();
            if (!Connected || string.IsNullOrEmpty(value)) {
                return;
            }
            if (!Enum.TryParse<OnStepXPreferredPierSide>(value, ignoreCase: true, out var side) || !Enum.IsDefined(side)) {
                Logger.Warning($"OnStepX: unknown preferred pier side '{value}'");
                Notification.ShowWarning($"OnStepX: unknown preferred pier side '{value}' (East, West or Best)");
                return;
            }
            try {
                RunOrThrow($"set the preferred pier side to {side}", d => d.SetPreferredPierSide(side));
                Logger.Info($"OnStepX: preferred pier side set to {side}");
            } catch (InvalidOperationException ex) {
                Notification.ShowError(ex.Message);
            }
        }

        private void WatchSettings() {
            UnwatchSettings();
            watchedSettings = profileService.ActiveProfile.TelescopeSettings;
            watchedSettings.PropertyChanged += TelescopeSettingsChanged;
            profileService.ProfileChanged += ProfileChanged;
        }

        private void UnwatchSettings() {
            if (watchedSettings is { } settings) {
                settings.PropertyChanged -= TelescopeSettingsChanged;
            }
            watchedSettings = null;
            profileService.ProfileChanged -= ProfileChanged;
        }

        private void TelescopeSettingsChanged(object? sender, PropertyChangedEventArgs e) {
            if (e.PropertyName == nameof(ITelescopeSettings.PreferredPierSide)) {
                ApplyPreferredPierSide();
            }
        }

        private void ProfileChanged(object? sender, EventArgs e) {
            if (Connected) {
                WatchSettings();
                ApplyPreferredPierSide();
            }
        }

        /// <summary>Like IndiTelescope: logs the clock difference, sets the controller's clock when TimeSync is on.</summary>
        private void CheckMountTime() {
            const double warningThresholdSeconds = 10;
            if (device is not { } d) {
                return;
            }

            try {
                var mountTime = d.GetUtcDate();
                var systemTime = DateTime.UtcNow;
                clockOffset = mountTime - systemTime;
                Logger.Info($"OnStepX: mount UTC {mountTime:u} / system UTC {systemTime:u}; difference {Math.Abs(clockOffset.TotalSeconds):0.0##} s");

                if (profileService.ActiveProfile.TelescopeSettings.TimeSync) {
                    var now = DateTime.UtcNow;
                    d.SetUtcDate(now, TimeZoneInfo.Local.GetUtcOffset(now));
                    clockOffset = d.GetUtcDate() - DateTime.UtcNow;
                    Logger.Info("OnStepX: system time has been synced to the mount");
                }

                if (Math.Abs(clockOffset.TotalSeconds) >= warningThresholdSeconds) {
                    Logger.Warning($"OnStepX: system and mount time differ by {Math.Abs(clockOffset.TotalSeconds):0.0##} seconds");
                    Notification.ShowWarning(string.Format(Loc.Instance["LblMountTimeDifferenceTooLarge"], Math.Abs(clockOffset.TotalSeconds)));
                }

                // without date and time OnStepX refuses every goto, reported as "outside limits" (Goto.cpp validate)
                if (d.IsDateTimeReady() == false) {
                    Logger.Warning("OnStepX: the controller's date and time are not set");
                    Notification.ShowWarning("OnStepX: the mount's date and time are not set, so it refuses gotos. Turn on time sync or set them on the mount.");
                }
            } catch (Exception ex) when (ex is OnStepXException) {
                Logger.Error($"OnStepX: reading or setting the mount time failed: {ex.Message}");
            }
        }

        #endregion IDevice

        #region State

        /// <summary>The mount's state, read at most every <see cref="StateMaxAge"/>; null when not connected or unreadable.</summary>
        private State? CurrentState => GetState(false);

        private State? GetState(bool fresh) {
            if (!Connected || device is not { } d) {
                return null;
            }

            State result;
            DateTime readStartedAt;
            lock (stateLock) {
                // after a failed read too, also before the first state: the rest of the poll does not retry at once
                if (!fresh && (state is not null || stateFailures > 0) && DateTime.UtcNow - stateReadAt < StateMaxAge) {
                    return state;
                }

                try {
                    readStartedAt = DateTime.UtcNow;
                    var status = d.GetStatus();
                    RememberStatus(status);
                    double ra = d.GetRightAscension();
                    double dec = d.GetDeclination();
                    var read = new State(status, ra, dec, d.GetAltitude(), d.GetAzimuth(), d.GetSiderealTime(), status.PierSide,
                        CoordinatesMoving(ra, dec, DateTime.UtcNow));
                    state = read;
                    result = read;
                    stateReadAt = DateTime.UtcNow;
                    stateFailures = 0;
                    if (status.Error != OnStepXError.None && status.Error != lastReportedError) {
                        Logger.Warning($"OnStepX: controller reports error {status.Error} ({status.Raw})");
                    }
                    lastReportedError = status.Error;
                } catch (OnStepXException ex) {
                    var now = DateTime.UtcNow;
                    Logger.Warning($"OnStepX: reading the mount state failed: {ex.Message}");
                    if (stateFailures++ == 0) {
                        firstStateFailureAt = now;
                    }
                    // back off: the other properties of this poll get the last state instead of retrying at once
                    stateReadAt = now;
                    if (stateFailures >= MaxStateFailures && now - firstStateFailureAt >= StateFailureSpan) {
                        ConnectionLost(ex);
                        return null;
                    }
                    // a wait must not take the state from before its command for a fresh one
                    return fresh ? null : state;
                } catch (Exception ex) when (IsPortFailure(ex)) {
                    ConnectionLost(ex);
                    return null;
                }
            }

            // outside stateLock (moveLock is taken while state is read elsewhere): neither 'g' nor 'G' in a status read
            // after the last move command means no move runs any more, e.g. after the firmware's guide time limit, a
            // limit or another client's stop
            if (!result.Status.ManualMove && !result.Status.PulseGuiding) {
                lock (moveLock) {
                    if (readStartedAt > moveCommandAt) {
                        primaryMove = secondaryMove = null;
                    }
                }
            }
            return result;
        }

        // Caller holds stateLock.
        private bool CoordinatesMoving(double ra, double dec, DateTime now) {
            if (motionReference is not { } reference) {
                motionReference = (ra, dec, now);
                return coordinatesMoving = false;
            }
            double seconds = (now - reference.At).TotalSeconds;
            if (seconds < CoordinateMotionSpan.TotalSeconds) {
                return coordinatesMoving;
            }
            double raHours = Math.Abs(ra - reference.Ra);
            if (raHours > 12) {
                raHours = 24 - raHours;
            }
            double degrees = Math.Max(raHours * 15, Math.Abs(dec - reference.Dec));
            motionReference = (ra, dec, now);
            return coordinatesMoving = degrees / seconds > CoordinateMotionDegreesPerSecond;
        }

        /// <summary>A sync or a new connection moves the coordinates without motion.</summary>
        private void ResetCoordinateMotion() {
            lock (stateLock) {
                motionReference = null;
                coordinatesMoving = false;
            }
        }

        private DateTime stateReadAt;
        private int connectionLost;
        private OnStepXError lastReportedError = OnStepXError.None;

        private void InvalidateState() {
            lock (stateLock) {
                stateReadAt = DateTime.MinValue;
            }
        }

        private static bool IsPortFailure(Exception ex) =>
            ex is IOException or InvalidOperationException or UnauthorizedAccessException or TimeoutException;

        private void ConnectionLost(Exception ex) {
            // once per connection: the reads still in flight fail the same way until the disconnect has run
            if (!Connected || Interlocked.Exchange(ref connectionLost, 1) == 1) {
                return;
            }
            Logger.Error("OnStepX: connection lost", ex);
            Notification.ShowError(Loc.Instance["LblTelescopeConnectionLost"]);
            var lost = device;
            // not a new connection made meanwhile
            _ = Task.Run(() => {
                if (ReferenceEquals(device, lost)) {
                    Disconnect();
                }
            });
        }

        /// <summary>On a cancelled goto, park or home: stop the mount, as ASCOM drivers abort the slew on a cancel.</summary>
        private void AbortQuietly() {
            if (!Connected || device is not { } d) {
                return;
            }
            try {
                d.Abort();
                Logger.Info("OnStepX: cancelled, mount stopped (:Q#)");
            } catch (Exception ex) {
                Logger.Warning($"OnStepX: stopping the mount after a cancel failed: {ex.Message}");
            }
            InvalidateState();
        }

        /// <summary>A goto, park or home was started: no pulse until a status read after it shows what the mount does.</summary>
        private void MotionCommandSent() {
            lock (pulseLock) {
                motionCommandAt = DateTime.UtcNow;
            }
            InvalidateState();
        }

        /// <summary>Runs a command; a port failure ends the connection, a refused or garbled reply is logged.</summary>
        private T Run<T>(Func<OnStepXDevice, T> command, T fallback) {
            if (!Connected || device is not { } d) {
                return fallback;
            }
            try {
                return command(d);
            } catch (OnStepXException ex) {
                Logger.Error($"OnStepX: {ex.Message}");
                Notification.ShowError($"OnStepX: {ex.Message}");
                return fallback;
            } catch (Exception ex) when (IsPortFailure(ex)) {
                ConnectionLost(ex);
                return fallback;
            }
        }

        /// <summary>
        /// Runs a command whose failure the caller must see (park, unpark, home, set park, tracking): a refusal throws
        /// <see cref="InvalidOperationException"/> naming the action and the controller's reason, as does a garbled or
        /// missing reply; a port failure ends the connection and throws too.
        /// </summary>
        private void RunOrThrow(string action, Action<OnStepXDevice> command) {
            if (!Connected || device is not { } d) {
                throw Failure($"cannot {action}: not connected");
            }
            try {
                command(d);
            } catch (OnStepXCommandRefusedException ex) {
                throw Failure($"the mount refused to {action}: {ex.Reason}", ex);
            } catch (OnStepXException ex) {
                throw Failure($"could not {action}: {ex.Message}", ex);
            } catch (Exception ex) when (IsPortFailure(ex)) {
                ConnectionLost(ex);
                throw Failure($"could not {action}: the connection was lost", ex);
            }
        }

        private static InvalidOperationException Failure(string message, Exception? inner = null) {
            Logger.Error($"OnStepX: {message}");
            return new InvalidOperationException($"OnStepX: {message}", inner);
        }

        private TimeSpan PollInterval => TimeSpan.FromSeconds(Math.Max(0.05, profileService.ActiveProfile.ApplicationSettings.DevicePollingInterval));

        private void RaiseAllPropertiesChanged() {
            RaisePropertyChanged(nameof(DisplayName));
            RaisePropertyChanged(nameof(Description));
            RaisePropertyChanged(nameof(DriverInfo));
            RaisePropertyChanged(nameof(DriverVersion));
        }

        #endregion State

        #region Position

        public Coordinates Coordinates => new(RightAscension, Declination, Epoch.JNOW, Coordinates.RAType.Hours);

        public double RightAscension => CurrentState?.RightAscension ?? double.NaN;

        public string RightAscensionString => AstroUtil.HoursToHMS(RightAscension);

        public double Declination => CurrentState?.Declination ?? double.NaN;

        public string DeclinationString => AstroUtil.DegreesToDMS(Declination);

        public double SiderealTime => CurrentState?.SiderealTime ?? double.NaN;

        public string SiderealTimeString => AstroUtil.HoursToHMS(SiderealTime);

        public double Altitude => CurrentState?.Altitude ?? double.NaN;

        public string AltitudeString => double.IsNaN(Altitude) ? string.Empty : AstroUtil.DegreesToDMS(Altitude);

        public double Azimuth => CurrentState?.Azimuth ?? double.NaN;

        public string AzimuthString => double.IsNaN(Azimuth) ? string.Empty : AstroUtil.DegreesToDMS(Azimuth);

        public PierSide SideOfPier => CurrentState?.PierSide switch {
            OnStepXPierSide.East => PierSide.pierEast,
            OnStepXPierSide.West => PierSide.pierWest,
            _ => PierSide.pierUnknown,
        };

        public Epoch EquatorialSystem => Epoch.JNOW;

        public bool HasUnknownEpoch => false;

        public AlignmentMode AlignmentMode => alignmentMode;

        public DateTime UTCDate => DateTime.UtcNow + clockOffset;

        public double HoursToMeridian {
            get {
                if (TrackingEnabled) {
                    return Astrometry.MeridianFlip.TimeToMeridian(
                        coordinates: Coordinates,
                        localSiderealTime: Angle.ByHours(SiderealTime)).TotalHours;
                }
                return 24;
            }
        }

        public string HoursToMeridianString => AstroUtil.HoursToHMS(HoursToMeridian);

        public double TimeToMeridianFlip {
            get {
                try {
                    if (TrackingEnabled) {
                        return Astrometry.MeridianFlip.TimeToMeridianFlip(
                            settings: profileService.ActiveProfile.MeridianFlipSettings,
                            coordinates: Coordinates,
                            localSiderealTime: Angle.ByHours(SiderealTime),
                            currentSideOfPier: SideOfPier).TotalHours;
                    }
                } catch (Exception ex) {
                    Logger.Error(ex);
                }
                return 24;
            }
        }

        public string TimeToMeridianFlipString => AstroUtil.HoursToHMS(TimeToMeridianFlip);

        public PierSide DestinationSideOfPier(Coordinates coordinates) =>
            Astrometry.MeridianFlip.ExpectedPierSide(coordinates.Transform(Epoch.JNOW), Angle.ByHours(SiderealTime));

        #endregion Position

        #region Site

        public double SiteLatitude {
            get => siteLatitude;
            set => SetSite(value, siteLongitude);
        }

        public double SiteLongitude {
            get => siteLongitude;
            set => SetSite(siteLatitude, value);
        }

        /// <summary>The controller's elevation (:Gv#, :Sv); the profile's when the controller does not tell.</summary>
        public double SiteElevation {
            get => siteElevation;
            set {
                if (Run(d => {
                    d.SetElevation(value);
                    return true;
                }, false)) {
                    siteElevation = value;
                    RaisePropertyChanged();
                }
            }
        }

        private void SetSite(double latitude, double longitude) {
            if (Run(d => {
                d.SetSite(latitude, longitude);
                return true;
            }, false)) {
                siteLatitude = latitude;
                siteLongitude = longitude;
                RaisePropertyChanged(nameof(SiteLatitude));
                RaisePropertyChanged(nameof(SiteLongitude));
            }
        }

        #endregion Site

        #region Tracking

        public bool CanSetTrackingEnabled => Connected;

        public bool TrackingEnabled {
            get => CurrentState?.Status.Tracking ?? false;
            set {
                if (!Connected) {
                    return;
                }
                try {
                    RunOrThrow(value ? "switch tracking on" : "switch tracking off", d => d.SetTracking(value));
                } catch (InvalidOperationException ex) {
                    Notification.ShowError(ex.Message);
                    return;
                }
                InvalidateState();
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(TrackingMode));
                RaisePropertyChanged(nameof(TrackingRate));
            }
        }

        public IList<TrackingMode> TrackingModes { get; } = ImmutableList.Create(TrackingMode.Sidereal, TrackingMode.Lunar, TrackingMode.Solar, TrackingMode.King, TrackingMode.Stopped);

        /// <summary>From :GU# ('(' lunar, 'O' solar, 'k' King), so a rate set on the mount or by homing shows too.</summary>
        public TrackingRate TrackingRate => CurrentState?.Status is not { Tracking: true } status
            ? new TrackingRate { TrackingMode = TrackingMode.Stopped }
            : new TrackingRate {
                TrackingMode = status.TrackingRate switch {
                    OnStepXTrackingRate.Lunar => TrackingMode.Lunar,
                    OnStepXTrackingRate.Solar => TrackingMode.Solar,
                    OnStepXTrackingRate.King => TrackingMode.King,
                    _ => TrackingMode.Sidereal,
                }
            };

        public TrackingMode TrackingMode {
            get => TrackingRate.TrackingMode;
            set {
                if (value == TrackingMode.Custom) {
                    throw new ArgumentException("TrackingMode cannot be set to Custom. Use SetCustomTrackingRate");
                }
                if (!Connected) {
                    return;
                }
                if (value == TrackingMode.Stopped) {
                    TrackingEnabled = false;
                    return;
                }

                var rate = value switch {
                    TrackingMode.Lunar => OnStepXTrackingRate.Lunar,
                    TrackingMode.Solar => OnStepXTrackingRate.Solar,
                    TrackingMode.King => OnStepXTrackingRate.King,
                    _ => OnStepXTrackingRate.Sidereal,
                };
                var before = CurrentState?.Status;
                try {
                    if (rate == OnStepXTrackingRate.Sidereal) {
                        RunOrThrow("select the sidereal rate", d => d.SetTrackingRate(rate));
                        // :TQ# does not turn rate compensation back on (Mount.command.cpp): restore what the other rate turned off
                        if (compensationBeforeRateChange is { } restore && restore != OnStepXCompensation.None) {
                            RunOrThrow($"restore the rate compensation ({restore})", d => d.SetCompensation(restore));
                            Logger.Info($"OnStepX: rate compensation {restore} restored");
                        }
                        compensationBeforeRateChange = null;
                    } else {
                        // :TL#, :TS# and :TK# turn rate compensation off; remember it for the way back to sidereal
                        if (before is { } b && b.Compensation != OnStepXCompensation.None) {
                            compensationBeforeRateChange = b.Compensation;
                        }
                        RunOrThrow($"select the {rate} rate", d => d.SetTrackingRate(rate));
                    }
                } catch (InvalidOperationException ex) {
                    Notification.ShowError(ex.Message);
                    return;
                }
                InvalidateState();
                if (!TrackingEnabled) {
                    TrackingEnabled = true;
                }
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(TrackingRate));
            }
        }

        public bool CanSetDeclinationRate => false;

        public bool CanSetRightAscensionRate => false;

        public void SetCustomTrackingRate(double rightAscensionRate, double declinationRate) =>
            throw new NotSupportedException("Custom tracking rate not supported");

        #endregion Tracking

        #region Park and home

        public bool AtPark => CurrentState?.Status.Park == OnStepXParkState.Parked;

        public bool AtHome => CurrentState?.Status.AtHome ?? false;

        public bool CanPark => Connected;

        public bool CanUnpark => Connected;

        public bool CanSetPark => Connected;

        public bool CanFindHome => Connected;

        public async Task Park(CancellationToken token) {
            if (!Connected) {
                throw Failure("cannot park: not connected");
            }
            await StopMotionFirst("park", token);

            // a refusal throws, so TelescopeVM reports "failed to park" with the reason instead of "Mount has parked";
            // a lost reply leaves it to the status, like Unpark
            bool acknowledged = false;
            RunOrThrow("park", d => acknowledged = d.Park());
            MotionCommandSent();
            if (!acknowledged) {
                Logger.Warning("OnStepX: :hP# not acknowledged, checking the park state");
            }

            // the slew to the park position shows as parking ('I') or a slew; a mount already there parks at once
            State? outcome;
            try {
                outcome = await WaitFor(s => s.Status.Park is OnStepXParkState.Parked or OnStepXParkState.ParkFailed, MotionTimeout, token);
            } catch (OperationCanceledException) {
                AbortQuietly();
                throw;
            }
            if (outcome is null) {
                throw Failure(Connected
                    ? $"the mount did not report being parked within {MotionTimeout.TotalMinutes:F0} minutes"
                    : "the connection was lost while parking");
            }
            if (outcome.Status.Park == OnStepXParkState.ParkFailed) {
                throw Failure($"the mount reports that parking failed ({outcome.Status.Raw})");
            }
        }

        /// <summary>A refusal also shows as a notification: the API that calls this only reports "unknown error".</summary>
        public void Setpark() {
            if (!Connected) {
                return;
            }
            try {
                RunOrThrow("set the park position", d => d.SetParkPosition());
            } catch (InvalidOperationException ex) {
                Notification.ShowError(ex.Message);
                throw;
            }
        }

        public async Task Unpark(CancellationToken token) {
            if (!Connected) {
                throw Failure("cannot unpark: not connected");
            }
            bool acknowledged = false;
            RunOrThrow("unpark", d => acknowledged = d.Unpark());
            if (!acknowledged) {
                // like INDI: the single-character reply to :hR# can get lost although the controller unparks, so the
                // status decides
                Logger.Warning("OnStepX: :hR# not acknowledged, checking the park state");
            }
            var outcome = await WaitFor(s => s.Status.Park != OnStepXParkState.Parked, MotionStartTimeout, token);
            if (outcome is null) {
                throw Failure(Connected ? "the mount is still parked" : "the connection was lost while unparking");
            }
        }

        /// <summary>
        /// :hC#. Without home sensors OnStepX homes with a goto to the home position: :GU# shows 'h' (HS_HOMING) and a
        /// slew until the goto ends, then the controller clears 'h' and stops tracking (Home::requestDone). 'H' is not
        /// the end: it only means the axes are within the home tolerance, which a mount with absolute encoders can miss
        /// after a long slew. A firmware that never shows 'h' falls back to INDITelescope's standstill check.
        /// </summary>
        public async Task FindHome(CancellationToken token) {
            if (!Connected) {
                throw Failure("cannot find home: not connected");
            }
            var start = await StopMotionFirst("find home", token);
            if (start.Status.Park == OnStepXParkState.Parked) {
                throw Failure("cannot find home: the mount is parked");
            }

            // :hC# has no reply; a refusal (standby, date and time not set, in motion) comes from :GE# and throws
            RunOrThrow("find home", d => d.FindHome());
            MotionCommandSent();
            try {
                await WaitForHome(start, token);
            } catch (OperationCanceledException) {
                AbortQuietly();
                throw;
            }
        }

        private async Task WaitForHome(State start, CancellationToken token) {
            var moving = await WaitFor(s => s.Status.Homing || s.Status.Slewing || Moved(start, s), MotionStartTimeout, token);
            if (moving is null) {
                if (!Connected) {
                    throw Failure("the connection was lost while homing");
                }
                Logger.Info($"OnStepX: no motion after :hC#, the mount is at home already ({CurrentState?.Status.Raw})");
                return;
            }

            var deadline = DateTime.UtcNow + MotionTimeout;
            var last = moving;
            bool sawHoming = moving.Status.Homing;
            var stillSince = DateTime.UtcNow;
            while (DateTime.UtcNow < deadline) {
                await Task.Delay(PollInterval, token);
                if (GetState(true) is not { } now) {
                    if (!Connected) {
                        throw Failure("the connection was lost while homing");
                    }
                    continue;
                }
                Logger.Debug($"OnStepX: homing, :GU# {now.Status.Raw}, alt {now.Altitude:F3}°, az {now.Azimuth:F3}°");
                sawHoming |= now.Status.Homing;

                if (sawHoming) {
                    if (!now.Status.Homing && !now.Status.Slewing) {
                        Logger.Info($"OnStepX: homing finished, at home {now.Status.AtHome} ({now.Status.Raw})");
                        return;
                    }
                } else if (now.Status.Slewing || Moved(last, now)) {
                    stillSince = DateTime.UtcNow;
                } else if (!now.Status.WaitingAtHome && DateTime.UtcNow - stillSince >= HomeStandstillTimeout) {
                    Logger.Info($"OnStepX: homing finished, the mount stopped (no homing flag 'h' seen, {now.Status.Raw})");
                    return;
                }
                last = now;
            }
            throw Failure($"homing did not finish within {MotionTimeout.TotalMinutes:F0} minutes ({last.Status.Raw})");
        }

        private static bool Moved(State from, State to) =>
            Math.Abs(from.Altitude - to.Altitude) > StandstillDegrees || Math.Abs(AstroUtil.EuclidianModulus(from.Azimuth - to.Azimuth + 180, 360) - 180) > StandstillDegrees;

        /// <summary>
        /// Polls fresh states until <paramref name="done"/>; null on timeout or a lost connection. An unreadable state is
        /// skipped, never replaced by one from before the command.
        /// </summary>
        private async Task<State?> WaitFor(Func<State, bool> done, TimeSpan timeout, CancellationToken token) {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline) {
                var s = GetState(true);
                if (s is null) {
                    if (!Connected) {
                        return null;
                    }
                } else if (done(s)) {
                    return s;
                }
                await Task.Delay(PollInterval, token);
            }
            return null;
        }

        /// <summary>
        /// Park::request and Home::request refuse while a goto runs, also while it still brakes after :Q#, and during any
        /// guide motion: a manual move or a pulse (Park.cpp, Home.cpp). Stops everything (:Q#) and waits until the mount
        /// stands still; returns that fresh state.
        /// </summary>
        private async Task<State> StopMotionFirst(string action, CancellationToken token) {
            // a single unreadable status must not fail the park
            if (await WaitFor(_ => true, StateFailureSpan, token) is not { } s) {
                throw Failure(Connected ? $"cannot {action}: the mount state is unreadable" : $"cannot {action}: the connection was lost");
            }
            if (!Moving(s)) {
                return s;
            }
            Logger.Info($"OnStepX: stopping the mount before it can {action} ({s.Status.Raw})");
            StopSlew();
            var stopped = await WaitFor(x => !Moving(x), StopTimeout, token);
            if (stopped is null) {
                throw Failure(Connected
                    ? $"cannot {action}: the mount did not stop within {StopTimeout.TotalSeconds:F0} s"
                    : $"cannot {action}: the connection was lost");
            }
            return stopped;
        }

        /// <summary>Park::request and Home::request also refuse while a move brakes or homing runs (guide state not GU_NONE).</summary>
        private static bool Moving(State s) =>
            s.Status.Slewing || s.Status.ManualMove || s.Status.PulseGuiding || s.Status.Homing || s.CoordinatesMoving;

        #endregion Park and home

        #region Slewing

        /// <summary>
        /// A goto ('N' absent from :GU#), homing or a manual move, as ASCOM counts MoveAxis motion as slewing. A move
        /// faster than 2x shows as 'g'; a slower one runs as a pulse guide in the firmware ('G', Guide.cpp), so the moves
        /// this driver started count too. Homing with home sensors is a guide that shows neither, only 'h'
        /// (Home::request, guide.startHome), and a move braking after its stop shows nothing: both count while the
        /// coordinates move, as with INDI. Pulse guiding alone does not. Waits for the end of a goto, park or home use
        /// <see cref="OnStepXStatus.Slewing"/> alone.
        /// </summary>
        public bool Slewing => CurrentState is { } s
            && (s.Status.Slewing || s.Status.ManualMove || s.Status.Homing || s.CoordinatesMoving || ManualMoveRunning);

        private bool ManualMoveRunning {
            get {
                lock (moveLock) {
                    return primaryMove is not null || secondaryMove is not null;
                }
            }
        }

        public bool CanSlew => Connected;

        public bool CanSlewAltAz => false;

        public bool CanSetPierSide => false;

        public Coordinates? TargetCoordinates {
            get => Connected ? targetCoordinates : null;
            private set {
                targetCoordinates = value;
                RaisePropertyChanged();
            }
        }

        public PierSide? TargetSideOfPier {
            get => Connected ? targetSideOfPier : null;
            private set {
                targetSideOfPier = value;
                RaisePropertyChanged();
            }
        }

        /// <summary>
        /// Throws when the goto cannot start, ends in a timeout or the connection is lost: TelescopeVM ignores the
        /// result and would carry on as if the mount had arrived. A cancel stops the mount.
        /// </summary>
        public async Task<bool> SlewToCoordinates(Coordinates coordinates, CancellationToken token) {
            if (!Connected) {
                throw Failure("cannot slew: not connected");
            }
            if (AtPark) {
                throw Failure("cannot slew: the mount is parked");
            }

            try {
                bool trackingJustEnabled = false;
                if (!TrackingEnabled) {
                    TrackingEnabled = true;
                    trackingJustEnabled = true;
                    await Task.Delay(TrackingSettleTime, token);
                }

                var target = coordinates.Transform(Epoch.JNOW);
                TargetCoordinates = target;

                var error = StartGoto(target);
                if (error == OnStepXGotoError.BelowHorizon && trackingJustEnabled) {
                    Logger.Info("OnStepX: goto refused as below the horizon right after tracking was switched on, retrying once");
                    await Task.Delay(TrackingSettleTime, token);
                    error = StartGoto(target);
                }
                if (error != OnStepXGotoError.None) {
                    throw Failure($"the mount refused the goto to {target}: {error.Describe()}");
                }
                MotionCommandSent();

                await Task.Delay(TimeSpan.FromMilliseconds(200), token);
                var stopped = await WaitFor(s => !s.Status.Slewing || s.Status.WaitingAtHome, MotionTimeout, token);
                if (stopped is null) {
                    throw Failure(Connected
                        ? $"the goto did not end within {MotionTimeout.TotalMinutes:F0} minutes"
                        : "the connection was lost during the goto");
                }
                if (stopped.Status.WaitingAtHome) {
                    throw Failure("the goto waits at home (OnStepX's pause at home on a meridian flip): continue it on the mount or turn that pause off");
                }

                if (stopped.Status.Error != OnStepXError.None) {
                    Logger.Warning($"OnStepX: goto ended with controller error {stopped.Status.Error}");
                }
                return true;
            } catch (OperationCanceledException) {
                AbortQuietly();
                throw;
            } finally {
                TargetCoordinates = null;
            }
        }

        /// <summary>The goto's answer from :MS#; a failed command (target refused, no reply) throws.</summary>
        private OnStepXGotoError StartGoto(Coordinates target) {
            var result = OnStepXGotoError.Unspecified;
            RunOrThrow("start the goto", d => result = d.Goto(target.RA, target.Dec));
            return result;
        }

        public Task<bool> SlewToAltAz(TopocentricCoordinates coordinates, CancellationToken token) => Task.FromResult(false);

        public void StopSlew() {
            Run(d => {
                d.Abort();
                return true;
            }, false);
            lock (moveLock) {
                primaryMove = secondaryMove = null;
            }
            InvalidateState();
        }

        public bool Sync(Coordinates coordinates) {
            if (!Connected) {
                return false;
            }
            if (!TrackingEnabled) {
                Logger.Error("OnStepX: the mount is not tracking, cannot sync");
                Notification.ShowError(Loc.Instance["LblTelescopeNotTrackingForSync"]);
                return false;
            }

            var target = coordinates.Transform(Epoch.JNOW);
            var error = Run<OnStepXGotoError?>(d => d.Sync(target.RA, target.Dec), null);
            ResetCoordinateMotion();
            InvalidateState();
            if (error is OnStepXGotoError.None) {
                Logger.Info($"OnStepX: synced to {target}");
                return true;
            }
            if (error is { } refused) {
                Logger.Error($"OnStepX: sync to {target} refused: {refused.Describe()}");
                Notification.ShowError($"OnStepX: sync refused: {refused.Describe()}");
            }
            return false;
        }

        /// <summary>
        /// As IndiTelescope with a mount that cannot set its pier side: the flip is a goto to the target. OnStepX takes it
        /// on the side its preferred pier side asks for (<see cref="ITelescopeSettings.PreferredPierSide"/>); with
        /// "Best" it stays on the current side until the meridian limit (Goto.cpp setTarget). A goto that ends on the
        /// wrong side counts as failed, so the retries go on instead of reporting a flip that did not happen.
        /// </summary>
        public async Task<bool> MeridianFlip(Coordinates targetCoordinates, CancellationToken token) {
            var success = false;
            try {
                if (!TrackingEnabled) {
                    TrackingEnabled = true;
                }

                var expectedSideOfPier = Astrometry.MeridianFlip.ExpectedPierSide(
                    coordinates: targetCoordinates,
                    localSiderealTime: Angle.ByHours(SiderealTime));
                if (profileService.ActiveProfile.MeridianFlipSettings.UseSideOfPier) {
                    var sop = SideOfPier;
                    Logger.Info($"OnStepX: side of pier is {sop}, target {expectedSideOfPier}");
                    if (expectedSideOfPier == sop) {
                        Logger.Info("OnStepX: already on the target side of pier, no flip required");
                        return true;
                    }
                }

                targetCoordinates = targetCoordinates.Transform(Epoch.JNOW);
                TargetSideOfPier = expectedSideOfPier;
                int retries = 0;
                do {
                    Logger.Info($"OnStepX: slewing to {targetCoordinates} for the meridian flip, attempt {retries + 1} / {MeridianFlipSlewRetryAttempts}");
                    try {
                        success = await SlewToCoordinates(targetCoordinates, token);
                    } catch (InvalidOperationException ex) when (Connected) {
                        Logger.Warning($"OnStepX: meridian flip goto failed: {ex.Message}");
                        success = false;
                    }
                    if (success && !FlippedTo(expectedSideOfPier, retries == 0)) {
                        success = false;
                    }
                    if (!success) {
                        if (retries++ >= MeridianFlipSlewRetryAttempts) {
                            Logger.Error("OnStepX: meridian flip slew failed, even after retrying");
                            Notification.ShowError(Loc.Instance["LblMeridianFlipRetryFailed"]);
                            break;
                        }
                        Logger.Warning($"OnStepX: meridian flip slew failed, retry {retries} of {MeridianFlipSlewRetryAttempts} in {MeridianFlipSlewRetryWait}");
                        Notification.ShowWarning(string.Format(Loc.Instance["LblMeridianFlipRetry"], MeridianFlipSlewRetryWait.TotalSeconds, retries, MeridianFlipSlewRetryAttempts));
                        await Task.Delay(MeridianFlipSlewRetryWait, token);
                    }
                } while (!success);

                if (success && retries > 0) {
                    Notification.ShowWarning(string.Format(Loc.Instance["LblMeridianFlipWaitLonger"], retries));
                }
            } catch (OperationCanceledException) {
                throw;
            } catch (Exception ex) {
                Logger.Error(ex);
                Notification.ShowError(Loc.Instance["LblMeridianFlipFailed"] + ex.Message);
            } finally {
                TargetSideOfPier = null;
            }
            return success;
        }

        /// <summary>Whether the mount ended on <paramref name="expected"/>; an unknown side on either end counts as yes.</summary>
        private bool FlippedTo(PierSide expected, bool notify) {
            if (expected == PierSide.pierUnknown) {
                return true;
            }
            InvalidateState();
            var side = SideOfPier;
            if (side == PierSide.pierUnknown || side == expected) {
                return true;
            }
            Logger.Warning($"OnStepX: the meridian flip goto ended on {side}, expected {expected}");
            if (notify) {
                Notification.ShowWarning("OnStepX: the mount did not flip to the other side of the pier. Set the preferred pier side " +
                    "(TelescopeSettings.PreferredPierSide) to the side it should flip to; otherwise it flips only at the meridian limit.");
            }
            return false;
        }

        #endregion Slewing

        #region Manual move

        public bool CanMovePrimaryAxis => Connected;

        public bool CanMoveSecondaryAxis => Connected;

        /// <summary>The move rates in degrees per second, index = :R&lt;n&gt;#; the last two only with a known slew speed.</summary>
        private IReadOnlyList<double> MoveRates {
            get {
                var rates = OnStepXDevice.SiderealMoveRates.Select(m => m * SiderealDegreesPerSecond).ToList();
                if (!double.IsNaN(slewSpeed)) {
                    rates.Add(slewSpeed / 2);
                    rates.Add(slewSpeed);
                }
                return rates;
            }
        }

        public IList<(double, double)> GetAxisRates(TelescopeAxes axis) =>
            axis == TelescopeAxes.Tertiary ? [] : MoveRates.Select(r => (r, r)).ToList();

        public double PrimaryMovingRate {
            get => double.IsNaN(primaryMovingRate) ? MoveRates[^1] : primaryMovingRate;
            set {
                primaryMovingRate = MoveRates[NearestMoveRate(value)];
                RaisePropertyChanged();
            }
        }

        public double SecondaryMovingRate {
            get => double.IsNaN(secondaryMovingRate) ? MoveRates[^1] : secondaryMovingRate;
            set {
                secondaryMovingRate = MoveRates[NearestMoveRate(value)];
                RaisePropertyChanged();
            }
        }

        private int NearestMoveRate(double degreesPerSecond) {
            var rates = MoveRates;
            int best = 0;
            for (int i = 1; i < rates.Count; i++) {
                if (Math.Abs(rates[i] - Math.Abs(degreesPerSecond)) < Math.Abs(rates[best] - Math.Abs(degreesPerSecond))) {
                    best = i;
                }
            }
            return best;
        }

        /// <summary>INDI LX200_OnStep's default move rate, 8x.</summary>
        public const int DefaultMoveRateIndex = 5;

        /// <summary>The names of the move rates :R0# to :R9#, as INDI's LX200_OnStep labels them.</summary>
        public static readonly IReadOnlyList<string> MoveRateLabels = ["0.25x", "0.5x", "1x", "2x", "4x", "8x", "20x", "48x", "Half-Max", "Max"];

        /// <summary>The move rates in degrees per second, by index; the last two only once the slew speed is known.</summary>
        public IReadOnlyList<double> MoveRatesDegreesPerSecond => MoveRates;

        /// <summary>The move rate <see cref="MoveAxisDirection"/> uses: the controller's at connect, then the last one set.</summary>
        public int SelectedMoveRate => selectedMoveRate;

        /// <summary>
        /// Selects the move rate (:R&lt;n&gt;#) for <see cref="MoveAxisDirection"/>, like INDI's TELESCOPE_SLEW_RATE; throws
        /// <see cref="InvalidOperationException"/> when it cannot be sent.
        /// </summary>
        public void SelectMoveRate(int index) {
            if (index < 0 || index >= MoveRates.Count) {
                throw new ArgumentOutOfRangeException(nameof(index), index, $"move rate index 0-{MoveRates.Count - 1}");
            }
            int? pulseRateBefore = LatestStatus(PulseRefusalStatusMaxAge)?.PulseGuideRateIndex;
            RunOrThrow($"select the move rate {MoveRateLabels[index]}", d => d.SetMoveRate(index));
            selectedMoveRate = index;
            Logger.Info($"OnStepX: move rate {MoveRateLabels[index]} (:R{index}#)");
            NoticePulseRateChange(index, pulseRateBefore);
        }

        /// <summary>
        /// OnStepX also makes a move rate of 1x or slower the pulse-guide rate and stores it (GUIDE_SEPARATE_PULSE_RATE,
        /// Guide.command.cpp): say so, since a guide calibration made at the old rate no longer fits.
        /// </summary>
        private void NoticePulseRateChange(int moveRateIndex, int? pulseRateBefore) {
            if (moveRateIndex > 2 || pulseRateBefore is not { } before || before == moveRateIndex) {
                return;
            }
            if (TryReadStatus() is { } after && after.PulseGuideRateIndex != before && after.PulseGuideRateIndex is >= 0 and <= 2) {
                Logger.Warning($"OnStepX: the move rate {MoveRateLabels[moveRateIndex]} also became the pulse-guide rate (was {MoveRateLabels[before]})");
                Notification.ShowWarning($"OnStepX: the mount now also guides at {MoveRateLabels[moveRateIndex]} (was {MoveRateLabels[before]}), since OnStepX takes slow move rates as the guide rate. A guide calibration made at the old rate no longer fits.");
            }
        }

        /// <summary>
        /// Moves an axis at the selected move rate without touching it, like INDITelescope.MoveAxisDirection: sign
        /// positive = east / north, negative = west / south, 0 stops the axis. Repeating the direction the axis is
        /// already moving in sends nothing: OnStepX picks the motor direction for :Mn#/:Ms# from the pier side at that
        /// moment (Guide::axis2AutoSlew), which is none at home and west a little away from it, so a client's keepalive
        /// :Mn# would turn the mount back and forth around home.
        /// </summary>
        public void MoveAxisDirection(TelescopeAxes axis, int sign) {
            if (!Connected || axis == TelescopeAxes.Tertiary) {
                return;
            }
            if (sign != 0 && AtPark) {
                Notification.ShowWarning(Loc.Instance["LblTelescopeParkedWarn"]);
                return;
            }

            var (positive, negative) = Directions(axis);
            OnStepXDirection? wanted = sign == 0 ? null : sign > 0 ? positive : negative;
            // a stop must not end a guide pulse (:Qe# and friends stop it too): only an axis that is known to move
            bool firmwareMoving = LatestStatus(PulseRefusalStatusMaxAge)?.ManualMove == true;
            lock (moveLock) {
                OnStepXDirection? moving = axis == TelescopeAxes.Primary ? primaryMove : secondaryMove;
                if (wanted == moving && (wanted is not null || !firmwareMoving)) {
                    // the state read above clears a move the firmware ended, so a keepalive starts it again
                    return;
                }

                if (Run(d => {
                    if (wanted is { } direction) {
                        if (moving is { } previous) {
                            d.StopMove(previous);
                        }
                        d.StartMove(direction);
                    } else if (moving is { } current) {
                        d.StopMove(current);
                    } else {
                        d.StopMove(positive);
                        d.StopMove(negative);
                    }
                    return true;
                }, false)) {
                    SetMoving(axis, wanted);
                }
            }
            InvalidateState();
        }

        // Caller holds moveLock.
        private void SetMoving(TelescopeAxes axis, OnStepXDirection? direction) {
            moveCommandAt = DateTime.UtcNow;
            if (axis == TelescopeAxes.Primary) {
                primaryMove = direction;
            } else {
                secondaryMove = direction;
            }
        }

        private static (OnStepXDirection Positive, OnStepXDirection Negative) Directions(TelescopeAxes axis) =>
            axis == TelescopeAxes.Primary
                ? (OnStepXDirection.East, OnStepXDirection.West)
                : (OnStepXDirection.North, OnStepXDirection.South);

        /// <summary>
        /// Like INDITelescope: primary positive = east, secondary positive = north; rate 0 stops the axis. OnStep has one
        /// move rate for both axes, the last one set wins.
        /// </summary>
        public void MoveAxis(TelescopeAxes axis, double rate) {
            if (!Connected || axis == TelescopeAxes.Tertiary) {
                return;
            }
            if (AtPark) {
                Notification.ShowWarning(Loc.Instance["LblTelescopeParkedWarn"]);
                return;
            }

            var (positive, negative) = Directions(axis);
            int? pulseRateBefore = LatestStatus(PulseRefusalStatusMaxAge)?.PulseGuideRateIndex;
            int index = NearestMoveRate(rate);
            lock (moveLock) {
                Run(d => {
                    if (rate == 0) {
                        d.StopMove(positive);
                        d.StopMove(negative);
                        SetMoving(axis, null);
                    } else {
                        Logger.Info($"OnStepX: moving {axis} at {MoveRates[index]:F4}°/s (:R{index}#)");
                        d.SetMoveRate(index);
                        selectedMoveRate = index;
                        d.StartMove(rate > 0 ? positive : negative);
                        SetMoving(axis, rate > 0 ? positive : negative);
                    }
                    return true;
                }, false);
            }
            InvalidateState();
            if (rate != 0) {
                NoticePulseRateChange(index, pulseRateBefore);
            }
        }

        #endregion Manual move

        #region Guiding

        public bool CanPulseGuide => Connected;

        public double GuideRateRightAscensionArcsecPerSec => PulseGuideRate * SiderealDegreesPerSecond * 3600;

        public double GuideRateDeclinationArcsecPerSec => PulseGuideRate * SiderealDegreesPerSecond * 3600;

        /// <summary>
        /// The pulse-guide rate as a multiple of sidereal, from the rate index in :GU# (what :GX90# reports), since a move
        /// rate of 1x or slower also changes it; the rate read at connect when the index is not 0-2.
        /// </summary>
        private double PulseGuideRate => CurrentState?.Status.PulseGuideRateIndex switch {
            0 => 0.25,
            1 => 0.5,
            2 => 1.0,
            _ => guideRate,
        };

        /// <summary>
        /// True until a pulse's estimated end (<see cref="PulseGuide"/>), then until the controller no longer reports one
        /// running ('G' in :GU#, for either axis), so the guider never starts an exposure while the mount still moves.
        /// The controller is asked once per <see cref="PulseCheckInterval"/>; past <see cref="PulseEndTimeout"/> the
        /// pulse counts as over.
        /// </summary>
        public bool IsPulseGuiding {
            get {
                // a manual move at 2x or slower also shows as 'G': then the flag tells nothing about the pulse
                bool manualMove = ManualMoveRunning;
                lock (pulseLock) {
                    var now = DateTime.UtcNow;
                    if (now < PulseEnd) {
                        return true;
                    }
                    if (pulseEndConfirmed) {
                        return false;
                    }
                    if (manualMove) {
                        pulseEndConfirmed = true;
                        return false;
                    }
                    if (now - lastPulseCheck < PulseCheckInterval) {
                        return true;
                    }
                    lastPulseCheck = now;
                }

                var status = TryReadStatus();

                lock (pulseLock) {
                    var now = DateTime.UtcNow;
                    if (now < PulseEnd || pulseEndConfirmed) {
                        // a new pulse started, or another caller confirmed the end meanwhile
                        return now < PulseEnd;
                    }
                    var late = now - PulseEnd;
                    if (status is null) {
                        // unreadable: the pulse may still run; ask again until the timeout
                        if (late >= PulseEndTimeout) {
                            Logger.Error($"OnStepX: the end of a guide pulse could not be confirmed {late.TotalMilliseconds:F0} ms after its estimated end; counting it as over");
                            pulseEndConfirmed = true;
                            return false;
                        }
                        return true;
                    }
                    if (!status.PulseGuiding) {
                        pulseEndConfirmed = true;
                        if (pulseLateLogged) {
                            Logger.Warning($"OnStepX: a guide pulse ended {late.TotalMilliseconds:F0} ms after its estimated end (USB serial delay)");
                        }
                        return false;
                    }
                    if (late >= PulseEndTimeout) {
                        Logger.Error($"OnStepX: the controller still reports a pulse {late.TotalMilliseconds:F0} ms after its estimated end ({status.Raw}); counting it as over");
                        pulseEndConfirmed = true;
                        return false;
                    }
                    if (late >= PulseLateWarning) {
                        pulseLateLogged = true;
                    } else {
                        Logger.Debug($"OnStepX: pulse still running {late.TotalMilliseconds:F0} ms after its estimated end");
                    }
                    return true;
                }
            }
        }

        // Caller holds pulseLock.
        private DateTime PulseEnd => raPulseEnd > decPulseEnd ? raPulseEnd : decPulseEnd;

        /// <summary>
        /// Sends the pulse at once, past any read in progress (:Mg has no reply). A pulse the controller would refuse
        /// (Guide::validate: parked, a goto running, which a pulse would abort, or a limit or hardware error) throws
        /// instead, judged on the last known status without reading it again, which would delay the pulse.
        /// </summary>
        public void PulseGuide(GuideDirections direction, int duration) {
            if (!Connected) {
                Notification.ShowWarning(Loc.Instance["LblTelescopeNotConnected"]);
                return;
            }
            if (duration < 1) {
                return;
            }
            lock (pulseLock) {
                // a pulse aborts a goto that may already run (Guide::validate), and the last status predates it
                if (motionCommandAt > latestStatusAt) {
                    throw new InvalidOperationException("OnStepX refuses guide pulses: a goto, park or home has just been started");
                }
            }
            if (LatestStatus(PulseRefusalStatusMaxAge) is { } known && PulseRefusal(known) is { } reason) {
                throw new InvalidOperationException($"OnStepX refuses guide pulses: {reason} ({known.Raw})");
            }

            var dir = direction switch {
                GuideDirections.guideNorth => OnStepXDirection.North,
                GuideDirections.guideSouth => OnStepXDirection.South,
                GuideDirections.guideEast => OnStepXDirection.East,
                _ => OnStepXDirection.West,
            };
            int ms = Math.Min(duration, OnStepXDevice.MaxPulseMs);
            var onLine = Run(d => d.PulseGuide(dir, ms), null);
            if (onLine is not { } start) {
                throw new InvalidOperationException("OnStepX: the guide pulse could not be sent");
            }

            lock (pulseLock) {
                var end = start + TimeSpan.FromMilliseconds(ms) + PulseEndMargin;
                if (dir is OnStepXDirection.East or OnStepXDirection.West) {
                    raPulseEnd = end;
                } else {
                    decPulseEnd = end;
                }
                pulseEndConfirmed = false;
                pulseLateLogged = false;
            }
        }

        /// <summary>
        /// Why the controller would refuse a guide pulse in this status (OnStepX Guide::validate), or null: parked, a goto
        /// running (which the pulse would abort), a motor fault, and limit or initialization errors only while the move
        /// rate is 1x or slower (validate checks limits.isError() when the rate index is below 3). Standby does not show
        /// in :GU#.
        /// </summary>
        internal static string? PulseRefusal(OnStepXStatus status) {
            if (status.Park == OnStepXParkState.Parked) {
                return "the mount is parked";
            }
            if (status.Slewing) {
                return "a goto is running";
            }
            if (status.Homing) {
                // Guide::startAxis1 ignores a pulse while homing with sensors runs as a guide
                return "homing is running";
            }
            if (status.Error == OnStepXError.MotorFault) {
                return "motor fault";
            }
            if (status.MoveRateIndex is < 0 or >= 3) {
                return null;
            }
            return status.Error switch {
                OnStepXError.LimitSense or OnStepXError.AltitudeMin or OnStepXError.AltitudeMax or OnStepXError.AzimuthLimit
                    or OnStepXError.UnderPoleLimit or OnStepXError.DecLimit or OnStepXError.MeridianLimit => $"limit reached ({status.Error})",
                OnStepXError.SiteNotInitialized or OnStepXError.NvInitFailed => $"controller not initialized ({status.Error})",
                _ => null,
            };
        }

        private void RememberStatus(OnStepXStatus status) {
            lock (pulseLock) {
                latestStatus = status;
                latestStatusAt = DateTime.UtcNow;
            }
        }

        private OnStepXStatus? LatestStatus(TimeSpan maxAge) {
            lock (pulseLock) {
                return latestStatus is { } s && DateTime.UtcNow - latestStatusAt <= maxAge ? s : null;
            }
        }

        /// <summary>:GU# alone, quietly: null when unreadable; a port failure ends the connection.</summary>
        private OnStepXStatus? TryReadStatus() {
            if (!Connected || device is not { } d) {
                return null;
            }
            try {
                var status = d.GetStatus();
                RememberStatus(status);
                return status;
            } catch (OnStepXException ex) {
                Logger.Debug($"OnStepX: reading the status failed: {ex.Message}");
                return null;
            } catch (Exception ex) when (IsPortFailure(ex)) {
                ConnectionLost(ex);
                return null;
            }
        }

        #endregion Guiding
    }
}
