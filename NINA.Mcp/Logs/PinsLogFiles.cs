// SPDX-License-Identifier: MPL-2.0

namespace NINA.Mcp.Logs;

/// <summary>A log file in the pins log folder.</summary>
public sealed record PinsLogFile(string Path, DateTime LastWriteTimeUtc) {

    public string Name => System.IO.Path.GetFileName(Path);
}

/// <summary>The log file chosen for read_log; <see cref="File"/> is null when there is none.</summary>
public sealed record PinsLogSelection(PinsLogFile? File);

/// <summary>
/// Chooses the log file of this pins run or of the previous one. Log files are named
/// <c>{start yyyyMMdd-HHmmss}-{version}.{process id}-{roll suffix}.log</c> by <c>NINA.Core.Utility.Logger</c>; a run
/// can have several segments (monthly or size roll) that share the name up to the process id. The file the logger holds
/// open identifies this run; the process id is only the fallback, because systemd often gives pins the same one on every
/// boot.
/// </summary>
public static class PinsLogFiles {

    /// <param name="files">The .log files of the folder.</param>
    /// <param name="openFileName">Name of the newest log file this process holds open; null when unknown.</param>
    /// <param name="processId">This process's id.</param>
    /// <param name="previousRun">Choose the newest file of an earlier run instead of this one.</param>
    public static PinsLogSelection Select(IEnumerable<PinsLogFile> files, string? openFileName, int processId, bool previousRun) {
        ArgumentNullException.ThrowIfNull(files);

        var marker = $".{processId}-";
        var runPrefix = RunPrefix(openFileName, marker);

        bool IsCurrentRun(PinsLogFile file) {
            return runPrefix != null
                ? file.Name.StartsWith(runPrefix, StringComparison.Ordinal)
                : file.Name.Contains(marker, StringComparison.Ordinal);
        }

        var newestFirst = files.OrderByDescending(file => file.LastWriteTimeUtc).ToList();

        var selected = previousRun
            ? newestFirst.FirstOrDefault(file => !IsCurrentRun(file))
            : newestFirst.FirstOrDefault(IsCurrentRun);

        return new PinsLogSelection(selected);
    }

    /// <summary>"{start}-{version}.{pid}-" of the open file; the whole name when it has no process id marker.</summary>
    private static string? RunPrefix(string? openFileName, string marker) {
        if (openFileName == null) {
            return null;
        }

        var name = System.IO.Path.GetFileName(openFileName);
        var index = name.IndexOf(marker, StringComparison.Ordinal);

        return index < 0 ? name : name[..(index + marker.Length)];
    }
}
