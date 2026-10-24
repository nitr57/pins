// SPDX-License-Identifier: MPL-2.0

using System.Globalization;
using NINA.GuideEngine.Algorithms;
using NINA.GuideEngine.Core;
using NINA.GuideEngine.Guiding;
using NINA.GuideEngine.Stats;

namespace NINA.GuideEngine.Coach;

/// <summary>One normal guide step as seen by the <see cref="LiveHintAnalyser"/>.</summary>
public sealed record LiveHintInput
{
    public DateTimeOffset Time { get; init; }

    /// <summary>Statistics after this step (the window is analysed).</summary>
    public required GuidingStatsSnapshot Stats { get; init; }

    public bool RaLimited { get; init; }

    public bool DecLimited { get; init; }

    public int RaDurationMs { get; init; }

    public int DecDurationMs { get; init; }

    /// <summary>Direction of the Dec pulse issued after this frame, null without one.</summary>
    public GuideDirection? DecDirection { get; init; }

    public double Snr { get; init; }

    /// <summary>Effective guider settings (for the suggested changes).</summary>
    public required GuiderSettings Settings { get; init; }
}

/// <summary>
/// Lightweight analyser over the guiding statistics window that raises dismissable live hints (<c>hint.*</c> codes of
/// docs/COACH.md §7) while guiding normally. Each hint id is raised at most once per <see cref="RepeatInterval"/>, shows
/// for <see cref="Lifetime"/> and can be dismissed for the rest of the guiding session. Thread-safe.
/// </summary>
public sealed class LiveHintAnalyser
{
    /// <summary>Frames (window, excluding dither/settle) needed before hints are evaluated.</summary>
    public const int MinFrames = 30;

    /// <summary>Oscillation index above which RA over-corrects (PHD2's alert threshold).</summary>
    public const double OscillationHigh = GuidingStatistics.OscillationAlertHigh;

    /// <summary>Oscillation index below which RA corrects too little (PHD2's alert threshold).</summary>
    public const double OscillationLow = GuidingStatistics.OscillationAlertLow;

    private const int RecentSize = 100;
    private const int SnrRecent = 10;

    private readonly object gate = new();
    private readonly Dictionary<string, DateTimeOffset> lastRaised = [];
    private readonly HashSet<string> dismissed = [];
    private readonly List<CoachFinding> active = [];
    private readonly Queue<Step> recent = new();
    private double? seeingFloor;

    public TimeSpan RepeatInterval { get; init; } = TimeSpan.FromMinutes(10);

    public TimeSpan Lifetime { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Seeing floor (total high-frequency RMS, arcsec) from the last coach drift measurement; enables hint.seeingBound.</summary>
    public double? SeeingFloorArcsec
    {
        get
        {
            lock (gate)
            {
                return seeingFloor;
            }
        }

        set
        {
            lock (gate)
            {
                seeingFloor = value;
            }
        }
    }

    /// <summary>Active hints: not expired and not dismissed.</summary>
    public IReadOnlyList<CoachFinding> GetActive(DateTimeOffset now)
    {
        lock (gate)
        {
            return active.Where(h => h.ExpiresAt is not { } e || e > now.UtcDateTime).ToList();
        }
    }

    /// <summary>Hides the hint for the rest of the guiding session. False when the id was never raised.</summary>
    public bool Dismiss(string id)
    {
        lock (gate)
        {
            bool known = lastRaised.ContainsKey(id) || active.Any(h => h.Id == id);
            active.RemoveAll(h => h.Id == id);
            if (known)
            {
                dismissed.Add(id);
            }

            return known;
        }
    }

    /// <summary>A new guiding session starts: dismissed and active hints and the recent-step history are cleared.</summary>
    public void ResetSession()
    {
        lock (gate)
        {
            dismissed.Clear();
            active.Clear();
            lastRaised.Clear();
            recent.Clear();
        }
    }

    /// <summary>Analyses a guide step; returns hints raised now.</summary>
    public IReadOnlyList<CoachFinding> Analyse(LiveHintInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        lock (gate)
        {
            recent.Enqueue(new Step(input.RaLimited, input.DecLimited, input.RaDurationMs > 0, input.DecDurationMs > 0 ? input.DecDirection : null, input.Snr));
            while (recent.Count > RecentSize)
            {
                recent.Dequeue();
            }

            var now = input.Time;
            active.RemoveAll(h => h.ExpiresAt is { } e && e <= now.UtcDateTime);
            var w = input.Stats.Window;
            if (w.IncludedFrames < MinFrames || recent.Count < MinFrames)
            {
                return [];
            }

            var raised = new List<CoachFinding>();
            foreach (var candidate in Candidates(input, w))
            {
                if (dismissed.Contains(candidate.Id) || (lastRaised.TryGetValue(candidate.Id, out var last) && now - last < RepeatInterval))
                {
                    continue;
                }

                lastRaised[candidate.Id] = now;
                active.RemoveAll(h => h.Id == candidate.Id);
                active.Add(candidate);
                raised.Add(candidate);
            }

            return raised;
        }
    }

    private IEnumerable<CoachFinding> Candidates(LiveHintInput input, GuidingStatsBlock w)
    {
        var ts = input.Time.UtcDateTime;
        var expires = ts + Lifetime;
        var s = input.Settings;

        // the Predictive algorithm sets its own gain: aggression advice doesn't apply to it
        bool aggressionTuned = s.RaAlgorithm.Kind != GuideAlgorithmKind.Predictive;
        if (aggressionTuned && w.OscillationIndex > OscillationHigh && w.RmsRaArcsec > 0.25)
        {
            yield return Hint(CoachCodes.HintRaOscillation, CoachSeverities.Warning, ts, expires,
                new() { ["index"] = Math.Round(w.OscillationIndex, 2) }, AggressionChange(s, -0.1));
        }
        else if (aggressionTuned && w.OscillationIndex < OscillationLow && w.RmsRaArcsec > 1.0)
        {
            yield return Hint(CoachCodes.HintRaSluggish, CoachSeverities.Info, ts, expires,
                new() { ["index"] = Math.Round(w.OscillationIndex, 2), ["rmsRaArcsec"] = Math.Round(w.RmsRaArcsec, 2) }, AggressionChange(s, +0.1));
        }

        int n = recent.Count;
        double raLimited = recent.Count(x => x.RaLimited) / (double)n;
        double decLimited = recent.Count(x => x.DecLimited) / (double)n;
        if (raLimited >= 0.1)
        {
            yield return Hint(CoachCodes.HintPulseLimited, CoachSeverities.Warning, ts, expires,
                new() { ["axis"] = "Ra", ["percent"] = Math.Round(raLimited * 100, 1) }, MaxPulseChange(CoachSettingNames.MaxRaDurationMs, s.MaxRaDurationMs), "Ra");
        }

        if (decLimited >= 0.1)
        {
            yield return Hint(CoachCodes.HintPulseLimited, CoachSeverities.Warning, ts, expires,
                new() { ["axis"] = "Dec", ["percent"] = Math.Round(decLimited * 100, 1) }, MaxPulseChange(CoachSettingNames.MaxDecDurationMs, s.MaxDecDurationMs), "Dec");
        }

        var snrs = recent.Select(x => x.Snr).ToArray();
        double lastSnr = snrs.Skip(n - SnrRecent).Average();
        double before = n > SnrRecent ? snrs.Take(n - SnrRecent).Average() : lastSnr;
        if (lastSnr < 10 || (before >= 20 && lastSnr < 0.5 * before))
        {
            yield return Hint(CoachCodes.HintLowSnr, CoachSeverities.Warning, ts, expires, new() { ["snr"] = Math.Round(lastSnr, 1) }, []);
        }

        // one-sided Dec corrections: guide Dec in one direction only, with the Drift mode that follows the drift when it
        // reverses (a fixed North or South would not); nothing to change in the one-direction modes
        int north = recent.Count(x => x.DecDirection == GuideDirection.North);
        int south = recent.Count(x => x.DecDirection == GuideDirection.South);
        if (north + south >= 15 && Math.Max(north, south) >= 0.9 * (north + south))
        {
            var changes = s.DecGuideMode == DecGuideMode.Auto
                ? new[] { new CoachSettingChange(CoachSettingNames.DecGuideMode, nameof(DecGuideMode.Drift), s.DecGuideMode.ToString()) }
                : [];
            yield return Hint(CoachCodes.HintDecDrift, CoachSeverities.Info, ts, expires,
                new() { ["driftArcsecPerMin"] = Math.Round(w.DecDriftArcsecPerMin ?? 0, 2), ["arcmin"] = Math.Round(w.PolarAlignmentErrorArcmin ?? 0, 1) },
                changes);
        }

        if (seeingFloor is { } floor && floor > 0 && w.RmsTotalArcsec <= 1.15 * floor)
        {
            yield return Hint(CoachCodes.HintSeeingBound, CoachSeverities.Good, ts, expires, new() { ["rmsArcsec"] = Math.Round(w.RmsTotalArcsec, 2) }, []);
        }
    }

    private static CoachFinding Hint(string code, string severity, DateTime ts, DateTime expires, Dictionary<string, object?> p,
        IReadOnlyList<CoachSettingChange> changes, string? qualifier = null) =>
        CoachFindings.Create(code, CoachStepNames.Live, severity, ts, p, changes: changes, qualifier: qualifier, expiresAt: expires);

    private static IReadOnlyList<CoachSettingChange> AggressionChange(GuiderSettings s, double delta)
    {
        if (CoachSettingsMap.GetParameter(s.RaAlgorithm, GuideAxis.Ra, "aggression") is not { } current)
        {
            return [];
        }

        double next = Math.Round(Math.Clamp(current + delta, 0.3, 1.0), 2);
        return Math.Abs(next - current) < 1e-6
            ? []
            : [new CoachSettingChange(CoachSettingNames.RaAggression, CoachSettingsMap.Format(next), CoachSettingsMap.Format(current))];
    }

    private static IReadOnlyList<CoachSettingChange> MaxPulseChange(string name, int current)
    {
        int next = Math.Min(8000, (int)Math.Round(current * 1.5 / 100.0) * 100);
        return next <= current ? [] : [new CoachSettingChange(name, next.ToString(CultureInfo.InvariantCulture), current.ToString(CultureInfo.InvariantCulture))];
    }

    private readonly record struct Step(bool RaLimited, bool DecLimited, bool RaPulse, GuideDirection? DecDirection, double Snr);
}
