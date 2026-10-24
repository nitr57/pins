// SPDX-License-Identifier: MPL-2.0

using System.Collections.Concurrent;
using System.Diagnostics;
using NINA.GuideEngine.Algorithms;
using NINA.GuideEngine.Calibration;
using NINA.GuideEngine.Coach;
using NINA.GuideEngine.Core;
using NINA.GuideEngine.MultiStar;

namespace NINA.GuideEngine.Guiding;

/// <summary>
/// Guiding Coach integration: temporary settings overlay, measurement/observer frame hooks, session interrupts,
/// camera lease for measurements outside the loop, events published from other threads and live hints.
/// </summary>
public sealed partial class Guider
{
    private readonly ConcurrentQueue<GuiderEvent> publishedEvents = new();
    private readonly object publishLock = new();
    private readonly LiveHintAnalyser liveHints = new();
    private readonly CombinedPositionMeter combinedMeter = new();
    private readonly CoachMountGate coachMountGate = new();
    private bool coachMountBusy;
    private bool coachPulsesDropped;

    private Action<string>? coachInterrupt;
    private ICoachFrameHook? coachHook;
    private Func<GuiderSettings, GuiderSettings>? coachOverlay;
    private volatile bool coachStoppingCapture;
    private bool cameraLeased;
    private bool deferredLoopStart;
    private CancellationToken deferredLoopToken;
    private bool forcedRestart;
    private CancellationToken forcedRestartToken;

    /// <summary>Active live coaching hints (not expired, not dismissed) of the current guiding session.</summary>
    public IReadOnlyList<CoachFinding> ActiveHints => liveHints.GetActive(clock.UtcNow);

    /// <summary>Hides a live hint for the rest of the guiding session. Returns false for an unknown id.</summary>
    public bool DismissHint(string id) => liveHints.Dismiss(id);

    /// <summary>True while a Guiding Coach session is attached (guiding commands interrupt it).</summary>
    public bool IsCoachAttached => Volatile.Read(ref coachInterrupt) is not null;

    /// <summary>The live hint analyser (e.g. to hand it the seeing floor measured by the coach).</summary>
    public LiveHintAnalyser LiveHints => liveHints;

    internal ICameraSource Camera => camera;

    internal IPulseOutput Output => output;

    internal IMountState MountState => mount;

    internal IClock Clock => clock;

    /// <summary>Settings as set by the host, without the coach's temporary overlay.</summary>
    internal GuiderSettings BaseSettings => baseSettings;

    internal int TrackerSearchRegion => tracker.SearchRegion;

    /// <summary>Guiding output state of the corrector (false only during coach measurements).</summary>
    internal bool GuidingOutputEnabled => corrector.GuidingEnabled;

    internal bool HasCoachOverlay => coachOverlay is not null;

    internal bool HasCoachHook => coachHook is not null;

    /// <summary>The installed frame hook, null without one (tests).</summary>
    internal ICoachFrameHook? CoachHook => coachHook;

    private bool IsCoachMeasuring => coachHook is { SuspendsGuiding: true };

    #region coach session API (internal)

    /// <summary>Attaches a coach session; <paramref name="onInterrupt"/> is called once when a host command or event interrupts it.</summary>
    internal bool AttachCoach(Action<string> onInterrupt)
    {
        if (Interlocked.CompareExchange(ref coachInterrupt, onInterrupt, null) is not null)
        {
            return false;
        }

        coachMountGate.Reset();
        return true;
    }

    /// <summary>
    /// Debounced mount check for the coach outside the loop (camera check): busy while a condition is active, an interrupt
    /// reason once it is confirmed (see <see cref="CoachMountGate"/>).
    /// </summary>
    internal CoachMountVerdict CheckCoachMount() => coachMountGate.Update(SafeSnapshot(), clock.UtcNow, settings.Safety);

    /// <summary>Detaches the session (no further interrupts). Does not restore state; see <see cref="ClearCoachStateAsync"/>.</summary>
    internal void DetachCoach() => Volatile.Write(ref coachInterrupt, null);

    /// <summary>Installs (or removes with null) the frame hook; completes when the loop applied it.</summary>
    internal Task SetCoachHookAsync(ICoachFrameHook? hook) => RunOnLoopAsync(() => SetCoachHookCore(hook));

    /// <summary>Sets (or clears with null) the temporary settings overlay applied on top of the host settings.</summary>
    internal Task SetCoachOverlayAsync(Func<GuiderSettings, GuiderSettings>? overlay) => RunOnLoopAsync(() =>
    {
        coachOverlay = overlay;
        ApplySettings(ComposeSettings());
    });

    /// <summary>Removes hook and overlay: guiding output re-enabled, host settings restored.</summary>
    internal Task ClearCoachStateAsync() => RunOnLoopAsync(ClearCoachStateCore);

    internal Task<SettleResult> StartGuidingForCoachAsync(SettleParams settleParams, CancellationToken ct, CancellationToken loopToken) =>
        StartGuidingCoreAsync(settleParams, false, ct, loopToken, forceLoop: true);

    internal void StopGuidingForCoach() => Post(() => StopGuidingCore(GuiderState.Selected));

    /// <summary>Starts looping (e.g. after the camera check stopped the loop) without interrupting the session.</summary>
    internal void StartLoopingForCoach(CancellationToken loopToken)
    {
        Post(() =>
        {
            if (state is GuiderState.Stopped or GuiderState.Failed)
            {
                ClearRequests();
                SetState(GuiderState.Looping);
            }
        });
        EnsureLoop(loopToken, force: true);
    }

    /// <summary>Executes queued commands on the caller when the loop is not running (commands posted as it ended).</summary>
    internal void FlushCommandsIfIdle()
    {
        if (!IsLoopRunning)
        {
            DrainCommands();
        }
    }

    internal void PauseForCoach() => PauseCore(false);

    /// <summary>Stops the capture loop without interrupting the attached session (camera check).</summary>
    internal async Task StopCaptureForCoachAsync()
    {
        coachStoppingCapture = true;
        try
        {
            await StopCaptureCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            coachStoppingCapture = false;
        }
    }

    /// <summary>
    /// Reserves the camera for captures outside the loop (the coach, the host's dark library). False while the loop runs;
    /// a loop start requested meanwhile waits for <see cref="ReleaseCamera"/>.
    /// </summary>
    public bool TryLeaseCamera()
    {
        lock (loopLock)
        {
            if (cameraLeased || loopTask is { IsCompleted: false })
            {
                return false;
            }

            cameraLeased = true;
            return true;
        }
    }

    /// <summary>Releases the camera; a loop start requested meanwhile happens now.</summary>
    public void ReleaseCamera()
    {
        bool start;
        CancellationToken token;
        lock (loopLock)
        {
            cameraLeased = false;
            start = deferredLoopStart;
            token = deferredLoopToken;
            deferredLoopStart = false;
            deferredLoopToken = default;
        }

        if (start)
        {
            EnsureLoop(token, force: true);
        }
    }

    /// <summary>Publishes an event from outside the loop; delivered by the loop (deferred behind pulses) or directly when it is not running.</summary>
    internal void PublishEvent(GuiderEvent e)
    {
        publishedEvents.Enqueue(e);
        if (!IsLoopRunning)
        {
            lock (publishLock)
            {
                while (publishedEvents.TryDequeue(out var ev))
                {
                    Deliver(ev);
                }
            }
        }
    }

    /// <summary>Runs <paramref name="action"/> on the loop between frames, or right away when the loop is not running.</summary>
    private Task RunOnLoopAsync(Action action)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() =>
        {
            try
            {
                action();
                tcs.TrySetResult();
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });
        if (!IsLoopRunning)
        {
            DrainCommands();
        }

        return tcs.Task;
    }

    #endregion

    private GuiderSettings ComposeSettings()
    {
        if (coachOverlay is not { } overlay)
        {
            return baseSettings;
        }

        try
        {
            return overlay(baseSettings);
        }
        catch (Exception ex)
        {
            ReportFault("Coach.SettingsOverlay", ex);
            return baseSettings;
        }
    }

    private void SetCoachHookCore(ICoachFrameHook? hook)
    {
        bool wasMeasuring = IsCoachMeasuring;
        coachHook = hook;
        combinedMeter.Reset();
        bool measuring = IsCoachMeasuring;
        var now = clock.UtcNow;
        if (measuring && !wasMeasuring)
        {
            corrector.SetGuidingEnabled(false, now);
            recenter.Cancel();
            response.Reset();
            runaway.Reset();
        }
        else if (!measuring && wasMeasuring)
        {
            EndMeasurement(now);
        }
    }

    private void EndMeasurement(DateTimeOffset now)
    {
        corrector.SetGuidingEnabled(true, now);
        corrector.Backlash.ResetState();
        runaway.Reset();
        response.Reset();
        tracker.Distances.NeedReset = true;
    }

    private void ClearCoachStateCore()
    {
        SetCoachHookCore(null);
        if (coachOverlay is not null)
        {
            coachOverlay = null;
            ApplySettings(ComposeSettings());
        }
    }

    /// <summary>Called by host commands before they act: the session ends and its state is restored before the command runs.</summary>
    private void InterruptCoach(string reason)
    {
        var cb = Interlocked.Exchange(ref coachInterrupt, null);
        if (cb is null)
        {
            return;
        }

        Post(ClearCoachStateCore);
        if (!IsLoopRunning)
        {
            DrainCommands();
        }

        SafeInvoke(cb, reason);
    }

    /// <summary>Interrupt detected on the loop (mount moved, loop ended, guiding failed).</summary>
    private void InterruptCoachOnLoop(string reason)
    {
        var cb = Interlocked.Exchange(ref coachInterrupt, null);
        if (cb is null)
        {
            return;
        }

        ClearCoachStateCore();
        SafeInvoke(cb, reason);
    }

    private void SafeInvoke(Action<string> cb, string reason)
    {
        try
        {
            cb(reason);
        }
        catch (Exception ex)
        {
            // the session handles its own errors
            ReportFault("Coach.Interrupt", ex);
        }
    }

    private void OnGuiderFailedForCoach(GuideErrorCode code)
    {
        if (Volatile.Read(ref coachInterrupt) is null)
        {
            return;
        }

        if (coachHook is { } hook)
        {
            bool handled;
            try
            {
                handled = hook.OnGuidingFailed(code);
            }
            catch (Exception ex)
            {
                ReportFault("Coach.OnGuidingFailed", ex);
                handled = false;
            }

            if (handled)
            {
                // only the hook's measurement ends (e.g. a runaway during a trial); the session restarts guiding
                SetCoachHookCore(null);
                return;
            }
        }

        InterruptCoachOnLoop(code is GuideErrorCode.CameraFailed or GuideErrorCode.CameraDisconnected or GuideErrorCode.MountDisconnected
            ? CoachInterruptReasons.Disconnect
            : CoachInterruptReasons.Stopped);
    }

    /// <summary>
    /// The loop ended. Only a cancellation (stop capture, host token) interrupts the session here: a guiding failure was
    /// already handled by <see cref="OnGuiderFailedForCoach"/>, and a loop that started with nothing to do ends harmlessly.
    /// </summary>
    private void OnLoopEndedForCoach(bool cancelled)
    {
        if (cancelled && !coachStoppingCapture)
        {
            InterruptCoachOnLoop(CoachInterruptReasons.Stopped);
        }
        else if (IsCoachMeasuring)
        {
            SetCoachHookCore(null);
        }
    }

    /// <summary>Loop side of the coach mount debounce: marks frames busy and interrupts once a condition is confirmed.</summary>
    private void UpdateCoachMountGate(Core.MountSnapshot snapshot)
    {
        if (Volatile.Read(ref coachInterrupt) is null)
        {
            coachMountBusy = false;
            coachMountGate.Reset();
            return;
        }

        var verdict = coachMountGate.Update(snapshot, clock.UtcNow, settings.Safety);
        coachMountBusy = verdict.Busy;
        if (verdict.InterruptReason is { } reason)
        {
            InterruptCoachOnLoop(reason);
        }
    }

    private void DrainPublishedEvents()
    {
        while (publishedEvents.TryDequeue(out var e))
        {
            Emit(e);
        }
    }

    /// <param name="combined">Measurement frames: the multi-star combined camera position from <see cref="CombinedPositionMeter"/>.</param>
    private CoachFrame BuildCoachFrame(GuideFrame frame, MultiStarFrameResult r, MountTransform t, GuidePoint mountOfs, Core.MountSnapshot snapshot,
        DateTimeOffset now, bool found, GuideCorrection? corr, bool recenterMove, GuidePoint? combined = null, int? starsUsed = null,
        bool pulsesDropped = false) => new()
    {
        Time = now,
        FrameNumber = frame.FrameNumber,
        State = state,
        MountBusy = coachMountBusy,
        PulsesDropped = pulsesDropped,
        RightAscensionHours = snapshot.RightAscensionHours,
        StarFound = found,
        StarPosition = found ? r.Primary.Position : GuidePoint.Invalid,
        MountOffset = !found ? GuidePoint.Invalid
            : combined is { IsValid: true } c && lockPosition.IsValid ? t.CameraToMount(c - lockPosition)
            : mountOfs,
        // the combined (multi-star) position: measured by the meter, or lock + offset of normal guiding
        MountPosition = !found ? GuidePoint.Invalid
            : combined is { IsValid: true } c2 ? t.CameraToMount(c2)
            : r.CameraOffset.IsValid && lockPosition.IsValid ? t.CameraToMount(lockPosition + r.CameraOffset)
            : t.CameraToMount(r.Primary.Position),
        Snr = r.Primary.Snr,
        Mass = r.Primary.Mass,
        Hfd = r.Primary.Hfd,
        StarsUsed = starsUsed ?? r.StarsUsed,
        // the tracker's offset (normal guiding); the meter's combined position has another one
        MeasurementSigmaPx = found && combined is null ? MeasurementUncertainty.FrameSigmaPx(r, tracker.FinderOptions.FindMode) : null,
        Transform = t,
        FrameWidth = frame.Width,
        FrameHeight = frame.Height,
        SearchRegion = tracker.SearchRegion,
        PixelScale = pixelScale,
        ExposureMs = settings.ExposureMs,
        DeclinationDeg = snapshot.DeclinationDeg,
        MaxRaDurationMs = settings.MaxRaDurationMs,
        MaxDecDurationMs = settings.MaxDecDurationMs,
        IsSettling = settle.IsActive,
        IsRecenterMove = recenterMove,
        RaDurationMs = corr?.RADuration ?? 0,
        DecDurationMs = corr?.DECDuration ?? 0,
        DecDirection = corr is { DECDuration: > 0 } ? corr.DECDirection : null,
        RaLimited = corr?.RALimited ?? false,
        DecLimited = corr?.DecLimited ?? false,
    };

    private IReadOnlyList<PulseCommand> CoachMeasurementFrame(GuideFrame frame, MultiStarFrameResult r, MountTransform t, GuidePoint mountOfs,
        Core.MountSnapshot snapshot, DateTimeOffset now, Stopwatch sw, bool raOnly)
    {
        Emit(new GuideStepEvent(now)
        {
            Frame = frame.FrameNumber,
            Time = (now - guideStart).TotalSeconds,
            Mount = MountName,
            Dx = r.CameraOffset.X,
            Dy = r.CameraOffset.Y,
            RaDistanceRaw = mountOfs.X,
            DecDistanceRaw = mountOfs.Y,
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
            MeasurementSigmaPx = MeasurementUncertainty.FrameSigmaPx(r, tracker.FinderOptions.FindMode),
            Stars = ToStarInfos(r),
            ProcessingMs = sw.Elapsed.TotalMilliseconds,
            CoachMeasurement = true,
        });

        var hook = coachHook!;
        bool found = r.Outcome == TrackerOutcome.Found;
        GuidePoint? combined = null;
        int used = 1;
        if (found)
        {
            combined = combinedMeter.Measure(frame, r.Primary.Position, r.Primary.Snr, tracker.GuideStars,
                tracker.FinderOptions, tracker.SearchRegion, settings.MultiStar.MultiStarEnabled ? tracker.FinderOptions.MaxStars : 1, pixelScale, out used);
        }

        bool dropped = coachPulsesDropped;
        coachPulsesDropped = false;
        IReadOnlyList<PulseCommand> requested;
        try
        {
            requested = hook.OnFrame(BuildCoachFrame(frame, r, t, mountOfs, snapshot, now, found, null, false, combined, used, dropped));
        }
        catch (Exception ex)
        {
            ReportFault("Coach.OnFrame", ex);
            InterruptCoachOnLoop(CoachInterruptReasons.Error);
            return [];
        }

        if (!found || requested.Count == 0)
        {
            return [];
        }

        // measurement pulses never exceed the configured max pulse
        return requested.Where(p => p.DurationMs > 0)
            .Select(p => p with { DurationMs = Math.Min(p.DurationMs, p.Direction.Axis() == GuideAxis.Ra ? settings.MaxRaDurationMs : settings.MaxDecDurationMs) })
            .ToList();
    }

    private void CoachObserveFrame(GuideFrame frame, MultiStarFrameResult r, MountTransform t, GuidePoint mountOfs, Core.MountSnapshot snapshot,
        DateTimeOffset now, GuideCorrection? corr, bool recenterMove)
    {
        if (coachHook is not { SuspendsGuiding: false } hook)
        {
            return;
        }

        try
        {
            hook.OnFrame(BuildCoachFrame(frame, r, t, mountOfs, snapshot, now, r.StarFound, corr, recenterMove));
        }
        catch (Exception ex)
        {
            ReportFault("Coach.OnFrame", ex);
            InterruptCoachOnLoop(CoachInterruptReasons.Error);
        }
    }

    private void CoachStarLostFrame(GuideFrame frame, MultiStarFrameResult r, MountTransform t, Core.MountSnapshot snapshot, DateTimeOffset now)
    {
        if (coachHook is not { } hook)
        {
            return;
        }

        try
        {
            hook.OnFrame(BuildCoachFrame(frame, r, t, GuidePoint.Invalid, snapshot, now, false, null, false));
        }
        catch (Exception ex)
        {
            ReportFault("Coach.OnFrame", ex);
            InterruptCoachOnLoop(CoachInterruptReasons.Error);
        }
    }

    private void UpdateLiveHints(GuideCorrection corr, MultiStarFrameResult r, DateTimeOffset now)
    {
        if (!settings.LiveHints || lastStats is null || Volatile.Read(ref coachInterrupt) is not null || state != GuiderState.Guiding)
        {
            return;
        }

        var input = new LiveHintInput
        {
            Time = now,
            Stats = lastStats,
            RaLimited = corr.RALimited,
            DecLimited = corr.DecLimited,
            RaDurationMs = corr.RADuration,
            DecDurationMs = corr.DECDuration,
            DecDirection = corr.DECDuration > 0 ? corr.DECDirection : null,
            Snr = r.Primary.Snr,
            Settings = settings,
        };

        foreach (var hint in liveHints.Analyse(input))
        {
            Emit(new CoachHintEvent(now, hint));
        }
    }

    private void ResetLiveHints() => liveHints.ResetSession();
}

/// <summary>
/// Multi-star combined star position for coach measurements: the SNR-weighted mean displacement of the primary and the
/// secondary guide stars (weights SNR/primary SNR, as PHD2's multi-star offset) from their positions at the start of the
/// measurement. Unlike the guide loop's refinement it has no stabilisation or excursion gates, so it stays consistent during
/// unguided drift and test pulses; the seeing it measures has the same multi-star averaging as the guided RMS. A secondary
/// whose displacement departs from the primary's by more than max(2 px, 3″) is dropped (hot pixel, wrong star).
/// </summary>
internal sealed class CombinedPositionMeter
{
    private readonly List<Secondary> secondaries = [];
    private GuidePoint primaryReference = GuidePoint.Invalid;

    public void Reset()
    {
        secondaries.Clear();
        primaryReference = GuidePoint.Invalid;
    }

    /// <summary>Combined camera position this frame (the primary's reference plus the weighted mean displacement).</summary>
    public GuidePoint Measure(GuideFrame frame, GuidePoint primary, double primarySnr, IReadOnlyList<Stars.GuideStar> guideStars,
        Stars.StarFinderOptions finder, int searchRegion, int maxStars, double pixelScale, out int used)
    {
        used = 1;
        if (!primaryReference.IsValid)
        {
            primaryReference = primary;
            for (int i = 1; i < guideStars.Count && secondaries.Count < maxStars - 1; i++)
            {
                var s = new Stars.Star { Position = guideStars[i].Position };
                if (guideStars[i].Position.IsValid && s.Find(frame, searchRegion, finder.FindMode, finder.MinHfd, finder.MaxHfd, finder.SaturationAdu))
                {
                    secondaries.Add(new Secondary(s, s.Position));
                }
            }

            used += secondaries.Count;
            return primary;
        }

        double px = primary.X - primaryReference.X, py = primary.Y - primaryReference.Y;
        double sumX = px, sumY = py, sumW = 1;
        double limit = Math.Max(2.0, 3.0 / (pixelScale > 0 ? pixelScale : 1.0));
        foreach (var sec in secondaries)
        {
            if (!sec.Active || !sec.Star.Find(frame, searchRegion, finder.FindMode, finder.MinHfd, finder.MaxHfd, finder.SaturationAdu))
            {
                continue;
            }

            double dx = sec.Star.X - sec.Reference.X, dy = sec.Star.Y - sec.Reference.Y;
            if (Math.Sqrt((dx - px) * (dx - px) + (dy - py) * (dy - py)) > limit)
            {
                sec.Active = false;
                continue;
            }

            double w = primarySnr > 0 ? sec.Star.Snr / primarySnr : 1.0;
            sumX += w * dx;
            sumY += w * dy;
            sumW += w;
            used++;
        }

        return new GuidePoint(primaryReference.X + sumX / sumW, primaryReference.Y + sumY / sumW);
    }

    private sealed class Secondary(Stars.Star star, GuidePoint reference)
    {
        public Stars.Star Star { get; } = star;

        public GuidePoint Reference { get; } = reference;

        public bool Active { get; set; } = true;
    }
}
