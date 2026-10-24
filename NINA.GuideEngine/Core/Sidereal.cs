// SPDX-License-Identifier: MPL-2.0

namespace NINA.GuideEngine.Core;

/// <summary>Sidereal time constants shared by the engine and the simulator.</summary>
internal static class Sidereal
{
    /// <summary>Length of a sidereal day in SI seconds (23 h 56 min 4.0905 s).</summary>
    public const double DaySeconds = 86164.0905;

    /// <summary>Length of a sidereal hour in SI seconds.</summary>
    public const double HourSeconds = DaySeconds / 24.0;

    /// <summary>
    /// The sidereal rate (1× tracking) in arcsec of RA axis angle per SI second, rounded: 360° per sidereal day is
    /// 15.0410686″/s. The 5 ppm are far below the accuracy of any guide rate, and the simulator's tuned test results
    /// depend on this exact value.
    /// </summary>
    public const double ArcsecPerSecond = 15.041;
}
