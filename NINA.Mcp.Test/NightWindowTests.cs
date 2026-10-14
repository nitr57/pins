// SPDX-License-Identifier: MPL-2.0

using NINA.Mcp.Tools;
using NUnit.Framework;

namespace NINA.Mcp.Test;

/// <summary>The span of plan_targets' night, independent of the pins box's time zone.</summary>
[TestFixture]
public class NightWindowTests {

    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    private static readonly TimeZoneInfo CentralEuropeanSummer = TimeZoneInfo.CreateCustomTimeZone("box+02", TimeSpan.FromHours(2), "box+02", "box+02");

    [Test]
    public void UtcBox_AtAUtcPlus10Site_StartsAtTheSitesNoon() {
        // A Pi left on UTC near Sydney (longitude 150° E): the old local-noon window ran 22:00 to 22:00 site time.
        var (start, end) = NightWindow.Span(new DateOnly(2026, 10, 13), 150, Utc);

        Assert.That(start, Is.EqualTo(new DateTime(2026, 10, 13, 2, 0, 0, DateTimeKind.Utc)));
        Assert.That(end, Is.EqualTo(new DateTime(2026, 10, 14, 2, 0, 0, DateTimeKind.Utc)));
        Assert.That(start.Kind, Is.EqualTo(DateTimeKind.Utc));
    }

    [Test]
    public void UtcBox_AtAUtcMinus7Site_KeepsTheMorning() {
        var (start, end) = NightWindow.Span(new DateOnly(2026, 10, 13), -105, Utc);

        Assert.That(start, Is.EqualTo(new DateTime(2026, 10, 13, 19, 0, 0, DateTimeKind.Utc)));
        Assert.That(end - start, Is.EqualTo(TimeSpan.FromDays(1)));
    }

    [Test]
    public void Start_IsTheBoxsFullHourBeforeSolarNoon() {
        // Longitude 8.1° E: solar noon 11:27:36 UTC = 13:27:36 box time, so the night starts 13:00 box time.
        Assert.That(NightWindow.SolarNoonUtc(new DateOnly(2026, 10, 13), 8.1), Is.EqualTo(new DateTime(2026, 10, 13, 11, 27, 36, DateTimeKind.Utc)).Within(TimeSpan.FromMilliseconds(1)));

        var (start, _) = NightWindow.Span(new DateOnly(2026, 10, 13), 8.1, CentralEuropeanSummer);

        Assert.That(start, Is.EqualTo(new DateTime(2026, 10, 13, 11, 0, 0, DateTimeKind.Utc)));
    }

    [Test]
    public void Current_FollowsTheSitesSolarTime() {
        // 13:00 UTC is 23:00 at 150° E: the night of the 13th, although the UTC box would call it afternoon.
        var midnightAtSite = new DateTime(2026, 10, 13, 13, 0, 0, DateTimeKind.Utc);
        var night = NightWindow.Current(midnightAtSite, 150);
        var (start, end) = NightWindow.Span(night, 150, Utc);

        Assert.That(night, Is.EqualTo(new DateOnly(2026, 10, 13)));
        Assert.That(midnightAtSite, Is.InRange(start, end));
        Assert.That(end - midnightAtSite, Is.GreaterThan(TimeSpan.FromHours(12)), "dawn is in the window");

        // 23:00 UTC is 09:00 the next morning at the site: still the night that began on the 13th.
        Assert.That(NightWindow.Current(new DateTime(2026, 10, 13, 23, 0, 0, DateTimeKind.Utc), 150), Is.EqualTo(new DateOnly(2026, 10, 13)));

        // 05:00 UTC on the 14th is 22:00 on the 13th at 105° W.
        Assert.That(NightWindow.Current(new DateTime(2026, 10, 14, 5, 0, 0, DateTimeKind.Utc), -105), Is.EqualTo(new DateOnly(2026, 10, 13)));
    }

    [Test]
    public void OffsetNote_OnlyWhenTheBoxZoneIsFarFromTheSite() {
        var note = NightWindow.OffsetNote(150, TimeSpan.Zero);

        Assert.That(note, Does.Contain("UTC+00:00").And.Contain("10 h"));
        Assert.That(NightWindow.OffsetNote(8.1, TimeSpan.FromHours(2)), Is.Null);
        Assert.That(NightWindow.OffsetNote(-105, TimeSpan.FromHours(-6)), Is.Null);
        Assert.That(NightWindow.OffsetNote(-105, TimeSpan.Zero), Does.Contain("7 h"));
    }
}
