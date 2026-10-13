// SPDX-License-Identifier: MPL-2.0

using NINA.Mcp.Observatory;
using System.Globalization;

namespace NINA.Mcp.Tools.Catalog;

internal sealed class MoveRotatorTool : ObservatoryTool {

    /// <summary>Turning the camera during an exposure smears it.</summary>
    internal static readonly string[] RotatorRequiresIdle = ["camera"];

    public MoveRotatorTool(IPinsObservatory observatory) : base(observatory) {
    }

    public override string Name => "move_rotator";

    public override string Title => "Move rotator";

    public override string Description =>
        "Turns the camera rotator to a position angle in degrees (0-360). By default this is the sky position angle as NINA "
        + "uses it after a plate solve synced the rotator (plate_solve with sync, center_target); before any sync it equals "
        + "the mechanical position. mechanical: true moves to the mechanical position of the device instead. NINA may adjust "
        + "the target to the rotator's allowed range (the result reports where it ended). Runs as a background job holding "
        + "the rotator; refused while a capture, plate solve, centering or autofocus job runs, because rotating smears an "
        + "exposure. A sky position angle needs a synced rotator. get_status shows the current position.";

    public override ToolAnnotations Annotations => ToolAnnotations.Hardware;

    protected override bool StartsJob => true;

    protected override void DefineArguments(ToolSchema arguments) {
        arguments.Number("position", "Target position angle in degrees (0 <= position < 360).", minimum: 0, maximum: 360, required: true, exclusiveMaximum: true);
        arguments.Boolean("mechanical", "Move to the mechanical position instead of the sky position angle. Default false.", defaultValue: false);
    }

    protected override Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, ToolContext context, CancellationToken cancellationToken) {
        var position = arguments.GetDouble("position", required: true)!.Value;
        var mechanical = arguments.GetBool("mechanical") ?? false;

        var description = string.Create(
            CultureInfo.InvariantCulture,
            $"Move rotator to {position:0.##}°{(mechanical ? " (mechanical)" : string.Empty)}");

        return RunJobAsync(
            arguments,
            context,
            new JobRequest("rotator", description, "rotator") {
                RequiresIdle = RotatorRequiresIdle
            },
            async token => JobPayload.ToOutcome(await Observatory.MoveRotatorAsync(position, mechanical, token).ConfigureAwait(false)),
            cancellationToken);
    }
}

internal sealed class ControlFlatDeviceTool : ObservatoryTool {

    /// <summary>Moving the cover or switching the light ruins a running exposure.</summary>
    internal static readonly string[] FlatDeviceRequiresIdle = ["camera"];

    private static readonly IReadOnlyDictionary<string, FlatDeviceAction> Actions = new Dictionary<string, FlatDeviceAction> {
        ["open_cover"] = FlatDeviceAction.OpenCover,
        ["close_cover"] = FlatDeviceAction.CloseCover,
        ["light_on"] = FlatDeviceAction.LightOn,
        ["light_off"] = FlatDeviceAction.LightOff,
        ["set_brightness"] = FlatDeviceAction.SetBrightness
    };

    public ControlFlatDeviceTool(IPinsObservatory observatory) : base(observatory) {
    }

    public override string Name => "control_flat_device";

    public override string Title => "Control flat panel";

    public override string Description =>
        "Controls the flat panel / dust cover (flat_device): open or close the cover, switch the light on or off, or set its "
        + "brightness (in the device's own units; get_status lists brightness, minBrightness and maxBrightness, and coverState "
        + "and lightOn where the device supports them). Typical flats: close the cover, set the brightness, switch the light "
        + "on, then take test exposures with capture_image; open the cover again before imaging the sky. Runs as a background "
        + "job holding the flat device; refused while a capture, plate solve, centering or autofocus job runs.";

    public override ToolAnnotations Annotations => ToolAnnotations.Hardware;

    protected override bool StartsJob => true;

    protected override void DefineArguments(ToolSchema arguments) {
        arguments.Enum("action", "What to do: open_cover, close_cover, light_on, light_off or set_brightness.", Actions.Keys.ToList(), required: true);
        arguments.Integer("brightness", "Brightness for set_brightness, within the device's min..max (see get_status). Only allowed with set_brightness.", minimum: 0, maximum: 1000000);
    }

    protected override Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, ToolContext context, CancellationToken cancellationToken) {
        var action = ReadAction(arguments.GetString("action", required: true)!);
        var brightness = arguments.GetInt("brightness");

        if (action == FlatDeviceAction.SetBrightness && brightness is null) {
            throw new ToolArgumentException("Argument 'brightness' is required for action set_brightness.");
        }

        if (action != FlatDeviceAction.SetBrightness && brightness is not null) {
            throw new ToolArgumentException("Argument 'brightness' only applies to action set_brightness; to switch the light use light_on or light_off.");
        }

        var description = action == FlatDeviceAction.SetBrightness
            ? string.Create(CultureInfo.InvariantCulture, $"Flat panel brightness {brightness}")
            : $"Flat device: {ActionName(action)}";

        return RunJobAsync(
            arguments,
            context,
            new JobRequest("flat_device", description, "flat_device") {
                RequiresIdle = FlatDeviceRequiresIdle
            },
            async token => JobPayload.ToOutcome(await Observatory.ControlFlatDeviceAsync(action, brightness, token).ConfigureAwait(false)),
            cancellationToken);
    }

    private static FlatDeviceAction ReadAction(string text) {
        var normalized = ArgumentSpec.NormalizeEnumValue(text);

        foreach (var pair in Actions) {
            if (ArgumentSpec.NormalizeEnumValue(pair.Key) == normalized) {
                return pair.Value;
            }
        }

        throw new ToolArgumentException($"Argument 'action' must be one of: {string.Join(", ", Actions.Keys)} (got \"{text}\").");
    }

    private static string ActionName(FlatDeviceAction action) {
        return Actions.First(pair => pair.Value == action).Key.Replace('_', ' ');
    }
}

internal sealed class ControlDomeTool : ObservatoryTool {

    private static readonly IReadOnlyDictionary<string, DomeAction> Actions = new Dictionary<string, DomeAction> {
        ["open_shutter"] = DomeAction.OpenShutter,
        ["close_shutter"] = DomeAction.CloseShutter,
        ["park"] = DomeAction.Park,
        ["find_home"] = DomeAction.FindHome,
        ["slew_to_azimuth"] = DomeAction.SlewToAzimuth,
        ["sync_to_mount"] = DomeAction.SyncToMount,
        ["start_following"] = DomeAction.StartFollowing,
        ["stop_following"] = DomeAction.StopFollowing
    };

    public ControlDomeTool(IPinsObservatory observatory) : base(observatory) {
    }

    public override string Name => "control_dome";

    public override string Title => "Control dome";

    public override string Description =>
        "Controls the dome or roll-off roof: open_shutter, close_shutter, park, find_home, slew_to_azimuth (azimuth in "
        + "degrees from north through east), sync_to_mount (turn the slit to where the telescope points once), "
        + "start_following / stop_following (keep the slit on the telescope as it moves). Opening exposes the telescope to the "
        + "weather and closing can hit a telescope pointing low: check get_status (dome shutter, mount position, weather and "
        + "safety_monitor) and confirm with the user. Other jobs do not block it, so closing stays possible in bad weather; "
        + "while the advanced sequence runs it needs force like every hardware tool. If the profile parks the mount before "
        + "shutter moves, NINA parks it as part of open_shutter and close_shutter. Runs as a background job holding the dome; "
        + "shutters can take minutes, poll get_job; cancel_job stops the dome. Not every dome supports every action; roll-off "
        + "roofs that open by unparking are not covered.";

    public override ToolAnnotations Annotations => ToolAnnotations.Hardware;

    protected override bool StartsJob => true;

    protected override void DefineArguments(ToolSchema arguments) {
        arguments.Enum("action", "What to do: " + string.Join(", ", Actions.Keys) + ".", Actions.Keys.ToList(), required: true);
        arguments.Number("azimuth", "Target azimuth in degrees (0 <= azimuth < 360) for slew_to_azimuth. Only allowed with that action.", minimum: 0, maximum: 360, exclusiveMaximum: true);
    }

    protected override Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, ToolContext context, CancellationToken cancellationToken) {
        var text = arguments.GetString("action", required: true)!;
        var action = ReadAction(text);
        var azimuth = arguments.GetDouble("azimuth");

        if (action == DomeAction.SlewToAzimuth && azimuth is null) {
            throw new ToolArgumentException("Argument 'azimuth' is required for action slew_to_azimuth.");
        }

        if (action != DomeAction.SlewToAzimuth && azimuth is not null) {
            throw new ToolArgumentException("Argument 'azimuth' only applies to action slew_to_azimuth.");
        }

        var name = Actions.First(pair => pair.Value == action).Key;
        var description = azimuth is null
            ? $"Dome: {name.Replace('_', ' ')}"
            : string.Create(CultureInfo.InvariantCulture, $"Dome: slew to azimuth {azimuth:0.#}°");

        return RunJobAsync(
            arguments,
            context,
            new JobRequest("dome", description, "dome"),
            async token => JobPayload.ToOutcome(await Observatory.ControlDomeAsync(action, azimuth, token).ConfigureAwait(false)),
            cancellationToken);
    }

    private static DomeAction ReadAction(string text) {
        var normalized = ArgumentSpec.NormalizeEnumValue(text);

        foreach (var pair in Actions) {
            if (ArgumentSpec.NormalizeEnumValue(pair.Key) == normalized) {
                return pair.Value;
            }
        }

        throw new ToolArgumentException($"Argument 'action' must be one of: {string.Join(", ", Actions.Keys)} (got \"{text}\").");
    }
}
