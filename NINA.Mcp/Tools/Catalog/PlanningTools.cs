// SPDX-License-Identifier: MPL-2.0

using NINA.Mcp.Observatory;
using System.Globalization;

namespace NINA.Mcp.Tools.Catalog;

internal sealed class PlanTargetsTool : ObservatoryTool {

    /// <summary>Sampling step of the night: 289 samples from noon to noon.</summary>
    internal static readonly TimeSpan Step = TimeSpan.FromMinutes(5);

    public const int MaxTargets = 10;

    public PlanTargetsTool(IPinsObservatory observatory) : base(observatory) {
    }

    public override string Name => "plan_targets";

    public override string Title => "Plan targets for a night";

    public override string Description =>
        "Night planning at the site in the pins profile (latitude, longitude, elevation, custom horizon), computed with "
        + "NINA's astrometry (SOFA/NOVAS) for one observing night from local noon to noon: sunset, civil/nautical/astronomical "
        + "dusk and dawn, sunrise and the dark hours; moon illumination, phase, rise and set; and for each target (J2000) its "
        + "altitude and azimuth now, transit time and altitude, when it rises above and sets below the altitude limit (the "
        + "higher of min_altitude and the custom horizon at its azimuth), usable hours in the dark, the longest usable window "
        + "and the moon separation in degrees. Times are local with UTC offset, to 5 minutes. Use it to choose targets and "
        + "order them, to check whether a target is still high enough, and before add_target. Side-effect free; does not "
        + "move anything.";

    public override ToolAnnotations Annotations => ToolAnnotations.Query;

    protected override void DefineArguments(ToolSchema arguments) {
        var target = new ToolSchema()
            .String("name", "Target name for the result, e.g. \"M 31\".", required: true, maxLength: 100)
            .RightAscension("ra", SlewMountTool.RightAscensionDescription, required: true)
            .Declination("dec", SlewMountTool.DeclinationDescription, required: true);

        arguments.ObjectArray(
            "targets",
            $"Targets to plan (1-{MaxTargets}), each with name, ra (J2000 hours) and dec (J2000 degrees).",
            target,
            minItems: 1,
            maxItems: MaxTargets,
            required: true);
        arguments.String(
            "date",
            "Local date (yyyy-MM-dd) of the evening the night starts. Omit for the current night (the one that contains now; "
            + "before noon that is the night that began the previous evening).",
            maxLength: 10,
            pattern: "^[0-9]{4}-[0-9]{2}-[0-9]{2}$");
        arguments.Number("min_altitude", "Altitude limit in degrees (0-89). Default 30.", minimum: 0, maximum: 89, defaultValue: 30);
        arguments.Enum("darkness", "Which twilight counts as dark: astronomical (sun below -18°, default) or nautical (below -12°).", ["astronomical", "nautical"]);
        arguments.Boolean("hourly", "Also return an hourly table (sun, moon and each target's altitude) between civil dusk and dawn. Default false.", defaultValue: false);
    }

    protected override Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, ToolContext context, CancellationToken cancellationToken) {
        var targets = arguments.GetObjectArray("targets", required: true)!
            .Select(item => new SkyTargetRequest(
                item.GetString("name", required: true)!.Trim(),
                CoordinateParser.ReadRightAscension(item, "ra")!.Value,
                CoordinateParser.ReadDeclination(item, "dec")!.Value))
            .ToList();

        var night = ReadDate(arguments.GetString("date"));
        var minAltitude = arguments.GetDouble("min_altitude") ?? 30;
        var darkness = ArgumentSpec.NormalizeEnumValue(arguments.GetString("darkness") ?? "astronomical") == "nautical"
            ? Darkness.Nautical
            : Darkness.Astronomical;
        var hourly = arguments.GetBool("hourly") ?? false;

        var sky = Observatory.GetNightSky(new NightSkyRequest(night, Step, targets));

        if (!sky.Success) {
            return Task.FromResult(ToolCallResult.Error(sky.Message));
        }

        if (!IsConsistent(sky, targets.Count)) {
            return Task.FromResult(ToolCallResult.Error("The night sky calculation returned inconsistent data; the pins log has details."));
        }

        return Task.FromResult(ToolCallResult.Json(NightPlanner.Summarize(sky, minAltitude, darkness, hourly)));
    }

    private static DateOnly? ReadDate(string? text) {
        if (string.IsNullOrWhiteSpace(text)) {
            return null;
        }

        if (DateOnly.TryParseExact(text.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) {
            return date;
        }

        throw new ToolArgumentException($"Argument 'date' must be a date like 2026-10-13 (got \"{text}\").");
    }

    /// <summary>Every series must have one value per sample, and there must be one result per requested target.</summary>
    private static bool IsConsistent(NightSky sky, int targetCount) {
        var count = sky.Times.Count;

        if (count < 2 || sky.SunAltitude.Count != count || sky.MoonAltitude.Count != count || sky.Targets.Count != targetCount) {
            return false;
        }

        return sky.Targets.All(target => target.Altitude.Count == count
            && target.Azimuth.Count == count
            && target.MoonSeparation.Count == count
            && (target.HorizonAltitude.Count == count || target.HorizonAltitude.Count == 0));
    }
}
