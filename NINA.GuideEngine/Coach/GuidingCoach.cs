// SPDX-License-Identifier: MPL-2.0

using System.Globalization;
using NINA.GuideEngine.Algorithms;
using NINA.GuideEngine.Core;
using NINA.GuideEngine.Guiding;
using NINA.GuideEngine.Simulation;

namespace NINA.GuideEngine.Coach;

/// <summary>
/// The Guiding Coach (docs/COACH.md): runs sessions of camera check, drift, mount response and guided trials against a
/// <see cref="Guider"/>, builds the report card and applies recommendations through the <see cref="ICoachHost"/>.
/// Thread-safe. Status updates are published as <see cref="CoachStatusEvent"/> through <see cref="Guider.EventRaised"/>.
/// </summary>
public sealed class GuidingCoach
{
    private readonly Guider guider;
    private readonly ICoachHost host;
    private readonly object gate = new();
    private Session? session;
    private CoachStatus lastStatus = new();
    private Task completion = Task.CompletedTask;

    public GuidingCoach(Guider guider, ICoachHost host)
    {
        this.guider = guider ?? throw new ArgumentNullException(nameof(guider));
        this.host = host ?? throw new ArgumentNullException(nameof(host));
    }

    /// <summary>
    /// Token for capture loops the coach starts itself (e.g. after the camera check). Hosts leave it unset; virtual-time tests
    /// pass a deadline so the loop cannot run forever.
    /// </summary>
    public CancellationToken LoopToken { get; set; }

    /// <summary>True while a session runs.</summary>
    public bool IsRunning
    {
        get
        {
            lock (gate)
            {
                return session is not null;
            }
        }
    }

    /// <summary>Completes when the current session (and its restore of the guider state) has finished.</summary>
    public Task Completion
    {
        get
        {
            lock (gate)
            {
                return completion;
            }
        }
    }

    /// <summary>Current or last session status (Phase Idle before the first session); always carries the camera's gain range.</summary>
    public CoachStatus Status
    {
        get
        {
            Session? s;
            CoachStatus last;
            lock (gate)
            {
                s = session;
                last = lastStatus;
            }

            if (s is not null)
            {
                return s.CurrentStatus;
            }

            var (min, max, current) = GainRange();
            return last with
            {
                GainMin = min,
                GainMax = max,
                CurrentGain = current,
                CurrentExposureSeconds = guider.BaseSettings.ExposureMs / 1000.0,
            };
        }
    }

    /// <summary>
    /// Starts a session in the background. A rejection — another session runs or the guider is busy/calibrating
    /// (<see cref="CoachCodes.Busy"/>), the camera is not connected (<see cref="CoachCodes.NotConnected"/>) — is reported only
    /// in the result; the status of a running or the last session is left untouched.
    /// </summary>
    public CoachStartResult Start(CoachOptions? options = null)
    {
        options ??= new CoachOptions();
        string code;
        lock (gate)
        {
            if (session is null)
            {
                string? reason = host.IsBusy || guider.State == GuiderState.Calibrating ? CoachCodes.Busy
                    : !guider.Camera.IsConnected ? CoachCodes.NotConnected
                    : null;
                if (reason is null)
                {
                    var s = new Session(this, guider, host, Normalize(options));
                    if (guider.AttachCoach(s.OnInterrupt))
                    {
                        session = s;
                        completion = Task.Run(s.RunAsync);
                        return new CoachStartResult { Accepted = true, Status = s.CurrentStatus };
                    }

                    reason = CoachCodes.Busy;
                }

                code = reason;
            }
            else
            {
                code = CoachCodes.Busy;
            }
        }

        return new CoachStartResult
        {
            Accepted = false,
            MessageCode = code,
            Message = CoachFindings.MessageFor(code, new Dictionary<string, object?>()),
            Status = Status,
        };
    }

    /// <summary>Skips the running step (its partial results are kept when usable). False when no step runs.</summary>
    public bool SkipStep()
    {
        Session? s;
        lock (gate)
        {
            s = session;
        }

        return s?.Skip() ?? false;
    }

    /// <summary>Cancels the session: temporary settings are restored, guiding output re-enabled and guiding resumed if it was active.</summary>
    public bool Cancel()
    {
        Session? s;
        lock (gate)
        {
            s = session;
        }

        if (s is null)
        {
            return false;
        }

        s.Cancel();
        return true;
    }

    /// <summary>
    /// Applies the setting changes of findings (<see cref="CoachFinding.Id"/>, including active live hints) or trials
    /// ("trial:&lt;id&gt;") of the current/last session through the host and marks them applied.
    /// </summary>
    public bool ApplyActions(IReadOnlyList<string> ids, out string? error)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var status = Status;
        var findings = (status.Report?.Findings ?? status.Findings).Concat(status.Findings).GroupBy(f => f.Id).Select(g => g.First()).ToList();
        var hints = guider.ActiveHints;
        var changes = new Dictionary<string, CoachSettingChange>();
        var appliedFindings = new HashSet<string>();
        var appliedTrials = new HashSet<string>();
        foreach (var id in ids)
        {
            if (id.StartsWith("trial:", StringComparison.Ordinal))
            {
                var trial = status.Trials.FirstOrDefault(t => t.Id == id[6..]);
                if (trial is null)
                {
                    error = $"unknown trial '{id}'";
                    return false;
                }

                foreach (var c in trial.Settings)
                {
                    changes[c.Name] = c;
                }

                appliedTrials.Add(trial.Id);
                continue;
            }

            var f = findings.FirstOrDefault(x => x.Id == id) ?? hints.FirstOrDefault(x => x.Id == id);
            if (f is null)
            {
                error = $"unknown finding '{id}'";
                return false;
            }

            foreach (var c in f.Changes)
            {
                changes[c.Name] = c;
            }

            appliedFindings.Add(f.Id);
        }

        if (changes.Count == 0)
        {
            error = "nothing to apply";
            return false;
        }

        if (!host.ApplySettings(changes.Values.ToList(), out error))
        {
            return false;
        }

        foreach (var hint in hints.Where(h => appliedFindings.Contains(h.Id)))
        {
            // an applied hint has done its job
            guider.DismissHint(hint.Id);
        }

        CoachFinding Mark(CoachFinding f) => appliedFindings.Contains(f.Id) ? f with { Applied = true } : f;
        CoachTrial MarkTrial(CoachTrial t) => appliedTrials.Contains(t.Id) ? t with { Applied = true } : t;
        CoachReport? report = null;
        lock (gate)
        {
            if (session is { } running)
            {
                running.MarkApplied(appliedFindings, appliedTrials);
            }
            else
            {
                report = lastStatus.Report is { } r ? r with { Findings = r.Findings.Select(Mark).ToList(), Trials = r.Trials.Select(MarkTrial).ToList() } : null;
                lastStatus = lastStatus with
                {
                    Findings = lastStatus.Findings.Select(Mark).ToList(),
                    Trials = lastStatus.Trials.Select(MarkTrial).ToList(),
                    Report = report,
                };
            }
        }

        if (report is not null)
        {
            try
            {
                host.SaveReport(report);
            }
            catch
            {
                // the settings are applied; the stored report just misses the flags
            }
        }

        guider.PublishEvent(new CoachStatusEvent(guider.Clock.UtcNow, Status));
        return true;
    }

    /// <summary>Stored reports, newest first, without the raw samples.</summary>
    public IReadOnlyList<CoachReport> GetHistory(int max) => host.LoadReports(max);

    private (int? Min, int? Max, int? Current) GainRange()
    {
        var range = host.GainRange ?? guider.Camera as IGainRange;
        int? current = guider.BaseSettings.Gain ?? range?.CurrentGain;
        return (range?.GainMin, range?.GainMax, current);
    }

    private static CoachOptions Normalize(CoachOptions o)
    {
        var steps = o.Steps.Count == 0
            ? CoachStepNames.All
            : CoachStepNames.All.Where(s => o.Steps.Contains(s, StringComparer.OrdinalIgnoreCase)).ToList();
        return o with
        {
            Steps = steps,
            ExposureSeconds = o.ExposureSeconds.Where(e => e > 0 && double.IsFinite(e)).Distinct().ToList(),
            FramesPerCombination = Math.Clamp(o.FramesPerCombination, 3, 50),
            DriftSeconds = Math.Max(120, double.IsFinite(o.DriftSeconds) ? o.DriftSeconds : 180),
            TrialSeconds = Math.Max(30, double.IsFinite(o.TrialSeconds) ? o.TrialSeconds : 120),
        };
    }

    private void OnSessionEnded(Session s, CoachStatus final)
    {
        lock (gate)
        {
            if (ReferenceEquals(session, s))
            {
                session = null;
                lastStatus = final;
            }
        }
    }

    /// <summary>
    /// The backlash settings suggested for a measured Dec backlash of <paramref name="backlashMs"/> (guiding reversals)
    /// and <paramref name="largeMoveBacklashMs"/> (after a long move); <paramref name="fits"/>: Dec guide mode Auto and
    /// drift.decGuideMode not raised. Adds playModel or playModelNeedsPredictive (true, for the UI's notes) and playReason
    /// (soft|large) to <paramref name="parameters"/>.
    /// </summary>
    internal static List<(string Name, string Value)> BacklashSuggestion(double backlashMs, double? largeMoveBacklashMs, bool fits,
        GuideAlgorithmKind decAlgorithm, IDictionary<string, object?> parameters)
    {
        if (!fits || backlashMs < 100)
        {
            return [];
        }

        string? reason = PlayModelReason(backlashMs, largeMoveBacklashMs);
        if (reason is not null && decAlgorithm == GuideAlgorithmKind.Predictive)
        {
            // a play that depends on the preceding move, or one too large for a single pulse, defeats a fixed pulse: the
            // play model tracks the gear inside it (Guider.UsesPlayModel), the pulse only seeds its prior (sizes up to 8 s)
            parameters["playModel"] = true;
            parameters["playReason"] = reason;
            return
            [
                (CoachSettingNames.BacklashCompensation, "true"),
                (CoachSettingNames.BacklashModel, nameof(BacklashModel.PlayModel)),
                (CoachSettingNames.BacklashPulseMs, CoachSettingsMap.Format(Floor10(Math.Min(backlashMs, PlayModelBacklash.MaxCompensationMs)))),
            ];
        }

        if (reason is not null)
        {
            // the play model would fit, but PHD2's algorithms don't learn what a pulse really did; noted above the fixed
            // pulse's range too, where the play model is the only compensation left
            parameters["playReason"] = reason;
            parameters["playModelNeedsPredictive"] = true;
        }

        if (backlashMs > 3000)
        {
            return [];
        }

        return
        [
            (CoachSettingNames.BacklashCompensation, "true"),
            (CoachSettingNames.BacklashPulseMs, CoachSettingsMap.Format(Floor10(backlashMs))),
        ];
    }

    // Why a fixed pulse won't fit a measured Dec backlash (PLAY-MODEL-BACKLASH.md): "soft" when a long move loses at least
    // 1.5× what a guiding reversal loses (the play depends on the preceding move), "large" from 1 s (more play than a
    // pulse should cross at once: the play model compensates per pulse up to the max Dec pulse and knows what is left);
    // null for a small hard play, which the fixed pulse handles
    internal static string? PlayModelReason(double backlashMs, double? largeMoveBacklashMs)
    {
        if (largeMoveBacklashMs is { } large && large >= 1.5 * backlashMs)
        {
            return "soft";
        }

        return backlashMs >= 1000 ? "large" : null;
    }

    private static int Floor10(double ms) => (int)(Math.Floor(ms / 10) * 10);

    /// <summary>A step could not run; becomes a problem finding and a failed step.</summary>
    private sealed class StepFailedException(string code, string? detail = null, Dictionary<string, object?>? parameters = null) : Exception(detail ?? code)
    {
        public string Code { get; } = code;

        public Dictionary<string, object?> Parameters { get; } = parameters ?? new();
    }

    private sealed class StepState(string name, double estimated)
    {
        public string Name { get; } = name;

        public string State { get; set; } = CoachStepStates.Pending;

        public string? Detail { get; set; }

        public string? DetailCode { get; set; }

        public Dictionary<string, object?> DetailParameters { get; set; } = new();

        public double Progress { get; set; }

        public DateTimeOffset? Started { get; set; }

        public double Elapsed { get; set; }

        public double Estimated { get; set; } = estimated;

        public string? Message { get; set; }

        public string? Code { get; set; }

        public Dictionary<string, object?> MessageParameters { get; set; } = new();
    }

    private sealed class Session
    {
        private const double LostTimeoutSec = 30;
        private static readonly SettleParams QuickSettle = new(1000, 0, 60, 1);

        private readonly GuidingCoach coach;
        private readonly Guider guider;
        private readonly ICoachHost host;
        private readonly CoachOptions options;
        private readonly IClock clock;
        private readonly CancellationTokenSource cts = new();
        private readonly object gate = new();
        private readonly string id;
        private readonly DateTimeOffset startedAt;
        private readonly List<StepState> steps;
        private readonly List<CoachFinding> findings = [];
        private readonly List<CoachCameraResult> cameraResults = [];
        private readonly List<CoachTrial> trials = [];
        private readonly Dictionary<string, double> effectiveFrames = [];
        private readonly Dictionary<string, double> cloudNoise = [];

        private CancellationTokenSource? stepCts;
        private volatile string? interruptReason;
        private StepState? current;
        private string? currentName;
        private bool calibrating;
        private bool calibrationFailed;
        private CoachCameraResult? cameraRecommended;
        private DriftAnalysis? driftAnalysis;
        private CoachDrift? drift;
        private CoachResponse? response;
        private CoachReport? report;
        private string phase = CoachPhases.Running;
        private string? message;
        private string? messageCode;
        private Dictionary<string, object?> messageParameters = new();
        private IReadOnlyList<CoachSettingChange>? cameraChanges;
        private DriftMeasurement? driftHook;
        private MountResponseProcedure? responseHook;
        private TrialObserver? trialHook;
        private int trialIndex = -1;
        private volatile CoachStatus status = new();

        // guider state at the start
        private readonly bool wasCapturing;
        private readonly bool wasGuiding;
        private readonly bool wasPaused;
        private readonly (double Total, double Ra, double Dec)? windowRms;
        private readonly double? windowOscillation;

        public Session(GuidingCoach coach, Guider guider, ICoachHost host, CoachOptions options)
        {
            this.coach = coach;
            this.guider = guider;
            this.host = host;
            this.options = options;
            clock = guider.Clock;
            startedAt = clock.UtcNow;
            id = startedAt.UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..6];
            var st = guider.State;
            wasCapturing = guider.IsLoopRunning && st.IsCapturing();
            wasGuiding = st.IsGuidingActive();
            wasPaused = st == GuiderState.Paused;
            if (wasGuiding && guider.Statistics?.Window is { IncludedFrames: >= 20 } w)
            {
                windowRms = (w.RmsTotalArcsec, w.RmsRaArcsec, w.RmsDecArcsec);
                windowOscillation = w.OscillationIndex;
            }

            steps = options.Steps.Select(s => new StepState(s, Estimate(s))).ToList();
            status = BuildStatus();
        }

        public CoachStatus CurrentStatus => status;

        public void OnInterrupt(string reason)
        {
            interruptReason = reason;
            Cancel();
        }

        public void Cancel()
        {
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        public bool Skip()
        {
            lock (gate)
            {
                if (stepCts is null)
                {
                    return false;
                }

                stepCts.Cancel();
                return true;
            }
        }

        public void MarkApplied(HashSet<string> findingIds, HashSet<string> trialIds)
        {
            lock (gate)
            {
                for (int i = 0; i < findings.Count; i++)
                {
                    if (findingIds.Contains(findings[i].Id))
                    {
                        findings[i] = findings[i] with { Applied = true };
                    }
                }

                for (int i = 0; i < trials.Count; i++)
                {
                    if (trialIds.Contains(trials[i].Id))
                    {
                        trials[i] = trials[i] with { Applied = true };
                    }
                }
            }

            Publish();
        }

        public async Task RunAsync()
        {
            var ct = cts.Token;
            guider.EventRaised += OnGuiderEvent;
            Publish();
            try
            {
                foreach (var step in steps)
                {
                    ct.ThrowIfCancellationRequested();
                    await RunStepAsync(step, ct).ConfigureAwait(false);
                }

                ct.ThrowIfCancellationRequested();
                lock (gate)
                {
                    currentName = CoachStepNames.Report;
                    current = null;
                }

                Publish();
                BuildReport();
                lock (gate)
                {
                    var failed = steps.FirstOrDefault(s => s.State == CoachStepStates.Failed);
                    if (steps.Count > 0 && steps.All(s => s.State == CoachStepStates.Failed))
                    {
                        phase = CoachPhases.Failed;
                        messageCode = failed!.Code;
                        message = failed.Message;
                        messageParameters = new Dictionary<string, object?>(failed.MessageParameters);
                    }
                    else
                    {
                        phase = CoachPhases.Complete;
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                lock (gate)
                {
                    string? reason = interruptReason;
                    if (reason == CoachInterruptReasons.Error)
                    {
                        phase = CoachPhases.Failed;
                        messageCode = CoachCodes.Internal;
                        message = CoachFindings.MessageFor(CoachCodes.Internal, new Dictionary<string, object?>());
                    }
                    else if (reason is not null)
                    {
                        phase = CoachPhases.Cancelled;
                        messageCode = CoachCodes.Interrupted;
                        var p = new Dictionary<string, object?> { ["reason"] = reason };
                        messageParameters = p;
                        message = CoachFindings.MessageFor(CoachCodes.Interrupted, p);
                        findings.Add(CoachFindings.Create(CoachCodes.Interrupted, current?.Name ?? currentName ?? CoachStepNames.Report, CoachSeverities.Problem,
                            Now(), p));
                    }
                    else
                    {
                        phase = CoachPhases.Cancelled;
                        message = "Cancelled.";
                    }
                }
            }
            catch (Exception ex)
            {
                lock (gate)
                {
                    phase = CoachPhases.Failed;
                    messageCode = CoachCodes.Internal;
                    message = ex.Message;
                }
            }
            finally
            {
                await FinishAsync().ConfigureAwait(false);
            }
        }

        private async Task FinishAsync()
        {
            guider.EventRaised -= OnGuiderEvent;
            guider.DetachCoach();
            try
            {
                await OnLoopAsync(guider.ClearCoachStateAsync()).ConfigureAwait(false);
            }
            catch
            {
                // restore is best effort; the guider also clears on interrupts
            }

            lock (gate)
            {
                foreach (var s in steps.Where(s => s.State is CoachStepStates.Running or CoachStepStates.Pending))
                {
                    s.State = phase == CoachPhases.Complete ? CoachStepStates.Skipped : s.State == CoachStepStates.Running ? CoachStepStates.Failed : CoachStepStates.Skipped;
                    if (s.State == CoachStepStates.Failed)
                    {
                        s.Message = message;
                        s.Code = messageCode;
                        s.MessageParameters = new Dictionary<string, object?>(messageParameters);
                    }
                }

                for (int i = 0; i < trials.Count; i++)
                {
                    if (trials[i].State is CoachTrialStates.Pending or CoachTrialStates.Settling or CoachTrialStates.Running)
                    {
                        trials[i] = trials[i] with { State = CoachTrialStates.Skipped };
                    }
                }

                currentName = null;
                current = null;
            }

            if (report is not null)
            {
                try
                {
                    host.SaveReport(report);
                }
                catch
                {
                    // storage problems must not break the session
                }

                if (report.SeeingArcsec is { } seeing)
                {
                    guider.LiveHints.SeeingFloorArcsec = seeing;
                }
            }

            var final = BuildStatus();
            status = final;
            coach.OnSessionEnded(this, final);
            guider.PublishEvent(new CoachStatusEvent(clock.UtcNow, final));

            try
            {
                await RestoreGuidingAsync().ConfigureAwait(false);
            }
            catch
            {
                // best effort
            }

            cts.Dispose();
        }

        /// <summary>Puts the guider back into its state at the start (unless a host command or the mount took over).</summary>
        private async Task RestoreGuidingAsync()
        {
            string? reason = interruptReason;
            if (reason is not null && reason != CoachInterruptReasons.Error)
            {
                return;
            }

            var st = guider.State;
            if (wasGuiding)
            {
                bool guidingNow = st is GuiderState.Guiding or GuiderState.LostLock or GuiderState.Reacquiring;
                if (!guidingNow)
                {
                    if (!guider.IsLoopRunning)
                    {
                        await guider.WaitForLoopAsync().ConfigureAwait(false);
                    }

                    var start = guider.StartGuidingForCoachAsync(TrialSettle(), CancellationToken.None, coach.LoopToken);
                    if (wasPaused)
                    {
                        await start.ConfigureAwait(false);
                        guider.PauseForCoach();
                    }
                }
                else if (wasPaused)
                {
                    guider.PauseForCoach();
                }
            }
            else if (!wasCapturing)
            {
                if (guider.IsLoopRunning)
                {
                    await guider.StopCaptureForCoachAsync().ConfigureAwait(false);
                }
            }
            else if (st.IsGuidingActive() || st == GuiderState.Calibrating)
            {
                guider.StopGuidingForCoach();
            }
            else if (!guider.IsLoopRunning)
            {
                guider.StartLoopingForCoach(coach.LoopToken);
            }
        }

        #region steps

        private async Task RunStepAsync(StepState step, CancellationToken sessionCt)
        {
            var linked = CancellationTokenSource.CreateLinkedTokenSource(sessionCt);
            lock (gate)
            {
                stepCts = linked;
                current = step;
                currentName = step.Name;
                step.State = CoachStepStates.Running;
                step.Started = clock.UtcNow;
            }

            Publish();
            var ct = linked.Token;
            try
            {
                switch (step.Name)
                {
                    case CoachStepNames.CameraCheck:
                        await CameraCheckAsync(step, ct, sessionCt).ConfigureAwait(false);
                        break;
                    case CoachStepNames.Drift:
                        await DriftAsync(step, ct, sessionCt).ConfigureAwait(false);
                        break;
                    case CoachStepNames.MountResponse:
                        await MountResponseAsync(step, ct, sessionCt).ConfigureAwait(false);
                        break;
                    case CoachStepNames.Trials:
                        await TrialsAsync(step, ct, sessionCt).ConfigureAwait(false);
                        break;
                }

                EndStep(step, linked.IsCancellationRequested && !sessionCt.IsCancellationRequested ? CoachStepStates.Skipped : CoachStepStates.Done);
            }
            catch (StepFailedException f)
            {
                EndStep(step, CoachStepStates.Failed, f.Code, f.Message, f.Parameters);
                var p = new Dictionary<string, object?>(f.Parameters);
                AddFinding(CoachFindings.Create(f.Code, step.Name, CoachSeverities.Problem, Now(), p, qualifier: step.Name));
            }
            catch (OperationCanceledException) when (!sessionCt.IsCancellationRequested)
            {
                EndStep(step, CoachStepStates.Skipped);
            }
            finally
            {
                lock (gate)
                {
                    stepCts = null;
                    driftHook = null;
                    responseHook = null;
                    trialHook = null;
                }

                linked.Dispose();
                if (!sessionCt.IsCancellationRequested)
                {
                    await OnLoopAsync(guider.SetCoachHookAsync(null)).ConfigureAwait(false);
                }
            }
        }

        private void EndStep(StepState step, string state, string? code = null, string? detail = null, Dictionary<string, object?>? parameters = null)
        {
            lock (gate)
            {
                step.State = state;
                step.Progress = state == CoachStepStates.Failed ? step.Progress : 1.0;
                step.Code = code;
                step.MessageParameters = parameters is null ? new() : new Dictionary<string, object?>(parameters);
                step.Message = code is null ? null : detail ?? CoachFindings.MessageFor(code, step.MessageParameters);
                step.Detail = null;
                step.DetailCode = null;
                step.DetailParameters = new();
                if (step.Started is { } s)
                {
                    step.Elapsed = (clock.UtcNow - s).TotalSeconds;
                }
            }

            Publish();
        }

        private async Task CameraCheckAsync(StepState step, CancellationToken ct, CancellationToken sessionCt)
        {
            if (guider.IsLoopRunning)
            {
                SetDetail("stopping looping", null);
                await guider.StopCaptureForCoachAsync().ConfigureAwait(false);
            }

            if (!guider.TryLeaseCamera())
            {
                sessionCt.ThrowIfCancellationRequested();
                throw new StepFailedException(CoachCodes.Busy);
            }

            var s = guider.BaseSettings;
            double scale = guider.PixelScale;
            var combos = Combinations();
            var sweep = new CameraSweep(guider.Camera, guider.Preprocessor, clock);
            try
            {
                for (int i = 0; i < combos.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var c = combos[i];
                    SetDetail(FormattableString.Invariant($"exposure {c.ExposureSeconds:0.##}s gain {(c.Gain is { } g ? g.ToString(CultureInfo.InvariantCulture) : "default")}"),
                        CoachDetailCodes.CameraCombination,
                        new() { ["exposureSeconds"] = c.ExposureSeconds, ["gain"] = c.Gain ?? -1, ["index"] = i + 1, ["total"] = combos.Count },
                        (double)i / combos.Count);
                    (IReadOnlyList<CameraFrameMeasurement> frames, int stars) measured;
                    try
                    {
                        measured = await sweep.MeasureAsync(c, options.FramesPerCombination, s.Binning, s.Offset, s.StarFinder with { PixelScale = scale },
                            s.MultiStar.MaxListSize, PublishFrame, ct, WaitForMountAsync).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        throw new StepFailedException(CoachCodes.CameraError, ex.Message);
                    }

                    var result = CameraCheckAnalyzer.Evaluate(c, measured.frames, measured.stars, scale);
                    lock (gate)
                    {
                        cameraResults.Add(result);
                        cameraRecommended = CameraCheckAnalyzer.SelectRecommended(cameraResults);
                    }

                    Publish();
                }
            }
            catch (OperationCanceledException) when (!sessionCt.IsCancellationRequested)
            {
                // skipped: evaluate what was measured
            }
            finally
            {
                guider.ReleaseCamera();
            }

            CameraFindings(combos);
        }

        private List<CameraCombination> Combinations()
        {
            var s = guider.BaseSettings;
            double currentExposure = Math.Round(s.ExposureMs / 1000.0, 3);
            var exposures = options.ExposureSeconds.Count > 0 ? options.ExposureSeconds.ToList() : [1.0, 2.0, 3.0];
            var (min, max, currentGain) = coach.GainRange();
            var gains = new List<int?>();
            if (options.Gains.Count > 0)
            {
                gains.AddRange(options.Gains.Distinct().Select(g => (int?)g));
            }
            else
            {
                gains.Add(currentGain);
                if (min is { } lo && max is { } hi && hi > lo)
                {
                    var candidates = new[] { 0.25, 0.5, 0.75 }.Select(f => (int)Math.Round(lo + f * (hi - lo))).Distinct()
                        .Where(g => g != currentGain).OrderByDescending(g => currentGain is { } c ? Math.Abs(g - c) : 0).Take(2).Order();
                    gains.AddRange(candidates.Select(g => (int?)g));
                }
            }

            var combos = new List<CameraCombination>();
            foreach (var g in gains.Distinct())
            {
                foreach (var e in exposures)
                {
                    combos.Add(new CameraCombination(e, g));
                }
            }

            // always measure the current settings so the recommendation can be compared with them
            if (!combos.Any(c => Math.Abs(c.ExposureSeconds - currentExposure) < 1e-6 && c.Gain == currentGain))
            {
                combos.Add(new CameraCombination(currentExposure, currentGain));
            }

            return combos;
        }

        private void CameraFindings(IReadOnlyList<CameraCombination> combos)
        {
            var ts = Now();
            var s = guider.BaseSettings;
            double scale = guider.PixelScale;
            double currentExposure = Math.Round(s.ExposureMs / 1000.0, 3);
            var (_, _, currentGain) = coach.GainRange();
            List<CoachCameraResult> results;
            lock (gate)
            {
                results = cameraResults.ToList();
            }

            // the current settings stay unless another combination is justified (not feasible, few stars, significantly steadier)
            var cur = results.FirstOrDefault(r => Math.Abs(r.ExposureSeconds - currentExposure) < 1e-6 && r.Gain == (currentGain ?? -1));
            var rec = CameraCheckAnalyzer.Recommend(results, cur);
            lock (gate)
            {
                cameraRecommended = rec;
            }
            if (rec is null)
            {
                if (results.Count > 0)
                {
                    AddFinding(CoachFindings.Create(CoachCodes.CameraNoFeasible, CoachStepNames.CameraCheck, CoachSeverities.Problem, ts,
                        new() { ["reason"] = CameraCheckAnalyzer.DominantReason(results) }));
                }
            }
            else
            {
                bool recIsCurrent = cur is not null && ReferenceEquals(rec, cur);
                if (recIsCurrent)
                {
                    AddFinding(CoachFindings.Create(CoachCodes.CameraGood, CoachStepNames.CameraCheck, CoachSeverities.Good, ts,
                        new() { ["snr"] = rec.Snr, ["jitterArcsec"] = rec.JitterArcsec }));
                }
                else
                {
                    var changes = CameraChanges(rec, currentExposure, currentGain);
                    double? impact = cur is { Feasible: true, JitterArcsec: { } cj } && rec.JitterArcsec is { } rj ? cj - rj : null;
                    AddFinding(CoachFindings.Create(CoachCodes.CameraRecommendation, CoachStepNames.CameraCheck, CoachSeverities.Info, ts,
                        new()
                        {
                            ["exposureSeconds"] = rec.ExposureSeconds,
                            ["gain"] = rec.Gain,
                            ["jitterArcsec"] = rec.JitterArcsec,
                            ["snr"] = rec.Snr,
                            ["stars"] = rec.Stars,
                            ["currentJitterArcsec"] = cur?.JitterArcsec,
                        }, impact, changes));
                    cameraChanges = changes;
                }

                if (rec.Snr is { } snr && snr < 25)
                {
                    double? noise = CameraCheckAnalyzer.CentroidNoiseArcsec(rec.Hfd, rec.Snr, scale);
                    AddFinding(CoachFindings.Create(CoachCodes.CameraSnrLow, CoachStepNames.CameraCheck, CoachSeverities.Warning, ts,
                        new() { ["snr"] = snr, ["noiseArcsec"] = noise is { } n ? Math.Round(n, 3) : null }, noise * 0.5));
                }

                if (rec.Hfd is { } hfd && CameraCheckAnalyzer.IsDefocused(hfd, scale))
                {
                    AddFinding(CoachFindings.Create(CoachCodes.CameraDefocused, CoachStepNames.CameraCheck, CoachSeverities.Warning, ts,
                        new() { ["hfdPx"] = Math.Round(hfd, 2), ["hfdArcsec"] = Math.Round(hfd * scale, 2) },
                        CameraCheckAnalyzer.CentroidNoiseArcsec(rec.Hfd, rec.Snr, scale) * 0.3));
                }

                if (rec.Stars < 3)
                {
                    AddFinding(CoachFindings.Create(CoachCodes.CameraFewStars, CoachStepNames.CameraCheck, CoachSeverities.Info, ts, new() { ["stars"] = rec.Stars }));
                }
            }

            if (cur is { Saturated: true })
            {
                AddFinding(CoachFindings.Create(CoachCodes.CameraSaturated, CoachStepNames.CameraCheck, CoachSeverities.Warning, ts,
                    new() { ["exposureSeconds"] = cur.ExposureSeconds, ["gain"] = cur.Gain }, null,
                    rec is null ? [] : CameraChanges(rec, currentExposure, currentGain)));
            }

            if (guider.Preprocessor.Darks is null && guider.Preprocessor.DefectMap is null)
            {
                AddFinding(CoachFindings.Create(CoachCodes.CameraNoDarks, CoachStepNames.CameraCheck, CoachSeverities.Info, ts));
            }
        }

        private List<CoachSettingChange> CameraChanges(CoachCameraResult rec, double currentExposure, int? currentGain)
        {
            var changes = new List<CoachSettingChange>();
            if (Math.Abs(rec.ExposureSeconds - currentExposure) > 1e-6)
            {
                changes.Add(Change(CoachSettingNames.ExposureSeconds, CoachSettingsMap.Format(rec.ExposureSeconds)));
            }

            if (rec.Gain >= 0 && rec.Gain != (currentGain ?? -1))
            {
                changes.Add(Change(CoachSettingNames.Gain, CoachSettingsMap.Format(rec.Gain)));
            }

            return changes;
        }

        private async Task DriftAsync(StepState step, CancellationToken ct, CancellationToken sessionCt)
        {
            await ApplyOverlayAsync(cameraChanges).ConfigureAwait(false);
            await EnsureGuidingAsync(ct).ConfigureAwait(false);
            SetDetail("measuring drift", CoachDetailCodes.DriftMeasuring);
            var hook = new DriftMeasurement(options.DriftSeconds, LostTimeoutSec, h =>
            {
                lock (gate)
                {
                    step.Progress = Math.Min(0.99, h.ElapsedSeconds / h.TargetSeconds);
                }

                Publish();
            });
            lock (gate)
            {
                driftHook = hook;
            }

            await OnLoopAsync(guider.SetCoachHookAsync(hook)).ConfigureAwait(false);
            var result = await WaitHookAsync(hook.Completion, hook.Stop, ct, sessionCt).ConfigureAwait(false);
            await OnLoopAsync(guider.SetCoachHookAsync(null)).ConfigureAwait(false);

            var s = guider.BaseSettings;
            double smart = MinMove.SmartDefault(s.FocalLengthMm, s.PixelSizeUm > 0 ? s.PixelSizeUm : guider.Camera.PixelSizeUm, s.Binning);
            var analysis = DriftAnalyzer.Analyze(result.Samples, result.ExposureSeconds, result.PixelScale, host.DeclinationDeg ?? result.DeclinationDeg, smart);
            var dto = DriftDto(result, analysis, final: true);
            lock (gate)
            {
                // partial results stay visible; findings and later steps only use complete measurements
                drift = dto;
            }

            if (result.StarLost || analysis is null)
            {
                throw new StepFailedException(result.StarLost ? CoachCodes.StarLost : CoachCodes.NoStar);
            }

            lock (gate)
            {
                driftAnalysis = analysis;
            }

            DriftFindings(analysis, guider.BaseSettings.ExposureMs / 1000.0);
        }

        // the RA periodic-error curve the Predictive algorithm learned or restored, on the sky at this target; also one too
        // small to predict while it is stable (an unstable one's amplitude is mostly the mount's wander); null without one.
        // From the algorithm's snapshot: it runs on the guide loop
        private PeriodicErrorState? LearnedPeriodicError() =>
            guider.RaAlgorithm is PredictiveAlgorithm { State.PeriodicError: { AmplitudeInArcsec: true, Amplitude: > 0, PeriodSeconds: > 0 } pe }
            && (pe.Phase is PeriodicErrorPhase.Ready or PeriodicErrorPhase.Predicting || (pe.Phase == PeriodicErrorPhase.Negligible && pe.Stable))
                ? pe
                : null;

        private CoachDrift DriftDto(DriftMeasurementResult? raw, DriftAnalysis? a, bool final, DriftMeasurement? live = null)
        {
            double scale = raw?.PixelScale ?? live?.PixelScale ?? guider.PixelScale;
            var samples = raw?.Samples ?? live?.Samples ?? [];
            return new CoachDrift
            {
                ElapsedSeconds = Math.Round(raw?.ElapsedSeconds ?? live?.ElapsedSeconds ?? 0, 1),
                TargetSeconds = options.DriftSeconds,
                Samples = samples.Select(x => new CoachSample(Math.Round(x.T, 2), Math.Round((x.RaPx - samples[0].RaPx) * scale, 3),
                    Math.Round((x.DecPx - samples[0].DecPx) * scale, 3))).ToList(),
                SnrAvg = R(a?.SnrAvg, 1),
                SeeingRaArcsec = R(a?.SeeingRaArcsec),
                SeeingDecArcsec = R(a?.SeeingDecArcsec),
                SeeingTotalArcsec = R(a?.SeeingTotalArcsec),
                RaPeakToPeakArcsec = R(a?.RaPeakToPeakArcsec),
                RaMaxRateArcsecPerSec = R(a?.RaMaxRateArcsecPerSec, 4),
                RaDriftArcsecPerMin = R(a?.RaDriftArcsecPerMin),
                DecDriftArcsecPerMin = R(a?.DecDriftArcsecPerMin),
                PeriodicErrorPeriodSeconds = R(a?.PeriodSeconds, 1),
                PeriodicErrorAmplitudeArcsec = R(a?.PeriodicAmplitudeArcsec),
                PeriodicErrorPhaseRad = R(a?.PeriodicPhaseRad, 4),
                PeriodicErrorOffsetArcsec = R(a?.PeriodicOffsetArcsec, 4),
                PolarAlignmentErrorArcmin = R(a?.PolarAlignmentErrorArcmin, 2),
                DeclinationAssumed = a?.DeclinationAssumed ?? false,
                DriftLimitingExposureSeconds = R(a?.DriftLimitingExposureSeconds, 2),
                GustPercent = R(a?.GustPercent, 2),
            };
        }

        private void DriftFindings(DriftAnalysis a, double exposureSeconds)
        {
            var ts = Now();
            double seeing = a.SeeingTotalArcsec;
            string level = seeing < 0.7 ? "good" : seeing < 1.3 ? "average" : "poor";
            AddFinding(CoachFindings.Create(CoachCodes.DriftSeeing, CoachStepNames.Drift,
                level == "good" ? CoachSeverities.Good : level == "average" ? CoachSeverities.Info : CoachSeverities.Warning, ts,
                new() { ["rmsArcsec"] = Math.Round(seeing, 3), ["level"] = level }));

            AddFinding(CoachFindings.Create(CoachCodes.DriftMinMove, CoachStepNames.Drift, CoachSeverities.Info, ts,
                new() { ["raPx"] = a.MinMoveRaPx, ["decPx"] = a.MinMoveDecPx }, null,
                [Change(CoachSettingNames.RaMinMove, CoachSettingsMap.Format(a.MinMoveRaPx)), Change(CoachSettingNames.DecMinMove, CoachSettingsMap.Format(a.MinMoveDecPx))]));

            // the worm curve Predictive guiding learned beats a drift measurement that is usually shorter than a worm turn
            var learned = LearnedPeriodicError();
            double peAmplitude = learned?.Amplitude ?? a.PeriodicAmplitudeArcsec;
            double? pePeriod = learned?.PeriodSeconds ?? a.PeriodSeconds;
            double peRate = learned?.PeriodSeconds is { } lp ? Math.Max(a.RaMaxRateArcsecPerSec, 2 * Math.PI * peAmplitude / lp) : a.RaMaxRateArcsecPerSec;
            bool peLarge = peAmplitude > Math.Max(3.0, 2 * a.SeeingRaArcsec);
            AddFinding(CoachFindings.Create(CoachCodes.DriftPeriodicError, CoachStepNames.Drift, peLarge ? CoachSeverities.Warning : CoachSeverities.Info, ts,
                new()
                {
                    ["amplitudeArcsec"] = Math.Round(peAmplitude, 3),
                    ["periodSeconds"] = pePeriod is { } p ? Math.Round(p, 1) : null,
                    ["maxRateArcsecPerSec"] = Math.Round(peRate, 4),
                    ["learned"] = learned is not null,
                    ["wormTeeth"] = learned?.Teeth,
                    ["predicting"] = learned?.Phase == PeriodicErrorPhase.Predicting,
                }, peLarge ? peRate * exposureSeconds / 2 : null));

            if (a.DriftLimitingExposureSeconds is { } limit)
            {
                bool tooLong = exposureSeconds > limit * 1.05;
                double suggested = Math.Max(0.5, Math.Floor(limit * 2) / 2);
                AddFinding(CoachFindings.Create(CoachCodes.DriftExposureLimit, CoachStepNames.Drift, tooLong ? CoachSeverities.Warning : CoachSeverities.Good, ts,
                    new() { ["seconds"] = Math.Round(limit, 2), ["currentSeconds"] = Math.Round(exposureSeconds, 2) },
                    tooLong ? (exposureSeconds - limit) * a.RaMaxRateArcsecPerSec / 2 : null,
                    tooLong ? [Change(CoachSettingNames.ExposureSeconds, CoachSettingsMap.Format(suggested))] : []));
            }

            double pae = a.PolarAlignmentErrorArcmin;
            string paSeverity = pae < 3 ? CoachSeverities.Good : pae < 5 ? CoachSeverities.Info : pae < 10 ? CoachSeverities.Warning : CoachSeverities.Problem;
            AddFinding(CoachFindings.Create(CoachCodes.DriftPolarAlignment, CoachStepNames.Drift, paSeverity, ts,
                new() { ["arcmin"] = Math.Round(pae, 2), ["decAssumed"] = a.DeclinationAssumed, ["driftArcsecPerMin"] = Math.Round(a.DecDriftArcsecPerMin, 3) },
                pae >= 5 ? Math.Abs(a.DecDriftArcsecPerMin) / 60 * exposureSeconds / 2 + 0.02 * pae : null));

            if (a.GustPercent > 2)
            {
                AddFinding(CoachFindings.Create(CoachCodes.DriftWind, CoachStepNames.Drift, CoachSeverities.Warning, ts,
                    new() { ["gustPercent"] = Math.Round(a.GustPercent, 1) }, null,
                    [Change(CoachSettingNames.RaMinMove, CoachSettingsMap.Format(Math.Round(a.MinMoveRaPx * 1.3, 2))),
                        Change(CoachSettingNames.DecMinMove, CoachSettingsMap.Format(Math.Round(a.MinMoveDecPx * 1.3, 2)))]));
            }
        }

        private async Task MountResponseAsync(StepState step, CancellationToken ct, CancellationToken sessionCt)
        {
            await ApplyOverlayAsync(cameraChanges).ConfigureAwait(false);
            await EnsureGuidingAsync(ct).ConfigureAwait(false);
            DriftAnalysis? a;
            lock (gate)
            {
                a = driftAnalysis;
            }

            var hook = new MountResponseProcedure(a?.RaDriftPxPerSec ?? 0, a?.DecDriftPxPerSec ?? 0, lostTimeoutSec: LostTimeoutSec,
                seeingRaPx: a?.SeeingRaPx, seeingDecPx: a?.SeeingDecPx, onProgress: h =>
            {
                lock (gate)
                {
                    step.Progress = h.Progress;
                    (step.Detail, step.DetailCode, step.DetailParameters) = h.Phase switch
                    {
                        "backlash" => ("measuring Dec backlash", CoachDetailCodes.ResponseBacklash, new Dictionary<string, object?>()),
                        "recenter" => ("recentering", (string?)null, new Dictionary<string, object?>()),
                        _ => h.CurrentPulse is { } cp
                            ? ($"{cp.Direction} {cp.DurationMs} ms pulses", CoachDetailCodes.ResponsePulses,
                                new Dictionary<string, object?> { ["direction"] = cp.Direction.ToString(), ["ms"] = cp.DurationMs })
                            : ("pulses", CoachDetailCodes.ResponsePulses, new Dictionary<string, object?>()),
                    };
                }

                Publish();
            });
            lock (gate)
            {
                responseHook = hook;
            }

            await OnLoopAsync(guider.SetCoachHookAsync(hook)).ConfigureAwait(false);
            var result = await WaitHookAsync(hook.Completion, hook.Skip, ct, sessionCt, waitForFrame: true).ConfigureAwait(false);
            await OnLoopAsync(guider.SetCoachHookAsync(null)).ConfigureAwait(false);
            lock (gate)
            {
                response = result.Response;
            }

            if (result.StarLost)
            {
                throw new StepFailedException(CoachCodes.StarLost);
            }

            ResponseFindings(result);
            DecGuideModeFinding();
        }

        private void ResponseFindings(MountResponseResult result)
        {
            var r = result.Response;
            var ts = Now();
            bool warnings = false;
            double scale = guider.PixelScale;
            DriftAnalysis? a;
            lock (gate)
            {
                a = driftAnalysis;
            }

            var s = guider.BaseSettings;
            double smart = MinMove.SmartDefault(s.FocalLengthMm, s.PixelSizeUm > 0 ? s.PixelSizeUm : guider.Camera.PixelSizeUm, s.Binning);
            if (r.BacklashState == CoachBacklashStates.Measured && r.BacklashMs is { } ms)
            {
                // ms is the backlash guiding meets (reversal test), the large-move value only its upper bound. The compensation
                // works only while both directions are guided (in Auto, as in PHD2), and one-way guiding along the drift is
                // suggested instead when that fits (drift.decGuideMode)
                string severity = ms < 100 ? CoachSeverities.Good : ms < 1000 ? CoachSeverities.Info : CoachSeverities.Warning;
                var parameters = new Dictionary<string, object?>
                {
                    ["ms"] = ms,
                    ["arcsec"] = r.BacklashArcsec,
                    ["largeMoveMs"] = r.LargeMoveBacklashMs,
                    ["largeMoveArcsec"] = r.LargeMoveBacklashArcsec,
                };

                bool fits = s.DecGuideMode == DecGuideMode.Auto && !SuggestsDriftMode(a, r);
                var changes = BacklashSuggestion(ms, r.LargeMoveBacklashMs, fits, s.DecAlgorithm.Kind, parameters)
                    .Select(c => Change(c.Name, c.Value)).ToArray();
                AddFinding(CoachFindings.Create(CoachCodes.ResponseDecBacklash, CoachStepNames.MountResponse, severity, ts,
                    parameters, ms >= 100 ? Math.Min(1.0, (r.BacklashArcsec ?? 0) * 0.25) : null, changes));
                warnings |= severity == CoachSeverities.Warning;
            }
            else if (r.BacklashState == CoachBacklashStates.None)
            {
                AddFinding(CoachFindings.Create(CoachCodes.ResponseDecBacklash, CoachStepNames.MountResponse, CoachSeverities.Good, ts,
                    new() { ["ms"] = r.BacklashMs ?? 0, ["arcsec"] = r.BacklashArcsec ?? 0, ["largeMoveMs"] = r.LargeMoveBacklashMs ?? 0,
                        ["largeMoveArcsec"] = r.LargeMoveBacklashArcsec ?? 0 }));
            }

            // stiction only when short pulses were conclusively ineffective (expected move ≥ 3 σ of the measurement)
            foreach (var (axis, stiction, rate, seeingMinMove) in new[]
            {
                ("Ra", result.StictionRaMs, result.RaRatePxPerMs, a?.MinMoveRaPx ?? smart),
                ("Dec", result.StictionDecMs, result.DecRatePxPerMs, a?.MinMoveDecPx ?? smart),
            })
            {
                if (stiction is not { } mp)
                {
                    continue;
                }

                // min-move so that corrections reach the effective pulse at the calibrated rate, but never far above the seeing
                double cap = Math.Max(2 * seeingMinMove, 0.6);
                double minMove = Math.Round(Math.Min(Math.Ceiling(mp * rate / 0.05 - 1e-9) * 0.05, cap), 2);
                var changes = minMove > 0
                    ? new[] { Change(axis == "Ra" ? CoachSettingNames.RaMinMove : CoachSettingNames.DecMinMove, CoachSettingsMap.Format(minMove)) }
                    : [];
                AddFinding(CoachFindings.Create(CoachCodes.ResponseMinPulse, CoachStepNames.MountResponse, CoachSeverities.Warning, ts,
                    new() { ["axis"] = axis, ["ms"] = mp }, minMove * scale * 0.2, changes, qualifier: axis));
                warnings = true;
            }

            foreach (var (axis, ratio) in new[] { ("Ra", r.AsymmetryRa), ("Dec", r.AsymmetryDec) })
            {
                if (ratio is { } q && (q < 0.7 || q > 1 / 0.7))
                {
                    AddFinding(CoachFindings.Create(CoachCodes.ResponseAsymmetry, CoachStepNames.MountResponse, CoachSeverities.Warning, ts,
                        new() { ["axis"] = axis, ["ratio"] = Math.Round(q, 2) }, qualifier: axis));
                    warnings = true;
                }
            }

            foreach (var (axis, ratio) in new[] { ("Ra", r.RateRatioRa), ("Dec", r.RateRatioDec) })
            {
                if (ratio is { } q && Math.Abs(q - 1) > 0.2)
                {
                    AddFinding(CoachFindings.Create(CoachCodes.ResponseRateMismatch, CoachStepNames.MountResponse, CoachSeverities.Warning, ts,
                        new() { ["axis"] = axis, ["ratio"] = Math.Round(q, 2) }, qualifier: axis));
                    warnings = true;
                }
            }

            if (!warnings && r.Pulses.Count > 0)
            {
                AddFinding(CoachFindings.Create(CoachCodes.ResponseGood, CoachStepNames.MountResponse, CoachSeverities.Good, ts));
            }
        }

        /// <summary>
        /// drift.decGuideMode: large backlash and a one-sided Dec drift → guide Dec in one direction only, with the Drift mode,
        /// which picks the direction from the drift it measures while guiding and follows it when it reverses (a fixed North
        /// or South would be wrong after a meridian flip, on another target or with a new polar alignment).
        /// </summary>
        private void DecGuideModeFinding()
        {
            DriftAnalysis? a;
            CoachResponse? r;
            lock (gate)
            {
                a = driftAnalysis;
                r = response;
            }

            if (!SuggestsDriftMode(a, r))
            {
                return;
            }

            // the backlash compensation works only while both directions are guided (as with PHD2's North and South): Drift
            // goes without it
            string mode = nameof(DecGuideMode.Drift);
            var changes = new List<CoachSettingChange> { Change(CoachSettingNames.DecGuideMode, mode) };
            if (guider.BaseSettings.Backlash.Enabled)
            {
                changes.Add(Change(CoachSettingNames.BacklashCompensation, "false"));
            }

            AddFinding(CoachFindings.Create(CoachCodes.DriftDecGuideMode, CoachStepNames.Drift, CoachSeverities.Info, Now(),
                new() { ["mode"] = mode, ["driftArcsecPerMin"] = Math.Round(a!.DecDriftArcsecPerMin, 3), ["backlashMs"] = r!.BacklashMs }, null, changes));
        }

        // drift.decGuideMode fits: large backlash (1 s or more), a Dec drift of 0.5″/min or more, and Dec not in Drift yet
        private bool SuggestsDriftMode(DriftAnalysis? a, CoachResponse? r) =>
            a is not null && r is { BacklashState: CoachBacklashStates.Measured, BacklashMs: >= 1000 } && Math.Abs(a.DecDriftArcsecPerMin) >= 0.5
            && guider.BaseSettings.DecGuideMode != DecGuideMode.Drift;

        private async Task TrialsAsync(StepState step, CancellationToken ct, CancellationToken sessionCt)
        {
            var sets = TrialSets();
            lock (gate)
            {
                trials.AddRange(sets);
            }

            Publish();
            for (int i = 0; i < trials.Count; i++)
            {
                if (ct.IsCancellationRequested)
                {
                    break;
                }

                CoachTrial trial;
                lock (gate)
                {
                    trial = trials[i];
                    trialIndex = i;
                }

                if (trial.State == CoachTrialStates.Skipped)
                {
                    continue;
                }

                try
                {
                    await RunTrialAsync(step, i, trial, ct, sessionCt).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!sessionCt.IsCancellationRequested)
                {
                    UpdateTrial(i, t => t with { State = CoachTrialStates.Skipped });
                    break;
                }
                catch (StepFailedException f)
                {
                    UpdateTrial(i, t => t with { State = CoachTrialStates.Failed });
                    if (f.Code is CoachCodes.CalibrationFailed or CoachCodes.NoCalibration or CoachCodes.NoPulseOutput or CoachCodes.NoStar)
                    {
                        throw;
                    }
                }

                lock (gate)
                {
                    step.Progress = (i + 1.0) / trials.Count;
                }
            }

            lock (gate)
            {
                for (int i = 0; i < trials.Count; i++)
                {
                    if (trials[i].State is CoachTrialStates.Pending)
                    {
                        trials[i] = trials[i] with { State = CoachTrialStates.Skipped };
                    }
                }

                trialIndex = -1;
            }

            await ApplyOverlayAsync(null).ConfigureAwait(false);
            TrialFindings();
        }

        private async Task RunTrialAsync(StepState step, int index, CoachTrial trial, CancellationToken ct, CancellationToken sessionCt)
        {
            UpdateTrial(index, t => t with { State = CoachTrialStates.Settling });
            SetDetail("trial " + trial.Id + " settling", CoachDetailCodes.TrialSettling, new() { ["id"] = trial.Id });
            await ApplyOverlayAsync(trial.Settings.Count == 0 ? null : trial.Settings).ConfigureAwait(false);
            await EnsureGuidingAsync(ct).ConfigureAwait(false);

            // the observer is installed before settling so a runaway while settling also fails only this trial
            var observer = new TrialObserver(options.TrialSeconds, LostTimeoutSec, h =>
            {
                var stats = h.CurrentStatistics();
                UpdateTrial(index, t => ApplyStats(t, stats, h.ElapsedSeconds), publish: false);
                lock (gate)
                {
                    step.Progress = (index + Math.Min(1, h.ElapsedSeconds / options.TrialSeconds)) / Math.Max(1, trials.Count);
                }

                Publish();
            });
            lock (gate)
            {
                trialHook = observer;
            }

            await OnLoopAsync(guider.SetCoachHookAsync(observer)).ConfigureAwait(false);
            var settleTask = guider.StartGuidingForCoachAsync(TrialSettle(), ct, coach.LoopToken);
            await Task.WhenAny(settleTask, observer.Completion).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (!observer.Completion.IsCompleted)
            {
                var settle = await settleTask.ConfigureAwait(false);
                if (!guider.State.IsGuidingActive())
                {
                    await OnLoopAsync(guider.SetCoachHookAsync(null)).ConfigureAwait(false);
                    throw new StepFailedException(settle.Code == GuideErrorCode.None ? CoachCodes.Internal : CoachCodes.StarLost, settle.Error);
                }

                UpdateTrial(index, t => t with { State = CoachTrialStates.Running });
                SetDetail("trial " + trial.Id + " guiding", CoachDetailCodes.TrialRunning, new() { ["id"] = trial.Id });
                observer.BeginMeasurement();
            }

            var obs = await WaitHookAsync(observer.Completion, observer.Stop, ct, sessionCt).ConfigureAwait(false);
            await OnLoopAsync(guider.SetCoachHookAsync(null)).ConfigureAwait(false);
            var final = TrialStatistics.Compute(obs.Samples, obs.PixelScale);
            if (final is not null)
            {
                lock (gate)
                {
                    effectiveFrames[trial.Id] = final.EffectiveFrames;
                    cloudNoise[trial.Id] = final.CloudNoiseArcsec;
                }
            }

            if (obs.Failure is not null || obs.StarLost)
            {
                UpdateTrial(index, t => ApplyStats(t, final, obs.ElapsedSeconds) with { State = CoachTrialStates.Failed });
                if (obs.Failure is not null && obs.Failure != GuideErrorCode.StarReacquireTimeout)
                {
                    // the guider stopped (runaway): restart for the next trial with the next settings
                    await guider.WaitForLoopAsync().ConfigureAwait(false);
                }

                return;
            }

            UpdateTrial(index, t => ApplyStats(t, final, obs.ElapsedSeconds) with { State = ct.IsCancellationRequested ? CoachTrialStates.Skipped : CoachTrialStates.Done });
        }

        private static CoachTrial ApplyStats(CoachTrial t, TrialStatistics? s, double elapsed) => t with
        {
            ElapsedSeconds = Math.Round(elapsed, 1),
            Frames = s?.Frames ?? 0,
            RmsRaArcsec = R(s?.RmsRaArcsec),
            RmsDecArcsec = R(s?.RmsDecArcsec),
            RmsTotalArcsec = R(s?.RmsTotalArcsec),
            PeakArcsec = R(s?.PeakArcsec),
            OscillationIndex = R(s?.OscillationIndex),
            SnrAvg = R(s?.SnrAvg, 1),
        };

        private void UpdateTrial(int index, Func<CoachTrial, CoachTrial> update, bool publish = true)
        {
            lock (gate)
            {
                trials[index] = update(trials[index]);
            }

            if (publish)
            {
                Publish();
            }
        }

        /// <summary>Settings sets A (current), B (suggestion), C (variant) and A2 (current again).</summary>
        private List<CoachTrial> TrialSets()
        {
            var s = guider.BaseSettings;
            var b = Suggestion(s, out var aggressionChange);
            var list = new List<CoachTrial>
            {
                new() { Id = CoachTrialIds.A, Kind = CoachTrialKinds.Current },
            };
            if (b.Count > 0)
            {
                list.Add(new CoachTrial { Id = CoachTrialIds.B, Kind = CoachTrialKinds.Suggestion, Settings = b });
                var c = Variant(s, b, aggressionChange);
                if (c is not null)
                {
                    list.Add(new CoachTrial { Id = CoachTrialIds.C, Kind = CoachTrialKinds.Variant, Settings = c });
                }
            }

            if (options.RepeatBaseline)
            {
                list.Add(new CoachTrial { Id = CoachTrialIds.A2, Kind = CoachTrialKinds.CurrentRepeat });
            }

            return list;
        }

        private List<CoachSettingChange> Suggestion(GuiderSettings s, out bool aggressionChanged)
        {
            aggressionChanged = false;
            DriftAnalysis? a;
            CoachResponse? r;
            CoachCameraResult? rec;
            List<CoachFinding> found;
            lock (gate)
            {
                a = driftAnalysis;
                r = response;
                rec = cameraRecommended;
                found = findings.ToList();
            }

            var changes = new Dictionary<string, CoachSettingChange>();
            void Add(CoachSettingChange c)
            {
                if (c.Value != c.CurrentValue && Applies(c, s))
                {
                    changes[c.Name] = c;
                }
            }

            // exposure/gain from the camera check, capped by the drift-limiting exposure
            double exposure = s.ExposureMs / 1000.0;
            if (cameraChanges is { } cc)
            {
                foreach (var c in cc)
                {
                    Add(c);
                }

                if (rec is not null)
                {
                    exposure = rec.ExposureSeconds;
                }
            }

            if (a?.DriftLimitingExposureSeconds is { } limit && exposure > limit * 1.05)
            {
                exposure = Math.Max(0.5, Math.Floor(limit * 2) / 2);
                Add(Change(CoachSettingNames.ExposureSeconds, CoachSettingsMap.Format(exposure)));
            }

            // min-move from the seeing, raised above the stiction limit
            double? raMin = a?.MinMoveRaPx, decMin = a?.MinMoveDecPx;
            foreach (var f in found.Where(f => f.Code == CoachCodes.ResponseMinPulse))
            {
                foreach (var c in f.Changes)
                {
                    double v = double.Parse(c.Value, CultureInfo.InvariantCulture);
                    if (c.Name == CoachSettingNames.RaMinMove)
                    {
                        raMin = Math.Max(raMin ?? 0, v);
                    }
                    else if (c.Name == CoachSettingNames.DecMinMove)
                    {
                        decMin = Math.Max(decMin ?? 0, v);
                    }
                }
            }

            if (raMin is { } rm)
            {
                Add(Change(CoachSettingNames.RaMinMove, CoachSettingsMap.Format(rm)));
            }

            if (decMin is { } dm)
            {
                Add(Change(CoachSettingNames.DecMinMove, CoachSettingsMap.Format(dm)));
            }

            // RA aggression from the PE rate vs the seeing per exposure and the oscillation before the session
            if (CoachSettingsMap.GetParameter(s.RaAlgorithm, GuideAxis.Ra, "aggression") is { } aggr && s.RaAlgorithm.Kind == GuideAlgorithmKind.Hysteresis)
            {
                double next = aggr;
                if (a is not null)
                {
                    double pePerExposure = a.RaMaxRateArcsecPerSec * exposure;
                    if (pePerExposure > a.SeeingRaArcsec)
                    {
                        next += 0.1;
                    }
                    else if (pePerExposure < 0.3 * a.SeeingRaArcsec)
                    {
                        next -= 0.1;
                    }
                }

                if (windowOscillation is { } osc)
                {
                    next += osc > LiveHintAnalyser.OscillationHigh ? -0.1 : osc < LiveHintAnalyser.OscillationLow ? 0.1 : 0;
                }

                next = Math.Round(Math.Clamp(next, 0.4, 1.0), 2);
                if (Math.Abs(next - aggr) > 1e-6)
                {
                    Add(Change(CoachSettingNames.RaAggression, CoachSettingsMap.Format(next)));
                    aggressionChanged = true;
                }
            }

            // Dec: one-sided guiding for large backlash with a steady drift, else backlash compensation
            var mode = found.FirstOrDefault(f => f.Code == CoachCodes.DriftDecGuideMode);
            if (mode is not null)
            {
                foreach (var c in mode.Changes)
                {
                    Add(c);
                }
            }
            else if (found.FirstOrDefault(f => f.Code == CoachCodes.ResponseDecBacklash) is { Changes.Count: > 0 } blc && r is not null)
            {
                foreach (var c in blc.Changes)
                {
                    Add(c);
                }
            }

            return changes.Values.OrderBy(c => CoachSettingNames.All.ToList().IndexOf(c.Name)).ToList();
        }

        // A setting change that the axis's algorithm reads: the Predictive algorithm sets its own gain and needs no min move.
        private static bool Applies(CoachSettingChange c, GuiderSettings s) => c.Name switch
        {
            CoachSettingNames.RaMinMove or CoachSettingNames.RaAggression or CoachSettingNames.RaHysteresis =>
                s.RaAlgorithm.Kind != GuideAlgorithmKind.Predictive,
            CoachSettingNames.DecMinMove or CoachSettingNames.DecAggression => s.DecAlgorithm.Kind != GuideAlgorithmKind.Predictive,
            _ => true,
        };

        private List<CoachSettingChange>? Variant(GuiderSettings s, List<CoachSettingChange> b, bool aggressionChanged)
        {
            var c = b.ToDictionary(x => x.Name);
            if (aggressionChanged && CoachSettingsMap.GetParameter(s.RaAlgorithm, GuideAxis.Ra, "aggression") is { } current)
            {
                double suggested = double.Parse(c[CoachSettingNames.RaAggression].Value, CultureInfo.InvariantCulture);
                double v = suggested > current ? suggested - 0.15 : suggested + 0.15;
                if (Math.Abs(v - current) < 1e-6)
                {
                    v = suggested > current ? suggested + 0.15 : suggested - 0.15;
                }

                v = Math.Round(Math.Clamp(v, 0.3, 1.0), 2);
                if (Math.Abs(v - suggested) < 1e-6)
                {
                    return null;
                }

                c[CoachSettingNames.RaAggression] = Change(CoachSettingNames.RaAggression, CoachSettingsMap.Format(v));
                return c.Values.OrderBy(x => CoachSettingNames.All.ToList().IndexOf(x.Name)).ToList();
            }

            // otherwise the alternative exposure: the second-best feasible camera result
            CoachCameraResult? rec;
            List<CoachCameraResult> results;
            lock (gate)
            {
                rec = cameraRecommended;
                results = cameraResults.ToList();
            }

            var alternative = results.Where(r => r.Feasible && !ReferenceEquals(r, rec) && Math.Abs(r.ExposureSeconds - (rec?.ExposureSeconds ?? 0)) > 1e-6)
                .OrderBy(r => r.JitterArcsec).FirstOrDefault();
            if (alternative is not null)
            {
                c[CoachSettingNames.ExposureSeconds] = Change(CoachSettingNames.ExposureSeconds, CoachSettingsMap.Format(alternative.ExposureSeconds));
                if (alternative.Gain >= 0)
                {
                    c[CoachSettingNames.Gain] = Change(CoachSettingNames.Gain, CoachSettingsMap.Format(alternative.Gain));
                }

                return c.Values.Where(x => x.Value != x.CurrentValue).OrderBy(x => CoachSettingNames.All.ToList().IndexOf(x.Name)).ToList();
            }

            return null;
        }

        private void TrialFindings()
        {
            List<CoachTrial> list;
            Dictionary<string, double> neff;
            Dictionary<string, double> clouds;
            lock (gate)
            {
                list = trials.ToList();
                neff = new Dictionary<string, double>(effectiveFrames);
                clouds = new Dictionary<string, double>(cloudNoise);
            }

            // autocorrelated guide errors: judged with the effective number of independent frames of each trial; a cloud
            // over the baseline does not make an alternative win
            var verdict = TrialJudge.Judge(list, neff, clouds);
            if (verdict.Baseline is not { } baseline)
            {
                return;
            }

            var ts = Now();
            if (verdict.ConditionsChanged)
            {
                AddFinding(CoachFindings.Create(CoachCodes.TrialsConditionsChanged, CoachStepNames.Trials, CoachSeverities.Warning, ts,
                    new() { ["changePercent"] = Math.Round(verdict.ConditionsChangePercent ?? 0, 1) }));
            }

            if (!list.Any(t => t.Kind is CoachTrialKinds.Suggestion or CoachTrialKinds.Variant))
            {
                // only the current settings were guided (e.g. Predictive axes and nothing from the camera check): no winner
                var s = guider.BaseSettings;
                AddFinding(CoachFindings.Create(CoachCodes.TrialsNothingToTry, CoachStepNames.Trials, CoachSeverities.Info, ts,
                    new()
                    {
                        ["rmsArcsec"] = baseline.RmsTotalArcsec,
                        ["predictive"] = s.RaAlgorithm.Kind == GuideAlgorithmKind.Predictive || s.DecAlgorithm.Kind == GuideAlgorithmKind.Predictive,
                    }));
                Publish();
                return;
            }

            var winner = verdict.Winner!;
            lock (gate)
            {
                for (int i = 0; i < trials.Count; i++)
                {
                    trials[i] = trials[i] with { IsWinner = trials[i].Id == winner.Id };
                }
            }

            if (!verdict.Significant)
            {
                // within the noise the current settings are as good as the alternatives (B/C stay applicable as trials)
                AddFinding(CoachFindings.Create(CoachCodes.TrialsNoImprovement, CoachStepNames.Trials, CoachSeverities.Good, ts,
                    new() { ["rmsArcsec"] = baseline.RmsTotalArcsec }));
            }
            else
            {
                double improvement = verdict.ImprovementPercent ?? 0;
                AddFinding(CoachFindings.Create(CoachCodes.TrialsWinner, CoachStepNames.Trials,
                    improvement >= 10 ? CoachSeverities.Good : CoachSeverities.Info, ts,
                    new()
                    {
                        ["id"] = winner.Id,
                        ["rmsArcsec"] = winner.RmsTotalArcsec,
                        ["baselineRmsArcsec"] = baseline.RmsTotalArcsec,
                        ["improvementPercent"] = Math.Round(improvement, 1),
                    }, baseline.RmsTotalArcsec - winner.RmsTotalArcsec, winner.Settings));
            }

            Publish();
        }

        #endregion

        #region guiding helpers

        /// <summary>Makes sure the guider guides (calibrating first if needed and allowed).</summary>
        private async Task EnsureGuidingAsync(CancellationToken ct)
        {
            if (!guider.Output.IsConnected)
            {
                throw new StepFailedException(CoachCodes.NoPulseOutput);
            }

            if (guider.State is GuiderState.Guiding or GuiderState.LostLock or GuiderState.Reacquiring && guider.IsLoopRunning)
            {
                return;
            }

            if (guider.Calibration is null && !options.AllowCalibration)
            {
                throw new StepFailedException(CoachCodes.NoCalibration);
            }

            if (guider.Calibration is null && calibrationFailed)
            {
                // don't repeat a failed calibration for every step
                throw new StepFailedException(CoachCodes.CalibrationFailed);
            }

            if (guider.State == GuiderState.Failed || !guider.IsLoopRunning)
            {
                // a loop that just ended (e.g. after a trial runaway) must finish before it is restarted
                await guider.WaitForLoopAsync().ConfigureAwait(false);
            }

            if (guider.Calibration is null)
            {
                SetDetail("calibrating", CoachDetailCodes.Calibrating);
            }
            else
            {
                SetDetail("starting guiding", null);
            }
            var result = await guider.StartGuidingForCoachAsync(QuickSettle, ct, coach.LoopToken).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            lock (gate)
            {
                calibrating = false;
            }

            if (result.Success || guider.State.IsGuidingActive())
            {
                return;
            }

            string code = result.Code switch
            {
                GuideErrorCode.CalibrationFailedRaNoMove or GuideErrorCode.CalibrationFailedDecNoMove or GuideErrorCode.CalibrationFailedBacklash
                    or GuideErrorCode.CalibrationFailedStarLost or GuideErrorCode.CalibrationInvalidated => CoachCodes.CalibrationFailed,
                GuideErrorCode.NoStarFound or GuideErrorCode.LockPositionNearEdge => CoachCodes.NoStar,
                GuideErrorCode.MountDisconnected or GuideErrorCode.PulseOutputFailed => CoachCodes.NoPulseOutput,
                GuideErrorCode.CameraFailed or GuideErrorCode.CameraCaptureFailed => CoachCodes.CameraError,
                _ => CoachCodes.Internal,
            };
            if (code == CoachCodes.CalibrationFailed)
            {
                calibrationFailed = true;
            }

            throw new StepFailedException(code, result.Error);
        }

        private SettleParams TrialSettle()
        {
            var p = host.TrialSettle ?? new SettleParams(1.5, 5, 30);
            return p with { TimeoutSec = Math.Min(p.TimeoutSec, 30) };
        }

        private async Task ApplyOverlayAsync(IReadOnlyList<CoachSettingChange>? changes)
        {
            var list = changes?.ToList();
            await OnLoopAsync(guider.SetCoachOverlayAsync(list is { Count: > 0 } ? s => CoachSettingsMap.Apply(s, list) : null)).ConfigureAwait(false);
        }

        /// <summary>Awaits a command queued for the loop; if the loop stops meanwhile the command is run by this thread.</summary>
        private async Task OnLoopAsync(Task command)
        {
            while (!command.IsCompleted)
            {
                guider.FlushCommandsIfIdle();
                await Task.WhenAny(command, Task.Delay(50)).ConfigureAwait(false);
            }

            await command.ConfigureAwait(false);
        }

        /// <summary>Waits for a hook; a skip stops it (keeping its partial result), a session cancel propagates.</summary>
        private async Task<T> WaitHookAsync<T>(Task<T> completion, Action stop, CancellationToken stepCt, CancellationToken sessionCt, bool waitForFrame = false)
        {
            try
            {
                return await completion.WaitAsync(stepCt).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!sessionCt.IsCancellationRequested)
            {
                stop();
                if (waitForFrame)
                {
                    // the procedure finishes on its next frame; don't hang when the loop has stopped
                    while (!completion.IsCompleted && guider.IsLoopRunning)
                    {
                        await Task.WhenAny(completion, Task.Delay(50, sessionCt)).ConfigureAwait(false);
                    }

                    if (!completion.IsCompleted)
                    {
                        throw;
                    }
                }

                return await completion.WaitAsync(sessionCt).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Camera check side of the mount debounce (<see cref="CoachMountGate"/>): waits while the mount reports a condition
        /// that is not confirmed yet, interrupts the session once it is.
        /// </summary>
        private async Task WaitForMountAsync(CancellationToken ct)
        {
            while (true)
            {
                var verdict = guider.CheckCoachMount();
                if (verdict.InterruptReason is { } reason)
                {
                    OnInterrupt(reason);
                    guider.DetachCoach();
                    cts.Token.ThrowIfCancellationRequested();
                }

                if (!verdict.Busy)
                {
                    return;
                }

                await clock.Delay(TimeSpan.FromMilliseconds(500), ct).ConfigureAwait(false);
            }
        }

        private void PublishFrame(GuideFrame frame, Stars.Star? star)
        {
            var stars = star is null ? [] : new[] { new StarInfo(star.X, star.Y, star.Snr, star.Mass, star.Hfd, true, true, 1, null) };
            guider.PublishEvent(new FrameReadyEvent(clock.UtcNow, frame, stars, GuidePoint.Invalid));
        }

        private void OnGuiderEvent(object? sender, GuiderEvent e)
        {
            bool changed = false;
            lock (gate)
            {
                switch (e)
                {
                    case StartCalibrationEvent:
                        calibrating = true;
                        changed = true;
                        break;
                    case CalibrationCompleteEvent or CalibrationFailedEvent:
                        calibrating = false;
                        changed = true;
                        break;
                }
            }

            if (changed)
            {
                Publish();
            }
        }

        #endregion

        #region status

        private CoachSettingChange Change(string name, string value)
        {
            string? currentValue;
            try
            {
                currentValue = host.GetSettingValue(name);
            }
            catch
            {
                currentValue = null;
            }

            return new CoachSettingChange(name, value, currentValue ?? CoachSettingsMap.GetValue(guider.BaseSettings, name));
        }

        private void AddFinding(CoachFinding f)
        {
            // min move, aggression and hysteresis are PHD2 algorithm parameters: nothing to apply on a Predictive axis
            var s = guider.BaseSettings;
            if (f.Changes.Any(c => !Applies(c, s)))
            {
                f = f with { Changes = f.Changes.Where(c => Applies(c, s)).ToList() };
                if (f.Code == CoachCodes.DriftMinMove && f.Changes.Count == 0)
                {
                    return;
                }
            }

            lock (gate)
            {
                findings.RemoveAll(x => x.Id == f.Id);
                findings.Add(f);
            }

            Publish();
        }

        private void SetDetail(string detail, string? code, Dictionary<string, object?>? parameters = null, double? progress = null)
        {
            lock (gate)
            {
                if (current is { } c)
                {
                    c.Detail = detail;
                    c.DetailCode = code;
                    c.DetailParameters = parameters ?? new();
                    if (progress is { } p)
                    {
                        c.Progress = p;
                    }
                }
            }

            Publish();
        }

        private void BuildReport()
        {
            MountSnapshot? m = null;
            try
            {
                m = guider.MountState.GetSnapshot();
            }
            catch
            {
                // no mount information
            }

            var s = guider.BaseSettings;
            CoachReportInput input;
            lock (gate)
            {
                input = new CoachReportInput
                {
                    Id = id,
                    Timestamp = clock.UtcNow,
                    ProfileName = host.ProfileName,
                    CameraName = guider.Camera.Name,
                    FocalLengthMm = s.FocalLengthMm > 0 ? s.FocalLengthMm : null,
                    PixelScale = Math.Round(guider.PixelScale, 4),
                    ImagingScale = host.ImagingScale,
                    DeclinationDeg = host.DeclinationDeg ?? m?.DeclinationDeg,
                    PierSide = (host.PierSide ?? m?.PierSide)?.ToString(),
                    StepsDone = steps.Where(x => x.State == CoachStepStates.Done).Select(x => x.Name).ToList(),
                    Findings = findings.ToList(),
                    Camera = CameraDto(),
                    Drift = drift,
                    LearnedPeriodicErrorArcsec = LearnedPeriodicError()?.Amplitude is { } learnedPe ? Math.Round(learnedPe, 3) : null,
                    Response = response,
                    Trials = trials.ToList(),
                    WindowRms = windowRms,
                };
            }

            var (r, reportFindings) = CoachReportBuilder.Build(input);
            lock (gate)
            {
                // impacts estimated by the report apply to the session findings too
                findings.Clear();
                findings.AddRange(r.Findings);
                report = r;
            }

            Publish();
        }

        private CoachCameraCheck? CameraDto() =>
            cameraResults.Count == 0 && cameraRecommended is null ? null : new CoachCameraCheck { Results = cameraResults.ToList(), Recommended = cameraRecommended };

        private void Publish()
        {
            var s = BuildStatus();
            status = s;
            guider.PublishEvent(new CoachStatusEvent(clock.UtcNow, s));
        }

        private CoachStatus BuildStatus()
        {
            lock (gate)
            {
                var now = clock.UtcNow;
                double total = steps.Sum(x => x.Estimated);
                double done = steps.Sum(x => x.State is CoachStepStates.Done or CoachStepStates.Skipped or CoachStepStates.Failed ? x.Estimated
                    : x.State == CoachStepStates.Running ? x.Estimated * Math.Clamp(x.Progress, 0, 1) : 0);
                var (min, max, currentGain) = coach.GainRange();
                CoachDrift? liveDrift = drift ?? (driftHook is { } dh ? DriftDto(null, null, false, dh) : null);
                CoachResponse? liveResponse = response ?? responseHook?.Snapshot();
                bool running = phase == CoachPhases.Running;
                return new CoachStatus
                {
                    Phase = phase,
                    Message = message,
                    MessageCode = messageCode,
                    MessageParameters = new Dictionary<string, object?>(messageParameters),
                    SessionId = id,
                    StartedAt = startedAt.UtcDateTime,
                    Step = running ? (calibrating ? CoachStepNames.Calibrating : currentName) : null,
                    Steps = steps.Select(x => new CoachStepStatus
                    {
                        Name = x.Name,
                        State = x.State,
                        Detail = x.Detail,
                        DetailCode = x.DetailCode,
                        DetailParameters = new Dictionary<string, object?>(x.DetailParameters),
                        Progress = Math.Round(x.Progress, 3),
                        ElapsedSeconds = Math.Round(x.State == CoachStepStates.Running && x.Started is { } st ? (now - st).TotalSeconds : x.Elapsed, 1),
                        EstimatedSeconds = Math.Round(x.Estimated),
                        Message = x.Message,
                        MessageCode = x.Code,
                        MessageParameters = new Dictionary<string, object?>(x.MessageParameters),
                    }).ToList(),
                    Progress = phase == CoachPhases.Complete ? 1.0 : total > 0 ? Math.Round(done / total, 3) : 0,
                    ElapsedSeconds = Math.Round((now - startedAt).TotalSeconds, 1),
                    EstimatedTotalSeconds = Math.Round(total),
                    GainMin = min,
                    GainMax = max,
                    CurrentGain = currentGain,
                    CurrentExposureSeconds = guider.BaseSettings.ExposureMs / 1000.0,
                    Camera = CameraDto(),
                    Drift = liveDrift,
                    Response = liveResponse,
                    Trials = trials.ToList(),
                    Findings = findings.ToList(),
                    Report = report,
                };
            }
        }

        private double Estimate(string step)
        {
            double exposure = guider.BaseSettings.ExposureMs / 1000.0;
            return step switch
            {
                CoachStepNames.CameraCheck => (options.ExposureSeconds.Count > 0 ? options.ExposureSeconds.Sum() : 6.0) * 3 * options.FramesPerCombination
                    + 3 * 3 * options.FramesPerCombination * 0.5,
                CoachStepNames.Drift => options.DriftSeconds + 15,
                CoachStepNames.MountResponse => (32 * MountResponseProcedure.AverageFrames + 20) * (exposure + 0.7),
                CoachStepNames.Trials => (options.RepeatBaseline ? 4 : 3) * (options.TrialSeconds + 20),
                _ => 0,
            };
        }

        private DateTime Now() => clock.UtcNow.UtcDateTime;

        private static double? R(double? v, int digits = 3) => v is { } d && double.IsFinite(d) ? Math.Round(d, digits) : null;

        #endregion
    }
}
