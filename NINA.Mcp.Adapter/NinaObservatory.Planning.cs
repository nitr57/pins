// SPDX-License-Identifier: MPL-2.0

using NINA.Astrometry;
using NINA.Core.Utility;
using NINA.Mcp.Observatory;
using static System.FormattableString;

namespace NINA.Mcp.Adapter;

public sealed partial class NinaObservatory {

    /// <summary>Upper bound of samples per night, whatever step is asked for.</summary>
    private const int MaxNightSamples = 1500;

    // ----- Planning -----

    /// <summary>
    /// Samples one observing night (local noon to the next local noon, in UTC steps, so DST nights have 23 or 25 hours)
    /// at the profile's site. Target positions use the same SOFA transform as NINA (<see cref="Coordinates.Transform(Angle, Angle, double, DateTime)"/>,
    /// no refraction); sun and moon come from NOVAS via <see cref="AstroUtil"/>.
    /// </summary>
    public NightSky GetNightSky(NightSkyRequest request) {
        var now = DateTimeOffset.Now;

        try {
            var astrometry = profileService.ActiveProfile.AstrometrySettings;
            var latitude = astrometry.Latitude;
            var longitude = astrometry.Longitude;
            var elevation = astrometry.Elevation;

            if (latitude == 0 && longitude == 0) {
                return NightSky.Fail(
                    "The site location is not set: latitude and longitude are 0 in the pins profile. Set them in the profile's astrometry settings (Touch-N-Stars settings).",
                    now);
            }

            var night = request.Night ?? DateOnly.FromDateTime(now.Hour < 12 ? now.Date.AddDays(-1) : now.Date);
            var startLocal = night.ToDateTime(new TimeOnly(12, 0), DateTimeKind.Unspecified);
            var startUtc = TimeZoneInfo.ConvertTimeToUtc(startLocal, TimeZoneInfo.Local);
            var endUtc = TimeZoneInfo.ConvertTimeToUtc(startLocal.AddDays(1), TimeZoneInfo.Local);
            var step = request.Step > TimeSpan.Zero ? request.Step : TimeSpan.FromMinutes(5);

            if ((endUtc - startUtc) / step > MaxNightSamples) {
                step = (endUtc - startUtc) / MaxNightSamples;
            }

            var observer = new ObserverInfo {
                Latitude = latitude,
                Longitude = longitude,
                Elevation = elevation
            };

            var latitudeAngle = Angle.ByDegree(latitude);
            var longitudeAngle = Angle.ByDegree(longitude);
            var horizon = astrometry.Horizon;

            var times = new List<DateTimeOffset>();
            var sun = new List<double>();
            var moon = new List<double>();
            var moonCoordinates = new List<Coordinates>();

            for (var time = startUtc; time <= endUtc; time += step) {
                times.Add(new DateTimeOffset(time).ToLocalTime());
                sun.Add(AstroUtil.GetSunAltitude(time, observer));
                moon.Add(AstroUtil.GetMoonAltitude(time, observer));

                var moonPosition = AstroUtil.GetMoonPosition(time, observer);
                moonCoordinates.Add(new Coordinates(Angle.ByHours(moonPosition.RA), Angle.ByDegree(moonPosition.Dec), Epoch.JNOW, time));
            }

            var targets = new List<TargetSky>();

            foreach (var target in request.Targets) {
                var coordinates = new Coordinates(Angle.ByHours(target.RaHours), Angle.ByDegree(target.DecDegrees), Epoch.J2000);
                var altitude = new List<double>(times.Count);
                var azimuth = new List<double>(times.Count);
                var horizonAltitude = new List<double?>(times.Count);
                var separation = new List<double>(times.Count);
                var index = 0;

                for (var time = startUtc; time <= endUtc; time += step) {
                    var topocentric = coordinates.Transform(latitudeAngle, longitudeAngle, elevation, time);
                    var az = topocentric.Azimuth.Degree;

                    altitude.Add(topocentric.Altitude.Degree);
                    azimuth.Add(az);
                    horizonAltitude.Add(horizon?.GetAltitude(az));

                    // Moon in apparent (JNOW) coordinates; the operator transforms the target to that epoch.
                    separation.Add((moonCoordinates[index] - coordinates).Distance.Degree);
                    index++;
                }

                var current = coordinates.Transform(latitudeAngle, longitudeAngle, elevation, now.UtcDateTime);

                targets.Add(new TargetSky(
                    target.Name,
                    target.RaHours,
                    target.DecDegrees,
                    current.Altitude.Degree,
                    current.Azimuth.Degree,
                    altitude,
                    azimuth,
                    horizon == null ? new List<double?>() : horizonAltitude,
                    separation));
            }

            // Illumination and phase at the middle of the night (local midnight).
            var middle = startUtc + (endUtc - startUtc) / 2;

            return new NightSky(
                true,
                Invariant($"Night of {night:yyyy-MM-dd} at latitude {latitude:0.###}, longitude {longitude:0.###}."),
                new ObservingSite(latitude, longitude, elevation, horizon != null),
                now,
                times,
                sun,
                moon,
                AstroUtil.GetMoonIllumination(middle, observer),
                AstroUtil.GetMoonPhase(middle, observer).ToString(),
                targets);
        } catch (Exception ex) {
            Logger.Error("MCP: night planning failed", ex);

            return NightSky.Fail($"Night planning failed: {ex.Message}", now);
        }
    }
}
