// SPDX-License-Identifier: MPL-2.0 AND BSD-3-Clause
// Copyright (c) 2013 Andy Galasso. All rights reserved. See THIRD_PARTY_NOTICES.md.
// Ported from PHD2 src/phdcontrol.cpp (STATE_SETTLE_BEGIN / STATE_SETTLE_WAIT) (a6c02722)

namespace NINA.GuideEngine.Guiding;

/// <summary>Settle criteria (PHD2 SettleParams). Frames defaults to "unlimited" as when set via the API.</summary>
public sealed record SettleParams(double TolerancePx, double SettleTimeSec, double TimeoutSec, int Frames = 99999);

public enum SettleOutcome
{
    InProgress,
    Succeeded,
    TimedOut,
}

/// <summary>Per-frame settle progress (PHD2 'Settling' event fields).</summary>
public sealed record SettleProgress(SettleOutcome Outcome, double Distance, double TimeInRangeSec, double SettleTimeSec, bool StarLocked, int TotalFrames, int DroppedFrames);

/// <summary>
/// Settle state machine. Call <see cref="Begin"/> after a dither or guide start, then
/// <see cref="Update"/> once per guide frame with the current guide error.
/// </summary>
public sealed class SettleMonitor
{
    private SettleParams? settle;
    private bool priorFrameInRange;
    private DateTimeOffset inRangeStart;
    private DateTimeOffset timeoutStart;
    private int frameCount;
    private int droppedFrames;

    public bool IsActive => settle is not null;

    public SettleParams? Params => settle;

    public void Begin(SettleParams p, DateTimeOffset now)
    {
        settle = p;
        priorFrameInRange = false;
        frameCount = droppedFrames = 0;
        timeoutStart = now;
    }

    public void Cancel() => settle = null;

    /// <param name="lockedOnStar">Star was found this frame and a lock position exists.</param>
    /// <param name="currentError">Current guide error (PHD2 CurrentError, see MultiStar.DistanceAverager).</param>
    public SettleProgress Update(bool lockedOnStar, double currentError, DateTimeOffset now)
    {
        var s = settle ?? throw new InvalidOperationException("settle not active");
        bool inRange = lockedOnStar && currentError <= s.TolerancePx;
        double timeInRange = 0;

        ++frameCount;
        if (!lockedOnStar)
        {
            ++droppedFrames;
        }

        if (frameCount >= s.Frames)
        {
            return Finish(SettleOutcome.Succeeded, currentError, 0, lockedOnStar);
        }

        if (inRange)
        {
            if (!priorFrameInRange)
            {
                // first frame in range
                if (s.SettleTimeSec <= 0)
                {
                    return Finish(SettleOutcome.Succeeded, currentError, 0, lockedOnStar);
                }

                inRangeStart = now;
            }
            else
            {
                // PHD2 compares whole seconds (integer ms / 1000)
                timeInRange = (now - inRangeStart).TotalMilliseconds;
                if (Math.Floor(timeInRange / 1000.0) >= s.SettleTimeSec)
                {
                    return Finish(SettleOutcome.Succeeded, currentError, timeInRange / 1000.0, lockedOnStar);
                }
            }
        }

        if (Math.Floor((now - timeoutStart).TotalMilliseconds / 1000.0) >= s.TimeoutSec)
        {
            return Finish(SettleOutcome.TimedOut, currentError, timeInRange / 1000.0, lockedOnStar);
        }

        priorFrameInRange = inRange;
        return new SettleProgress(SettleOutcome.InProgress, currentError, timeInRange / 1000.0, s.SettleTimeSec, lockedOnStar, frameCount, droppedFrames);
    }

    private SettleProgress Finish(SettleOutcome outcome, double distance, double timeInRange, bool locked)
    {
        var p = new SettleProgress(outcome, distance, timeInRange, settle!.SettleTimeSec, locked, frameCount, droppedFrames);
        settle = null;
        return p;
    }
}
