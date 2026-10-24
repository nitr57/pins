// SPDX-License-Identifier: MPL-2.0

namespace NINA.Mcp.Tools;

/// <summary>
/// The span of one observing night at the site: from the site's mean solar noon (12:00 UTC minus longitude / 15 hours,
/// east positive) to the next one, so that the night is right whatever time zone the pins box is set to. The start is
/// moved back to the box's full local hour, so that samples in whole-minute steps still hit every full local hour.
/// </summary>
public static class NightWindow {

    /// <summary>How far the box's UTC offset may be from the site's solar offset before the result notes it.</summary>
    public static readonly TimeSpan OffsetNoteThreshold = TimeSpan.FromHours(3);

    /// <summary>The site's mean solar noon on <paramref name="night"/> (the date of the evening at the site), UTC.</summary>
    public static DateTime SolarNoonUtc(DateOnly night, double longitudeDegrees) {
        var noon = night.ToDateTime(new TimeOnly(12, 0), DateTimeKind.Utc);

        return noon - SolarOffset(longitudeDegrees);
    }

    /// <summary>
    /// Start and end (UTC) of the night <paramref name="night"/>: 24 hours from the full local hour at or before the solar
    /// noon, in <paramref name="boxZone"/> (the box's time zone, which only decides the full hour).
    /// </summary>
    public static (DateTime StartUtc, DateTime EndUtc) Span(DateOnly night, double longitudeDegrees, TimeZoneInfo boxZone) {
        ArgumentNullException.ThrowIfNull(boxZone);

        var noonUtc = SolarNoonUtc(night, longitudeDegrees);
        var offset = boxZone.GetUtcOffset(noonUtc);
        var local = noonUtc + offset;
        var fullHour = new DateTime(local.Year, local.Month, local.Day, local.Hour, 0, 0, DateTimeKind.Unspecified);
        var startUtc = DateTime.SpecifyKind(fullHour - offset, DateTimeKind.Utc);

        return (startUtc, startUtc.AddDays(1));
    }

    /// <summary>The night that contains <paramref name="utcNow"/>: before the site's solar noon it is the previous evening's.</summary>
    public static DateOnly Current(DateTime utcNow, double longitudeDegrees) {
        var solar = utcNow.ToUniversalTime() + SolarOffset(longitudeDegrees);

        return DateOnly.FromDateTime(solar.Hour < 12 ? solar.Date.AddDays(-1) : solar.Date);
    }

    /// <summary>
    /// A note when the box's UTC offset differs from the site's solar offset by more than <see cref="OffsetNoteThreshold"/>
    /// (a Pi left on UTC far from Greenwich): every time is still right, but carries the box's offset. Null otherwise.
    /// </summary>
    public static string? OffsetNote(double longitudeDegrees, TimeSpan boxOffset) {
        var difference = boxOffset - SolarOffset(longitudeDegrees);

        if (difference.Duration() <= OffsetNoteThreshold) {
            return null;
        }

        var sign = boxOffset < TimeSpan.Zero ? "-" : "+";

        return FormattableString.Invariant($"The pins box's time zone (UTC{sign}{boxOffset.Duration():hh\\:mm}) is about ")
            + FormattableString.Invariant($"{Math.Abs(difference.TotalHours):0} h away from the site's solar time; all times carry the ")
            + "box's offset, so convert them for the user and consider setting the box's time zone.";
    }

    private static TimeSpan SolarOffset(double longitudeDegrees) {
        return TimeSpan.FromHours(longitudeDegrees / 15);
    }
}
