// SPDX-License-Identifier: MPL-2.0

using NINA.Mcp.Jobs;
using NINA.Mcp.Observatory;

namespace NINA.Mcp.Tools.Catalog;

internal sealed class GetStatusTool : ObservatoryTool {

    public GetStatusTool(IPinsObservatory observatory) : base(observatory) {
    }

    public override string Name => "get_status";

    public override string Title => "Get observatory status";

    public override string Description =>
        "Snapshot of the whole observatory: every device slot (camera, mount, focuser, filter_wheel, guider, rotator, "
        + "flat_device, dome, switch, weather, safety_monitor) with connected flag, device name and key readings "
        + "(e.g. camera temperature in °C and cooler power, mount RA in hours / Dec in degrees (J2000; the mount's native "
        + "epoch values are in raHoursMount/decDegreesMount), altitude, tracking, park state, focuser position in steps, "
        + "current filter, guider state (e.g. Guiding, Looping, Calibrating, Stopped) and RMS in arcsec), whether the advanced sequence "
        + "is running and which items run, and all running background jobs. Fast and side-effect free; it does not wait "
        + "for hardware. Call it first in every session, before acting on hardware, and to monitor progress.";

    public override ToolAnnotations Annotations => ToolAnnotations.Query;

    protected override void DefineArguments(ToolSchema arguments) {
    }

    protected override Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, ToolContext context, CancellationToken cancellationToken) {
        var status = Observatory.GetStatus();

        var runningJobs = context.Jobs.List()
            .Where(job => job.State == JobState.Running)
            .Select(JobPayload.Describe)
            .ToList();

        var devices = status.Devices
            .Select(device => new {
                device = DeviceKinds.ToName(device.Kind),
                connected = device.Connected,
                name = device.Name,
                properties = device.Properties
            })
            .ToList();

        var payload = new {
            time = status.Time,
            sequence = new {
                running = status.SequenceRunning,
                runningItems = status.RunningSequenceItems
            },
            devices,
            runningJobs
        };

        return Task.FromResult(ToolCallResult.Json(payload));
    }
}

internal sealed class GetRecentImagesTool : ObservatoryTool {

    public GetRecentImagesTool(IPinsObservatory observatory) : base(observatory) {
    }

    public override string Name => "get_recent_images";

    public override string Title => "Get recent images";

    public override string Description =>
        "The newest entries of the image history (captures by the sequence and by capture_image), newest first: time, "
        + "image type, filter, exposure in seconds, HFR (half flux radius in pixels, lower is sharper), detected star count, "
        + "mean/median/standard deviation in ADU, camera temperature in °C, guiding RMS in arcsec, target name and file path. "
        + "Use it to judge focus (rising HFR → run_autofocus), clouds (star count drops), and sequence progress.";

    public override ToolAnnotations Annotations => ToolAnnotations.Query;

    protected override void DefineArguments(ToolSchema arguments) {
        arguments.Integer("count", "Number of images to return (1-50).", minimum: 1, maximum: 50, defaultValue: 10);
    }

    protected override Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, ToolContext context, CancellationToken cancellationToken) {
        var count = arguments.GetInt("count") ?? 10;

        var images = Observatory.GetRecentImages(count);

        var payload = new {
            count = images.Count,
            images
        };

        return Task.FromResult(ToolCallResult.Json(payload));
    }
}
