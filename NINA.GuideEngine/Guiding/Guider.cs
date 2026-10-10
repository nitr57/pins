// SPDX-License-Identifier: MPL-2.0

using System.Collections.Concurrent;
using System.Diagnostics;
using NINA.GuideEngine.Algorithms;
using NINA.GuideEngine.Calibration;
using NINA.GuideEngine.Coach;
using NINA.GuideEngine.Core;
using NINA.GuideEngine.Imaging;
using NINA.GuideEngine.MultiStar;
using NINA.GuideEngine.Stars;
using NINA.GuideEngine.Stats;

namespace NINA.GuideEngine.Guiding;

/// <summary>Outcome of a guide start or dither: completes when settling finished (PHD2 SettleDone).</summary>
public sealed record SettleResult(bool Success, string? Error, int TotalFrames, int DroppedFrames, GuideErrorCode Code = GuideErrorCode.None)
{
    public static SettleResult Failed(GuideErrorCode code, string? detail = null) =>
        new(false, detail ?? GuideErrorCatalog.Get(code).Title, 0, 0, code);
}

/// <summary>
/// The guider: owns the capture loop and orchestrates star tracking, calibration, guide algorithms,
/// settling, dithering, statistics and the safety protocols.
/// </summary>
/// <remarks>
/// <para>Host-agnostic: hardware comes in through <see cref="ICameraSource"/>, <see cref="IPulseOutput"/> and
/// <see cref="IMountState"/>, time through <see cref="IClock"/>.</para>
/// <para>Threading: all state is owned by the loop task. Public commands are queued and executed on the loop
/// between frames; their results are delivered through the returned tasks and <see cref="EventRaised"/>
/// (raised on the loop task — handlers must not block).</para>
/// </remarks>
public sealed partial class Guider : IAsyncDisposable
{
    private const string MountName = "Mount";

    // guiding frames in a row with every secondary star lost before they are found again (about a minute at 2 s)
    private const int SecondaryRefreshFrames = 30;

    // a Dec runaway this soon after a meridian flip inverts Dec once instead of stopping (ARCHITECTURE.md, "Calibration")
    private static readonly TimeSpan FlipRunawayWindow = TimeSpan.FromMinutes(15);

    // what SafeSnapshot returns when the mount state could not be read
    private static readonly MountSnapshot UnknownMount = new() { IsConnected = false };

    private readonly ICameraSource camera;
    private readonly IPulseOutput output;
    private readonly IMountState mount;
    private readonly IClock clock;
    private readonly ConcurrentQueue<Action> commands = new();
    private readonly object loopLock = new();
    private readonly SettleMonitor settle = new();
    private readonly FastRecenter recenter = new();
    private readonly DecFlipVerifier decFlip = new();
    private readonly DitherPlanner ditherPlanner;

    private GuiderSettings settings;

    // settings as set by the host; `settings` = baseSettings plus the coach's temporary overlay
    private GuiderSettings baseSettings;

    // periodic error: the stored curve for new RA algorithms, and the RA axis angle of the last frame (kept continuous)
    private PeriodicErrorModel? storedPeriodicError;
    private double? lastAxisHours;
    private MultiStarTracker tracker;
    private AxisCorrector corrector;
    private GuidingStatistics stats;
    private RunawayDetector runaway;
    private MountResponseMonitor response;
    private MountStateWatcher mountWatch;
    private CameraRetryPolicy cameraRetry;
    private ReacquirePolicy reacquire;

    private Task? loopTask;
    private CancellationTokenSource? loopCts;

    private volatile GuiderState state = GuiderState.Stopped;
    private CalibrationData? calibration;
    private CalibrationAdjustment? adjustment;
    private MountTransform? transform;
    private CalibrationProcess? calibrationProcess;
    private int calibrationLostFrames;
    private GuidePoint lockPosition = GuidePoint.Invalid;
    private long frameNumber;
    private DateTimeOffset guideStart;
    private double pixelScale = 1.0;
    private int frameWidth;
    private int frameHeight;

    private bool autoSelectRequested;
    private int autoSelectAttemptsLeft;

    // manual star selection waiting for the next looping frame
    private (GuidePoint Position, TaskCompletionSource<StarSelectionResult> Completion)? manualSelect;

    // guiding frames in a row with the primary found and every measured secondary star lost
    private int secondariesLostFrames;
    private PendingOperation? pending;
    private bool startGuidingRequested;
    private bool forceCalibration;
    private bool fullPause;
    private bool autoPaused;
    private bool needsRestartAfterMove;
    private bool userPaused;
    private bool lostTimeoutAlerted;
    private DateTimeOffset? flipTime;
    private bool flipInverted;
    private bool deferEvents;
    private readonly List<GuiderEvent> deferredEvents = [];
    private DecGuideMode? savedDecGuideMode;
    private int pulseFailures;

    // correction the pulses of this frame's guide step apply, reported to the algorithms once they went out
    private (double RaPx, double DecPx)? pendingCorrection;
    private double lastGoodMass;
    private double lastGoodSnr;
    private DateTimeOffset lastSlowAlert = DateTimeOffset.MinValue;
    private GuideErrorInfo? lastError;
    private GuidingStatsSnapshot? lastStats;
    private GuidePoint lastStarPosition = GuidePoint.Invalid;

    public Guider(ICameraSource camera, IPulseOutput output, IMountState mount, IClock? clock = null, GuiderSettings? settings = null,
        FramePreprocessor? preprocessor = null, int? ditherSeed = null)
    {
        this.camera = camera ?? throw new ArgumentNullException(nameof(camera));
        this.output = output ?? throw new ArgumentNullException(nameof(output));
        this.mount = mount ?? throw new ArgumentNullException(nameof(mount));
        this.clock = clock ?? SystemClock.Instance;
        this.settings = settings ?? new GuiderSettings();
        baseSettings = this.settings;
        Preprocessor = preprocessor ?? new FramePreprocessor();
        ditherPlanner = new DitherPlanner(ditherSeed);

        tracker = new MultiStarTracker();
        corrector = AxisCorrector.CreateDefault(0.2);
        stats = new GuidingStatistics();
        runaway = new RunawayDetector(this.settings.Safety);
        response = new MountResponseMonitor(this.settings.Safety);
        mountWatch = new MountStateWatcher(this.settings.Safety);
        cameraRetry = new CameraRetryPolicy(this.settings.Safety);
        reacquire = new ReacquirePolicy(this.settings.Safety);
        ApplySettings(this.settings, initial: true);
    }

    /// <summary>Raised on the loop task for every event (PHD2 event stream + extensions).</summary>
    public event EventHandler<GuiderEvent>? EventRaised;

    /// <summary>Dark library / defect map / noise reduction applied to every frame.</summary>
    public FramePreprocessor Preprocessor { get; }

    public GuiderState State => state;

    public bool IsSettling => settle.IsActive;

    public GuiderSettings Settings => settings;

    /// <summary>Current (pier/binning adjusted) calibration, null when not calibrated.</summary>
    public CalibrationData? Calibration => calibration;

    public GuidePoint LockPosition => lockPosition;

    /// <summary>Image scale ″/px of the guide frames.</summary>
    public double PixelScale => pixelScale;

    public GuideErrorInfo? LastError => lastError;

    /// <summary>Active RA guide algorithm (read-only use: parameters for logs/UI).</summary>
    public IGuideAlgorithm RaAlgorithm => corrector.RaAlgorithm;

    /// <summary>Active Dec guide algorithm (read-only use).</summary>
    public IGuideAlgorithm DecAlgorithm => corrector.DecAlgorithm;

    /// <summary>Search region in pixels actually used by star tracking.</summary>
    public int SearchRegion => tracker.SearchRegion;

    /// <summary>Latest statistics snapshot (updated every guide frame).</summary>
    public GuidingStatsSnapshot? Statistics => lastStats;

    /// <summary>
    /// When true (default) commands that need exposures start the capture loop automatically. Tests driving the
    /// guider in virtual time set this to false, queue commands, then call <see cref="StartLooping"/>.
    /// </summary>
    public bool AutoStartLoop { get; set; } = true;

    public bool IsLoopRunning
    {
        get
        {
            lock (loopLock)
            {
                return loopTask is { IsCompleted: false };
            }
        }
    }

    #region public commands

    /// <summary>Start looping exposures (PHD2 loop).</summary>
    public void StartLooping(CancellationToken externalToken = default)
    {
        if (!IsLoopRunning)
        {
            InterruptCoach(CoachInterruptReasons.Guiding);
        }

        StartLoopingCore(externalToken);
    }

    private void StartLoopingCore(CancellationToken externalToken)
    {
        Post(() =>
        {
            if (state is GuiderState.Stopped or GuiderState.Failed)
            {
                ClearRequests();
                SetState(GuiderState.Looping);
            }
        });
        EnsureLoop(externalToken, force: true);
    }

    /// <summary>Stop capturing (PHD2 stop_capture). Guiding stops too.</summary>
    public Task StopCaptureAsync()
    {
        InterruptCoach(CoachInterruptReasons.Stopped);
        return StopCaptureCoreAsync();
    }

    private async Task StopCaptureCoreAsync()
    {
        Task? t;
        lock (loopLock)
        {
            t = loopTask;
            loopCts?.Cancel();
        }

        if (t is not null)
        {
            try
            {
                await t.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        try
        {
            await camera.AbortAsync().ConfigureAwait(false);
        }
        catch
        {
            // best effort
        }
    }

    /// <summary>Select the best star on the next frame (PHD2 find_star).</summary>
    public void AutoSelectStar()
    {
        InterruptCoach(CoachInterruptReasons.Guiding);
        Post(() =>
        {
            if (state.IsGuidingActive() || state == GuiderState.Calibrating || coachHook is not null)
            {
                // the guide star cannot be changed while guiding
                return;
            }

            autoSelectRequested = true;
            autoSelectAttemptsLeft = Math.Max(1, settings.AutoSelectAttempts);
            if (state is GuiderState.Stopped or GuiderState.Failed)
            {
                SetState(GuiderState.Looping);
            }
        });
        EnsureLoop();
    }

    /// <summary>
    /// Select the star nearest <paramref name="position"/> (frame px, within the search region) as the guide star on the
    /// next frame, like clicking a star in PHD2; in multi-star mode its secondary stars are found around it. Only while
    /// looping exposures without guiding: completes with <see cref="StarSelectionError.Busy"/> while guiding, calibrating,
    /// starting to guide or running the Coach, and with <see cref="StarSelectionError.NotLooping"/> when not looping.
    /// </summary>
    public Task<StarSelectionResult> SelectStarAsync(GuidePoint position, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<StarSelectionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() =>
        {
            if (state.IsGuidingActive() || state == GuiderState.Calibrating || coachHook is not null || startGuidingRequested)
            {
                tcs.TrySetResult(StarSelectionResult.Failed(StarSelectionError.Busy));
                return;
            }

            if (state is not (GuiderState.Looping or GuiderState.Selected))
            {
                tcs.TrySetResult(StarSelectionResult.Failed(StarSelectionError.NotLooping));
                return;
            }

            CancelManualSelect();
            manualSelect = (position, tcs);
        });

        // no loop to run the command: answer now
        if (!IsLoopRunning)
        {
            DrainCommands();
        }

        return ct.CanBeCanceled ? tcs.Task.WaitAsync(ct) : tcs.Task;
    }

    /// <summary>
    /// Start guiding (PHD2 guide): selects a star if needed, calibrates if needed (or forced), starts guiding
    /// and completes once settled.
    /// </summary>
    public Task<SettleResult> StartGuidingAsync(SettleParams settleParams, bool forceCalibration = false, CancellationToken ct = default)
    {
        InterruptCoach(CoachInterruptReasons.Guiding);
        return StartGuidingCoreAsync(settleParams, forceCalibration, ct, default, forceLoop: false);
    }

    private Task<SettleResult> StartGuidingCoreAsync(SettleParams settleParams, bool forceCalibration, CancellationToken ct, CancellationToken loopToken,
        bool forceLoop)
    {
        var op = new PendingOperation(PendingKind.Guide, settleParams, ct);
        Post(() =>
        {
            if (state.IsGuidingActive() && !forceCalibration && state != GuiderState.Paused)
            {
                // already guiding: PHD2 just settles again
                ReplacePending(op);
                BeginSettle(settleParams);
                return;
            }

            ReplacePending(op);
            if (state.IsGuidingActive() || state == GuiderState.Calibrating)
            {
                // restart (forced recalibration, or resume from pause/lost star): stop the current run first
                StopRunCore();
            }

            this.forceCalibration = forceCalibration;
            startGuidingRequested = true;
            if (state is GuiderState.Stopped or GuiderState.Failed or GuiderState.Paused or GuiderState.Selected)
            {
                ResetGuidingRuntime();
                SetState(GuiderState.Looping);
            }

            if (!tracker.IsLocked && state == GuiderState.Looping)
            {
                autoSelectRequested = true;
                autoSelectAttemptsLeft = Math.Max(1, settings.AutoSelectAttempts);
            }
        });
        EnsureLoop(loopToken, force: forceLoop);
        return op.Task;
    }

    /// <summary>Stop guiding but keep looping with the star selected.</summary>
    public void StopGuiding()
    {
        InterruptCoach(CoachInterruptReasons.Guiding);
        Post(() => StopGuidingCore(GuiderState.Selected));
    }

    /// <summary>Dither by up to <paramref name="amountPx"/> pixels (PHD2 dither); completes when settled.</summary>
    public Task<SettleResult> DitherAsync(double amountPx, bool raOnly, SettleParams settleParams, CancellationToken ct = default)
    {
        InterruptCoach(CoachInterruptReasons.Dither);
        var op = new PendingOperation(PendingKind.Dither, settleParams, ct);
        Post(() => DitherCore(amountPx, raOnly, op));
        if (!IsLoopRunning && AutoStartLoop)
        {
            op.Complete(SettleResult.Failed(GuideErrorCode.DitherFailed, "not guiding"));
        }

        return op.Task;
    }

    /// <summary>Pause guiding (PHD2 set_paused). <paramref name="full"/> also stops exposures.</summary>
    public void Pause(bool full = false)
    {
        InterruptCoach(CoachInterruptReasons.Guiding);
        PauseCore(full);
    }

    private void PauseCore(bool full) => Post(() =>
    {
        if (!state.IsGuidingActive())
        {
            return;
        }

        fullPause = full;
        userPaused = true;
        if (state != GuiderState.Paused)
        {
            corrector.GuidingPaused();
            pulseModel.Interrupt(mountMoved: true);
            SetState(GuiderState.Paused);
            Emit(new PausedEvent(clock.UtcNow, full ? "full" : "guiding"));
        }
    });

    /// <summary>Resume after <see cref="Pause"/> or after an auto-pause that requires an explicit resume.</summary>
    public void Resume()
    {
        InterruptCoach(CoachInterruptReasons.Guiding);
        ResumeCommand();
    }

    private void ResumeCommand() => Post(() =>
    {
        fullPause = false;
        userPaused = false;
        if (needsRestartAfterMove)
        {
            // the mount moved far while paused: select a new star and lock on it without recalibrating
            needsRestartAfterMove = false;
            mountWatch.ClearExplicitResume();
            autoPaused = false;
            tracker.InvalidateCurrentPosition(fullReset: true);
            lockPosition = GuidePoint.Invalid;
            startGuidingRequested = true;
            autoSelectRequested = true;
            autoSelectAttemptsLeft = Math.Max(1, settings.AutoSelectAttempts);
            SetState(GuiderState.Looping);
            return;
        }

        if (state == GuiderState.Paused && !autoPaused)
        {
            ResumeCore();
        }
    });

    /// <summary>Clears pending start/select requests (stale requests must never start calibration later).</summary>
    private void ClearRequests()
    {
        startGuidingRequested = false;
        autoSelectRequested = false;
        forceCalibration = false;
        CancelManualSelect();
    }

    private void CancelManualSelect()
    {
        manualSelect?.Completion.TrySetResult(StarSelectionResult.Failed(StarSelectionError.Cancelled));
        manualSelect = null;
    }

    /// <summary>Stops the current guiding/calibration run (without touching the pending operation) so it can be restarted.</summary>
    private void StopRunCore()
    {
        corrector.GuidingStopped();
        pulseModel.Interrupt(mountMoved: true);
        EmitPeriodicErrorModel(clock.UtcNow);
        settle.Cancel();
        recenter.Cancel();
        calibrationProcess = null;
        fullPause = false;
        userPaused = false;
        Emit(new GuidingStoppedEvent(clock.UtcNow));
        SetState(tracker.IsLocked ? GuiderState.Selected : GuiderState.Looping);
    }

    /// <summary>Forget the calibration (PHD2 clear_calibration).</summary>
    public void ClearCalibration()
    {
        InterruptCoach(CoachInterruptReasons.Guiding);
        ClearCalibrationCommand();
    }

    private void ClearCalibrationCommand() => Post(() =>
    {
        if (state.IsGuidingActive() || state == GuiderState.Calibrating)
        {
            StopGuidingCore(GuiderState.Selected);
        }

        calibration = null;
        adjustment = null;
        transform = null;
        tracker.CameraToMount = null;
    });

    /// <summary>Use a stored calibration (e.g. from <see cref="CalibrationStore"/>); it is adjusted for the current pointing when guiding starts.</summary>
    public void LoadCalibration(CalibrationData data)
    {
        Post(() => LoadCalibrationCore(data));

        // no loop to apply it: apply now so hosts/UIs see the stored calibration before guiding starts
        if (!IsLoopRunning)
        {
            DrainCommands();
        }
    }

    private void LoadCalibrationCore(CalibrationData data)
    {
        if (state.IsGuidingActive() || state == GuiderState.Calibrating)
        {
            // applied on the next start; never swap the calibration under a running guide loop
            return;
        }

        calibration = data;
        adjustment = null;
        transform = null;
    }

    /// <summary>Set the lock position explicitly (PHD2 set_lock_position).</summary>
    public void SetLockPosition(GuidePoint position)
    {
        InterruptCoach(CoachInterruptReasons.Guiding);
        SetLockPositionCommand(position);
    }

    private void SetLockPositionCommand(GuidePoint position) => Post(() =>
    {
        if (frameWidth > 0 && !tracker.IsValidLockPosition(position))
        {
            Alert(GuideErrorCode.LockPositionNearEdge);
            return;
        }

        // the offsets jump with the lock position; the open-loop Dec position continues in a new segment
        decDrift.Break();
        pulseModel.Interrupt(mountMoved: false);
        SetLockPositionCore(position);
    });

    /// <summary>
    /// The periodic error learned in an earlier session (stored by the host from <see cref="PeriodicErrorModelEvent"/>): the
    /// RA axis's Predictive algorithm starts from it, now and whenever it is created anew. Null forgets it.
    /// </summary>
    public void RestorePeriodicError(PeriodicErrorModel? model)
    {
        Post(() =>
        {
            if (model is not null && PeriodicErrorEstimator.Invalid(model) is { } why)
            {
                // a corrupted model must never drive pulses: the host deletes its stored copy
                var now = clock.UtcNow;
                Emit(new AlgorithmNoteEvent(now, GuideAxis.Ra, PredictiveNoteKind.PeriodicError, $"stored periodic error model discarded: {why}"));
                Emit(new PeriodicErrorModelDiscardedEvent(now, model));
                model = null;
            }

            storedPeriodicError = model;
            if (model is not null && corrector.RaAlgorithm is PredictiveAlgorithm predictive)
            {
                predictive.RestorePeriodicError(model);
            }
        });
        if (!IsLoopRunning)
        {
            DrainCommands();
        }
    }

    /// <summary>Forgets the learned periodic error (after mechanical work on the mount); the host deletes its stored copy.</summary>
    public void ForgetPeriodicError()
    {
        Post(() =>
        {
            storedPeriodicError = null;
            (corrector.RaAlgorithm as PredictiveAlgorithm)?.ForgetPeriodicError();
        });
        if (!IsLoopRunning)
        {
            DrainCommands();
        }
    }

    /// <summary>Apply new settings (takes effect between frames).</summary>
    public void UpdateSettings(GuiderSettings newSettings)
    {
        ArgumentNullException.ThrowIfNull(newSettings);
        Post(() =>
        {
            baseSettings = newSettings;
            ApplySettings(ComposeSettings());
        });
        if (!IsLoopRunning)
        {
            DrainCommands();
        }
    }

    public async ValueTask DisposeAsync() => await StopCaptureAsync().ConfigureAwait(false);

    #endregion

    #region loop

    /// <summary>Queue a command; commands issued while the loop is not running are applied when it starts.</summary>
    private void Post(Action a) => commands.Enqueue(a);

    private void EnsureLoop(CancellationToken externalToken = default, bool force = false)
    {
        if (!force && !AutoStartLoop)
        {
            return;
        }

        lock (loopLock)
        {
            if (cameraLeased)
            {
                // the coach is using the camera outside the loop: start once it releases the camera
                deferredLoopStart = true;
                deferredLoopToken = externalToken.CanBeCanceled ? externalToken : deferredLoopToken;
                return;
            }

            if (loopTask is { IsCompleted: false })
            {
                if (force)
                {
                    // the loop may be finishing: make sure a forced (coach) start is not lost
                    forcedRestart = true;
                    forcedRestartToken = externalToken;
                }

                return;
            }

            loopCts?.Dispose();
            loopCts = externalToken.CanBeCanceled ? CancellationTokenSource.CreateLinkedTokenSource(externalToken) : new CancellationTokenSource();
            var ct = loopCts.Token;
            var task = Task.Run(() => LoopAsync(ct), CancellationToken.None);
            loopTask = task;

            // commands posted while the loop was finishing would otherwise wait for the next start
            task.ContinueWith(_ =>
            {
                bool restart;
                CancellationToken token;
                lock (loopLock)
                {
                    restart = forcedRestart;
                    token = forcedRestartToken;
                    forcedRestart = false;
                    forcedRestartToken = default;
                }

                if (!commands.IsEmpty && (AutoStartLoop || restart))
                {
                    EnsureLoop(token, force: restart);
                }
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    /// <summary>Completes when the capture loop has ended (for hosts and tests).</summary>
    public Task WaitForLoopAsync()
    {
        lock (loopLock)
        {
            return loopTask ?? Task.CompletedTask;
        }
    }

    private void DrainCommands()
    {
        while (commands.TryDequeue(out var a))
        {
            a();
        }

        DrainPublishedEvents();
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                DrainCommands();
                lock (loopLock)
                {
                    // commands of a forced start were drained by this loop: no restart needed when it ends
                    forcedRestart = false;
                }

                if (!state.IsCapturing())
                {
                    break;
                }

                if (fullPause && state == GuiderState.Paused)
                {
                    await clock.Delay(TimeSpan.FromMilliseconds(500), ct).ConfigureAwait(false);
                    continue;
                }

                var snapshot = SafeSnapshot();
                HandleMountState(snapshot);
                if (!state.IsCapturing())
                {
                    break;
                }

                GuideFrame raw;
                try
                {
                    raw = await camera.CaptureAsync(new CaptureRequest(settings.ExposureMs, settings.Binning, default, settings.Gain, settings.Offset), ct)
                        .ConfigureAwait(false);
                    cameraRetry.Success();
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (!await HandleCameraFailureAsync(ex, ct).ConfigureAwait(false))
                    {
                        break;
                    }

                    continue;
                }

                var sw = Stopwatch.StartNew();
                var frame = Preprocessor.Process(raw, out framePreprocess);
                frame.FrameNumber = ++frameNumber;
                frameWidth = frame.Width;
                frameHeight = frame.Height;

                DrainCommands();
                await ProcessFrameAsync(frame, snapshot, sw, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Fail(GuideErrorCode.Internal, ex.Message);
        }
        finally
        {
            DrainCommands();
            if (state.IsGuidingActive() || state == GuiderState.Calibrating)
            {
                Emit(new GuidingStoppedEvent(clock.UtcNow));
            }

            if (state.IsCapturing())
            {
                SetState(GuiderState.Stopped);
            }

            settle.Cancel();
            recenter.Cancel();
            calibrationProcess = null;
            ClearRequests();
            CompletePending(SettleResult.Failed(state == GuiderState.Failed ? lastError?.Code ?? GuideErrorCode.Internal : GuideErrorCode.None, "guiding stopped"));
            OnLoopEndedForCoach(ct.IsCancellationRequested);
            FlushEvents();
            Emit(new LoopingExposuresStoppedEvent(clock.UtcNow));
            DrainPublishedEvents();
        }
    }

    private MountSnapshot SafeSnapshot()
    {
        try
        {
            return mount.GetSnapshot();
        }
        catch
        {
            return UnknownMount;
        }
    }

    private async Task<bool> HandleCameraFailureAsync(Exception ex, CancellationToken ct)
    {
        switch (cameraRetry.Failure())
        {
            case CameraFailureAction.Retry:
                Alert(GuideErrorCode.CameraCaptureFailed, ex.Message);
                return true;
            case CameraFailureAction.Reconnect:
                Alert(GuideErrorCode.CameraReconnecting, ex.Message);
                try
                {
                    await camera.ReconnectAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return false;
                }
                catch (Exception rex)
                {
                    Alert(GuideErrorCode.CameraCaptureFailed, rex.Message);
                }

                return true;
            default:
                Fail(GuideErrorCode.CameraFailed, ex.Message);
                return false;
        }
    }

    private async Task ProcessFrameAsync(GuideFrame frame, MountSnapshot snapshot, Stopwatch sw, CancellationToken ct)
    {
        // events are delivered after the correction went out so subscribers (logs, UIs) never delay pulses; the flight
        // recorder also records the frame after that
        deferEvents = true;
        BeginIncidentFrame();
        bool processed = false;
        try
        {
            await ProcessFrameCoreAsync(frame, snapshot, sw, ct).ConfigureAwait(false);
            processed = true;
        }
        finally
        {
            EndIncidentFrame(frame, snapshot, processed);
            DrainPublishedEvents();
            deferEvents = false;
            FlushEvents();
        }
    }

    private async Task ProcessFrameCoreAsync(GuideFrame frame, MountSnapshot snapshot, Stopwatch sw, CancellationToken ct)
    {
        var now = clock.UtcNow;
        MultiStarFrameResult? result = null;
        IReadOnlyList<PulseCommand> pulses = [];
        pendingCorrection = null;
        pulseModelFrame = false;

        switch (state)
        {
            case GuiderState.Looping:
            case GuiderState.Selected:
                result = LoopingFrame(frame, snapshot, now, out pulses);
                break;
            case GuiderState.Calibrating:
                result = CalibratingFrame(frame, snapshot, now, out pulses);
                break;
            case GuiderState.Guiding:
            case GuiderState.LostLock:
            case GuiderState.Reacquiring:
            case GuiderState.Paused:
                result = GuidingFrame(frame, snapshot, now, sw, out pulses);
                break;
        }

        frameResult = result;

        double processingMs = sw.Elapsed.TotalMilliseconds;
        if (processingMs > settings.Safety.SlowProcessingMs && (now - lastSlowAlert).TotalMinutes >= 5)
        {
            lastSlowAlert = now;
            Alert(GuideErrorCode.FrameProcessingSlow, $"{processingMs:F0} ms");
        }

        if (settings.FrameEventInterval > 0 && frame.FrameNumber % settings.FrameEventInterval == 0)
        {
            Emit(new FrameReadyEvent(now, frame, result is null ? [] : ToStarInfos(result), lockPosition));
        }

        if (pulses.Count > 0 && state.IsCapturing())
        {
            // the snapshot was taken before the exposure: never pulse a mount that started slewing/parked meanwhile
            if (MountStateWatcher.Evaluate(SafeSnapshot()) != MountPauseReason.None)
            {
                coachPulsesDropped = IsCoachMeasuring;
                pulseModel.Interrupt(mountMoved: true);
                return;
            }

            framePulses = pulses;
            bool sent = await IssuePulsesAsync(pulses, ct).ConfigureAwait(false);
            if (sent && pendingCorrection is { } applied)
            {
                corrector.CorrectionsApplied(applied.RaPx, applied.DecPx);
                decDrift.CorrectionApplied(applied.DecPx);
            }

            if (sent)
            {
                PulseModelPulsesSent(pulses);
            }
            else
            {
                pulseModel.Interrupt(mountMoved: false);
            }
        }
    }

    #endregion

    #region looping / selection / calibration

    private MultiStarFrameResult? LoopingFrame(GuideFrame frame, MountSnapshot snapshot, DateTimeOffset now, out IReadOnlyList<PulseCommand> pulses)
    {
        pulses = [];
        Emit(new LoopingExposuresEvent(now, frame.FrameNumber));

        if (manualSelect is { } request)
        {
            // then tracked on this frame too: the overlay shows the new stars at once, a failed choice keeps the old star
            manualSelect = null;
            request.Completion.TrySetResult(SelectStarCore(frame, request.Position, now));
        }

        if (autoSelectRequested)
        {
            int edge = calibration is null ? CalibrationDistancePx(snapshot) : 0;
            var sel = tracker.AutoSelect(frame, default, edge);
            if (sel.Success)
            {
                autoSelectRequested = false;
                lastGoodMass = sel.Primary.Mass;
                lastGoodSnr = sel.Primary.Snr;
                Emit(new StarSelectedEvent(now, sel.Primary.Position.X, sel.Primary.Position.Y));
                SetLockPositionCore(sel.LockPosition);
                SetState(GuiderState.Selected);
            }
            else if (--autoSelectAttemptsLeft <= 0)
            {
                autoSelectRequested = false;
                Alert(GuideErrorCode.NoStarFound);
                if (startGuidingRequested)
                {
                    startGuidingRequested = false;
                    CompletePending(SettleResult.Failed(GuideErrorCode.NoStarFound));
                }
            }

            return null;
        }

        if (!tracker.IsLocked && state == GuiderState.Looping)
        {
            return null;
        }

        var r = tracker.ProcessFrame(frame, lockPosition, TrackerState.Looping, now);
        if (r.StarFound)
        {
            lastStarPosition = r.Primary.Position;
            lastGoodMass = r.Primary.Mass;
            lastGoodSnr = r.Primary.Snr;
        }
        else if (startGuidingRequested)
        {
            // lost the selected star before guiding started: select again
            autoSelectRequested = true;
            autoSelectAttemptsLeft = Math.Max(1, settings.AutoSelectAttempts);
            return r;
        }

        if (startGuidingRequested && r.StarFound)
        {
            startGuidingRequested = false;

            // a star kept from before (e.g. after a failure or a slew) may not be the one its secondaries belong to
            RefreshSecondaries(frame, force: false);
            BeginGuiding(r.Primary.Position, snapshot, now, out pulses);
        }

        return r;
    }

    private StarSelectionResult SelectStarCore(GuideFrame frame, GuidePoint position, DateTimeOffset now)
    {
        var sel = tracker.SelectStar(frame, position);
        if (!sel.Success)
        {
            return sel;
        }

        // the explicit choice replaces a pending automatic selection
        autoSelectRequested = false;
        secondariesLostFrames = 0;
        lastGoodMass = sel.Primary.Mass;
        lastGoodSnr = sel.Primary.Snr;
        Emit(new StarSelectedEvent(now, sel.Primary.Position.X, sel.Primary.Position.Y));
        SetLockPositionCore(sel.Primary.Position);
        SetState(GuiderState.Selected);
        return sel;
    }

    /// <summary>
    /// Extension: finds the secondary stars again around the primary (see <see cref="MultiStarTracker.RefreshSecondaryStars"/>);
    /// alerts when it replaced secondaries that were lost.
    /// </summary>
    private void RefreshSecondaries(GuideFrame frame, bool force)
    {
        secondariesLostFrames = 0;
        var r = tracker.RefreshSecondaryStars(frame, force);
        if (r.Replaced && r.Before > 0)
        {
            Alert(GuideErrorCode.SecondaryStarsRefreshed, $"{r.Found} secondary stars (before: {r.Before})");
        }
    }

    /// <summary>
    /// Extension: a secondary star that is lost is only searched at its original offset from the primary, so when the
    /// primary changes to a neighbouring star (clouds, a reacquisition) all secondaries stay lost and multi-star guiding
    /// silently degrades to the primary alone. Finds them again after <see cref="SecondaryRefreshFrames"/> guiding frames
    /// in a row with the primary found and every measured secondary lost.
    /// </summary>
    private void CheckSecondaryStars(GuideFrame frame, MultiStarFrameResult r)
    {
        if (r.Outcome != TrackerOutcome.Found)
        {
            // a lost or estimated primary says nothing about the secondaries
            return;
        }

        int measured = 0;
        int lost = 0;
        foreach (var s in r.Stars)
        {
            if (s.Index == 0)
            {
                continue;
            }

            switch (s.Status)
            {
                case TrackedStarStatus.Lost:
                    lost++;
                    measured++;
                    break;
                case TrackedStarStatus.Used or TrackedStarStatus.Miss or TrackedStarStatus.ReferenceReset or TrackedStarStatus.Resnapped:
                    measured++;
                    break;
            }
        }

        if (measured == 0)
        {
            // not measured this frame (settling, stabilising, paused, single star)
            return;
        }

        if (lost < measured)
        {
            secondariesLostFrames = 0;
            return;
        }

        if (++secondariesLostFrames >= SecondaryRefreshFrames)
        {
            RefreshSecondaries(frame, force: true);
        }
    }

    private void BeginGuiding(GuidePoint starPosition, MountSnapshot snapshot, DateTimeOffset now, out IReadOnlyList<PulseCommand> pulses)
    {
        pulses = [];
        if (!forceCalibration && calibration is not null)
        {
            var adj = AdjustCalibration(calibration, snapshot);
            if (adj.IsValid)
            {
                EnterGuiding(starPosition, adj, now);
                return;
            }

            Alert(GuideErrorCode.CalibrationInvalidated);
        }

        forceCalibration = false;
        StartCalibration(starPosition, snapshot, now, out pulses);
    }

    private void StartCalibration(GuidePoint starPosition, MountSnapshot snapshot, DateTimeOffset now, out IReadOnlyList<PulseCommand> pulses)
    {
        pulseModel.Interrupt(mountMoved: true);
        var optics = Optics();
        var cal = settings.Calibration with
        {
            MaxRaDurationMs = settings.MaxRaDurationMs,
            MaxDecDurationMs = settings.MaxDecDurationMs,
            MaxMovePixels = tracker.SearchRegion,
            DecGuideMode = settings.DecGuideMode,
            DecCompensationEnabled = settings.DecCompensation,
        };
        if (settings.AutoCalibrationStep)
        {
            var rec = CalibrationStepCalculator.Recommend(optics, snapshot);
            cal = cal with { StepMs = rec.StepMs, DistancePx = rec.DistancePx };
        }

        calibrationProcess = new CalibrationProcess(cal, new CalibrationContext { Optics = optics, PreviousCalibration = calibration, Clock = clock });
        calibrationLostFrames = 0;
        SetState(GuiderState.Calibrating);
        Emit(new StartCalibrationEvent(now, MountName));
        var u = calibrationProcess.Begin(starPosition, snapshot);
        pulses = HandleCalibrationUpdate(u, starPosition, snapshot, now);
    }

    private MultiStarFrameResult CalibratingFrame(GuideFrame frame, MountSnapshot snapshot, DateTimeOffset now, out IReadOnlyList<PulseCommand> pulses)
    {
        var r = tracker.ProcessFrame(frame, GuidePoint.Invalid, TrackerState.Looping, now);
        var pos = r.StarFound ? r.Primary.Position : GuidePoint.Invalid;
        if (!r.StarFound)
        {
            Emit(new StarLostEvent(now, frame.FrameNumber, 0, r.Primary.Mass, r.Primary.Snr, r.Primary.Hfd, 0, (int)r.ErrorCode, r.Status));
            if (++calibrationLostFrames * Math.Max(settings.ExposureMs, 1) / 1000.0 > settings.Safety.ReacquireTimeoutSec)
            {
                FailCalibration(GuideErrorCode.CalibrationFailedStarLost, "star lost during calibration", now);
                pulses = [];
                return r;
            }
        }
        else
        {
            calibrationLostFrames = 0;
            lastStarPosition = pos;
        }

        var u = calibrationProcess!.Update(pos, snapshot);
        pulses = HandleCalibrationUpdate(u, pos, snapshot, now);
        return r;
    }

    private IReadOnlyList<PulseCommand> HandleCalibrationUpdate(CalibrationUpdate u, GuidePoint starPosition, MountSnapshot snapshot, DateTimeOffset now)
    {
        if (u.Status is { } st)
        {
            Emit(new CalibratingEvent(now)
            {
                Mount = MountName,
                Direction = st.Direction,
                Distance = st.Distance,
                Dx = st.Dx,
                Dy = st.Dy,
                Position = st.Position,
                Step = st.StepNumber,
                State = st.Message ?? string.Empty,
                Progress = u.EstimatedTotalSteps > 0 ? Math.Min(1.0, (double)u.StepsIssued / u.EstimatedTotalSteps) : 0,
            });
        }

        foreach (var a in u.Advisories.Where(a => a.ShowToUser))
        {
            Alert(GuideErrorCode.CalibrationReturnIncomplete, a.Message);
        }

        if (u.IsFailed)
        {
            var code = u.ErrorCode switch
            {
                CalibrationErrorCode.RaStarDidNotMove => GuideErrorCode.CalibrationFailedRaNoMove,
                CalibrationErrorCode.DecStarDidNotMove => GuideErrorCode.CalibrationFailedDecNoMove,
                CalibrationErrorCode.BacklashClearingFailed => GuideErrorCode.CalibrationFailedBacklash,
                CalibrationErrorCode.InvalidLockPosition => GuideErrorCode.LockPositionNearEdge,
                _ => GuideErrorCode.CalibrationFailedRaNoMove,
            };
            FailCalibration(code, u.FailureReason, now);
            return [];
        }

        if (u.IsComplete && u.Result is { } result)
        {
            calibration = result;
            calibrationProcess = null;
            if (u.Sanity is { ShouldAlert: true, ReportedIssue: { } issue })
            {
                Alert(issue.Type switch
                {
                    CalibrationIssueType.Steps => GuideErrorCode.CalibrationFewSteps,
                    CalibrationIssueType.Angle => GuideErrorCode.CalibrationNotOrthogonal,
                    CalibrationIssueType.Rates => GuideErrorCode.CalibrationRateRatio,
                    _ => GuideErrorCode.CalibrationDecRateChanged,
                }, issue.Message);
            }

            Emit(new CalibrationCompleteEvent(now, MountName, result));
            Emit(new CalibrationUpdatedEvent(now, result, settings.DecFlipRequired));
            var adj = AdjustCalibration(result, snapshot);
            EnterGuiding(starPosition.IsValid ? starPosition : lastStarPosition, adj, now);
            return [];
        }

        return u.Pulses.Where(p => p.DurationMs > 0).ToList();
    }

    private void FailCalibration(GuideErrorCode code, string? reason, DateTimeOffset now)
    {
        calibrationProcess = null;
        ClearRequests();
        Alert(code, reason);
        Emit(new CalibrationFailedEvent(now, MountName, reason ?? GuideErrorCatalog.Get(code).Title, code));
        SetState(tracker.IsLocked ? GuiderState.Selected : GuiderState.Looping);
        CompletePending(SettleResult.Failed(code, reason));
    }

    private CalibrationAdjustment AdjustCalibration(CalibrationData cal, MountSnapshot snapshot)
    {
        var adj = CalibrationAdjuster.AdjustForScopePointing(cal, new ScopePointing
        {
            Mount = snapshot,
            Binning = settings.Binning,
            PixelSizeUm = settings.PixelSizeUm > 0 ? settings.PixelSizeUm : camera.PixelSizeUm,
            DecFlipRequired = settings.DecFlipRequired,
            DecCompensationEnabled = settings.DecCompensation,
        });

        foreach (var a in adj.Alerts.Where(a => a.ShowToUser))
        {
            var code = a.Type switch
            {
                CalibrationAlertType.GuideSpeedChanged => GuideErrorCode.CalibrationGuideRateChanged,
                CalibrationAlertType.NoPierSideInformation => GuideErrorCode.PierSideUnknown,
                CalibrationAlertType.PixelSizeChanged => GuideErrorCode.CalibrationInvalidated,
                CalibrationAlertType.Flipped => GuideErrorCode.CalibrationFlipped,
                CalibrationAlertType.CalibrationTooFarFromEquator => GuideErrorCode.CalibrationHighDeclination,
                _ => GuideErrorCode.CalibrationInvalidated,
            };
            Alert(code, a.Message);
        }

        if (adj.Flipped)
        {
            Emit(new CalibrationDataFlippedEvent(clock.UtcNow, MountName));
        }

        if (adj.IsValid && adj.CalibrationChanged)
        {
            calibration = adj.Calibration;
            Emit(new CalibrationUpdatedEvent(clock.UtcNow, adj.Calibration, settings.DecFlipRequired));
        }

        return adj;
    }

    private void UseCalibration(CalibrationAdjustment adj)
    {
        adjustment = adj;
        calibration = adj.Calibration;
        transform = new MountTransform(adj.Calibration, adj.EffectiveXRate);
        var t = transform;
        tracker.CameraToMount = p => t.CameraToMount(p);
        stats.DeclinationDeg = adj.Calibration.Declination is { } d ? d * 180 / Math.PI : null;
    }

    private void EnterGuiding(GuidePoint starPosition, CalibrationAdjustment adj, DateTimeOffset now)
    {
        ClearRequests();
        UseCalibration(adj);
        ResetGuidingRuntime();
        corrector.GuidingStarted();
        corrector.SetGuidingEnabled(true, now);
        ResetDecDirection("guiding started");
        stats.Start(now);
        guideStart = now;
        ResetLiveHints();
        ditherPlanner.ResetSpiral();
        SetLockPositionCore(starPosition);
        SetState(GuiderState.Guiding);
        StartPulseModel();
        Emit(new StartGuidingEvent(now));
        EmitPulseModelInUse(now);

        if (adj.Flipped && settings.VerifyDecAfterFlip && settings.DecGuideMode != DecGuideMode.Off)
        {
            decFlip.Start();
            flipTime = now;
            flipInverted = false;
        }

        if (pending is { Kind: PendingKind.Guide } p)
        {
            BeginSettle(p.Settle);
        }
    }

    private void ResetGuidingRuntime()
    {
        secondariesLostFrames = 0;
        runaway.Reset();
        response.Reset();
        reacquire.Reset();
        recenter.Cancel();
        decFlip.Reset();
        pulseFailures = 0;
        autoPaused = false;
        userPaused = false;
        needsRestartAfterMove = false;
        lostTimeoutAlerted = false;
        mountWatch.ClearExplicitResume();
        tracker.Distances.NeedReset = true;
    }

    #endregion

    #region guiding

    private MultiStarFrameResult GuidingFrame(GuideFrame frame, MountSnapshot snapshot, DateTimeOffset now, Stopwatch sw, out IReadOnlyList<PulseCommand> pulses)
    {
        pulses = [];
        bool paused = state == GuiderState.Paused || autoPaused;
        bool raOnly = corrector.DecGuideMode == DecGuideMode.Off;
        bool measuring = IsCoachMeasuring;

        // coach measurements track the primary alone here; their multi-star combined position comes from the coach meter
        // (PHD2's refinement gates are made for guiding near the lock position, not for unguided drift and test pulses)
        var ts = new TrackerState(IsGuiding: true, IsSettling: settle.IsActive, IsPaused: paused, RaOnly: raOnly, GuidingEnabled: !paused && !measuring);
        var r = tracker.ProcessFrame(frame, lockPosition, ts, now);
        CheckSecondaryStars(frame, r);
        if (transform is not { } t)
        {
            Fail(GuideErrorCode.NotCalibrated, "no calibration while guiding");
            return r;
        }

        GuideCorrection? corr = null;
        bool recenterMove = false;
        pulseModelFrame = true;
        pulseModelRates = (t.XRate, t.YRate);
        var rates = PulseRates(t);

        if (r.StarFound && r.HasOffset)
        {
            if (reacquire.IsActive)
            {
                reacquire.Reset();
                Alert(GuideErrorCode.StarReacquired);

                // whatever moved the star away may have moved the mount
                decDrift.Break();
            }

            if (state is GuiderState.LostLock or GuiderState.Reacquiring)
            {
                SetState(GuiderState.Guiding);
            }

            if (r.Outcome == TrackerOutcome.Found)
            {
                lastGoodMass = r.Primary.Mass;
                lastGoodSnr = r.Primary.Snr;
                lastStarPosition = r.Primary.Position;
            }

            var mountOfs = t.CameraToMount(r.CameraOffset);
            double? sigma = MeasurementUncertainty.FrameSigmaPx(r, tracker.FinderOptions.FindMode);
            if (measuring)
            {
                // guiding output is off: no corrections, statistics or safety monitors; the coach's test pulses move the
                // mount unaccounted, so the open-loop Dec position continues in a new segment
                decDrift.Break();
                pulseModelFrame = false;
                pulseModel.Interrupt(mountMoved: true);
                pulses = CoachMeasurementFrame(frame, r, t, mountOfs, snapshot, now, sw, raOnly);
                return r;
            }

            // judge the previous frame's large pulse
            if (response.Evaluate(mountOfs.X, mountOfs.Y))
            {
                Fail(GuideErrorCode.MountNotResponding);
                return r;
            }

            if (!paused)
            {
                // a completed dither window may change the pulse model: the rates follow at once
                PulseModelFrameMeasured(mountOfs.X, mountOfs.Y, now);
                rates = PulseRates(t);
                UpdateDecDirection(now, mountOfs.Y, settle.IsActive || recenter.IsActive);
                if (recenter.IsActive)
                {
                    var (step, done) = recenter.NextStep();
                    corr = corrector.Move(step.X, step.Y, rates.X, rates.Y, MoveOptions.RecoveryMove, now);
                    corrector.DirectMoveApplied(step.X, step.Y);
                    if (done)
                    {
                        tracker.Distances.NeedReset = true;
                    }

                    recenterMove = true;
                }
                else
                {
                    PrepareAlgorithms(settle.IsActive, snapshot, sigma);
                    corr = corrector.GuideStep(mountOfs.X, mountOfs.Y, rates.X, rates.Y, now);
                }
            }
            else
            {
                pulseModel.Interrupt(mountMoved: true);
            }

            if (corr is not null)
            {
                foreach (var a in corr.Alerts)
                {
                    Alert(GuideErrorCode.PulseLimitReached, a.Message);
                }

                var corrPulses = corr.Pulses.Where(p => p.DurationMs > 0).ToList();
                if (recenterMove)
                {
                    // recovery moves bypass the algorithms and the limiter: never exceed the max pulse
                    corrPulses = corrPulses.Select(p => p with
                    {
                        DurationMs = Math.Min(p.DurationMs, p.Direction.Axis() == GuideAxis.Ra ? settings.MaxRaDurationMs : settings.MaxDecDurationMs),
                    }).ToList();
                }

                if (decFlip.IsActive)
                {
                    var decPulse = corrPulses.Where(p => p.Direction.Axis() == GuideAxis.Dec).Select(p => (PulseCommand?)p).FirstOrDefault();
                    var check = decFlip.Observe(mountOfs.Y, decPulse, rates.Y);
                    if (check.Verdict == DecFlipVerdict.InvertDec)
                    {
                        InvertDecAfterFlip(now);
                        corrPulses.RemoveAll(p => p.Direction.Axis() == GuideAxis.Dec);
                    }
                }

                if (!recenterMove)
                {
                    bool decObserved = decFlip.IsActive;
                    int raMs = SignedMs(corr.RADuration, corr.RADirection);
                    int decMs = decObserved ? 0 : SignedMs(corr.DECDuration, corr.DECDirection);
                    var verdict = runaway.Record(now, mountOfs.X, raMs, decObserved ? 0 : mountOfs.Y, decMs, out var axis);
                    if (verdict == SafetyVerdict.Runaway && axis == GuideAxis.Dec && flipTime is { } ft && now - ft < FlipRunawayWindow && !flipInverted
                        && settings.VerifyDecAfterFlip)
                    {
                        // shortly after a meridian flip a Dec runaway is almost certainly a wrong Dec-flip setting:
                        // invert Dec once instead of stopping; a second runaway stops guiding
                        InvertDecAfterFlip(now);
                        corrPulses.RemoveAll(p => p.Direction.Axis() == GuideAxis.Dec);
                        runaway.Reset();
                    }
                    else if (verdict == SafetyVerdict.Runaway)
                    {
                        Fail(GuideErrorCode.RunawayDetected, $"{axis} axis");
                        return r;
                    }

                    // judged on RA only: Dec pulses may legitimately be absorbed by backlash after a reversal
                    if (corr.RADuration > 0)
                    {
                        response.PulseIssued(GuideAxis.Ra, mountOfs.X, -corr.RaCorrectionPx);
                    }
                    else
                    {
                        response.NoPulse();
                    }
                }
                else
                {
                    response.NoPulse();
                }

                pulses = corrPulses;
                pendingCorrection = AxisCorrector.CorrectionOf(corrPulses, rates.X, rates.Y, corr.BacklashCompMs);
                stats.Add(GuideStepSample.FromCorrection(corr, now, r.Primary.Snr, r.StarsUsed, excluded: settle.IsActive || recenterMove));
                lastStats = stats.GetSnapshot(now);
                if (!settle.IsActive && !recenterMove)
                {
                    UpdateLiveHints(corr, r, now);
                }
            }

            EmitAlgorithmNotes(now);

            // the pulses as sent: a recenter pulse is clamped to the max pulse after the corrector computed it
            int raSentMs = pulses.Where(p => p.Direction.Axis() == GuideAxis.Ra).Sum(p => p.DurationMs);
            int decSentMs = pulses.Where(p => p.Direction.Axis() == GuideAxis.Dec).Sum(p => p.DurationMs);
            Emit(new GuideStepEvent(now)
            {
                Frame = frame.FrameNumber,
                Time = (now - guideStart).TotalSeconds,
                Mount = MountName,
                Dx = r.CameraOffset.X,
                Dy = r.CameraOffset.Y,
                RaDistanceRaw = mountOfs.X,
                DecDistanceRaw = mountOfs.Y,
                RaDistanceGuide = corr?.RADistanceGuide ?? 0,
                DecDistanceGuide = corr?.DECDistanceGuide ?? 0,
                RaDuration = raSentMs,
                RaDirection = raSentMs > 0 ? corr?.RADirection : null,
                DecDuration = decSentMs,
                DecDirection = decSentMs > 0 ? corr?.DECDirection : null,
                RaLimited = corr?.RALimited ?? false,
                DecLimited = corr?.DecLimited ?? false,
                StarMass = r.Primary.Mass,
                Snr = r.Primary.Snr,
                Hfd = r.Primary.Hfd,
                AvgDist = tracker.Distances.CurrentError(raOnly, now.ToUnixTimeMilliseconds()),
                ErrorCode = (int)r.ErrorCode,
                PixelScale = pixelScale,
                LockPosition = lockPosition,
                StarPosition = r.Primary.Position,
                StarsUsed = r.StarsUsed,
                PrimaryEstimated = r.IsEstimated,
                MeasurementSigmaPx = sigma,
                RaNoiseFactor = corr is not null && !recenterMove ? (corrector.RaAlgorithm as PredictiveAlgorithm)?.LastNoiseFactor : null,
                DecNoiseFactor = corr is not null && !recenterMove ? (corrector.DecAlgorithm as PredictiveAlgorithm)?.LastNoiseFactor : null,
                IsSettling = settle.IsActive,
                IsRecenterMove = recenterMove,
                Stars = ToStarInfos(r),
                ProcessingMs = sw.Elapsed.TotalMilliseconds,
            });

            if (r.IsEstimated)
            {
                Emit(new AlertEvent(now, GuideErrorCode.PrimaryEstimatedFromSecondaries, GuideErrorSeverity.Info,
                    GuideErrorCatalog.Get(GuideErrorCode.PrimaryEstimatedFromSecondaries).Title));
            }

            CoachObserveFrame(frame, r, t, mountOfs, snapshot, now, corr, recenterMove);
        }
        else if (r.Outcome == TrackerOutcome.JumpRejected)
        {
            response.NoPulse();
            pulseModel.Interrupt(mountMoved: false);
        }
        else
        {
            response.NoPulse();
            pulseModel.Interrupt(mountMoved: false);
            HandleStarLost(frame, r, now, paused, t, out pulses);
            if (!state.IsCapturing())
            {
                return r;
            }

            CoachStarLostFrame(frame, r, t, snapshot, now);
        }

        UpdateSettle(now, raOnly);
        return r;
    }

    private void HandleStarLost(GuideFrame frame, MultiStarFrameResult r, DateTimeOffset now, bool paused, MountTransform t, out IReadOnlyList<PulseCommand> pulses)
    {
        pulses = [];
        Emit(new StarLostEvent(now, frame.FrameNumber, (now - guideStart).TotalSeconds, r.Primary.Mass, r.Primary.Snr, r.Primary.Hfd,
            tracker.Distances.CurrentError(false, now.ToUnixTimeMilliseconds()), (int)r.ErrorCode, r.Status));
        stats.AddStarLost(now);

        if (paused)
        {
            // paused (by the user or because the mount moves): no reacquire timer, no corrections
            reacquire.Reset();
            return;
        }

        if (!reacquire.IsActive)
        {
            reacquire.StarLost(now, lastGoodMass, lastGoodSnr);
            Alert(GuideErrorCode.StarLost, r.Status);
            if (state == GuiderState.Guiding)
            {
                SetState(GuiderState.LostLock);
            }
        }
        else if (state == GuiderState.LostLock)
        {
            SetState(GuiderState.Reacquiring);
        }

        if (!paused)
        {
            var (xRate, yRate) = PulseRates(t);
            var deduced = corrector.DeducedStep(xRate, yRate, now);
            if (deduced is not null)
            {
                pulses = deduced.Pulses.Where(p => p.DurationMs > 0).ToList();
            }
        }

        switch (reacquire.Next(now))
        {
            case ReacquireAction.GiveUp:
                pulses = [];
                if (settings.Safety.StopOnLostStarTimeout)
                {
                    Fail(GuideErrorCode.StarReacquireTimeout);
                    break;
                }

                // Deviation from the v1 design (reviewed): like PHD2, keep searching after the timeout so guiding recovers
                // by itself when the clouds pass; the timeout raises a critical alert once.
                if (!lostTimeoutAlerted)
                {
                    lostTimeoutAlerted = true;
                    Alert(GuideErrorCode.StarReacquireTimeout, "still searching");
                }

                if (frame.FrameNumber % 2 == 0)
                {
                    TryReacquireFullFrame(frame);
                }

                break;
            case ReacquireAction.SearchFullFrame:
                TryReacquireFullFrame(frame);
                break;
        }
    }

    private void TryReacquireFullFrame(GuideFrame frame)
    {
        var opts = settings.StarFinder;
        var found = GuideStar.AutoFind(frame, 0, tracker.SearchRegion, default, settings.MultiStar.MultiStarEnabled ? settings.MultiStar.MaxListSize : 1, opts);
        var candidates = found.Where(s => reacquire.IsPlausible(s.Mass, s.Snr)).ToList();
        if (candidates.Count == 0)
        {
            return;
        }

        // only accept a candidate near where the star was last seen: a different star elsewhere in the frame
        // would make the guider drive the mount towards the old lock position
        var anchor = lastStarPosition.IsValid ? lastStarPosition : lockPosition;
        double maxDistance = 3.0 * tracker.SearchRegion;
        var best = candidates
            .Where(s => !anchor.IsValid || s.Position.Distance(anchor) <= maxDistance)
            .OrderBy(s => anchor.IsValid ? s.Position.Distance(anchor) : 0)
            .FirstOrDefault();
        if (best is null)
        {
            return;
        }

        var ordered = new List<GuideStar> { best };
        ordered.AddRange(found.Where(s => !ReferenceEquals(s, best)));
        tracker.Initialize(frame, ordered, best.Position);
    }

    private void InvertDecAfterFlip(DateTimeOffset now)
    {
        if (calibration is null || adjustment is null)
        {
            return;
        }

        var inverted = CalibrationAdjuster.InvertDec(calibration);
        settings = settings with { DecFlipRequired = !settings.DecFlipRequired };
        baseSettings = baseSettings with { DecFlipRequired = settings.DecFlipRequired };
        UseCalibration(adjustment with { Calibration = inverted });
        decFlip.Reset();
        flipInverted = true;
        corrector.DecAlgorithm.Reset();
        ResetDecDirection("Dec reversed after the meridian flip");
        Alert(GuideErrorCode.DecFlipCorrected);
        Emit(new CalibrationUpdatedEvent(now, inverted, settings.DecFlipRequired));
    }

    private static int SignedMs(int duration, GuideDirection dir) => dir is GuideDirection.West or GuideDirection.South ? -duration : duration;

    private void UpdateSettle(DateTimeOffset now, bool raOnly)
    {
        if (!settle.IsActive)
        {
            return;
        }

        var p = settle.Update(tracker.IsLocked, tracker.Distances.CurrentError(raOnly, now.ToUnixTimeMilliseconds()), now);
        switch (p.Outcome)
        {
            case SettleOutcome.InProgress:
                Emit(new SettlingEvent(now, p.Distance, p.TimeInRangeSec, p.SettleTimeSec, p.StarLocked));
                break;
            case SettleOutcome.Succeeded:
                FinishSettle(now, new SettleResult(true, null, p.TotalFrames, p.DroppedFrames));
                break;
            case SettleOutcome.TimedOut:
                Alert(GuideErrorCode.SettleTimeout);
                FinishSettle(now, new SettleResult(false, "timed-out waiting for guider to settle", p.TotalFrames, p.DroppedFrames, GuideErrorCode.SettleTimeout));
                break;
        }
    }

    private void BeginSettle(SettleParams p)
    {
        var now = clock.UtcNow;
        settle.Begin(p, now);
        Emit(new SettleBeginEvent(now));
    }

    private void FinishSettle(DateTimeOffset now, SettleResult result)
    {
        if (savedDecGuideMode is { } mode)
        {
            corrector.DecGuideMode = mode;
            savedDecGuideMode = null;
        }

        corrector.GuidingDitherSettleDone(result.Success);
        Emit(new SettleDoneEvent(now, result.Success ? 0 : 1, result.Error, result.TotalFrames, result.DroppedFrames));
        CompletePending(result);
    }

    private void DitherCore(double amountPx, bool raOnly, PendingOperation op)
    {
        if (state is not (GuiderState.Guiding or GuiderState.LostLock or GuiderState.Reacquiring) || transform is null)
        {
            op.Complete(SettleResult.Failed(GuideErrorCode.DitherFailed, "cannot dither if not guiding"));
            return;
        }

        if (IsCoachMeasuring)
        {
            op.Complete(SettleResult.Failed(GuideErrorCode.DitherFailed, "cannot dither during a Guiding Coach measurement"));
            return;
        }

        var now = clock.UtcNow;
        bool decOff = corrector.DecGuideMode == DecGuideMode.Off;
        var mountDelta = ditherPlanner.NextMountOffset(amountPx, raOnly || decOff);
        var t = transform;
        var plan = DitherPlanner.Plan(lockPosition, mountDelta, frameWidth, frameHeight, m => t.MountToCamera(m), p => tracker.IsValidLockPosition(p));
        if (!plan.LockPositionValid)
        {
            op.Complete(SettleResult.Failed(GuideErrorCode.DitherFailed, "move lock failed"));
            return;
        }

        ReplacePending(op);
        SetLockPositionCore(plan.NewLockPosition);
        corrector.GuidingDithered(plan.MountDelta.X, plan.MountDelta.Y, now);

        // the lock moved by the mount delta: the offsets the dither creates are its negative
        pulseModel.DitherStarted(-plan.MountDelta.X, -plan.MountDelta.Y);

        // the open-loop Dec position continues in a new segment after the recovery (see DecDriftEstimator)
        decDrift.Break();
        tracker.AddDitherDistance(plan.CameraDelta.Distance(), Math.Abs(plan.MountDelta.X));
        if (settings.FastRecenter)
        {
            recenter.Start(plan.MountDelta, tracker.SearchRegion);
        }

        Emit(new GuidingDitheredEvent(now, plan.MountDelta.X, plan.MountDelta.Y));

        // PHD2 PhdController: temporarily allow both Dec directions while settling after a dither
        if (corrector.DecGuideMode is DecGuideMode.North or DecGuideMode.South)
        {
            savedDecGuideMode = corrector.DecGuideMode;
            corrector.DecGuideMode = DecGuideMode.Auto;
        }

        runaway.Reset();
        response.Reset();
        BeginSettle(op.Settle);
    }

    private void StopGuidingCore(GuiderState next)
    {
        if (state.IsGuidingActive() || state == GuiderState.Calibrating)
        {
            corrector.GuidingStopped();
            pulseModel.Interrupt(mountMoved: true);
            calibrationProcess = null;
            settle.Cancel();
            recenter.Cancel();
            Emit(new GuidingStoppedEvent(clock.UtcNow));
            CompletePending(SettleResult.Failed(GuideErrorCode.None, "guiding stopped"));
            SetState(tracker.IsLocked ? next : GuiderState.Looping);
        }

        ClearRequests();
        fullPause = false;
        userPaused = false;
    }

    private void ResumeCore()
    {
        var now = clock.UtcNow;
        fullPause = false;
        if (calibration is not null && adjustment is not null)
        {
            // the mount may have flipped while paused
            var adj = AdjustCalibration(calibration, SafeSnapshot());
            if (adj.IsValid)
            {
                UseCalibration(adj);
                if (adj.Flipped && settings.VerifyDecAfterFlip && settings.DecGuideMode != DecGuideMode.Off)
                {
                    decFlip.Start();
                    flipTime = now;
                    flipInverted = false;
                }

                if (adj.Flipped)
                {
                    ResetDecDirection("meridian flip");
                }
            }
        }

        // the mount may have moved while paused: the open-loop Dec position continues in a new segment
        decDrift.Break();
        corrector.GuidingResumed();
        tracker.Distances.NeedReset = true;
        runaway.Reset();
        response.Reset();
        SetState(GuiderState.Guiding);
        Emit(new ResumedEvent(now));
    }

    private void HandleMountState(MountSnapshot snapshot)
    {
        var reason = mountWatch.Update(snapshot);
        UpdateCoachMountGate(snapshot);

        if (!(state.IsGuidingActive() || state == GuiderState.Calibrating))
        {
            return;
        }

        if (reason != MountPauseReason.None)
        {
            if (state == GuiderState.Calibrating)
            {
                FailCalibration(PauseCode(reason), $"mount {reason}", clock.UtcNow);
                return;
            }

            if (!autoPaused)
            {
                autoPaused = true;
                corrector.GuidingPaused();
                Alert(PauseCode(reason));
                if (state != GuiderState.Paused)
                {
                    SetState(GuiderState.Paused);
                }

                Emit(new PausedEvent(clock.UtcNow, reason.ToString()));
            }

            return;
        }

        if (autoPaused)
        {
            if (mountWatch.RequiresExplicitResume)
            {
                // stay paused until the host calls Resume()/StartGuiding
                needsRestartAfterMove = true;
                return;
            }

            autoPaused = false;
            if (userPaused)
            {
                // the user paused before/while the mount moved: stay paused until they resume
                return;
            }

            ResumeCore();
        }
    }

    private static GuideErrorCode PauseCode(MountPauseReason r) => r switch
    {
        MountPauseReason.Slewing or MountPauseReason.Homing => GuideErrorCode.MountSlewing,
        MountPauseReason.Parked => GuideErrorCode.MountParked,
        MountPauseReason.TrackingOff => GuideErrorCode.MountTrackingOff,
        _ => GuideErrorCode.MountDisconnected,
    };

    /// <summary>Sends the pulses; false when the guide output failed.</summary>
    private async Task<bool> IssuePulsesAsync(IReadOnlyList<PulseCommand> pulses, CancellationToken ct)
    {
        try
        {
            if (settings.SimultaneousPulses && output.SupportsSimultaneousPulses && pulses.Count > 1)
            {
                await Task.WhenAll(pulses.Select(p => output.PulseAsync(p.Direction, p.DurationMs, ct))).ConfigureAwait(false);
            }
            else
            {
                foreach (var p in pulses)
                {
                    await output.PulseAsync(p.Direction, p.DurationMs, ct).ConfigureAwait(false);
                }
            }

            pulseFailures = 0;
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Alert(GuideErrorCode.PulseOutputFailed, ex.Message);
            if (++pulseFailures >= settings.MaxPulseFailures)
            {
                Fail(GuideErrorCode.MountDisconnected, ex.Message);
            }

            return false;
        }
    }

    #endregion

    #region helpers

    // Re-applying unchanged settings (a coach step without setting changes) keeps every state: only the constructor's
    // initial call builds the algorithms, statistics and monitors unconditionally.
    private void ApplySettings(GuiderSettings s, bool initial = false)
    {
        var old = settings;
        settings = s;
        pixelScale = s.PixelScale(camera.PixelSizeUm);

        if (initial || !ReferenceEquals(old, s) || tracker.FinderOptions != s.StarFinder with { PixelScale = pixelScale })
        {
            tracker.FinderOptions = s.StarFinder with { PixelScale = pixelScale };
            tracker.Options = s.MultiStar;
        }

        Preprocessor.NoiseReduction = s.NoiseReduction;
        recorder?.Configure(s.Incidents);

        double minMove = s.MinMovePx ?? MinMove.SmartDefault(pixelScale);
        bool sameMinMove = old.MinMovePx == s.MinMovePx && MinMove.SmartDefault(old.PixelScale(camera.PixelSizeUm)) == MinMove.SmartDefault(pixelScale);
        bool samePixels = old.Binning == s.Binning;
        var ra = initial ? CreateAlgorithm(s.RaAlgorithm, GuideAxis.Ra, minMove)
            : NextAlgorithm(corrector.RaAlgorithm, old.RaAlgorithm, s.RaAlgorithm, GuideAxis.Ra, minMove, sameMinMove, samePixels);
        var dec = initial ? CreateAlgorithm(s.DecAlgorithm, GuideAxis.Dec, minMove)
            : NextAlgorithm(corrector.DecAlgorithm, old.DecAlgorithm, s.DecAlgorithm, GuideAxis.Dec, minMove, sameMinMove, samePixels);
        if (ra is PredictiveAlgorithm freshRa && !ReferenceEquals(ra, corrector.RaAlgorithm) && storedPeriodicError is { } storedModel)
        {
            freshRa.RestorePeriodicError(storedModel);
        }
        var limiter = new PulseLimiter();
        limiter.SetMaxRaDuration(s.MaxRaDurationMs);
        limiter.SetMaxDecDuration(s.MaxDecDurationMs);
        var backlash = new BacklashCompensation(s.Backlash.PulseMs, s.Backlash.FloorMs, s.Backlash.CeilingMs, s.Backlash.Enabled, limiter);
        var newCorrector = new AxisCorrector(ra, dec, limiter, backlash)
        {
            DecGuideMode = s.DecGuideMode,
            MinPulseMs = s.MinPulseMs,
        };
        if (IsCoachMeasuring)
        {
            // a new corrector starts with output enabled: keep it off during a coach measurement
            newCorrector.SetGuidingEnabled(false, clock.UtcNow);
        }

        corrector = newCorrector;
        if (!initial)
        {
            DecGuideModeChanged(old.DecGuideMode, s.DecGuideMode);
            if (!samePixels)
            {
                // the drift is measured in pixels
                ResetDecDirection("binning changed");
            }
        }

        ditherPlanner.Mode = s.DitherMode;
        ditherPlanner.ScaleFactor = s.DitherScale;

        if (old.StatsWindow != s.StatsWindow || initial)
        {
            stats = new GuidingStatistics(s.StatsWindow, pixelScale);
        }
        else
        {
            stats.PixelScale = pixelScale;
        }

        // records compare by value: only a real change of the safety settings rebuilds the monitors (and loses their state)
        if (!Equals(old.Safety, s.Safety) || initial)
        {
            runaway = new RunawayDetector(s.Safety);
            response = new MountResponseMonitor(s.Safety);
            mountWatch = new MountStateWatcher(s.Safety);
            cameraRetry = new CameraRetryPolicy(s.Safety);
            reacquire = new ReacquirePolicy(s.Safety);
        }

        if (!initial)
        {
            Emit(new SettingsChangedEvent(clock.UtcNow));
            if (s.PulseModel && !old.PulseModel && state.IsGuidingActive())
            {
                EmitPulseModelInUse(clock.UtcNow);
            }
        }
    }

    private static bool SameAlgorithm(AlgorithmSettings a, AlgorithmSettings b)
    {
        if (a.Kind != b.Kind)
        {
            return false;
        }

        var pa = a.Parameters ?? new Dictionary<string, double>();
        var pb = b.Parameters ?? new Dictionary<string, double>();
        return pa.Count == pb.Count && pa.All(kv => pb.TryGetValue(kv.Key, out var v) && v == kv.Value);
    }

    // Per axis: an algorithm whose settings did not change keeps running with its state. The Predictive algorithm also keeps
    // what it learned about the sky and the mount when only its parameters change (a coach trial's min move, its
    // correction pace), unless the pixels changed (binning): its estimates are in pixels. Another kind starts over.
    internal static IGuideAlgorithm NextAlgorithm(IGuideAlgorithm current, AlgorithmSettings was, AlgorithmSettings next, GuideAxis axis,
        double minMove, bool sameMinMove, bool samePixels)
    {
        var fresh = CreateAlgorithm(next, axis, minMove);
        if (current.Name != fresh.Name || (current is PredictiveAlgorithm && !samePixels))
        {
            return fresh;
        }

        // the smart min move is for the PHD2 algorithms only
        if (SameAlgorithm(was, next) && (sameMinMove || current is PredictiveAlgorithm))
        {
            return current;
        }

        if (current is PredictiveAlgorithm predictive)
        {
            predictive.ChangeParameters(next.Parameters);
            return predictive;
        }

        return fresh;
    }

    private static IGuideAlgorithm CreateAlgorithm(AlgorithmSettings a, GuideAxis axis, double minMove)
    {
        var alg = GuideAlgorithmFactory.Create(a.Kind, axis, minMove);
        if (a.Parameters is not null)
        {
            foreach (var (name, value) in a.Parameters)
            {
                alg.TrySetParam(name, value);
            }
        }

        return alg;
    }

    private GuideOptics Optics() => new(settings.FocalLengthMm, settings.PixelSizeUm > 0 ? settings.PixelSizeUm : camera.PixelSizeUm, settings.Binning);

    private int CalibrationDistancePx(MountSnapshot snapshot) =>
        settings.AutoCalibrationStep ? CalibrationStepCalculator.Recommend(Optics(), snapshot).DistancePx : settings.Calibration.DistancePx;

    private void SetLockPositionCore(GuidePoint p)
    {
        if (!lockPosition.IsValid || lockPosition.X != p.X || lockPosition.Y != p.Y)
        {
            lockPosition = p;
            Emit(new LockPositionSetEvent(clock.UtcNow, p.X, p.Y));
        }
    }

    private void SetState(GuiderState s)
    {
        var prev = state;
        if (prev == s)
        {
            return;
        }

        state = s;
        Emit(new AppStateEvent(clock.UtcNow, s, prev));
    }

    private void Alert(GuideErrorCode code, string? detail = null)
    {
        var info = GuideErrorCatalog.Get(code);
        if (info.Severity != GuideErrorSeverity.Info)
        {
            lastError = info;
        }

        string? incidentId = IncidentForAlert(code, info, detail);
        Emit(new AlertEvent(clock.UtcNow, code, info.Severity, info.Title, detail) { IncidentId = incidentId });
    }

    private void Fail(GuideErrorCode code, string? detail = null)
    {
        OnGuiderFailedForCoach(code);
        Alert(code, detail);
        if (state.IsGuidingActive() || state == GuiderState.Calibrating)
        {
            corrector.GuidingStopped();
            Emit(new GuidingStoppedEvent(clock.UtcNow));
        }

        settle.Cancel();
        recenter.Cancel();
        calibrationProcess = null;
        ClearRequests();
        fullPause = false;
        userPaused = false;
        SetState(GuiderState.Failed);
        CompletePending(SettleResult.Failed(code, detail));
    }

    // a newer start or dither carries on what the pending one began: the replaced caller gets the newer one's outcome
    // instead of a failure while guiding goes on
    private void ReplacePending(PendingOperation op)
    {
        var replaced = pending;
        pending = op;
        replaced?.Follow(op);
    }

    private void CompletePending(SettleResult result)
    {
        var p = pending;
        pending = null;
        p?.Complete(result);
    }

    // settling frames guide but don't teach a Predictive algorithm (like they don't count in the statistics); noisier
    // frames are trusted less (measurement uncertainty); the RA axis also learns where the worm is
    private void PrepareAlgorithms(bool settling, MountSnapshot snapshot, double? measurementSigmaPx)
    {
        foreach (var algorithm in new[] { corrector.RaAlgorithm, corrector.DecAlgorithm })
        {
            if (algorithm is PredictiveAlgorithm predictive)
            {
                predictive.Settling = settling;
                predictive.MeasurementSigmaPx = measurementSigmaPx;
            }
        }

        if (corrector.RaAlgorithm is PredictiveAlgorithm ra)
        {
            ra.PeriodicContext = PeriodicContext(snapshot);
        }
    }

    // RA axis angle = the mount's hour angle (its sidereal time − its right ascension) + 12 h on the West pier side, kept
    // continuous from frame to frame; without sidereal time or RA the periodic error falls back to the time
    private PeriodicErrorContext PeriodicContext(MountSnapshot snapshot)
    {
        double? axis = null;
        if (snapshot.IsConnected && snapshot.SiderealTimeHours is { } lst && snapshot.RightAscensionHours is { } ra)
        {
            double hours = lst - ra + (snapshot.PierSide == PierSide.West ? 12.0 : 0.0);
            hours = lastAxisHours is { } last ? hours + 24.0 * Math.Round((last - hours) / 24.0) : hours - 24.0 * Math.Floor((hours + 12.0) / 24.0);
            lastAxisHours = hours;
            axis = hours;
        }

        return new PeriodicErrorContext
        {
            AxisHours = axis,
            DeclinationDeg = snapshot.IsConnected ? snapshot.DeclinationDeg : null,
            PixelScale = pixelScale,
            PierSide = snapshot.IsConnected ? snapshot.PierSide : PierSide.Unknown,
        };
    }

    private void EmitPeriodicErrorModel(DateTimeOffset now)
    {
        if (corrector.RaAlgorithm is not PredictiveAlgorithm ra)
        {
            return;
        }

        // a detected period that no longer holds takes a stored curve with that period along (it would come back with
        // the next algorithm or session)
        if (ra.TakeDiscardedPeriodicError() is { } discarded && storedPeriodicError is { } stored
            && Math.Abs(stored.PeriodSeconds - discarded) <= PeriodicErrorEstimator.DiscardDifference * discarded)
        {
            storedPeriodicError = null;
            Emit(new PeriodicErrorModelDiscardedEvent(now, stored));
        }

        if (ra.TakePeriodicErrorModel(now) is { } model)
        {
            storedPeriodicError = model;
            Emit(new PeriodicErrorModelEvent(now, model));
        }
    }

    // what the Predictive algorithms noted during this frame (and lifecycle calls before it)
    private void EmitAlgorithmNotes(DateTimeOffset now)
    {
        EmitPeriodicErrorModel(now);
        foreach (var (axis, algorithm) in new[] { (GuideAxis.Ra, corrector.RaAlgorithm), (GuideAxis.Dec, corrector.DecAlgorithm) })
        {
            if (algorithm is PredictiveAlgorithm predictive)
            {
                foreach (var note in predictive.TakeNotes())
                {
                    Emit(new AlgorithmNoteEvent(now, axis, note.Kind, note.Message));
                }
            }
        }
    }

    private void Emit(GuiderEvent e)
    {
        ObserveIncidentEvent(e);
        if (deferEvents)
        {
            deferredEvents.Add(e);
            return;
        }

        Deliver(e);
    }

    private void FlushEvents()
    {
        if (deferredEvents.Count == 0)
        {
            return;
        }

        var events = deferredEvents.ToArray();
        deferredEvents.Clear();
        foreach (var e in events)
        {
            Deliver(e);
        }
    }

    private void Deliver(GuiderEvent e)
    {
        try
        {
            EventRaised?.Invoke(this, e);
        }
        catch (Exception ex)
        {
            // a faulty subscriber must never break the guide loop (rate-limited: a subscriber that fails on the fault
            // event too ends there)
            ReportFault("Guider.EventSubscriber", ex);
        }
    }

    private static IReadOnlyList<StarInfo> ToStarInfos(MultiStarFrameResult r) => r.Stars.Select(s => new StarInfo(
        s.Star.Position.X, s.Star.Position.Y, s.Star.Snr, s.Star.Mass, s.Star.Hfd, s.Index == 0,
        s.Status is TrackedStarStatus.Primary or TrackedStarStatus.Used or TrackedStarStatus.FallbackUsed,
        s.Weight,
        s.Status is TrackedStarStatus.Primary or TrackedStarStatus.Used or TrackedStarStatus.FallbackUsed ? null : s.Status.ToString())).ToList();

    private enum PendingKind
    {
        Guide,
        Dither,
    }

    private sealed class PendingOperation
    {
        private readonly TaskCompletionSource<SettleResult> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenRegistration reg;

        public PendingOperation(PendingKind kind, SettleParams settle, CancellationToken ct)
        {
            Kind = kind;
            Settle = settle;
            if (ct.CanBeCanceled)
            {
                reg = ct.Register(() => tcs.TrySetCanceled(ct));
            }
        }

        public PendingKind Kind { get; }

        public SettleParams Settle { get; }

        public Task<SettleResult> Task => tcs.Task;

        public void Complete(SettleResult r)
        {
            reg.Dispose();
            tcs.TrySetResult(r);
        }

        /// <summary>Completes with the outcome of the operation that replaced this one.</summary>
        public void Follow(PendingOperation newer)
        {
            newer.Task.ContinueWith(t => Complete(t.IsCompletedSuccessfully
                    ? t.Result
                    : SettleResult.Failed(GuideErrorCode.None, "the operation that replaced this one was cancelled")),
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    #endregion
}

/// <summary>A calibration was created or changed (flip, Dec inversion); the host should persist it and the Dec-flip setting.</summary>
public sealed record CalibrationUpdatedEvent(DateTimeOffset Timestamp, CalibrationData Calibration, bool DecFlipRequired) : GuiderEvent(Timestamp)
{
    public override string Phd2Name => "CalibrationUpdated";
}
