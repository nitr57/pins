// SPDX-License-Identifier: MPL-2.0

#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NINA.Equipment.Equipment.MyGuider.Advanced;

namespace NINA.Equipment.Equipment.MyGuider.Internal;

/// <summary>
/// The guide logs in the guider's log directory for <see cref="NINA.Equipment.Interfaces.IGuideLogArchive"/>: the
/// per-night InternalGuider_GuideLog_yyyy-MM-dd.txt files and the older PinsGuider_/NativeGuider_ ones.
/// </summary>
internal static partial class GuideLogFiles
{
    // a plain file name, no path: <prefix>_GuideLog_<anything>.txt
    [GeneratedRegex(@"^[A-Za-z0-9]+_GuideLog_[A-Za-z0-9_\-]*\.txt$")]
    private static partial Regex NamePattern();

    [GeneratedRegex(@"(\d{4}-\d{2}-\d{2})")]
    private static partial Regex NightPattern();

    public static bool IsGuideLogName(string? name) => name is not null && NamePattern().IsMatch(name);

    /// <summary>The guide logs in <paramref name="directory"/>, newest night first; empty when it does not exist.</summary>
    /// <param name="activePath">The log being written, null when none.</param>
    public static IReadOnlyList<AdvancedGuideLogFile> List(string directory, string? activePath, TimeZoneInfo zone, DateTime utcNow)
    {
        if (!Directory.Exists(directory)) return [];
        string? active = activePath is null ? null : Path.GetFullPath(activePath);
        string zoneId = zone.Id;
        int offset = (int)Math.Round(zone.GetUtcOffset(utcNow).TotalMinutes);
        var files = new List<AdvancedGuideLogFile>();
        foreach (string path in Directory.EnumerateFiles(directory, "*_GuideLog_*.txt"))
        {
            string name = Path.GetFileName(path);
            if (!IsGuideLogName(name)) continue;
            try
            {
                var info = new FileInfo(path);
                files.Add(new AdvancedGuideLogFile
                {
                    Name = name,
                    Night = Night(name),
                    SizeBytes = info.Length,
                    ModifiedUtc = info.LastWriteTimeUtc,
                    Active = active is not null && string.Equals(Path.GetFullPath(path), active, StringComparison.Ordinal),
                    TimeZone = zoneId,
                    UtcOffsetMinutes = offset,
                });
            }
            catch (IOException)
            {
                // removed while listing
            }
        }

        return files
            .OrderByDescending(f => f.Night ?? string.Empty, StringComparer.Ordinal)
            .ThenByDescending(f => f.ModifiedUtc)
            .ToList();
    }

    /// <summary>Opens a guide log of <paramref name="directory"/> for reading while it may still be written; null when unknown.</summary>
    public static Stream? Open(string directory, string name)
    {
        if (!IsGuideLogName(name)) return null;
        string path = Path.Combine(directory, name);
        if (!File.Exists(path)) return null;
        try
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static string? Night(string name)
    {
        var m = NightPattern().Match(name);
        return m.Success && DateTime.TryParseExact(m.Groups[1].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
            ? m.Groups[1].Value
            : null;
    }
}
