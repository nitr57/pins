// SPDX-License-Identifier: MPL-2.0

using NINA.Mcp.Observatory;
using System.Globalization;

namespace NINA.Mcp.Tools;

/// <summary>Which sun altitude counts as dark for <see cref="NightPlanner"/>.</summary>
internal enum Darkness {

    /// <summary>Sun below -18°.</summary>
    Astronomical,

    /// <summary>Sun below -12°.</summary>
    Nautical
}

/// <summary>
/// Turns the sampled <see cref="NightSky"/> into the planning summary of <c>plan_targets</c>: twilight times, the dark
/// period, moon, and per target the transit, the times it rises above and sets below the altitude limit, the hours it
/// is usable in the dark, its best window and its distance to the moon. Pure computation on the samples, so the
/// resolution is the sampling step; crossing times are interpolated linearly between samples.
/// </summary>
internal static class NightPlanner {

    /// <summary>Sun altitude at sunrise and sunset (refraction and semi-diameter), degrees.</summary>
    public const double SunsetAltitude = -0.833;

    public const double CivilTwilight = -6;

    public const double NauticalTwilight = -12;

    public const double AstronomicalTwilight = -18;

    public static double DarkLimit(Darkness darkness) {
        return darkness == Darkness.Nautical ? NauticalTwilight : AstronomicalTwilight;
    }

    public static object Summarize(NightSky sky, double minAltitude, Darkness darkness, bool hourly) {
        var times = sky.Times;
        var count = times.Count;
        var darkLimit = DarkLimit(darkness);
        var dark = Enumerable.Range(0, count).Select(i => sky.SunAltitude[i] < darkLimit).ToArray();
        var stepHours = count > 1 ? (times[1] - times[0]).TotalHours : 0;
        var darkHours = Hours(dark, stepHours);
        var darkWindow = LongestRun(dark);

        var sun = new {
            sunset = Format(FirstCrossing(sky, sky.SunAltitude, SunsetAltitude, rising: false)),
            civilDusk = Format(FirstCrossing(sky, sky.SunAltitude, CivilTwilight, rising: false)),
            nauticalDusk = Format(FirstCrossing(sky, sky.SunAltitude, NauticalTwilight, rising: false)),
            astronomicalDusk = Format(FirstCrossing(sky, sky.SunAltitude, AstronomicalTwilight, rising: false)),
            astronomicalDawn = Format(LastCrossing(sky, sky.SunAltitude, AstronomicalTwilight, rising: true)),
            nauticalDawn = Format(LastCrossing(sky, sky.SunAltitude, NauticalTwilight, rising: true)),
            civilDawn = Format(LastCrossing(sky, sky.SunAltitude, CivilTwilight, rising: true)),
            sunrise = Format(LastCrossing(sky, sky.SunAltitude, SunsetAltitude, rising: true)),
            darkness = darkness == Darkness.Nautical ? "nautical" : "astronomical",
            darkFrom = darkWindow is null ? null : Format(times[darkWindow.Value.Start]),
            darkTo = darkWindow is null ? null : Format(times[darkWindow.Value.End]),
            darkHours = Round(darkHours, 2)
        };

        var moonUpInDark = Hours(Enumerable.Range(0, count).Select(i => dark[i] && sky.MoonAltitude[i] > 0).ToArray(), stepHours);

        var moon = new {
            illuminationPercent = sky.MoonIllumination is null ? (double?)null : Round(sky.MoonIllumination.Value * 100, 0),
            phase = sky.MoonPhase,
            rise = Format(FirstCrossing(sky, sky.MoonAltitude, 0, rising: true)),
            set = Format(FirstCrossing(sky, sky.MoonAltitude, 0, rising: false)),
            upDuringDarkHours = Round(moonUpInDark, 2)
        };

        var targets = sky.Targets
            .Select(target => SummarizeTarget(sky, target, minAltitude, dark, stepHours))
            .ToList();

        var notes = new List<string>(sky.Notes ?? []);

        if (darkHours <= 0) {
            notes.Add(darkness == Darkness.Astronomical
                ? "The sun does not get below -18° this night (no astronomical darkness); retry with darkness: nautical."
                : "The sun does not get below -12° this night (no nautical darkness).");
        }

        return new {
            site = sky.Site is null
                ? null
                : new {
                    latitude = Round(sky.Site.LatitudeDegrees, 4),
                    longitude = Round(sky.Site.LongitudeDegrees, 4),
                    elevation = Round(sky.Site.ElevationMeters, 0),
                    customHorizon = sky.Site.CustomHorizon
                },
            night = new {
                from = Format(count > 0 ? times[0] : null),
                to = Format(count > 0 ? times[count - 1] : null),
                now = Format(sky.Now),
                resolutionMinutes = Round(stepHours * 60, 1),
                minAltitude
            },
            sun,
            moon,
            targets,
            hourly = hourly ? Hourly(sky) : null,
            notes = notes.Count > 0 ? notes : null
        };
    }

    private static object SummarizeTarget(NightSky sky, TargetSky target, double minAltitude, bool[] dark, double stepHours) {
        var times = sky.Times;
        var count = times.Count;

        // The limit at each sample: the altitude limit or the custom horizon, whichever is higher.
        var limit = Enumerable.Range(0, count)
            .Select(i => Math.Max(minAltitude, HorizonAt(target, i)))
            .ToArray();

        var above = Enumerable.Range(0, count).Select(i => target.Altitude[i] > limit[i]).ToArray();
        var usable = Enumerable.Range(0, count).Select(i => above[i] && dark[i]).ToArray();
        var best = LongestRun(usable);

        var transitIndex = IndexOfMax(target.Altitude);
        var lowestIndex = IndexOfMin(target.Altitude);

        var rises = Crossings(sky, i => target.Altitude[i] - limit[i]).FirstOrDefault(crossing => crossing.Rising);
        var sets = Crossings(sky, i => target.Altitude[i] - limit[i]).FirstOrDefault(crossing => !crossing.Rising);

        var minSeparation = best is null
            ? null
            : Round(Enumerable.Range(best.Value.Start, best.Value.End - best.Value.Start + 1).Min(i => target.MoonSeparation[i]), 1);

        var notes = new List<string>();

        if (count > 0 && !above.Any(value => value)) {
            notes.Add(target.Altitude.Max() <= minAltitude
                ? string.Create(CultureInfo.InvariantCulture, $"Never above {minAltitude:0.#}° this night (highest {target.Altitude.Max():0.#}°).")
                : string.Create(CultureInfo.InvariantCulture, $"Never above the custom horizon this night (highest {target.Altitude.Max():0.#}°)."));
        } else if (count > 0 && above.All(value => value)) {
            notes.Add("Above the limit all night (circumpolar at this limit).");
        }

        if (count > 0 && best is null && above.Any(value => value)) {
            notes.Add("Above the limit only outside the dark hours.");
        }

        return new {
            name = target.Name,
            raHours = Round(target.RaHours, 4),
            decDegrees = Round(target.DecDegrees, 4),
            now = new {
                altitude = Round(target.AltitudeNow, 1),
                azimuth = Round(target.AzimuthNow, 1),
                aboveMinAltitude = target.AltitudeNow > minAltitude
            },
            transit = count == 0
                ? null
                : new {
                    time = Format(times[transitIndex]),
                    altitude = Round(target.Altitude[transitIndex], 1),
                    azimuth = Round(target.Azimuth[transitIndex], 1),
                    inDark = dark[transitIndex],
                    moonSeparation = Round(target.MoonSeparation[transitIndex], 1)
                },
            lowestAltitude = count == 0 ? (double?)null : Round(target.Altitude[lowestIndex], 1),
            risesAboveLimit = Format(rises.Time),
            setsBelowLimit = Format(sets.Time),
            usableDarkHours = Round(Hours(usable, stepHours), 2),
            bestWindow = best is null
                ? null
                : new {
                    from = Format(times[best.Value.Start]),
                    to = Format(times[best.Value.End]),
                    hours = Round(RunHours(best.Value, count, stepHours), 2),
                    minMoonSeparation = minSeparation
                },
            notes = notes.Count > 0 ? notes : null
        };
    }

    /// <summary>Full hours between civil dusk and civil dawn: sun, moon and every target's altitude (target order).</summary>
    private static List<object> Hourly(NightSky sky) {
        var rows = new List<object>();

        for (var i = 0; i < sky.Times.Count; i++) {
            var time = sky.Times[i];

            if (time.Minute != 0 || sky.SunAltitude[i] >= CivilTwilight) {
                continue;
            }

            rows.Add(new {
                time = Format(time),
                sun = Round(sky.SunAltitude[i], 1),
                moon = Round(sky.MoonAltitude[i], 1),
                targets = sky.Targets.Select(target => Round(target.Altitude[i], 1)).ToList()
            });
        }

        return rows;
    }

    private static (DateTimeOffset? Time, bool Rising) FirstCrossing(NightSky sky, IReadOnlyList<double> series, double threshold, bool rising) {
        return Crossings(sky, i => series[i] - threshold).FirstOrDefault(crossing => crossing.Rising == rising);
    }

    private static (DateTimeOffset? Time, bool Rising) LastCrossing(NightSky sky, IReadOnlyList<double> series, double threshold, bool rising) {
        return Crossings(sky, i => series[i] - threshold).LastOrDefault(crossing => crossing.Rising == rising);
    }

    /// <summary>Sign changes of <paramref name="value"/> between samples, with linearly interpolated times.</summary>
    private static List<(DateTimeOffset? Time, bool Rising)> Crossings(NightSky sky, Func<int, double> value) {
        var crossings = new List<(DateTimeOffset? Time, bool Rising)>();

        for (var i = 1; i < sky.Times.Count; i++) {
            var before = value(i - 1);
            var after = value(i);

            if (!double.IsFinite(before) || !double.IsFinite(after)) {
                continue;
            }

            var rising = before < 0 && after >= 0;
            var setting = before >= 0 && after < 0;

            if (!rising && !setting) {
                continue;
            }

            var fraction = before == after ? 0 : before / (before - after);
            var time = sky.Times[i - 1] + (sky.Times[i] - sky.Times[i - 1]) * fraction;

            crossings.Add((time, rising));
        }

        return crossings;
    }

    /// <summary>
    /// Hours during which <paramref name="values"/> is true, by the trapezoid rule over the sampling intervals: an interval
    /// counts fully when both ends are true and half when one is, so an all-dark night is 24 hours, not one step more.
    /// </summary>
    private static double Hours(bool[] values, double stepHours) {
        var halves = 0;

        for (var i = 1; i < values.Length; i++) {
            halves += (values[i - 1] ? 1 : 0) + (values[i] ? 1 : 0);
        }

        return halves * stepHours / 2;
    }

    /// <summary>Hours of one run of true samples, counted like <see cref="Hours"/>: half a step beyond each end inside the night.</summary>
    private static double RunHours((int Start, int End) run, int count, double stepHours) {
        var steps = run.End - run.Start + (run.Start > 0 ? 0.5 : 0) + (run.End < count - 1 ? 0.5 : 0);

        return steps * stepHours;
    }

    /// <summary>First and last index of the longest run of true values; null when there is none.</summary>
    private static (int Start, int End)? LongestRun(bool[] values) {
        (int Start, int End)? best = null;
        var start = -1;

        for (var i = 0; i <= values.Length; i++) {
            var on = i < values.Length && values[i];

            if (on && start < 0) {
                start = i;
            } else if (!on && start >= 0) {
                if (best is null || i - 1 - start > best.Value.End - best.Value.Start) {
                    best = (start, i - 1);
                }

                start = -1;
            }
        }

        return best;
    }

    private static int IndexOfMax(IReadOnlyList<double> values) {
        var index = 0;

        for (var i = 1; i < values.Count; i++) {
            if (values[i] > values[index]) {
                index = i;
            }
        }

        return index;
    }

    private static int IndexOfMin(IReadOnlyList<double> values) {
        var index = 0;

        for (var i = 1; i < values.Count; i++) {
            if (values[i] < values[index]) {
                index = i;
            }
        }

        return index;
    }

    /// <summary>Local time with offset, to the minute: 2026-10-13T21:35+02:00.</summary>
    public static string? Format(DateTimeOffset? time) {
        return time?.ToString("yyyy-MM-dd'T'HH:mmzzz", CultureInfo.InvariantCulture);
    }

    private static string? Format((DateTimeOffset? Time, bool Rising) crossing) {
        return Format(crossing.Time);
    }

    /// <summary>Rounded for the model; null for NaN and infinities (e.g. a moon position that could not be computed).</summary>
    private static double? Round(double value, int digits) {
        return double.IsFinite(value) ? Math.Round(value, digits, MidpointRounding.AwayFromZero) : null;
    }

    /// <summary>The custom horizon at sample <paramref name="index"/>; minus infinity without a custom horizon.</summary>
    private static double HorizonAt(TargetSky target, int index) {
        if (target.HorizonAltitude.Count <= index) {
            return double.NegativeInfinity;
        }

        return target.HorizonAltitude[index] ?? double.NegativeInfinity;
    }
}
