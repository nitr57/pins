// SPDX-License-Identifier: MPL-2.0

using NINA.Mcp.Observatory;
using System.Globalization;

namespace NINA.Mcp.Tools.Catalog;

internal sealed class GetSystemHealthTool : ObservatoryTool {

    /// <summary>Clock error that spoils plate solving hints, planning and meridian flips.</summary>
    internal const double ClockWarningSeconds = 30;

    internal const double LowDiskGb = 5;

    internal const double HotCelsius = 80;

    public GetSystemHealthTool(IPinsObservatory observatory) : base(observatory) {
    }

    public override string Name => "get_system_health";

    public override string Title => "Get system health";

    public override string Description =>
        "Health of the pins box: local time and, when client_time is given, the clock error against it (a Raspberry Pi "
        + "without RTC battery and without network starts with a wrong clock, which spoils plate solving, planning and "
        + "meridian flips), whether the clock is NTP-synchronized, the mount's UTC time, free disk space for images and for "
        + "pins, CPU temperature, power problems (under-voltage, frequency capping, thermal throttling, now and since boot: "
        + "brown-outs at the field power supply show up here), memory and load. warnings lists what needs attention. Call it at "
        + "the start of a session (with client_time set to the current UTC time of the client) and when frames fail to save or "
        + "devices drop. Side-effect free.";

    public override ToolAnnotations Annotations => ToolAnnotations.Query;

    protected override void DefineArguments(ToolSchema arguments) {
        arguments.String(
            "client_time",
            "The client's current time as ISO 8601 with offset or Z, e.g. \"2026-10-13T21:05:00Z\", to measure the pins clock error.",
            maxLength: 40);
    }

    protected override Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, ToolContext context, CancellationToken cancellationToken) {
        var clientText = arguments.GetString("client_time");
        DateTimeOffset? client = null;

        if (!string.IsNullOrWhiteSpace(clientText)) {
            if (!DateTimeOffset.TryParse(clientText.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)) {
                throw new ToolArgumentException($"Argument 'client_time' must be an ISO 8601 time like 2026-10-13T21:05:00Z (got \"{clientText}\").");
            }

            client = parsed;
        }

        var health = Observatory.GetSystemHealth();
        var warnings = new List<string>();

        double? clockOffset = client is null ? null : Math.Round((health.LocalTime - client.Value).TotalSeconds, 1);
        double? mountOffset = health.MountUtc is null ? null : Math.Round((health.MountUtc.Value - health.LocalTime).TotalSeconds, 1);

        if (clockOffset is not null && Math.Abs(clockOffset.Value) > ClockWarningSeconds) {
            warnings.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"The pins clock is {clockOffset:+0;-0} s off the client's time. Fix the system time before plate solving, planning or a sequence with meridian flips."));
        }

        if (health.ClockSynchronized == false) {
            warnings.Add("The system clock is not NTP-synchronized (no internet or time server); compare it with client_time.");
        }

        if (mountOffset is not null && Math.Abs(mountOffset.Value) > ClockWarningSeconds) {
            warnings.Add(string.Create(CultureInfo.InvariantCulture, $"The mount's clock differs from pins by {mountOffset:+0;-0} s."));
        }

        foreach (var disk in new[] { health.ImageDisk, health.AppDisk }) {
            if (disk is not null && disk.FreeGb < LowDiskGb) {
                warnings.Add(string.Create(CultureInfo.InvariantCulture, $"Only {disk.FreeGb:0.0} GB free on the disk of {disk.Path}."));
            }
        }

        if (health.CpuTemperatureCelsius > HotCelsius) {
            warnings.Add(string.Create(CultureInfo.InvariantCulture, $"CPU temperature {health.CpuTemperatureCelsius:0} °C: the box overheats."));
        }

        if (health.Throttling is { } throttling) {
            if (throttling.UnderVoltageNow || throttling.UnderVoltageSinceBoot) {
                warnings.Add(throttling.UnderVoltageNow
                    ? "Under-voltage right now: the supply is too weak or the cable too thin. Devices may drop and files may get corrupted."
                    : "Under-voltage occurred since boot: check the supply and cables (inrush when switching loads, dew heaters).");
            }

            if (throttling.ThrottledNow || throttling.SoftTemperatureLimitNow) {
                warnings.Add("The CPU is throttled right now (temperature or power): plate solving and image processing are slower.");
            }
        }

        var payload = new {
            localTime = health.LocalTime,
            clockOffsetSeconds = clockOffset,
            clockSynchronized = health.ClockSynchronized,
            mountUtc = health.MountUtc,
            mountClockOffsetSeconds = mountOffset,
            uptimeHours = health.UptimeHours is null ? (double?)null : Math.Round(health.UptimeHours.Value, 2),
            imageDisk = health.ImageDisk,
            appDisk = health.AppDisk,
            cpuTemperatureCelsius = health.CpuTemperatureCelsius,
            throttling = health.Throttling,
            memoryAvailableMb = health.MemoryAvailableMb,
            memoryTotalMb = health.MemoryTotalMb,
            loadAverage1m = health.LoadAverage1m,
            warnings
        };

        return Task.FromResult(ToolCallResult.Json(payload));
    }
}
