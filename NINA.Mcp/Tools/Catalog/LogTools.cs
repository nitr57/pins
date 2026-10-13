// SPDX-License-Identifier: MPL-2.0

using NINA.Mcp.Logs;
using NINA.Mcp.Observatory;
using System.Globalization;

namespace NINA.Mcp.Tools.Catalog;

internal sealed class ReadLogTool : ObservatoryTool {

    private static readonly IReadOnlyList<string> Levels = ["trace", "debug", "info", "warning", "error"];

    /// <summary>Characters of message and detail text in one result: keeps a 500-entry answer within a model's reach.</summary>
    internal const int MaxResultCharacters = 120_000;

    public ReadLogTool(IPinsObservatory observatory) : base(observatory) {
    }

    public override string Name => "read_log";

    public override string Title => "Read pins log";

    public override string Description =>
        "The newest entries of the pins (N.I.N.A.) log, oldest first: local time, level, source file, member, line, message "
        + "and the exception text when there is one. Filter by min_level (default warning: warnings and errors only), by text "
        + "(contains, case-insensitive, matched against message, source, member and exception) and by age (since_minutes). "
        + "Use it to find out why a sequence item, slew, plate solve, autofocus or connection failed: start with the default, "
        + "then widen to info around the time of the failure. previous_run reads the log of the run before this one (after a "
        + "crash or restart). Trace and debug entries only exist when the log level in the pins settings is set that low. "
        + "The result says how much of the file was scanned. Side-effect free.";

    public override ToolAnnotations Annotations => ToolAnnotations.Query;

    protected override void DefineArguments(ToolSchema arguments) {
        arguments.Integer("lines", "Maximum number of entries to return, the newest matching ones (1-500). Default 100.", minimum: 1, maximum: 500, defaultValue: 100);
        arguments.Enum("min_level", "Lowest level to include: trace, debug, info, warning or error. Default warning.", Levels);
        arguments.String("contains", "Only entries containing this text (case-insensitive), e.g. \"Platesolve\", \"Sequence\" or \"Exception\".", maxLength: 200);
        arguments.Number("since_minutes", "Only entries from the last N minutes (0.1-10080).", minimum: 0.1, maximum: 10080);
        arguments.Boolean("previous_run", "Read the log of the previous pins run instead of the current one. Default false.", defaultValue: false);
    }

    protected override async Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, ToolContext context, CancellationToken cancellationToken) {
        var lines = arguments.GetInt("lines") ?? 100;
        var levelText = arguments.GetString("min_level") ?? "warning";
        var contains = arguments.GetString("contains")?.Trim();
        var sinceMinutes = arguments.GetDouble("since_minutes");
        var previousRun = arguments.GetBool("previous_run") ?? false;

        if (!PinsLogReader.TryParseLevel(levelText, out var minLevel)) {
            throw new ToolArgumentException($"Argument 'min_level' must be one of: {string.Join(", ", Levels)} (got \"{levelText}\").");
        }

        var source = Observatory.GetLogSource(previousRun);

        if (string.IsNullOrEmpty(source.FilePath)) {
            return ToolCallResult.Error(source.Message);
        }

        DateTime? since = sinceMinutes is null ? null : DateTime.Now - TimeSpan.FromMinutes(sinceMinutes.Value);
        var query = new PinsLogQuery(lines, minLevel, string.IsNullOrEmpty(contains) ? null : contains, since);

        PinsLogReadResult result;

        try {
            result = await Task.Run(() => PinsLogReader.ReadTail(source.FilePath, query, cancellationToken), cancellationToken).ConfigureAwait(false);
        } catch (IOException ex) {
            return ToolCallResult.Error($"The log file {Path.GetFileName(source.FilePath)} could not be read: {ex.Message}");
        } catch (UnauthorizedAccessException ex) {
            return ToolCallResult.Error($"The log file {Path.GetFileName(source.FilePath)} could not be read: {ex.Message}");
        }

        // Newest entries win when the text budget runs out.
        var kept = new List<PinsLogEntry>();
        var characters = 0;

        for (var i = result.Entries.Count - 1; i >= 0; i--) {
            var entry = result.Entries[i];

            characters += entry.Message.Length + (entry.Detail?.Length ?? 0);

            if (characters > MaxResultCharacters && kept.Count > 0) {
                break;
            }

            kept.Add(entry);
        }

        kept.Reverse();

        var droppedForSize = result.Entries.Count - kept.Count;

        var entries = kept
            .Select(entry => new {
                time = entry.Time,
                level = entry.Level,
                source = entry.Source,
                member = entry.Member,
                line = entry.Line,
                message = entry.Message,
                detail = entry.Detail
            })
            .ToList();

        var payload = new {
            file = Path.GetFileName(source.FilePath),
            count = entries.Count,
            entries,
            coverage = new {
                wholeFile = result.ReachedStart,
                reachedSince = result.ReachedSince,
                oldestScannedTime = result.OldestScannedTime,
                scannedBytes = result.ScannedBytes,
                fileBytes = result.FileBytes
            },
            droppedForSize = droppedForSize > 0 ? droppedForSize : (int?)null,
            next = droppedForSize > 0
                ? $"{droppedForSize} older matching entries were left out to keep the answer small: narrow with contains or since_minutes."
                : Next(result, entries.Count, lines)
        };

        return ToolCallResult.Json(payload);
    }

    private static string Next(PinsLogReadResult result, int count, int lines) {
        if (count >= lines) {
            return "More matching entries may exist before these: narrow with contains or since_minutes, or raise lines.";
        }

        if (!result.ReachedStart && !result.ReachedSince) {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"Only the last {result.ScannedBytes / (1024 * 1024)} MB of the file were scanned; older entries were not searched.");
        }

        return count == 0
            ? "No matching entries. Try a lower min_level or a different contains text."
            : "All matching entries in the scanned range are shown.";
    }
}
