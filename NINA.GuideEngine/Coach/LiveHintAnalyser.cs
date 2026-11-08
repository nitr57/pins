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
/// for <see cref="Lifetime"/> and can be dismissed for the rest of the guiding session. One-time hints
/// (<see cref="IsOneTime"/>) are raised once per guiding session, stay until their condition ends and, once dismissed or
/// applied, are never raised again (<see cref="PermanentlyDismissed"/>, <see cref="SetSuppressed"/>). Thread-safe.
/// </summary>
public sealed class LiveHintAnalyser
{
    /// <summary>Frames (window, excluding dither/settle) needed before hints are evaluated.</summary>
    public const int MinFrames = 30;

    /// <summary>Oscillation index above which RA over-corrects (PHD2's alert threshold).</summary>
    public const double OscillationHigh = GuidingStatistics.OscillationAlertHigh;

    /// <summary>Oscillation index below which RA corrects too little (PHD2's alert threshold).</summary>
    public const double OscillationLow = GuidingStatistics.OscillationAlertLow;

    /// <summary>Guide exposure (seconds) above which hint.longExposure suggests a shorter one.</summary>
    public const double LongExposureSeconds = 2.0;

    /// <summary>Exposure (seconds) hint.longExposure suggests: the default guide exposure.</summary>
    public const double SuggestedExposureSeconds = 1.0;

    private const int RecentSize = 100;
    private const int SnrRecent = 10;

    private readonly object gate = new();
    private readonly Dictionary<string, DateTimeOffset> lastRaised = [];
    private readonly HashSet<string> dismissed = [];
    private readonly HashSet<string> suppressed = [];
    private readonly List<CoachFinding> active = [];
    private readonly Queue<Step> recent = new();
    private double? seeingFloor;

    /// <summary>A one-time hint was dismissed or applied: the host stores the id and hands it back with <see cref="SetSuppressed"/>.</summary>
    public event Action<string>? PermanentlyDismissed;

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

    /// <summary>Hints shown once and never again after they were dismissed or applied.</summary>
    public static bool IsOneTime(string id) => id == CoachCodes.HintLongExposure;

    /// <summary>One-time hints dismissed in an earlier session (stored by the host): never raised. Replaces the previous set.</summary>
    public void SetSuppressed(IEnumerable<string> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        lock (gate)
        {
            suppressed.Clear();
            suppressed.UnionWith(ids.Where(IsOneTime));
            active.RemoveAll(h => suppressed.Contains(h.Id));
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

    /// <summary>
    /// Hides the hint for the rest of the guiding session, a one-time hint for good (<see cref="PermanentlyDismissed"/>).
    /// False when the id was never raised.
    /// </summary>
    public bool Dismiss(string id)
    {
        bool permanent;
        lock (gate)
        {
            bool known = lastRaised.ContainsKey(id) || active.Any(h => h.Id == id);
            active.RemoveAll(h => h.Id == id);
            if (!known)
            {
                return false;
            }

            dismissed.Add(id);
            permanent = IsOneTime(id) && suppressed.Add(id);
        }

        if (permanent)
        {
            PermanentlyDismissed?.Invoke(id);
        }

        return true;
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
            var candidates = Candidates(input, w).ToList();

            // a one-time hint stays while its condition holds (no expiry) and goes when it ends, e.g. a shorter exposure
            active.RemoveAll(h => IsOneTime(h.Id) && !candidates.Any(c => c.Id == h.Id));
            foreach (var candidate in candidates)
            {
                bool oneTime = IsOneTime(candidate.Id);
                if (dismissed.Contains(candidate.Id) || (oneTime && (suppressed.Contains(candidate.Id) || lastRaised.ContainsKey(candidate.Id)))
                    || (lastRaised.TryGetValue(candidate.Id, out var last) && now - last < RepeatInterval))
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
        // the hint names the polar alignment error it points to: without one (near the pole) it said "about 0.0′"
        if (north + south >= 15 && Math.Max(north, south) >= 0.9 * (north + south) && w.PolarAlignmentErrorArcmin is double arcmin)
        {
            var changes = s.DecGuideMode == DecGuideMode.Auto
                ? new[] { new CoachSettingChange(CoachSettingNames.DecGuideMode, nameof(DecGuideMode.Drift), s.DecGuideMode.ToString()) }
                : [];
            yield return Hint(CoachCodes.HintDecDrift, CoachSeverities.Info, ts, expires,
                new() { ["driftArcsecPerMin"] = Math.Round(w.DecDriftArcsecPerMin ?? 0, 2), ["arcmin"] = Math.Round(arcmin, 1) },
                changes);
        }

        // long guide exposures react late to the mount's faster errors (wind, gear noise); shorter ones usually guide better
        double exposure = s.ExposureMs / 1000.0;
        if (exposure > LongExposureSeconds + 1e-6)
        {
            yield return Hint(CoachCodes.HintLongExposure, CoachSeverities.Info, ts, null,
                new() { ["exposureSeconds"] = Math.Round(exposure, 2), ["suggestedSeconds"] = SuggestedExposureSeconds },
                [new CoachSettingChange(CoachSettingNames.ExposureSeconds, CoachSettingsMap.Format(SuggestedExposureSeconds), CoachSettingsMap.Format(exposure))]);
        }

        if (seeingFloor is { } floor && floor > 0 && w.RmsTotalArcsec <= 1.15 * floor)
        {
            yield return Hint(CoachCodes.HintSeeingBound, CoachSeverities.Good, ts, expires, new() { ["rmsArcsec"] = Math.Round(w.RmsTotalArcsec, 2) }, []);
        }
    }

    private static CoachFinding Hint(string code, string severity, DateTime ts, DateTime? expires, Dictionary<string, object?> p,
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
