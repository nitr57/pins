// SPDX-License-Identifier: MPL-2.0

namespace NINA.GuideEngine.Core;

public enum GuideDirection
{
    North,
    South,
    East,
    West,
}

public enum GuideAxis
{
    Ra,
    Dec,
}

public enum PierSide
{
    Unknown,
    East,
    West,
}

public enum DecGuideMode
{
    Off,

    /// <summary>Both directions (PHD2).</summary>
    Auto,

    /// <summary>North pulses only.</summary>
    North,

    /// <summary>South pulses only.</summary>
    South,

    /// <summary>
    /// One direction only, the one that counters the measured Dec drift, switching when the drift reverses; both
    /// directions while no clear drift is measured, while settling and while a large error on the other side is brought
    /// back (not in PHD2; see <see cref="Algorithms.DecDirectionPolicy"/>).
    /// </summary>
    Drift,
}

/// <summary>The Dec direction(s) guided in <see cref="DecGuideMode.Drift"/>.</summary>
public enum DecGuideDirection
{
    /// <summary>Both directions: no clear drift (not yet, or not any more).</summary>
    Both,

    /// <summary>North pulses only (the drift pushes the other way).</summary>
    North,

    /// <summary>South pulses only.</summary>
    South,
}

public static class GuideDirectionExtensions
{
    public static GuideAxis Axis(this GuideDirection d) => d is GuideDirection.North or GuideDirection.South ? GuideAxis.Dec : GuideAxis.Ra;

    public static GuideDirection Opposite(this GuideDirection d) => d switch
    {
        GuideDirection.North => GuideDirection.South,
        GuideDirection.South => GuideDirection.North,
        GuideDirection.East => GuideDirection.West,
        _ => GuideDirection.East,
    };

    public static PierSide Opposite(this PierSide p) => p switch
    {
        PierSide.East => PierSide.West,
        PierSide.West => PierSide.East,
        _ => PierSide.Unknown,
    };
}
