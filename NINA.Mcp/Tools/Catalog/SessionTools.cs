// SPDX-License-Identifier: MPL-2.0

using NINA.Mcp.Observatory;
using System.Globalization;

namespace NINA.Mcp.Tools.Catalog;

internal sealed class GetSessionSummaryTool : ObservatoryTool {

    /// <summary>The image history is kept in memory for the current pins run; this bounds what is summarized.</summary>
    internal const int MaxImages = 5000;

    /// <summary>A frame is suspect when its HFR exceeds the group median by this factor...</summary>
    internal const double HfrOutlierFactor = 1.3;

    /// <summary>...or its star count falls below this share of the group median (clouds, dew, lost guiding).</summary>
    internal const double StarsOutlierShare = 0.5;

    private static readonly IReadOnlyList<string> Groupings = ["target_filter", "target", "filter"];

    public GetSessionSummaryTool(IPinsObservatory observatory) : base(observatory) {
    }

    public override string Name => "get_session_summary";

    public override string Title => "Get session summary";

    public override string Description =>
        "Summary of the frames taken in this pins run (the image history, also what the sequence captured), grouped by "
        + "target and filter: frame count, total integration in hours, first and last frame time, HFR (pixels: median, 90th "
        + "percentile, best, worst), star count, FWHM (arcsec) and eccentricity medians, guiding RMS (arcsec, median and "
        + "worst), camera temperature range, and suspect frames (HFR more than 30 % above the group median, or fewer than "
        + "half the median stars: focus drift, clouds, dew or guiding trouble) with their ids. By default only LIGHT frames "
        + "count; image_type all includes snapshots, flats and darks. Use it for the morning report and to decide whether a "
        + "target needs more time. The history starts empty after a pins restart. Side-effect free.";

    public override ToolAnnotations Annotations => ToolAnnotations.Query;

    protected override void DefineArguments(ToolSchema arguments) {
        arguments.Enum("group_by", "Grouping: target_filter (default), target or filter.", Groupings);
        arguments.Enum("image_type", "Frames to include: light (default) or all.", ["light", "all"]);
        arguments.Number("since_hours", "Only frames from the last N hours (0.1-72).", minimum: 0.1, maximum: 72);
    }

    protected override Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, ToolContext context, CancellationToken cancellationToken) {
        var groupBy = ArgumentSpec.NormalizeEnumValue(arguments.GetString("group_by") ?? "target_filter");
        var lightsOnly = ArgumentSpec.NormalizeEnumValue(arguments.GetString("image_type") ?? "light") == "light";
        var sinceHours = arguments.GetDouble("since_hours");

        var images = Observatory.GetRecentImages(MaxImages);
        var now = Observatory.GetStatus().Time;

        var selected = images
            .Where(image => !lightsOnly || string.Equals(image.ImageType, "LIGHT", StringComparison.OrdinalIgnoreCase))
            .Where(image => sinceHours is null || image.Time is null || image.Time >= now - TimeSpan.FromHours(sinceHours.Value))
            .OrderBy(image => image.Time ?? DateTimeOffset.MinValue)
            .ThenBy(image => image.Id)
            .ToList();

        var groups = selected
            .GroupBy(image => groupBy switch {
                "target" => (Target: image.TargetName ?? "(no target)", Filter: (string?)null),
                "filter" => (Target: (string?)null, Filter: image.Filter ?? "(no filter)"),
                _ => (Target: image.TargetName ?? "(no target)", Filter: image.Filter ?? "(no filter)")
            })
            .Select(group => Summarize(group.Key.Target, group.Key.Filter, group.ToList()))
            .ToList();

        var types = images
            .GroupBy(image => image.ImageType ?? "UNKNOWN", StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count());

        var payload = new {
            frames = selected.Count,
            integrationHours = Round(selected.Sum(image => image.ExposureSeconds ?? 0) / 3600, 2),
            from = selected.FirstOrDefault()?.Time,
            to = selected.LastOrDefault()?.Time,
            imageTypesInHistory = types,
            truncated = images.Count >= MaxImages ? true : (bool?)null,
            groups,
            next = selected.Count == 0
                ? "No frames yet in this pins run (the history starts empty after a restart)."
                : "get_recent_images has the individual frames; read_log explains failures around suspect frames."
        };

        return Task.FromResult(ToolCallResult.Json(payload));
    }

    private static object Summarize(string? target, string? filter, List<ImageRecord> images) {
        var hfr = Values(images.Select(image => image.Hfr));
        var stars = Values(images.Select(image => (double?)image.Stars));
        var medianHfr = Percentile(hfr, 0.5);
        var medianStars = Percentile(stars, 0.5);

        var suspects = images
            .Where(image =>
                (medianHfr is not null && image.Hfr is not null && image.Hfr > medianHfr * HfrOutlierFactor)
                || (medianStars is not null && image.Stars is not null && image.Stars < medianStars * StarsOutlierShare))
            .Select(image => new {
                id = image.Id,
                time = image.Time,
                hfr = image.Hfr,
                stars = image.Stars,
                guidingRmsArcsec = image.GuidingRmsArcsec,
                reason = Reason(image, medianHfr, medianStars)
            })
            .ToList();

        var temperatures = Values(images.Select(image => image.CameraTemperature));
        var rms = Values(images.Select(image => image.GuidingRmsArcsec));

        return new {
            target,
            filter,
            frames = images.Count,
            integrationHours = Round(images.Sum(image => image.ExposureSeconds ?? 0) / 3600, 2),
            exposureSeconds = images.Select(image => image.ExposureSeconds).Where(value => value is not null).Distinct().ToList(),
            first = images.First().Time,
            last = images.Last().Time,
            hfr = hfr.Count == 0
                ? null
                : new {
                    median = Round(medianHfr, 2),
                    p90 = Round(Percentile(hfr, 0.9), 2),
                    best = Round(hfr.Min(), 2),
                    worst = Round(hfr.Max(), 2)
                },
            starsMedian = Round(medianStars, 0),
            fwhmArcsecMedian = Round(Percentile(Values(images.Select(image => image.FwhmArcsec)), 0.5), 2),
            eccentricityMedian = Round(Percentile(Values(images.Select(image => image.Eccentricity)), 0.5), 2),
            guidingRmsArcsec = rms.Count == 0
                ? null
                : new {
                    median = Round(Percentile(rms, 0.5), 2),
                    worst = Round(rms.Max(), 2)
                },
            cameraTemperature = temperatures.Count == 0
                ? null
                : new {
                    min = Round(temperatures.Min(), 1),
                    max = Round(temperatures.Max(), 1)
                },
            suspectFrames = suspects.Count == 0 ? null : suspects.Take(20).ToList(),
            suspectCount = suspects.Count
        };
    }

    private static string Reason(ImageRecord image, double? medianHfr, double? medianStars) {
        var reasons = new List<string>();

        if (medianHfr is not null && image.Hfr > medianHfr * HfrOutlierFactor) {
            reasons.Add(string.Create(CultureInfo.InvariantCulture, $"HFR {image.Hfr:0.##} vs median {medianHfr:0.##}"));
        }

        if (medianStars is not null && image.Stars < medianStars * StarsOutlierShare) {
            reasons.Add(string.Create(CultureInfo.InvariantCulture, $"{image.Stars} stars vs median {medianStars:0}"));
        }

        return string.Join("; ", reasons);
    }

    private static List<double> Values(IEnumerable<double?> values) {
        return values
            .Where(value => value is not null && double.IsFinite(value.Value))
            .Select(value => value!.Value)
            .OrderBy(value => value)
            .ToList();
    }

    /// <summary>Linear interpolation between closest ranks of a sorted list; null when empty.</summary>
    internal static double? Percentile(List<double> sorted, double share) {
        if (sorted.Count == 0) {
            return null;
        }

        var position = share * (sorted.Count - 1);
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);

        return sorted[lower] + (sorted[upper] - sorted[lower]) * (position - lower);
    }

    private static double? Round(double? value, int digits) {
        return value is null || !double.IsFinite(value.Value) ? null : Math.Round(value.Value, digits, MidpointRounding.AwayFromZero);
    }
}
