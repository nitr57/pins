// SPDX-License-Identifier: MPL-2.0

using System.Globalization;
using NINA.GuideEngine.Core;
using NINA.GuideEngine.MultiStar;

namespace NINA.GuideEngine.Algorithms;

/// <summary>Where the <see cref="PredictiveAlgorithm"/> stands.</summary>
public enum PredictivePhase
{
    /// <summary>
    /// The scores have been taught for fewer than <see cref="PredictiveAlgorithm.ScoreWindowFrames"/> frames or
    /// <see cref="PredictiveAlgorithm.ScoreWindowSeconds"/>: the choice of model is provisional.
    /// </summary>
    Learning,

    /// <summary>Learned and settled on a model.</summary>
    Adapted,
}

/// <summary>What the <see cref="PredictiveAlgorithm"/> has learned about one axis (an immutable snapshot).</summary>
public sealed record PredictiveState
{
    public PredictivePhase Phase { get; init; } = PredictivePhase.Learning;

    /// <summary>Frames that went into the model scores since the last reset.</summary>
    public int FramesLearned { get; init; }

    /// <summary>
    /// The smaller of <see cref="FramesLearned"/> / <see cref="PredictiveAlgorithm.ScoreWindowFrames"/> and the seconds
    /// those frames span / <see cref="PredictiveAlgorithm.ScoreWindowSeconds"/>, at most 1.
    /// </summary>
    public double Progress { get; init; }

    /// <summary>Share of each measurement taken over by the position estimate (Kalman gain, 0 … 1).</summary>
    public double Gain { get; init; }

    /// <summary>Measurement noise (seeing and centroid), σ in px; 0 before the first estimate.</summary>
    public double SeeingPx { get; init; }

    /// <summary>The mount's own random motion per frame, σ in px.</summary>
    public double WanderPx { get; init; }

    /// <summary>Estimated drift, px/s.</summary>
    public double DriftPxPerSec { get; init; }

    /// <summary>Wander/seeing variance ratio of the model in use.</summary>
    public double WanderRatio { get; init; }

    /// <summary>Drift-change/seeing variance ratio of the model in use.</summary>
    public double DriftRatio { get; init; }

    /// <summary>
    /// Recent prediction error of the model in use (mean square over <see cref="PredictiveAlgorithm.FitWindowFrames"/>
    /// frames) relative to its long-run one (the score memory, see <see cref="PredictiveAlgorithm.ScoreWindowSeconds"/>):
    /// about 1 while nothing changes, larger when the seeing got worse or the mount moves more, smaller when the seeing
    /// got better. Null before there is data.
    /// </summary>
    public double? Fit { get; init; }

    /// <summary>Model changes since the last reset.</summary>
    public int Takeovers { get; init; }

    /// <summary>Frames since the last model change, null without one.</summary>
    public int? FramesSinceTakeover { get; init; }

    /// <summary>The periodic-error prediction (RA), null on an axis without it.</summary>
    public PeriodicErrorState? PeriodicError { get; init; }
}

/// <summary>Kinds of <see cref="PredictiveNote"/>.</summary>
public enum PredictiveNoteKind
{
    /// <summary>Another model drives the correction.</summary>
    Takeover,

    /// <summary>A measurement far outside the noise was followed at once (bump, gust).</summary>
    Jump,

    /// <summary>The position estimate started over (start, dither, resume, gap); what was learned stays.</summary>
    Restart,

    /// <summary>Everything learned was forgotten.</summary>
    Reset,

    /// <summary>The learning phase ended.</summary>
    Learned,

    /// <summary>
    /// The recent prediction error left its long-run level, or came back (diagnostic only: without periodic-error and
    /// backlash terms in the model these swing it too, not only a change of the sky).
    /// </summary>
    Fit,

    /// <summary>The parameters changed; what was learned stays.</summary>
    Parameters,

    /// <summary>
    /// Periodic error: period detected, rejected as not stable, replaced or forgotten, fitted, small against the seeing,
    /// switched on or off, re-learned after a flip.
    /// </summary>
    PeriodicError,

    /// <summary>Periodic state, every <see cref="PredictiveAlgorithm.SummaryFrames"/> frames.</summary>
    Summary,

    /// <summary>
    /// Frames noisier than usual (thin clouds) are trusted less, the noise is back to normal, or a noisier usual level was
    /// taken over by the seeing estimate (<see cref="PredictiveAlgorithm.MeasurementSigmaPx"/>).
    /// </summary>
    Noise,
}

/// <summary>A notable event of the <see cref="PredictiveAlgorithm"/> for diagnostics (debug log, guide log).</summary>
internal sealed record PredictiveNote(PredictiveNoteKind Kind, string Message);

/// <summary>
/// Model-based guide algorithm: a Kalman filter per axis whose state is the position error and the drift rate, with the
/// corrections that actually went out as known inputs. The correction sends the motion predicted until the next
/// measurement (drift, periodic error) in full and a share (<see cref="Pace"/>) of the estimated position error.
/// </summary>
/// <remarks>
/// How much a measurement is trusted follows the night instead of aggression and hysteresis settings: a bank of filters
/// on a grid of noise ratios learns the ratio of the mount's own motion to the seeing, and a frame noisier than usual
/// (<see cref="MeasurementSigmaPx"/>) is trusted less. Below a pace of 1 an offset is taken out over a few frames instead
/// of at once. The model, the bank, the per-frame noise and the pace are described in docs/ALGORITHMS.md, "Predictive",
/// "Measurement uncertainty" and "Correction pace".
/// </remarks>
public sealed class PredictiveAlgorithm : GuideAlgorithmBase, IGuideAlgorithm
{
    /// <summary>No dead band by default, see <see cref="MinMove"/>.</summary>
    public const double DefaultMinMove = 0;

    /// <summary>Share of the estimated position error corrected per frame by default (1 = all of it).</summary>
    public const double DefaultPace = 1.0;

    /// <summary>Largest <see cref="Pace"/>: correcting more than the estimated position error overshoots.</summary>
    public const double MaxPace = 1.0;

    /// <summary>Smallest <see cref="Pace"/>; below it a real offset takes too many frames to remove.</summary>
    public const double MinPace = 0.1;

    /// <summary>
    /// Shortest memory of the prediction scores and the seeing estimate in frames (exponential forgetting), and the
    /// frames the learning phase needs at least: telling the filters apart takes this many prediction errors.
    /// </summary>
    public const int ScoreWindowFrames = 120;

    /// <summary>
    /// Memory of the prediction scores in seconds when that is more than <see cref="ScoreWindowFrames"/> frames (short
    /// exposures), and the time the learning phase needs at least: what the scores average over (the periodic error's
    /// cycle, a change of seeing) takes time, not frames. 120 frames of 2 s, where the algorithm was tuned, so frame
    /// intervals of 2 s and longer keep exactly <see cref="ScoreWindowFrames"/>.
    /// </summary>
    public const double ScoreWindowSeconds = 240;

    /// <summary>Frames between two <see cref="PredictiveNoteKind.Summary"/> notes.</summary>
    public const int SummaryFrames = 50;

    /// <summary>Memory in frames of the model fit check (<see cref="PredictiveState.Fit"/>).</summary>
    public const int FitWindowFrames = 20;

    // Fit outside these bounds: noted as a possible change of the conditions (the seeing σ changed by about 1.6× or
    // more); back inside the inner ones: noted again. Diagnostic only, not a phase: in steady simulated skies the fit
    // stays within 0.49 … 1.78, but a large periodic error or Dec backlash swing it beyond the bounds too.
    private const double ChangedAbove = 2.5;
    private const double ChangedBelow = 0.35;
    private const double SteadyAbove = 0.7;
    private const double SteadyBelow = 1.4;

    // q_e/r and q_d/r of the filter bank; the default filter drives the correction until the scores have data
    private static readonly double[] WanderRatios = [0.001, 0.003, 0.01, 0.03, 0.1, 0.3, 1, 3, 10, 30];
    private static readonly double[] DriftRatios = [0, 1e-5, 1e-4, 1e-3, 1e-2];
    private const double DefaultWanderRatio = 0.1;
    private const double DefaultDriftRatio = 1e-3;

    // frames of scores before the best filter takes over
    private const double MinScoreWeight = 10;

    // another filter takes over when its score is this much better (no flickering between near equals)
    private const double SwitchMargin = 0.97;

    // an innovation beyond this many sigmas is a real jump (bump, gust): the position follows it at once
    private const double JumpSigmas = 4;

    // time since the last measurement beyond this many frame intervals restarts the position (star lost, paused)
    private const double GapIntervals = 5;

    // the frame interval is an exponential mean with this weight for the newest interval (steady against one late frame)
    private const double IntervalWeight = 0.2;

    // the prediction scales with the time since the last frame, from this share of the usual interval (a frame right
    // after another) up to GapIntervals
    private const double MinIntervalScale = 0.2;

    // px², i.e. 0.01 px
    private const double MinNoise = 1e-4;

    private const double FitLambda = 1.0 - 1.0 / FitWindowFrames;

    // notes nobody collects are dropped beyond this
    private const int MaxNotes = 100;

    // periodic-error gate: on when the bank predicts this much better with it than without, off again above GateOff;
    // it fades in and out over these frames, and a curve that helps is stored every StoreEveryFrames
    private const double GateOn = 0.95;
    private const double GateOff = 0.99;
    private const int FadeInFrames = 20;
    private const int FadeOutFrames = 10;
    private const int StoreEveryFrames = 200;

    // per-frame measurement noise: the usual centroid σ is the median of the last UsualSigmaFrames frames or
    // UsualSigmaSeconds, whichever holds more frames (a cloud over up to half of them is no new normal), from a buffer of
    // at most MaxUsualSigmaFrames (12 minutes down to 0.18 s per frame); up to MeasurementUncertainty.UsualSigmaSpread ×
    // it is the frame-to-frame scatter, and a variance up to UsualVarianceShare × r beyond it is too small to matter; a
    // frame's variance is at most MaxNoiseFactor × r. From NoisyFactor on a frame counts as trusted less for the notes; a
    // stretch of them ends after CalmFrames below it.
    private const int UsualSigmaFrames = 3 * ScoreWindowFrames;
    private const double UsualSigmaSeconds = 3 * ScoreWindowSeconds;
    private const int MaxUsualSigmaFrames = 4000;
    private const double UsualVarianceShare = 0.25;
    private const double MaxNoiseFactor = 100;
    private const double NoisyFactor = 2;
    private const int CalmFrames = 10;

    private static readonly string[] Names = ["minMove", "pace"];
    private static readonly string[] PeriodicNames = ["minMove", "pace", "wormTeeth", "periodicError"];

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // the bank that guides (with the periodic-error prediction as a known input when there is one) and the same models
    // without it, the gate's yardstick and the correction while the prediction fades
    private readonly Filter[] bank;
    private readonly Filter[] reference;
    private readonly int defaultIndex;
    private readonly List<PredictiveNote> notes = [];
    private readonly PeriodicErrorEstimator? periodic;

    private double minMove;
    private double pace;

    private int selected;
    private int referenceSelected;
    private bool tracking;
    private double pendingCorrection;
    private DateTimeOffset? lastTime;
    private double intervalSec;

    // seeing estimate: weighted mean of the chosen filter's squared normalised innovation (units px²)
    private double seeingSum;
    private double seeingWeight;

    // what the scores were taught: frames, and the seconds they span (the learning phase needs both)
    private int learnedFrames;
    private double learnedSeconds;

    // diagnostics
    private int takeovers;
    private int? framesSinceTakeover;
    private bool misfit;
    private int misfitFrames;
    private int framesSinceSummary;
    private string? restartReason;
    private volatile PredictiveState state = new();

    // periodic error
    private bool periodicEnabled = true;
    private int wormTeeth;
    private bool gateOn;
    private double periodicWeight;
    private bool periodicStoreDue;
    private int framesSinceStore;
    private double? previousSeconds;
    private PeriodicErrorContext? previousContext;

    // measurement noise: centroid σ of the recent frames (ring buffer, and a copy to sort), the usual one, the usual
    // σ² the seeing estimate r was learned with (0 = none yet), this frame's σ and its measurement variance factor, and
    // the debug notes
    private readonly double[] sigmas = new double[MaxUsualSigmaFrames];
    private readonly double[] sortedSigmas = new double[MaxUsualSigmaFrames];
    private int sigmaCount;
    private int sigmaNext;
    private double usualSigma;
    private double learnedUsualVariance;
    private double frameSigma;
    private double noiseFactor = 1;
    private bool noisy;
    private bool noisyNoted;
    private int noisyFrames;
    private int noisyStretch;
    private int calmFrames;
    private double noisyMax;
    private int framesSinceNoiseNote = int.MaxValue / 2;
    private int noisySinceSummary;
    private double noisyMaxSinceSummary;

    /// <summary>A Predictive algorithm; with <paramref name="periodicError"/> (the RA axis) it also learns the periodic error.</summary>
    public PredictiveAlgorithm(bool periodicError = false)
    {
        bank = CreateBank(out defaultIndex);
        reference = CreateBank(out _);
        periodic = periodicError ? new PeriodicErrorEstimator() : null;
        RestoreDefaults();
        Reset();
    }

    private static Filter[] CreateBank(out int defaultIndex)
    {
        defaultIndex = 0;
        var filters = new List<Filter>();
        for (int wi = 0; wi < WanderRatios.Length; wi++)
        {
            for (int di = 0; di < DriftRatios.Length; di++)
            {
                if (WanderRatios[wi] == DefaultWanderRatio && DriftRatios[di] == DefaultDriftRatio)
                {
                    defaultIndex = filters.Count;
                }

                filters.Add(new Filter(WanderRatios[wi], DriftRatios[di], wi, di));
            }
        }

        return [.. filters];
    }

    public override string Name => "Predictive";

    public override GuideAlgorithmKind Kind => GuideAlgorithmKind.Predictive;

    /// <summary>
    /// Dead band of the predicted error (px); −1 (none, PHD2 convention) unless set with the "minMove" parameter. The
    /// filtered estimate needs none: the guider's smart default, made for the PHD2 filters that see the raw offset,
    /// would make the error creep up to its size before each correction (in the simulator 0.44″ instead of 0.16″ on a
    /// good mount).
    /// </summary>
    public override double MinMove
    {
        get => minMove > 0 ? minMove : -1;
        set => minMove = Math.Max(0, value);
    }

    /// <summary>
    /// Share of the estimated position error corrected per frame, <see cref="MinPace"/> … <see cref="MaxPace"/>. The drift and
    /// the periodic error's change until the next frame always go out in full; below 1 an offset is taken out over several
    /// frames instead of at once.
    /// </summary>
    public double Pace => pace;

    /// <summary>Estimated measurement noise (seeing and centroid), σ in px; 0 before the first estimate.</summary>
    public double SeeingPx => Math.Sqrt(SeeingVariance());

    /// <summary>Estimated wander of the mount per frame, σ in px (q_e of the chosen filter).</summary>
    public double WanderPx => Math.Sqrt(bank[selected].WanderRatio * SeeingVariance());

    /// <summary>Estimated drift, px/s.</summary>
    public double DriftPxPerSec => bank[selected].D;

    /// <summary>Kalman gain of the position in the last update (0 = measurement ignored, 1 = fully trusted).</summary>
    public double LastGain => bank[selected].LastGain;

    /// <summary>True while the filter tracks the position (after the first frame of a guiding run).</summary>
    public bool IsTracking => tracking;

    /// <summary>
    /// True while the guider settles (after a dither or the start of guiding), set by it before each frame. The frames
    /// still guide, but teach the model nothing: with the large settling corrections a small calibration-rate error or
    /// Dec backlash would look like seeing, wander and drift. The drift estimate is held meanwhile.
    /// </summary>
    public bool Settling { get; set; }

    /// <summary>Snapshot of what was learned, updated after every frame; safe to read from any thread.</summary>
    public PredictiveState State => state;

    /// <summary>Where the RA axis is for the next frame (set by the guider on the RA axis); the periodic error needs it.</summary>
    internal PeriodicErrorContext? PeriodicContext { get; set; }

    /// <summary>
    /// Centroid uncertainty of the next measurement, σ in px (<see cref="MeasurementUncertainty"/>), set by the
    /// guider before each frame; null = unknown (the frame counts as a usual one). A frame noisier than usual is trusted
    /// less.
    /// </summary>
    internal double? MeasurementSigmaPx { get; set; }

    /// <summary>Measurement variance of the last frame relative to the learned level (1 = a usual frame, larger = trusted less).</summary>
    public double LastNoiseFactor => noiseFactor;

    /// <summary>The usual centroid σ (px): the median of the recent frames; 0 before there are enough.</summary>
    public double UsualSigmaPx => usualSigma;

    /// <summary>Trusts noisier frames less (<see cref="MeasurementSigmaPx"/>); off only for comparisons.</summary>
    internal bool FrameWeighting { get; set; } = true;

    /// <summary>
    /// Memories in time with a floor in frames (<see cref="ScoreWindowSeconds"/>); off, they count frames only, as they
    /// did before. Off only for comparisons.
    /// </summary>
    internal bool TimeWindows { get; set; } = true;

    /// <summary>The periodic-error estimator (RA), null on an axis without it.</summary>
    internal PeriodicErrorEstimator? PeriodicError => periodic;

    /// <summary>True while the periodic-error prediction is applied (it may still be fading in or out).</summary>
    public bool PredictingPeriodicError => periodicWeight > 0;

    public override IReadOnlyList<string> ParamNames => periodic is null ? Names : PeriodicNames;

    public override string SettingsSummary
    {
        get
        {
            string text = minMove > 0
                ? string.Format(CultureInfo.InvariantCulture, "Correction pace = {0:F2}, Minimum move = {1:F3}", pace, minMove)
                : string.Format(CultureInfo.InvariantCulture, "Correction pace = {0:F2}, Minimum move = none", pace);
            return periodic is null ? text
                : text + string.Format(CultureInfo.InvariantCulture, ", Periodic error prediction = {0}, Worm teeth = {1}",
                    periodicEnabled ? "on" : "off", wormTeeth > 0 ? wormTeeth.ToString(CultureInfo.InvariantCulture) : "detect");
        }
    }

    /// <summary>Forgets everything, including what was learned about the mount and the seeing.</summary>
    public override void Reset()
    {
        if (learnedFrames > 0)
        {
            Note(PredictiveNoteKind.Reset, string.Create(Inv, $"reset after {learnedFrames} frames: everything learned is forgotten"));
        }

        foreach (var f in bank.Concat(reference))
        {
            f.Clear();
        }

        selected = defaultIndex;
        referenceSelected = defaultIndex;
        gateOn = false;
        periodicWeight = 0;
        previousSeconds = null;
        periodic?.StartSegment();
        tracking = false;
        pendingCorrection = 0;
        lastTime = null;
        intervalSec = 0;
        seeingSum = 0;
        seeingWeight = 0;
        learnedFrames = 0;
        learnedSeconds = 0;
        takeovers = 0;
        framesSinceTakeover = null;
        misfit = false;
        misfitFrames = 0;
        framesSinceSummary = 0;
        restartReason = null;
        sigmaCount = 0;
        sigmaNext = 0;
        usualSigma = 0;
        learnedUsualVariance = 0;
        noiseFactor = 1;
        noisy = false;
        framesSinceNoiseNote = int.MaxValue / 2;
        noisySinceSummary = 0;
        noisyMaxSinceSummary = 0;
        UpdateState();
    }

    // A new guiding session, a dither, a resume or re-enabled output: the position starts over from the next
    // measurement; what was learned about the mount and the seeing stays.
    public override void GuidingStarted()
    {
        RestartPosition("guiding started");
        periodic?.StartSegment();
    }

    public override void GuidingStopped()
    {
        RestartPosition("guiding stopped, drift forgotten");
        foreach (var f in bank.Concat(reference))
        {
            f.ForgetDrift();
        }

        lastTime = null;
        previousSeconds = null;
        periodicStoreDue |= gateOn;
        UpdateState();
    }

    public override void GuidingResumed() => RestartPosition("resumed");

    public override void GuidingDithered(double amount)
    {
        RestartPosition("dither");
        periodic?.Dithered(amount);
    }

    public override void GuidingEnabled() => RestartPosition("guiding output enabled");

    /// <summary>
    /// New parameters (<see cref="ParamNames"/>; the ones not given go back to their defaults) for the running algorithm.
    /// What was learned stays: the parameters shape the correction, not the model.
    /// </summary>
    public void ChangeParameters(IReadOnlyDictionary<string, double>? parameters)
    {
        RestoreDefaults();
        foreach (var (name, value) in parameters ?? new Dictionary<string, double>())
        {
            TrySetParam(name, value);
        }

        // a tooth count left out goes back to "detect" (a different one forgets the curve)
        periodic?.SetTeeth(wormTeeth > 0 ? wormTeeth : null);
        Note(PredictiveNoteKind.Parameters, string.Create(Inv, $"parameters changed ({SettingsSummary}), keeps what it learned in {learnedFrames} frames"));
    }

    /// <summary>Starts the periodic error from a stored curve (RA axis; ignored for a different tooth count).</summary>
    public bool RestorePeriodicError(PeriodicErrorModel model)
    {
        bool restored = periodic?.Restore(model) ?? false;
        TakePeriodicNotes();
        UpdateState();
        return restored;
    }

    /// <summary>Forgets the learned periodic error (after mechanical work on the mount).</summary>
    public void ForgetPeriodicError()
    {
        if (periodic is null)
        {
            return;
        }

        periodic.Forget();
        gateOn = false;
        periodicWeight = 0;
        Note(PredictiveNoteKind.PeriodicError, "periodic error forgotten");
        UpdateState();
    }

    /// <summary>The periodic-error curve to store, when one is due (it helps and was not stored for a while), else null.</summary>
    public PeriodicErrorModel? TakePeriodicErrorModel(DateTimeOffset now)
    {
        if (!periodicStoreDue || periodic is null)
        {
            return null;
        }

        periodicStoreDue = false;
        return periodic.ModelForStorage(now);
    }

    /// <summary>
    /// The period (s) of a detected periodic error that no longer holds (its curve changes, a stronger period replaced
    /// it, or another period was found after it was dropped as too weak) since the last call, else null: a stored curve
    /// with that period is to be discarded.
    /// </summary>
    internal double? TakeDiscardedPeriodicError() => periodic?.TakeDiscardedPeriod();

    /// <summary>Notes since the last call, oldest first (called by the guider after each frame).</summary>
    internal IReadOnlyList<PredictiveNote> TakeNotes()
    {
        if (notes.Count == 0)
        {
            return [];
        }

        var taken = notes.ToArray();
        notes.Clear();
        return taken;
    }

    public override double Result(double input) =>
        Result(input, lastTime is { } t ? t + TimeSpan.FromSeconds(intervalSec > 0 ? intervalSec : 1) : DateTimeOffset.UnixEpoch);

    /// <summary>Correction for the offset measured at <paramref name="time"/> (px, reduces a positive offset).</summary>
    public double Result(double input, DateTimeOffset time)
    {
        if (!double.IsFinite(input))
        {
            return 0;
        }

        double dt = lastTime is { } lt ? (time - lt).TotalSeconds : 0;
        bool gap = lastTime is null || dt <= 0 || (intervalSec > 0 && dt > GapIntervals * intervalSec);
        if (!gap)
        {
            intervalSec = intervalSec > 0 ? (1 - IntervalWeight) * intervalSec + IntervalWeight * dt : dt;
        }

        lastTime = time;
        double seconds = (time - DateTimeOffset.UnixEpoch).TotalSeconds;
        var context = periodicEnabled ? PeriodicContext : null;
        if (!tracking || gap || intervalSec <= 0)
        {
            // the position is taken as it is measured
            noiseFactor = 1;
            string reason = tracking ? (dt > 0 ? string.Create(Inv, $"gap of {dt:F1} s") : "clock went back") : restartReason ?? "first frame";
            Note(PredictiveNoteKind.Restart, string.Create(Inv,
                $"position restarted ({reason}) after {learnedFrames} frames learned, model {Model(bank[selected])}, drift {bank[selected].D:F4} px/s"));
            restartReason = null;
            foreach (var f in bank.Concat(reference))
            {
                f.Start(input, DriftPrior());
            }

            // the open-loop motion stays continuous; the first frame of a run teaches nothing
            if (context is not null)
            {
                periodic?.Observe(seconds, input, context, false, SeeingVariance(), 0, 0);
                TakePeriodicNotes();
            }

            tracking = true;
        }
        else
        {
            framesSinceTakeover += 1;

            // after the noise factor: a new usual noise level moves the seeing estimate
            double noise = noiseFactor = NoiseFactor();
            double r = SeeingVariance();
            double scale = Math.Clamp(dt / intervalSec, MinIntervalScale, GapIntervals);
            double lambda = 1.0 - 1.0 / ScoreMemoryFrames;
            bool wasLearned = Learned;

            // the periodic error's change since the last frame is a known input like the correction (same curve for both)
            double periodicStep = context is not null && previousSeconds is { } ps && previousContext is { } pc
                ? PeriodicPx(seconds, context) - PeriodicPx(ps, pc)
                : 0;
            var chosen = bank[selected];
            chosen.Predict(dt, pendingCorrection - periodicStep, scale, intervalSec);
            double chosenS = chosen.P11 + noise;
            double chosenNu = input - chosen.E;
            bool jump = seeingWeight >= MinScoreWeight && chosenNu * chosenNu > JumpSigmas * JumpSigmas * chosenS * r;
            bool learn = !Settling;
            if (jump && learn)
            {
                Note(PredictiveNoteKind.Jump, string.Create(Inv,
                    $"jump of {chosenNu:F2} px ({Math.Abs(chosenNu) / Math.Sqrt(chosenS * r):F1} σ of the prediction) followed at once, seeing σ {Math.Sqrt(r):F3} px"));
            }
            else if (!jump && learn)
            {
                // a noisier frame teaches the seeing estimate less (its normalised innovation already allows for its noise)
                double w = 1 / noise;
                double keep = Keep(lambda, w);
                seeingSum = keep * seeingSum + w * chosenNu * chosenNu / chosenS;
                seeingWeight = keep * seeingWeight + w;
                learnedFrames++;
                learnedSeconds += dt;
            }

            foreach (var f in bank)
            {
                if (!ReferenceEquals(f, chosen))
                {
                    f.Predict(dt, pendingCorrection - periodicStep, scale, intervalSec);
                }

                f.Update(input, jump, r, learn, noise, lambda);
            }

            foreach (var f in reference)
            {
                f.Predict(dt, pendingCorrection, scale, intervalSec);
                f.Update(input, jump, r, learn, noise, lambda);
            }

            if (context is not null && periodic is not null)
            {
                var best0 = bank[selected];
                periodic.Observe(seconds, input, context, learn && !jump, r, best0.WanderRatio * r * scale,
                    best0.DriftRatio * r / (intervalSec * intervalSec) * scale, noise);
                TakePeriodicNotes();
            }

            NoteNoise(noise, r);
            if (learn)
            {
                Select();
                SelectReference();
                UpdateGate(context);
                if (!jump)
                {
                    CheckFit();
                }

                if (!jump && !wasLearned && Learned)
                {
                    Note(PredictiveNoteKind.Learned, $"learned after {learnedFrames} frames: {Describe()}");
                    framesSinceSummary = 0;
                }
                else if (++framesSinceSummary >= SummaryFrames)
                {
                    Note(PredictiveNoteKind.Summary, Describe() + (noisySinceSummary > 0
                        ? string.Create(Inv, $", {noisySinceSummary} frames trusted less (variance up to ×{noisyMaxSinceSummary:F1})")
                        : ""));
                    framesSinceSummary = 0;
                    noisySinceSummary = 0;
                    noisyMaxSinceSummary = 0;
                }
            }
        }

        pendingCorrection = 0;
        previousSeconds = seconds;
        previousContext = context;
        UpdateState();
        var best = bank[selected];
        var plain = reference[referenceSelected];

        // the error expected at the next frame is the position now plus the motion until then (drift, and the periodic
        // error's change); the correction sends the motion in full but only the pace's share of the position (the sums
        // keep their order, so a pace of 1 gives exactly the correction without pace)
        double withoutPeriodic = plain.E + plain.D * intervalSec;
        double pacedWithoutPeriodic = pace * plain.E + plain.D * intervalSec;
        if (periodicWeight <= 0 || context is null)
        {
            return Output(withoutPeriodic, pacedWithoutPeriodic);
        }

        double peNext = PeriodicPx(seconds + intervalSec, NextContext(context));
        double peNow = PeriodicPx(seconds, context);
        double withPeriodic = best.E + best.D * intervalSec + peNext - peNow;
        double pacedWithPeriodic = pace * best.E + best.D * intervalSec + peNext - peNow;
        return Output((1 - periodicWeight) * withoutPeriodic + periodicWeight * withPeriodic,
            (1 - periodicWeight) * pacedWithoutPeriodic + periodicWeight * pacedWithPeriodic);
    }

    /// <summary>The correction that went out after the last <see cref="Result(double, DateTimeOffset)"/>.</summary>
    public void CorrectionApplied(double amount)
    {
        if (double.IsFinite(amount))
        {
            pendingCorrection += amount;
            periodic?.CorrectionApplied(amount);
        }
    }

    public override bool TryGetParam(string name, out double value)
    {
        switch (name)
        {
            case "minMove":
                value = minMove;
                return true;
            case "pace":
                value = pace;
                return true;
            case "wormTeeth" when periodic is not null:
                value = wormTeeth;
                return true;
            case "periodicError" when periodic is not null:
                value = periodicEnabled ? 1 : 0;
                return true;
            default:
                value = 0;
                return false;
        }
    }

    public override bool TrySetParam(string name, double value)
    {
        switch (name)
        {
            case "minMove":
                minMove = Math.Max(0, value);
                return value >= 0;
            case "pace":
                bool valid = value >= MinPace && value <= MaxPace;
                pace = valid ? value : DefaultPace;
                return valid;
            case "wormTeeth" when periodic is not null:
                wormTeeth = value >= 1 && value <= PeriodicErrorEstimator.MaxTeeth ? (int)Math.Round(value) : 0;
                periodic.SetTeeth(wormTeeth > 0 ? wormTeeth : null);
                return value >= 0;
            case "periodicError" when periodic is not null:
                periodicEnabled = value != 0;
                if (!periodicEnabled)
                {
                    gateOn = false;
                    periodicWeight = 0;
                }

                return true;
            default:
                return false;
        }
    }

    // the tooth count only reaches the estimator through TrySetParam or ChangeParameters, so that restoring the
    // defaults in between does not forget the curve
    protected override void RestoreDefaults()
    {
        minMove = DefaultMinMove;
        pace = DefaultPace;
        wormTeeth = 0;
        periodicEnabled = true;
    }

    // the dead band applies to the whole predicted error, the pace only to what goes out
    private double Output(double predicted, double paced) => Math.Abs(predicted) < minMove ? 0 : paced;

    private void RestartPosition(string reason)
    {
        // several in a row (stop, then start) are reported together at the next frame
        restartReason = tracking || restartReason is null ? reason : restartReason + ", " + reason;
        tracking = false;
        pendingCorrection = 0;
    }

    private double SeeingVariance() => seeingWeight > 0 ? Math.Max(MinNoise, seeingSum / seeingWeight) : 0;

    // exponential forgetting for a sample of weight w: it forgets as much as w samples of weight 1 would
    private static double Keep(double lambda, double w) => w == 1 ? lambda : Math.Pow(lambda, w);

    /// <summary>
    /// Frames the score memory spans at the current frame interval: <see cref="ScoreWindowSeconds"/> of frames, at least
    /// <see cref="ScoreWindowFrames"/>. The scores forget by λ = 1 − 1/N per frame.
    /// </summary>
    internal double ScoreMemoryFrames => WindowFrames(ScoreWindowFrames, ScoreWindowSeconds);

    /// <summary>Frames the usual centroid σ is the median of at the current frame interval.</summary>
    internal int UsualSigmaWindow => (int)Math.Min(MaxUsualSigmaFrames, Math.Round(WindowFrames(UsualSigmaFrames, UsualSigmaSeconds)));

    // taught long enough to trust the choice of model: ScoreWindowFrames frames spanning ScoreWindowSeconds; counting both
    // keeps it learned when a shorter exposure lengthens the memory afterwards
    private bool Learned => learnedFrames >= ScoreWindowFrames && (!TimeWindows || learnedSeconds >= ScoreWindowSeconds);

    // frames the learning phase takes at the current frame interval, for the debug log
    private int LearningFrames => (int)Math.Round(ScoreMemoryFrames);

    // N = max(minFrames, seconds / Δt) at the measured frame interval Δt: intervals of seconds / minFrames (2 s) and longer
    // keep exactly minFrames
    private double WindowFrames(int minFrames, double seconds) =>
        TimeWindows && intervalSec > 0 ? Math.Max(minFrames, seconds / intervalSec) : minFrames;

    // this frame's measurement variance relative to r (see the remarks); remembers σ for the usual level, and moves r
    // to a new usual level
    private double NoiseFactor()
    {
        if (MeasurementSigmaPx is not { } s || !double.IsFinite(s) || s <= 0)
        {
            return 1;
        }

        frameSigma = s;
        usualSigma = MedianSigma();
        sigmas[sigmaNext] = s;
        sigmaNext = (sigmaNext + 1) % sigmas.Length;
        sigmaCount = Math.Min(sigmaCount + 1, sigmas.Length);
        if (!FrameWeighting || usualSigma <= 0)
        {
            return 1;
        }

        double usual = usualSigma * usualSigma;
        TakeOverUsualLevel(usual);
        double r = SeeingVariance();
        if (r <= 0)
        {
            return 1;
        }

        double extra = s * s - UsualBand(usual, r);
        return extra > 0 ? Math.Min(MaxNoiseFactor, 1 + extra / r) : 1;
    }

    // the σ² of a frame up to which it counts as usual: the frame-to-frame scatter of σ, and a change too small to matter
    // next to the seeing
    private static double UsualBand(double usual, double r) =>
        Math.Max(MeasurementUncertainty.UsualSigmaSpread * MeasurementUncertainty.UsualSigmaSpread * usual, usual + UsualVarianceShare * r);

    // r was learned on frames with the usual σ² learnedUsualVariance. A new usual level beyond its band (a fainter star, a
    // shorter exposure, lasting haze) makes the frames usual (f = 1) from one frame to the next: r takes over the
    // difference at the same time. A lower level is only remembered.
    private void TakeOverUsualLevel(double usual)
    {
        if (learnedUsualVariance <= 0 || usual < learnedUsualVariance)
        {
            learnedUsualVariance = usual;
            return;
        }

        double r = SeeingVariance();
        if (usual <= UsualBand(learnedUsualVariance, r))
        {
            return;
        }

        seeingSum += (usual - learnedUsualVariance) * seeingWeight;
        if (seeingWeight > 0)
        {
            Note(PredictiveNoteKind.Noise, string.Create(Inv,
                $"new usual centroid σ {usualSigma:F3} px (was {Math.Sqrt(learnedUsualVariance):F3} px): seeing σ {Math.Sqrt(r):F3} -> {SeeingPx:F3} px"));
        }

        learnedUsualVariance = usual;
    }

    // the median σ of the newest UsualSigmaWindow frames, 0 while there are fewer than MinScoreWeight
    private double MedianSigma()
    {
        if (sigmaCount < MinScoreWeight)
        {
            return 0;
        }

        int n = Math.Min(sigmaCount, UsualSigmaWindow);
        for (int k = 0; k < n; k++)
        {
            sortedSigmas[k] = sigmas[(sigmaNext - 1 - k + sigmas.Length) % sigmas.Length];
        }

        Array.Sort(sortedSigmas, 0, n);
        int m = n / 2;
        return n % 2 == 1 ? sortedSigmas[m] : 0.5 * (sortedSigmas[m - 1] + sortedSigmas[m]);
    }

    // frames trusted less, for the debug log: a note when a stretch of them starts (at most one per SummaryFrames frames),
    // one when it ends after it was noted, and a count in the summaries
    private void NoteNoise(double noise, double r)
    {
        framesSinceNoiseNote++;
        if (noise >= NoisyFactor)
        {
            noisySinceSummary++;
            noisyMaxSinceSummary = Math.Max(noisyMaxSinceSummary, noise);
            if (!noisy)
            {
                noisy = true;
                noisyFrames = 0;
                noisyStretch = 0;
                noisyMax = 0;
                noisyNoted = framesSinceNoiseNote >= SummaryFrames;
                if (noisyNoted)
                {
                    framesSinceNoiseNote = 0;
                    Note(PredictiveNoteKind.Noise, string.Create(Inv,
                        $"frames trusted less: centroid σ {frameSigma:F3} px, usually {usualSigma:F3} px, measurement variance ×{noise:F1} of the learned level (seeing σ {Math.Sqrt(r):F3} px)"));
                }
            }

            noisyFrames++;
            noisyMax = Math.Max(noisyMax, noise);
            calmFrames = 0;
        }

        if (noisy)
        {
            noisyStretch++;
            if (noise < NoisyFactor && ++calmFrames >= CalmFrames)
            {
                noisy = false;
                if (noisyNoted)
                {
                    Note(PredictiveNoteKind.Noise, string.Create(Inv,
                        $"measurement noise back to normal: {noisyFrames} of the last {noisyStretch} frames trusted less, variance up to ×{noisyMax:F1}"));
                }
            }
        }
    }

    // drift uncertainty (units of r / s²) when the position (re)starts: about a tenth of the seeing per frame interval
    private double DriftPrior()
    {
        double t = intervalSec > 0 ? intervalSec : 1;
        return 1.0 / (100 * t * t);
    }

    private double PeriodicPx(double seconds, PeriodicErrorContext context) => periodic?.PredictPx(seconds, context) ?? 0;

    // the RA axis one frame interval later (it turns with the sidereal rate while tracking)
    private PeriodicErrorContext NextContext(PeriodicErrorContext context) => context.AxisHours is { } h
        ? context with { AxisHours = h + intervalSec / Sidereal.HourSeconds }
        : context;

    private void TakePeriodicNotes()
    {
        if (periodic is null)
        {
            return;
        }

        foreach (var text in periodic.TakeNotes())
        {
            Note(PredictiveNoteKind.PeriodicError, text);
        }
    }

    // applies the periodic-error prediction while the curve is significant (and stable) and the bank predicts better with
    // it than the same models without it (the same frames, so the same seeing and mount)
    private void UpdateGate(PeriodicErrorContext? context)
    {
        if (periodic is null)
        {
            return;
        }

        var with = bank[selected];
        var without = reference[referenceSelected];
        bool usable = periodicEnabled && context is not null && periodic.IsSignificant;
        bool scored = with.ScoreWeight >= MinScoreWeight && without.ScoreWeight >= MinScoreWeight;
        if (!gateOn && usable && scored && with.Score < GateOn * without.Score)
        {
            gateOn = true;
            periodicStoreDue = true;
            framesSinceStore = 0;
            Note(PredictiveNoteKind.PeriodicError, string.Create(Inv,
                $"periodic error prediction on: prediction error {with.Score:G3} px² with it, {without.Score:G3} px² without ({(1 - with.Score / without.Score) * 100:F0} % less); {periodic.Describe()}"));
        }
        else if (gateOn && (!usable || (scored && with.Score > GateOff * without.Score)))
        {
            gateOn = false;
            Note(PredictiveNoteKind.PeriodicError, string.Create(Inv,
                $"periodic error prediction off ({(usable ? $"prediction error {with.Score:G3} px² with it, {without.Score:G3} px² without" : periodic.IsSignificant ? "switched off or no axis information" : periodic.IsStable ? "curve not significant" : "curve not stable")}); {periodic.Describe()}"));
        }

        periodicWeight = gateOn ? Math.Min(1, periodicWeight + 1.0 / FadeInFrames) : Math.Max(0, periodicWeight - 1.0 / FadeOutFrames);
        if (gateOn && ++framesSinceStore >= StoreEveryFrames)
        {
            framesSinceStore = 0;
            periodicStoreDue = true;
        }
    }

    private void SelectReference()
    {
        var current = reference[referenceSelected];
        if (current.ScoreWeight < MinScoreWeight)
        {
            return;
        }

        int best = referenceSelected;
        for (int i = 0; i < reference.Length; i++)
        {
            if (reference[i].Score < reference[best].Score)
            {
                best = i;
            }
        }

        if (best != referenceSelected && reference[best].Score < SwitchMargin * current.Score)
        {
            referenceSelected = best;
        }
    }

    private void Select()
    {
        var current = bank[selected];
        if (current.ScoreWeight < MinScoreWeight)
        {
            return;
        }

        int best = selected;
        double bestScore = current.Score;
        for (int i = 0; i < bank.Length; i++)
        {
            if (bank[i].Score < bestScore)
            {
                best = i;
                bestScore = bank[i].Score;
            }
        }

        if (best != selected && bestScore < SwitchMargin * current.Score)
        {
            var next = bank[best];
            int gridSteps = Math.Max(Math.Abs(next.WanderIndex - current.WanderIndex), Math.Abs(next.DriftIndex - current.DriftIndex));
            takeovers++;
            framesSinceTakeover = 0;

            Note(PredictiveNoteKind.Takeover, string.Create(Inv,
                $"model {Model(current)} -> {Model(next)} ({gridSteps} grid step{(gridSteps == 1 ? "" : "s")}): prediction error {current.Score:G3} -> {next.Score:G3} px² ({(1 - next.Score / current.Score) * 100:F0} % less), gain {current.LastGain:F2} -> {next.LastGain:F2}, frame {learnedFrames}, takeover {takeovers}"));
            selected = best;
        }
    }

    // Did the conditions change? The recent prediction error of the model in use against its long-run one: a change
    // detector on the innovation variance, for the debug log (see ChangedAbove).
    private void CheckFit()
    {
        if (bank[selected].Fit is not { } fit || !Learned)
        {
            misfit = false;
            return;
        }

        if (!misfit && (fit > ChangedAbove || fit < ChangedBelow))
        {
            misfit = true;
            misfitFrames = 0;
            Note(PredictiveNoteKind.Fit, string.Create(Inv,
                $"prediction error {fit:F2}× its long-run level ({(fit > 1 ? "seeing worse, the mount moves more, periodic error or backlash" : "seeing better or calmer mount")}); {Describe()}"));
        }
        else if (misfit && fit > SteadyAbove && fit < SteadyBelow)
        {
            misfit = false;
            Note(PredictiveNoteKind.Fit, string.Create(Inv, $"prediction error back at its long-run level after {misfitFrames} frames (fit {fit:F2}); {Describe()}"));
        }
        else if (misfit)
        {
            misfitFrames++;
        }
    }

    private static string Model(Filter f) => string.Create(Inv, $"wander {f.WanderRatio:G3}/drift {f.DriftRatio:G3}");

    private string Describe()
    {
        var f = bank[selected];
        Filter? second = null;
        foreach (var other in bank)
        {
            if (!ReferenceEquals(other, f) && (second is null || other.Score < second.Score))
            {
                second = other;
            }
        }

        string centroid = usualSigma > 0 ? string.Create(Inv, $" (centroid σ {usualSigma:F3} px)") : "";
        string runnerUp = second is { ScoreWeight: > 0 } ? string.Create(Inv, $", next best {Model(second)} {second.Score:G3} px²") : "";
        string score = f.ScoreWeight > 0 ? string.Create(Inv, $"{f.Score:G3} px²") : "none yet";
        string periodicText = periodic is null || !periodicEnabled ? ""
            : string.Create(Inv, $"; periodic error {periodic.Describe()}, prediction {(gateOn ? "on" : "off")} (weight {periodicWeight:F2}), without it {reference[referenceSelected].Score:G3} px²");
        return string.Create(Inv,
            $"model {Model(f)}, gain {f.LastGain:F2}, seeing σ {SeeingPx:F3} px{centroid}, wander σ {WanderPx:F3} px, drift {f.D:F4} px/s, frame {intervalSec:F1} s, prediction error {score}{runnerUp}, fit {(f.Fit is { } fit ? fit.ToString("F2", Inv) : "-")}, learned {Math.Min(learnedFrames, LearningFrames)}/{LearningFrames} frames, {takeovers} takeovers{periodicText}");
    }

    private void Note(PredictiveNoteKind kind, string message)
    {
        if (notes.Count >= MaxNotes)
        {
            notes.RemoveAt(0);
        }

        notes.Add(new PredictiveNote(kind, message));
    }

    private void UpdateState()
    {
        var f = bank[selected];
        var phase = Learned ? PredictivePhase.Adapted : PredictivePhase.Learning;
        state = new PredictiveState
        {
            Phase = phase,
            FramesLearned = learnedFrames,
            Progress = Math.Min(1.0, TimeWindows
                ? Math.Min(learnedFrames / (double)ScoreWindowFrames, learnedSeconds / ScoreWindowSeconds)
                : learnedFrames / (double)ScoreWindowFrames),
            Gain = f.LastGain,
            SeeingPx = SeeingPx,
            WanderPx = WanderPx,
            DriftPxPerSec = f.D,
            WanderRatio = f.WanderRatio,
            DriftRatio = f.DriftRatio,
            Fit = f.Fit,
            Takeovers = takeovers,
            FramesSinceTakeover = framesSinceTakeover,
            PeriodicError = periodic?.State(
                !periodicEnabled ? PeriodicErrorPhase.Off
                : periodicWeight > 0 ? PeriodicErrorPhase.Predicting
                : periodic.IsNegligible ? PeriodicErrorPhase.Negligible
                : periodic.IsSignificant ? PeriodicErrorPhase.Ready
                : PeriodicErrorPhase.Learning,
                periodicWeight, PeriodicContext),
        };
    }

    /// <summary>One Kalman filter of the bank; covariances in units of the measurement noise r.</summary>
    private sealed class Filter(double wanderRatio, double driftRatio, int wanderIndex, int driftIndex)
    {
        private double scoreSum;
        private double recentSum;
        private double recentWeight;

        public double WanderRatio { get; } = wanderRatio;

        public double DriftRatio { get; } = driftRatio;

        /// <summary>Position in the grid of <see cref="WanderRatios"/>.</summary>
        public int WanderIndex { get; } = wanderIndex;

        /// <summary>Position in the grid of <see cref="DriftRatios"/>.</summary>
        public int DriftIndex { get; } = driftIndex;

        public double E { get; private set; }

        public double D { get; private set; }

        public double P11 { get; private set; }

        public double LastGain { get; private set; }

        public double ScoreWeight { get; private set; }

        /// <summary>Weighted mean squared innovation (px²).</summary>
        public double Score => ScoreWeight > 0 ? scoreSum / ScoreWeight : double.MaxValue;

        /// <summary>Recent mean squared innovation relative to <see cref="Score"/>, null without enough data.</summary>
        public double? Fit => recentWeight >= MinScoreWeight && scoreSum > 0 ? recentSum / recentWeight / Score : null;

        private double P12 { get; set; }

        private double P22 { get; set; }

        public void Clear()
        {
            E = D = P11 = P12 = P22 = LastGain = 0;
            scoreSum = 0;
            ScoreWeight = 0;
            recentSum = 0;
            recentWeight = 0;
        }

        public void ForgetDrift()
        {
            D = 0;
            P22 = 0;
        }

        public void Start(double z, double driftPrior)
        {
            E = z;
            P11 = 1;
            P12 = 0;
            P22 = Math.Max(P22, driftPrior);
            LastGain = 1;
        }

        public void Predict(double dt, double correction, double scale, double interval)
        {
            E += D * dt - correction;
            P11 += 2 * dt * P12 + dt * dt * P22 + WanderRatio * scale;
            P12 += dt * P22;
            P22 += DriftRatio / (interval * interval) * scale;
        }

        /// <summary>
        /// Measurement update with the measurement variance <paramref name="noise"/> (units of r, 1 = a usual frame); without
        /// <paramref name="learn"/> no score and the drift held. The score forgets by <paramref name="lambda"/> per frame.
        /// </summary>
        public void Update(double z, bool jump, double r, bool learn, double noise, double lambda)
        {
            double nu = z - E;
            if (jump)
            {
                // follow a real jump at once; it says nothing about which model predicts best
                P11 += r > 0 ? nu * nu / r : 0;
            }
            else if (learn)
            {
                // a noisier frame counts less in the scores
                double w = 1 / noise;
                double keep = Keep(lambda, w);
                double keepRecent = Keep(FitLambda, w);
                scoreSum = keep * scoreSum + w * nu * nu;
                ScoreWeight = keep * ScoreWeight + w;
                recentSum = keepRecent * recentSum + w * nu * nu;
                recentWeight = keepRecent * recentWeight + w;
            }

            double s = P11 + noise;
            double k1 = P11 / s;
            E += k1 * nu;
            if (learn)
            {
                double k2 = P12 / s;
                D += k2 * nu;
                P22 = Math.Max(P22 - k2 * P12, 0);
            }

            // held drift: a Schmidt ("consider") update with no gain for it, which keeps P22 and scales P12 like P11
            P12 = (1 - k1) * P12;
            P11 = (1 - k1) * P11;
            LastGain = k1;
        }
    }
}
