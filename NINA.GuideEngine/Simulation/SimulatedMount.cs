// SPDX-License-Identifier: MPL-2.0

using NINA.GuideEngine.Core;

namespace NINA.GuideEngine.Simulation;

/// <summary>Breakdown of the mount pointing error at one instant (all <see cref="SkyOffset"/> in arcsec on the sky).</summary>
/// <param name="Bumps">Sudden jumps (<see cref="SimulatedMount.Bump"/>), not seen by the mount's encoders.</param>
public readonly record struct PointingErrorBreakdown(
    SkyOffset PeriodicError,
    SkyOffset Drift,
    SkyOffset RandomWalk,
    SkyOffset Wind,
    SkyOffset Guiding,
    SkyOffset Moves,
    SkyOffset Bumps = default)
{
    public SkyOffset Total => PeriodicError + Drift + RandomWalk + Wind + Guiding + Moves + Bumps;
}

/// <summary>
/// Simulated equatorial mount: a physical model of the pointing error in arcsec on the sky, driven by
/// tracking errors and guide pulses. Implements <see cref="IPulseOutput"/> (pulse guiding) and
/// <see cref="IMountState"/>.
/// </summary>
/// <remarks>
/// <para>
/// Conventions: the pointing error is where the telescope points relative to the target, RA positive
/// East (great-circle arcsec), Dec positive North. A star therefore appears displaced by the negative
/// pointing error. Time <c>t</c> is <see cref="ClockExtensions.NowSeconds"/> of the shared clock.
/// </para>
/// <para>
/// Guide pulses: West moves the pointing West (RA error decreases) by
/// <c>rate·15.041″/s·cos(dec)·duration</c> (<see cref="SiderealArcsecPerSec"/>), East the opposite; North moves the
/// pointing North by <c>rate·15.041″/s·duration</c> (reversed on pier side West when
/// <see cref="MountSimConfig.DecGuideReversedOnWestPier"/>, and reversed again by the
/// <see cref="InvertDecPulses"/> fault). Speeds are scaled per direction (<see cref="MountSimConfig.RaWestEfficiency"/> ...),
/// the first <see cref="MountSimConfig.RaStictionMs"/> / <see cref="MountSimConfig.DecStictionMs"/> of a pulse produce no
/// motion, and Dec reversals first take up the backlash dead band. The motion is spread linearly over the pulse, and
/// <see cref="PulseAsync"/> advances the clock by the pulse duration.
/// </para>
/// </remarks>
public sealed class SimulatedMount : IPulseOutput, IMountState
{
    /// <summary>Sidereal rate in arcsec of RA axis angle per second (<see cref="Sidereal.ArcsecPerSecond"/>).</summary>
    public const double SiderealArcsecPerSec = Sidereal.ArcsecPerSecond;

    /// <summary>SI seconds per sidereal hour (<see cref="Sidereal.HourSeconds"/>).</summary>
    public const double SecondsPerSiderealHour = Sidereal.HourSeconds;

    private const double WalkStepSec = 1.0;

    private readonly object gate = new();
    private readonly IClock clock;
    private readonly AxisPulses raPulses = new();
    private readonly AxisPulses decPulses = new();
    private readonly List<MoveSegment> moves = [];
    private readonly List<(double T, SkyOffset Offset)> bumps = [];
    private readonly List<double> walkRa = [0.0];
    private readonly List<double> walkDec = [0.0];
    private readonly SimRng walkRng;
    private readonly List<Gust> gusts = [];
    private readonly SimRng gustRng;
    private double gustsGeneratedUntil;
    private double maxGustDuration;

    private double declinationDeg;
    private double guideRateRa;
    private double guideRateDec;
    private PierSide pierSide;
    private bool isParked;
    private bool isTracking = true;
    private int trackingOffMove = -1;
    private double slewEnd = double.NegativeInfinity;
    private double notRespondingUntil = double.NegativeInfinity;
    private double invertDecUntil = double.NegativeInfinity;

    // reported RA = true RA + this (a sync moves the reported coordinates, not the axis)
    private double syncOffsetHours;

    // pier side from each time on, for the axis angle at past times
    private readonly List<(double T, PierSide Side)> pierSides = [];

    // Dec backlash: position of the motor inside the dead band, 0 .. DecBacklashArcsec.
    private double backlashPlay;

    public SimulatedMount(MountSimConfig config, IClock clock)
    {
        Config = config;
        this.clock = clock;
        declinationDeg = config.DeclinationDeg;
        guideRateRa = config.GuideRateRa;
        guideRateDec = config.GuideRateDec;
        pierSide = config.PierSide;
        pierSides.Add((double.NegativeInfinity, config.PierSide));
        walkRng = new SimRng((ulong)config.Seed, 0x57A1CUL);
        gustRng = new SimRng((ulong)config.Seed, 0x6057UL);
    }

    public MountSimConfig Config { get; }

    public string Name => Config.Name;

    public bool IsConnected { get; set; } = true;

    public bool SupportsSimultaneousPulses => Config.SupportsSimultaneousPulses;

    /// <summary>Fault: pulses are accepted but produce no motion (mount not responding).</summary>
    public bool NotResponding { get; set; }

    /// <summary>Fault: Dec pulses move in the opposite direction to the normal model (e.g. unexpected Dec reversal after a flip).</summary>
    public bool InvertDecPulses { get; set; }

    /// <summary>Fault: pulses produce no motion until <paramref name="duration"/> from now (like <see cref="NotResponding"/>, but timed).</summary>
    public void StopRespondingFor(TimeSpan duration)
    {
        lock (gate) notRespondingUntil = clock.NowSeconds() + duration.TotalSeconds;
    }

    /// <summary>Fault: Dec pulses move the wrong way until <paramref name="duration"/> from now (like <see cref="InvertDecPulses"/>, but timed).</summary>
    public void InvertDecPulsesFor(TimeSpan duration)
    {
        lock (gate) invertDecUntil = clock.NowSeconds() + duration.TotalSeconds;
    }

    /// <summary>
    /// Fault: the pointing jumps by <paramref name="offset"/> (arcsec on the sky) now, once, e.g. a knock against the tripod.
    /// The mount's encoders don't see it (reported coordinates are unchanged).
    /// </summary>
    public void Bump(SkyOffset offset)
    {
        lock (gate) bumps.Add((clock.NowSeconds(), offset));
    }

    /// <summary>Fault: when set, the snapshot reports this pier side regardless of the physical one.</summary>
    public PierSide? ReportedPierSideOverride { get; set; }

    /// <summary>Physical pier side.</summary>
    public PierSide PierSide
    {
        get
        {
            lock (gate) return pierSide;
        }
    }

    public double DeclinationDeg
    {
        get
        {
            lock (gate) return declinationDeg;
        }
    }

    public double GuideRateRa
    {
        get
        {
            lock (gate) return guideRateRa;
        }
    }

    public double GuideRateDec
    {
        get
        {
            lock (gate) return guideRateDec;
        }
    }

    /// <summary>Total number of pulses received (including ignored ones).</summary>
    public int PulseCount { get; private set; }

    /// <summary>Current Dec backlash take-up position, 0 .. <see cref="MountSimConfig.DecBacklashArcsec"/> (diagnostics).</summary>
    public double DecBacklashPlay
    {
        get
        {
            lock (gate) return backlashPlay;
        }
    }

    public bool IsSlewing
    {
        get
        {
            lock (gate) return clock.NowSeconds() < slewEnd;
        }
    }

    public MountSnapshot GetSnapshot()
    {
        lock (gate)
        {
            double now = clock.NowSeconds();

            // reported coordinates follow slews (commanded moves) but not tracking errors, like real encoders
            var moved = GetPointingErrorBreakdown(now).Moves;
            double dec = declinationDeg + moved.Dec / 3600.0;
            double cosDec = Math.Max(Math.Cos(declinationDeg * Math.PI / 180.0), 1e-6);
            double ra = TrueRightAscensionHours(now) + syncOffsetHours;
            ra = ((ra % 24.0) + 24.0) % 24.0;
            return new MountSnapshot
            {
                IsConnected = IsConnected,
                DeclinationDeg = dec,
                RightAscensionHours = ra,
                SiderealTimeHours = ((SiderealTimeHours(now) % 24.0) + 24.0) % 24.0,
                PierSide = ReportedPierSideOverride ?? pierSide,
                IsSlewing = now < slewEnd,
                IsParked = isParked,
                IsHoming = false,
                IsTracking = isTracking && !isParked,
                GuideRateRa = guideRateRa,
                GuideRateDec = guideRateDec,
            };
        }
    }

    /// <summary>Issues a guide pulse; completes after the pulse duration on the clock.</summary>
    public async Task PulseAsync(GuideDirection direction, int durationMs, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!IsConnected) throw new InvalidOperationException("simulated mount is not connected");
        if (durationMs <= 0) return;

        AxisPulses? axis;
        lock (gate)
        {
            PulseCount++;
            axis = RegisterPulse(direction, durationMs, clock.NowSeconds());
        }

        try
        {
            await clock.Delay(TimeSpan.FromMilliseconds(durationMs), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (axis != null)
        {
            lock (gate) axis.TruncateAt(clock.NowSeconds());
            throw;
        }
    }

    /// <summary>Pointing error (arcsec on the sky) at time <paramref name="t"/> seconds.</summary>
    public SkyOffset GetPointingError(double t) => GetPointingErrorBreakdown(t).Total;

    /// <summary>Pointing error now.</summary>
    public SkyOffset GetPointingError() => GetPointingError(clock.NowSeconds());

    /// <summary>Pointing error at time <paramref name="t"/> split into its physical components.</summary>
    public PointingErrorBreakdown GetPointingErrorBreakdown(double t)
    {
        if (t < 0) t = 0;
        lock (gate)
        {
            double cosDec = Math.Cos(declinationDeg * Math.PI / 180.0);
            // the worm turns with the RA axis: its angle in tracking seconds, or the plain time
            double tau = Config.PeriodicErrorFollowsAxis ? AxisSecondsAt(t) : t;
            double pe = 0;
            var terms = Config.PeriodicError;
            for (int i = 0; i < terms.Count; i++)
            {
                var term = terms[i];
                double amplitude = term.AmplitudeArcsec * (1 + Wander(term.AmplitudeChange, term.AmplitudeChangeSec, i, 0, tau));
                double phase = term.PhaseRad + Wander(term.PhaseChangeRad, term.PhaseChangeSec, i, 1, tau);
                pe += amplitude * Math.Sin(2 * Math.PI * tau / term.PeriodSec + phase);
            }

            double decDrift = Config.DecDriftArcsecPerMin * t / 60.0 + Config.DecDriftChangePerHour * t * t / (2 * 3600.0 * 60.0);
            var drift = new SkyOffset(Config.RaDriftArcsecPerMin * t / 60.0, decDrift);
            return new PointingErrorBreakdown(
                new SkyOffset(pe * cosDec, 0),
                drift,
                RandomWalkAt(t),
                WindAt(t),
                new SkyOffset(raPulses.DisplacementAt(t), decPulses.DisplacementAt(t)),
                MovesAt(t),
                BumpsAt(t));
        }
    }

    /// <summary>Periodic error on the sky (arcsec, RA) at time <paramref name="t"/>.</summary>
    public double PeriodicErrorAt(double t) => GetPointingErrorBreakdown(t).PeriodicError.Ra;

    /// <summary>Changes the declination (affects cos(dec) scaling of RA pulses and periodic error from now on).</summary>
    public void SetDeclination(double deg)
    {
        lock (gate) declinationDeg = deg;
    }

    public void SetGuideRates(double raRate, double decRate)
    {
        lock (gate)
        {
            guideRateRa = raRate;
            guideRateDec = decRate;
        }
    }

    /// <summary>Performs a meridian flip: the physical pier side toggles (image rotates by 180°).</summary>
    public void MeridianFlip()
    {
        lock (gate) SetPierSideLocked(pierSide == PierSide.West ? PierSide.East : PierSide.West);
    }

    public void SetPierSide(PierSide side)
    {
        lock (gate) SetPierSideLocked(side);
    }

    /// <summary>A sync: the reported right ascension changes by <paramref name="raOffsetHours"/>, the axis does not move.</summary>
    public void Sync(double raOffsetHours)
    {
        lock (gate) syncOffsetHours += raOffsetHours;
    }

    /// <summary>Local sidereal time (hours, not wrapped) at time <paramref name="t"/> seconds.</summary>
    public double SiderealTimeHours(double t) => (Config.SiderealTimeAtStartHours ?? Config.RightAscensionHours) + t / SecondsPerSiderealHour;

    /// <summary>
    /// RA axis angle in tracking seconds at time <paramref name="t"/>: hour angle of the true pointing, plus 12 h on the
    /// West pier side (the axis is turned by 180° for the same hour angle).
    /// </summary>
    public double AxisSecondsAt(double t)
    {
        lock (gate)
        {
            double hours = SiderealTimeHours(t) - TrueRightAscensionHours(t) + (PierSideAt(t) == PierSide.West ? 12.0 : 0.0);
            return hours * SecondsPerSiderealHour;
        }
    }

    private void SetPierSideLocked(PierSide side)
    {
        pierSide = side;
        pierSides.Add((clock.NowSeconds(), side));
    }

    private PierSide PierSideAt(double t)
    {
        for (int i = pierSides.Count - 1; i >= 0; i--)
        {
            if (pierSides[i].T <= t) return pierSides[i].Side;
        }

        return pierSides[0].Side;
    }

    // right ascension of the true pointing: follows slews and tracking-off drift (East = increasing RA), not tracking
    // errors (not wrapped). With tracking off it grows like the sidereal time, so the hour angle stands still.
    private double TrueRightAscensionHours(double t)
    {
        double cosDec = Math.Max(Math.Cos(declinationDeg * Math.PI / 180.0), 1e-6);
        return Config.RightAscensionHours + MovesAt(t).Ra / (15.0 * 3600.0 * cosDec);
    }

    /// <summary>
    /// Starts a slew that moves the pointing by the given offset (arcsec on the sky) linearly over
    /// <paramref name="duration"/>. <see cref="MountSnapshot.IsSlewing"/> is true meanwhile and pulses are ignored.
    /// </summary>
    public void StartSlew(SkyOffset offset, TimeSpan duration)
    {
        lock (gate)
        {
            double now = clock.NowSeconds();
            double d = Math.Max(duration.TotalSeconds, 1e-3);
            moves.Add(new MoveSegment(now, now + d, offset.Ra / d, offset.Dec / d));
            slewEnd = now + d;
        }
    }

    /// <summary>Stops or starts sidereal tracking. With tracking off the pointing drifts East at the sidereal rate.</summary>
    public void SetTracking(bool on)
    {
        lock (gate) SetTrackingLocked(on);
    }

    public void Park()
    {
        lock (gate)
        {
            isParked = true;
            SetTrackingLocked(false);
        }
    }

    public void Unpark()
    {
        lock (gate)
        {
            isParked = false;
            SetTrackingLocked(true);
        }
    }

    private void SetTrackingLocked(bool on)
    {
        double now = clock.NowSeconds();
        if (!on && isTracking)
        {
            double v = SiderealArcsecPerSec * Math.Cos(declinationDeg * Math.PI / 180.0);
            moves.Add(new MoveSegment(now, double.PositiveInfinity, v, 0));
            trackingOffMove = moves.Count - 1;
        }
        else if (on && !isTracking && trackingOffMove >= 0)
        {
            moves[trackingOffMove] = moves[trackingOffMove] with { End = now };
            trackingOffMove = -1;
        }

        isTracking = on;
    }

    private AxisPulses? RegisterPulse(GuideDirection direction, int durationMs, double now)
    {
        if (NotResponding || now < notRespondingUntil || isParked || now < slewEnd) return null;

        double dur = durationMs / 1000.0;
        if (direction.Axis() == GuideAxis.Ra)
        {
            double stiction = Math.Min(dur, Config.RaStictionMs / 1000.0);
            double v = guideRateRa * SiderealArcsecPerSec * Math.Cos(declinationDeg * Math.PI / 180.0);
            v *= direction == GuideDirection.West ? Config.RaWestEfficiency : Config.RaEastEfficiency;
            if (direction == GuideDirection.West) v = -v;
            raPulses.Add(now + stiction, now + dur, v);
            return raPulses;
        }

        // Dec: stiction delays the start, then backlash acts on the motor; motor "North" = +.
        double decStiction = Math.Min(dur, Config.DecStictionMs / 1000.0);
        double speed = guideRateDec * SiderealArcsecPerSec * (direction == GuideDirection.North ? Config.DecNorthEfficiency : Config.DecSouthEfficiency);
        double motorSign = direction == GuideDirection.North ? 1.0 : -1.0;
        double motion = speed * (dur - decStiction);
        double takeUp = 0;
        double band = Config.DecBacklashArcsec;
        if (band > 0)
        {
            if (motorSign > 0)
            {
                takeUp = Math.Min(motion, band - backlashPlay);
                backlashPlay += takeUp;
            }
            else
            {
                takeUp = Math.Min(motion, backlashPlay);
                backlashPlay -= takeUp;
            }
        }

        double skySign = motorSign;
        if (pierSide == PierSide.West && Config.DecGuideReversedOnWestPier) skySign = -skySign;
        if (InvertDecPulses || now < invertDecUntil) skySign = -skySign;
        double start = now + decStiction + (speed > 0 ? takeUp / speed : dur);
        decPulses.Add(start, now + dur, skySign * speed);
        return decPulses;
    }

    private SkyOffset MovesAt(double t)
    {
        double ra = 0, dec = 0;
        foreach (var m in moves)
        {
            if (t <= m.Start) continue;
            double dt = Math.Min(t, m.End) - m.Start;
            ra += m.VRa * dt;
            dec += m.VDec * dt;
        }

        return new SkyOffset(ra, dec);
    }

    private SkyOffset BumpsAt(double t)
    {
        var sum = SkyOffset.Zero;
        foreach (var (bt, offset) in bumps)
        {
            if (bt < t) sum += offset;
        }

        return sum;
    }

    private SkyOffset RandomWalkAt(double t)
    {
        double sigma = Config.RandomWalkArcsecPerSqrtSec;
        if (sigma <= 0) return SkyOffset.Zero;
        int i = (int)Math.Floor(t / WalkStepSec);
        while (walkRa.Count < i + 2)
        {
            double s = sigma * Math.Sqrt(WalkStepSec);
            walkRa.Add(walkRa[^1] + s * walkRng.NextGaussian());
            walkDec.Add(walkDec[^1] + s * walkRng.NextGaussian());
        }

        double f = t / WalkStepSec - i;
        return new SkyOffset(
            walkRa[i] + (walkRa[i + 1] - walkRa[i]) * f,
            walkDec[i] + (walkDec[i + 1] - walkDec[i]) * f);
    }

    // slow change of a periodic error term (PeriodicErrorTerm remarks): values evenly within ± size at knots lengthSec
    // apart, joined by a smoothstep; 0 when off
    private double Wander(double size, double lengthSec, int term, int kind, double tau)
    {
        if (size == 0 || !(lengthSec > 0)) return 0;
        double x = tau / lengthSec;
        double k = Math.Floor(x), f = x - k;
        ulong stream = 0x9E00UL + 2 * (ulong)term + (ulong)kind;
        double a = KnotValue(stream, (long)k), b = KnotValue(stream, (long)k + 1);
        return size * (a + (b - a) * f * f * (3 - 2 * f));
    }

    private double KnotValue(ulong stream, long knot) =>
        2 * ((Hash.Combine((ulong)Config.Seed, stream, (ulong)knot) >> 11) * (1.0 / (1UL << 53))) - 1;

    private SkyOffset WindAt(double t)
    {
        var w = Config.Wind;
        if (w == null || w.GustsPerMinute <= 0 || w.DurationSec <= 0) return SkyOffset.Zero;
        while (gustsGeneratedUntil <= t)
        {
            gustsGeneratedUntil += gustRng.NextExponential(60.0 / w.GustsPerMinute);
            double amp = w.AmplitudeArcsec * gustRng.Uniform(0.5, 1.0);
            double ang = gustRng.Uniform(0, 2 * Math.PI);
            gusts.Add(new Gust(gustsGeneratedUntil, w.DurationSec, amp * Math.Cos(ang), amp * Math.Sin(ang)));
            maxGustDuration = Math.Max(maxGustDuration, w.DurationSec);
        }

        double ra = 0, dec = 0;
        for (int i = gusts.Count - 1; i >= 0; i--)
        {
            var g = gusts[i];
            if (g.Start + maxGustDuration < t) break;
            if (t <= g.Start || t >= g.Start + g.Duration) continue;
            double s = Math.Sin(Math.PI * (t - g.Start) / g.Duration);
            ra += g.Ra * s;
            dec += g.Dec * s;
        }

        return new SkyOffset(ra, dec);
    }

    private readonly record struct MoveSegment(double Start, double End, double VRa, double VDec);

    private readonly record struct Gust(double Start, double Duration, double Ra, double Dec);

    /// <summary>Non-overlapping guide motion segments of one axis with prefix sums for O(log n) evaluation.</summary>
    private sealed class AxisPulses
    {
        private readonly List<double> starts = [];
        private readonly List<double> ends = [];
        private readonly List<double> velocities = [];
        private readonly List<double> prefix = [0.0]; // prefix[k] = displacement of the first k segments

        public void Add(double start, double end, double velocity)
        {
            if (end <= start || velocity == 0) return;
            if (starts.Count > 0 && ends[^1] > start) TruncateAt(start);
            starts.Add(start);
            ends.Add(end);
            velocities.Add(velocity);
            prefix.Add(prefix[^1] + velocity * (end - start));
        }

        /// <summary>Stops the last segment at <paramref name="t"/> (a newer pulse or a cancellation replaces it).</summary>
        public void TruncateAt(double t)
        {
            int k = starts.Count - 1;
            if (k < 0 || ends[k] <= t) return;
            if (t <= starts[k])
            {
                starts.RemoveAt(k);
                ends.RemoveAt(k);
                velocities.RemoveAt(k);
                prefix.RemoveAt(k + 1);
                return;
            }

            ends[k] = t;
            prefix[k + 1] = prefix[k] + velocities[k] * (t - starts[k]);
        }

        public double DisplacementAt(double t)
        {
            // number of segments completely finished by t
            int lo = 0, hi = ends.Count;
            while (lo < hi)
            {
                int mid = (lo + hi) >> 1;
                if (ends[mid] <= t) lo = mid + 1;
                else hi = mid;
            }

            double d = prefix[lo];
            if (lo < starts.Count && t > starts[lo]) d += velocities[lo] * (t - starts[lo]);
            return d;
        }
    }
}
