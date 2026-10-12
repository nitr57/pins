// SPDX-License-Identifier: MPL-2.0

using NINA.Mcp.Jobs;
using NINA.Mcp.Observatory;
using System.Globalization;

namespace NINA.Mcp.Tools.Catalog;

internal sealed class CaptureImageTool : ObservatoryTool {

    public CaptureImageTool(IPinsObservatory observatory) : base(observatory) {
    }

    public override string Name => "capture_image";

    public override string Title => "Capture image";

    public override string Description =>
        "Takes one exposure with the imaging camera (changing the filter first when 'filter' is set) and returns its "
        + "statistics in outcome.data (HFR in pixels, star count, mean/median ADU). The frame is also added to the image "
        + "history (get_recent_images). Use it for test frames: framing, focus check, exposure check. By default the frame is "
        + "analysed but not saved; save: true writes it like a normal LIGHT frame (the file path appears in get_recent_images "
        + "once it is written). Omitted gain/offset use the profile defaults; omitted binning keeps the camera's current "
        + "binning. Runs as a background job holding the camera and refused while a focuser move, filter change or mount "
        + "job (slew, park, home) runs; the default wait covers short exposures, poll get_job for long ones. For many "
        + "frames build a sequence with add_target instead.";

    public override ToolAnnotations Annotations => ToolAnnotations.Hardware;

    protected override bool StartsJob => true;

    protected override void DefineArguments(ToolSchema arguments) {
        arguments.Number("exposure_seconds", "Exposure time in seconds (0.0001-3600).", minimum: 0.0001, maximum: 3600, required: true);
        arguments.String("filter", "Filter name as configured in the filter wheel (see get_status), e.g. \"Ha\" or \"L\". Omit to keep the current filter.", maxLength: 100);
        arguments.Integer("gain", "Camera gain (driver units). Omit for the profile default gain.", minimum: 0, maximum: 100000);
        arguments.Integer("offset", "Camera offset (driver units). Omit for the profile default offset.", minimum: 0, maximum: 100000);
        arguments.Integer("binning", "Symmetric binning factor (1-4; 2 = 2x2). Omit to keep the current binning.", minimum: 1, maximum: 4);
        arguments.Boolean("save", "Save the frame to disk as a LIGHT frame. Default false (snapshot, analysed only).", defaultValue: false);
    }

    protected override Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, ToolContext context, CancellationToken cancellationToken) {
        var filter = arguments.GetString("filter")?.Trim();

        var request = new CaptureRequest(
            arguments.GetDouble("exposure_seconds", required: true)!.Value,
            Filter: filter,
            Gain: arguments.GetInt("gain"),
            Offset: arguments.GetInt("offset"),
            Binning: arguments.GetInt("binning"),
            Save: arguments.GetBool("save") ?? false);

        var description = filter is null
            ? string.Create(CultureInfo.InvariantCulture, $"Capture {request.ExposureSeconds:0.####} s")
            : string.Create(CultureInfo.InvariantCulture, $"Capture {request.ExposureSeconds:0.####} s ({filter})");

        var job = new JobRequest("capture", description, "camera") {
            RequiresIdle = ["filter_wheel", "focuser", "mount"]
        };

        return RunJobAsync(
            arguments,
            context,
            job,
            async token => {
                var result = await Observatory.CaptureAsync(request, token).ConfigureAwait(false);

                return result.Success
                    ? JobOutcome.Ok(result.Message, result.Image)
                    : JobOutcome.Fail(result.Message, result.Image);
            },
            cancellationToken);
    }
}

internal sealed class CoolCameraTool : ObservatoryTool {

    public CoolCameraTool(IPinsObservatory observatory) : base(observatory) {
    }

    public override string Name => "cool_camera";

    public override string Title => "Cool camera";

    public override string Description =>
        "Turns the camera cooler on and ramps the sensor to target_celsius over duration_minutes (a slow ramp avoids "
        + "thermal stress and icing). Usually takes several minutes, so the call returns a running job: poll get_job or "
        + "watch the camera temperature in get_status. The job holds the 'camera_cooler' resource, so test captures are "
        + "possible meanwhile. Typical target: -10 °C (check what the user's camera can reach).";

    public override ToolAnnotations Annotations => ToolAnnotations.Hardware;

    protected override bool StartsJob => true;

    protected override void DefineArguments(ToolSchema arguments) {
        arguments.Number("target_celsius", "Target sensor temperature in °C (-50 to 30).", minimum: -50, maximum: 30, required: true);
        arguments.Number("duration_minutes", "Ramp duration in minutes (0-60, default 10; 0 = set the target at once).", minimum: 0, maximum: 60, defaultValue: 10);
    }

    protected override Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, ToolContext context, CancellationToken cancellationToken) {
        var target = arguments.GetDouble("target_celsius", required: true)!.Value;
        var minutes = arguments.GetDouble("duration_minutes") ?? 10;

        return RunJobAsync(
            arguments,
            context,
            new JobRequest(
                "cool",
                string.Create(CultureInfo.InvariantCulture, $"Cool camera to {target:0.#} °C over {minutes:0.#} min"),
                "camera_cooler"),
            async token => JobPayload.ToOutcome(
                await Observatory.CoolCameraAsync(target, TimeSpan.FromMinutes(minutes), token).ConfigureAwait(false)),
            cancellationToken);
    }
}

internal sealed class WarmCameraTool : ObservatoryTool {

    public WarmCameraTool(IPinsObservatory observatory) : base(observatory) {
    }

    public override string Name => "warm_camera";

    public override string Title => "Warm camera";

    public override string Description =>
        "Ramps the camera sensor back to ambient over duration_minutes and then turns the cooler off. Do this at the end of "
        + "a session before disconnecting the camera. Returns a running job (holds 'camera_cooler'); poll get_job.";

    public override ToolAnnotations Annotations => ToolAnnotations.Hardware;

    protected override bool StartsJob => true;

    protected override void DefineArguments(ToolSchema arguments) {
        arguments.Number("duration_minutes", "Ramp duration in minutes (0-60, default 10).", minimum: 0, maximum: 60, defaultValue: 10);
    }

    protected override Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, ToolContext context, CancellationToken cancellationToken) {
        var minutes = arguments.GetDouble("duration_minutes") ?? 10;

        return RunJobAsync(
            arguments,
            context,
            new JobRequest("warm", string.Create(CultureInfo.InvariantCulture, $"Warm camera over {minutes:0.#} min"), "camera_cooler"),
            async token => JobPayload.ToOutcome(
                await Observatory.WarmCameraAsync(TimeSpan.FromMinutes(minutes), token).ConfigureAwait(false)),
            cancellationToken);
    }
}
