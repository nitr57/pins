// SPDX-License-Identifier: MPL-2.0

using NINA.Core.Utility;
using NINA.Mcp.Observatory;

namespace NINA.Mcp.Adapter;

public sealed partial class NinaObservatory {

    // ----- Log -----

    /// <summary>
    /// The log files live in <c>%LOCALAPPDATA%/NINA/Logs</c> (<c>~/.local/share/NINA/Logs</c> on Linux) and are named
    /// <c>{start yyyyMMdd-HHmmss}-{version}.{process id}-{roll suffix}.log</c> by <c>NINA.Core.Utility.Logger</c>; the
    /// start time part may be renamed after a clock correction, the process id part stays. This run's file is the newest
    /// one with this process id, the previous run's the newest one without it.
    /// </summary>
    public LogSource GetLogSource(bool previousRun) {
        try {
            var directory = Path.Combine(CoreUtil.APPLICATIONTEMPPATH, "Logs");

            if (!Directory.Exists(directory)) {
                return new LogSource(null, $"The pins log folder {directory} does not exist.");
            }

            var marker = $".{Environment.ProcessId}-";

            var files = new DirectoryInfo(directory)
                .GetFiles("*.log")
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .ToList();

            var file = previousRun
                ? files.FirstOrDefault(candidate => !candidate.Name.Contains(marker, StringComparison.Ordinal))
                : files.FirstOrDefault(candidate => candidate.Name.Contains(marker, StringComparison.Ordinal));

            if (file == null) {
                return new LogSource(
                    null,
                    previousRun
                        ? "There is no log file of an earlier pins run."
                        : "The log file of this pins run was not found.");
            }

            return new LogSource(file.FullName, file.Name);
        } catch (Exception ex) {
            Logger.Error("MCP: locating the log file failed", ex);

            return new LogSource(null, $"Locating the log file failed: {ex.Message}");
        }
    }
}
