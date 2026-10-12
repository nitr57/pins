// SPDX-License-Identifier: MPL-2.0

using NINA.Mcp.Jobs;
using NINA.Mcp.Observatory;

namespace NINA.Mcp.Tools.Catalog;

internal sealed class GetSequenceTool : ObservatoryTool {

    public GetSequenceTool(IPinsObservatory observatory) : base(observatory) {
    }

    public override string Name => "get_sequence";

    public override string Title => "Get advanced sequence";

    public override string Description =>
        "The sequence currently loaded in the advanced sequencer as a tree: each node has path (index path such as "
        + "\"1/0/2\"), name, type, status (Created, Running, Finished, Failed, Skipped, Disabled), details (e.g. target "
        + "coordinates, exposure time, filter, progress) and children. Also tells whether it is running. Use it to inspect "
        + "a loaded sequence before start_sequence and to follow progress (which target and exposure block is active).";

    public override ToolAnnotations Annotations => ToolAnnotations.Query;

    protected override void DefineArguments(ToolSchema arguments) {
        arguments.Integer("max_depth", "Maximum tree depth to return (1-10, default 4); deeper nodes are omitted.", minimum: 1, maximum: 10, defaultValue: 4);
    }

    protected override Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, ToolContext context, CancellationToken cancellationToken) {
        var depth = arguments.GetInt("max_depth") ?? 4;

        return Task.FromResult(ToolCallResult.Json(Observatory.GetSequence(depth)));
    }
}

internal sealed class ListSavedSequencesTool : ObservatoryTool {

    public ListSavedSequencesTool(IPinsObservatory observatory) : base(observatory) {
    }

    public override string Name => "list_saved_sequences";

    public override string Title => "List saved sequences";

    public override string Description =>
        "Sequence files saved in the profile's sequence folder: name (pass it to load_sequence), file name and last "
        + "modification time.";

    public override ToolAnnotations Annotations => ToolAnnotations.Query;

    protected override void DefineArguments(ToolSchema arguments) {
    }

    protected override Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, ToolContext context, CancellationToken cancellationToken) {
        var sequences = Observatory.ListSavedSequences();

        var payload = new {
            count = sequences.Count,
            sequences
        };

        return Task.FromResult(ToolCallResult.Json(payload));
    }
}

internal sealed class ListTargetTemplatesTool : ObservatoryTool {

    public ListTargetTemplatesTool(IPinsObservatory observatory) : base(observatory) {
    }

    public override string Name => "list_target_templates";

    public override string Title => "List target templates";

    public override string Description =>
        "Names of the saved target templates (deep sky object containers prepared by the user with their preferred "
        + "instructions: centering, autofocus triggers, exposure blocks, dithering). Pass one as 'template' to add_target to "
        + "reuse the user's own imaging recipe instead of building an exposure plan.";

    public override ToolAnnotations Annotations => ToolAnnotations.Query;

    protected override void DefineArguments(ToolSchema arguments) {
    }

    protected override Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, ToolContext context, CancellationToken cancellationToken) {
        var templates = Observatory.ListTargetTemplates();

        var payload = new {
            count = templates.Count,
            templates
        };

        return Task.FromResult(ToolCallResult.Json(payload));
    }
}

internal sealed class LoadSequenceTool : ObservatoryTool {

    public LoadSequenceTool(IPinsObservatory observatory) : base(observatory) {
    }

    public override string Name => "load_sequence";

    public override string Title => "Load sequence";

    public override string Description =>
        "Loads a saved sequence (name from list_saved_sequences) into the advanced sequencer, REPLACING the currently "
        + "loaded sequence; unsaved edits (e.g. targets added with add_target) are lost, so save_sequence first if needed. "
        + "Refused while a sequence runs. Does not start it: inspect with get_sequence, then start_sequence.";

    public override ToolAnnotations Annotations => ToolAnnotations.ChangeDestructive;

    public override SequenceGuard Guard => SequenceGuard.Strict;

    protected override void DefineArguments(ToolSchema arguments) {
        arguments.String("name", "Name of the saved sequence as listed by list_saved_sequences.", required: true, maxLength: 200);
    }

    protected override async Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, ToolContext context, CancellationToken cancellationToken) {
        var requested = arguments.GetString("name", required: true)!.Trim();
        var saved = Observatory.ListSavedSequences();

        var match = saved.FirstOrDefault(sequence => string.Equals(sequence.Name, requested, StringComparison.Ordinal))
            ?? saved.FirstOrDefault(sequence => string.Equals(sequence.Name, requested, StringComparison.OrdinalIgnoreCase));

        if (match is null) {
            var available = saved.Count == 0
                ? "There are no saved sequences."
                : $"Available: {string.Join(", ", saved.Select(sequence => $"\"{sequence.Name}\""))}.";

            return ToolCallResult.Error($"No saved sequence named \"{requested}\". {available}");
        }

        var result = await Observatory.LoadSequenceAsync(match.Name, cancellationToken).ConfigureAwait(false);

        return FromOperation(result);
    }
}

internal sealed class AddTargetTool : ObservatoryTool {

    private static readonly string[] PlanOnlyArguments = ["slew_and_center", "start_guiding", "autofocus_at_start", "dither_every"];

    public AddTargetTool(IPinsObservatory observatory) : base(observatory) {
    }

    public override string Name => "add_target";

    public override string Title => "Add target to sequence";

    public override string Description =>
        "Adds an imaging target to the end of the advanced sequence's target area. Two ways, pass exactly one: "
        + "(1) 'template': a saved target template (list_target_templates) whose target name and coordinates are replaced; "
        + "the template's own instructions are used and the plan options below are not allowed. "
        + "(2) 'exposures': an exposure plan; pins builds the target from standard instructions in this order: slew and "
        + "center with plate solving (slew_and_center, default true), autofocus (autofocus_at_start, default false), start "
        + "guiding (start_guiding, default true), then one exposure block per entry (filter, exposure, count, gain/offset/"
        + "binning, dithering every dither_every frames). Coordinates are J2000. Only edits the sequence (nothing moves); "
        + "check the result with get_sequence, then start_sequence. Adding while the sequence runs needs force.";

    public override ToolAnnotations Annotations => ToolAnnotations.Change;

    protected override void DefineArguments(ToolSchema arguments) {
        var exposure = new ToolSchema()
            .Number("exposure_seconds", "Exposure time of each frame in seconds (0.0001-3600).", minimum: 0.0001, maximum: 3600, required: true)
            .Integer("count", "Number of frames (1-1000).", minimum: 1, maximum: 1000, required: true)
            .String("filter", "Filter name as configured in the filter wheel, e.g. \"Ha\". Omit for no filter change.", maxLength: 100)
            .Integer("gain", "Camera gain. Omit for the profile default.", minimum: 0, maximum: 100000)
            .Integer("offset", "Camera offset. Omit for the profile default.", minimum: 0, maximum: 100000)
            .Integer("binning", "Symmetric binning (1-4). Omit for 1x1 / profile default.", minimum: 1, maximum: 4);

        arguments.String("name", "Target name used for the sequence container and file names, e.g. \"M 42\".", required: true, maxLength: 100);
        arguments.RightAscension("ra", SlewMountTool.RightAscensionDescription, required: true);
        arguments.Declination("dec", SlewMountTool.DeclinationDescription, required: true);
        arguments.Number(
            "rotation_degrees",
            "Desired sky position angle of the camera in degrees (0 <= x < 360), used by slew-and-center with a rotator. Omit to keep the default.",
            minimum: 0,
            maximum: 360,
            exclusiveMaximum: true);
        arguments.String("template", "Name of a saved target template (list_target_templates). Mutually exclusive with exposures.", maxLength: 200);
        arguments.ObjectArray(
            "exposures",
            "Exposure plan: one block per entry, executed in order. Mutually exclusive with template.",
            exposure,
            minItems: 1,
            maxItems: 20);
        arguments.Boolean("slew_and_center", "Exposure plan only: slew, plate solve and center (and rotate) before imaging. Default true.", defaultValue: true);
        arguments.Boolean("start_guiding", "Exposure plan only: start guiding before the exposures. Default true.", defaultValue: true);
        arguments.Boolean("autofocus_at_start", "Exposure plan only: run autofocus after centering. Default false.", defaultValue: false);
        arguments.Integer("dither_every", "Exposure plan only: dither after every N frames (0 = never, max 32 like NINA). Default 0.", minimum: 0, maximum: 32, defaultValue: 0);
    }

    protected override async Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, ToolContext context, CancellationToken cancellationToken) {
        var template = arguments.GetString("template")?.Trim();
        var exposures = arguments.GetObjectArray("exposures");

        if ((template is null) == (exposures is null)) {
            throw new ToolArgumentException("Pass exactly one of 'template' (a saved target template) or 'exposures' (an exposure plan).");
        }

        if (template is not null) {
            var planOnly = PlanOnlyArguments.Where(arguments.Has).ToList();

            if (planOnly.Count > 0) {
                throw new ToolArgumentException(
                    $"{string.Join(", ", planOnly)} only apply to an exposure plan; with a template its own instructions are used. Remove them.");
            }

            var templates = Observatory.ListTargetTemplates();

            var match = templates.FirstOrDefault(candidate => string.Equals(candidate, template, StringComparison.Ordinal))
                ?? templates.FirstOrDefault(candidate => string.Equals(candidate, template, StringComparison.OrdinalIgnoreCase));

            if (match is null) {
                var available = templates.Count == 0
                    ? "There are no saved target templates; pass 'exposures' instead."
                    : $"Available: {string.Join(", ", templates.Select(candidate => $"\"{candidate}\""))}.";

                return ToolCallResult.Error($"No target template named \"{template}\". {available}");
            }

            template = match;
        }

        var request = new TargetRequest(
            arguments.GetString("name", required: true)!.Trim(),
            CoordinateParser.ReadRightAscension(arguments, "ra")!.Value,
            CoordinateParser.ReadDeclination(arguments, "dec")!.Value,
            RotationDegrees: arguments.GetDouble("rotation_degrees"),
            TemplateName: template,
            Exposures: exposures?.Select(ToPlanItem).ToList(),
            SlewAndCenter: arguments.GetBool("slew_and_center") ?? true,
            StartGuiding: arguments.GetBool("start_guiding") ?? true,
            AutofocusAtStart: arguments.GetBool("autofocus_at_start") ?? false,
            DitherEvery: arguments.GetInt("dither_every") ?? 0);

        var result = await Observatory.AddTargetAsync(request, cancellationToken).ConfigureAwait(false);

        if (!result.Success) {
            return ToolCallResult.Error(result.Message);
        }

        return ToolCallResult.Json(new {
            success = true,
            message = result.Message,
            target = new {
                name = request.Name,
                raHours = request.RaHours,
                decDegrees = request.DecDegrees,
                ra = CoordinateParser.FormatRightAscension(request.RaHours),
                dec = CoordinateParser.FormatDeclination(request.DecDegrees),
                rotationDegrees = request.RotationDegrees,
                template = request.TemplateName,
                exposures = request.Exposures
            },
            next = "Review with get_sequence, then start_sequence (or save_sequence to keep it)."
        });
    }

    private static ExposurePlanItem ToPlanItem(ToolArguments item) {
        return new ExposurePlanItem(
            item.GetDouble("exposure_seconds", required: true)!.Value,
            item.GetInt("count", required: true)!.Value,
            Filter: item.GetString("filter")?.Trim(),
            Gain: item.GetInt("gain"),
            Offset: item.GetInt("offset"),
            Binning: item.GetInt("binning"));
    }
}

internal sealed class ClearTargetsTool : ObservatoryTool {

    public ClearTargetsTool(IPinsObservatory observatory) : base(observatory) {
    }

    public override string Name => "clear_targets";

    public override string Title => "Clear sequence targets";

    public override string Description =>
        "Removes all targets from the advanced sequence's target area; the start and end areas (cooling, unpark, park, "
        + "warm-up instructions) stay. Unsaved targets are lost. Refused while the sequence runs. Use it before building a "
        + "fresh plan with add_target.";

    public override ToolAnnotations Annotations => ToolAnnotations.ChangeDestructive;

    public override SequenceGuard Guard => SequenceGuard.Strict;

    protected override void DefineArguments(ToolSchema arguments) {
    }

    protected override async Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, ToolContext context, CancellationToken cancellationToken) {
        return FromOperation(await Observatory.ClearTargetsAsync(cancellationToken).ConfigureAwait(false));
    }
}

internal sealed class SaveSequenceTool : ObservatoryTool {

    public const int MaxNameLength = 100;

    public SaveSequenceTool(IPinsObservatory observatory) : base(observatory) {
    }

    public override string Name => "save_sequence";

    public override string Title => "Save sequence";

    public override string Description =>
        "Saves the current advanced sequence to the profile's sequence folder under 'name' (without extension; it appears "
        + "in list_saved_sequences and can be loaded later with load_sequence). Names may contain letters, digits, spaces, "
        + "'-', '_' and '.', no path separators. An existing file with that name is only replaced with overwrite: true. "
        + "Allowed while the sequence runs (it only writes a file); like NINA's Save As, the sequence then takes the new "
        + "name, which also changes $$SEQUENCETITLE$$ in the file names of the frames still to come.";

    /// <summary>Destructive: with overwrite it replaces an existing file.</summary>
    public override ToolAnnotations Annotations => ToolAnnotations.ChangeDestructive;

    public override SequenceGuard Guard => SequenceGuard.None;

    protected override void DefineArguments(ToolSchema arguments) {
        arguments.String("name", "File name without extension, e.g. \"M42 Ha-OIII 2026-10-11\".", required: true, maxLength: MaxNameLength + 5);
        arguments.Boolean("overwrite", "Replace an existing sequence with the same name. Default false.", defaultValue: false);
    }

    protected override async Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, ToolContext context, CancellationToken cancellationToken) {
        var name = NormalizeName(arguments.GetString("name", required: true)!);
        var overwrite = arguments.GetBool("overwrite") ?? false;

        return FromOperation(await Observatory.SaveSequenceAsync(name, overwrite, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Validates a sequence file name and strips a trailing ".json". Throws <see cref="ToolArgumentException"/>.</summary>
    public static string NormalizeName(string raw) {
        var name = raw.Trim();

        if (name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) {
            name = name[..^5].TrimEnd();
        }

        if (name.Length == 0) {
            throw new ToolArgumentException("Argument 'name' must not be empty.");
        }

        if (name.Length > MaxNameLength) {
            throw new ToolArgumentException($"Argument 'name' must be at most {MaxNameLength} characters long.");
        }

        var invalid = name.FirstOrDefault(character => !IsAllowed(character));

        if (invalid != default(char)) {
            throw new ToolArgumentException(
                $"Argument 'name' contains '{invalid}'. Allowed are letters, digits, spaces, '-', '_' and '.'; no path separators.");
        }

        if (name.StartsWith('.') || name.EndsWith('.')) {
            throw new ToolArgumentException("Argument 'name' must not start or end with '.'.");
        }

        return name;
    }

    private static bool IsAllowed(char character) {
        return char.IsLetterOrDigit(character) || character is ' ' or '-' or '_' or '.';
    }
}

internal sealed class StartSequenceTool : ObservatoryTool {

    public StartSequenceTool(IPinsObservatory observatory) : base(observatory) {
    }

    public override string Name => "start_sequence";

    public override string Title => "Start sequence";

    public override string Description =>
        "Starts the advanced sequence that is loaded (load_sequence) or built (add_target) and returns as soon as it runs; "
        + "it then runs on its own for hours, moving the mount, camera, focuser and guider. NINA validates the sequence first "
        + "(devices connected, filters exist, ...) and refuses with the issues listed; skip_validation bypasses that check "
        + "(not recommended). While it runs, other hardware and change tools are refused unless force is passed. Monitor "
        + "with get_status, get_sequence and get_recent_images; stop with stop_sequence (or stop_all). Finished items are "
        + "skipped on a new start: reset_sequence first to run everything again. Refused while a job started over MCP "
        + "(slew, capture, autofocus, focuser move, filter change, guiding start, switch, device connection) still runs: "
        + "wait for it with get_job or stop it with cancel_job. A running camera cooling job does not block the start.";

    /// <summary>
    /// Job resources the sequence would fight over: every device slot. Cooling (camera_cooler) is deliberately not listed.
    /// </summary>
    internal static readonly string[] HardwareResources = [.. DeviceKinds.All];

    public override ToolAnnotations Annotations => ToolAnnotations.Hardware;

    public override SequenceGuard Guard => SequenceGuard.Strict;

    protected override void DefineArguments(ToolSchema arguments) {
        arguments.Boolean("skip_validation", "Start even when NINA's sequence validation reports issues. Default false.", defaultValue: false);
    }

    protected override async Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, ToolContext context, CancellationToken cancellationToken) {
        var skipValidation = arguments.GetBool("skip_validation") ?? false;
        IDisposable reservation;

        // Held until the sequence runs (the sequence guard takes over from there), so no job can start in between.
        try {
            reservation = context.Jobs.Reserve(HardwareResources, "start_sequence");
        } catch (JobConflictException ex) {
            var busy = RunningJobsHolding(context.Jobs, HardwareResources);

            if (busy.Count == 0) {
                return ToolCallResult.Error($"Refused: {ex.Message}");
            }

            var jobs = string.Join(", ", busy.Select(job => $"{job.Id} ({job.Description})"));

            return ToolCallResult.Error(
                $"Refused: MCP jobs still use the hardware the sequence needs: {jobs}. Wait for them with get_job or stop them with cancel_job, then retry.");
        }

        using (reservation) {
            return FromOperation(await Observatory.StartSequenceAsync(skipValidation, cancellationToken).ConfigureAwait(false));
        }
    }
}

internal sealed class StopSequenceTool : ObservatoryTool {

    public StopSequenceTool(IPinsObservatory observatory) : base(observatory) {
    }

    public override string Name => "stop_sequence";

    public override string Title => "Stop sequence";

    public override string Description =>
        "Stops the running advanced sequence (the current instruction is interrupted, e.g. the exposure is aborted). Does "
        + "not park the mount, stop guiding or warm the camera. Progress is kept: start_sequence continues with unfinished "
        + "items, reset_sequence starts over. Safe to call when nothing runs. Never refused by the sequence guard.";

    public override ToolAnnotations Annotations => ToolAnnotations.Stop;

    public override SequenceGuard Guard => SequenceGuard.None;

    protected override void DefineArguments(ToolSchema arguments) {
    }

    protected override Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, ToolContext context, CancellationToken cancellationToken) {
        return Task.FromResult(FromOperation(Observatory.StopSequence()));
    }
}

internal sealed class ResetSequenceTool : ObservatoryTool {

    public ResetSequenceTool(IPinsObservatory observatory) : base(observatory) {
    }

    public override string Name => "reset_sequence";

    public override string Title => "Reset sequence progress";

    public override string Description =>
        "Resets the progress of every item in the advanced sequence (finished exposures count from zero again) so that "
        + "start_sequence runs it from the beginning. Does not touch hardware. Refused while the sequence runs.";

    public override ToolAnnotations Annotations => ToolAnnotations.ChangeDestructive;

    public override SequenceGuard Guard => SequenceGuard.Strict;

    protected override void DefineArguments(ToolSchema arguments) {
    }

    protected override Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, ToolContext context, CancellationToken cancellationToken) {
        return Task.FromResult(FromOperation(Observatory.ResetSequence()));
    }
}
