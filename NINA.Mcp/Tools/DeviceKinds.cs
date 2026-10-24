// SPDX-License-Identifier: MPL-2.0

using NINA.Mcp.Observatory;

namespace NINA.Mcp.Tools;

/// <summary>Maps <see cref="DeviceKind"/> to the snake_case names used in tool arguments and payloads ("filter_wheel").</summary>
public static class DeviceKinds {

    private static readonly IReadOnlyDictionary<DeviceKind, string> Names = new Dictionary<DeviceKind, string> {
        [DeviceKind.Camera] = "camera",
        [DeviceKind.Mount] = "mount",
        [DeviceKind.Focuser] = "focuser",
        [DeviceKind.FilterWheel] = "filter_wheel",
        [DeviceKind.Guider] = "guider",
        [DeviceKind.Rotator] = "rotator",
        [DeviceKind.FlatDevice] = "flat_device",
        [DeviceKind.Dome] = "dome",
        [DeviceKind.Switch] = "switch",
        [DeviceKind.Weather] = "weather",
        [DeviceKind.SafetyMonitor] = "safety_monitor"
    };

    /// <summary>All names in enum order.</summary>
    public static IReadOnlyList<string> All { get; } = Enum.GetValues<DeviceKind>().Select(ToName).ToList();

    public static string ToName(DeviceKind kind) {
        return Names.TryGetValue(kind, out var name)
            ? name
            : throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown device kind.");
    }

    /// <summary>Accepts "filter_wheel", "FilterWheel", "filter wheel" and "filter-wheel" (case-insensitive).</summary>
    public static bool TryParse(string? text, out DeviceKind kind) {
        kind = default;

        if (string.IsNullOrWhiteSpace(text)) {
            return false;
        }

        var normalized = ArgumentSpec.NormalizeEnumValue(text);

        foreach (var pair in Names) {
            if (ArgumentSpec.NormalizeEnumValue(pair.Value) == normalized) {
                kind = pair.Key;
                return true;
            }
        }

        return false;
    }

    /// <summary>Reads a required device argument; throws <see cref="ToolArgumentException"/> listing the valid names.</summary>
    public static DeviceKind Read(ToolArguments arguments, string name) {
        var text = arguments.GetString(name, required: true);

        if (TryParse(text, out var kind)) {
            return kind;
        }

        throw new ToolArgumentException($"Argument '{name}' must be one of: {string.Join(", ", All)} (got \"{text}\").");
    }
}
