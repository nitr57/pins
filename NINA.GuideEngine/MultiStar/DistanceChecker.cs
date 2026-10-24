// SPDX-License-Identifier: MPL-2.0 AND BSD-3-Clause
// Copyright (c) 2012 Bret McKee
// Copyright (c) 2020 Bruce Waddington
// Ported from PHD2 src/guider_multistar.cpp (DistanceChecker) and src/guider.cpp
// (UpdateCurrentDistance, CurrentError) (a6c02722)

namespace NINA.GuideEngine.MultiStar;

/// <summary>
/// Moving averages of the guide-star distance from the lock position (PHD2 Guider m_avgDistance*):
/// a fast EMA (α = 0.3) and a heavily smoothed one (mean of the first 10 samples, then α = 0.045).
/// Used by the jump filter and useful for settling.
/// </summary>
public sealed class DistanceAverager
{
    /// <summary>Value reported when no star was found for more than 20 s.</summary>
    public const double LargeDistance = 100.0;

    private const int ThresholdSeconds = 20;

    private double avgDistance;
    private double avgDistanceRa;
    private double avgDistanceLong;
    private double avgDistanceLongRa;
    private long? starFoundTimeMs;

    public int FrameCount { get; private set; }

    /// <summary>Set when the average history must be re-initialised (PHD2: leaving a full pause).</summary>
    public bool NeedReset { get; set; }

    /// <summary>PHD2 Guider::UpdateCurrentDistance.</summary>
    public void Update(double distance, double distanceRa, bool isGuiding, long nowMs)
    {
        starFoundTimeMs = nowMs;

        if (isGuiding)
        {
            // update moving average distance
            const double alpha = .3; // moderately high weighting for latest sample
            avgDistance += alpha * (distance - avgDistance);
            avgDistanceRa += alpha * (distanceRa - avgDistanceRa);

            ++FrameCount;

            if (FrameCount < 10)
            {
                // initialize smoothed running avg with mean of first 10 pts
                avgDistanceLong += (distance - avgDistanceLong) / FrameCount;
                avgDistanceLongRa += (distanceRa - avgDistanceLongRa) / FrameCount;
            }
            else
            {
                const double alphaLong = .045; // heavy smoothing, .045 => 15 frame half-life
                avgDistanceLong += alphaLong * (distance - avgDistanceLong);
                avgDistanceLongRa += alphaLong * (distanceRa - avgDistanceLongRa);
            }
        }
        else
        {
            // not yet guiding, reinitialize average distance
            avgDistance = avgDistanceLong = distance;
            avgDistanceRa = avgDistanceLongRa = distanceRa;
            FrameCount = 1;
        }

        if (NeedReset)
        {
            // avg distance history invalidated
            avgDistance = avgDistanceLong = distance;
            avgDistanceRa = avgDistanceLongRa = distanceRa;
            FrameCount = 1;
            NeedReset = false;
        }
    }

    /// <summary>Adds a dither offset right away so the current distance reflects it (PHD2 MoveLockPosition).</summary>
    public void AddDither(double distance, double distanceRa)
    {
        avgDistance += distance;
        avgDistanceLong += distance;
        avgDistanceRa += distanceRa;
        avgDistanceLongRa += distanceRa;
    }

    /// <summary>PHD2 Guider::CurrentError (fast average).</summary>
    public double CurrentError(bool raOnly, long nowMs) => Stale(nowMs) ? LargeDistance : raOnly ? avgDistanceRa : avgDistance;

    /// <summary>PHD2 Guider::CurrentErrorSmoothed (slow average).</summary>
    public double CurrentErrorSmoothed(bool raOnly, long nowMs) => Stale(nowMs) ? LargeDistance : raOnly ? avgDistanceLongRa : avgDistanceLong;

    // PHD2 compares time_t seconds: now - found > 20
    private bool Stale(long nowMs) => starFoundTimeMs is not { } t || Math.Floor(nowMs / 1000.0) - Math.Floor(t / 1000.0) > ThresholdSeconds;
}

/// <summary>
/// "Tolerate jumps" filter (PHD2 DistanceChecker): while guiding, rejects a frame whose distance from
/// the lock position exceeds <c>tolerance × CurrentErrorSmoothed</c>; after 5 s of rejections it
/// enters a recovering state that accepts frames again.
/// </summary>
internal sealed class DistanceChecker
{
    private const int WaitIntervalMs = 5000;
    private const int MinFramesForStats = 10;

    private enum State
    {
        Guiding,
        Waiting,
        Recovering,
    }

    private State state = State.Guiding;
    private long expiresMs;
    private double forceTolerance;

    public void Activate(long nowMs)
    {
        if (state == State.Guiding)
        {
            state = State.Waiting;
            expiresMs = nowMs + WaitIntervalMs;
            forceTolerance = 2.0;
        }
    }

    public void Reset()
    {
        state = State.Guiding;
        forceTolerance = 0;
    }

    private static bool CheckDistanceInner(double distance, bool raOnly, double tolerance, bool isGuiding, bool isPaused, bool isSettling,
        DistanceAverager avg, long nowMs)
    {
        if (!isGuiding || isPaused || isSettling || avg.FrameCount < MinFramesForStats)
            return true;
        double avgDist = avg.CurrentErrorSmoothed(raOnly, nowMs);
        double threshold = tolerance * avgDist;
        return !(distance > threshold);
    }

    public bool CheckDistance(double distance, bool raOnly, double tolerance, bool isGuiding, bool isPaused, bool isSettling,
        DistanceAverager avg, long nowMs)
    {
        if (forceTolerance != 0.0)
            tolerance = forceTolerance;

        bool smallOffset = CheckDistanceInner(distance, raOnly, tolerance, isGuiding, isPaused, isSettling, avg, nowMs);

        switch (state)
        {
            default:
            case State.Guiding:
                if (smallOffset)
                    return true;
                state = State.Waiting;
                expiresMs = nowMs + WaitIntervalMs;
                return false;

            case State.Waiting:
                if (smallOffset)
                {
                    state = State.Guiding;
                    forceTolerance = 0.0;
                    return true;
                }

                // large distance
                if (nowMs < expiresMs)
                    return false; // reject frame

                // timed-out
                state = State.Recovering;
                goto case State.Recovering;

            case State.Recovering:
                if (smallOffset)
                    state = State.Guiding;
                return true;
        }
    }
}
