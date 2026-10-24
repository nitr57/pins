// SPDX-License-Identifier: MPL-2.0

using NINA.GuideEngine.Core;

namespace NINA.GuideEngine.Guiding;

/// <summary>Safety limits. Defaults are deliberately conservative: they must never trip on normal guiding.</summary>
public sealed record SafetySettings
{
    /// <summary>Stop when an axis' pulse duty over <see cref="DutyWindowSec"/> exceeds this fraction.</summary>
    public double MaxPulseDuty { get; init; } = 0.5;

    public double DutyWindowSec { get; init; } = 60;

    /// <summary>Frames examined by the runaway detector.</summary>
    public int RunawayWindow { get; init; } = 6;

    /// <summary>Of <see cref="RunawayWindow"/> corrected frames, this many must have increased the error.</summary>
    public int RunawayMinIncreasing { get; init; } = 5;

    /// <summary>Minimum error (px) before a runaway can be declared.</summary>
    public double RunawayMinErrorPx { get; init; } = 3.0;

    /// <summary>Error must have grown by this factor over the window.</summary>
    public double RunawayGrowthFactor { get; init; } = 2.5;

    /// <summary>Consecutive large pulses without the expected star movement before "mount not responding".</summary>
    public int NotRespondingPulses { get; init; } = 3;

    /// <summary>A pulse counts as large when its expected move is at least this many pixels.</summary>
    public double NotRespondingMinExpectedPx { get; init; } = 3.0;

    /// <summary>Observed move below this fraction of the expected move counts as "no response".</summary>
    public double NotRespondingFraction { get; init; } = 0.25;

    /// <summary>Lost-star reacquire timeout: a critical alert is raised, then (see <see cref="StopOnLostStarTimeout"/>) guiding stops or keeps searching.</summary>
    public double ReacquireTimeoutSec { get; init; } = 60;

    /// <summary>Stop guiding (Failed) at the lost-star timeout. Default false: keep searching like PHD2 and resume when the star returns.</summary>
    public bool StopOnLostStarTimeout { get; init; }

    /// <summary>Seconds of searching near the last position before full-frame searches start.</summary>
    public double ReacquireLocalSearchSec { get; init; } = 10;

    /// <summary>During full-frame phase, run a full-frame search every N frames.</summary>
    public int ReacquireFullFrameEvery { get; init; } = 2;

    /// <summary>Reacquired star mass must be within [1/f, f] of the lost star's mass.</summary>
    public double ReacquireMassFactor { get; init; } = 2.5;

    /// <summary>Reacquired star SNR must be at least this fraction of the lost star's SNR.</summary>
    public double ReacquireMinSnrFraction { get; init; } = 0.4;

    /// <summary>Exposure retries before reconnecting the camera.</summary>
    public int CameraRetries { get; init; } = 2;

    /// <summary>Camera reconnects before failing.</summary>
    public int CameraReconnects { get; init; } = 1;

    /// <summary>Pointing change (arcmin) during an auto-pause beyond which guiding only resumes on request.</summary>
    public double LargeMoveArcmin { get; init; } = 5;

    /// <summary>Processing time per frame (ms) above which a slow-processing warning is raised.</summary>
    public double SlowProcessingMs { get; init; } = 500;

    /// <summary>
    /// Guiding Coach: seconds a Slewing/Homing/TrackingOff report must persist before it interrupts a session (a real goto
    /// that moves more than <see cref="LargeMoveArcmin"/> interrupts at once). Must exceed the longest pulse plus INDI's 3 s
    /// coordinate-motion window, which can report "slewing" briefly after guide pulses.
    /// </summary>
    public double CoachMountInterruptSeconds { get; init; } = 8;
}

public enum SafetyVerdict
{
    Ok,
    Runaway,
    MountNotResponding,
}

/// <summary>Why <see cref="RunawayDetector"/> declared a runaway.</summary>
public enum RunawayCause
{
    /// <summary>The error kept growing in the same direction despite the corrections.</summary>
    ErrorGrowing,

    /// <summary>
    /// The pulse duty was implausibly high, also when the error stayed small (e.g. corrections swinging between North and
    /// South): no evidence of corrections with the wrong sign.
    /// </summary>
    PulseDuty,
}

/// <summary>
/// Detects guiding that makes things worse: per axis, the error keeps growing in the same direction
/// despite corrections, or the pulse duty is implausibly high. Operates on mount-axis pixels.
/// </summary>
public sealed class RunawayDetector
{
    private readonly SafetySettings settings;
    private readonly AxisHistory ra;
    private readonly AxisHistory dec;

    public RunawayDetector(SafetySettings settings)
    {
        this.settings = settings;
        ra = new AxisHistory(settings);
        dec = new AxisHistory(settings);
    }

    /// <summary>Why the last <see cref="SafetyVerdict.Runaway"/> was declared; null while none was.</summary>
    public RunawayCause? Cause { get; private set; }

    public void Reset()
    {
        ra.Reset();
        dec.Reset();
        Cause = null;
    }

    /// <summary>Record a guide frame: mount-axis error (px) and the pulse issued in response (signed ms, 0 = none).</summary>
    public SafetyVerdict Record(DateTimeOffset time, double raError, int raPulseMs, double decError, int decPulseMs, out GuideAxis? axis)
    {
        axis = null;
        if (ra.Record(time, raError, raPulseMs) is { } raCause)
        {
            axis = GuideAxis.Ra;
            Cause = raCause;
            return SafetyVerdict.Runaway;
        }

        if (dec.Record(time, decError, decPulseMs) is { } decCause)
        {
            axis = GuideAxis.Dec;
            Cause = decCause;
            return SafetyVerdict.Runaway;
        }

        return SafetyVerdict.Ok;
    }

    private sealed class AxisHistory(SafetySettings s)
    {
        private readonly Queue<(double Error, bool Corrected)> frames = new();
        private readonly Queue<(DateTimeOffset Time, int Ms)> pulses = new();
        private long pulseMsInWindow;

        public void Reset()
        {
            frames.Clear();
            pulses.Clear();
            pulseMsInWindow = 0;
        }

        public RunawayCause? Record(DateTimeOffset time, double error, int pulseMs)
        {
            frames.Enqueue((error, pulseMs != 0));
            while (frames.Count > s.RunawayWindow + 1)
            {
                frames.Dequeue();
            }

            if (pulseMs != 0)
            {
                pulses.Enqueue((time, Math.Abs(pulseMs)));
                pulseMsInWindow += Math.Abs(pulseMs);
            }

            while (pulses.Count > 0 && (time - pulses.Peek().Time).TotalSeconds > s.DutyWindowSec)
            {
                pulseMsInWindow -= pulses.Dequeue().Ms;
            }

            // duty: only meaningful once the window is (nearly) filled
            if (pulses.Count > 0 && (time - pulses.Peek().Time).TotalSeconds >= s.DutyWindowSec * 0.8 &&
                pulseMsInWindow > s.MaxPulseDuty * s.DutyWindowSec * 1000)
            {
                return RunawayCause.PulseDuty;
            }

            if (frames.Count < s.RunawayWindow + 1)
            {
                return null;
            }

            var f = frames.ToArray();
            int increasing = 0;
            int sign = Math.Sign(f[^1].Error);
            for (int i = 1; i < f.Length; i++)
            {
                // a correction was issued after frame i-1 and the error grew with unchanged sign
                if (f[i - 1].Corrected && Math.Sign(f[i].Error) == sign && Math.Sign(f[i - 1].Error) == sign &&
                    Math.Abs(f[i].Error) > Math.Abs(f[i - 1].Error))
                {
                    increasing++;
                }
            }

            double first = Math.Abs(f[0].Error), last = Math.Abs(f[^1].Error);
            return increasing >= s.RunawayMinIncreasing && last >= s.RunawayMinErrorPx && last >= s.RunawayGrowthFactor * Math.Max(first, 0.5)
                ? RunawayCause.ErrorGrowing
                : null;
        }
    }
}

/// <summary>
/// Detects a mount that ignores guide pulses: several consecutive large pulses whose expected
/// star movement did not happen.
/// </summary>
public sealed class MountResponseMonitor(SafetySettings settings)
{
    private int consecutive;
    private (GuideAxis Axis, double ErrorBefore, double ExpectedMovePx)? pending;

    public void Reset()
    {
        consecutive = 0;
        pending = null;
    }

    /// <summary>
    /// Record a pulse just issued. <paramref name="expectedMovePx"/> is the signed expected change of the
    /// mount-axis error (usually −error × aggressiveness for a guide correction).
    /// </summary>
    public void PulseIssued(GuideAxis axis, double errorBefore, double expectedMovePx)
    {
        if (Math.Abs(expectedMovePx) >= settings.NotRespondingMinExpectedPx)
        {
            pending = (axis, errorBefore, expectedMovePx);
        }
    }

    /// <summary>Evaluate the next frame's mount-axis errors. Returns true when the mount is deemed unresponsive.</summary>
    public bool Evaluate(double raError, double decError)
    {
        if (pending is not { } p)
        {
            return false;
        }

        pending = null;
        double after = p.Axis == GuideAxis.Ra ? raError : decError;
        double observed = after - p.ErrorBefore;

        // observed movement in the expected direction, as a fraction of what was expected
        double fraction = observed / p.ExpectedMovePx;
        if (fraction < settings.NotRespondingFraction)
        {
            consecutive++;
        }
        else
        {
            consecutive = 0;
        }

        return consecutive >= settings.NotRespondingPulses;
    }

    /// <summary>A frame passed without a large pulse to evaluate; does not reset the counter.</summary>
    public void NoPulse() => pending = null;
}

public enum MountPauseReason
{
    None,
    Slewing,
    Parked,
    Homing,
    TrackingOff,
    Disconnected,
}

/// <summary>Decides when guiding must be auto-paused based on the mount state, and whether a resume needs a request.</summary>
public sealed class MountStateWatcher(SafetySettings settings)
{
    private MountSnapshot? pausedAt;

    public MountPauseReason CurrentReason { get; private set; }

    /// <summary>True when the mount moved more than the threshold while paused; resume then needs an explicit request.</summary>
    public bool RequiresExplicitResume { get; private set; }

    public static MountPauseReason Evaluate(MountSnapshot m) =>
        !m.IsConnected ? MountPauseReason.Disconnected
        : m.IsParked ? MountPauseReason.Parked
        : m.IsHoming ? MountPauseReason.Homing
        : m.IsSlewing ? MountPauseReason.Slewing
        : !m.IsTracking ? MountPauseReason.TrackingOff
        : MountPauseReason.None;

    /// <summary>Update with the latest snapshot; returns the pause reason (None = guiding may run).</summary>
    public MountPauseReason Update(MountSnapshot m)
    {
        var reason = Evaluate(m);
        if (reason != MountPauseReason.None && CurrentReason == MountPauseReason.None)
        {
            pausedAt = m;
            RequiresExplicitResume = false;
        }

        if (reason != MountPauseReason.None && pausedAt is not null && MovedFar(pausedAt, m))
        {
            RequiresExplicitResume = true;
        }

        if (reason == MountPauseReason.None && CurrentReason != MountPauseReason.None && pausedAt is not null && MovedFar(pausedAt, m))
        {
            RequiresExplicitResume = true;
        }

        CurrentReason = reason;
        return reason;
    }

    public void ClearExplicitResume()
    {
        RequiresExplicitResume = false;
        pausedAt = null;
    }

    private bool MovedFar(MountSnapshot a, MountSnapshot b) => MovedFar(a, b, settings.LargeMoveArcmin);

    /// <summary>True when the pier side changed or the pointing moved more than <paramref name="limitArcmin"/>.</summary>
    internal static bool MovedFar(MountSnapshot a, MountSnapshot b, double limitArcmin) =>
        a.PierSide != PierSide.Unknown && b.PierSide != PierSide.Unknown && a.PierSide != b.PierSide
        || SeparationArcmin(a, b) is { } sep && sep > limitArcmin;

    /// <summary>Pointing separation between two snapshots (arcmin), null without coordinates.</summary>
    internal static double? SeparationArcmin(MountSnapshot a, MountSnapshot b)
    {
        if (a.RightAscensionHours is not { } ra1 || b.RightAscensionHours is not { } ra2 || a.DeclinationDeg is not { } d1 || b.DeclinationDeg is not { } d2)
        {
            return null;
        }

        double dra = (ra2 - ra1) * 15.0;
        dra = ((dra + 540) % 360) - 180;
        return Math.Sqrt(Math.Pow(dra * Math.Cos(d1 * Math.PI / 180), 2) + Math.Pow(d2 - d1, 2)) * 60.0;
    }
}

/// <summary>Verdict of the <see cref="CoachMountGate"/>.</summary>
/// <param name="Busy">A mount condition (slewing, homing, tracking off, ...) is active: measurements skip the frame.</param>
/// <param name="InterruptReason">Set when the condition interrupts the coach session (a <c>CoachInterruptReasons</c> value).</param>
internal readonly record struct CoachMountVerdict(bool Busy, string? InterruptReason);

/// <summary>
/// Debounces mount-state reports for Guiding Coach sessions (shared by the guide loop and the camera check). Disconnected
/// and Parked interrupt at once. Slewing, Homing and TrackingOff interrupt only when they persist for
/// <see cref="SafetySettings.CoachMountInterruptSeconds"/>, or at once when the pointing moved more than
/// <see cref="SafetySettings.LargeMoveArcmin"/> since the condition started (a real goto); until then the frames are only
/// marked busy. Some INDI drivers report "slewing" for a few seconds after guide pulses (coordinate-motion heuristics), which
/// must not cancel a session. Normal guiding is not affected (it still pauses at once). Thread-safe.
/// </summary>
internal sealed class CoachMountGate
{
    private readonly object gate = new();
    private DateTimeOffset? since;
    private MountSnapshot? start;

    public void Reset()
    {
        lock (gate)
        {
            since = null;
            start = null;
        }
    }

    public CoachMountVerdict Update(MountSnapshot m, DateTimeOffset now, SafetySettings settings)
    {
        var reason = MountStateWatcher.Evaluate(m);
        lock (gate)
        {
            if (reason == MountPauseReason.None)
            {
                // a goto that finished between two checks: the pointing moved far while the condition was active
                bool movedFar = start is not null && MountStateWatcher.MovedFar(start, m, settings.LargeMoveArcmin);
                since = null;
                start = null;
                return new CoachMountVerdict(false, movedFar ? Coach.CoachInterruptReasons.Slew : null);
            }

            if (reason == MountPauseReason.Disconnected)
            {
                return new CoachMountVerdict(true, Coach.CoachInterruptReasons.Disconnect);
            }

            if (reason == MountPauseReason.Parked)
            {
                return new CoachMountVerdict(true, Coach.CoachInterruptReasons.Slew);
            }

            since ??= now;
            start ??= m;
            if (MountStateWatcher.MovedFar(start, m, settings.LargeMoveArcmin))
            {
                return new CoachMountVerdict(true, Coach.CoachInterruptReasons.Slew);
            }

            if ((now - since.Value).TotalSeconds >= settings.CoachMountInterruptSeconds)
            {
                return new CoachMountVerdict(true, reason == MountPauseReason.TrackingOff ? Coach.CoachInterruptReasons.TrackingOff : Coach.CoachInterruptReasons.Slew);
            }

            return new CoachMountVerdict(true, null);
        }
    }
}

public enum CameraFailureAction
{
    Retry,
    Reconnect,
    Fail,
}

/// <summary>Camera failure escalation: retry exposure → reconnect → fail. A successful frame resets it.</summary>
public sealed class CameraRetryPolicy(SafetySettings settings)
{
    private int retries;
    private int reconnects;

    public void Success()
    {
        retries = 0;
        reconnects = 0;
    }

    public CameraFailureAction Failure()
    {
        if (retries < settings.CameraRetries)
        {
            retries++;
            return CameraFailureAction.Retry;
        }

        if (reconnects < settings.CameraReconnects)
        {
            reconnects++;
            retries = 0;
            return CameraFailureAction.Reconnect;
        }

        return CameraFailureAction.Fail;
    }
}

public enum ReacquireAction
{
    /// <summary>Search around the last known position only.</summary>
    SearchLocal,

    /// <summary>Run a full-frame star search this frame.</summary>
    SearchFullFrame,

    /// <summary>Timeout reached: fail.</summary>
    GiveUp,
}

/// <summary>Lost-star reacquisition policy with a plausibility check on candidate stars.</summary>
public sealed class ReacquirePolicy(SafetySettings settings)
{
    private DateTimeOffset? lostSince;
    private int frames;
    private double lostMass;
    private double lostSnr;

    public bool IsActive => lostSince is not null;

    public DateTimeOffset? LostSince => lostSince;

    public void StarLost(DateTimeOffset now, double lastGoodMass, double lastGoodSnr)
    {
        if (lostSince is not null)
        {
            return;
        }

        lostSince = now;
        frames = 0;
        lostMass = lastGoodMass;
        lostSnr = lastGoodSnr;
    }

    public void Reset() => lostSince = null;

    public double ElapsedSec(DateTimeOffset now) => lostSince is null ? 0 : (now - lostSince.Value).TotalSeconds;

    public ReacquireAction Next(DateTimeOffset now)
    {
        if (lostSince is null)
        {
            throw new InvalidOperationException("not reacquiring");
        }

        double elapsed = ElapsedSec(now);
        if (elapsed >= settings.ReacquireTimeoutSec)
        {
            return ReacquireAction.GiveUp;
        }

        frames++;
        if (elapsed < settings.ReacquireLocalSearchSec)
        {
            return ReacquireAction.SearchLocal;
        }

        return frames % Math.Max(1, settings.ReacquireFullFrameEvery) == 0 ? ReacquireAction.SearchFullFrame : ReacquireAction.SearchLocal;
    }

    /// <summary>Whether a candidate star is plausibly the lost guide star (mass/SNR close to the last good values).</summary>
    public bool IsPlausible(double mass, double snr)
    {
        if (lostMass > 0)
        {
            double ratio = mass / lostMass;
            if (ratio > settings.ReacquireMassFactor || ratio < 1.0 / settings.ReacquireMassFactor)
            {
                return false;
            }
        }

        return lostSnr <= 0 || snr >= settings.ReacquireMinSnrFraction * lostSnr;
    }
}
