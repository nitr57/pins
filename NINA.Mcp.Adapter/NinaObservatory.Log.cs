// SPDX-License-Identifier: MPL-2.0

using NINA.Core.Utility;
using NINA.Mcp.Observatory;

namespace NINA.Mcp.Adapter;

public sealed partial class NinaObservatory {

    private const string OpenFileDescriptors = "/proc/self/fd";

    // ----- Log -----

    /// <summary>
    /// The log files live in <c>%LOCALAPPDATA%/NINA/Logs</c> (<c>~/.local/share/NINA/Logs</c> on Linux), named
    /// <c>{start yyyyMMdd-HHmmss}-{version}.{process id}-{roll suffix}.log</c> by <c>NINA.Core.Utility.Logger</c>. This run's
    /// file is the one the logger holds open (Serilog keeps it open, <c>shared: false</c>), found through
    /// <c>/proc/self/fd</c>, which also follows the logger's rename after a clock correction. On a Pi, systemd often gives
    /// pins the same process id on every boot, so the process id in the name is only the fallback. The previous run's file
    /// is the newest other one.
    /// </summary>
    public LogSource GetLogSource(bool previousRun) {
        try {
            var directory = Path.GetFullPath(Path.Combine(CoreUtil.APPLICATIONTEMPPATH, "Logs"));

            if (!Directory.Exists(directory)) {
                return new LogSource(null, $"The pins log folder {directory} does not exist.");
            }

            var files = new DirectoryInfo(directory)
                .GetFiles("*.log")
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .ToList();

            var marker = $".{Environment.ProcessId}-";
            var open = FindOpenLogFile(directory);

            // Only the newest segment of a run is open; earlier segments of this run (monthly or size roll) share the name
            // up to the process id: "{start}-{version}.{pid}-".
            var runPrefix = RunPrefix(open, marker);

            bool IsCurrentRun(FileInfo file) {
                return runPrefix != null
                    ? file.Name.StartsWith(runPrefix, StringComparison.Ordinal)
                    : file.Name.Contains(marker, StringComparison.Ordinal);
            }

            var selected = previousRun
                ? files.FirstOrDefault(file => !IsCurrentRun(file))
                : files.FirstOrDefault(IsCurrentRun);

            if (selected == null) {
                return new LogSource(
                    null,
                    previousRun
                        ? "There is no log file of an earlier pins run."
                        : "The log file of this pins run was not found.");
            }

            return new LogSource(selected.FullName, selected.Name);
        } catch (Exception ex) {
            Logger.Error("MCP: locating the log file failed", ex);

            return new LogSource(null, $"Locating the log file failed: {ex.Message}");
        }
    }

    private static string? RunPrefix(string? openFile, string marker) {
        if (openFile == null) {
            return null;
        }

        var name = Path.GetFileName(openFile);
        var index = name.IndexOf(marker, StringComparison.Ordinal);

        return index < 0 ? name : name[..(index + marker.Length)];
    }

    /// <summary>
    /// The newest .log file in <paramref name="directory"/> this process holds open; null when /proc is not available.
    /// Link targets are fully resolved paths (a symlinked ~/.local would never match the folder), so open files are
    /// matched by name: log names carry start time and process id and are unique in the folder.
    /// </summary>
    private static string? FindOpenLogFile(string directory) {
        if (!Directory.Exists(OpenFileDescriptors)) {
            return null;
        }

        string? newest = null;
        var newestTime = DateTime.MinValue;

        foreach (var descriptor in Directory.EnumerateFileSystemEntries(OpenFileDescriptors)) {
            string? target;

            try {
                target = new FileInfo(descriptor).LinkTarget;
            } catch (Exception) {
                // Descriptors come and go while enumerating.
                continue;
            }

            if (target == null || !target.EndsWith(".log", StringComparison.Ordinal)) {
                continue;
            }

            var candidate = Path.Combine(directory, Path.GetFileName(target));

            if (!File.Exists(candidate)) {
                continue;
            }

            var written = File.GetLastWriteTimeUtc(candidate);

            if (newest == null || written > newestTime) {
                newest = candidate;
                newestTime = written;
            }
        }

        return newest;
    }
}
