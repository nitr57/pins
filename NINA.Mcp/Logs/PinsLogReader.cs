// SPDX-License-Identifier: MPL-2.0

using System.Buffers;
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

    /// <summary>
    /// Longest line kept while reading backwards; the start of a longer line is kept (its header and the first part of the
    /// message), so a huge line costs bounded copying instead of being re-copied with every chunk.
    /// </summary>
    private const int MaxLineBytes = 64 * 1024;

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
        var budget = Math.Max(ChunkSize, query.MaxScanBytes);

        // One buffer for the whole scan: each chunk is read in front of the part of a line carried over from the chunk
        // after it, which stays at the start of the buffer and is moved behind the chunk (at most MaxLineBytes of it).
        var data = ArrayPool<byte>.Shared.Rent(ChunkSize + MaxLineBytes);

        try {
            return Scan(stream, collector, data, length, budget, cancellationToken);
        } finally {
            ArrayPool<byte>.Shared.Return(data);
        }
    }

    private static PinsLogReadResult Scan(Stream stream, Collector collector, byte[] data, long length, long budget, CancellationToken cancellationToken) {
        var position = length;
        var pending = 0;

        while (position > 0 && !collector.Done && length - position < budget) {
            cancellationToken.ThrowIfCancellationRequested();

            var remaining = budget - (length - position);
            var count = (int)Math.Min(ChunkSize, Math.Min(position, remaining));
            position -= count;

            // Overlapping when the carried-over part is longer than the chunk; BlockCopy handles that.
            Buffer.BlockCopy(data, 0, data, count, pending);

            stream.Seek(position, SeekOrigin.Begin);
            stream.ReadExactly(data, 0, count);

            var filled = count + pending;
            var end = filled;

            while (!collector.Done) {
                var newline = data.AsSpan(0, end).LastIndexOf((byte)'\n');

                if (newline < 0) {
                    break;
                }

                collector.AddLine(data, newline + 1, end - newline - 1);
                end = newline;
            }

            // The bytes before the first newline belong to a line that starts in an earlier chunk.
            pending = Math.Min(end, MaxLineBytes);
        }

        // The first line of the file has no newline before it.
        var reachedStart = position == 0 && !collector.Done;

        if (reachedStart && pending > 0) {
            collector.AddLine(data, 0, pending);
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

        // Logger.Error(Exception) writes "time|ERROR|source|member|line" without a message; the exception follows.
        if (parts.Length >= 5 && int.TryParse(parts[4], NumberStyles.None, CultureInfo.InvariantCulture, out var number)) {
            source = parts[2];
            member = parts[3];
            lineNumber = number;
            message = parts.Length == 6 ? parts[5] : string.Empty;
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

    /// <summary>
    /// Reads time and level of a header line as <see cref="TryParseLine"/> would accept them (first character a digit, at
    /// least three '|' fields, a time in one of <see cref="TimeFormats"/>, a known level), without decoding the line.
    /// False when that is not certain; such a line is left to the full parse.
    /// </summary>
    private static bool TryReadHeaderFields(ReadOnlySpan<byte> line, out int timeLength, out DateTime time, out PinsLogLevel level) {
        timeLength = 0;
        time = default;
        level = PinsLogLevel.Info;

        if (line.Length < 20 || line[0] < (byte)'0' || line[0] > (byte)'9') {
            return false;
        }

        var first = line.IndexOf((byte)'|');

        if (first < 0 || first > 27) {
            return false;
        }

        var second = line[(first + 1)..].IndexOf((byte)'|');

        if (second < 0) {
            return false;
        }

        timeLength = first;

        return TryParseTime(line[..first], out time) && TryParseLevel(line.Slice(first + 1, second), out level);
    }

    /// <summary>
    /// "yyyy-MM-ddTHH:mm:ss" with an optional fraction of 1-7 digits, with the calendar checks DateTime.TryParseExact makes;
    /// anything else is left to TryParseExact.
    /// </summary>
    private static bool TryParseTime(ReadOnlySpan<byte> text, out DateTime time) {
        time = default;

        if (text.Length != 19 && (text.Length < 21 || text.Length > 27 || text[19] != (byte)'.')) {
            return false;
        }

        if (text[4] != (byte)'-' || text[7] != (byte)'-' || text[10] != (byte)'T' || text[13] != (byte)':' || text[16] != (byte)':') {
            return false;
        }

        if (!TryDigits(text, 0, 4, out var year) || !TryDigits(text, 5, 2, out var month) || !TryDigits(text, 8, 2, out var day)
            || !TryDigits(text, 11, 2, out var hour) || !TryDigits(text, 14, 2, out var minute) || !TryDigits(text, 17, 2, out var second)) {
            return false;
        }

        if (year < 1 || month < 1 || month > 12 || day < 1 || day > DateTime.DaysInMonth(year, month) || hour > 23 || minute > 59 || second > 59) {
            return false;
        }

        long ticks = 0;

        if (text.Length > 19) {
            var digits = text.Length - 20;

            if (!TryDigits(text, 20, digits, out var fraction)) {
                return false;
            }

            ticks = fraction;

            for (var i = digits; i < 7; i++) {
                ticks *= 10;
            }
        }

        time = new DateTime(year, month, day, hour, minute, second).AddTicks(ticks);

        return true;
    }

    private static bool TryDigits(ReadOnlySpan<byte> text, int start, int length, out int value) {
        value = 0;

        for (var i = start; i < start + length; i++) {
            var digit = text[i] - (byte)'0';

            if (digit < 0 || digit > 9) {
                return false;
            }

            value = value * 10 + digit;
        }

        return true;
    }

    /// <summary>The levels <see cref="TryParseLevel(string?, out PinsLogLevel)"/> accepts, as written by the logger (upper case, no blanks).</summary>
    private static bool TryParseLevel(ReadOnlySpan<byte> text, out PinsLogLevel level) {
        level = PinsLogLevel.Info;

        if (text.SequenceEqual("INFO"u8)) {
            level = PinsLogLevel.Info;
        } else if (text.SequenceEqual("DEBUG"u8)) {
            level = PinsLogLevel.Debug;
        } else if (text.SequenceEqual("WARNING"u8)) {
            level = PinsLogLevel.Warning;
        } else if (text.SequenceEqual("ERROR"u8)) {
            level = PinsLogLevel.Error;
        } else if (text.SequenceEqual("TRACE"u8)) {
            level = PinsLogLevel.Trace;
        } else if (text.SequenceEqual("FATAL"u8)) {
            level = PinsLogLevel.Fatal;
        } else {
            // Other spellings and aliases go to the full parse.
            return false;
        }

        return true;
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
        private readonly char[] oldestTimeBuffer = new char[32];

        // The trimmed contains text when it can be checked line by line; null without one, and for a text that only the
        // joined detail (newlines) or a truncated message (" …") could match.
        private readonly string? containsText;

        private char[] decoded = new char[1024];
        private string? oldestTime;
        private int oldestTimeLength = -1;

        public Collector(PinsLogQuery query) {
            this.query = query;

            var text = string.IsNullOrWhiteSpace(query.Contains) ? null : query.Contains.Trim();

            containsText = text is null || text.Contains('\n') || text.Contains('…') ? null : text;
        }

        public bool Done { get; private set; }

        public bool ReachedSince { get; private set; }

        public string? OldestTime => oldestTimeLength >= 0 ? new string(oldestTimeBuffer, 0, oldestTimeLength) : oldestTime;

        /// <summary>
        /// A line as read. A header line whose entry can't match (level below the minimum, or the text neither in the line
        /// nor in its continuation lines) is dropped without being decoded, split and parsed in full; its time is still
        /// read for <see cref="PinsLogQuery.Since"/> and <see cref="OldestTime"/>. Every other line goes through
        /// <see cref="AddLine(string)"/>, so the result is the same as parsing every line.
        /// </summary>
        public void AddLine(byte[] data, int offset, int count) {
            if (Done) {
                return;
            }

            var line = data.AsSpan(offset, count);

            if (!TryReadHeaderFields(line, out var timeLength, out var time, out var level)) {
                AddLine(Decode(data, offset, count));

                return;
            }

            // No contains text that can be checked line by line: only the level decides here.
            if (level >= query.MinLevel && (containsText is null || MayContain(line, containsText))) {
                AddLine(Decode(data, offset, count));

                return;
            }

            // As AddLine(string) does for an entry that doesn't match: its continuation lines go with it.
            continuation.Clear();
            SetOldestTime(line[..timeLength]);

            if (query.Since is not null && time < query.Since) {
                ReachedSince = true;
                Done = true;
            }
        }

        // The decoded line contains the text, or one of the continuation lines collected for it does (Matches looks at
        // the message, source, member and detail, which are all parts of these). Decoded into a reused buffer.
        private bool MayContain(ReadOnlySpan<byte> line, string text) {
            var needed = Encoding.UTF8.GetMaxCharCount(line.Length);

            if (decoded.Length < needed) {
                decoded = new char[Math.Max(needed, decoded.Length * 2)];
            }

            var chars = Encoding.UTF8.GetChars(line, decoded);

            if (decoded.AsSpan(0, chars).Contains(text, StringComparison.OrdinalIgnoreCase)) {
                return true;
            }

            foreach (var part in continuation) {
                if (part.Contains(text, StringComparison.OrdinalIgnoreCase)) {
                    return true;
                }
            }

            return false;
        }

        private void SetOldestTime(ReadOnlySpan<byte> timeBytes) {
            // ASCII (checked by TryReadHeaderFields), at most 27 characters.
            oldestTimeLength = Encoding.ASCII.GetChars(timeBytes, oldestTimeBuffer);
            oldestTime = null;
        }

        public void AddLine(string line) {
            if (Done) {
                return;
            }

            if (!TryParseLine(line, out var entry)) {
                if (line.Length > 0) {
                    // Collected newest first: drop the bottom of a long stack trace, keep its top (the exception message).
                    if (continuation.Count == MaxDetailLines) {
                        continuation.RemoveAt(0);
                    }

                    continuation.Add(line);
                }

                return;
            }

            var detail = TakeDetail();

            oldestTime = entry.Time;
            oldestTimeLength = -1;

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
