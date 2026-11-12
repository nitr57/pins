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

    // the date of a per-night name, or date and start time of an older per-session name (_yyyy-MM-dd_HHmmss)
    [GeneratedRegex(@"(\d{4}-\d{2}-\d{2})(?:_(\d{6}))?\.txt$")]
    private static partial Regex NightPattern();

    public static bool IsGuideLogName(string? name) => name is not null && NamePattern().IsMatch(name);

    /// <summary>The guide logs in <paramref name="directory"/>, newest night first; empty when it does not exist.</summary>
    /// <param name="activePath">The log being written, null when none.</param>
    public static IReadOnlyList<AdvancedGuideLogFile> List(string directory, string? activePath, TimeZoneInfo zone, DateTime utcNow)
    {
        if (!Directory.Exists(directory)) return [];
        string? active = activePath is null ? null : Path.GetFullPath(activePath);
        string zoneId = zone.Id;
        var files = new List<AdvancedGuideLogFile>();
        foreach (string path in Directory.EnumerateFiles(directory, "*_GuideLog_*.txt"))
        {
            string name = Path.GetFileName(path);
            if (!IsGuideLogName(name)) continue;
            try
            {
                var info = new FileInfo(path);
                string? night = Night(name);
                files.Add(new AdvancedGuideLogFile
                {
                    Name = name,
                    Night = night,
                    SizeBytes = info.Length,
                    ModifiedUtc = info.LastWriteTimeUtc,
                    Active = active is not null && string.Equals(Path.GetFullPath(path), active, StringComparison.Ordinal),
                    TimeZone = zoneId,
                    UtcOffsetMinutes = OffsetMinutes(zone, night, utcNow),
                });
            }
            catch (FileNotFoundException)
            {
                // removed while listing
            }
        }

        return files
            .OrderByDescending(f => f.Night ?? string.Empty, StringComparer.Ordinal)
            .ThenByDescending(f => f.ModifiedUtc)
            .ToList();
    }

    /// <summary>
    /// Opens a guide log of <paramref name="directory"/> for reading while it may still be written; null when there is no
    /// such log. Other errors (unreadable card, permissions) are thrown, so the caller can report them.
    /// </summary>
    public static Stream? Open(string directory, string name)
    {
        if (!IsGuideLogName(name)) return null;
        try
        {
            return new FileStream(Path.Combine(directory, name), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                64 * 1024, FileOptions.SequentialScan);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    // The night (noon to noon) of the name: the date of a per-night log, the night of the start time of a per-session one,
    // which can be the day before its date
    private static string? Night(string name)
    {
        var m = NightPattern().Match(name);
        if (!m.Success || !DateTime.TryParseExact(m.Groups[1].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            return null;
        }

        if (m.Groups[2].Success
            && DateTime.TryParseExact(m.Groups[1].Value + m.Groups[2].Value, "yyyy-MM-ddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start))
        {
            date = InternalGuider.GuideLogNight(start);
        }

        return date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    // The zone's offset in the evening of the night (the offset the log's times were written with, also across a DST
    // change); the current one for a log without a night
    private static int OffsetMinutes(TimeZoneInfo zone, string? night, DateTime utcNow)
    {
        DateTime at = utcNow;
        if (night is not null && DateTime.TryParseExact(night, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            var evening = DateTime.SpecifyKind(date.AddHours(18), DateTimeKind.Unspecified);
            at = zone.IsInvalidTime(evening) ? utcNow : TimeZoneInfo.ConvertTimeToUtc(evening, zone);
        }

        return (int)Math.Round(zone.GetUtcOffset(at).TotalMinutes);
    }
}
