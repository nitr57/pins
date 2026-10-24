// SPDX-License-Identifier: MPL-2.0

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using NINA.Equipment.Equipment.MyGuider.Advanced;
using NINA.GuideEngine.Incidents;
using NINA.GuideEngine.Logging;

namespace NINA.Equipment.Equipment.MyGuider.Internal;

/// <summary>
/// The log excerpts of an incident download: guide-log lines and PINS (NINA) log lines of the incident's time window, with
/// the site location and the home directory masked.
/// </summary>
internal static partial class IncidentArchive
{
    /// <summary>Most lines an excerpt takes (a runaway debug log must not blow up the download).</summary>
    public const int MaxLines = 50000;

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly string[] CalibrationRows = ["West,", "East,", "Backlash,", "North,", "South,", "NudgeSouth,"];

    /// <summary>
    /// settings.json, guide-log.txt and pins-log.txt of an incident download: the log lines of the incident's time ±1 min
    /// from the per-night guide logs in <paramref name="guideLogDirectory"/> and the NINA logs in <paramref name="pinsLogDirectory"/>,
    /// with the site location and the home directory masked.
    /// </summary>
    public static IEnumerable<(string Name, string Content)> Extras(Incident incident, string settingsJson, string guideLogDirectory, string pinsLogDirectory,
        double? latitude, double? longitude, string? home, TimeZoneInfo zone)
    {
        var from = incident.Start.AddMinutes(-1);
        var to = incident.End.AddMinutes(1);
        yield return ("settings.json", Mask(settingsJson, latitude, longitude, home));
        yield return ("guide-log.txt", Mask(Text(GuideLogFiles(guideLogDirectory, from, to, zone).SelectMany(f => GuideLogExcerpt(ReadLines(f), from, to, zone)),
            "guide log"), latitude, longitude, home));
        yield return ("pins-log.txt", Mask(Text(PinsLogFiles(pinsLogDirectory, from, to, zone).SelectMany(f => PinsLogExcerpt(ReadLines(f), from, to, zone)),
            "PINS log"), latitude, longitude, home));
    }

    /// <summary>settings.json: the engine settings at the incident and the plugin settings now (by their names).</summary>
    public static string SettingsJson(string? engineSettingsJson, IEnumerable<AdvancedGuiderSetting> current)
    {
        JsonNode? atIncident = null;
        try
        {
            atIncident = string.IsNullOrEmpty(engineSettingsJson) ? null : JsonNode.Parse(engineSettingsJson);
        }
        catch (JsonException)
        {
            // keep null
        }

        var now = new JsonObject();
        foreach (var s in current.Where(s => s.Type != "action"))
        {
            now[s.Name] = s.Value;
        }

        var root = new JsonObject { ["engineSettingsAtIncident"] = atIncident, ["pluginSettingsNow"] = now };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>The guide log of a night (noon to noon): InternalGuider_GuideLog_yyyy-MM-dd.txt.</summary>
    public static string GuideLogFileName(DateTime night) => $"{GuidingLog.FilePrefix}{night.ToString("yyyy-MM-dd", Inv)}.txt";

    /// <summary>The per-night guide logs (<see cref="GuideLogFileName"/>) that cover the window.</summary>
    private static IEnumerable<string> GuideLogFiles(string directory, DateTimeOffset from, DateTimeOffset to, TimeZoneInfo zone)
    {
        var first = TimeZoneInfo.ConvertTime(from, zone).AddHours(-12).Date;
        var last = TimeZoneInfo.ConvertTime(to, zone).AddHours(-12).Date;
        for (var night = first; night <= last && night <= first.AddDays(2); night = night.AddDays(1))
        {
            string path = Path.Combine(directory, GuideLogFileName(night));
            if (File.Exists(path))
            {
                yield return path;
            }
        }
    }

    /// <summary>NINA logs (yyyyMMdd-HHmmss-version.pid-*.log) started before the window's end and written after its start.</summary>
    private static IEnumerable<string> PinsLogFiles(string directory, DateTimeOffset from, DateTimeOffset to, TimeZoneInfo zone)
    {
        if (!Directory.Exists(directory))
        {
            return [];
        }

        var localEnd = TimeZoneInfo.ConvertTime(to, zone).DateTime;
        return Directory.EnumerateFiles(directory, "*.log")
            .Where(f =>
            {
                string name = Path.GetFileName(f);
                bool startedBefore = name.Length < 15 || !DateTime.TryParseExact(name[..15], "yyyyMMdd-HHmmss", Inv, DateTimeStyles.None, out var started)
                    || started <= localEnd;
                return startedBefore && File.GetLastWriteTimeUtc(f) >= from.UtcDateTime;
            })
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();
    }

    private static string Text(IEnumerable<string> lines, string what)
    {
        var sb = new StringBuilder();
        foreach (var line in lines.Take(MaxLines))
        {
            sb.Append(line).Append('\n');
        }

        return sb.Length > 0 ? sb.ToString() : $"(no {what} lines in the incident's time window)\n";
    }

    /// <summary>
    /// Guide-log lines (PHD2 format) between <paramref name="from"/> and <paramref name="to"/>. Row times come from the
    /// "... Begins at" lines plus the rows' seconds; lines without a time of their own take the last known time. A section
    /// with lines in the window brings its header along. Times in the log are local to <paramref name="zone"/>.
    /// </summary>
    public static IReadOnlyList<string> GuideLogExcerpt(IEnumerable<string> lines, DateTimeOffset from, DateTimeOffset to, TimeZoneInfo zone)
    {
        var all = new List<(string Line, DateTimeOffset? Time, int Section, bool Header)>();
        DateTimeOffset? current = null, anchor = null;
        int section = -1;
        bool header = false;
        foreach (var line in lines)
        {
            bool isHeader = false;
            var at = AtTime().Match(line);
            if (at.Success && DateTime.TryParseExact(at.Groups[1].Value, "yyyy-MM-dd HH:mm:ss", Inv, DateTimeStyles.None, out var local))
            {
                current = Local(local, zone);
                if (line.Contains(" Begins at ", StringComparison.Ordinal))
                {
                    section++;
                    header = true;
                    anchor = line.StartsWith("Guiding Begins", StringComparison.Ordinal) ? current : null;
                }
                else
                {
                    header = false;
                }

                isHeader = header;
            }
            else if (anchor is { } a && DataRowSeconds(line) is { } sec)
            {
                current = a.AddSeconds(sec);
                header = false;
            }
            else if (CalibrationRows.Any(r => line.StartsWith(r, StringComparison.Ordinal)))
            {
                header = false;
            }
            else
            {
                isHeader = header;
            }

            all.Add((line, current, section, isHeader));
        }

        bool InWindow(DateTimeOffset? t) => t is { } v && v >= from && v <= to;
        var sections = all.Where(l => !l.Header && InWindow(l.Time)).Select(l => l.Section).ToHashSet();
        return all.Where(l => l.Header ? sections.Contains(l.Section) : InWindow(l.Time)).Select(l => l.Line).Take(MaxLines).ToList();
    }

    /// <summary>
    /// NINA log lines (<c>yyyy-MM-ddTHH:mm:ss.ffff|LEVEL|...</c>, local time of <paramref name="zone"/>) between
    /// <paramref name="from"/> and <paramref name="to"/>; continuation lines (exceptions) follow their line.
    /// </summary>
    public static IReadOnlyList<string> PinsLogExcerpt(IEnumerable<string> lines, DateTimeOffset from, DateTimeOffset to, TimeZoneInfo zone)
    {
        var result = new List<string>();
        bool include = false;
        foreach (var line in lines)
        {
            if (line.Length >= 24 && line[4] == '-' && line[10] == 'T'
                && DateTime.TryParseExact(line[..24], "yyyy-MM-ddTHH:mm:ss.ffff", Inv, DateTimeStyles.None, out var local))
            {
                var t = Local(local, zone);
                include = t >= from && t <= to;
            }

            if (include)
            {
                result.Add(line);
                if (result.Count >= MaxLines)
                {
                    break;
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Masks the site latitude and longitude (decimal degrees with '.' or ',' and at least 2 decimals, 3 near 0°; and
    /// degrees-minutes(-seconds) with °, ', ", :, d/m/s or spaces; leading zeros and signs included) and the home directory
    /// (replaced by ~).
    /// </summary>
    public static string Mask(string text, double? latitude, double? longitude, string? home)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        if (!string.IsNullOrEmpty(home) && home.Length > 1)
        {
            text = text.Replace(home.TrimEnd('/'), "~", StringComparison.Ordinal);
        }

        if (latitude is { } lat && double.IsFinite(lat) && lat != 0)
        {
            text = CoordinatePattern(lat).Replace(text, "<latitude>");
        }

        if (longitude is { } lon && double.IsFinite(lon) && lon != 0)
        {
            text = CoordinatePattern(lon).Replace(text, "<longitude>");
        }

        return text;
    }

    /// <summary>Reads a text file that another process may be writing.</summary>
    public static IEnumerable<string> ReadLines(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(fs, Encoding.UTF8);
        while (reader.ReadLine() is { } line)
        {
            yield return line;
        }
    }

    private static Regex CoordinatePattern(double value)
    {
        double a = Math.Abs(value);
        var patterns = new List<string>();

        // decimal degrees: truncated or rounded to 2 decimals (3 near 0°) and any further digits
        int decimals = a < 1 ? 3 : 2;
        double scale = Math.Pow(10, decimals);
        foreach (var d in new[] { Math.Truncate(a * scale) / scale, Math.Round(a, decimals, MidpointRounding.AwayFromZero) }.Distinct())
        {
            string s = d.ToString("F" + decimals.ToString(Inv), Inv);
            int dot = s.IndexOf('.');
            patterns.Add("0*" + Regex.Escape(s[..dot]) + "[.,]" + s[(dot + 1)..] + @"\d*");
        }

        // sexagesimal: whole degrees and minutes (truncated, or rounded up by the seconds), then seconds or decimal minutes
        int deg = (int)Math.Truncate(a);
        double minutes = (a - deg) * 60;
        int min = (int)Math.Truncate(minutes);
        var pairs = new HashSet<(int, int)> { (deg, min) };
        if (minutes - min >= 59.5 / 60)
        {
            pairs.Add(min + 1 == 60 ? (deg + 1, 0) : (deg, min + 1));
        }

        foreach (var (dd, mm) in pairs)
        {
            patterns.Add("0*" + dd.ToString(Inv) + @"\s*(?:°|:|d|\s)\s*0?" + mm.ToString(Inv)
                + @"(?:\s*(?:'|′|:|m|\s)\s*\d{1,2}(?:[.,]\d+)?(?:\s*(?:""|″|s))?|[.,]\d+(?:\s*(?:'|′))?|\s*(?:'|′))");
        }

        return new Regex(@"(?<![\d.,])[-+]?(?:" + string.Join("|", patterns) + @")(?!\d)", RegexOptions.CultureInvariant);
    }

    /// <summary>Seconds of a guide or DROP row ("12,34.567,\"Mount\",..."), null for other lines.</summary>
    private static double? DataRowSeconds(string line)
    {
        if (line.Length == 0 || !char.IsAsciiDigit(line[0]))
        {
            return null;
        }

        var parts = line.Split(',', 3);
        return parts.Length >= 3 && double.TryParse(parts[1], NumberStyles.Float, Inv, out var sec) ? sec : null;
    }

    private static DateTimeOffset Local(DateTime local, TimeZoneInfo zone) =>
        new(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), zone.GetUtcOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified)));

    [GeneratedRegex(@"\bat (\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})\s*$")]
    private static partial Regex AtTime();
}
