// SPDX-License-Identifier: MPL-2.0

using NINA.Mcp.Observatory;

namespace NINA.Mcp.Tools.Catalog;

internal sealed class GetEventsTool : ObservatoryTool {

    private static readonly IReadOnlyList<string> Severities = ["info", "warning", "error", "critical"];

    private readonly ObservatoryEventLog events;

    public GetEventsTool(IPinsObservatory observatory, ObservatoryEventLog events) : base(observatory) {
        this.events = events ?? throw new ArgumentNullException(nameof(events));
    }

    public override string Name => "get_events";

    public override string Title => "Get events";

    public override string Description =>
        "What happened in pins, oldest first: the safety monitor turning unsafe or safe, devices connecting or dropping, "
        + "sequence start and end, images saved or failing to save (disk full), camera download timeouts, guiding started "
        + "or stopped, meridian flips, mount park and unpark, internal guider alerts, star losses and flight recorder "
        + "incidents. get_status only shows the state now; this shows what happened in between. Without since_id it "
        + "returns the newest events; pass nextSinceId from the previous result as since_id to get only newer ones, and "
        + "wait_seconds to long-poll for the next event (a cheap way to watch a night). missed is true when events were "
        + "dropped because the buffer (the last 2000; info events go first) moved on or pins restarted (each run starts with higher ids). Filter by "
        + "kinds and min_severity. Side-effect free.";

    public override ToolAnnotations Annotations => ToolAnnotations.Query;

    protected override void DefineArguments(ToolSchema arguments) {
        arguments.Integer("since_id", "Return only events after this id (nextSinceId of the previous call). Omit for the newest events.", minimum: 0, maximum: int.MaxValue);
        arguments.String("kinds", "Comma-separated kinds: safety, device, sequence, image, camera, guider, mount. Omit for all.", maxLength: 100, pattern: "^[A-Za-z, ]+$");
        arguments.Enum("min_severity", "Lowest severity: info (default), warning, error or critical.", Severities);
        arguments.Integer("limit", "Maximum number of events (1-200). Default 50.", minimum: 1, maximum: 200, defaultValue: 50);
        arguments.WaitSeconds(
            0,
            "With since_id: seconds to wait for a new matching event before returning empty (0-55, default 0: return at once).");
    }

    protected override async Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, ToolContext context, CancellationToken cancellationToken) {
        var sinceId = arguments.GetInt("since_id");
        var limit = arguments.GetInt("limit") ?? 50;
        var wait = JobPayload.ReadWait(arguments, 0);

        var kinds = (arguments.GetString("kinds") ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var minimum = ArgumentSpec.NormalizeEnumValue(arguments.GetString("min_severity") ?? "info") switch {
            "critical" => EventSeverity.Critical,
            "error" => EventSeverity.Error,
            "warning" => EventSeverity.Warning,
            _ => EventSeverity.Info
        };

        bool Matches(ObservatoryEvent entry) {
            return entry.Severity >= minimum && (kinds.Count == 0 || kinds.Contains(entry.Kind));
        }

        if (sinceId is null && wait > TimeSpan.Zero) {
            throw new ToolArgumentException("wait_seconds needs since_id: call once without it, then wait with the returned nextSinceId.");
        }

        var page = sinceId is null
            ? events.Recent(limit, Matches)
            : await events.WaitAsync(sinceId.Value, limit, Matches, wait, cancellationToken).ConfigureAwait(false);

        var payload = new {
            count = page.Events.Count,
            events = page.Events.Select(entry => new {
                id = entry.Id,
                time = entry.Time,
                kind = entry.Kind,
                severity = entry.Severity,
                message = entry.Message,
                data = entry.Data
            }).ToList(),
            latestId = page.LatestId,
            nextSinceId = page.NextSinceId,
            more = page.More ? true : (bool?)null,
            missed = page.Missed ? true : (bool?)null,
            next = page.More
                ? "More events are waiting: call again with since_id = nextSinceId."
                : "Call again with since_id = nextSinceId (and wait_seconds to long-poll) for newer events."
        };

        return ToolCallResult.Json(payload);
    }
}
