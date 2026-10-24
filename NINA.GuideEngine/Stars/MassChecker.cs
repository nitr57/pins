// SPDX-License-Identifier: MPL-2.0 AND BSD-3-Clause
// Copyright (c) 2006-2010 Craig Stark.
// Copyright (c) 2012 Bret McKee
// Copyright (c) 2020 Bruce Waddington
// Ported from PHD2 src/guider_multistar.cpp (MassChecker) (a6c02722)

using NINA.GuideEngine.Imaging;

namespace NINA.GuideEngine.Stars;

/// <summary>Limits computed by <see cref="MassChecker.CheckMass"/> (PHD2 limits[4]).</summary>
public readonly record struct MassLimits(double Low, double Median, double High, double Spike);

/// <summary>
/// Detects a sudden change of star mass (wrong star, clouds...). Keeps the masses of the last
/// time window, and rejects a mass below <c>low·(1−t)</c>, above <c>high·(1+t)</c> or above
/// <c>median·(1+2t)</c>. Time comes from the caller (no wall clock).
/// </summary>
public sealed class MassChecker
{
    /// <summary>PHD2 DefaultTimeWindowMs; the effective window is twice this value (45 s).</summary>
    public const int DefaultTimeWindowMs = 22500;

    /// <summary>PHD2 DefaultMassChangeThreshold.</summary>
    public const double DefaultThreshold = 0.5;

    private readonly Queue<(long TimeMs, double Mass)> data = new();
    private double[] tmp = new double[64];
    private double highMass; // high-water mark
    private double lowMass = 9e99; // low-water mark
    private long timeWindow;
    private double exposure;
    private bool isAutoExposure;

    public MassChecker()
    {
        SetTimeWindow(DefaultTimeWindowMs);
    }

    public int Count => data.Count;

    /// <summary>An abrupt change in mass affects the median after approx. half the window, so the window is doubled.</summary>
    public void SetTimeWindow(int milliseconds) => timeWindow = (long)milliseconds * 2;

    /// <summary>
    /// Sets the exposure; with auto exposure masses are normalised by exposure. A change of exposure
    /// without auto exposure (or a change of the auto-exposure flag) resets the history.
    /// </summary>
    public void SetExposure(double exposureMs, bool isAutoExposure)
    {
        if (isAutoExposure != this.isAutoExposure)
        {
            this.isAutoExposure = isAutoExposure;
            exposure = exposureMs;
            Reset();
        }
        else if (exposureMs != exposure)
        {
            exposure = exposureMs;
            if (!this.isAutoExposure)
                Reset();
        }
    }

    public double AdjustedMass(double mass) => isAutoExposure ? mass / exposure : mass;

    /// <summary>Appends a mass measured at <paramref name="timeMs"/> (monotonic ms) and drops old entries.</summary>
    public void AppendData(double mass, long timeMs)
    {
        long oldest = timeMs - timeWindow;
        while (data.Count > 0 && data.Peek().TimeMs < oldest)
            data.Dequeue();
        data.Enqueue((timeMs, AdjustedMass(mass)));
    }

    /// <summary>Returns true when <paramref name="mass"/> should be rejected. Needs at least 5 samples.</summary>
    public bool CheckMass(double mass, double threshold, out MassLimits limits)
    {
        limits = default;
        if (data.Count < 5)
            return false;

        if (tmp.Length < data.Count)
            tmp = new double[data.Count + 10];

        int n = 0;
        foreach (var e in data)
            tmp[n++] = e.Mass;

        int mid = n / 2;
        double med = ImageMath.NthElement(tmp.AsSpan(0, n), mid);

        if (med > highMass)
            highMass = med;
        if (med < lowMass)
            lowMass = med;

        // let the low water mark drift to follow the median so that it moves back up after a
        // period of intermittent clouds has brought it down
        lowMass += .05 * (med - lowMass);

        double l0 = lowMass * (1.0 - threshold);
        double l1 = med;
        double l2 = highMass * (1.0 + threshold);
        // when mass is depressed by sky conditions, we still want to trigger a rejection when
        // there is a large spike in mass, even if it is still below the high water mark-based threshold
        double l3 = med * (1.0 + 2.0 * threshold);

        double adjmass = AdjustedMass(mass);
        bool reject = adjmass < l0 || adjmass > l2 || adjmass > l3;

        if (reject && isAutoExposure)
        {
            // convert back to mass-like numbers for logging by caller
            l0 *= exposure;
            l1 *= exposure;
            l2 *= exposure;
            l3 *= exposure;
        }

        limits = new MassLimits(l0, l1, l2, l3);
        return reject;
    }

    public void Reset()
    {
        data.Clear();
        highMass = 0.0;
        lowMass = 9e99;
    }
}
