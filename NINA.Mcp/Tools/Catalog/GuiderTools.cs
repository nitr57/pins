// SPDX-License-Identifier: MPL-2.0

using NINA.Mcp.Observatory;
using System.Globalization;

namespace NINA.Mcp.Tools.Catalog;

internal sealed class StartGuidingTool : ObservatoryTool {

    public StartGuidingTool(IPinsObservatory observatory) : base(observatory) {
    }

    public override string Name => "start_guiding";

    public override string Title => "Start guiding";

    public override string Description =>
        "Starts autoguiding with the connected guider (selects a guide star, calibrates if needed, then guides). The mount "
        + "must track. Calibration can take a few minutes, so this may return a running job: poll get_job. Afterwards watch "
        + "guider state and RMS (arcsec) in get_status; get_guider_report has the details. Refused while a mount job (slew, "
        + "park, home, centering) runs. A sequence built with add_target starts guiding by itself.";

    public override ToolAnnotations Annotations => ToolAnnotations.Hardware;

    protected override bool StartsJob => true;

    protected override void DefineArguments(ToolSchema arguments) {
        arguments.Boolean(
            "force_calibration",
            "Discard the existing calibration and calibrate again (after changing the guide setup or a large declination change). Default false.",
            defaultValue: false);
    }

    protected override Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, ToolContext context, CancellationToken cancellationToken) {
        var forceCalibration = arguments.GetBool("force_calibration") ?? false;

        var description = forceCalibration
            ? "Start guiding (new calibration)"
            : "Start guiding";

        return RunJobAsync(
            arguments,
            context,
            new JobRequest("guide_start", description, "guider") {
                RequiresIdle = ["mount"]
            },
            async token => JobPayload.ToOutcome(await Observatory.StartGuidingAsync(forceCalibration, token).ConfigureAwait(false)),
            cancellationToken);
    }
}

internal sealed class StopGuidingTool : ObservatoryTool {

    public StopGuidingTool(IPinsObservatory observatory) : base(observatory) {
    }

    public override string Name => "stop_guiding";

    public override string Title => "Stop guiding";

    public override string Description =>
        "Stops autoguiding. Does not move the mount; tracking continues. Claims no resource, so it also works while a "
        + "start_guiding job is still calibrating (cancel that job with cancel_job to abort the start instead). While a "
        + "sequence runs it needs force, because stopping guiding spoils the sequence's exposures.";

    public override ToolAnnotations Annotations => ToolAnnotations.Stop;

    protected override bool StartsJob => true;

    protected override void DefineArguments(ToolSchema arguments) {
    }

    protected override Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, ToolContext context, CancellationToken cancellationToken) {
        return RunJobAsync(
            arguments,
            context,
            new JobRequest("guide_stop", "Stop guiding", null),
            async token => JobPayload.ToOutcome(await Observatory.StopGuidingAsync(token).ConfigureAwait(false)),
            cancellationToken);
    }
}

internal sealed class SetSwitchTool : ObservatoryTool {

    public SetSwitchTool(IPinsObservatory observatory) : base(observatory) {
    }

    public override string Name => "set_switch";

    public override string Title => "Set switch";

    public override string Description =>
        "Sets one writable switch of the connected switch device (power outlets, dew heaters, USB ports). get_status lists "
        + "the switches with index, name, current value, min, max and writable flag; on/off switches use 0 and 1. Switching "
        + "power can cut off devices (camera, mount): confirm with the user first. Runs as a background job holding the "
        + "switch device.";

    public override ToolAnnotations Annotations => ToolAnnotations.Hardware;

    protected override bool StartsJob => true;

    protected override void DefineArguments(ToolSchema arguments) {
        arguments.Integer("index", "Zero-based switch index as listed in get_status.", minimum: 0, maximum: 1000, required: true);
        arguments.Number("value", "New value within the switch's min..max (0/1 for on/off switches).", required: true);
    }

    protected override Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, ToolContext context, CancellationToken cancellationToken) {
        var index = arguments.GetInt("index", required: true)!.Value;
        var value = arguments.GetDouble("value", required: true)!.Value;

        return RunJobAsync(
            arguments,
            context,
            new JobRequest("switch", string.Create(CultureInfo.InvariantCulture, $"Set switch {index} to {value:0.###}"), "switch"),
            async token => JobPayload.ToOutcome(await Observatory.SetSwitchAsync(index, value, token).ConfigureAwait(false)),
            cancellationToken);
    }
}
