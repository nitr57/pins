// SPDX-License-Identifier: MPL-2.0

using NINA.Mcp.Observatory;

namespace NINA.Mcp.Tools.Catalog;

internal abstract class DeviceConnectionTool : ObservatoryTool {

    protected DeviceConnectionTool(IPinsObservatory observatory) : base(observatory) {
    }

    public override ToolAnnotations Annotations => ToolAnnotations.Hardware;

    protected override bool StartsJob => true;

    protected abstract bool Connect { get; }

    protected override void DefineArguments(ToolSchema arguments) {
        arguments.Enum("device", "Device slot.", DeviceKinds.All, required: true);
    }

    protected override Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, ToolContext context, CancellationToken cancellationToken) {
        var kind = DeviceKinds.Read(arguments, "device");
        var name = DeviceKinds.ToName(kind);

        var request = Connect
            ? new JobRequest("connect", $"Connect {name}", name)
            : new JobRequest("disconnect", $"Disconnect {name}", name);

        if (!Connect) {
            request = request with {
                RequiresIdle = DisconnectRequiresIdle(kind)
            };
        }

        return RunJobAsync(
            arguments,
            context,
            request,
            async token => {
                var result = Connect
                    ? await Observatory.ConnectAsync(kind, token).ConfigureAwait(false)
                    : await Observatory.DisconnectAsync(kind, token).ConfigureAwait(false);

                return JobPayload.ToOutcome(result);
            },
            cancellationToken);
    }

    /// <summary>
    /// Jobs that use a device without holding its own resource: cooling holds "camera_cooler", autofocus holds "camera"
    /// but drives the focuser and filter wheel, a capture with a filter holds "camera", guiding drives the mount.
    /// </summary>
    internal static IReadOnlyList<string> DisconnectRequiresIdle(DeviceKind kind) {
        return kind switch {
            DeviceKind.Camera => ["camera_cooler"],
            DeviceKind.Focuser => ["camera"],
            DeviceKind.FilterWheel => ["camera"],
            DeviceKind.Mount => ["guider"],
            _ => []
        };
    }
}

internal sealed class ConnectDeviceTool : DeviceConnectionTool {

    public ConnectDeviceTool(IPinsObservatory observatory) : base(observatory) {
    }

    public override string Name => "connect_device";

    public override string Title => "Connect device";

    public override string Description =>
        "Connects the device selected in the active pins profile for one slot (camera, mount, focuser, filter_wheel, "
        + "guider, rotator, flat_device, dome, switch, weather, safety_monitor). Which physical device is used is configured "
        + "in the profile, not here. Connecting an already connected device is harmless. Runs as a background job holding "
        + "that device. Check get_status first to see what is already connected.";

    protected override bool Connect => true;
}

internal sealed class DisconnectDeviceTool : DeviceConnectionTool {

    public DisconnectDeviceTool(IPinsObservatory observatory) : base(observatory) {
    }

    public override string Name => "disconnect_device";

    public override string Title => "Disconnect device";

    public override string Description =>
        "Disconnects one device slot. Disconnecting a cooled camera stops its cooler abruptly: run warm_camera first "
        + "(refused while a cool_camera or warm_camera job runs). Refused while a job uses the device indirectly (focuser or "
        + "filter wheel during a capture/autofocus job, mount during a guiding start). "
        + "Disconnecting the mount does not park it: run park_mount first when ending a session. Runs as a background job "
        + "holding that device.";

    protected override bool Connect => false;
}
