// SPDX-License-Identifier: MPL-2.0

using System.Globalization;
using System.Text;

namespace NINA.Mcp.Logs;

/// <summary>Levels of the pins (N.I.N.A.) log, in increasing severity.</summary>
public enum PinsLogLevel {
    Trace,
    Debug,
    Info,
    Warning,
    Error,
    Fatal
}

/// <summary>
/// One log entry. <see cref="Time"/> is the local time as written in the file (no offset). <see cref="Detail"/> holds the
/// continuation lines (exception and stack trace), null when there are none.
/// </summary>
public sealed record PinsLogEntry(
    string Time,
    PinsLogLevel Level,
    string? Source,
    string? Member,
    int? Line,
    string Message,
    string? Detail) {

    /// <summary>The parsed <see cref="Time"/> (kind Unspecified, i.e. the writer's local time); null when unparsable.</summary>
    public DateTime? ParsedTime { get; init; }
}

/// <summary>What to read: the newest <see cref="MaxEntries"/> entries matching the filters, scanning at most <see cref="MaxScanBytes"/>.</summary>
public sealed record PinsLogQuery(
    int MaxEntries,
    PinsLogLevel MinLevel = PinsLogLevel.Info,
    string? Contains = null,
    DateTime? Since = null,
    long MaxScanBytes = PinsLogReader.DefaultMaxScanBytes);

/// <summary>
/// Entries oldest first. <see cref="ReachedStart"/> is true when the whole file was scanned; otherwise the scan stopped
/// because enough entries were found, the byte budget was used up, or <see cref="PinsLogQuery.Since"/> was passed
/// (<see cref="ReachedSince"/>). <see cref="OldestScannedTime"/> is the time of the oldest entry looked at.
/// </summary>
public sealed record PinsLogReadResult(
    IReadOnlyList<PinsLogEntry> Entries,
    bool ReachedStart,
    bool ReachedSince,
    string? OldestScannedTime,
    long ScannedBytes,
    long FileBytes);

/// <summary>
/// Reads the end of a pins log file backwards, so that the last entries of a large night log are found without reading
/// the whole file. Line format (see <c>NINA.Core.Utility.Logger</c>):
/// <c>2026-10-13T22:59:01.1234|INFO|Source.cs|Member|123|message</c>, followed by continuation lines for exceptions.
/// Lines written without the source/member/line fields are accepted too. The file is opened with full sharing, so
/// the running logger keeps writing.
/// </summary>
public static class PinsLogReader {

    public const long DefaultMaxScanBytes = 32L * 1024 * 1024;

    public const int MaxMessageLength = 2000;

    public const int MaxDetailLength = 4000;

    private const int ChunkSize = 64 * 1024;

    private const int MaxDetailLines = 60;

    private static readonly string[] TimeFormats = [
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF",
        "yyyy-MM-dd'T'HH:mm:ss"
    ];

    /// <summary>Reads the newest matching entries of <paramref name="path"/>.</summary>
    /// <exception cref="IOException">The file cannot be opened or read.</exception>
    public static PinsLogReadResult ReadTail(string path, PinsLogQuery query, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(query);

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        return ReadTail(stream, query, cancellationToken);
    }

    /// <summary>Reads the newest matching entries of a seekable stream (for tests and other sources).</summary>
    public static PinsLogReadResult ReadTail(Stream stream, PinsLogQuery query, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(query);

        var collector = new Collector(query);
        var length = stream.Length;
        var position = length;
        var budget = Math.Max(ChunkSize, query.MaxScanBytes);
        var pending = Array.Empty<byte>();
        var buffer = new byte[ChunkSize];

        while (position > 0 && !collector.Done && length - position < budget) {
            cancellationToken.ThrowIfCancellationRequested();

            var count = (int)Math.Min(ChunkSize, position);
            position -= count;

            stream.Seek(position, SeekOrigin.Begin);
            stream.ReadExactly(buffer, 0, count);

            var data = new byte[count + pending.Length];
            Buffer.BlockCopy(buffer, 0, data, 0, count);
            Buffer.BlockCopy(pending, 0, data, count, pending.Length);

            var end = data.Length;

            for (var i = data.Length - 1; i >= 0 && !collector.Done; i--) {
                if (data[i] != (byte)'\n') {
                    continue;
                }

                collector.AddLine(Decode(data, i + 1, end - i - 1));
                end = i;
            }

            pending = data[..end];
        }

        // The first line of the file has no newline before it.
        var reachedStart = position == 0 && !collector.Done;

        if (reachedStart && pending.Length > 0) {
            collector.AddLine(Decode(pending, 0, pending.Length));
        }

        return new PinsLogReadResult(
            collector.Results(),
            reachedStart && !collector.Done,
            collector.ReachedSince,
            collector.OldestTime,
            length - position,
            length);
    }

    /// <summary>Parses a header line (one that starts an entry); false for continuation and file header lines.</summary>
    public static bool TryParseLine(string line, out PinsLogEntry entry) {
        entry = null!;

        if (line.Length < 20 || !char.IsDigit(line[0])) {
            return false;
        }

        var parts = line.Split('|', 6);

        if (parts.Length < 3) {
            return false;
        }

        if (!DateTime.TryParseExact(parts[0], TimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)) {
            return false;
        }

        if (!TryParseLevel(parts[1], out var level)) {
            return false;
        }

        string? source = null;
        string? member = null;
        int? lineNumber = null;
        string message;

        if (parts.Length == 6 && int.TryParse(parts[4], NumberStyles.None, CultureInfo.InvariantCulture, out var number)) {
            source = parts[2];
            member = parts[3];
            lineNumber = number;
            message = parts[5];
        } else {
            message = string.Join('|', parts.Skip(2));
        }

        entry = new PinsLogEntry(parts[0], level, source, member, lineNumber, Truncate(message, MaxMessageLength), null) {
            ParsedTime = time
        };

        return true;
    }

    /// <summary>Accepts the file's names (TRACE, DEBUG, INFO, WARNING, ERROR, FATAL) and the enum names, case-insensitive.</summary>
    public static bool TryParseLevel(string? text, out PinsLogLevel level) {
        level = PinsLogLevel.Info;

        switch (text?.Trim().ToUpperInvariant()) {
            case "TRACE":
            case "VERBOSE":
                level = PinsLogLevel.Trace;
                return true;

            case "DEBUG":
                level = PinsLogLevel.Debug;
                return true;

            case "INFO":
            case "INFORMATION":
                level = PinsLogLevel.Info;
                return true;

            case "WARNING":
            case "WARN":
                level = PinsLogLevel.Warning;
                return true;

            case "ERROR":
                level = PinsLogLevel.Error;
                return true;

            case "FATAL":
                level = PinsLogLevel.Fatal;
                return true;

            default:
                return false;
        }
    }

    private static string Decode(byte[] data, int offset, int count) {
        var text = Encoding.UTF8.GetString(data, offset, count);

        return text.EndsWith('\r') ? text[..^1] : text;
    }

    private static string Truncate(string text, int maxLength) {
        return text.Length <= maxLength ? text : string.Concat(text.AsSpan(0, maxLength), " …");
    }

    /// <summary>Receives lines newest first and assembles entries (continuation lines come before their header).</summary>
    private sealed class Collector {

        private readonly PinsLogQuery query;
        private readonly List<PinsLogEntry> results = [];
        private readonly List<string> continuation = [];

        public Collector(PinsLogQuery query) {
            this.query = query;
        }

        public bool Done { get; private set; }

        public bool ReachedSince { get; private set; }

        public string? OldestTime { get; private set; }

        public void AddLine(string line) {
            if (Done) {
                return;
            }

            if (!TryParseLine(line, out var entry)) {
                if (line.Length > 0 && continuation.Count < MaxDetailLines) {
                    continuation.Add(line);
                }

                return;
            }

            var detail = TakeDetail();

            OldestTime = entry.Time;

            if (query.Since is not null && entry.ParsedTime is not null && entry.ParsedTime < query.Since) {
                ReachedSince = true;
                Done = true;

                return;
            }

            entry = entry with { Detail = detail };

            if (!Matches(entry)) {
                return;
            }

            results.Add(entry);

            if (results.Count >= Math.Max(1, query.MaxEntries)) {
                Done = true;
            }
        }

        public IReadOnlyList<PinsLogEntry> Results() {
            var oldestFirst = results.ToList();

            oldestFirst.Reverse();

            return oldestFirst;
        }

        private string? TakeDetail() {
            if (continuation.Count == 0) {
                return null;
            }

            // Collected newest first.
            continuation.Reverse();

            var detail = Truncate(string.Join('\n', continuation), MaxDetailLength);

            continuation.Clear();

            return detail;
        }

        private bool Matches(PinsLogEntry entry) {
            if (entry.Level < query.MinLevel) {
                return false;
            }

            if (string.IsNullOrWhiteSpace(query.Contains)) {
                return true;
            }

            var text = query.Contains.Trim();

            return Contains(entry.Message, text)
                || Contains(entry.Source, text)
                || Contains(entry.Member, text)
                || Contains(entry.Detail, text);
        }

        private static bool Contains(string? value, string text) {
            return value is not null && value.Contains(text, StringComparison.OrdinalIgnoreCase);
        }
    }
}
