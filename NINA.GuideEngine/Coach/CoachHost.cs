// SPDX-License-Identifier: MPL-2.0

using System.Text.Json;
using NINA.GuideEngine.Core;
using NINA.GuideEngine.Guiding;

namespace NINA.GuideEngine.Coach;

/// <summary>What the Guiding Coach needs from its host (the PINS plugin). Members with defaults are optional.</summary>
public interface ICoachHost
{
    /// <summary>Imaging camera scale (arcsec/px) from the profile; null when unknown (the grade then uses arcsec).</summary>
    double? ImagingScale { get; }

    /// <summary>Profile name for the report.</summary>
    string? ProfileName { get; }

    /// <summary>Current value of a plugin setting (invariant culture) for <see cref="CoachSettingChange.CurrentValue"/>; null = derive from the engine settings.</summary>
    string? GetSettingValue(string name);

    /// <summary>Applies and persists setting changes (ApplyCoachActions); false with an error when rejected.</summary>
    bool ApplySettings(IReadOnlyList<CoachSettingChange> changes, out string? error);

    /// <summary>Stores a completed report (same <see cref="CoachReport.Id"/> = replace, e.g. after applying actions).</summary>
    void SaveReport(CoachReport report);

    /// <summary>Stored reports of the active profile, newest first.</summary>
    IReadOnlyList<CoachReport> LoadReports(int max);

    /// <summary>Declination override (degrees); null = from the mount state.</summary>
    double? DeclinationDeg => null;

    /// <summary>Pier side override; null = from the mount state.</summary>
    PierSide? PierSide => null;

    /// <summary>Camera gain range; null = the camera's <see cref="IGainRange"/> if it implements it.</summary>
    IGainRange? GainRange => null;

    /// <summary>Settle criteria for trials (the profile's tolerance); the timeout is capped at 30 s. Null = 1.5 px, 5 s.</summary>
    SettleParams? TrialSettle => null;

    /// <summary>True while the host is busy with something that excludes a session (e.g. building darks).</summary>
    bool IsBusy => false;
}

/// <summary>Stores reports as JSON files in a directory (one file per report).</summary>
public sealed class FileCoachReportStore(string directory)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public string Directory { get; } = directory;

    public void Save(CoachReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        System.IO.Directory.CreateDirectory(Directory);
        string path = Path.Combine(Directory, FileName(report.Id));
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(report, Json));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Reports newest first, without the raw drift samples.</summary>
    public IReadOnlyList<CoachReport> Load(int max)
    {
        if (!System.IO.Directory.Exists(Directory) || max <= 0)
        {
            return [];
        }

        var reports = new List<CoachReport>();
        foreach (var file in System.IO.Directory.EnumerateFiles(Directory, "coach-*.json"))
        {
            try
            {
                var r = JsonSerializer.Deserialize<CoachReport>(File.ReadAllText(file), Json);
                if (r is not null)
                {
                    reports.Add(Normalize(r));
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or NotSupportedException)
            {
                // skip unreadable reports
            }
        }

        return reports.OrderByDescending(r => r.Timestamp).Take(max).ToList();
    }

    /// <summary>Drops the raw samples and turns JSON parameter values back into double/string/bool.</summary>
    public static CoachReport Normalize(CoachReport r) => r with
    {
        Drift = r.Drift is null ? null : r.Drift with { Samples = [] },
        Findings = r.Findings.Select(NormalizeFinding).ToList(),
    };

    private static CoachFinding NormalizeFinding(CoachFinding f) => f with
    {
        Parameters = f.Parameters.ToDictionary(kv => kv.Key, kv => kv.Value is JsonElement e ? FromJson(e) : kv.Value),
    };

    private static object? FromJson(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Number => e.GetDouble(),
        JsonValueKind.String => e.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };

    private static string FileName(string id) => "coach-" + string.Concat(id.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_')) + ".json";
}

/// <summary>
/// Default host working directly on a <see cref="Guider"/> (tests, standalone use): applied changes go to the guider's
/// host settings, reports are kept in memory or in a <see cref="FileCoachReportStore"/>.
/// </summary>
public sealed class SimpleCoachHost(Guider guider, string? reportDirectory = null) : ICoachHost
{
    private readonly object gate = new();
    private readonly List<CoachReport> reports = [];
    private readonly FileCoachReportStore? store = reportDirectory is null ? null : new FileCoachReportStore(reportDirectory);

    public double? ImagingScale { get; set; }

    public string? ProfileName { get; set; }

    public SettleParams? TrialSettle { get; set; }

    public string? GetSettingValue(string name) => CoachSettingsMap.GetValue(guider.BaseSettings, name);

    public bool ApplySettings(IReadOnlyList<CoachSettingChange> changes, out string? error)
    {
        if (!CoachSettingsMap.TryApply(guider.BaseSettings, changes, out var applied, out error))
        {
            return false;
        }

        guider.UpdateSettings(applied);
        return true;
    }

    public void SaveReport(CoachReport report)
    {
        if (store is not null)
        {
            store.Save(report);
            return;
        }

        lock (gate)
        {
            reports.RemoveAll(r => r.Id == report.Id);
            reports.Add(report);
        }
    }

    /// <summary>Reports of the current <see cref="ProfileName"/> (all when it is null), newest first.</summary>
    public IReadOnlyList<CoachReport> LoadReports(int max)
    {
        IEnumerable<CoachReport> all;
        if (store is not null)
        {
            all = store.Load(int.MaxValue);
        }
        else
        {
            lock (gate)
            {
                all = reports.Select(FileCoachReportStore.Normalize).ToList();
            }
        }

        return all.Where(r => ProfileName is null || r.ProfileName == ProfileName).OrderByDescending(r => r.Timestamp).Take(max).ToList();
    }
}
