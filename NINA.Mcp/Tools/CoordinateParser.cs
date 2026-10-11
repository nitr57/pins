// SPDX-License-Identifier: MPL-2.0

using System.Globalization;
using System.Text;
using System.Text.Json;

namespace NINA.Mcp.Tools;

/// <summary>
/// Parses J2000 equatorial coordinates the way people and models write them.
/// </summary>
/// <remarks>
/// Right ascension is in hours: a number (5.5883) or a sexagesimal string ("05h35m17.3s", "5:35:17.3", "5 35 17.3",
/// "5h35.3m"). Declination is in degrees: a number (-5.391) or a sexagesimal string ("-05°23'28\"", "-5:23:28",
/// "-5 23 28", "-5d23m28s", "+41°16′09″"). Rules:
/// <list type="bullet">
/// <item>One to three components (hours/degrees, minutes, seconds); only the last one may have a fraction.</item>
/// <item>Separators are whitespace, ':' or unit markers (h/° d deg, m ' ′, s " ″). A marker must fit its position.</item>
/// <item>The sign applies to the whole value, so "-00:30:00" is -0.5. Minutes and seconds must be below 60.</item>
/// <item>RA must be 0 &lt;= ra &lt; 24 (24h is rejected, not wrapped) and has no sign. Dec must be -90..+90.</item>
/// </list>
/// </remarks>
public static class CoordinateParser {

    private const string RightAscensionHelp =
        "Use hours as a number (0 <= ra < 24, e.g. 5.5883) or a sexagesimal string such as \"05h35m17.3s\", \"5:35:17.3\" or \"5 35 17.3\".";

    private const string DeclinationHelp =
        "Use degrees as a number (-90..90, e.g. -5.391) or a sexagesimal string such as \"-05°23'28\\\"\", \"-5:23:28\" or \"-5 23 28\".";

    private enum Marker {
        None,
        Colon,
        Hours,
        Degrees,
        Minutes,
        Seconds
    }

    private readonly record struct Component(string Text, double Value, Marker Marker);

    /// <summary>Parses a right ascension string to hours. Returns false with a message when the text is invalid or out of range.</summary>
    public static bool TryParseRightAscension(string? text, out double hours, out string error) {
        hours = 0;

        if (!TryParseSexagesimal(text, isRightAscension: true, out var negative, out var value, out error)) {
            return false;
        }

        if (negative) {
            error = "Right ascension cannot be negative.";
            return false;
        }

        if (value >= 24) {
            error = "Right ascension must be below 24h.";
            return false;
        }

        hours = value;
        return true;
    }

    /// <summary>Parses a declination string to degrees. Returns false with a message when the text is invalid or out of range.</summary>
    public static bool TryParseDeclination(string? text, out double degrees, out string error) {
        degrees = 0;

        if (!TryParseSexagesimal(text, isRightAscension: false, out var negative, out var value, out error)) {
            return false;
        }

        if (value > 90) {
            error = "Declination must be between -90° and +90°.";
            return false;
        }

        degrees = negative ? -value : value;
        return true;
    }

    /// <summary>
    /// Reads a right ascension argument (number or string). Null when absent. Throws <see cref="ToolArgumentException"/>
    /// naming <paramref name="displayName"/> (default: <paramref name="name"/>) when invalid.
    /// </summary>
    public static double? ReadRightAscension(ToolArguments arguments, string name, string? displayName = null) {
        displayName ??= name;

        var raw = arguments.GetRaw(name);

        if (raw is null) {
            return null;
        }

        if (raw.Value.ValueKind == JsonValueKind.Number) {
            var hours = arguments.GetDouble(name)!.Value;

            if (hours < 0 || hours >= 24) {
                throw new ToolArgumentException(
                    $"Argument '{displayName}' must be at least 0 and below 24 hours (got {Format(hours)}). "
                    + "Right ascension is in hours, not degrees: divide degrees by 15.");
            }

            return hours;
        }

        if (raw.Value.ValueKind != JsonValueKind.String) {
            throw new ToolArgumentException($"Argument '{displayName}' must be a number or a string. {RightAscensionHelp}");
        }

        var text = raw.Value.GetString();

        if (!TryParseRightAscension(text, out var parsed, out var error)) {
            throw new ToolArgumentException($"Argument '{displayName}' (\"{text}\") is not a valid right ascension: {error} {RightAscensionHelp}");
        }

        return parsed;
    }

    /// <summary>
    /// Reads a declination argument (number or string). Null when absent. Throws <see cref="ToolArgumentException"/>
    /// naming <paramref name="displayName"/> (default: <paramref name="name"/>) when invalid.
    /// </summary>
    public static double? ReadDeclination(ToolArguments arguments, string name, string? displayName = null) {
        displayName ??= name;

        var raw = arguments.GetRaw(name);

        if (raw is null) {
            return null;
        }

        if (raw.Value.ValueKind == JsonValueKind.Number) {
            var degrees = arguments.GetDouble(name)!.Value;

            if (degrees < -90 || degrees > 90) {
                throw new ToolArgumentException($"Argument '{displayName}' must be between -90 and 90 degrees (got {Format(degrees)}).");
            }

            return degrees;
        }

        if (raw.Value.ValueKind != JsonValueKind.String) {
            throw new ToolArgumentException($"Argument '{displayName}' must be a number or a string. {DeclinationHelp}");
        }

        var text = raw.Value.GetString();

        if (!TryParseDeclination(text, out var parsed, out var error)) {
            throw new ToolArgumentException($"Argument '{displayName}' (\"{text}\") is not a valid declination: {error} {DeclinationHelp}");
        }

        return parsed;
    }

    /// <summary>Formats hours as "05h35m17.3s".</summary>
    public static string FormatRightAscension(double hours) {
        var tenths = (long)Math.Round(Math.Abs(hours) * 36000, MidpointRounding.AwayFromZero) % (24 * 36000);

        var h = tenths / 36000;
        var m = tenths / 600 % 60;
        var s = tenths % 600 / 10.0;

        return string.Create(CultureInfo.InvariantCulture, $"{h:00}h{m:00}m{s:00.0}s");
    }

    /// <summary>Formats degrees as "-05°23'28\"".</summary>
    public static string FormatDeclination(double degrees) {
        var arcseconds = (long)Math.Round(Math.Abs(degrees) * 3600, MidpointRounding.AwayFromZero);

        var d = arcseconds / 3600;
        var m = arcseconds / 60 % 60;
        var s = arcseconds % 60;
        var sign = degrees < 0 && arcseconds > 0 ? "-" : "+";

        return string.Create(CultureInfo.InvariantCulture, $"{sign}{d:00}°{m:00}'{s:00}\"");
    }

    private static bool TryParseSexagesimal(
        string? text,
        bool isRightAscension,
        out bool negative,
        out double value,
        out string error) {
        negative = false;
        value = 0;

        if (string.IsNullOrWhiteSpace(text)) {
            error = "The value is empty.";
            return false;
        }

        var normalized = Normalize(text);

        if (normalized[0] is '+' or '-') {
            negative = normalized[0] == '-';
            normalized = normalized[1..].TrimStart();
        }

        if (!TryTokenize(normalized, out var components, out error)) {
            return false;
        }

        if (!TryCheckMarkers(components, isRightAscension, out error)) {
            return false;
        }

        for (var i = 0; i < components.Count; i++) {
            var component = components[i];

            if (i < components.Count - 1 && component.Text.Contains('.')) {
                error = "Only the last component may have a decimal fraction.";
                return false;
            }

            if (i > 0 && component.Value >= 60) {
                var unit = i == 1 ? "Minutes" : "Seconds";

                error = $"{unit} must be below 60 (got {component.Text}).";
                return false;
            }
        }

        value = components[0].Value;

        if (components.Count > 1) {
            value += components[1].Value / 60;
        }

        if (components.Count > 2) {
            value += components[2].Value / 3600;
        }

        error = string.Empty;
        return true;
    }

    /// <summary>Lower case, unicode primes/quotes/minus signs mapped to ASCII, "''" to '"', trimmed.</summary>
    private static string Normalize(string text) {
        var builder = new StringBuilder(text.Length);

        foreach (var character in text.Trim()) {
            var mapped = character switch {
                '′' or '’' or '‘' or '´' or '`' => '\'',
                '″' or '“' or '”' => '"',
                '−' or '–' or '—' => '-',
                'º' or '˚' => '°',
                ' ' or '\t' => ' ',
                _ => char.ToLowerInvariant(character)
            };

            builder.Append(mapped);
        }

        return builder.ToString().Replace("''", "\"", StringComparison.Ordinal).Trim();
    }

    private static bool TryTokenize(string text, out List<Component> components, out string error) {
        components = [];

        var index = 0;

        while (index < text.Length) {
            SkipWhitespace(text, ref index);

            if (index >= text.Length) {
                break;
            }

            if (!char.IsAsciiDigit(text[index])) {
                error = $"Unexpected '{text[index]}'.";
                return false;
            }

            var start = index;

            while (index < text.Length && (char.IsAsciiDigit(text[index]) || text[index] == '.')) {
                index++;
            }

            var number = text[start..index];

            if (!double.TryParse(number, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var parsed)
                || number.EndsWith('.')) {
                error = $"'{number}' is not a number.";
                return false;
            }

            SkipWhitespace(text, ref index);

            var marker = ReadMarker(text, ref index);

            components.Add(new Component(number, parsed, marker));

            if (components.Count > 3) {
                error = "Too many components (at most hours/degrees, minutes and seconds).";
                return false;
            }
        }

        if (components.Count == 0) {
            error = "No number found.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static void SkipWhitespace(string text, ref int index) {
        while (index < text.Length && char.IsWhiteSpace(text[index])) {
            index++;
        }
    }

    private static Marker ReadMarker(string text, ref int index) {
        if (index >= text.Length) {
            return Marker.None;
        }

        if (string.CompareOrdinal(text, index, "deg", 0, 3) == 0) {
            index += 3;
            return Marker.Degrees;
        }

        var marker = text[index] switch {
            ':' => Marker.Colon,
            'h' => Marker.Hours,
            '°' or 'd' => Marker.Degrees,
            'm' or '\'' => Marker.Minutes,
            's' or '"' => Marker.Seconds,
            _ => Marker.None
        };

        if (marker != Marker.None) {
            index++;
        }

        return marker;
    }

    private static bool TryCheckMarkers(List<Component> components, bool isRightAscension, out string error) {
        for (var i = 0; i < components.Count; i++) {
            var marker = components[i].Marker;
            var isLast = i == components.Count - 1;

            if (marker == Marker.None) {
                continue;
            }

            if (marker == Marker.Colon) {
                if (isLast) {
                    error = "A ':' must be followed by another number.";
                    return false;
                }

                continue;
            }

            if (i == 0 && isRightAscension && marker == Marker.Degrees) {
                error = "Right ascension is in hours, not degrees: divide degrees by 15.";
                return false;
            }

            if (i == 0 && !isRightAscension && marker == Marker.Hours) {
                error = "Declination is in degrees, not hours.";
                return false;
            }

            var expected = i switch {
                0 => isRightAscension ? Marker.Hours : Marker.Degrees,
                1 => Marker.Minutes,
                _ => Marker.Seconds
            };

            if (marker != expected) {
                error = $"Unit marker of '{components[i].Text}' does not fit its position.";
                return false;
            }
        }

        error = string.Empty;
        return true;
    }

    private static string Format(double value) {
        return value.ToString("0.#########", CultureInfo.InvariantCulture);
    }
}
