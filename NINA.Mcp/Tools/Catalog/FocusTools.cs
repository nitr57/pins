// SPDX-License-Identifier: MPL-2.0

using NINA.Mcp.Jobs;
using NINA.Mcp.Observatory;

namespace NINA.Mcp.Tools.Catalog;

internal sealed class MoveFocuserTool : ObservatoryTool {

    public MoveFocuserTool(IPinsObservatory observatory) : base(observatory) {
    }

    public override string Name => "move_focuser";

    public override string Title => "Move focuser";

    public override string Description =>
        "Moves the focuser to an absolute position in steps (see the current position in get_status). Runs as a background "
        + "job holding the focuser; refused while a camera job (capture or autofocus) runs, because moving focus would spoil "
        + "it. Prefer run_autofocus for finding focus; use this to return to a known good position.";

    public override ToolAnnotations Annotations => ToolAnnotations.Hardware;

    protected override bool StartsJob => true;

    protected override void DefineArguments(ToolSchema arguments) {
        arguments.Integer("position", "Absolute target position in focuser steps (>= 0).", minimum: 0, required: true);
    }

    protected override Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, ToolContext context, CancellationToken cancellationToken) {
        var position = arguments.GetInt("position", required: true)!.Value;

        var job = new JobRequest("focuser_move", $"Move focuser to {position}", "focuser") {
            RequiresIdle = ["camera"]
        };

        return RunJobAsync(
            arguments,
            context,
            job,
            async token => JobPayload.ToOutcome(await Observatory.MoveFocuserAsync(position, token).ConfigureAwait(false)),
            cancellationToken);
    }
}

internal sealed class RunAutofocusTool : ObservatoryTool {

    public RunAutofocusTool(IPinsObservatory observatory) : base(observatory) {
    }

    public override string Name => "run_autofocus";

    public override string Title => "Run autofocus";

    public override string Description =>
        "Runs NINA's autofocus routine with the profile's autofocus settings on the current filter (change_filter first to "
        + "focus on another filter): takes a series of exposures at several focuser positions, fits the HFR curve and moves to "
        + "the best position. Needs a connected camera and focuser and stars in the field (the mount should track a star field). "
        + "Takes one to several minutes, so it usually returns a running job: poll get_job. outcome.data holds the final "
        + "position, HFR, filter and temperature. The job holds the 'camera' resource (autofocus uses camera and focuser) and "
        + "is refused while a focuser job runs.";

    public override ToolAnnotations Annotations => ToolAnnotations.Hardware;

    protected override bool StartsJob => true;

    protected override void DefineArguments(ToolSchema arguments) {
    }

    protected override Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, ToolContext context, CancellationToken cancellationToken) {
        var job = new JobRequest("autofocus", "Autofocus", "camera") {
            RequiresIdle = ["focuser"]
        };

        return RunJobAsync(
            arguments,
            context,
            job,
            async token => {
                var result = await Observatory.RunAutofocusAsync(token).ConfigureAwait(false);

                var data = new {
                    position = result.Position,
                    hfr = result.Hfr,
                    filter = result.Filter,
                    temperature = result.Temperature
                };

                return result.Success
                    ? JobOutcome.Ok(result.Message, data)
                    : JobOutcome.Fail(result.Message, data);
            },
            cancellationToken);
    }
}

internal sealed class ChangeFilterTool : ObservatoryTool {

    public ChangeFilterTool(IPinsObservatory observatory) : base(observatory) {
    }

    public override string Name => "change_filter";

    public override string Title => "Change filter";

    public override string Description =>
        "Moves the filter wheel to a filter, given either by name (case-insensitive, as listed in get_status) or by "
        + "zero-based position; pass exactly one of them. NINA applies the profile's focus offsets for the filter. Runs as a "
        + "background job holding the filter wheel; refused while a camera job runs.";

    public override ToolAnnotations Annotations => ToolAnnotations.Hardware;

    protected override bool StartsJob => true;

    protected override void DefineArguments(ToolSchema arguments) {
        arguments.String("name", "Filter name, e.g. \"Ha\". Mutually exclusive with position.", maxLength: 100);
        arguments.Integer("position", "Zero-based filter slot. Mutually exclusive with name.", minimum: 0, maximum: 100);
    }

    protected override Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, ToolContext context, CancellationToken cancellationToken) {
        var name = arguments.GetString("name")?.Trim();
        var position = arguments.GetInt("position");

        if ((name is null) == (position is null)) {
            throw new ToolArgumentException("Pass exactly one of 'name' or 'position'.");
        }

        var description = name is null
            ? $"Change filter to position {position}"
            : $"Change filter to {name}";

        var job = new JobRequest("filter_change", description, "filter_wheel") {
            RequiresIdle = ["camera"]
        };

        return RunJobAsync(
            arguments,
            context,
            job,
            async token => JobPayload.ToOutcome(await Observatory.ChangeFilterAsync(name, position, token).ConfigureAwait(false)),
            cancellationToken);
    }
}
