// SPDX-License-Identifier: MPL-2.0

using NINA.Mcp.Observatory;

namespace NINA.Mcp.Tools.Catalog;

internal sealed class FindTargetsTool : ObservatoryTool {

    private static readonly IReadOnlyList<string> Orders = ["name", "magnitude", "size"];

    public FindTargetsTool(IPinsObservatory observatory) : base(observatory) {
    }

    public override string Name => "find_targets";

    public override string Title => "Find targets in the catalog";

    public override string Description =>
        "Searches NINA's offline deep sky catalog on the pins box (the sky atlas database: Messier, NGC, IC, Sharpless, "
        + "common names), so coordinates never have to be guessed, also without internet. Returns J2000 RA in hours and Dec in "
        + "degrees ready for plan_targets, add_target or center_target, plus type, constellation, magnitude, surface "
        + "brightness, size in arcmin and position angle. Search by name (\"M 42\", \"NGC 7000\", \"Orion Nebula\", substring "
        + "match; an exact designation or alias comes first, then names that start with it) or browse with filters: types (GALXY galaxy, BRTNB bright nebula, PLNNB planetary, OPNCL open cluster, GLOCL "
        + "globular, DRKNB dark nebula, SNREM supernova remnant, CL+NB cluster with nebula, ASTER asterism), constellation "
        + "(three-letter IAU code such as ORI or CYG), max_magnitude and size limits; order by name, magnitude (brightest "
        + "first) or size (largest first). Side-effect free.";

    public override ToolAnnotations Annotations => ToolAnnotations.Query;

    protected override void DefineArguments(ToolSchema arguments) {
        arguments.String("name", "Designation or common name to search for (substring, case-insensitive), e.g. \"M 31\" or \"Veil\".", maxLength: 100);
        arguments.String("types", "Comma-separated type codes, e.g. \"GALXY\" or \"BRTNB,SNREM\".", maxLength: 200, pattern: "^[A-Za-z0-9+, ]+$");
        arguments.String("constellation", "Three-letter IAU constellation code, e.g. \"ORI\".", maxLength: 3, pattern: "^[A-Za-z]{3}$");
        arguments.Number("max_magnitude", "Only objects at least this bright (visual magnitude, e.g. 10).", minimum: -30, maximum: 30);
        arguments.Number("min_size_arcmin", "Only objects at least this large (major axis, arcmin).", minimum: 0, maximum: 10000);
        arguments.Number("max_size_arcmin", "Only objects at most this large (major axis, arcmin).", minimum: 0, maximum: 10000);
        arguments.Enum("order", "Sort order: name (default with a name), magnitude (brightest first, default otherwise) or size (largest first).", Orders);
        arguments.Integer("limit", "Maximum number of results (1-50). Default 10.", minimum: 1, maximum: 50, defaultValue: 10);
    }

    protected override async Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, ToolContext context, CancellationToken cancellationToken) {
        var name = arguments.GetString("name")?.Trim();
        var types = (arguments.GetString("types") ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(type => type.ToUpperInvariant())
            .Distinct()
            .ToList();
        var constellation = arguments.GetString("constellation")?.Trim().ToUpperInvariant();
        var maxMagnitude = arguments.GetDouble("max_magnitude");
        var minSize = arguments.GetDouble("min_size_arcmin");
        var maxSize = arguments.GetDouble("max_size_arcmin");

        var order = arguments.GetString("order") is { } text
            ? ArgumentSpec.NormalizeEnumValue(text)
            : string.IsNullOrEmpty(name) ? "magnitude" : "name";

        if (string.IsNullOrEmpty(name) && types.Count == 0 && constellation is null && maxMagnitude is null && minSize is null && maxSize is null) {
            throw new ToolArgumentException("Give a name or at least one filter (types, constellation, max_magnitude, min_size_arcmin, max_size_arcmin).");
        }

        if (minSize is not null && maxSize is not null && minSize > maxSize) {
            throw new ToolArgumentException("Argument 'min_size_arcmin' must not be larger than 'max_size_arcmin'.");
        }

        var search = new CatalogSearch(
            string.IsNullOrEmpty(name) ? null : name,
            types,
            constellation,
            maxMagnitude,
            minSize,
            maxSize,
            order,
            arguments.GetInt("limit") ?? 10);

        var result = await Observatory.FindTargetsAsync(search, cancellationToken).ConfigureAwait(false);

        if (!result.Success) {
            return ToolCallResult.Error(result.Message);
        }

        var objects = result.Objects
            .Select(item => new {
                name = item.Name,
                aliases = item.Aliases,
                type = item.Type,
                constellation = item.Constellation,
                raHours = Math.Round(item.RaHours, 5),
                decDegrees = Math.Round(item.DecDegrees, 4),
                ra = CoordinateParser.FormatRightAscension(item.RaHours),
                dec = CoordinateParser.FormatDeclination(item.DecDegrees),
                magnitude = item.Magnitude,
                surfaceBrightness = item.SurfaceBrightness,
                sizeArcmin = item.SizeArcmin is null ? (double?)null : Math.Round(item.SizeArcmin.Value, 1),
                sizeMinArcmin = item.SizeMinArcmin is null ? (double?)null : Math.Round(item.SizeMinArcmin.Value, 1),
                positionAngleDegrees = item.PositionAngleDegrees
            })
            .ToList();

        return ToolCallResult.Json(new {
            count = objects.Count,
            objects,
            next = objects.Count == 0
                ? "Nothing found. Try a shorter name (\"M 42\" rather than \"Messier 42\"), fewer filters, or another type code."
                : "Use raHours/decDegrees (J2000) with plan_targets to check visibility tonight."
        });
    }
}
