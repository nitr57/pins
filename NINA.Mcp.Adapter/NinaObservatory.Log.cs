// SPDX-License-Identifier: MPL-2.0

using NINA.Core.Utility;
using NINA.Mcp.Logs;
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
    /// is the newest other one. The choice itself is <see cref="PinsLogFiles.Select"/>.
    /// </summary>
    public LogSource GetLogSource(bool previousRun) {
        try {
            var directory = Path.GetFullPath(Path.Combine(CoreUtil.APPLICATIONTEMPPATH, "Logs"));

            if (!Directory.Exists(directory)) {
                return new LogSource(null, $"The pins log folder {directory} does not exist.");
            }

            var files = new DirectoryInfo(directory)
                .GetFiles("*.log")
                .Select(file => new PinsLogFile(file.FullName, file.LastWriteTimeUtc));

            var selected = PinsLogFiles.Select(files, FindOpenLogFile(directory), Environment.ProcessId, previousRun).File;

            if (selected == null) {
                return new LogSource(
                    null,
                    previousRun
                        ? "There is no log file of an earlier pins run."
                        : "The log file of this pins run was not found.");
            }

            return new LogSource(selected.Path, selected.Name);
        } catch (Exception ex) {
            Logger.Error("MCP: locating the log file failed", ex);

            return new LogSource(null, $"Locating the log file failed: {ex.Message}");
        }
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
