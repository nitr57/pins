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

        /// <summary>Consecutive unreadable states (the controller silent or garbled) before the connection counts as lost.</summary>
        private const int MaxStateFailures = 3;

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
        private OnStepXTrackingRate trackingRate = OnStepXTrackingRate.Sidereal;
        private readonly object pulseLock = new();
        private DateTime raPulseEnd = DateTime.MinValue;
        private DateTime decPulseEnd = DateTime.MinValue;
        private bool pulseEndConfirmed = true;
        private bool pulseLateLogged;
        private DateTime lastPulseCheck = DateTime.MinValue;
        private OnStepXStatus? latestStatus;
        private DateTime latestStatusAt = DateTime.MinValue;
        private int selectedMoveRate = DefaultMoveRateIndex;
        private OnStepXDirection? primaryMove;
        private OnStepXDirection? secondaryMove;
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
                KnownModels[port] = device.Model;
                return device.Model;
            } catch (Exception ex) {
                Logger.Debug($"OnStepX: no mount identified on {port} while scanning: {ex.Message}");
                return null;
            }
        }

        private sealed record State(OnStepXStatus Status, double RightAscension, double Declination, double Altitude, double Azimuth, double SiderealTime, OnStepXPierSide PierSide);

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
                    device = connectDevice(port.Trim());
                    token.ThrowIfCancellationRequested();
                    ReadMountSetup(device);
                    lock (stateLock) {
                        state = null;
                        stateFailures = 0;
                    }
                    connectionLost = 0;
                    KnownModels[port.Trim()] = device.Model;
                    Connected = true;
                    Logger.Info($"OnStepX: connected to {device.Model} on {port}, {device.Identification}");
                    CheckMountTime();
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

        public void Disconnect() {
            CloseDevice();
            Connected = false;
            RaiseAllPropertiesChanged();
        }

        public void Dispose() {
            CloseDevice();
            GC.SuppressFinalize(this);
        }

        private void CloseDevice() {
            primaryMove = secondaryMove = null;
            var d = Interlocked.Exchange(ref device, null);
            lock (stateLock) {
                state = null;
            }
            try {
                d?.Dispose();
            } catch (Exception ex) {
                Logger.Error("OnStepX: closing the port failed", ex);
            }
        }

        private void ReadMountSetup(OnStepXDevice d) {
            (siteLatitude, siteLongitude) = d.GetSite();
            siteElevation = profileService.ActiveProfile.AstrometrySettings.Elevation;
            slewSpeed = d.GetSlewSpeed();
            try {
                guideRate = d.GetPulseGuideRate();
            } catch (OnStepXException ex) {
                Logger.Warning($"OnStepX: guide rate unknown: {ex.Message}");
                guideRate = double.NaN;
            }
            alignmentMode = d.GetStatus().MountType switch {
                OnStepXMountType.AltAz => AlignmentMode.AltAz,
                OnStepXMountType.Fork or OnStepXMountType.ForkAlt => AlignmentMode.Polar,
                _ => AlignmentMode.GermanPolar,
            };
            trackingRate = OnStepXTrackingRate.Sidereal;
            int currentMoveRate = d.GetStatus().MoveRateIndex;
            selectedMoveRate = currentMoveRate is >= 0 and <= OnStepXDevice.MaxMoveRateIndex ? currentMoveRate : DefaultMoveRateIndex;
            Logger.Info($"OnStepX: site {siteLatitude:F4}, {siteLongitude:F4}, slew speed {slewSpeed:F2}°/s, guide rate {guideRate:F2}x, {alignmentMode}");
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

            lock (stateLock) {
                if (!fresh && state is { } s && DateTime.UtcNow - stateReadAt < StateMaxAge) {
                    return s;
                }

                try {
                    var status = d.GetStatus();
                    RememberStatus(status);
                    state = new State(status, d.GetRightAscension(), d.GetDeclination(), d.GetAltitude(), d.GetAzimuth(), d.GetSiderealTime(), status.PierSide);
                    stateReadAt = DateTime.UtcNow;
                    stateFailures = 0;
                    if (status.Error != OnStepXError.None && status.Error != lastReportedError) {
                        Logger.Warning($"OnStepX: controller reports error {status.Error} ({status.Raw})");
                    }
                    lastReportedError = status.Error;
                    return state;
                } catch (OnStepXException ex) {
                    Logger.Warning($"OnStepX: reading the mount state failed: {ex.Message}");
                    if (++stateFailures >= MaxStateFailures) {
                        ConnectionLost(ex);
                        return null;
                    }
                    return state;
                } catch (Exception ex) when (IsPortFailure(ex)) {
                    ConnectionLost(ex);
                    return null;
                }
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
            _ = Task.Run(Disconnect);
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

        /// <summary>The controller keeps no elevation; this is the profile's until set.</summary>
        public double SiteElevation {
            get => siteElevation;
            set {
                siteElevation = value;
                RaisePropertyChanged();
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
                if (Run(d => d.SetTracking(value), false)) {
                    InvalidateState();
                    RaisePropertyChanged();
                    RaisePropertyChanged(nameof(TrackingMode));
                    RaisePropertyChanged(nameof(TrackingRate));
                } else if (Connected) {
                    Logger.Error($"OnStepX: tracking {(value ? "on" : "off")} refused");
                    Notification.ShowError($"OnStepX: the mount refused to switch tracking {(value ? "on" : "off")}");
                }
            }
        }

        public IList<TrackingMode> TrackingModes { get; } = ImmutableList.Create(TrackingMode.Sidereal, TrackingMode.Lunar, TrackingMode.Solar, TrackingMode.Stopped);

        public TrackingRate TrackingRate => !Connected || !TrackingEnabled
            ? new TrackingRate { TrackingMode = TrackingMode.Stopped }
            : new TrackingRate {
                TrackingMode = trackingRate switch {
                    OnStepXTrackingRate.Lunar => TrackingMode.Lunar,
                    OnStepXTrackingRate.Solar => TrackingMode.Solar,
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
                    _ => OnStepXTrackingRate.Sidereal,
                };
                if (Run(d => {
                    d.SetTrackingRate(rate);
                    return true;
                }, false)) {
                    trackingRate = rate;
                }
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
                return;
            }
            if (CurrentState?.Status.Slewing == true) {
                // INDI LX200_OnStep::Park stops a running slew first
                Run(d => {
                    d.Abort();
                    return true;
                }, false);
                await Task.Delay(TimeSpan.FromMilliseconds(100), token);
            }

            if (!Run(d => d.Park(), false)) {
                if (Connected) {
                    var reason = GetState(true)?.Status.Error ?? OnStepXError.None;
                    Logger.Error($"OnStepX: park refused, controller error {reason}");
                    Notification.ShowError($"OnStepX: the mount refused to park ({reason})");
                }
                return;
            }

            // the slew to the park position shows as parking ('I') or a slew; a mount already there parks at once
            var outcome = await WaitFor(s => s.Status.Park is OnStepXParkState.Parked or OnStepXParkState.ParkFailed, MotionTimeout, token);
            if (outcome?.Status.Park == OnStepXParkState.ParkFailed) {
                Logger.Error($"OnStepX: park failed ({outcome.Status.Raw})");
                Notification.ShowError("OnStepX: the mount reports that parking failed");
            } else if (outcome is null) {
                Logger.Error("OnStepX: the mount did not report being parked");
            }
        }

        public void Setpark() {
            if (Connected && !Run(d => d.SetParkPosition(), false)) {
                Logger.Error("OnStepX: setting the park position refused");
                Notification.ShowError("OnStepX: the mount refused to set the park position");
            }
        }

        public async Task Unpark(CancellationToken token) {
            if (!Connected) {
                return;
            }
            if (!Run(d => d.Unpark(), false)) {
                // like INDI: the single-character reply to :hR# can get lost although the controller unparks, so the
                // status decides
                Logger.Warning("OnStepX: :hR# not acknowledged, checking the park state");
            }
            var outcome = await WaitFor(s => s.Status.Park != OnStepXParkState.Parked, MotionStartTimeout, token);
            if (outcome is null) {
                Logger.Error("OnStepX: the mount is still parked");
                Notification.ShowError("OnStepX: the mount did not unpark");
            }
        }

        /// <summary>
        /// :hC#. Without home sensors OnStepX homes with a goto to the home position: :GU# shows 'h' (HS_HOMING) and a
        /// slew until the goto ends, then the controller clears 'h' and stops tracking (Home::requestDone). 'H' is not
        /// the end: it only means the axes are within the home tolerance, which a mount with absolute encoders can miss
        /// after a long slew. A firmware that never shows 'h' falls back to INDITelescope's standstill check.
        /// </summary>
        public async Task FindHome(CancellationToken token) {
            if (!Connected || CurrentState is not { } start) {
                return;
            }
            if (start.Status.Park == OnStepXParkState.Parked) {
                Notification.ShowWarning(Loc.Instance["LblTelescopeParkedWarn"]);
                return;
            }
            if (!Run(d => {
                d.FindHome();
                return true;
            }, false)) {
                return;
            }

            var moving = await WaitFor(s => s.Status.Homing || s.Status.Slewing || Moved(start, s), MotionStartTimeout, token);
            if (moving is null) {
                Logger.Info($"OnStepX: no motion after :hC#, the mount is at home already or refused ({CurrentState?.Status.Raw})");
                return;
            }

            var deadline = DateTime.UtcNow + MotionTimeout;
            var last = moving;
            bool sawHoming = moving.Status.Homing;
            var stillSince = DateTime.UtcNow;
            while (DateTime.UtcNow < deadline) {
                await Task.Delay(PollInterval, token);
                if (GetState(true) is not { } now) {
                    return;
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
            Logger.Warning($"OnStepX: homing did not finish within {MotionTimeout.TotalMinutes:F0} minutes ({last.Status.Raw})");
        }

        private static bool Moved(State from, State to) =>
            Math.Abs(from.Altitude - to.Altitude) > StandstillDegrees || Math.Abs(AstroUtil.EuclidianModulus(from.Azimuth - to.Azimuth + 180, 360) - 180) > StandstillDegrees;

        /// <summary>Polls the state until <paramref name="done"/>; null on timeout or a lost connection.</summary>
        private async Task<State?> WaitFor(Func<State, bool> done, TimeSpan timeout, CancellationToken token) {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline) {
                if (GetState(true) is not { } s) {
                    return null;
                }
                if (done(s)) {
                    return s;
                }
                await Task.Delay(PollInterval, token);
            }
            return null;
        }

        #endregion Park and home

        #region Slewing

        public bool Slewing => CurrentState?.Status.Slewing ?? false;

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

        public async Task<bool> SlewToCoordinates(Coordinates coordinates, CancellationToken token) {
            if (!Connected || AtPark) {
                return false;
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
                if (error is not { } e || e != OnStepXGotoError.None) {
                    if (error is { } refused) {
                        Logger.Error($"OnStepX: goto to {target} refused: {refused.Describe()}");
                        Notification.ShowError($"OnStepX: goto refused: {refused.Describe()}");
                    }
                    return false;
                }

                InvalidateState();
                await Task.Delay(TimeSpan.FromMilliseconds(200), token);
                var stopped = await WaitFor(s => !s.Status.Slewing, MotionTimeout, token);
                if (stopped is null) {
                    return false;
                }

                if (stopped.Status.Error != OnStepXError.None) {
                    Logger.Warning($"OnStepX: goto ended with controller error {stopped.Status.Error}");
                }
                return true;
            } finally {
                TargetCoordinates = null;
            }
        }

        /// <summary>The goto result, or null when the command failed (already reported).</summary>
        private OnStepXGotoError? StartGoto(Coordinates target) =>
            Run<OnStepXGotoError?>(d => d.Goto(target.RA, target.Dec), null);

        public Task<bool> SlewToAltAz(TopocentricCoordinates coordinates, CancellationToken token) => Task.FromResult(false);

        public void StopSlew() {
            Run(d => {
                d.Abort();
                return true;
            }, false);
            primaryMove = secondaryMove = null;
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
        /// As IndiTelescope with a mount that cannot set its pier side: the flip is a goto to the target, which OnStepX
        /// takes on the other side of the pier, retried while the mount is still within its meridian limit.
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
                    success = await SlewToCoordinates(targetCoordinates, token);
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

        /// <summary>Selects the move rate (:R&lt;n&gt;#) for <see cref="MoveAxisDirection"/>, like INDI's TELESCOPE_SLEW_RATE.</summary>
        public void SelectMoveRate(int index) {
            if (index < 0 || index >= MoveRates.Count) {
                throw new ArgumentOutOfRangeException(nameof(index), index, $"move rate index 0-{MoveRates.Count - 1}");
            }
            if (Run(d => {
                d.SetMoveRate(index);
                return true;
            }, false)) {
                selectedMoveRate = index;
                Logger.Info($"OnStepX: move rate {MoveRateLabels[index]} (:R{index}#){(index <= 2 ? ", which OnStepX also makes the pulse-guide rate" : string.Empty)}");
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
            OnStepXDirection? moving = axis == TelescopeAxes.Primary ? primaryMove : secondaryMove;
            if (wanted is not null && wanted == moving) {
                return;
            }

            if (Run(d => {
                if (wanted is { } direction) {
                    if (moving is { } previous) {
                        d.StopMove(previous);
                    }
                    d.StartMove(direction);
                } else {
                    d.StopMove(positive);
                    d.StopMove(negative);
                }
                return true;
            }, false)) {
                SetMoving(axis, wanted);
            }
            InvalidateState();
        }

        private void SetMoving(TelescopeAxes axis, OnStepXDirection? direction) {
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
            Run(d => {
                if (rate == 0) {
                    d.StopMove(positive);
                    d.StopMove(negative);
                    SetMoving(axis, null);
                } else {
                    int index = NearestMoveRate(rate);
                    Logger.Info($"OnStepX: moving {axis} at {MoveRates[index]:F4}°/s (:R{index}#)");
                    d.SetMoveRate(index);
                    selectedMoveRate = index;
                    d.StartMove(rate > 0 ? positive : negative);
                    SetMoving(axis, rate > 0 ? positive : negative);
                }
                return true;
            }, false);
            InvalidateState();
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
                lock (pulseLock) {
                    var now = DateTime.UtcNow;
                    if (now < PulseEnd) {
                        return true;
                    }
                    if (pulseEndConfirmed) {
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
                    if (status is null || !status.PulseGuiding) {
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

        /// <summary>Why the controller would refuse a guide pulse in this status (OnStepX Guide::validate), or null.</summary>
        internal static string? PulseRefusal(OnStepXStatus status) {
            if (status.Park == OnStepXParkState.Parked) {
                return "the mount is parked";
            }
            if (status.Slewing) {
                return "a goto is running";
            }
            return status.Error switch {
                OnStepXError.MotorFault => "motor fault",
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
