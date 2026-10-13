// SPDX-License-Identifier: MPL-2.0

using NINA.Astrometry;
using NINA.Core.Utility;
using NINA.Mcp.Observatory;
using System.Diagnostics;
using System.Globalization;
using static System.FormattableString;

namespace NINA.Mcp.Adapter;

public sealed partial class NinaObservatory {

    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(2);

    // ----- Catalog -----

    /// <summary>The sky atlas query (DatabaseInteraction.GetDeepSkyObjects); the database keeps sizes in arcsec.</summary>
    public async Task<CatalogSearchResult> FindTargetsAsync(CatalogSearch search, CancellationToken cancellationToken) {
        try {
            var parameters = new DatabaseInteraction.DeepSkyObjectSearchParams {
                Constellation = search.Constellation ?? string.Empty,
                DsoTypes = search.Types.ToList(),
                ObjectName = search.Name ?? string.Empty,
                Limit = search.Limit
            };

            parameters.Magnitude.Thru = search.MaxMagnitude;
            parameters.Size.From = search.MinSizeArcmin * 60;
            parameters.Size.Thru = search.MaxSizeArcmin * 60;

            switch (search.Order) {
                case "magnitude":
                    parameters.SearchOrder.Field = "magnitude";
                    parameters.SearchOrder.Direction = "ASC";

                    // Without it, objects without a magnitude (NULL sorts first) would fill the list.
                    parameters.Magnitude.Thru ??= 99;
                    break;

                case "size":
                    parameters.SearchOrder.Field = "sizemax";
                    parameters.SearchOrder.Direction = "DESC";
                    break;

                default:
                    parameters.SearchOrder.Field = "id";
                    parameters.SearchOrder.Direction = "ASC";
                    break;
            }

            var database = new DatabaseInteraction();
            var horizon = profileService.ActiveProfile.AstrometrySettings.Horizon;
            var found = await database.GetDeepSkyObjects(string.Empty, horizon, parameters, cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();

            var objects = found
                .Select(dso => new CatalogObject(
                    string.IsNullOrWhiteSpace(dso.Name) ? dso.Id : dso.Name,
                    (dso.AlsoKnownAs ?? new List<string>()).Take(8).ToList(),
                    dso.DSOType,
                    dso.Constellation,
                    dso.Coordinates.RA,
                    dso.Coordinates.Dec,
                    dso.Magnitude,
                    dso.SurfaceBrightness,
                    dso.Size / 60,
                    dso.SizeMin / 60,
                    dso.PositionAngle == null ? null : Number(dso.PositionAngle.Degree)))
                .ToList();

            return new CatalogSearchResult(true, Invariant($"{objects.Count} object(s)."), objects);
        } catch (OperationCanceledException) {
            throw;
        } catch (Exception ex) {
            Logger.Error("MCP: catalog search failed", ex);

            return new CatalogSearchResult(false, $"The catalog search failed: {ex.Message}", []);
        }
    }

    // ----- System health -----

    public SystemHealth GetSystemHealth() {
        var imagePath = Try(() => profileService.ActiveProfile.ImageFileSettings.FilePath);

        return new SystemHealth(
            DateTimeOffset.Now,
            Environment.TickCount64 / 3_600_000.0,
            Try(ReadClockSynchronized),
            Try(ReadMountUtc),
            Try(() => ReadDisk(imagePath)),
            Try(() => ReadDisk(CoreUtil.APPLICATIONTEMPPATH)),
            Try(ReadCpuTemperature),
            Try(() => ThrottleState.Parse(RunCommand("vcgencmd", "get_throttled"))),
            Try(() => ReadMeminfo("MemAvailable:")),
            Try(() => ReadMeminfo("MemTotal:")),
            Try(ReadLoadAverage));
    }

    /// <summary>systemd-timesyncd marks a synchronized clock with this file; otherwise ask timedatectl (also covers chrony).</summary>
    private static bool? ReadClockSynchronized() {
        if (File.Exists("/run/systemd/timesync/synchronized")) {
            return true;
        }

        var output = RunCommand("timedatectl", "show --property=NTPSynchronized --value")?.Trim();

        return output switch {
            "yes" => true,
            "no" => false,
            _ => null
        };
    }

    private DateTimeOffset? ReadMountUtc() {
        var info = telescopeMediator.GetInfo();

        if (info?.Connected != true || info.UTCDate == default) {
            return null;
        }

        return new DateTimeOffset(DateTime.SpecifyKind(info.UTCDate, DateTimeKind.Utc));
    }

    /// <summary>The file system holding <paramref name="path"/>: the mounted drive with the longest matching root.</summary>
    private static DiskSpace? ReadDisk(string? path) {
        if (string.IsNullOrWhiteSpace(path)) {
            return null;
        }

        var full = Path.GetFullPath(path);

        var drive = DriveInfo.GetDrives()
            .Where(candidate => candidate.IsReady && full.StartsWith(candidate.RootDirectory.FullName, StringComparison.Ordinal))
            .OrderByDescending(candidate => candidate.RootDirectory.FullName.Length)
            .FirstOrDefault();

        if (drive == null) {
            return null;
        }

        return new DiskSpace(
            full,
            Math.Round(drive.AvailableFreeSpace / 1e9, 1),
            Math.Round(drive.TotalSize / 1e9, 1));
    }

    private static double? ReadCpuTemperature() {
        const string zone = "/sys/class/thermal/thermal_zone0/temp";

        if (!File.Exists(zone)) {
            return null;
        }

        return double.TryParse(File.ReadAllText(zone).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var milli)
            ? Math.Round(milli / 1000, 1)
            : null;
    }

    private static double? ReadMeminfo(string key) {
        const string meminfo = "/proc/meminfo";

        if (!File.Exists(meminfo)) {
            return null;
        }

        var line = File.ReadLines(meminfo).FirstOrDefault(candidate => candidate.StartsWith(key, StringComparison.Ordinal));
        var parts = line?.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        return parts is { Length: >= 2 } && double.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var kilobytes)
            ? Math.Round(kilobytes / 1024, 0)
            : null;
    }

    private static double? ReadLoadAverage() {
        const string loadavg = "/proc/loadavg";

        if (!File.Exists(loadavg)) {
            return null;
        }

        var first = File.ReadAllText(loadavg).Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();

        return double.TryParse(first, NumberStyles.Float, CultureInfo.InvariantCulture, out var load) ? load : null;
    }

    /// <summary>Standard output of a short command, or null when it is missing, fails or does not finish in time.</summary>
    private static string? RunCommand(string fileName, string arguments) {
        var start = new ProcessStartInfo(fileName, arguments) {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(start);

        if (process == null) {
            return null;
        }

        var output = process.StandardOutput.ReadToEndAsync();

        if (!process.WaitForExit(CommandTimeout)) {
            try {
                process.Kill(entireProcessTree: true);
            } catch (Exception) {
                // Already gone.
            }

            return null;
        }

        return process.ExitCode == 0 && output.Wait(CommandTimeout) ? output.Result : null;
    }

    /// <summary>Each health reading is optional: a missing file or command must not hide the others.</summary>
    private static T? Try<T>(Func<T?> read) {
        try {
            return read();
        } catch (Exception ex) {
            Logger.Debug($"MCP: a system health reading failed: {ex.Message}");
            return default;
        }
    }
}
