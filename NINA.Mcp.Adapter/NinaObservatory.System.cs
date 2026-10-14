// SPDX-License-Identifier: MPL-2.0

using NINA.Astrometry;
using NINA.Core.Utility;
using NINA.Mcp.Observatory;
using NINA.Mcp.Tools;
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
            // GetDeepSkyObjects swallows query errors and returns an empty list; a missing database must not look like "no match".
            var databaseFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NINA", "NINA.sqlite");

            if (!File.Exists(databaseFile)) {
                return new CatalogSearchResult(false, $"The catalog database {databaseFile} does not exist.", []);
            }

            var parameters = new DatabaseInteraction.DeepSkyObjectSearchParams {
                Constellation = search.Constellation ?? string.Empty,
                DsoTypes = search.Types.ToList(),
                ObjectName = search.Name ?? string.Empty,
                // A name matches by substring and the query truncates before any ranking: fetch more, rank, then truncate.
                Limit = string.IsNullOrWhiteSpace(search.Name) ? search.Limit : Math.Max(search.Limit, CatalogRanking.NameSearchFetchLimit)
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

            IEnumerable<CatalogObject> objects = found
                .Select(dso => (Dso: dso, Name: string.IsNullOrWhiteSpace(dso.Name) ? dso.Id : dso.Name))
                .Select(item => (item.Dso, item.Name, Aliases: (item.Dso.AlsoKnownAs ?? new List<string>())
                    .Where(alias => !string.Equals(alias, item.Name, StringComparison.OrdinalIgnoreCase))
                    .ToList()))
                .Select(item => new CatalogObject(
                    item.Name,
                    item.Aliases,
                    item.Dso.DSOType,
                    item.Dso.Constellation,
                    item.Dso.Coordinates.RA,
                    item.Dso.Coordinates.Dec,
                    item.Dso.Magnitude,
                    item.Dso.SurfaceBrightness,
                    item.Dso.Size / 60,
                    item.Dso.SizeMin / 60,
                    item.Dso.PositionAngle == null ? null : Number(item.Dso.PositionAngle.Degree)));

            // The query sorts by catalog id; "name" means the displayed name.
            if (search.Order == "name") {
                objects = objects.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase);
            }

            // Ranked on every alias, so the named object comes first; only then truncated and the aliases trimmed.
            var ranked = string.IsNullOrWhiteSpace(search.Name) ? objects.ToList() : CatalogRanking.Rank(objects, search.Name);
            var result = ranked
                .Take(search.Limit)
                .Select(item => item with { Aliases = item.Aliases.Take(8).ToList() })
                .ToList();

            var message = result.Count == 0
                ? "No match. If one was expected, read_log may show a catalog error."
                : Invariant($"{result.Count} object(s).");

            return new CatalogSearchResult(true, message, result);
        } catch (OperationCanceledException) {
            throw;
        } catch (Exception ex) {
            Logger.Error("MCP: catalog search failed", ex);

            return new CatalogSearchResult(false, $"The catalog search failed: {ex.Message}", []);
        }
    }

    // ----- System health -----

    public async Task<SystemHealth> GetSystemHealthAsync(CancellationToken cancellationToken) {
        var imagePath = Try(() => profileService.ActiveProfile.ImageFileSettings.FilePath);

        // One budget for every external command; they run at the same time and never block a request thread.
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(CommandTimeout);

        var clockSynchronized = TryAsync(() => ReadClockSynchronizedAsync(budget.Token));
        var throttling = TryAsync(async () => ThrottleState.Parse(await RunCommandAsync("vcgencmd", "get_throttled", budget.Token)));
        var imageDisk = ReadDiskAsync(imagePath, budget.Token);
        var appDisk = ReadDiskAsync(CoreUtil.APPLICATIONTEMPPATH, budget.Token);

        return new SystemHealth(
            DateTimeOffset.Now,
            Environment.TickCount64 / 3_600_000.0,
            await clockSynchronized,
            Try(ReadMountUtc),
            await imageDisk,
            await appDisk,
            Try(ReadCpuTemperature),
            await throttling,
            Try(() => ReadMeminfo("MemAvailable:")),
            Try(() => ReadMeminfo("MemTotal:")),
            Try(ReadLoadAverage));
    }

    /// <summary>systemd-timesyncd marks a synchronized clock with this file; otherwise ask timedatectl (also covers chrony).</summary>
    private static async Task<bool?> ReadClockSynchronizedAsync(CancellationToken cancellationToken) {
        if (File.Exists("/run/systemd/timesync/synchronized")) {
            return true;
        }

        var output = (await RunCommandAsync("timedatectl", "show --property=NTPSynchronized --value", cancellationToken))?.Trim();

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

    /// <summary>
    /// <see cref="ReadDisk"/> on the thread pool within the budget: the stat of a stale network share can hang, and the
    /// health answer must not wait for it (the reading is then null; the blocked thread is left to finish).
    /// </summary>
    private static async Task<DiskSpace?> ReadDiskAsync(string? path, CancellationToken cancellationToken) {
        try {
            return await Task.Run(() => ReadDisk(path)).WaitAsync(cancellationToken);
        } catch (OperationCanceledException) {
            Logger.Debug($"MCP: reading the free space of {path} did not finish in time");
            return null;
        } catch (Exception ex) {
            Logger.Debug($"MCP: a system health reading failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>The file system holding <paramref name="path"/>: the mounted drive with the longest matching root.</summary>
    private static DiskSpace? ReadDisk(string? path) {
        if (string.IsNullOrWhiteSpace(path)) {
            return null;
        }

        var full = Path.GetFullPath(path);

        // IsReady stats the mount point, which blocks on a stale network share: only check the one that holds the path.
        // A mount that is not ready is not replaced by its parent, whose free space would be reported as the path's.
        var drive = DriveInfo.GetDrives()
            .Where(candidate => IsUnder(full, candidate.RootDirectory.FullName))
            .OrderByDescending(candidate => candidate.RootDirectory.FullName.Length)
            .FirstOrDefault();

        if (drive == null || !drive.IsReady) {
            return null;
        }

        return new DiskSpace(
            full,
            Math.Round(drive.AvailableFreeSpace / 1e9, 1),
            Math.Round(drive.TotalSize / 1e9, 1),
            Directory.Exists(full));
    }

    /// <summary>True when <paramref name="path"/> is <paramref name="root"/> or inside it (/mnt/data does not contain /mnt/data2).</summary>
    private static bool IsUnder(string path, string root) {
        if (string.Equals(path, root, StringComparison.Ordinal)) {
            return true;
        }

        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;

        return path.StartsWith(prefix, StringComparison.Ordinal);
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

    /// <summary>Standard output of a short command, or null when it is missing, fails or does not finish before the token.</summary>
    private static async Task<string?> RunCommandAsync(string fileName, string arguments, CancellationToken cancellationToken) {
        var start = new ProcessStartInfo(fileName, arguments) {
            RedirectStandardOutput = true,
            RedirectStandardError = false,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(start);

        if (process == null) {
            return null;
        }

        try {
            var output = process.StandardOutput.ReadToEndAsync(cancellationToken);

            await process.WaitForExitAsync(cancellationToken);

            return process.ExitCode == 0 ? await output : null;
        } catch (OperationCanceledException) {
            try {
                process.Kill(entireProcessTree: true);
            } catch (Exception) {
                // Already gone.
            }

            return null;
        }
    }

    /// <summary>Like <see cref="Try{T}"/> for the readings that run a command.</summary>
    private static async Task<T?> TryAsync<T>(Func<Task<T?>> read) {
        try {
            return await read();
        } catch (Exception ex) {
            Logger.Debug($"MCP: a system health reading failed: {ex.Message}");
            return default;
        }
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
