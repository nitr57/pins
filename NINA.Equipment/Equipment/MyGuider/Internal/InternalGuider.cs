// SPDX-License-Identifier: MPL-2.0

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Globalization;
using NINA.Astrometry;
using NINA.Core.Interfaces;
using NINA.Core.Model;
using NINA.Core.Utility;
using NINA.Core.Utility.Notification;
using NINA.Equipment.Equipment.MyGuider;
using NINA.Equipment.Equipment.MyGuider.Advanced;
using NINA.Equipment.Equipment.MyGuider.PHD2.PhdEvents;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Profile.Interfaces;
using NINA.Image.Interfaces;
using NINA.GuideEngine.Algorithms;
using NINA.GuideEngine.Calibration;
using NINA.GuideEngine.Coach;
using NINA.GuideEngine.Core;
using NINA.GuideEngine.Guiding;
using NINA.GuideEngine.Incidents;
using NINA.GuideEngine.Logging;
using NINA.GuideEngine.Simulation;
using NINA.GuideEngine.Stats;

namespace NINA.Equipment.Equipment.MyGuider.Internal;

/// <summary>
/// The internal guider: the guiding engine (NINA.GuideEngine) as a NINA guider, taking its frames from the guide camera
/// slot. Implements <see cref="IGuider"/> for the sequencer/ninaAPI, and
/// <see cref="IAdvancedGuider"/>, <see cref="IGuidingCoach"/> and <see cref="IGuideIncidentRecorder"/> for rich UIs
/// (Touch-N-Stars). Public primitive properties double as settings that ninaAPI's generic guider get-settings/set-setting
/// endpoints can read and write.
/// </summary>
public sealed class InternalGuider : BaseINPC, IAdvancedGuider, IGuidingCoach, IGuideIncidentRecorder
{
    public const string DeviceId = "InternalGuider";

    /// <summary>Guide steps kept for <see cref="GetRecentSteps"/>: over an hour of 2 s frames for the UIs' graphs.</summary>
    private const int MaxSteps = 2000;

    /// <summary>Alerts kept for <see cref="GetRecentAlerts"/>: the recent history for the UIs, bounded so a repeating alert cannot grow memory.</summary>
    private const int MaxAlerts = 300;

    /// <summary>Bounds NINA's synchronous Disconnect; the coach cancel and the last guide frame normally take a few seconds.</summary>
    private static readonly TimeSpan DisconnectTimeout = TimeSpan.FromSeconds(20);

    /// <summary>How long a disconnect lets a running Guiding Coach session restore its temporary settings.</summary>
    private static readonly TimeSpan CoachCancelTimeout = TimeSpan.FromSeconds(10);

    /// <summary>How long a disconnect waits for a cancelled dark library build: the guide camera aborts its exposure.</summary>
    private static readonly TimeSpan DarkBuildStopTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Added to the exposures of the auto-select attempts: download and processing of each frame on a slow Pi.</summary>
    private static readonly TimeSpan AutoSelectTimeoutMargin = TimeSpan.FromSeconds(20);

    /// <summary>How often StartGuiding checks a calibration started elsewhere (e.g. from the UI) while it waits for it.</summary>
    private static readonly TimeSpan CalibrationPollInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>NINA's profile defaults for settling (1.5 px for 10 s, give up after 40 s), used when the profile has none.</summary>
    private const double DefaultSettlePixels = 1.5;
    private const double DefaultSettleTimeSeconds = 10;
    private const double DefaultSettleTimeoutSeconds = 40;

    /// <summary>Most frames per dark exposure: more barely improve the master dark and take minutes per exposure.</summary>
    private const int MaxDarkFramesPerExposure = 50;

    /// <summary>Tolerance when matching the requested exposure range to the dark exposure steps: floating-point noise only.</summary>
    private const double DarkExposureTolerance = 1e-9;

    /// <summary>
    /// Relative period difference up to which the stored periodic-error curve counts as the one the engine discarded (the
    /// event carries that curve); a curve stored since then with a clearly different period is kept.
    /// </summary>
    private const double SameStoredPeriodTolerance = 1e-3;

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private readonly IProfileService profileService;
    private readonly ITelescopeMediator telescopeMediator;
    private readonly IGuideCameraMediator guideCameraMediator;
    private readonly InternalGuiderOptions options;
    private readonly object sync = new();
    private readonly LinkedList<AdvancedGuideStep> steps = new();
    private readonly LinkedList<AdvancedGuiderAlert> alerts = new();
    private readonly SemaphoreSlim connectLock = new(1, 1);
    private readonly GuidingCoachHost coachHost;
    private readonly IncidentStore incidents;
    private readonly string periodicErrorStorePath;
    private readonly string pulseModelStorePath;

    private Guider? guider;
    private GuidingCoach? coach;
    private GuideCameraSource? slotCamera;
    private Simulator? simulator;
    private GuidingLog? guideLog;
    private StreamWriter? guideLogWriter;
    private GuideLogBridge? guideLogBridge;
    private volatile bool connected;
    private double pixelScale = 1.0;
    private GuideFrame? latestFrame;
    private IReadOnlyList<StarInfo> latestStars = [];
    private GuidePoint latestLock = GuidePoint.Invalid;
    private GuideStepEvent? lastStep;
    private string calibrationStep = string.Empty;
    private double calibrationProgress;
    private string settleStatus = string.Empty;
    private AdvancedGuiderAlert? lastError;

    // progress reporters of the StartGuiding/Dither calls in flight: each call adds and removes its own
    private readonly List<IProgress<ApplicationStatus>> progressSinks = [];

    // the auto-select in flight (single flight: concurrent calls wait for it instead of starting another)
    private readonly object autoSelectGate = new();
    private TaskCompletionSource<bool>? autoSelectTcs;
    private ICameraSource? cameraSource;
    private string darkLibrary = string.Empty;
    private volatile bool buildingDarks;

    // the dark library build in flight: a disconnect cancels it and waits for it before the guide camera is released
    private readonly object darkGate = new();
    private CancellationTokenSource? darkBuildCts;
    private Task darkBuildDone = Task.CompletedTask;

    // the pulse output of the connected session: the PulseOutput setting only applies at the next connect
    private bool sessionUsesCameraSt4;

    // each StartGuiding call; a cancelled call stops capturing only while no newer call has started
    private int startGeneration;
    private volatile bool pauseWhenSlewing = true;
    private volatile bool pauseWhenTrackingOff = true;

    public InternalGuider(IProfileService profileService, ITelescopeMediator telescopeMediator, IGuideCameraMediator guideCameraMediator)
        : this(profileService, telescopeMediator, guideCameraMediator, MoveOldStorage())
    {
    }

    /// <param name="incidentDirectory">Where incidents are stored (tests use a temp folder).</param>
    /// <param name="periodicErrorStorePath">Where the learned periodic errors are stored (tests use a temp file).</param>
    /// <param name="pulseModelStorePath">Where the learned pulse models are stored (tests use a temp file).</param>
    internal InternalGuider(IProfileService profileService, ITelescopeMediator telescopeMediator, IGuideCameraMediator guideCameraMediator,
        string incidentDirectory, string? periodicErrorStorePath = null, string? pulseModelStorePath = null)
    {
        this.profileService = profileService;
        this.periodicErrorStorePath = periodicErrorStorePath ?? DefaultPeriodicErrorStorePath;
        this.pulseModelStorePath = pulseModelStorePath ?? DefaultPulseModelStorePath;
        this.telescopeMediator = telescopeMediator;
        this.guideCameraMediator = guideCameraMediator;
        options = new InternalGuiderOptions(profileService, InternalGuiderOptions.SettingsId);
        guideCameraMediator.Disconnected += OnGuideCameraDisconnected;
        coachHost = new GuidingCoachHost(profileService, options, TrySetSetting, SettleFromProfile, () => buildingDarks);

        // incidents stay readable while the guider is not connected (the next morning)
        incidents = new IncidentStore(incidentDirectory, IncidentStoreOptionsNow());
        incidents.Saved += OnIncidentSaved;
        incidents.Deleted += OnIncidentDeleted;
        profileService.ProfileChanged += (_, _) =>
        {
            ApplySettingsIfConnected();
            RestoreStoredPeriodicError();
            RestoreStoredPulseModel();
            incidents.Options = IncidentStoreOptionsNow();
        };
    }

    /// <summary>
    /// Where the internal guider keeps its files: calibrations, periodic error, pulse model, darks, incidents, Guiding
    /// Coach reports and guide logs.
    /// </summary>
    public static string StorageDirectory => Path.Combine(CoreUtil.APPLICATIONTEMPPATH, "InternalGuider");

    /// <summary>Folder of the flight recorder's incidents.</summary>
    public static string IncidentDirectory => Path.Combine(StorageDirectory, "Incidents");

    private static int oldStorageChecked;

    /// <summary>
    /// The guider was a plugin first (pins-guider), which kept its files in NativeGuider/: they move to
    /// <see cref="StorageDirectory"/> once, so calibrations, darks and incidents carry over.
    /// </summary>
    /// <returns><see cref="IncidentDirectory"/>, for the constructor chain.</returns>
    private static string MoveOldStorage()
    {
        if (Interlocked.Exchange(ref oldStorageChecked, 1) == 0)
        {
            var old = Path.Combine(CoreUtil.APPLICATIONTEMPPATH, "NativeGuider");
            try
            {
                if (Directory.Exists(old) && !Directory.Exists(StorageDirectory))
                {
                    Directory.Move(old, StorageDirectory);
                    Logger.Info($"InternalGuider: moved the files of the pins-guider plugin from {old} to {StorageDirectory}");
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"InternalGuider: could not move {old} to {StorageDirectory}: {ex.Message}");
            }
        }

        return IncidentDirectory;
    }

    public event EventHandler<IGuideStep>? GuideEvent;

    public event EventHandler<AdvancedGuiderEventArgs>? AdvancedGuiderEvent;

    #region IDevice

    public bool HasSetupDialog => false;

    public string Id => DeviceId;

    public string Name => "Native Guider";

    public string DisplayName => Name;

    public string Category => "Guiders";

    public bool Connected => connected;

    public string Description => "Multi-star autoguider (PHD2-compatible algorithms) running inside pins, using the guide camera slot.";

    public string DriverInfo => "Native Guider";

    public string DriverVersion => typeof(InternalGuider).Assembly.GetName().Version?.ToString() ?? "0.1";

    public IList<string> SupportedActions => [];

    public string Action(string actionName, string actionParameters) => string.Empty;

    public string SendCommandString(string command, bool raw = true) => string.Empty;

    public bool SendCommandBool(string command, bool raw = true) => false;

    public void SendCommandBlind(string command, bool raw = true)
    {
    }

    public void SetupDialog()
    {
    }

    public async Task<bool> Connect(CancellationToken token)
    {
        await connectLock.WaitAsync(token).ConfigureAwait(false);
        bool changed = false;
        try
        {
            if (Connected)
            {
                return true;
            }

            await ConnectCoreAsync(token).ConfigureAwait(false);
            connected = true;
            changed = true;
            return true;
        }
        catch (GuideCameraException ex)
        {
            // configuration/hardware problem the user can fix: no stack trace in the log
            Logger.Warning($"InternalGuider: connect failed: {ex.Message}");
            Notification.ShowError($"Internal guider: {ex.Message}");
            await CleanupAsync().ConfigureAwait(false);
            return false;
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            Notification.ShowError($"Internal guider: {ex.Message}");
            await CleanupAsync().ConfigureAwait(false);
            return false;
        }
        finally
        {
            connectLock.Release();
            if (changed)
            {
                RaiseConnectionChanged();
            }
        }
    }

    public void Disconnect()
    {
        try
        {
            if (!Task.Run(DisconnectAsync).Wait(DisconnectTimeout))
            {
                Logger.Warning($"InternalGuider: disconnect did not finish within {DisconnectTimeout.TotalSeconds:F0} s, it continues in the background");
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
        }
    }

    // serialised with Connect: a disconnect during a connect runs once the connect has finished, and cleans up after it
    private async Task DisconnectAsync()
    {
        await connectLock.WaitAsync().ConfigureAwait(false);
        bool wasConnected = connected;
        try
        {
            await CleanupAsync().ConfigureAwait(false);
        }
        finally
        {
            connected = false;
            connectLock.Release();
            // only a real change: a listener answering it with Disconnect would otherwise be told again
            if (wasConnected)
            {
                RaiseConnectionChanged();
            }
        }
    }

    /// <summary>
    /// Raised outside the connect lock: a listener may answer with Connect or Disconnect (GuiderVM disconnects when the
    /// guider reports Connected = false), which takes the lock. Raised under it, that waited for itself.
    /// </summary>
    private void RaiseConnectionChanged()
    {
        RaisePropertyChanged(nameof(Connected));
        RaisePropertyChanged(nameof(State));
    }

    private async Task ConnectCoreAsync(CancellationToken ct)
    {
        RefreshCachedOptions();
        var settings = options.ToEngineSettings();
        sessionUsesCameraSt4 = false;
        ICameraSource camera;
        IPulseOutput output;
        IMountState mount;
        if (options.IsSimulator)
        {
            var scenario = options.SimulatorScenario;
            simulator = new Simulator(scenario, SystemClock.Instance);
            camera = simulator.Camera;
            output = simulator.Mount;
            mount = simulator.Mount;
            settings = settings with
            {
                FocalLengthMm = scenario.Camera.FocalLengthMm,
                PixelSizeUm = scenario.Camera.PixelSizeUm,
            };
            Logger.Info($"InternalGuider: using the built-in simulator ({scenario.Name})");
        }
        else
        {
            // the guide camera slot chooses and configures the camera; connecting the guider connects it if it isn't yet
            if (!guideCameraMediator.GetInfo().Connected)
            {
                Logger.Info("InternalGuider: connecting the guide camera");
                ct.ThrowIfCancellationRequested();
                if (!await guideCameraMediator.Connect().ConfigureAwait(false) || !guideCameraMediator.GetInfo().Connected)
                {
                    throw new GuideCameraException("The guide camera could not be connected. Choose it in the guide camera slot of the equipment.");
                }
            }

            var source = new GuideCameraSource(guideCameraMediator);
            source.Acquire();
            slotCamera = source;
            camera = source;
            if (options.UseCameraSt4)
            {
                if (source.IndiDeviceName is null)
                {
                    throw new GuideCameraException("The camera ST4 guide output needs an INDI guide camera. Choose the mount as guide output, or an INDI camera as guide camera.");
                }

                output = new CameraSt4PulseOutput(() => source.IndiDeviceName);
                sessionUsesCameraSt4 = true;
            }
            else
            {
                output = new MountPulseOutput(telescopeMediator);
            }

            Logger.Info($"InternalGuider: guide camera '{source.Name}': {source.SensorWidth}x{source.SensorHeight}, {source.PixelSizeUm} µm, {source.BitsPerPixel} bit");
            mount = new NinaMountState(telescopeMediator, () => pauseWhenSlewing, () => pauseWhenTrackingOff, mountOptional: options.UseCameraSt4);
            if (settings.FocalLengthMm <= 0)
            {
                Notification.ShowWarning("Internal guider: guide focal length is not set — arcsec values and calibration step use defaults. Set FocalLengthMm.");
            }
        }

        var g = new Guider(camera, output, mount, SystemClock.Instance, settings);
        g.EventRaised += OnEngineEvent;
        g.SetIncidentStore(incidents, IncidentTagsNow);
        guider = g;
        coach = new GuidingCoach(g, coachHost);
        cameraSource = camera;
        PixelScale = g.PixelScale;
        LoadDarks();

        if (options.ReuseCalibration && LoadStoredCalibration() is { } cal)
        {
            g.LoadCalibration(cal);
            Logger.Info("InternalGuider: restored calibration from a previous session");
        }

        RestoreStoredPeriodicError();
        RestoreStoredPulseModel();

        if (options.SaveGuideLog)
        {
            OpenGuideLog(camera, g, mount);
        }
    }

    private async Task CleanupAsync()
    {
        // before the loop stops and the guide camera is released: a dark still exposing would outlive both
        await StopDarkBuildAsync().ConfigureAwait(false);

        var c = coach;
        coach = null;
        if (c is { IsRunning: true })
        {
            // let the session restore its temporary settings before the camera goes away
            c.Cancel();
            try
            {
                await c.Completion.WaitAsync(CoachCancelTimeout).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.Debug($"InternalGuider: coach cancel: {ex.Message}");
            }
        }

        var g = guider;
        guider = null;
        if (g is not null)
        {
            try
            {
                await g.StopCaptureAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.Debug($"InternalGuider: stop capture: {ex.Message}");
            }

            // an incident still open is saved as stopped (its save event comes from the store)
            g.SetIncidentStore(null);
            g.EventRaised -= OnEngineEvent;
        }

        CloseGuideLog();
        // the guide camera stays connected: it belongs to its slot
        slotCamera?.Release();
        slotCamera = null;
        simulator = null;
        cameraSource = null;
        darkLibrary = string.Empty;
        autoSelectTcs?.TrySetResult(false);
    }

    /// <summary>
    /// The guide camera went away (disconnected in its slot, unplugged): the guider can't guide without it, so it
    /// disconnects too. In the background: the slot's disconnect waits for its event handlers.
    /// </summary>
    private Task OnGuideCameraDisconnected(object sender, EventArgs e)
    {
        if (slotCamera is not null)
        {
            Logger.Warning("InternalGuider: the guide camera was disconnected; disconnecting the guider");
            Notification.ShowWarning("Internal guider: the guide camera was disconnected, guiding stopped");
            _ = Task.Run(DisconnectAsync);
        }

        return Task.CompletedTask;
    }

    #endregion

    #region IGuider

    public double PixelScale
    {
        get => pixelScale;
        set
        {
            pixelScale = value;
            RaisePropertyChanged();
        }
    }

    /// <summary>PHD2-compatible state name (Stopped, Looping, Selected, Calibrating, Guiding, LostLock, Paused).</summary>
    public string State => guider?.State.ToPhd2AppState() ?? "Stopped";

    public bool CanClearCalibration => true;

    public bool CanSetShiftRate => false;

    public bool ShiftEnabled => false;

    public bool CanGetLockPosition => true;

    public SiderealShiftTrackingRate ShiftRate => SiderealShiftTrackingRate.Disabled;

    public async Task<bool> AutoSelectGuideStar()
    {
        var g = guider;
        if (g is null || g.State.IsGuidingActive() || g.State == GuiderState.Calibrating || RefusedWhileBuildingDarks())
        {
            return false;
        }

        // single flight: a call while a selection is running waits for that one instead of replacing its completion
        TaskCompletionSource<bool> tcs;
        bool start;
        lock (autoSelectGate)
        {
            if (autoSelectTcs is { Task.IsCompleted: false } running)
            {
                tcs = running;
                start = false;
            }
            else
            {
                tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                autoSelectTcs = tcs;
                start = true;
            }
        }

        if (start)
        {
            g.AutoSelectStar();
        }

        var timeout = TimeSpan.FromSeconds(g.Settings.ExposureMs / 1000.0 * (g.Settings.AutoSelectAttempts + 1)) + AutoSelectTimeoutMargin;
        try
        {
            return await tcs.Task.WaitAsync(timeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // end the selection, so that the next call starts a new one
            tcs.TrySetResult(false);
            return false;
        }
    }

    public async Task<bool> StartGuiding(bool forceCalibration, IProgress<ApplicationStatus> progress, CancellationToken ct)
    {
        var g = guider;
        if (g is null || !Connected)
        {
            Notification.ShowError("Internal guider is not connected");
            return false;
        }

        if (RefusedWhileBuildingDarks())
        {
            return false;
        }

        if (!SimulatorInUse && !CameraSt4InUse)
        {
            var info = telescopeMediator.GetInfo();
            if (info is null || !info.Connected)
            {
                Notification.ShowError("Internal guider: connect the mount before starting guiding");
                return false;
            }

            if (!info.CanPulseGuide)
            {
                Notification.ShowError("Internal guider: the mount driver does not support pulse guiding (use the camera ST4 guide output instead)");
                return false;
            }
        }

        if (!forceCalibration && g.State == GuiderState.Guiding)
        {
            // PHD2 parity: already guiding → nothing to do (NINA's Restore Guiding calls this before every frame)
            return true;
        }

        if (!forceCalibration && g.State == GuiderState.Calibrating)
        {
            // a calibration is running (e.g. started from the UI): wait for it instead of restarting, then settle below
            while (g.State == GuiderState.Calibrating)
            {
                await Task.Delay(CalibrationPollInterval, ct).ConfigureAwait(false);
            }

            if (g.State != GuiderState.Guiding)
            {
                return false;
            }
        }

        int generation = Interlocked.Increment(ref startGeneration);
        AddProgressSink(progress);
        try
        {
            var result = await g.StartGuidingAsync(SettleFromProfile(), forceCalibration, ct).ConfigureAwait(false);
            if (result.Success)
            {
                return true;
            }

            if (g.State is GuiderState.Guiding && result.Code == GuideErrorCode.SettleTimeout)
            {
                Notification.ShowWarning("Internal guider: guiding started but did not settle within the timeout");
                return true;
            }

            Notification.ShowError($"Internal guider: guiding could not be started — {result.Error}");
            return false;
        }
        catch (OperationCanceledException)
        {
            // a newer start owns the guider now: stopping capture would end that one too
            if (Volatile.Read(ref startGeneration) == generation)
            {
                await g.StopCaptureAsync().ConfigureAwait(false);
            }

            throw;
        }
        finally
        {
            RemoveProgressSink(progress);
            progress?.Report(new ApplicationStatus { Source = Name, Status = string.Empty });
        }
    }

    public async Task<bool> StopGuiding(CancellationToken ct)
    {
        var g = guider;
        if (g is null)
        {
            return false;
        }

        await g.StopCaptureAsync().ConfigureAwait(false);
        return true;
    }

    public async Task<bool> Dither(IProgress<ApplicationStatus> progress, CancellationToken ct)
    {
        var gs = profileService.ActiveProfile.GuiderSettings;
        return await DitherCoreAsync(gs.DitherPixels, gs.DitherRAOnly, progress, ct).ConfigureAwait(false);
    }

    public Task<bool> ClearCalibration(CancellationToken ct)
    {
        guider?.ClearCalibration();
        try
        {
            var store = LoadStore();
            if (CalibrationKeyNow() is { } key && store.Remove(key))
            {
                SaveStore(store);
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
        }

        return Task.FromResult(true);
    }

    public Task<bool> SetShiftRate(SiderealShiftTrackingRate shiftTrackingRate, CancellationToken ct) => Task.FromResult(false);

    public Task<bool> StopShifting(CancellationToken ct) => Task.FromResult(false);

    public Task<LockPosition?> GetLockPosition()
    {
        var p = guider?.LockPosition ?? GuidePoint.Invalid;
        return Task.FromResult(p.IsValid ? new LockPosition((float)p.X, (float)p.Y) : null);
    }

    #endregion

    #region IAdvancedGuider

    public AdvancedGuiderStatus GetStatus()
    {
        var g = guider;
        var decDirection = g?.Settings.DecGuideMode == GuideEngine.Core.DecGuideMode.Drift ? g.DecDirection : null;
        lock (sync)
        {
            var stats = g?.Statistics;
            var lockPos = g?.LockPosition ?? GuidePoint.Invalid;
            var primary = latestStars.FirstOrDefault(s => s.IsPrimary);
            return new AdvancedGuiderStatus
            {
                State = g?.State.ToString() ?? AdvancedGuiderStates.Stopped,
                Connected = Connected,
                IsSettling = g?.IsSettling ?? false,
                IsCalibrated = g?.Calibration is not null,
                CameraName = GuideCameraName ?? string.Empty,
                ExposureSeconds = (g?.Settings.ExposureMs ?? options.ToEngineSettings().ExposureMs) / 1000.0,
                PixelScale = PixelScale,
                FrameNumber = latestFrame?.FrameNumber ?? 0,
                LastProcessingMs = lastStep?.ProcessingMs ?? 0,
                LockX = lockPos.IsValid ? lockPos.X : null,
                LockY = lockPos.IsValid ? lockPos.Y : null,
                PrimaryStar = primary is null ? null : ToDto(primary),
                StarsUsed = lastStep?.StarsUsed ?? (primary is null ? 0 : 1),
                StarCount = latestStars.Count,
                CalibrationStep = calibrationStep,
                CalibrationProgress = calibrationProgress,
                WindowStats = stats is null ? null : ToDto(stats.Window, stats),
                SessionStats = stats is null ? null : ToDto(stats.Session, stats),
                LastError = lastError,
                SettleStatus = settleStatus,
                DarkLibrary = darkLibrary,
                Hints = g?.ActiveHints.Select(CoachMapping.ToDto).ToList() ?? [],
                CoachRunning = coach?.IsRunning ?? false,
                RaAlgorithmState = ToDto(g?.RaAlgorithm, PixelScale),
                DecAlgorithmState = ToDto(g?.DecAlgorithm, PixelScale),
                DecDrift = ToDecDriftDto(decDirection, PixelScale),
            };
        }
    }

    public IReadOnlyList<AdvancedGuideStep> GetRecentSteps(int maxCount)
    {
        lock (sync)
        {
            return steps.Skip(Math.Max(0, steps.Count - Math.Max(0, maxCount))).ToList();
        }
    }

    public IReadOnlyList<AdvancedGuiderAlert> GetRecentAlerts(int maxCount)
    {
        lock (sync)
        {
            return alerts.Skip(Math.Max(0, alerts.Count - Math.Max(0, maxCount))).ToList();
        }
    }

    public AdvancedGuiderFrame? GetLatestFrame()
    {
        lock (sync)
        {
            var f = latestFrame;
            if (f is null)
            {
                return null;
            }

            return new AdvancedGuiderFrame
            {
                FrameNumber = f.FrameNumber,
                Timestamp = (f.StartTime == default ? DateTimeOffset.UtcNow : f.StartTime).UtcDateTime,
                Width = f.Width,
                Height = f.Height,
                BitDepth = f.BitsPerPixel,
                Pixels = f.Pixels,
                LockX = latestLock.IsValid ? latestLock.X : null,
                LockY = latestLock.IsValid ? latestLock.Y : null,
                Stars = latestStars.Select(ToDto).ToList(),
            };
        }
    }

    public AdvancedGuiderCalibration? GetCalibration()
    {
        var cal = guider?.Calibration;
        if (cal is null)
        {
            return null;
        }

        double scale = PixelScale;
        return new AdvancedGuiderCalibration
        {
            Timestamp = cal.Timestamp.UtcDateTime,
            RaAngleDeg = cal.XAngle * 180.0 / Math.PI,
            DecAngleDeg = cal.YAngle * 180.0 / Math.PI,
            RaRatePxPerSec = cal.XRate * 1000.0,
            DecRatePxPerSec = cal.YRate * 1000.0,
            RaRateArcsecPerSec = cal.XRate * 1000.0 * scale,
            DecRateArcsecPerSec = cal.YRate * 1000.0 * scale,
            OrthogonalityErrorDeg = cal.YRate > 0 ? MountTransform.OrthogonalityErrorDegrees(cal.XAngle, cal.YAngle) : 0,
            DeclinationDeg = cal.Declination is { } d ? d * 180.0 / Math.PI : null,
            PierSide = cal.PierSide.ToString(),
            Binning = cal.Binning,
            RaSteps = cal.RaStepCount,
            DecSteps = cal.DecStepCount,
            LastIssue = cal.LastIssue.ToString(),
            DecFlipRequired = options.GetBool("DecFlipRequired"),
            Points = cal.Points.Select(p => new AdvancedCalibrationPoint { Direction = p.Direction, Step = p.Step, X = p.X, Y = p.Y }).ToList(),
        };
    }

    public IReadOnlyList<AdvancedGuiderSetting> GetSettings() => options.Describe();

    public bool TrySetSetting(string name, string value, out string error)
    {
        if (guider is { } running && (running.State.IsGuidingActive() || running.State == GuiderState.Calibrating || buildingDarks)
            && string.Equals(name, "Binning", StringComparison.OrdinalIgnoreCase))
        {
            error = "Stop guiding before changing the binning";
            return false;
        }

        if (!options.TrySet(name, value, out error))
        {
            return false;
        }

        var def = InternalGuiderOptions.Find(name);
        if (def?.Name == "ForgetPeriodicError")
        {
            ForgetPeriodicError();
            return true;
        }

        if (def?.Name == "SimulateFault")
        {
            return SimulateFault(value, out error);
        }

        if (def?.Name == "IncidentBudgetMb")
        {
            incidents.Options = IncidentStoreOptionsNow();
        }

        ApplySettingsIfConnected();
        if (def?.Name is "UseDarkLibrary" or "Binning")
        {
            LoadDarks();
        }
        if (def?.RequiresReconnect == true && Connected)
        {
            error = "saved; reconnect the guider to apply";
        }

        RaisePropertyChanged(def?.Name ?? name);
        return true;
    }

    public Task<bool> StartLooping(CancellationToken ct)
    {
        var g = guider;
        if (g is null || RefusedWhileBuildingDarks())
        {
            return Task.FromResult(false);
        }

        g.StartLooping();
        return Task.FromResult(true);
    }

    public async Task<bool> StopLooping(CancellationToken ct) => await StopGuiding(ct).ConfigureAwait(false);

    public Task<bool> SetPaused(bool paused, CancellationToken ct)
    {
        var g = guider;
        if (g is null)
        {
            return Task.FromResult(false);
        }

        if (paused)
        {
            g.Pause();
        }
        else
        {
            g.Resume();
        }

        return Task.FromResult(true);
    }

    public Task<bool> DitherBy(double pixels, bool raOnly, CancellationToken ct) => DitherCoreAsync(pixels, raOnly, null, ct);

    /// <summary>PHD2's standard dark exposure steps (seconds).</summary>
    private static readonly double[] DarkExposures = [0.01, 0.02, 0.05, 0.1, 0.2, 0.5, 1, 1.5, 2, 2.5, 3, 3.5, 4, 4.5, 5, 6, 7, 8, 9, 10, 15, 20, 30];

    public async Task<bool> BuildDarkLibrary(double minExposureSeconds, double maxExposureSeconds, int framesPerExposure, CancellationToken ct)
    {
        var g = guider;
        var cam = cameraSource;
        if (g is null || cam is null || simulator is not null)
        {
            return false;
        }

        var exposures = DarkExposures.Where(e => e >= minExposureSeconds - DarkExposureTolerance && e <= maxExposureSeconds + DarkExposureTolerance).ToList();
        if (exposures.Count == 0)
        {
            exposures.Add(Math.Max(DarkExposures[0], minExposureSeconds));
        }

        framesPerExposure = Math.Clamp(framesPerExposure, 1, MaxDarkFramesPerExposure);

        // the darks are taken outside the guide loop: the camera lease keeps the loop from starting meanwhile (a start
        // requested anyway waits for the release), so the two never expose on the guide camera at once
        CancellationTokenSource buildCts;
        TaskCompletionSource done;
        lock (darkGate)
        {
            if (buildingDarks || coach?.IsRunning == true || g.State.IsCapturing() || !g.TryLeaseCamera())
            {
                Notification.ShowError("Internal guider: stop guiding/looping (and the Guiding Coach) before building a dark library");
                return false;
            }

            buildingDarks = true;
            buildCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            darkBuildCts = buildCts;
            done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            darkBuildDone = done.Task;
        }

        ct = buildCts.Token;
        int total = exposures.Count * framesPerExposure;
        int index = 0;
        try
        {
            var s = g.Settings;
            var masters = new List<GuideFrame>();
            foreach (var e in exposures)
            {
                var builder = new GuideEngine.Imaging.MasterDarkBuilder();
                for (int i = 0; i < framesPerExposure; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    Raise(AdvancedGuiderEventTypes.Darks, DateTimeOffset.UtcNow, new { status = "capturing", exposure = e, frame = i + 1, framesPerExposure, index = ++index, total });
                    var frame = await cam.CaptureAsync(new CaptureRequest(e * 1000.0, s.Binning, default, s.Gain, s.Offset), ct).ConfigureAwait(false);
                    builder.Add(frame);
                }

                var master = builder.Build();
                master.ExposureMs = e * 1000.0;
                master.Binning = s.Binning;
                masters.Add(master);
            }

            var path = DarkLibraryFiles.NativePath(cam.Name, cam.SensorWidth, cam.SensorHeight, s.Binning);
            DarkLibraryFiles.Save(path, masters, s.Gain);
            LoadDarks();
            Raise(AdvancedGuiderEventTypes.Darks, DateTimeOffset.UtcNow, new { status = "done", total, message = darkLibrary });
            Notification.ShowSuccess($"Internal guider: dark library built ({masters.Count} exposures)");
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Logger.Info("InternalGuider: dark library build cancelled");
            Raise(AdvancedGuiderEventTypes.Darks, DateTimeOffset.UtcNow, new { status = "failed", message = "cancelled" });
            return false;
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            Raise(AdvancedGuiderEventTypes.Darks, DateTimeOffset.UtcNow, new { status = "failed", message = ex.Message });
            Notification.ShowError($"Internal guider: dark library failed — {ex.Message}");
            return false;
        }
        finally
        {
            lock (darkGate)
            {
                darkBuildCts = null;
                buildingDarks = false;
            }

            buildCts.Dispose();
            g.ReleaseCamera();
            done.SetResult();
        }
    }

    /// <summary>
    /// While a dark library is being built the guide camera takes darks: guiding, looping and star selection are refused
    /// until it is done.
    /// </summary>
    private bool RefusedWhileBuildingDarks()
    {
        if (!buildingDarks)
        {
            return false;
        }

        Notification.ShowError("Internal guider: a dark library is being built — wait until it finishes");
        return true;
    }

    /// <summary>Cancels a dark library build in flight and waits until it has stopped using the guide camera.</summary>
    private async Task StopDarkBuildAsync()
    {
        Task done;
        lock (darkGate)
        {
            darkBuildCts?.Cancel();
            done = darkBuildDone;
        }

        try
        {
            await done.WaitAsync(DarkBuildStopTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Logger.Warning($"InternalGuider: the dark library build did not stop within {DarkBuildStopTimeout.TotalSeconds:F0} s");
        }
    }

    public Task<AdvancedCoachStartResult> StartCoach(AdvancedCoachOptions options, CancellationToken ct)
    {
        var c = coach;
        if (c is null || !Connected)
        {
            return Task.FromResult(new AdvancedCoachStartResult
            {
                Accepted = false,
                Message = "The guider is not connected",
                MessageCode = CoachCodes.NotConnected,
                Status = GetCoachStatus(),
            });
        }

        var result = c.Start(CoachMapping.FromDto(options));
        if (result.Accepted)
        {
            Logger.Info($"InternalGuider: Guiding Coach session {result.Status.SessionId} started ({string.Join(", ", result.Status.Steps.Select(s => s.Name))})");
        }
        else
        {
            Logger.Info($"InternalGuider: Guiding Coach start rejected: {result.MessageCode} {result.Message}");
        }

        return Task.FromResult(CoachMapping.ToDto(result));
    }

    public Task<bool> SkipCoachStep(CancellationToken ct) => Task.FromResult(coach?.SkipStep() ?? false);

    public Task<bool> CancelCoach(CancellationToken ct) => Task.FromResult(coach?.Cancel() ?? false);

    public AdvancedCoachStatus GetCoachStatus()
    {
        var c = coach;
        if (c is not null)
        {
            return CoachMapping.ToDto(c.Status);
        }

        return new AdvancedCoachStatus { Phase = AdvancedCoachPhases.Idle, CurrentExposureSeconds = options.ToEngineSettings().ExposureMs / 1000.0 };
    }

    public Task<bool> ApplyCoachActions(IReadOnlyCollection<string> ids, CancellationToken ct)
    {
        var c = coach;
        if (c is null || ids is null || ids.Count == 0)
        {
            return Task.FromResult(false);
        }

        if (!c.ApplyActions(ids.ToList(), out var error))
        {
            Logger.Warning($"InternalGuider: coach actions not applied: {error}");
            return Task.FromResult(false);
        }

        return Task.FromResult(true);
    }

    public IReadOnlyList<AdvancedCoachReport> GetCoachHistory(int maxCount)
    {
        int max = Math.Max(0, maxCount);
        var reports = coach?.GetHistory(max) ?? coachHost.LoadReports(max);
        return reports.Select(r => CoachMapping.ToDto(r)).ToList();
    }

    public bool DismissHint(string id) => guider?.DismissHint(id) ?? false;

    public AdvancedIncidentList GetIncidents()
    {
        var opts = incidents.Options;
        var list = new AdvancedIncidentList
        {
            Enabled = options.IncidentRecorder,
            RecordingId = guider?.RecordingIncidentId,
            BudgetBytes = opts.BudgetBytes,
            MaxIncidents = opts.MaxIncidents,
            SimulatorBudgetBytes = opts.SimulatorBudgetBytes,
        };
        try
        {
            list.Incidents = incidents.List().Select(IncidentMapping.ToSummary).ToList();
            var usage = incidents.Usage;
            list.UsedBytes = usage.Bytes;
            list.SimulatorUsedBytes = usage.SimulatorBytes;
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
        }

        return list;
    }

    public AdvancedIncident? GetIncident(string id) => Safe(() => incidents.Get(id) is { } i ? IncidentMapping.ToDto(i) : null);

    public AdvancedIncidentImage? GetIncidentImage(string id, string kind, long frame) =>
        Safe(() => IncidentMapping.ImageKind(kind) is { } k && incidents.ReadImage(id, k, frame) is { } img ? IncidentMapping.ToDto(img) : null);

    public IReadOnlyList<AdvancedIncidentImage> GetIncidentCrops(string id, long frame) =>
        Safe(() => incidents.ReadCrops(id, frame).Select(IncidentMapping.ToDto).ToList()) ?? [];

    public bool SetIncidentKept(string id, bool kept) => Safe(() => incidents.SetKept(id, kept) is not null);

    public bool DeleteIncident(string id)
    {
        bool deleted = Safe(() => incidents.Delete(id));
        if (deleted)
        {
            Logger.Info($"InternalGuider: incident {id} deleted");
        }

        return deleted;
    }

    public int DeleteAllIncidents()
    {
        int n = Safe(() => incidents.DeleteAllNotKept());
        Logger.Info($"InternalGuider: {n} incidents deleted");
        return n;
    }

    public string? MarkIncident(string? note, out string error)
    {
        var g = guider;
        if (g is null || !Connected)
        {
            error = "The internal guider is not connected";
            return null;
        }

        if (!options.IncidentRecorder)
        {
            error = "The incident recorder is turned off in the settings";
            return null;
        }

        var id = g.MarkIncident(note, out var refused);
        error = refused ?? string.Empty;
        if (id is not null)
        {
            Logger.Info($"InternalGuider: incident marked ({id}){(string.IsNullOrWhiteSpace(note) ? string.Empty : $": {note}")}");
        }

        return id;
    }

    public Task<bool> WriteIncidentArchive(string id, Stream output, CancellationToken ct)
    {
        if (output is null || !Safe(() => incidents.Exists(id)))
        {
            return Task.FromResult(false);
        }

        return Task.Run(() =>
        {
            var incident = incidents.Get(id);
            if (incident is null)
            {
                return false;
            }

            ct.ThrowIfCancellationRequested();
            try
            {
                return incidents.WriteZip(id, output, ArchiveExtras(incident));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // e.g. the client went away, or the incident was deleted meanwhile
                Logger.Warning($"InternalGuider: incident {id} download failed: {ex.Message}");
                return false;
            }
        }, ct);
    }

    private void LoadDarks()
    {
        var g = guider;
        var cam = cameraSource;
        if (g is null || cam is null)
        {
            return;
        }

        if (!options.UseDarkLibrary || simulator is not null)
        {
            g.Preprocessor.Darks = null;
            darkLibrary = string.Empty;
            return;
        }

        try
        {
            var loaded = DarkLibraryFiles.Load(cam.Name, cam.SensorWidth, cam.SensorHeight, Math.Max(1, options.GetInt("Binning")));
            if (loaded is { } named)
            {
                named.Library.Name = Path.GetFileName(named.Source);
            }

            g.Preprocessor.Darks = loaded?.Library;
            darkLibrary = loaded is { } l ? $"{Path.GetFileName(l.Source)} ({l.Library.Count} darks)" : string.Empty;
            if (loaded is null)
            {
                Logger.Info("InternalGuider: no dark library found for the guide camera");
            }
        }
        catch (Exception ex)
        {
            Logger.Warning($"InternalGuider: dark library not loaded: {ex.Message}");
        }
    }

    #endregion

    #region settings as properties (ninaAPI guider get-settings / set-setting)

    public string GuideSource { get => options.GetString(nameof(GuideSource)); set => Set(nameof(GuideSource), value); }

    public double FocalLengthMm { get => options.FocalLengthMm; set => Set(nameof(FocalLengthMm), value.ToString(Inv)); }

    public double ExposureSeconds { get => options.GetDouble(nameof(ExposureSeconds)); set => Set(nameof(ExposureSeconds), value.ToString(Inv)); }

    public int Gain { get => options.GetInt(nameof(Gain)); set => Set(nameof(Gain), value.ToString(Inv)); }

    public int Binning { get => options.GetInt(nameof(Binning)); set => Set(nameof(Binning), value.ToString(Inv)); }

    public string PulseOutput { get => options.GetString(nameof(PulseOutput)); set => Set(nameof(PulseOutput), value); }

    public string DecGuideMode { get => options.GetString(nameof(DecGuideMode)); set => Set(nameof(DecGuideMode), value); }

    public bool MultiStar { get => options.GetBool(nameof(MultiStar)); set => Set(nameof(MultiStar), value ? "true" : "false"); }

    public int MaxStars { get => options.GetInt(nameof(MaxStars)); set => Set(nameof(MaxStars), value.ToString(Inv)); }

    public double MinSnr { get => options.GetDouble(nameof(MinSnr)); set => Set(nameof(MinSnr), value.ToString(Inv)); }

    public double RaAggression { get => options.GetDouble(nameof(RaAggression)); set => Set(nameof(RaAggression), value.ToString(Inv)); }

    public double DecAggression { get => options.GetDouble(nameof(DecAggression)); set => Set(nameof(DecAggression), value.ToString(Inv)); }

    public int CalibrationStepMs { get => options.GetInt(nameof(CalibrationStepMs)); set => Set(nameof(CalibrationStepMs), value.ToString(Inv)); }

    public bool DecFlipRequired { get => options.GetBool(nameof(DecFlipRequired)); set => Set(nameof(DecFlipRequired), value ? "true" : "false"); }

    /// <summary>Write-mostly generic setter: "Name=Value" for any internal guider setting (see GetSettings()).</summary>
    public string InternalGuiderSetting
    {
        get => string.Empty;
        set
        {
            int eq = value?.IndexOf('=') ?? -1;
            if (eq > 0)
            {
                Set(value![..eq].Trim(), value[(eq + 1)..].Trim());
            }
        }
    }

    private void Set(string name, string value)
    {
        if (!TrySetSetting(name, value, out var error) && !string.IsNullOrEmpty(error))
        {
            Notification.ShowWarning($"Internal guider: {error}");
            throw new ArgumentException(error);
        }
    }

    private void RefreshCachedOptions()
    {
        pauseWhenSlewing = options.GetBool("PauseWhenSlewing");
        pauseWhenTrackingOff = options.GetBool("PauseWhenTrackingOff");
    }

    private void ApplySettingsIfConnected()
    {
        RefreshCachedOptions();
        var g = guider;
        if (g is null)
        {
            return;
        }

        try
        {
            var s = options.ToEngineSettings();
            if (simulator is { } sim)
            {
                s = s with { FocalLengthMm = sim.Scenario.Camera.FocalLengthMm, PixelSizeUm = sim.Scenario.Camera.PixelSizeUm };
            }

            g.UpdateSettings(s);
            PixelScale = s.PixelScale(slotCamera?.PixelSizeUm ?? simulator?.Camera.PixelSizeUm ?? 0);
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
        }
    }

    #endregion

    #region engine events

    // internal for the tests
    internal void OnEngineEvent(object? sender, GuiderEvent e)
    {
        try
        {
            switch (e)
            {
                case GuideStepEvent { CoachMeasurement: true } s:
                    // coach measurement frame (guiding output off): not a guide step for NINA or the guide graph
                    lock (sync)
                    {
                        lastStep = s;
                    }

                    break;
                case GuideStepEvent s:
                    OnGuideStep(s);
                    break;
                case CoachStatusEvent cs:
                    Raise(AdvancedGuiderEventTypes.Coach, e.Timestamp, CoachMapping.ToDto(cs.Status));
                    break;
                case CoachHintEvent ch:
                    Logger.Info($"InternalGuider: hint {ch.Hint.Code}: {ch.Hint.Message}");
                    Raise(AdvancedGuiderEventTypes.Hint, e.Timestamp, CoachMapping.ToDto(ch.Hint));
                    break;
                case AlgorithmNoteEvent n:
                    Logger.Debug($"InternalGuider: Predictive {n.Axis} {n.Kind.ToString().ToLowerInvariant()}: {n.Message}");
                    break;
                case EngineFaultEvent f:
                    Logger.Error($"InternalGuider: engine fault in {f.Source}, guiding continues: {f.Message} ({f.Suppressed} more since the last report)");
                    break;
                case DecDirectionNoteEvent d:
                    Logger.Debug($"InternalGuider: Dec direction {d.Kind.ToString().ToLowerInvariant()}: {d.Message}");
                    if (d.Kind is DecDirectionNoteKind.Switch or DecDirectionNoteKind.ValveOpened or DecDirectionNoteKind.ValveClosed)
                    {
                        // the UI shows the direction: don't wait for the next poll
                        Raise(AdvancedGuiderEventTypes.State, e.Timestamp, GetStatus());
                    }

                    break;
                case AppStateEvent st:
                    if (st.State is not GuiderState.Calibrating)
                    {
                        calibrationStep = string.Empty;
                        calibrationProgress = 0;
                    }

                    RaisePropertyChanged(nameof(State));
                    Raise(AdvancedGuiderEventTypes.State, e.Timestamp, GetStatus());
                    break;
                case AlertEvent a:
                    OnAlert(a);
                    break;
                case IncidentStartedEvent started:
                    Logger.Info($"InternalGuider: recording incident {started.Id}");
                    Raise(AdvancedGuiderEventTypes.Incident, e.Timestamp, new AdvancedIncidentEvent
                    {
                        Action = "started",
                        Id = started.Id,
                        Summary = IncidentMapping.Started(started, IncidentTagsNow()),
                    });
                    break;
                case StarSelectedEvent:
                    autoSelectTcs?.TrySetResult(true);
                    break;
                case StartCalibrationEvent:
                    calibrationStep = "Starting calibration";
                    calibrationProgress = 0;
                    Report("Calibrating");
                    break;
                case CalibratingEvent c:
                    calibrationStep = string.IsNullOrEmpty(c.State) ? $"{c.Direction} step {c.Step}" : c.State;
                    calibrationProgress = c.Progress;
                    Report($"Calibrating: {calibrationStep}");
                    Raise(AdvancedGuiderEventTypes.Calibration, e.Timestamp, new { step = calibrationStep, progress = c.Progress, direction = c.Direction, dx = c.Dx, dy = c.Dy, distance = c.Distance });
                    break;
                case CalibrationCompleteEvent:
                    calibrationStep = "Calibration complete";
                    calibrationProgress = 1;
                    Raise(AdvancedGuiderEventTypes.Calibration, e.Timestamp, GetCalibration());
                    break;
                case CalibrationFailedEvent cf:
                    calibrationStep = $"Calibration failed: {cf.Reason}";
                    Raise(AdvancedGuiderEventTypes.Calibration, e.Timestamp, new { step = calibrationStep, failed = true, reason = cf.Reason });
                    break;
                case CalibrationUpdatedEvent cu:
                    PersistCalibration(cu);
                    break;
                case PeriodicErrorModelEvent pe:
                    PersistPeriodicError(pe.Model);
                    break;
                case PeriodicErrorModelDiscardedEvent pd:
                    DiscardPeriodicError(pd.Model);
                    break;
                case PulseModelLearnedEvent pm:
                    PersistPulseModel(pm.State);
                    break;
                case PulseModelUpdatedEvent pu:
                    Logger.Info($"InternalGuider: {GuideLogBridge.PulseModelText(pu)}");
                    break;
                case SettleBeginEvent:
                    settleStatus = "Settling";
                    Report("Settling");
                    Raise(AdvancedGuiderEventTypes.Settle, e.Timestamp, new { status = "begin" });
                    break;
                case SettlingEvent se:
                    settleStatus = $"Settling: {se.Distance:F2} px, {se.Time:F0}/{se.SettleTime:F0} s";
                    Report(settleStatus);
                    Raise(AdvancedGuiderEventTypes.Settle, e.Timestamp, new { status = "settling", distance = se.Distance, time = se.Time, settleTime = se.SettleTime, starLocked = se.StarLocked });
                    break;
                case SettleDoneEvent sd:
                    settleStatus = sd.Succeeded ? "Settled" : $"Settle failed: {sd.Error}";
                    Raise(AdvancedGuiderEventTypes.Settle, e.Timestamp, new { status = sd.Succeeded ? "done" : "failed", error = sd.Error, totalFrames = sd.TotalFrames, droppedFrames = sd.DroppedFrames });
                    break;
                case GuidingDitheredEvent gd:
                    Raise(AdvancedGuiderEventTypes.Dither, e.Timestamp, new { dx = gd.Dx, dy = gd.Dy });
                    break;
                case StarLostEvent sl:
                    Raise(AdvancedGuiderEventTypes.StarLost, e.Timestamp, new { frame = sl.Frame, snr = sl.Snr, mass = sl.StarMass, status = sl.Status });
                    break;
                case FrameReadyEvent fr:
                    lock (sync)
                    {
                        latestFrame = fr.Frame;
                        latestStars = fr.Stars;
                        latestLock = fr.LockPosition;
                    }

                    Raise(AdvancedGuiderEventTypes.Frame, e.Timestamp, new { frameNumber = fr.Frame.FrameNumber, width = fr.Frame.Width, height = fr.Frame.Height, stars = fr.Stars.Count });
                    break;
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
        }
    }

    private void OnGuideStep(GuideStepEvent s)
    {
        double scale = s.PixelScale > 0 ? s.PixelScale : PixelScale;
        var dto = new AdvancedGuideStep
        {
            Frame = s.Frame,
            Timestamp = s.Timestamp.UtcDateTime,
            Time = s.Time,
            Dx = s.Dx,
            Dy = s.Dy,
            RaDistanceRaw = s.RaDistanceRaw,
            DecDistanceRaw = s.DecDistanceRaw,
            RaArcsec = s.RaDistanceRaw * scale,
            DecArcsec = s.DecDistanceRaw * scale,
            RaDuration = s.RaDuration,
            RaDirection = s.RaDirection?.ToString() ?? string.Empty,
            DecDuration = s.DecDuration,
            DecDirection = s.DecDirection?.ToString() ?? string.Empty,
            RaLimited = s.RaLimited,
            DecLimited = s.DecLimited,
            Snr = s.Snr,
            StarMass = s.StarMass,
            Hfd = s.Hfd,
            StarsUsed = s.StarsUsed,
            IsSettling = s.IsSettling,
            IsRecenterMove = s.IsRecenterMove,
            PrimaryEstimated = s.PrimaryEstimated,
            AvgDist = s.AvgDist,
        };
        lock (sync)
        {
            lastStep = s;
            steps.AddLast(dto);
            while (steps.Count > MaxSteps)
            {
                steps.RemoveFirst();
            }
        }

        var step = ToNinaGuideStep(s);
        try
        {
            GuideEvent?.Invoke(this, step);
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
        }

        Raise(AdvancedGuiderEventTypes.Step, s.Timestamp, dto);
        if (guider?.Statistics is { } stats)
        {
            Raise(AdvancedGuiderEventTypes.Stats, s.Timestamp, new { window = ToDto(stats.Window, stats), session = ToDto(stats.Session, stats) });
        }
    }

    private void OnAlert(AlertEvent a)
    {
        var info = GuideErrorCatalog.Get(a.Code);
        var dto = new AdvancedGuiderAlert
        {
            Timestamp = a.Timestamp.UtcDateTime,
            Code = (int)a.Code,
            CodeName = a.Code.ToString(),
            Severity = a.Severity.ToString(),
            Title = info.Title,
            Explanation = info.Explanation,
            Fix = info.Fix,
            Detail = a.Detail ?? string.Empty,
            IncidentId = a.IncidentId,
        };
        lock (sync)
        {
            alerts.AddLast(dto);
            while (alerts.Count > MaxAlerts)
            {
                alerts.RemoveFirst();
            }

            if (a.Severity != GuideErrorSeverity.Info)
            {
                lastError = dto;
            }
        }

        string text = $"Internal guider: {info.Title}{(string.IsNullOrEmpty(a.Detail) ? string.Empty : $" ({a.Detail})")}. {info.Fix}";
        switch (a.Severity)
        {
            case GuideErrorSeverity.Critical:
                Logger.Error(text);
                Notification.ShowError(text);
                break;
            case GuideErrorSeverity.Warning:
                Logger.Warning(text);
                if (a.Code is GuideErrorCode.StarLost or GuideErrorCode.CalibrationNotOrthogonal or GuideErrorCode.CalibrationFewSteps
                    or GuideErrorCode.CalibrationRateRatio or GuideErrorCode.DecFlipCorrected or GuideErrorCode.PulseLimitReached
                    or GuideErrorCode.NoStarFound or GuideErrorCode.SettleTimeout)
                {
                    Notification.ShowWarning(text);
                }

                break;
            default:
                Logger.Info(text);
                break;
        }

        if (a.Code == GuideErrorCode.NoStarFound)
        {
            autoSelectTcs?.TrySetResult(false);
        }

        Raise(AdvancedGuiderEventTypes.Alert, a.Timestamp, dto);
    }

    private void Raise(string type, DateTimeOffset ts, object? payload)
    {
        var h = AdvancedGuiderEvent;
        if (h is null)
        {
            return;
        }

        try
        {
            h(this, new AdvancedGuiderEventArgs { Type = type, Timestamp = ts.UtcDateTime, Payload = payload });
        }
        catch (Exception ex)
        {
            Logger.Debug($"InternalGuider: event subscriber failed: {ex.Message}");
        }
    }

    private void Report(string status)
    {
        IProgress<ApplicationStatus>[] sinks;
        lock (progressSinks)
        {
            if (progressSinks.Count == 0)
            {
                return;
            }

            sinks = [.. progressSinks];
        }

        foreach (var sink in sinks)
        {
            try
            {
                sink.Report(new ApplicationStatus { Source = Name, Status = status });
            }
            catch
            {
                // a failing reporter must not break the guide loop
            }
        }
    }

    private void AddProgressSink(IProgress<ApplicationStatus>? progress)
    {
        if (progress is not null)
        {
            lock (progressSinks)
            {
                progressSinks.Add(progress);
            }
        }
    }

    private void RemoveProgressSink(IProgress<ApplicationStatus>? progress)
    {
        if (progress is not null)
        {
            lock (progressSinks)
            {
                progressSinks.Remove(progress);
            }
        }
    }

    #endregion

    #region helpers

    private async Task<bool> DitherCoreAsync(double pixels, bool raOnly, IProgress<ApplicationStatus>? progress, CancellationToken ct)
    {
        var g = guider;
        if (g is null || g.State is not (GuiderState.Guiding or GuiderState.LostLock or GuiderState.Reacquiring))
        {
            Logger.Warning("InternalGuider: dither requested while not guiding");
            return false;
        }

        AddProgressSink(progress);
        try
        {
            var r = await g.DitherAsync(pixels, raOnly, SettleFromProfile(), ct).ConfigureAwait(false);
            if (r.Success)
            {
                return true;
            }

            if (r.Code == GuideErrorCode.SettleTimeout && g.State == GuiderState.Guiding)
            {
                return true;
            }

            Logger.Warning($"InternalGuider: dither failed: {r.Error}");
            return false;
        }
        finally
        {
            RemoveProgressSink(progress);
        }
    }

    private SettleParams SettleFromProfile()
    {
        var gs = profileService.ActiveProfile.GuiderSettings;
        double pixels = gs.SettlePixels > 0 ? gs.SettlePixels : DefaultSettlePixels;
        double time = gs.SettleTime > 0 ? gs.SettleTime : DefaultSettleTimeSeconds;
        double timeout = gs.SettleTimeout > 0 ? gs.SettleTimeout : DefaultSettleTimeoutSeconds;
        return new SettleParams(pixels, time, timeout);
    }

    // while connected, what the session uses: GuideSource and PulseOutput are saved at once but only apply at the next connect
    private bool SimulatorInUse => guider is not null ? simulator is not null : options.IsSimulator;

    private bool CameraSt4InUse => guider is not null ? sessionUsesCameraSt4 : options.UseCameraSt4;

    /// <summary>
    /// The guide camera as calibrations, darks and incidents name it: "Simulator", or the slot's camera (also while the
    /// guider is not connected, e.g. to clear its calibration); null when there is none.
    /// </summary>
    private string? GuideCameraName => simulator is not null || (slotCamera is null && options.IsSimulator) ? "Simulator" : GuideCameraSource.NameOf(guideCameraMediator);

    private static string StorePath => Path.Combine(StorageDirectory, "calibrations.json");

    private static CalibrationStore LoadStore()
    {
        try
        {
            if (File.Exists(StorePath))
            {
                return CalibrationStore.FromJson(File.ReadAllText(StorePath));
            }
        }
        catch (Exception ex)
        {
            Logger.Warning($"InternalGuider: could not read calibrations: {ex.Message}");
        }

        return new CalibrationStore();
    }

    private static void SaveStore(CalibrationStore store)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
        var tmp = StorePath + ".tmp";
        File.WriteAllText(tmp, store.ToJson());
        File.Move(tmp, StorePath, overwrite: true);
    }

    private CalibrationKey? CalibrationKeyNow()
    {
        var g = guider;
        string profileId = profileService.ActiveProfile.Id.ToString();
        string? cameraName = GuideCameraName;
        string mountName = simulator is not null ? "Simulator"
            : CameraSt4InUse ? "ST4"
            : profileService.ActiveProfile.TelescopeSettings.Id is { Length: > 0 } tid && tid != "No_Device" ? tid : "Mount";
        if (string.IsNullOrEmpty(cameraName))
        {
            return null;
        }

        var s = g?.Settings ?? options.ToEngineSettings();
        return new CalibrationKey(profileId, cameraName, mountName, s.Binning, s.FocalLengthMm);
    }

    private CalibrationData? LoadStoredCalibration()
    {
        try
        {
            var key = CalibrationKeyNow();
            var g = guider;
            if (key is null || g is null)
            {
                return null;
            }

            var optics = new GuideOptics(g.Settings.FocalLengthMm, g.Settings.PixelSizeUm > 0 ? g.Settings.PixelSizeUm : slotCamera?.PixelSizeUm ?? 0, g.Settings.Binning);
            var lookup = LoadStore().Lookup(key, optics);
            if (lookup.Validity == CalibrationValidity.Valid && lookup.Calibration is { } cal)
            {
                return cal;
            }

            if (lookup.Validity != CalibrationValidity.NotFound)
            {
                Logger.Info($"InternalGuider: stored calibration not used ({lookup.Validity})");
            }
        }
        catch (Exception ex)
        {
            Logger.Warning($"InternalGuider: could not restore calibration: {ex.Message}");
        }

        return null;
    }

    private void PersistCalibration(CalibrationUpdatedEvent cu)
    {
        try
        {
            if (options.GetBool("DecFlipRequired") != cu.DecFlipRequired)
            {
                options.TrySet("DecFlipRequired", cu.DecFlipRequired ? "true" : "false", out _);
            }

            if (!options.ReuseCalibration || CalibrationKeyNow() is not { } key)
            {
                return;
            }

            var store = LoadStore();
            store.Set(key, cu.Calibration);
            SaveStore(store);
        }
        catch (Exception ex)
        {
            Logger.Warning($"InternalGuider: could not save calibration: {ex.Message}");
        }
    }

    private static string DefaultPeriodicErrorStorePath => Path.Combine(StorageDirectory, "periodic-error.json");

    // the worm belongs to the mount: one curve per profile and mount, whatever guide camera (connected or not)
    private PeriodicErrorKey PeriodicErrorKeyNow()
    {
        string mountName = SimulatorInUse ? "Simulator"
            : profileService.ActiveProfile.TelescopeSettings.Id is { Length: > 0 } tid && tid != "No_Device" ? tid : "Mount";
        return new PeriodicErrorKey(profileService.ActiveProfile.Id.ToString(), mountName);
    }

    private PeriodicErrorStore LoadPeriodicErrorStore()
    {
        try
        {
            if (File.Exists(periodicErrorStorePath))
            {
                return PeriodicErrorStore.FromJson(File.ReadAllText(periodicErrorStorePath));
            }
        }
        catch (Exception ex)
        {
            Logger.Warning($"InternalGuider: could not read the periodic error store: {ex.Message}");
        }

        return new PeriodicErrorStore();
    }

    private void SavePeriodicErrorStore(PeriodicErrorStore store)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(periodicErrorStorePath)!);
        var tmp = periodicErrorStorePath + ".tmp";
        File.WriteAllText(tmp, store.ToJson());
        File.Move(tmp, periodicErrorStorePath, overwrite: true);
    }

    private void RestoreStoredPeriodicError()
    {
        try
        {
            if (guider is not { } g)
            {
                return;
            }

            var model = LoadPeriodicErrorStore().Get(PeriodicErrorKeyNow());
            g.RestorePeriodicError(model);
            if (model is not null)
            {
                Logger.Info(FormattableString.Invariant(
                    $"InternalGuider: periodic error of this mount from {model.LearnedAt:yyyy-MM-dd}: ±{model.AmplitudeArcsec:F1}″, period {model.PeriodSeconds:F1} s{(model.Teeth is { } t ? $" ({t} teeth)" : "")}"));
            }
        }
        catch (Exception ex)
        {
            Logger.Warning($"InternalGuider: could not restore the periodic error: {ex.Message}");
        }
    }

    private void PersistPeriodicError(PeriodicErrorModel model)
    {
        try
        {
            var store = LoadPeriodicErrorStore();
            store.Set(PeriodicErrorKeyNow(), model);
            SavePeriodicErrorStore(store);
        }
        catch (Exception ex)
        {
            Logger.Warning($"InternalGuider: could not save the periodic error: {ex.Message}");
        }
    }

    // the stored curve's detected period no longer holds: delete it, unless another curve was stored meanwhile
    private void DiscardPeriodicError(PeriodicErrorModel model)
    {
        try
        {
            var store = LoadPeriodicErrorStore();
            var key = PeriodicErrorKeyNow();
            if (store.Get(key) is { } stored && Math.Abs(stored.PeriodSeconds - model.PeriodSeconds) <= SameStoredPeriodTolerance * model.PeriodSeconds && store.Remove(key))
            {
                SavePeriodicErrorStore(store);
                Logger.Info(FormattableString.Invariant($"InternalGuider: stored periodic error with period {model.PeriodSeconds:F1} s discarded: it no longer holds"));
            }
        }
        catch (Exception ex)
        {
            Logger.Warning($"InternalGuider: could not discard the stored periodic error: {ex.Message}");
        }
    }

    private void ForgetPeriodicError()
    {
        guider?.ForgetPeriodicError();
        try
        {
            var store = LoadPeriodicErrorStore();
            if (store.Remove(PeriodicErrorKeyNow()))
            {
                SavePeriodicErrorStore(store);
            }

            Logger.Info("InternalGuider: periodic error of this mount forgotten");
        }
        catch (Exception ex)
        {
            Logger.Warning($"InternalGuider: could not forget the periodic error: {ex.Message}");
        }
    }

    private static string DefaultPulseModelStorePath => Path.Combine(StorageDirectory, "pulse-model.json");

    // the gears belong to the mount, like the worm; a stored model also names the calibration it holds with
    private PulseModelKey PulseModelKeyNow()
    {
        var pe = PeriodicErrorKeyNow();
        return new PulseModelKey(pe.ProfileId, pe.MountName);
    }

    private PulseModelStore LoadPulseModelStore()
    {
        try
        {
            if (File.Exists(pulseModelStorePath))
            {
                return PulseModelStore.FromJson(File.ReadAllText(pulseModelStorePath));
            }
        }
        catch (Exception ex)
        {
            Logger.Warning($"InternalGuider: could not read the pulse model store: {ex.Message}");
        }

        return new PulseModelStore();
    }

    private void RestoreStoredPulseModel()
    {
        try
        {
            if (guider is { } g)
            {
                g.RestorePulseModel(LoadPulseModelStore().Get(PulseModelKeyNow()));
            }
        }
        catch (Exception ex)
        {
            Logger.Warning($"InternalGuider: could not restore the pulse model: {ex.Message}");
        }
    }

    private void PersistPulseModel(PulseModelState state)
    {
        try
        {
            var store = LoadPulseModelStore();
            store.Set(PulseModelKeyNow(), state);
            Directory.CreateDirectory(Path.GetDirectoryName(pulseModelStorePath)!);
            var tmp = pulseModelStorePath + ".tmp";
            File.WriteAllText(tmp, store.ToJson());
            File.Move(tmp, pulseModelStorePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Logger.Warning($"InternalGuider: could not save the pulse model: {ex.Message}");
        }
    }

    private IncidentStoreOptions IncidentStoreOptionsNow() => new() { BudgetBytes = options.IncidentBudgetBytes };

    /// <summary>Where an incident was recorded (profile, equipment, image scales); called on the guide loop when an incident starts.</summary>
    private IncidentTags IncidentTagsNow()
    {
        var profile = profileService.ActiveProfile;
        string? mount = null;
        if (simulator is not null)
        {
            mount = "Simulator";
        }
        else if (CameraSt4InUse)
        {
            mount = "ST4";
        }
        else
        {
            try
            {
                mount = telescopeMediator.GetInfo() is { Connected: true } info && !string.IsNullOrEmpty(info.Name) ? info.Name : profile?.TelescopeSettings.Id;
            }
            catch
            {
                // unknown mount
            }
        }

        return new IncidentTags
        {
            ProfileId = profile?.Id.ToString(),
            ProfileName = profile?.Name,
            GuideCamera = GuideCameraName,
            Mount = mount,
            Simulator = simulator is not null,
            PixelScale = PixelScale,
            ImagingScale = coachHost.ImagingScale,
        };
    }

    private void OnIncidentSaved(object? sender, Incident summary)
    {
        Logger.Info($"InternalGuider: incident {summary.Id} saved ({summary.Kind}, {summary.FrameCount} frames, {summary.SizeBytes / 1e6:F1} MB, {summary.EndReason})");
        Raise(AdvancedGuiderEventTypes.Incident, DateTimeOffset.UtcNow, new AdvancedIncidentEvent { Action = "saved", Id = summary.Id, Summary = IncidentMapping.ToSummary(summary) });
    }

    private void OnIncidentDeleted(object? sender, string id) =>
        Raise(AdvancedGuiderEventTypes.Incident, DateTimeOffset.UtcNow, new AdvancedIncidentEvent { Action = "deleted", Id = id, Summary = null });

    private bool SimulateFault(string value, out string error)
    {
        if (simulator is not { } sim || !Connected)
        {
            error = "Connect the guider with the simulator as the guide camera first";
            return false;
        }

        if (!Enum.TryParse<SimulatorFault>(value?.Trim(), ignoreCase: true, out var fault) || !Enum.IsDefined(fault))
        {
            error = $"Unknown fault '{value}'";
            return false;
        }

        sim.InjectFault(fault);
        Logger.Info($"InternalGuider: simulator fault {fault} injected");
        error = string.Empty;
        return true;
    }

    /// <summary>The extra files of an incident download: settings, guide-log and PINS-log excerpts (location and home masked).</summary>
    private IEnumerable<(string Name, string Content)> ArchiveExtras(Incident incident)
    {
        double? latitude = null, longitude = null;
        try
        {
            var a = profileService.ActiveProfile.AstrometrySettings;
            latitude = a.Latitude;
            longitude = a.Longitude;
        }
        catch
        {
            // no profile: nothing to mask
        }

        return IncidentArchive.Extras(incident, IncidentArchive.SettingsJson(incident.SettingsJson, options.Describe()),
            Path.Combine(StorageDirectory, "Logs"), Path.Combine(CoreUtil.APPLICATIONTEMPPATH, "Logs"),
            latitude, longitude, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), TimeZoneInfo.Local);
    }

    private static T? Safe<T>(Func<T> f)
    {
        try
        {
            return f();
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            return default;
        }
    }

    private void OpenGuideLog(ICameraSource camera, Guider g, IMountState mount)
    {
        try
        {
            var dir = Path.Combine(StorageDirectory, "Logs");
            Directory.CreateDirectory(dir);
            // one log per night (noon to noon) like PHD2's per-session log; reconnects append to it
            var night = DateTime.Now.AddHours(-12).Date;
            var path = Path.Combine(dir, IncidentArchive.GuideLogFileName(night));
            guideLogWriter = new StreamWriter(path, append: true) { AutoFlush = true };
            guideLog = new GuidingLog(guideLogWriter, SystemClock.Instance, new GuidingLogOptions { AppVersion = "pins Native Guider " + DriverVersion });
            guideLog.EnableLogging();
            guideLogBridge = new GuideLogBridge(g, guideLog, mount, () => new GuideLogContext
            {
                EquipmentProfile = profileService.ActiveProfile.Name,
                CameraName = camera.Name,
                MountName = telescopeMediator.GetInfo()?.Name ?? string.Empty,
                SensorWidth = camera.SensorWidth,
                SensorHeight = camera.SensorHeight,
                PixelSizeUm = camera.PixelSizeUm,
            });
            Logger.Info($"InternalGuider: guide log {path}");
        }
        catch (Exception ex)
        {
            Logger.Warning($"InternalGuider: could not open guide log: {ex.Message}");
            CloseGuideLog();
        }
    }

    private void CloseGuideLog()
    {
        try
        {
            guideLogBridge?.Dispose();
            guideLog?.Close();
            guideLogWriter?.Dispose();
        }
        catch
        {
            // ignore
        }

        guideLogBridge = null;
        guideLog = null;
        guideLogWriter = null;
    }

    private static AdvancedGuideStar ToDto(StarInfo s) => new()
    {
        X = s.X,
        Y = s.Y,
        Snr = s.Snr,
        Mass = s.Mass,
        Hfd = s.Hfd,
        IsPrimary = s.IsPrimary,
        Used = s.Used,
        Weight = s.Weight,
        RejectReason = s.RejectReason,
    };

    /// <summary>Dec guide mode Drift in the status: the direction, the drift it follows and the safety valve; null in the other modes.</summary>
    internal static AdvancedDecDriftState? ToDecDriftDto(DecDirectionState? state, double pixelScale) => state is null ? null : new()
    {
        Direction = state.Direction.ToString(),
        DriftArcsecPerMin = state.DriftPxPerSec * 60 * pixelScale,
        SafetyValveOpen = state.ValveOpen,
    };

    /// <summary>What a learning algorithm (Predictive) knows about its axis; null for the PHD2 algorithms.</summary>
    internal static AdvancedGuideAlgorithmState? ToDto(IGuideAlgorithm? algorithm, double pixelScale)
    {
        if (algorithm is not PredictiveAlgorithm predictive)
        {
            return null;
        }

        var s = predictive.State;
        return new AdvancedGuideAlgorithmState
        {
            Name = predictive.Name,
            Phase = s.Phase.ToString(),
            Progress = s.Progress,
            FramesLearned = s.FramesLearned,
            Gain = s.Gain,
            SeeingPx = s.SeeingPx,
            SeeingArcsec = s.SeeingPx * pixelScale,
            WanderPx = s.WanderPx,
            WanderArcsec = s.WanderPx * pixelScale,
            DriftPxPerMin = s.DriftPxPerSec * 60,
            DriftArcsecPerMin = s.DriftPxPerSec * 60 * pixelScale,
            Fit = s.Fit,
            ModelChanges = s.Takeovers,
            FramesSinceModelChange = s.FramesSinceTakeover,
            PeriodicError = s.PeriodicError is not { } pe ? null : new AdvancedPeriodicErrorState
            {
                Phase = pe.Phase.ToString(),
                Progress = pe.Progress,
                Stable = pe.Stable,
                PeriodSeconds = pe.PeriodSeconds,
                WormTeeth = pe.Teeth,
                AmplitudeArcsec = pe.Amplitude is not { } a ? null : pe.AmplitudeInArcsec ? a : a * pixelScale,
                AmplitudePx = pe.Amplitude is not { } b ? null : !pe.AmplitudeInArcsec ? b : pixelScale > 0 ? b / pixelScale : null,
                Weight = pe.Weight,
            },
        };
    }

    private static AdvancedGuiderStats ToDto(GuidingStatsBlock b, GuidingStatsSnapshot snap) => new()
    {
        Frames = b.IncludedFrames,
        RmsRaArcsec = b.RmsRaArcsec,
        RmsDecArcsec = b.RmsDecArcsec,
        RmsTotalArcsec = b.RmsTotalArcsec,
        RmsRaPx = b.RmsRaPx,
        RmsDecPx = b.RmsDecPx,
        RmsTotalPx = b.RmsTotalPx,
        PeakRaArcsec = b.PeakRaArcsec,
        PeakDecArcsec = b.PeakDecArcsec,
        DriftRaArcsecPerMin = b.RaDriftArcsecPerMin,
        DriftDecArcsecPerMin = b.DecDriftArcsecPerMin,
        PolarAlignmentErrorArcmin = b.PolarAlignmentErrorArcmin,
        OscillationIndex = b.OscillationIndex,
        RaDutyPercent = b.RaDutyPercent,
        DecDutyPercent = b.DecDutyPercent,
        SnrMin = b.SnrMin,
        SnrAvg = b.SnrAvg,
        SnrLast = b.SnrLast,
        AvgStarCount = b.StarCountAvg,
        StarLostCount = snap.StarLostCount,
        ElapsedSeconds = snap.Elapsed.TotalSeconds,
    };

    /// <summary>
    /// Guide step for the NINA guide graph and the ninaAPI step history, in PHD2 conventions (px; the
    /// duration getters turn East and South negative). A PHD2 step type because both read SNR, star
    /// mass and HFD only from it.
    /// </summary>
    internal static PhdEventGuideStep ToNinaGuideStep(GuideStepEvent s) => new()
    {
        Frame = s.Frame,
        Time = s.Time,
        // the getter negates RA (NINA's PHD2 parsing); the graphs keep showing the engine's sign
        RADistanceRaw = -s.RaDistanceRaw,
        DECDistanceRaw = s.DecDistanceRaw,
        RADuration = s.RaDuration,
        RADirection = s.RaDirection?.ToString() ?? string.Empty,
        DECDuration = s.DecDuration,
        DECDirection = s.DecDirection?.ToString() ?? string.Empty,
        StarMass = s.StarMass,
        SNR = s.Snr,
        HFD = s.Hfd,
        AvgDist = s.AvgDist,
        RALimited = s.RaLimited,
        DecLimited = s.DecLimited,
        Event = "GuideStep",
        TimeStamp = s.Timestamp.ToUnixTimeMilliseconds().ToString(Inv),
        Host = Environment.MachineName,
        Inst = 1,
    };

    #endregion
}
