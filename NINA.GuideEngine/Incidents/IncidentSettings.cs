// SPDX-License-Identifier: MPL-2.0

namespace NINA.GuideEngine.Incidents;

/// <summary>Flight recorder settings (docs/INCIDENTS.md §2), part of <see cref="Guiding.GuiderSettings.Incidents"/>.</summary>
public sealed record IncidentSettings
{
    /// <summary>Record incidents. Off: nothing is buffered.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Frames kept before a trigger, seconds.</summary>
    public double PreSeconds { get; init; } = 120;

    /// <summary>Frames kept after the recovery (and after a manual mark), seconds.</summary>
    public double PostSeconds { get; init; } = 30;

    /// <summary>Longest window of one occurrence (before + after its trigger), seconds; then it ends with <see cref="IncidentEndReason.Cap"/>.</summary>
    public double MaxSeconds { get; init; } = 300;

    /// <summary>A trigger of the same kind within this time after an incident closed reopens it, seconds.</summary>
    public double RepeatSeconds { get; init; } = 600;

    /// <summary>Consecutive calm frames (star found, error under <see cref="RecoveryRmsFactor"/> × RMS) that count as recovered.</summary>
    public int RecoveryFrames { get; init; } = 10;

    public double RecoveryRmsFactor { get; init; } = 2;

    /// <summary>A spike is a guide frame whose total error exceeds this many rolling RMS ...</summary>
    public double SpikeFactor { get; init; } = 4;

    /// <summary>... and this share of the imaging pixel scale ...</summary>
    public double SpikeImagingFraction { get; init; } = 0.5;

    /// <summary>... or this many arcsec when the imaging scale is unknown.</summary>
    public double SpikeFallbackArcsec { get; init; } = 1.5;

    /// <summary>Guide frames of the rolling RMS.</summary>
    public int SpikeWindow { get; init; } = 50;

    /// <summary>Guide frames without spike detection after guiding started or resumed.</summary>
    public int SpikeWarmupFrames { get; init; } = 20;
}
