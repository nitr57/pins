// SPDX-License-Identifier: MPL-2.0

using Moq;
using NINA.Mcp.Observatory;
using NINA.Profile.Interfaces;
using System;
using System.Linq;

namespace NINA.Test.Mcp {

    /// <summary>
    /// plan_targets' sampling (SOFA/NOVAS through the MCP adapter) against reference values for Greenwich
    /// (51.4779° N, 0.0015° W).
    /// </summary>
    /// <remarks>
    /// Sunrise and sunset on 2024-06-21: 03:43 and 20:21 UTC (04:43 and 21:21 BST), as published by HM Nautical Almanac
    /// Office / USNO for London (sun's upper limb at the horizon with standard refraction, i.e. centre at -0.833°).
    /// Culmination altitudes follow from the spherical-astronomy relations h = 90° - |φ - δ| (upper) and h = φ + δ - 90°
    /// (lower), with the sun's declination +23.43° on 2024-06-21/22 (Astronomical Almanac) and about -12.1° on
    /// 2026-10-25.
    /// </remarks>
    [TestFixture]
    public class NinaObservatoryNightSkyTest {

        private const double Latitude = 51.4779;

        private const double Longitude = -0.0015;

        [Test]
        public void SummerSolsticeAtGreenwich_MatchesTheAlmanac() {
            var sky = Sample(new DateOnly(2024, 6, 21));

            Assert.That(sky.Success, Is.True, sky.Message);

            var sunset = Crossing(sky, -0.833, rising: false);
            var sunrise = Crossing(sky, -0.833, rising: true);

            Assert.That(sunset, Is.EqualTo(new DateTime(2024, 6, 21, 20, 21, 0, DateTimeKind.Utc)).Within(TimeSpan.FromMinutes(3)));
            Assert.That(sunrise, Is.EqualTo(new DateTime(2024, 6, 22, 3, 43, 0, DateTimeKind.Utc)).Within(TimeSpan.FromMinutes(3)));

            // Upper culmination at the start of the window (solar noon), lower one at solar midnight.
            Assert.That(sky.SunAltitude.Max(), Is.EqualTo(90 - (Latitude - 23.43)).Within(0.2));
            Assert.That(sky.SunAltitude.Min(), Is.EqualTo(Latitude + 23.43 - 90).Within(0.2));

            // No astronomical darkness at midsummer in London: the sun stays above -18°.
            Assert.That(sky.SunAltitude.Min(), Is.GreaterThan(-18));
        }

        [Test]
        public void DaylightSavingChangeNight_IsOneContinuous24HourWindow() {
            // Europe leaves summer time at 01:00 UTC on 2026-10-25, inside the night of the 24th.
            var sky = Sample(new DateOnly(2026, 10, 24));

            Assert.That(sky.Success, Is.True, sky.Message);

            var utc = sky.Times.Select(time => time.UtcDateTime).ToList();

            Assert.That(utc.First(), Is.EqualTo(new DateTime(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc)).Within(TimeSpan.FromHours(1)));
            Assert.That(utc.Last() - utc.First(), Is.EqualTo(TimeSpan.FromDays(1)));
            Assert.That(utc.Zip(utc.Skip(1), (a, b) => b - a).Distinct(), Is.EqualTo(new[] { TimeSpan.FromMinutes(5) }));
            Assert.That(sky.SunAltitude.Min(), Is.EqualTo(Latitude - 12.1 - 90).Within(0.5));
        }

        private static NightSky Sample(DateOnly night) {
            var harness = new NinaObservatoryHarness();
            var astrometry = new Mock<IAstrometrySettings>();

            astrometry.SetupGet(settings => settings.Latitude).Returns(Latitude);
            astrometry.SetupGet(settings => settings.Longitude).Returns(Longitude);
            astrometry.SetupGet(settings => settings.Elevation).Returns(46);
            harness.Profile.Setup(profile => profile.ActiveProfile.AstrometrySettings).Returns(astrometry.Object);

            return harness.Observatory.GetNightSky(new NightSkyRequest(night, TimeSpan.FromMinutes(5), []));
        }

        /// <summary>The first sun crossing of <paramref name="altitude"/> in the given direction, interpolated linearly.</summary>
        private static DateTime Crossing(NightSky sky, double altitude, bool rising) {
            for (var i = 1; i < sky.Times.Count; i++) {
                var before = sky.SunAltitude[i - 1] - altitude;
                var after = sky.SunAltitude[i] - altitude;

                if (rising ? before < 0 && after >= 0 : before >= 0 && after < 0) {
                    var fraction = before / (before - after);

                    return (sky.Times[i - 1] + (sky.Times[i] - sky.Times[i - 1]) * fraction).UtcDateTime;
                }
            }

            throw new AssertionException($"No {(rising ? "rising" : "setting")} crossing of {altitude}°.");
        }
    }
}
