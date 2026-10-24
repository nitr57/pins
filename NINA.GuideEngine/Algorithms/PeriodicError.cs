// SPDX-License-Identifier: MPL-2.0

using System.Globalization;
using NINA.GuideEngine.Core;

namespace NINA.GuideEngine.Algorithms;

/// <summary>
/// Periodic error of the RA drive as learned by the <see cref="PredictiveAlgorithm"/>, as stored between sessions: worm
/// fundamental and harmonics in RA-axis arcseconds against the worm angle 2π · N · (hour angle + 12 h on the West pier
/// side) / 24 h (N = teeth, or the sidereal day over the period when the tooth count is not known).
/// </summary>
public sealed record PeriodicErrorModel
{
    /// <summary>Teeth of the worm wheel, null when only the period is known.</summary>
    public int? Teeth { get; init; }

    /// <summary>Worm period in seconds of tracking.</summary>
    public double PeriodSeconds { get; init; }

    /// <summary>Sine coefficients per harmonic order 1, 2, 3 (RA-axis arcsec).</summary>
    public IReadOnlyList<double> Sin { get; init; } = [];

    /// <summary>Cosine coefficients per harmonic order 1, 2, 3 (RA-axis arcsec).</summary>
    public IReadOnlyList<double> Cos { get; init; } = [];

    /// <summary>Worm cycles the model was learned from.</summary>
    public double Cycles { get; init; }

    public DateTimeOffset LearnedAt { get; init; }

    /// <summary>Semi-amplitude of the fundamental (RA-axis arcsec).</summary>
    public double AmplitudeArcsec => Sin.Count > 0 && Cos.Count > 0 ? Math.Sqrt(Sin[0] * Sin[0] + Cos[0] * Cos[0]) : 0;
}

/// <summary>Where the RA axis is for one guide frame (set by the guider for the RA algorithm).</summary>
internal sealed record PeriodicErrorContext
{
    /// <summary>
    /// RA axis angle in hours: the mount's hour angle (its sidereal time − its right ascension), plus 12 h on the West
    /// pier side, kept continuous by the guider. Null without mount information: the phase is then time-based.
    /// </summary>
    public double? AxisHours { get; init; }

    public double? DeclinationDeg { get; init; }

    /// <summary>Guide camera pixel scale (arcsec/px); 0 when unknown.</summary>
    public double PixelScale { get; init; }

    public PierSide PierSide { get; init; } = PierSide.Unknown;
}

/// <summary>What the periodic-error prediction is doing.</summary>
public enum PeriodicErrorPhase
{
    /// <summary>Switched off, or an axis without periodic error (Dec).</summary>
    Off,

    /// <summary>
    /// Detecting the period, fitting the curve, or waiting for it to pass the stability test; see
    /// <see cref="PeriodicErrorState.Progress"/>.
    /// </summary>
    Learning,

    /// <summary>A significant, stable curve, but it does not (yet) predict better than without it: not applied.</summary>
    Ready,

    /// <summary>Applied (see <see cref="PeriodicErrorState.Weight"/> while it fades in or out).</summary>
    Predicting,

    /// <summary>
    /// Too small to matter: whatever the curve at the period is, stable or not (see
    /// <see cref="PeriodicErrorState.Stable"/>), it moves the star between two frames by a small fraction of the seeing,
    /// and predicting it does not help (a curve that does is <see cref="Predicting"/>).
    /// </summary>
    Negligible,
}

/// <summary>Snapshot of the periodic-error prediction of an axis.</summary>
public sealed record PeriodicErrorState
{
    public PeriodicErrorPhase Phase { get; init; }

    /// <summary>
    /// Progress of the learning, 0 … 1, from the data of the current stability run (the data since the last slew, sync
    /// or flip): for the period detection, then the first cycle of the fit; with the tooth count known the data of the
    /// stability test.
    /// </summary>
    public double Progress { get; init; }

    public double? PeriodSeconds { get; init; }

    /// <summary>Teeth of the worm wheel: set, or snapped from a stable detected period; null for any other period.</summary>
    public int? Teeth { get; init; }

    /// <summary>Semi-amplitude of the fundamental on the sky (arcsec, RA axis × cos dec), or in px without scale.</summary>
    public double? Amplitude { get; init; }

    /// <summary>True when <see cref="Amplitude"/> is in arcsec on the sky, false for guide pixels.</summary>
    public bool AmplitudeInArcsec { get; init; }

    /// <summary>Share of the prediction in the correction, 0 … 1 (fades in and out).</summary>
    public double Weight { get; init; }

    /// <summary>
    /// True while the curve is stable like a mechanical periodic error: a detected period while it is held, the curve of
    /// a known tooth count once it passed the stability test, a restored curve until it fails it.
    /// </summary>
    public bool Stable { get; init; }
}

/// <summary>
/// Learns the periodic error of the RA drive from the open-loop RA motion: the worm period (from the tooth count, or
/// detected), and a fit of fundamental + 2 harmonics against the worm angle. Not thread-safe: driven by the
/// <see cref="PredictiveAlgorithm"/> on the guide loop.
/// </summary>
/// <remarks>
/// The open-loop motion is the measured error + every correction that went out + dither offsets. The period is detected
/// with a periodogram and snapped to a tooth count once precise enough; the fit is a Kalman filter over level, drift and
/// the harmonics whose parameters wander slowly (level: the mount's own wander; harmonics: a memory of a few cycles). The
/// phase is the worm angle from the RA axis angle, or the time without mount information. Works in RA-axis arcseconds
/// when the pixel scale and the declination are known (the curve then holds for any target), else in guide pixels.
/// A detected period is only accepted, and kept, while it is stable like a mechanical periodic error: the same
/// amplitude and phase in consecutive parts of the stability run (the data since the last slew, sync or flip, over any
/// number of guiding runs); the full range is searched again every 10 minutes and a clearly stronger stable period
/// takes over. A known tooth count (set, snapped from a stable period, or restored with the curve) is kept; its curve
/// counts only while it passes the same test. The search and the tests are spread over the frames, one step per frame.
/// A curve too small to matter at this frame rate and seeing is <see cref="IsNegligible"/>. The design and the results
/// are in docs/ALGORITHMS.md, "Periodic-error prediction".
/// </remarks>
internal sealed class PeriodicErrorEstimator
{
    /// <summary>Harmonic orders fitted (worm fundamental + 2 harmonics).</summary>
    public const int Harmonics = 3;

    /// <summary>Shortest and longest worm period considered by the detection (seconds).</summary>
    public const double MinPeriodSeconds = 180;

    public const double MaxPeriodSeconds = 1200;

    /// <summary>Largest tooth count of a worm wheel accepted (a setting or a stored model).</summary>
    public const int MaxTeeth = 2000;

    /// <summary>
    /// Largest harmonic coefficient of a stored model accepted, RA-axis arcsec: far beyond the periodic error of any
    /// mount (a poor one has ±30″), so only a corrupted model is refused.
    /// </summary>
    public const double MaxStoredAmplitudeArcsec = 300;

    /// <summary>
    /// A stored curve is taken along with a discarded detected period (<see cref="TakeDiscardedPeriod"/>) when the periods
    /// differ by at most this share; a weak period is dropped only for another one farther away.
    /// </summary>
    public const double DiscardDifference = 0.02;

    // the period is Sidereal.DaySeconds / teeth, exactly: one worm turn moves the gear by one tooth
    private const int Parameters = 2 + 2 * Harmonics;

    // a typical worm period (180 teeth: 478.7 s): the progress of a detection that has no period yet
    private const double TypicalPeriodSeconds = 480.0;

    // a restored curve counts as known to a quarter of its amplitude, and at least to this (RA-axis arcsec per
    // coefficient), so that the live fit can still move a small or zero curve
    private const double RestoredUncertaintyShare = 0.25;
    private const double MinRestoredUncertaintyArcsec = 0.1;

    // detection: cycles of data needed, how often it runs, and when the tooth count is unambiguous
    private const double DetectCycles = 2.0;
    private const double DetectEverySeconds = 60;
    private const double RefineEverySeconds = 300;
    private const double SnapSigmaTeeth = 0.12;

    // the tooth count also needs the last refinements (5 min apart) all this close to the same whole number: the σ is
    // too optimistic on the reconstructed open-loop motion, whose estimates can stay 0.3 teeth off for an hour
    private const int SnapAgreement = 3;
    private const double SnapTolerance = 0.2;

    // the samples: at most MaxSamples; frames closer than MinSampleSeconds are averaged into one, so that the store holds
    // more than AcceptParts cycles of the longest period at any frame rate
    private const int MaxSamples = 6000;
    private const double MinSampleSeconds = (AcceptParts + 1) * MaxPeriodSeconds / MaxSamples;

    // samples of the full period search (the refinement near a held period uses the whole stability run): about an
    // hour of guiding, and at least two and a half cycles of the longest period
    private const int SearchSamples = 1500;
    private const double MinSearchSeconds = 2.5 * MaxPeriodSeconds;

    // significance of the fitted fundamental (in standard deviations) and the data it needs
    private const double SignificanceSigmas = 3.0;

    // a periodogram peak this far above the median of the searched range is a candidate, the strongest few are tried
    private const double PeakFactor = 8;
    private const int MaxCandidates = 5;

    // Stability test: the stability run (each guiding run's level and drift taken out with the curve) in parts of one
    // cycle of the data it covers (2 to 6), each with its own least squares of level, drift and the harmonics. Each
    // part's fundamental above 2.5 standard errors of the noise at that frequency (a part of pure noise gets there 4 % of
    // the time, Rayleigh; all of 2 parts 0.2 %); any two parts' curve amplitudes within a factor 2 (a mechanical error
    // hardly changes within a night) or their difference within 3 standard errors; their phases within 45° (an eighth of
    // a cycle) or 3 standard errors of the difference. A detected period needs 5 parts, 5 cycles of data: a wobble of the
    // mount can look as steady as a worm for a few of its cycles. The curve of a known tooth count needs 2.
    private const double PartSigmas = 2.5;
    private const int MaxParts = 6;
    private const double MinPartCycles = 1.0;
    private const int AcceptParts = 5;
    private const double PartAmplitudeRatio = 2.0;
    private const double PartPhaseDegrees = 45;
    private const double PartDifferenceSigmas = 3.0;

    // a held period: kept while its parts stay above 2 standard errors, dropped after two failed checks in a row (the
    // check runs every RefineEverySeconds on data that mostly overlaps: hysteresis against its noise). Its stored curve
    // goes with it when significant parts disagree, or later when another period is found; a known tooth count is kept
    private const double KeepPartSigmas = 2.0;
    private const int DropAfterFailures = 2;

    // Euler–Mascheroni constant: E[ln P] of a periodogram ordinate of noise lies this far below the ln of its level
    // (P/level ~ χ²₂/2)
    private const double EulerGamma = 0.5772156649015329;

    // keep searching: the full range again this often while a detected period is held; another stable period takes over
    // when its harmonic power is at least this much larger on the same data (the peak test already makes it significant)
    private const double SearchEverySeconds = 600;
    private const double SwitchPower = 1.5;

    // A stability run ends when the RA axis angle moves by more than AxisJumpSeconds (tracking seconds) beyond the
    // time that passed (a slew, or a plate-solve sync that shifts the curve's phase against it), or at a flip; frames
    // more than LongGapSeconds apart start a new level (the mount wandered meanwhile), and the gap counts as no data. A
    // detected period dropped because its curve was too weak, not because it changed, discards a stored curve only once
    // another period is found at least DiscardDifference away (the guider takes a stored curve along within that share).
    private const double AxisJumpSeconds = 10;
    private const double LongGapSeconds = 60;

    // Too small to matter (IsNegligible): the curve's steepest change between two frames below this share of the seeing
    // σ per frame (leaves again above 1.5×). A guider that follows the star within a frame trails such a curve by at most
    // that change, which in quadrature with the seeing adds ≤ 1 % to the RMS. A slow one (poor seeing, a low gain) trails
    // it by several frames and can still gain from the prediction, so it does not keep the gate from applying a curve
    // that measurably helps; it tells why one is not applied. The seeing is the filter bank's, without the extra noise of
    // single frames (the measurement uncertainty): a cloud does not make the curve small.
    private const double NegligibleStep = 0.15;
    private const double NegligibleLeave = 1.5;
    private const int NegligibleSeeingFrames = 100;

    // Most the level and the drift of the fit may wander per frame, relative to the seeing variance. The filter bank's
    // wander and drift can't be taken as they are: until the curve is predicted, the bank sees the periodic error itself
    // as wander and drift, and a level that follows it leaves nothing for the harmonics.
    private const double LevelNoiseCap = 0.01;
    private const double DriftNoiseCap = 1e-10;

    // sample × frequency evaluations of the detection on this thread (for the tests)
    [ThreadStatic]
    private static long evaluations;

    private readonly List<Sample> samples = [];
    private readonly double[] x = new double[Parameters];
    private readonly double[,] p = new double[Parameters, Parameters];
    private readonly List<string> notes = [];
    private readonly List<double> recentTeeth = [];
    private readonly double[] fitH = new double[Parameters];
    private readonly double[] fitPh = new double[Parameters];

    private int? configuredTeeth;
    private double? period;
    private int? teeth;
    private double periodSigma = double.PositiveInfinity;
    private bool fitActive;
    private bool stable;
    private bool verified;
    private bool unstableNoted;
    private bool fitSignificant;
    private bool restored;
    private double cycles;
    private double restoredCycles;
    private double? lastPhase;
    private bool segmentStart = true;
    private bool guidingRunStart;
    private double segmentOrigin;
    private int segment;
    private int run;
    private int runStart;
    private double runCovered;
    private double? lastTime;
    private double lastTau;
    private double openLoopPx;
    private double? timeOrigin;
    private bool? hourAngleMode;
    private PierSide lastPierSide = PierSide.Unknown;
    private double nextDetect = double.NegativeInfinity;
    private double nextSearch = double.NegativeInfinity;
    private double nextSizeCheck = double.NegativeInfinity;
    private double nextRejectionNote = double.NegativeInfinity;
    private int failedChecks;
    private double? discardedPeriod;
    private double? weakPeriod;
    private IEnumerator<bool>? job;
    private double lastScale = 1;
    private bool axisUnits;

    public PeriodicErrorEstimator(int? teeth = null)
    {
        SetTeeth(teeth);
    }

    /// <summary>Worm period in seconds of tracking, null while unknown.</summary>
    public double? PeriodSeconds => period;

    /// <summary>Teeth of the worm wheel, null while not known exactly.</summary>
    public int? Teeth => teeth;

    /// <summary>
    /// True when the fitted curve can be trusted: its fundamental stands out from its uncertainty, and the curve passed
    /// the stability test (a restored one until it fails it).
    /// </summary>
    public bool IsSignificant { get; private set; }

    /// <summary>
    /// True while the curve is stable like a mechanical periodic error: a detected period while it is held, the curve of
    /// a known tooth count once it passed the stability test, a restored curve until it fails it.
    /// </summary>
    internal bool IsStable => stable;

    /// <summary>
    /// True when the curve (over at least one cycle of the stability run) is too small to matter, stable or not: its
    /// steepest change between two frames is a small fraction of the seeing. It tells why a curve is not applied; the
    /// gate still applies one that measurably helps.
    /// </summary>
    public bool IsNegligible { get; private set; }

    /// <summary>The last size check's steepest change of the curve per frame over the seeing σ (NaN before one).</summary>
    internal double SizeRatio { get; private set; } = double.NaN;

    /// <summary>Sample × frequency evaluations (a sin/cos pair or its like) of the detection on this thread so far.</summary>
    internal static long Evaluations => evaluations;

    /// <summary>Semi-amplitude of the fundamental in the fit's units (RA-axis arcsec, or px).</summary>
    public double Amplitude => Math.Sqrt(x[2] * x[2] + x[3] * x[3]);

    /// <summary>True while the fit works in RA-axis arcseconds (pixel scale and declination known).</summary>
    public bool AxisUnits => axisUnits;

    /// <summary>Axis arcsec per guide pixel of the last frame (1 without scale).</summary>
    public double Scale => lastScale;

    /// <summary>Worm cycles of data behind the fit (including a restored model's).</summary>
    public double Cycles => cycles + restoredCycles;

    /// <summary>
    /// Learning progress 0 … 1 from the data of the current stability run: for the period detection, then the first
    /// cycle of the fit (with the tooth count known, first the data of the stability test).
    /// </summary>
    public double Progress
    {
        get
        {
            if (IsSignificant)
            {
                return 1;
            }

            if (period is { } per && fitActive)
            {
                // a curve not (yet) stable: the two cycles of its stability test; then the first cycle of the fit
                return Math.Clamp(stable ? Cycles / MinPartCycles : runCovered / (2 * MinPartCycles * per), 0, 0.99);
            }

            // the data a detected period needs in this stability run, for a worm of a typical period
            return Math.Clamp(runCovered / (AcceptParts * MinPartCycles * TypicalPeriodSeconds), 0, 0.99);
        }
    }

    /// <summary>The tooth count of the setting (null or 0 = detect). A different one forgets everything learned.</summary>
    public void SetTeeth(int? count)
    {
        int? value = count is > 0 ? count : null;
        if (value == configuredTeeth && (period is not null || value is null))
        {
            return;
        }

        configuredTeeth = value;
        Forget();
        if (value is { } n)
        {
            teeth = n;
            period = Sidereal.DaySeconds / n;
            periodSigma = 0;
            fitActive = true;
            ResetFit();
        }
    }

    /// <summary>Forgets the period (unless set by the tooth count) and the fitted curve.</summary>
    public void Forget()
    {
        ClearSamples();
        teeth = configuredTeeth;
        period = configuredTeeth is { } n ? Sidereal.DaySeconds / n : null;
        periodSigma = configuredTeeth is null ? double.PositiveInfinity : 0;
        fitActive = period is not null;
        stable = false;
        verified = false;
        unstableNoted = false;
        restored = false;
        restoredCycles = 0;
        cycles = 0;
        fitSignificant = IsSignificant = false;
        IsNegligible = false;
        lastPhase = null;
        nextDetect = double.NegativeInfinity;
        nextSearch = double.NegativeInfinity;
        nextSizeCheck = double.NegativeInfinity;
        nextRejectionNote = double.NegativeInfinity;
        failedChecks = 0;
        discardedPeriod = null;
        weakPeriod = null;
        SizeRatio = double.NaN;
        recentTeeth.Clear();
        ResetFit();
    }

    /// <summary>
    /// Starts from a stored model (ignored when the tooth count of the setting differs). The curve counts as known, with
    /// an uncertainty of a quarter of its amplitude: the live fit and the gate take it from there.
    /// </summary>
    public bool Restore(PeriodicErrorModel model)
    {
        if (Invalid(model) is { } why)
        {
            Note($"stored periodic error model discarded: {why}");
            return false;
        }

        if (configuredTeeth is { } n && model.Teeth != n)
        {
            return false;
        }

        Forget();
        teeth = model.Teeth;
        period = model.Teeth is { } t ? Sidereal.DaySeconds / t : model.PeriodSeconds;
        periodSigma = model.Teeth is null ? period.Value * 1e-3 : 0;
        fitActive = true;
        ResetFit();
        double prior = Math.Max(RestoredUncertaintyShare * model.AmplitudeArcsec, MinRestoredUncertaintyArcsec);
        for (int k = 0; k < Harmonics; k++)
        {
            x[2 + 2 * k] = model.Sin[k];
            x[3 + 2 * k] = model.Cos[k];
            p[2 + 2 * k, 2 + 2 * k] = prior * prior;
            p[3 + 2 * k, 3 + 2 * k] = prior * prior;
        }

        restored = true;
        stable = true;
        restoredCycles = model.Cycles;
        axisUnits = true;
        UpdateSignificance();
        Note(string.Create(CultureInfo.InvariantCulture,
            $"periodic error model restored: {Describe()} from {model.Cycles:F1} cycles learned {model.LearnedAt:yyyy-MM-dd}"));
        return true;
    }

    /// <summary>
    /// Why a stored model can't be used, null when it can: all its numbers finite, a tooth count of 1 …
    /// <see cref="MaxTeeth"/> or a period of <see cref="MinPeriodSeconds"/> … <see cref="MaxPeriodSeconds"/>, the
    /// harmonics complete and no coefficient beyond <see cref="MaxStoredAmplitudeArcsec"/>.
    /// </summary>
    public static string? Invalid(PeriodicErrorModel model)
    {
        if (model.Sin.Count < Harmonics || model.Cos.Count < Harmonics)
        {
            return $"{Math.Min(model.Sin.Count, model.Cos.Count)} harmonics instead of {Harmonics}";
        }

        if (!double.IsFinite(model.PeriodSeconds) || !double.IsFinite(model.Cycles) || model.Sin.Concat(model.Cos).Any(v => !double.IsFinite(v)))
        {
            return "not a number";
        }

        if (model.Teeth is { } t ? t < 1 || t > MaxTeeth : model.PeriodSeconds < MinPeriodSeconds || model.PeriodSeconds > MaxPeriodSeconds)
        {
            return string.Create(CultureInfo.InvariantCulture, $"period {model.PeriodSeconds:F1} s, {model.Teeth?.ToString(CultureInfo.InvariantCulture) ?? "no"} teeth out of range");
        }

        if (model.Sin.Concat(model.Cos).Any(v => Math.Abs(v) > MaxStoredAmplitudeArcsec))
        {
            return string.Create(CultureInfo.InvariantCulture, $"a coefficient beyond ±{MaxStoredAmplitudeArcsec:F0}″");
        }

        return null;
    }

    /// <summary>
    /// The model to store (asked for while the prediction helps): only a significant fit in RA-axis arcseconds against
    /// the hour angle that passed the stability test on this session's data (a restored curve is applied at once, but
    /// stored again only once the data confirm it).
    /// </summary>
    public PeriodicErrorModel? ModelForStorage(DateTimeOffset now)
    {
        if (!IsSignificant || !verified || !axisUnits || hourAngleMode != true || period is not { } per)
        {
            return null;
        }

        return new PeriodicErrorModel
        {
            Teeth = teeth,
            PeriodSeconds = per,
            Sin = Enumerable.Range(0, Harmonics).Select(k => Math.Round(x[2 + 2 * k], 4)).ToList(),
            Cos = Enumerable.Range(0, Harmonics).Select(k => Math.Round(x[3 + 2 * k], 4)).ToList(),
            Cycles = Math.Round(Cycles, 1),
            LearnedAt = now,
        };
    }

    /// <summary>
    /// The period (s) of a detected curve that no longer holds since the last call, else null: a stored model with that
    /// period is to be discarded. That is a curve whose significant parts disagree, one a clearly stronger period
    /// replaced, or one dropped as too weak once another period is found; never a known tooth count.
    /// </summary>
    internal double? TakeDiscardedPeriod()
    {
        var p = discardedPeriod;
        discardedPeriod = null;
        return p;
    }

    /// <summary>A new guiding run (new lock position): level and drift start over, the curve stays.</summary>
    public void StartSegment()
    {
        segmentStart = true;
        guidingRunStart = true;
    }

    /// <summary>The lock position moved by <paramref name="px"/> (dither): the open-loop motion stays continuous.</summary>
    public void Dithered(double px)
    {
        if (double.IsFinite(px))
        {
            openLoopPx += px;
        }
    }

    /// <summary>A correction that went out (px, the amount by which a positive offset is reduced).</summary>
    public void CorrectionApplied(double px)
    {
        if (double.IsFinite(px))
        {
            openLoopPx += px;
        }
    }

    /// <summary>
    /// One measured offset (px). <paramref name="learn"/> false (settling, a jump) only keeps the open-loop motion
    /// continuous. The variances (px²) come from the filter bank: seeing, the mount's wander and drift change per frame;
    /// <paramref name="noiseFactor"/> is this frame's extra measurement noise (measurement uncertainty), which trusts it
    /// less in the fit.
    /// </summary>
    public void Observe(double time, double offsetPx, PeriodicErrorContext context, bool learn, double seeingPx2, double wanderPx2,
        double driftPx2, double noiseFactor = 1)
    {
        bool hourAngle = context.AxisHours is not null;
        if (hourAngleMode is { } mode && mode != hourAngle)
        {
            // the phase reference changed (mount connected or lost): the fitted phase no longer applies
            Note(hourAngle ? "periodic error phase now from the hour angle: curve re-learned" : "periodic error phase now from the time (no mount information): curve re-learned");
            ReEstimatePhase();
            ClearSamples();
        }

        if (restored && !hourAngle)
        {
            // a stored curve is phased against the hour angle: without mount information only its period holds
            Note("stored periodic error curve not usable without mount information: phase re-learned from the time");
            ReEstimatePhase();
        }

        hourAngleMode = hourAngle;
        if (context.PierSide != lastPierSide && lastPierSide != PierSide.Unknown && context.PierSide != PierSide.Unknown)
        {
            OnFlip();
        }

        if (context.PierSide != PierSide.Unknown)
        {
            lastPierSide = context.PierSide;
        }

        double scale = AxisScale(context, out bool units);
        if (units != axisUnits && (fitActive || samples.Count > 0))
        {
            // px ↔ arcsec: start the fit and the samples over in the new units (a stored curve needs arcsec)
            ClearSamples();
            if (restored)
            {
                Note("stored periodic error curve not usable without pixel scale and declination: learned anew");
                Forget();
            }
            else
            {
                ResetFit();
                stable &= teeth is null;
                verified &= teeth is null;
            }
        }

        axisUnits = units;
        lastScale = scale;
        timeOrigin ??= time;
        double tau = Tau(time, context);
        double u = (offsetPx + openLoopPx) * scale;
        NextRun(time, tau, hourAngle);
        if (segmentStart)
        {
            segmentStart = false;
            segment++;
            segmentOrigin = tau;
            StartLevel(u, tau);
        }

        if (!learn)
        {
            return;
        }

        double r = Math.Max(seeingPx2 * noiseFactor, 1e-4) * scale * scale;
        if (period is { } per && fitActive)
        {
            double phase = 2 * Math.PI * tau / per;
            if (lastPhase is { } lp && phase > lp)
            {
                cycles += Math.Min(phase - lp, 1.0) / (2 * Math.PI);
            }

            lastPhase = phase;
            FitStep(tau, u, r, Math.Min(wanderPx2 * scale * scale, LevelNoiseCap * r), Math.Min(driftPx2 * scale * scale, DriftNoiseCap * r), per, FrameSeconds());
            UpdateSignificance();
        }

        Store(tau, u, r, Math.Max(seeingPx2, 1e-4) * scale * scale, context.PierSide);

        // a detected period is refined, checked and searched on; the curve of a known tooth count (set, snapped or
        // restored with it) only checked. One step per frame.
        if (job is null && tau >= nextDetect)
        {
            job = (teeth is null ? Detect(tau) : CheckCurve(tau)).GetEnumerator();
        }

        if (job is not null && !job.MoveNext())
        {
            job.Dispose();
            job = null;
        }

        UpdateNegligible(tau);
    }

    /// <summary>Periodic error in px at <paramref name="time"/> (0 without a period).</summary>
    public double PredictPx(double time, PeriodicErrorContext context)
    {
        if (period is not { } per || !fitActive)
        {
            return 0;
        }

        double scale = AxisScale(context, out _);
        double tau = Tau(time, context);
        return Curve(2 * Math.PI * tau / per) / scale;
    }

    /// <summary>Snapshot for the UI (amplitude on the sky at the declination of <paramref name="context"/>).</summary>
    public PeriodicErrorState State(PeriodicErrorPhase phase, double weight, PeriodicErrorContext? context)
    {
        double? amplitude = null;
        bool arcsec = false;
        if (fitActive && Amplitude > 0)
        {
            if (axisUnits && context?.DeclinationDeg is { } dec)
            {
                amplitude = Amplitude * Math.Cos(dec * Math.PI / 180.0);
                arcsec = true;
            }
            else
            {
                amplitude = axisUnits ? Amplitude / lastScale : Amplitude;
            }
        }

        return new PeriodicErrorState
        {
            Phase = phase,
            Progress = Progress,
            PeriodSeconds = period,
            Teeth = teeth,
            Amplitude = amplitude,
            AmplitudeInArcsec = arcsec,
            Weight = weight,
            Stable = stable,
        };
    }

    /// <summary>Short description for the log.</summary>
    public string Describe()
    {
        if (period is not { } per)
        {
            return "no period yet";
        }

        // a tooth count only for a worm: set, or snapped from a stable period (any other period is just a period)
        string unit = axisUnits ? "″ (RA axis)" : " px";
        string t = teeth is { } n ? string.Create(CultureInfo.InvariantCulture, $"{n} teeth") : string.Create(CultureInfo.InvariantCulture, $"{per / 60:F1} min");
        var a = Enumerable.Range(0, Harmonics).Select(k => Math.Sqrt(x[2 + 2 * k] * x[2 + 2 * k] + x[3 + 2 * k] * x[3 + 2 * k])).ToArray();
        return string.Create(CultureInfo.InvariantCulture,
            $"period {per:F1} s ({t}), ±{a[0]:F2}/{a[1]:F2}/{a[2]:F2}{unit}, {Cycles:F1} cycles, {(IsSignificant ? "significant" : fitSignificant ? "not stable" : "not significant")}{(IsNegligible ? ", small against the seeing" : "")}");
    }

    /// <summary>Notes since the last call.</summary>
    public IReadOnlyList<string> TakeNotes()
    {
        if (notes.Count == 0)
        {
            return [];
        }

        var taken = notes.ToArray();
        notes.Clear();
        return taken;
    }

    // RA-axis arcsec per px (pixel scale / cos dec), or 1 (px units) when either is unknown
    private static double AxisScale(PeriodicErrorContext context, out bool units)
    {
        if (context.PixelScale > 0 && context.DeclinationDeg is { } dec && Math.Abs(dec) < 89)
        {
            units = true;
            return context.PixelScale / Math.Cos(dec * Math.PI / 180.0);
        }

        units = false;
        return 1;
    }

    // phase variable in tracking seconds: the RA axis angle, or the time since the first frame
    private double Tau(double time, PeriodicErrorContext context) =>
        context.AxisHours is { } h ? h * Sidereal.HourSeconds : time - (timeOrigin ?? time);

    private double Curve(double phase)
    {
        double v = 0;
        for (int k = 0; k < Harmonics; k++)
        {
            double a = (k + 1) * phase;
            v += x[2 + 2 * k] * Math.Sin(a) + x[3 + 2 * k] * Math.Cos(a);
        }

        return v;
    }

    private void ResetFit()
    {
        Array.Clear(x);
        Array.Clear(p);
        p[0, 0] = 1e6;
        p[1, 1] = 1e-2;

        // about ±15 px of periodic error before any data (in the fit's units, set once the scale is known)
        for (int i = 2; i < Parameters; i++)
        {
            p[i, i] = 225 * lastScale * lastScale;
        }

        cycles = 0;
        lastPhase = null;
        fitSignificant = IsSignificant = false;
        segmentStart = true;
    }

    // no samples: a stability run starts with the next one, a search or check in progress is abandoned
    private void ClearSamples()
    {
        samples.Clear();
        job?.Dispose();
        job = null;
        lastTime = null;
        StartRun();
    }

    // a new stability run (and guiding run for the fit's level): nothing of it yet
    private void StartRun()
    {
        run++;
        runCovered = 0;
        segmentStart = true;
    }

    // A slew or a sync (the axis angle moved by more than the time that passed) or, without the axis angle, a new
    // guiding run (a slew can't be told) starts a new stability run; a long gap only a new level
    private void NextRun(double time, double tau, bool hourAngle)
    {
        if (lastTime is { } previous)
        {
            double dt = time - previous;
            if (hourAngle ? Math.Abs(tau - lastTau - dt) > AxisJumpSeconds : guidingRunStart)
            {
                StartRun();
            }
            else if (dt > LongGapSeconds)
            {
                segmentStart = true;
            }
        }

        guidingRunStart = false;
        lastTime = time;
        lastTau = tau;
    }

    // A learning frame into the samples: averaged into the last one while closer than MinSampleSeconds to its first
    // frame (same guiding run), else a new one; the start and the covered time of the current stability run follow
    private void Store(double tau, double u, double r, double seeing, PierSide side)
    {
        if (samples.Count > 0 && samples[^1] is var last && last.Run == run && last.Segment == segment && tau > last.Start
            && tau - last.Start < MinSampleSeconds)
        {
            int n = last.Frames;
            samples[^1] = last with
            {
                Tau = (last.Tau * n + tau) / (n + 1),
                U = (last.U * n + u) / (n + 1),
                R = (last.R * n + r) / (n + 1),
                Seeing = (last.Seeing * n + seeing) / (n + 1),
                Frames = n + 1,
            };
            return;
        }

        if (samples.Count == 0 || samples[^1].Run != run)
        {
            runStart = samples.Count;
        }
        else if (samples[^1].Segment == segment && tau - samples[^1].Tau is > 0 and <= LongGapSeconds and var dt)
        {
            runCovered += dt;
        }

        samples.Add(new Sample(tau, u, r, seeing, tau, 1, segment, run, side));
        if (samples.Count > MaxSamples)
        {
            int k = samples.Count - MaxSamples;
            samples.RemoveRange(0, k);
            runStart = Math.Max(0, runStart - k);
        }
    }

    // a new level (and drift) for a new guiding run: uncorrelated with the curve, which stays
    private void StartLevel(double u, double tau)
    {
        double curve = period is { } per && fitActive ? Curve(2 * Math.PI * tau / per) : 0;
        for (int i = 0; i < Parameters; i++)
        {
            p[0, i] = p[i, 0] = 0;
            p[1, i] = p[i, 1] = 0;
        }

        x[0] = u - curve;
        x[1] = 0;
        p[0, 0] = 1e6;
        p[1, 1] = 1e-2;
    }

    // one Kalman step of the parameters: level and drift follow the mount's wander, the harmonics wander slowly (steps of
    // stepSeconds: a memory of a few cycles)
    private void FitStep(double tau, double u, double r, double qLevel, double qDrift, double per, double stepSeconds)
    {
        double framesPerCycle = Math.Max(per / Math.Max(stepSeconds, 0.5), 10);
        double scaleRef = Math.Max(Amplitude, Math.Sqrt(r));
        double qCurve = 0.01 * scaleRef * scaleRef / framesPerCycle;
        p[0, 0] += Math.Max(qLevel, 1e-8);
        p[1, 1] += Math.Max(qDrift, 1e-12);
        for (int i = 2; i < Parameters; i++)
        {
            p[i, i] += qCurve;
        }

        var (h, ph) = (fitH, fitPh);
        h[0] = 1;
        h[1] = tau - segmentOrigin;
        Phasors(h, 2, 1 / per, tau);
        for (int i = 0; i < Parameters; i++)
        {
            double v = 0;
            for (int j = 0; j < Parameters; j++)
            {
                v += p[i, j] * h[j];
            }

            ph[i] = v;
        }

        double s = r;
        double predicted = 0;
        for (int i = 0; i < Parameters; i++)
        {
            s += h[i] * ph[i];
            predicted += h[i] * x[i];
        }

        double nu = u - predicted;
        for (int i = 0; i < Parameters; i++)
        {
            x[i] += ph[i] / s * nu;
        }

        for (int i = 0; i < Parameters; i++)
        {
            for (int j = 0; j < Parameters; j++)
            {
                p[i, j] -= ph[i] * ph[j] / s;
            }
        }
    }

    // typical time between frames (tracking seconds): over the last samples, each averaged from Frames frames
    private double FrameSeconds()
    {
        int n = samples.Count;
        if (n < 2)
        {
            return 2;
        }

        int k = Math.Min(n - 1, 20), frames = 0;
        for (int i = n - k; i < n; i++)
        {
            frames += samples[i].Frames;
        }

        double dt = (samples[n - 1].Tau - samples[n - 1 - k].Tau) / frames;
        return dt > 0 ? dt : 2;
    }

    private void UpdateSignificance()
    {
        double a1 = Math.Sqrt(x[2] * x[2] + x[3] * x[3]);
        if (a1 <= 0 || !fitActive)
        {
            fitSignificant = IsSignificant = false;
            return;
        }

        double var = (x[2] * x[2] * p[2, 2] + x[3] * x[3] * p[3, 3] + 2 * x[2] * x[3] * p[2, 3]) / (a1 * a1);
        bool covered = restored || Cycles >= 1;
        fitSignificant = covered && a1 > SignificanceSigmas * Math.Sqrt(Math.Max(var, 0));
        IsSignificant = fitSignificant && stable;
    }

    // a flip turns the axis by 180°: exact with a known tooth count, else the phase is learned anew (Q12); either way a
    // new stability run
    private void OnFlip()
    {
        StartRun();
        if (teeth is not null)
        {
            Note("meridian flip: periodic error phase follows from the tooth count");
            return;
        }

        Note("meridian flip without a known tooth count: periodic error phase re-learned, prediction paused");
        ReEstimatePhase();
    }

    // keeps the period and the size of the curve, but not its phase
    private void ReEstimatePhase()
    {
        double a = Math.Max(Amplitude, 0.5 * lastScale);
        for (int i = 2; i < Parameters; i++)
        {
            for (int j = 0; j < Parameters; j++)
            {
                p[i, j] = p[j, i] = 0;
            }

            p[i, i] = a * a;
        }

        restored = false;
        restoredCycles = 0;
        cycles = 0;
        lastPhase = null;
        fitSignificant = IsSignificant = false;

        // a detected period stays held; the curve of a known tooth count is checked again with its new phase
        if (teeth is not null)
        {
            stable = verified = false;
            unstableNoted = false;
        }
    }

    // the samples of the full search: with an uncertain tooth count only those of the current pier side
    private List<Sample> CurrentSide() =>
        teeth is null && lastPierSide != PierSide.Unknown ? samples.FindAll(s => s.Side == lastPierSide || s.Side == PierSide.Unknown) : samples;

    // the samples of the current stability run, which the refinement, the stability test and the size check judge:
    // between runs a slew with a plate-solve sync can shift the curve's phase against the hour angle (the live fit
    // catches up with it)
    private List<Sample> CurrentRun() =>
        samples.Count > 0 && samples[^1].Run == run ? samples.GetRange(runStart, samples.Count - runStart) : [];

    // tracking seconds between sample i − 1 and i when they are of the same guiding run and no long gap apart, else 0
    private static double Interval(List<Sample> data, int i)
    {
        double dt = data[i].Tau - data[i - 1].Tau;
        return data[i].Segment == data[i - 1].Segment && dt > 0 && dt <= LongGapSeconds ? dt : 0;
    }

    // the last SearchSamples of the data, more while they cover less than MinSearchSeconds (frames averaged into one)
    private static List<Sample> Recent(List<Sample> data)
    {
        int from = Math.Max(0, data.Count - SearchSamples);
        double covered = 0;
        for (int i = from + 1; i < data.Count; i++)
        {
            covered += Interval(data, i);
        }

        for (; from > 0 && covered < MinSearchSeconds; from--)
        {
            covered += Interval(data, from);
        }

        return from == 0 ? data : data.GetRange(from, data.Count - from);
    }

    // the data as the search and the tests use them: detrended (each guiding run on its own), each sample's guiding run,
    // and the tracking seconds covered up to it
    private static Series Prepare(List<Sample> data)
    {
        var (t, y) = Detrend(data);
        var piece = new int[data.Count];
        var covered = new double[data.Count];
        for (int i = 0; i < data.Count; i++)
        {
            piece[i] = data[i].Segment;
            covered[i] = i == 0 ? 0 : covered[i - 1] + Interval(data, i);
        }

        return new Series(t, y, piece, covered);
    }

    // Without a period: the full search. With a detected one (no tooth count): refined while the tooth count is open,
    // checked for stability (dropped when it fails), and every SearchEverySeconds the full range searched again for a
    // clearly stronger stable period. The search looks at the recent data of this pier side, the refinement and the
    // stability test at the stability run: a mechanical periodic error holds all night, a wobble that looked steady for
    // a while does not. The data are taken at the start; one step per frame.
    private IEnumerable<bool> Detect(double tau)
    {
        nextDetect = tau + (period is null ? DetectEverySeconds : RefineEverySeconds);
        var side = CurrentSide();
        if (side.Count < 100)
        {
            yield break;
        }

        var recent = Prepare(Recent(side));
        var run = Prepare(CurrentRun());
        if (period is not { } held)
        {
            foreach (bool step in Search(recent, null, tau, run))
            {
                yield return step;
            }

            yield break;
        }

        var refined = Refine(run, held);
        yield return true;
        var check = Stability(run, 1 / (refined?.Period ?? held), KeepPartSigmas, 2);
        failedChecks = check.Verdict == StabilityVerdict.Unstable ? failedChecks + 1 : 0;
        verified |= check.Verdict == StabilityVerdict.Stable;
        if (failedChecks >= DropAfterFailures)
        {
            Drop(held, check, tau);
            yield break;
        }

        if (refined is { } r)
        {
            // the tooth count only from a period that passed the stability test here
            yield return true;
            Adopt(r, check.Verdict == StabilityVerdict.Stable);
        }

        if (teeth is null && tau >= nextSearch)
        {
            nextSearch = tau + SearchEverySeconds;
            foreach (bool step in Search(recent, period, tau, run))
            {
                yield return step;
            }
        }
    }

    // The full range: the strongest periodogram peaks in turn (the grid's ends are no peaks); the first that is
    // significant, stands out from the noise and passes the stability test is accepted, or with a period held (keep
    // searching) takes over when its power is at least SwitchPower × the held period's on the same data. A peak outside
    // the range, with no peak of its own on the stability run, or next to one already tried is skipped. One step per
    // frame: the periodogram, then per candidate its peak on the recent data, on the run, and the test.
    private IEnumerable<bool> Search(Series recent, double? held, double tau, Series run)
    {
        var (t, y) = (recent.T, recent.Y);
        double span = recent.Span;
        if (t.Length < 100 || span < DetectCycles * MinPeriodSeconds)
        {
            yield break;
        }

        double fMin = 1.0 / Math.Min(MaxPeriodSeconds, span / DetectCycles);
        double fMax = 1.0 / MinPeriodSeconds;
        double df = 1.0 / (6 * span);
        int count = fMax > fMin ? (int)Math.Floor((fMax - fMin) / df) + 1 : 0;
        if (count < 3)
        {
            yield break;
        }

        var power = Powers(t, y, fMin, df, count, Harmonics);
        var sorted = (double[])power.Clone();
        Array.Sort(sorted);
        double median = sorted[count / 2];
        var peaks = Enumerable.Range(1, count - 2)
            .Where(i => power[i] >= power[i - 1] && power[i] >= power[i + 1])
            .OrderByDescending(i => power[i])
            .Take(MaxCandidates)
            .ToList();
        double heldPower = held is { } hp ? HarmonicPower(t, y, 1 / hp) : 0;
        var tried = new List<double>();
        var tested = new List<double>();
        var rejected = new List<string>();
        foreach (int i in peaks)
        {
            yield return true;

            // the peak itself by golden-section search: a grid would quantise the period at the scale the tooth count needs
            double g = fMin + i * df;
            var (f, pw) = RefinePeak(t, y, g - df, g + df);
            if (pw <= PeakFactor * median || f < fMin || f > fMax || tried.Exists(v => Math.Abs(v - f) < 0.5 / span))
            {
                continue;
            }

            tried.Add(f);
            if (held is { } h && (Math.Abs(f - 1 / h) <= RefineWidth(h) || pw < SwitchPower * heldPower))
            {
                // the held period itself, or not clearly stronger
                continue;
            }

            var fit = FitHarmonics(t, y, f);
            if (fit.Amplitudes[0] + fit.Amplitudes[1] < 4 * fit.Sigma * Math.Sqrt(2.0 / t.Length))
            {
                continue;
            }

            // the peak again on the data of the stability run, which the test judges: the last hour places it only to
            // about a cycle over its span, which over several hours turns into a phase that seems to change
            yield return true;
            if (PeakOn(run, f, 1 / span) is not { } near)
            {
                continue;
            }

            yield return true;
            double fr = RefinePeak(run.T, run.Y, near - 1 / (6 * run.Span), near + 1 / (6 * run.Span)).F;
            if (fr < fMin || fr > fMax || tested.Exists(v => Math.Abs(v - fr) < 0.5 / span))
            {
                continue;
            }

            tested.Add(fr);
            yield return true;
            var check = Stability(run, fr, PartSigmas, AcceptParts);
            if (check.Verdict == StabilityVerdict.Unstable)
            {
                rejected.Add(string.Create(CultureInfo.InvariantCulture, $"{1 / fr:F1} s ({check.Text})"));
            }

            if (check.Verdict != StabilityVerdict.Stable)
            {
                continue;
            }

            yield return true;
            Accept(Candidate(run.T, fr, run.Span, FitHarmonics(run.T, run.Y, fr)), held, pw / Math.Max(heldPower, 1e-12), span, tau, check.Text);
            yield break;
        }

        if (rejected.Count > 0 && tau >= nextRejectionNote)
        {
            // at most one such note per search interval (the search runs every minute without a period)
            nextRejectionNote = tau + SearchEverySeconds;
            Note($"periodic error candidates not stable, not taken: {string.Join("; ", rejected)}");
        }
    }

    // a period found by the search: detected, or taking over from the held one (whose stored curve goes); a period
    // dropped earlier as too weak gives its stored curve up now, unless it is found again
    private void Accept(DetectedPeriod candidate, double? held, double powerRatio, double span, double tau, string why)
    {
        if (held is { } old)
        {
            Note(string.Create(CultureInfo.InvariantCulture,
                $"periodic error period {old:F1} s replaced by {candidate.Period:F1} ± {candidate.Sigma:F1} s ({candidate.Period / 60:F1} min), {powerRatio:F1}× its harmonic power over the last {span / 60:F0} min; stable: {why}"));
            discardedPeriod = old;
            teeth = null;
            restored = false;
            restoredCycles = 0;
            recentTeeth.Clear();
        }
        else
        {
            Note(string.Create(CultureInfo.InvariantCulture,
                $"periodic error period {candidate.Period:F1} ± {candidate.Sigma:F1} s ({candidate.Period / 60:F1} min) detected from {candidate.Span / 60:F0} min; stable: {why}"));
            if (weakPeriod is { } weak && Math.Abs(candidate.Period - weak) > DiscardDifference * weak)
            {
                Note(string.Create(CultureInfo.InvariantCulture, $"periodic error period {weak:F1} s, dropped earlier, gives way: its stored curve discarded"));
                discardedPeriod = weak;
            }
        }

        weakPeriod = null;
        period = candidate.Period;
        periodSigma = candidate.Sigma;
        stable = verified = true;
        recentTeeth.Add(candidate.Teeth);
        nextSearch = tau + SearchEverySeconds;
        nextSizeCheck = double.NegativeInfinity;
        failedChecks = 0;
        fitActive = true;
        Replay();
    }

    // The curve of a known tooth count (set, snapped from a stable period, or restored with it): the period is certain,
    // the curve is not (a weak worm under the mount's wander fits differently from one part of the night to the next).
    // It counts only once it passes the stability test on the stability run, checked every minute until there is enough
    // data for a verdict, then every RefineEverySeconds; once stable, two failed checks in a row withdraw it, but the
    // period, the tooth count and a stored curve stay.
    private IEnumerable<bool> CheckCurve(double tau)
    {
        nextDetect = tau + DetectEverySeconds;
        var data = CurrentRun();
        if (data.Count < 100 || period is not { } per)
        {
            yield break;
        }

        var check = Stability(Prepare(data), 1 / per, stable ? KeepPartSigmas : PartSigmas, 2);
        if (check.Verdict == StabilityVerdict.Undecided)
        {
            yield break;
        }

        nextDetect = tau + RefineEverySeconds;
        bool wasStable = stable;
        if (check.Verdict == StabilityVerdict.Stable)
        {
            failedChecks = 0;
            stable = verified = true;
            unstableNoted = false;
        }
        else if (!stable || ++failedChecks >= DropAfterFailures)
        {
            failedChecks = 0;
            stable = false;
        }

        UpdateSignificance();
        if (stable && !wasStable)
        {
            Note(string.Create(CultureInfo.InvariantCulture, $"periodic error curve stable: {check.Text}"));
        }
        else if (!stable && !unstableNoted)
        {
            unstableNoted = true;
            Note(string.Create(CultureInfo.InvariantCulture,
                $"periodic error curve {(wasStable ? "no longer" : "not")} stable ({check.Text}): not applied; {Describe()}"));
        }
    }

    // the highest harmonic power of the data within ± halfWidth of f on a grid of a sixth of its resolution (the peak
    // itself is then refined within a grid step); null when the highest is at the edge (the power rises towards another
    // period: no peak of its own here)
    private static double? PeakOn(Series data, double f, double halfWidth)
    {
        if (data.T.Length < 100 || data.Span <= 0)
        {
            return null;
        }

        double df = 1.0 / (6 * data.Span);
        int count = (int)Math.Floor(2 * halfWidth / df) + 1;
        if (count < 3)
        {
            return null;
        }

        var power = Powers(data.T, data.Y, f - halfWidth, df, count, Harmonics);
        int best = Array.IndexOf(power, power.Max());
        if (best == 0 || best == count - 1)
        {
            return null;
        }

        return f - halfWidth + best * df;
    }

    // the refinement near the held period over the stability run, null when it has no peak there that stands out
    private DetectedPeriod? Refine(Series data, double held)
    {
        var (t, y) = (data.T, data.Y);
        double span = data.Span;
        double f0 = 1.0 / held;
        double width = RefineWidth(held);
        double fMin = Math.Max(1.0 / Math.Min(MaxPeriodSeconds, span / DetectCycles), f0 - width);
        double fMax = Math.Min(1.0 / MinPeriodSeconds, f0 + width);
        if (t.Length < 100 || span <= 0 || fMax <= fMin)
        {
            return null;
        }

        double df = 1.0 / (6 * span);
        int count = (int)Math.Floor((fMax - fMin) / df) + 1;
        var power = Powers(t, y, fMin, df, count, Harmonics);
        int best = Array.IndexOf(power, power.Max());

        // highest at the edge of the window: the power rises towards another period, the held one has no peak here
        if (power[best] <= 0 || best == 0 || best == count - 1)
        {
            return null;
        }

        double bestF = fMin + best * df;
        (bestF, _) = RefinePeak(t, y, bestF - df, bestF + df);
        var fit = FitHarmonics(t, y, bestF);
        if (fit.Amplitudes[0] + fit.Amplitudes[1] < 4 * fit.Sigma * Math.Sqrt(2.0 / t.Length) || 1 / bestF > span / DetectCycles)
        {
            return null;
        }

        return Candidate(t, bestF, span, fit);
    }

    // the refined period; a whole tooth count only when it is unambiguous and the period stable: precise, and the last
    // refinements all close to the same integer
    private void Adopt(DetectedPeriod refined, bool stable)
    {
        double? previous = period;
        period = refined.Period;
        periodSigma = refined.Sigma;
        int rounded = (int)Math.Round(refined.Teeth);
        recentTeeth.Add(refined.Teeth);
        if (recentTeeth.Count > SnapAgreement)
        {
            recentTeeth.RemoveAt(0);
        }

        bool agreed = recentTeeth.Count == SnapAgreement && recentTeeth.All(e => Math.Abs(e - rounded) <= SnapTolerance);
        if (stable && refined.SigmaTeeth < SnapSigmaTeeth && agreed && rounded > 0)
        {
            teeth = rounded;
            period = Sidereal.DaySeconds / rounded;
            periodSigma = 0;
            Note(string.Create(CultureInfo.InvariantCulture,
                $"worm: {rounded} teeth (period {period:F1} s; {refined.Teeth:F2} ± {refined.SigmaTeeth:F2} teeth from {refined.Span / 60:F0} min)"));
        }

        if (previous is not { } prev || Math.Abs(period.Value - prev) > 1e-4 * prev)
        {
            fitActive = true;
            Replay();
        }
    }

    // Cramér–Rao bound of a harmonic signal's frequency (its precision grows with the span to the power 1.5), with the
    // effective number of independent samples: the residual is not white (the mount's wander, drift changes), and the
    // white-noise bound was ~10× too optimistic in the simulator
    private static DetectedPeriod Candidate(double[] t, double f, double span, (double[] Amplitudes, double Sigma, double[] Residual) fit)
    {
        double amplitudeWeight = Math.Sqrt(fit.Amplitudes.Select((a, k) => (k + 1) * (k + 1) * a * a).Sum());
        double sigmaF = Math.Sqrt(6.0 / EffectiveSamples(t, fit.Residual)) * fit.Sigma / (Math.PI * Math.Max(amplitudeWeight, 1e-9) * span);
        double p = 1 / f;
        return new DetectedPeriod(p, sigmaF * p * p, Sidereal.DaySeconds / p, Sidereal.DaySeconds * sigmaF, span);
    }

    // half-width (frequency) of the refinement around a held period: 3 σ, at least 2 %
    private double RefineWidth(double held) => Math.Max(3 * periodSigma / (held * held), 0.02 / held);

    // The held detected period stopped being stable: forgotten with its curve, the search starts over. Its stored curve
    // is discarded when significant parts disagree (the curve changes); one only too weak on this data (a poor night)
    // stays stored until another period is found.
    private void Drop(double held, StabilityResult check, double tau)
    {
        string stored = check.Contradicted ? "with a stored curve" : "(a stored curve is kept until another period is found)";
        Note(string.Create(CultureInfo.InvariantCulture, $"periodic error period {held:F1} s no longer stable ({check.Text}): forgotten {stored}, searching again"));
        if (check.Contradicted)
        {
            discardedPeriod = held;
            weakPeriod = null;
        }
        else
        {
            weakPeriod = held;
        }

        period = null;
        teeth = null;
        periodSigma = double.PositiveInfinity;
        fitActive = false;
        stable = verified = false;
        restored = false;
        restoredCycles = 0;
        recentTeeth.Clear();
        ResetFit();

        // the samples stay one guiding run: no new level (or detrending segment) at the next frame
        segmentStart = false;
        IsNegligible = false;
        nextDetect = tau + DetectEverySeconds;
        nextSearch = double.NegativeInfinity;
        nextSizeCheck = double.NegativeInfinity;
        failedChecks = 0;
    }

    /// <summary>
    /// Stability test of a period (1/<paramref name="f"/>) on the detrended data: is it a mechanical periodic error,
    /// which keeps its amplitude and its phase against the RA axis, or a wobble of the mount that comes and goes?
    /// </summary>
    /// <remarks>
    /// Each of 2-6 consecutive parts (one per cycle of the data they cover, each guiding run's level and drift taken out
    /// first together with the curve) gets its own fit of level, drift and the harmonics; the noise at each harmonic comes
    /// from the periodogram of what the harmonics leave of all data (see NoiseLevels). Stable when every part's fundamental
    /// is significant and the parts agree in the curve's total amplitude and the fundamental's phase (see PartSigmas);
    /// contradicted when two significant parts disagree. Undecided with too little data.
    /// </remarks>
    private StabilityResult Stability(Series data, double f, double partSigmas, int minParts)
    {
        var t = data.T;
        double span = data.Span;
        int count = Math.Min((int)(span * f / MinPartCycles), MaxParts);
        if (count < minParts || t.Length < 100)
        {
            return new StabilityResult(StabilityVerdict.Undecided, "too little data", false);
        }

        // A check of a held curve takes fewer, longer parts (down to minParts) when that keeps them from spanning a pause.
        // A new period keeps its parts: one across a pause is judged with more uncertainty, which only makes it harder
        var cuts = PartCuts(data, f, count);
        for (int fewer = count - 1; minParts < AcceptParts && fewer >= minParts && Straddles(data, f, cuts); fewer--)
        {
            var other = PartCuts(data, f, fewer);
            if (!Straddles(data, f, other))
            {
                (cuts, count) = (other, fewer);
            }
        }

        var y = Unlevelled(data, f);
        var residual = FitHarmonics(t, y, f).Residual;
        if (NoiseLevels(t, residual, f, span, span / count) is not { } noise)
        {
            return new StabilityResult(StabilityVerdict.Undecided, "noise unknown", false);
        }

        var parts = new List<PartFit>();
        for (int k = 0; k < count; k++)
        {
            if (FitPart(t, y, data.Covered, cuts[k], cuts[k + 1], f, noise) is not { } part)
            {
                return new StabilityResult(StabilityVerdict.Undecided, "parts too short", false);
            }

            parts.Add(part);
        }

        string unit = axisUnits ? "″" : " px";
        string text = string.Create(CultureInfo.InvariantCulture,
            $"{count} parts ±{string.Join("/", parts.Select(q => q.Amplitude.ToString("F2", CultureInfo.InvariantCulture)))}{unit} (noise ±{string.Join("/", parts.Select(q => q.AmplitudeSigma.ToString("F2", CultureInfo.InvariantCulture)))}) at {string.Join("/", parts.Select(q => (((q.Phase * 180 / Math.PI) % 360 + 360) % 360).ToString("F0", CultureInfo.InvariantCulture)))}°");
        var why = new List<string>();
        if (parts.Any(q => q.Amplitude < partSigmas * q.AmplitudeSigma))
        {
            why.Add("not significant in every part");
        }

        bool contradicted = false;
        for (int i = 0; i < parts.Count; i++)
        {
            for (int j = i + 1; j < parts.Count; j++)
            {
                var (a, b) = (parts[i], parts[j]);
                bool changes = false;
                double ratio = Math.Max(a.Total, b.Total) / Math.Max(Math.Min(a.Total, b.Total), 1e-12);
                if (ratio > PartAmplitudeRatio && Math.Abs(a.Total - b.Total) > PartDifferenceSigmas * Math.Sqrt(a.TotalSigma * a.TotalSigma + b.TotalSigma * b.TotalSigma))
                {
                    why.Add("amplitude changes");
                    changes = true;
                }

                double dPhase = Math.Abs(Math.IEEERemainder(a.Phase - b.Phase, 2 * Math.PI));
                double tolerance = Math.Max(PartPhaseDegrees * Math.PI / 180, PartDifferenceSigmas * Math.Sqrt(a.PhaseSigma * a.PhaseSigma + b.PhaseSigma * b.PhaseSigma));
                if (dPhase > tolerance)
                {
                    why.Add("phase changes");
                    changes = true;
                }

                contradicted |= changes && a.Amplitude >= partSigmas * a.AmplitudeSigma && b.Amplitude >= partSigmas * b.AmplitudeSigma;
            }
        }

        return why.Count == 0
            ? new StabilityResult(StabilityVerdict.Stable, text, false)
            : new StabilityResult(StabilityVerdict.Unstable, string.Join(", ", why.Distinct()) + ": " + text, contradicted);
    }

    // Where the parts of the stability test start (and the last one ends): equal shares of the covered time, a gap no
    // part of a cycle. A cut moves to the start of a guiding run nearby while every part keeps 0.9 of a cycle: a part
    // across a pause covers the cycle's phases with a hole, which pins the curve down less well.
    private static int[] PartCuts(Series data, double f, int count)
    {
        var (covered, piece) = (data.Covered, data.Piece);
        double span = data.Span, min = 0.9 * MinPartCycles / f;
        var cuts = new int[count + 1];
        cuts[count] = covered.Length;
        for (int k = 1; k < count; k++)
        {
            double from = covered[cuts[k - 1]], share = (span - from) / (count - k + 1), target = from + share, reach = 0.5 * share;
            int cut = cuts[k - 1] + 1;
            while (cut < covered.Length && covered[cut] < target)
            {
                cut++;
            }

            for (int i = cuts[k - 1] + 1; i < covered.Length && covered[i] - target <= reach && span - covered[i] >= (count - k) * min; i++)
            {
                if (piece[i] != piece[i - 1] && covered[i] - from >= min && Math.Abs(covered[i] - target) <= reach)
                {
                    reach = Math.Abs(covered[i] - target);
                    cut = i;
                }
            }

            cuts[k] = cut;
        }

        return cuts;
    }

    // true when a part spans a pause with a quarter of a cycle of data or more on either side of it
    private static bool Straddles(Series data, double f, int[] cuts)
    {
        double quarter = 0.25 * MinPartCycles / f;
        for (int k = 0; k + 1 < cuts.Length; k++)
        {
            for (int i = cuts[k] + 1; i < cuts[k + 1]; i++)
            {
                if (data.Piece[i] != data.Piece[i - 1] && data.Covered[i] - data.Covered[cuts[k]] >= quarter
                    && data.Covered[cuts[k + 1] - 1] - data.Covered[i] >= quarter)
                {
                    return true;
                }
            }
        }

        return false;
    }

    // Noise level at each harmonic k·f for fits over parts of partSpan (as the variance of white noise with that
    // periodogram level) from what the harmonics leave of all data: ln P(g) ≈ A + B ln g, least squares of the log
    // periodogram ordinates (spacing 1/span, those next to a harmonic left out) over f/3 … 3f, the log bias added back;
    // the mount's wander is red noise, and a white level, or the median over all periods, flatters long periods (Vaughan
    // 2005, A&A 431, 391). When higher, the ordinates seen through the spectral window of a part (Fejér kernel
    // sinc²((g − k·f) · partSpan), out to its 4th zero): strong motion at a nearby period, a wobble or a transient,
    // leaks into the fit of a part that short, though all the data resolve it. A flat power law with fewer than 3
    // ordinates, null without any ordinate.
    private static double[]? NoiseLevels(double[] t, double[] residual, double f, double span, double partSpan)
    {
        double window = 4 / partSpan;
        int j0 = Math.Max(1, (int)Math.Ceiling(Math.Min(f / 3, f - window) * span)), j1 = (int)Math.Floor((Harmonics * f + window) * span);
        if (j1 < j0)
        {
            return null;
        }

        var ordinates = Powers(t, residual, j0 / span, 1 / span, j1 - j0 + 1, 1);
        var lx = new List<double>();
        var ly = new List<double>();
        var sum = new double[Harmonics];
        var weight = new double[Harmonics];
        for (int j = j0; j <= j1; j++)
        {
            double g = j / span;
            bool nearHarmonic = false;
            for (int k = 1; k <= Harmonics; k++)
            {
                nearHarmonic |= Math.Abs(g - k * f) < 0.75 / span;
            }

            double pw = nearHarmonic ? 0 : ordinates[j - j0];
            if (pw <= 0)
            {
                continue;
            }

            if (g >= f / 3 && g <= Harmonics * f)
            {
                lx.Add(Math.Log(g));
                ly.Add(Math.Log(pw));
            }

            for (int k = 0; k < Harmonics; k++)
            {
                double u = Math.PI * (g - (k + 1) * f) * partSpan;
                if (Math.Abs(g - (k + 1) * f) <= window)
                {
                    double w = Math.Abs(u) < 1e-9 ? 1 : Math.Sin(u) * Math.Sin(u) / (u * u);
                    sum[k] += w * pw;
                    weight[k] += w;
                }
            }
        }

        if (lx.Count == 0)
        {
            return null;
        }

        double mx = lx.Average(), my = ly.Average();
        double sxx = lx.Sum(v => (v - mx) * (v - mx));
        double slope = lx.Count >= 3 && sxx > 0 ? lx.Zip(ly, (a, b) => (a - mx) * (b - my)).Sum() / sxx : 0;
        double intercept = my - slope * mx + EulerGamma;
        return Enumerable.Range(0, Harmonics)
            .Select(k => Math.Max(Math.Exp(intercept + slope * Math.Log((k + 1) * f)), weight[k] > 0 ? sum[k] / weight[k] : 0))
            .ToArray();
    }

    // one part's least squares of level, drift and the harmonics; the standard errors from the noise level at each
    // harmonic (the covariance of white noise of that level). Null when the part covers less than a cycle.
    private static PartFit? FitPart(double[] t, double[] y, double[] covered, int from, int to, double f, double[] noise)
    {
        if (to - from < 2 * Parameters || (covered[to - 1] - covered[from]) * f < 0.9 * MinPartCycles || LeastSquares(t, y, from, to, f) is not { } ls)
        {
            return null;
        }

        var (sol, cov) = ls;

        // per harmonic: amplitude and its variance by the delta method, with the noise level at (k + 1) f
        double total2 = 0, totalVar = 0, amplitude = 0, amplitudeSigma = 0, phase = 0, phaseSigma = 0;
        for (int k = 0; k < Harmonics; k++)
        {
            int s = 2 + 2 * k, c = 3 + 2 * k;
            double level = noise[k];
            double vs = cov[s, s] * level, vc = cov[c, c] * level, vsc = cov[s, c] * level;
            double a2 = sol[s] * sol[s] + sol[c] * sol[c];
            total2 += a2;
            totalVar += sol[s] * sol[s] * vs + sol[c] * sol[c] * vc + 2 * sol[s] * sol[c] * vsc;
            if (k == 0)
            {
                amplitude = Math.Sqrt(a2);
                double a = Math.Max(amplitude, 1e-12);
                amplitudeSigma = Math.Sqrt(Math.Max(sol[s] * sol[s] * vs + sol[c] * sol[c] * vc + 2 * sol[s] * sol[c] * vsc, 0)) / a;
                phase = Math.Atan2(sol[c], sol[s]);
                phaseSigma = Math.Sqrt(Math.Max(sol[c] * sol[c] * vs + sol[s] * sol[s] * vc - 2 * sol[s] * sol[c] * vsc, 0)) / (a * a);
            }
        }

        double total = Math.Sqrt(total2);
        return new PartFit(amplitude, amplitudeSigma, phase, phaseSigma, total, Math.Sqrt(Math.Max(totalVar, 0)) / Math.Max(total, 1e-12));
    }

    // least squares of samples [from, to) as level + drift + the harmonics at f (the parameter layout of the fit): the
    // solution and (AᵀA)⁻¹, null when singular
    private static (double[] Solution, double[,] Covariance)? LeastSquares(double[] t, double[] y, int from, int to, double f)
    {
        double t0 = t[from], t1 = t[to - 1];
        double mid = 0.5 * (t0 + t1), half = Math.Max(0.5 * Math.Abs(t1 - t0), 1e-9);
        var ata = new double[Parameters, Parameters];
        var aty = new double[Parameters];
        var h = new double[Parameters];
        for (int i = from; i < to; i++)
        {
            h[0] = 1;
            h[1] = (t[i] - mid) / half;
            Phasors(h, 2, f, t[i]);

            for (int r = 0; r < Parameters; r++)
            {
                aty[r] += h[r] * y[i];
                for (int c = 0; c < Parameters; c++)
                {
                    ata[r, c] += h[r] * h[c];
                }
            }
        }

        evaluations += Harmonics * (to - from);
        if (!Invert(ata))
        {
            return null;
        }

        var sol = new double[Parameters];
        for (int r = 0; r < Parameters; r++)
        {
            for (int c = 0; c < Parameters; c++)
            {
                sol[r] += ata[r, c] * aty[c];
            }
        }

        return (sol, ata);
    }

    // The data less each guiding run's level and drift, fitted together with the harmonics at f. The detrending (a line
    // through each run) takes a share of the curve with it from a run of a few cycles or less; where a part of the
    // stability test spans two runs that shows as a step. The data as they are for a single run.
    private static double[] Unlevelled(Series data, double f)
    {
        var (t, y, piece) = (data.T, data.Y, data.Piece);
        var starts = new List<int> { 0 };
        for (int i = 1; i < t.Length; i++)
        {
            if (piece[i] != piece[i - 1])
            {
                starts.Add(i);
            }
        }

        int runs = starts.Count, n = 2 * Harmonics + 2 * runs;
        if (runs == 1)
        {
            return y;
        }

        // columns: the harmonics, then level and drift of each run
        var mid = new double[runs];
        var half = new double[runs];
        for (int r = 0; r < runs; r++)
        {
            int s0 = starts[r], s1 = r + 1 < runs ? starts[r + 1] : t.Length;
            mid[r] = 0.5 * (t[s0] + t[s1 - 1]);
            half[r] = 0.5 * (t[s1 - 1] - t[s0]);
        }

        var ata = new double[n, n];
        var aty = new double[n];
        var h = new double[2 * Harmonics + 2];
        var index = new int[h.Length];
        for (int k = 0; k < 2 * Harmonics; k++)
        {
            index[k] = k;
        }

        for (int i = 0, run = 0; i < t.Length; i++)
        {
            run += i > 0 && piece[i] != piece[i - 1] ? 1 : 0;
            Phasors(h, 0, f, t[i]);

            index[^2] = 2 * Harmonics + 2 * run;
            index[^1] = index[^2] + 1;
            h[^2] = 1;
            h[^1] = half[run] > 0 ? (t[i] - mid[run]) / half[run] : 0;
            for (int a = 0; a < h.Length; a++)
            {
                aty[index[a]] += h[a] * y[i];
                for (int b = 0; b < h.Length; b++)
                {
                    ata[index[a], index[b]] += h[a] * h[b];
                }
            }
        }

        evaluations += Harmonics * t.Length;

        // a run of a single frame has no drift: its column decoupled (solution 0)
        for (int c = 0; c < n; c++)
        {
            if (ata[c, c] == 0)
            {
                ata[c, c] = 1;
            }
        }

        if (!Invert(ata))
        {
            return y;
        }

        var sol = new double[n];
        for (int r = 0; r < n; r++)
        {
            for (int c = 0; c < n; c++)
            {
                sol[r] += ata[r, c] * aty[c];
            }
        }

        var result = new double[t.Length];
        for (int i = 0, run = 0; i < t.Length; i++)
        {
            run += i > 0 && piece[i] != piece[i - 1] ? 1 : 0;
            int c = 2 * Harmonics + 2 * run;
            result[i] = y[i] - sol[c] - (half[run] > 0 ? sol[c + 1] * (t[i] - mid[run]) / half[run] : 0);
        }

        return result;
    }

    // sin and cos of the harmonics at time t into h from index at on (the fit's layout), by multiplying the fundamental's
    private static void Phasors(double[] h, int at, double f, double t)
    {
        var (s1, c1) = Math.SinCos(2 * Math.PI * f * t);
        double s = s1, c = c1;
        for (int k = 0; k < Harmonics; k++)
        {
            h[at + 2 * k] = s;
            h[at + 2 * k + 1] = c;
            (c, s) = (c * c1 - s * s1, s * c1 + c * s1);
        }
    }

    // in-place inverse of a small symmetric positive matrix (Gauss–Jordan with partial pivoting); false when singular
    private static bool Invert(double[,] m)
    {
        int n = m.GetLength(0);
        var inv = new double[n, n];
        for (int i = 0; i < n; i++)
        {
            inv[i, i] = 1;
        }

        for (int col = 0; col < n; col++)
        {
            int pivot = col;
            for (int r = col + 1; r < n; r++)
            {
                if (Math.Abs(m[r, col]) > Math.Abs(m[pivot, col]))
                {
                    pivot = r;
                }
            }

            if (Math.Abs(m[pivot, col]) < 1e-12)
            {
                return false;
            }

            for (int c = 0; c < n; c++)
            {
                (m[col, c], m[pivot, c]) = (m[pivot, c], m[col, c]);
                (inv[col, c], inv[pivot, c]) = (inv[pivot, c], inv[col, c]);
            }

            double d = m[col, col];
            for (int c = 0; c < n; c++)
            {
                m[col, c] /= d;
                inv[col, c] /= d;
            }

            for (int r = 0; r < n; r++)
            {
                double factor = m[r, col];
                if (r == col || factor == 0)
                {
                    continue;
                }

                for (int c = 0; c < n; c++)
                {
                    m[r, c] -= factor * m[col, c];
                    inv[r, c] -= factor * inv[col, c];
                }
            }
        }

        Array.Copy(inv, m, inv.Length);
        return true;
    }

    // Too small to predict (see NegligibleStep), judged on the recent data rather than on the live fit, whose harmonics
    // also take up some of the mount's wander: the least-squares curve of the last SearchSamples of the stability run at
    // the period, each harmonic less the noise's share of its amplitude (E[A²] = A² + the variance of its two terms), its
    // steepest change over the typical frame interval against the seeing σ of the last frames. Checked every
    // RefineEverySeconds once the run covers a cycle; notes when it changes. Stable or not: a curve that small at the
    // period, whatever it is, does not matter.
    private void UpdateNegligible(double tau)
    {
        if (!fitActive || period is not { } per)
        {
            IsNegligible = false;
            SizeRatio = double.NaN;
            return;
        }

        if (tau < nextSizeCheck)
        {
            return;
        }

        nextSizeCheck = tau + RefineEverySeconds;
        var data = Recent(CurrentRun());
        if (data.Count < 2 * Parameters)
        {
            return;
        }

        var series = Prepare(data);
        var (t, y) = (series.T, Unlevelled(series, 1 / per));
        double span = series.Span;
        if (span < per || LeastSquares(t, y, 0, t.Length, 1 / per) is not { } ls)
        {
            // less than a cycle of this stability run: the last verdict stands
            return;
        }

        var (sol, cov) = ls;
        var noise = NoiseLevels(t, FitHarmonics(t, y, 1 / per).Residual, 1 / per, span, span);
        var curve = new double[2 * Harmonics];
        for (int k = 0; k < Harmonics; k++)
        {
            int s = 2 + 2 * k, c = 3 + 2 * k;
            double a2 = sol[s] * sol[s] + sol[c] * sol[c];
            double noise2 = noise is { } levels ? (cov[s, s] + cov[c, c]) * levels[k] : 0;
            double keep = a2 > 0 ? Math.Sqrt(Math.Max(a2 - noise2, 0) / a2) : 0;
            curve[2 * k] = keep * sol[s];
            curve[2 * k + 1] = keep * sol[c];
        }

        int n = Math.Min(data.Count, NegligibleSeeingFrames), frames = 0;
        double r = 0;
        for (int i = data.Count - n; i < data.Count; i++)
        {
            r += data[i].Seeing;
        }

        foreach (var s in data)
        {
            frames += s.Frames;
        }

        double sigma = Math.Sqrt(r / n);
        double frame = MedianInterval(t) * data.Count / frames;
        double step = SteepestSlope(curve, per) * frame;
        double ratio = sigma > 0 ? step / sigma : double.PositiveInfinity;
        SizeRatio = ratio;
        bool negligible = ratio < (IsNegligible ? NegligibleLeave * NegligibleStep : NegligibleStep);
        if (negligible == IsNegligible)
        {
            return;
        }

        IsNegligible = negligible;
        string unit = axisUnits ? "″" : " px";
        Note(negligible
            ? string.Create(CultureInfo.InvariantCulture,
                $"periodic error small against the seeing (too small to predict unless it measurably helps): over the last {span / 60:F0} min the curve changes by at most {step:F3}{unit} between frames ({frame:F1} s), {ratio:F2} × the seeing σ {sigma:F3}{unit}; {Describe()}")
            : string.Create(CultureInfo.InvariantCulture,
                $"periodic error no longer small against the seeing: {step:F3}{unit} between frames, {ratio:F2} × the seeing σ; {Describe()}"));
    }

    // steepest slope of a curve (sin, cos per harmonic; units per tracking second), from its derivative every 5° of phase
    private static double SteepestSlope(double[] curve, double per)
    {
        double max = 0;
        for (int i = 0; i < 72; i++)
        {
            double phase = i * Math.PI / 36, d = 0;
            for (int k = 0; k < Harmonics; k++)
            {
                double a = (k + 1) * phase;
                d += (k + 1) * (curve[2 * k] * Math.Cos(a) - curve[2 * k + 1] * Math.Sin(a));
            }

            max = Math.Max(max, Math.Abs(d));
        }

        return max * 2 * Math.PI / per;
    }

    // median time between consecutive samples (gaps and settling left out by the median)
    private static double MedianInterval(double[] t)
    {
        var d = new List<double>(t.Length);
        for (int i = 1; i < t.Length; i++)
        {
            if (t[i] > t[i - 1])
            {
                d.Add(t[i] - t[i - 1]);
            }
        }

        if (d.Count == 0)
        {
            return 2;
        }

        d.Sort();
        return d[d.Count / 2];
    }

    // fits the curve anew over all samples with the current period (a sample of several frames as one step of them)
    private void Replay()
    {
        ResetFit();
        int lastSegment = -1;
        double lastPh = double.NaN;
        double frame = FrameSeconds();
        cycles = 0;
        foreach (var s in samples)
        {
            if (s.Segment != lastSegment)
            {
                lastSegment = s.Segment;
                segmentOrigin = s.Tau;
                StartLevel(s.U, s.Tau);
            }

            double phase = 2 * Math.PI * s.Tau / period!.Value;
            if (!double.IsNaN(lastPh) && phase > lastPh)
            {
                cycles += Math.Min(phase - lastPh, 1.0) / (2 * Math.PI);
            }

            lastPh = phase;
            FitStep(s.Tau, s.U, s.R / s.Frames, s.Frames * LevelNoiseCap * s.R, s.Frames * DriftNoiseCap * s.R, period.Value, s.Frames * frame);
        }

        evaluations += Parameters * samples.Count;
        lastPhase = double.IsNaN(lastPh) ? null : lastPh;
        segmentStart = false;
        if (samples.Count > 0)
        {
            segmentOrigin = samples.Find(s => s.Segment == samples[^1].Segment).Tau;
        }

        UpdateSignificance();
        Note($"periodic error fitted: {Describe()}");
    }

    private static (double[] T, double[] Y) Detrend(List<Sample> data)
    {
        var t = new double[data.Count];
        var y = new double[data.Count];
        int i = 0;
        foreach (var g in data.GroupBy(s => s.Segment))
        {
            var list = g.ToList();
            double mt = list.Average(s => s.Tau), mu = list.Average(s => s.U);
            double stt = list.Sum(s => (s.Tau - mt) * (s.Tau - mt));
            double slope = stt > 0 ? list.Sum(s => (s.Tau - mt) * (s.U - mu)) / stt : 0;
            foreach (var s in list)
            {
                t[i] = s.Tau;
                y[i] = s.U - mu - slope * (s.Tau - mt);
                i++;
            }
        }

        return (t, y);
    }

    // golden-section search for the maximum of the harmonic power in [lo, hi] (a single peak there), to a thousandth of
    // the interval: a few thousandths of a tooth
    private static (double F, double Power) RefinePeak(double[] t, double[] y, double lo, double hi)
    {
        const double g = 0.6180339887498949;
        double a = lo, b = hi;
        double c = b - g * (b - a), d = a + g * (b - a);
        double pc = HarmonicPower(t, y, c), pd = HarmonicPower(t, y, d);
        for (int i = 0; i < 40 && b - a > 1e-3 * (hi - lo); i++)
        {
            if (pc > pd)
            {
                b = d;
                d = c;
                pd = pc;
                c = b - g * (b - a);
                pc = HarmonicPower(t, y, c);
            }
            else
            {
                a = c;
                c = d;
                pc = pd;
                d = a + g * (b - a);
                pd = HarmonicPower(t, y, d);
            }
        }

        return pc > pd ? (c, pc) : (d, pd);
    }

    // one sin/cos pair per sample; the harmonics by multiplying the phasor (cheap enough for a Pi)
    private static double HarmonicPower(double[] t, double[] y, double f)
    {
        var c = new double[Harmonics];
        var s = new double[Harmonics];
        double w = 2 * Math.PI * f;
        for (int i = 0; i < t.Length; i++)
        {
            var (s1, c1) = Math.SinCos(w * t[i]);
            double ck = c1, sk = s1;
            for (int k = 0; k < Harmonics; k++)
            {
                c[k] += y[i] * ck;
                s[k] += y[i] * sk;
                double cn = ck * c1 - sk * s1;
                sk = sk * c1 + ck * s1;
                ck = cn;
            }
        }

        evaluations += t.Length;
        double power = 0;
        for (int k = 0; k < Harmonics; k++)
        {
            power += (c[k] * c[k] + s[k] * s[k]) / t.Length;
        }

        return power;
    }

    // HarmonicPower (over the first `harmonics` orders) at f0 + j·df, j = 0 … count − 1; per sample one sin/cos pair for
    // f0 and one for df, the grid by turning the phasor. With one harmonic the periodogram ordinates, (c² + s²) / N: the
    // noise variance for white noise.
    private static double[] Powers(double[] t, double[] y, double f0, double df, int count, int harmonics)
    {
        var c = new double[count * harmonics];
        var s = new double[count * harmonics];
        for (int i = 0; i < t.Length; i++)
        {
            double w0 = 2 * Math.PI * f0 * t[i], dw = 2 * Math.PI * df * t[i];
            var (zs, zc) = Math.SinCos(w0);
            var (rs, rc) = Math.SinCos(dw);
            if (harmonics == 1)
            {
                // the periodogram ordinates: the plain loop
                for (int j = 0; j < count; j++)
                {
                    c[j] += y[i] * zc;
                    s[j] += y[i] * zs;
                    double zn = zc * rc - zs * rs;
                    zs = zs * rc + zc * rs;
                    zc = zn;
                }

                continue;
            }

            for (int j = 0, o = 0; j < count; j++, o += harmonics)
            {
                double ck = zc, sk = zs;
                for (int k = 0; k < harmonics; k++)
                {
                    c[o + k] += y[i] * ck;
                    s[o + k] += y[i] * sk;
                    double cn = ck * zc - sk * zs;
                    sk = sk * zc + ck * zs;
                    ck = cn;
                }

                double zn = zc * rc - zs * rs;
                zs = zs * rc + zc * rs;
                zc = zn;
            }
        }

        evaluations += (long)t.Length * count;
        var power = new double[count];
        for (int j = 0; j < count; j++)
        {
            for (int k = 0; k < harmonics; k++)
            {
                int o = j * harmonics + k;
                power[j] += (c[o] * c[o] + s[o] * s[o]) / t.Length;
            }
        }

        return power;
    }

    // N (1 − ρ) / (1 + ρ) with ρ the lag-1 autocorrelation of the residual (AR(1) approximation), at least 10
    private static double EffectiveSamples(double[] t, double[] residual)
    {
        double c0 = 0, c1 = 0;
        for (int i = 0; i < residual.Length; i++)
        {
            c0 += residual[i] * residual[i];
            if (i > 0 && t[i] > t[i - 1])
            {
                c1 += residual[i] * residual[i - 1];
            }
        }

        double rho = c0 > 0 ? Math.Clamp(c1 / c0, 0, 0.99) : 0;
        return Math.Max(10, residual.Length * (1 - rho) / (1 + rho));
    }

    // least squares of each harmonic at frequency f on its own: amplitudes, the residual standard deviation and the
    // residual (the harmonics' phasors by multiplying the fundamental's)
    private static (double[] Amplitudes, double Sigma, double[] Residual) FitHarmonics(double[] t, double[] y, double f)
    {
        // per harmonic Σcos², Σsin², Σcos·sin, Σy·cos, Σy·sin
        var sums = new double[5 * Harmonics];
        double w = 2 * Math.PI * f;
        for (int i = 0; i < t.Length; i++)
        {
            var (s1, c1) = Math.SinCos(w * t[i]);
            double c = c1, s = s1;
            for (int o = 0; o < sums.Length; o += 5)
            {
                sums[o] += c * c;
                sums[o + 1] += s * s;
                sums[o + 2] += c * s;
                sums[o + 3] += y[i] * c;
                sums[o + 4] += y[i] * s;
                (c, s) = (c * c1 - s * s1, s * c1 + c * s1);
            }
        }

        var amplitudes = new double[Harmonics];
        var a = new double[Harmonics];
        var b = new double[Harmonics];
        for (int k = 0; k < Harmonics; k++)
        {
            var (cc, ss, cs, yc, ys) = (sums[5 * k], sums[5 * k + 1], sums[5 * k + 2], sums[5 * k + 3], sums[5 * k + 4]);
            double det = cc * ss - cs * cs;
            if (Math.Abs(det) >= 1e-12)
            {
                a[k] = (yc * ss - ys * cs) / det;
                b[k] = (ys * cc - yc * cs) / det;
                amplitudes[k] = Math.Sqrt(a[k] * a[k] + b[k] * b[k]);
            }
        }

        var residual = (double[])y.Clone();
        for (int i = 0; i < t.Length; i++)
        {
            var (s1, c1) = Math.SinCos(w * t[i]);
            double c = c1, s = s1;
            for (int k = 0; k < Harmonics; k++)
            {
                residual[i] -= a[k] * c + b[k] * s;
                (c, s) = (c * c1 - s * s1, s * c1 + c * s1);
            }
        }

        evaluations += 2L * Harmonics * t.Length;
        double sigma = Math.Sqrt(residual.Sum(v => v * v) / Math.Max(t.Length - 2 * Harmonics, 1));
        return (amplitudes, sigma, residual);
    }

    private void Note(string text)
    {
        if (notes.Count < 50)
        {
            notes.Add(text);
        }
    }

    // a stored sample: the mean of Frames frames from the one at Start (tracking seconds), their mean measurement
    // variance R of one frame (its measurement uncertainty included) and the seeing variance without it, of guiding run Segment and
    // stability run Run
    private readonly record struct Sample(double Tau, double U, double R, double Seeing, double Start, int Frames, int Segment, int Run, PierSide Side);

    // a detected or refined period (s) with its σ, and the same as a tooth count, from data of this span (s)
    private readonly record struct DetectedPeriod(double Period, double Sigma, double Teeth, double SigmaTeeth, double Span);

    // one part of the stability test: the fundamental (amplitude, phase in rad, standard errors) and the curve's total
    // amplitude √(Σ harmonic amplitudes²) with its standard error
    private readonly record struct PartFit(double Amplitude, double AmplitudeSigma, double Phase, double PhaseSigma, double Total, double TotalSigma);

    // the detrended samples (time order), each one's guiding run, and the tracking seconds the data cover up to it (a
    // gap counts as none)
    private readonly record struct Series(double[] T, double[] Y, int[] Piece, double[] Covered)
    {
        public double Span => Covered.Length > 0 ? Covered[^1] : 0;
    }

    private enum StabilityVerdict
    {
        Undecided,
        Stable,
        Unstable,
    }

    // the verdict; contradicted when two parts that are both significant disagree (not only a curve too weak to confirm)
    private readonly record struct StabilityResult(StabilityVerdict Verdict, string Text, bool Contradicted);
}
