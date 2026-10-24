// SPDX-License-Identifier: MPL-2.0 AND BSD-3-Clause
// Copyright (c) 2006-2010 Craig Stark
// Ported from PHD2 src/scope.cpp (Scope::MoveAxis, AlertLimitReached, DeferPulseLimitAlertCheck) (a6c02722)

using NINA.GuideEngine.Core;

namespace NINA.GuideEngine.Algorithms;

/// <summary>Which of PHD2's three "limit reached" messages applies.</summary>
public enum PulseLimitAlertKind
{
    /// <summary>Max duration is at or above the default: likely a mechanical/cabling/calibration problem.</summary>
    InsufficientCorrection,

    /// <summary>The user lowered the max duration below the default; recommend restoring it.</summary>
    MaxDurationTooLow,

    /// <summary>Already at the absolute maximum (8000 ms); corrections cannot keep up.</summary>
    AtAbsoluteMaximum,
}

/// <summary>Alert raised after repeated same-direction clamps on one axis.</summary>
public sealed record PulseLimitAlert(GuideAxis Axis, PulseLimitAlertKind Kind, int DurationMs, string Message);

/// <summary>Result of <see cref="PulseLimiter.Limit"/>.</summary>
public readonly record struct LimitedPulse(GuideDirection Direction, int DurationMs, bool Limited, PulseLimitAlert? Alert);

/// <summary>
/// Max pulse duration clamp per axis and PHD2's "limit reached" alert logic. Time is passed in; no
/// wall clock is read.
/// </summary>
/// <remarks>
/// PHD2 semantics: a clamp in the same direction as the previous clamp increments a counter; once it
/// reaches 5 an alert is attempted on every further clamp. Alerts are throttled to one per 30 s
/// across both axes (the throttle timestamp is updated even when the alert is suppressed by the
/// grace period), and suppressed until the grace period set by <see cref="DeferAlertCheck"/> (120 s)
/// has elapsed. PHD2 starts the grace period when guiding output is re-enabled; the engine also calls it
/// after dithers. Time comparisons use whole seconds like PHD2's <c>time_t</c>.
/// </remarks>
public sealed class PulseLimiter
{
    public const int DefaultMaxDurationMs = 2500;
    public const int MinMaxDurationMs = 50;
    public const int MaxMaxDurationMs = 8000;
    public const int LimitReachedWarnCount = 5;
    public const int AlertThrottleSeconds = 30;
    public const int GracePeriodSeconds = 120;

    private int maxRaDurationMs = DefaultMaxDurationMs;
    private int maxDecDurationMs = DefaultMaxDurationMs;
    private GuideDirection? raLimitReachedDirection;
    private int raLimitReachedCount;
    private GuideDirection? decLimitReachedDirection;
    private int decLimitReachedCount;
    private long? lastAlertSeconds;
    private long deferralSeconds = long.MinValue;

    public int MaxRaDurationMs => maxRaDurationMs;

    public int MaxDecDurationMs => maxDecDurationMs;

    /// <summary>True when pulses go through the mount's pulse-guide interface (affects message text only).</summary>
    public bool CanPulseGuide { get; set; } = true;

    /// <summary>Consecutive same-direction clamp count, RA.</summary>
    public int RaLimitReachedCount => raLimitReachedCount;

    /// <summary>Consecutive same-direction clamp count, Dec.</summary>
    public int DecLimitReachedCount => decLimitReachedCount;

    /// <summary>Sets the RA max duration. Returns true on error (PHD2 convention).</summary>
    /// <remarks>
    /// Deviation from PHD2: PHD2's setter only rejects negative values (→ default) and relies on the UI
    /// range; here values are additionally clamped to 50..8000 ms (reported as error).
    /// </remarks>
    public bool SetMaxRaDuration(int ms) => SetMax(ref maxRaDurationMs, ms);

    /// <summary>Sets the Dec max duration. Returns true on error. See <see cref="SetMaxRaDuration"/>.</summary>
    public bool SetMaxDecDuration(int ms) => SetMax(ref maxDecDurationMs, ms);

    private static bool SetMax(ref int field, int ms)
    {
        if (ms < 0)
        {
            field = DefaultMaxDurationMs;
            return true;
        }

        int clamped = Math.Clamp(ms, MinMaxDurationMs, MaxMaxDurationMs);
        field = clamped;
        return clamped != ms;
    }

    /// <summary>Suppresses limit alerts for 120 s from <paramref name="now"/>.</summary>
    public void DeferAlertCheck(DateTimeOffset now)
    {
        deferralSeconds = now.ToUnixTimeSeconds() + GracePeriodSeconds;
    }

    /// <summary>Clears the consecutive-clamp counters (not the alert throttle).</summary>
    public void ResetCounters()
    {
        raLimitReachedDirection = null;
        raLimitReachedCount = 0;
        decLimitReachedDirection = null;
        decLimitReachedCount = 0;
    }

    /// <summary>
    /// Clamps an algorithm/deduced pulse to the axis maximum and runs the alert logic. Moves that are
    /// not algorithm/deduced moves pass through untouched and do not affect the counters.
    /// </summary>
    public LimitedPulse Limit(GuideDirection direction, int durationMs, DateTimeOffset now, bool isAlgorithmOrDeducedMove = true)
    {
        if (!isAlgorithmOrDeducedMove)
            return new LimitedPulse(direction, durationMs, false, null);

        bool limitReached = false;
        PulseLimitAlert? alert = null;

        if (direction.Axis() == GuideAxis.Dec)
        {
            if (durationMs > maxDecDurationMs)
            {
                durationMs = maxDecDurationMs;
                limitReached = true;
            }

            if (limitReached && direction == decLimitReachedDirection)
            {
                if (++decLimitReachedCount >= LimitReachedWarnCount)
                    alert = AlertLimitReached(durationMs, GuideAxis.Dec, now);
            }
            else
            {
                decLimitReachedCount = 0;
            }

            decLimitReachedDirection = limitReached ? direction : null;
        }
        else
        {
            if (durationMs > maxRaDurationMs)
            {
                durationMs = maxRaDurationMs;
                limitReached = true;
            }

            if (limitReached && direction == raLimitReachedDirection)
            {
                if (++raLimitReachedCount >= LimitReachedWarnCount)
                    alert = AlertLimitReached(durationMs, GuideAxis.Ra, now);
            }
            else
            {
                raLimitReachedCount = 0;
            }

            raLimitReachedDirection = limitReached ? direction : null;
        }

        return new LimitedPulse(direction, durationMs, limitReached, alert);
    }

    private PulseLimitAlert? AlertLimitReached(int duration, GuideAxis axis, DateTimeOffset now)
    {
        long nowSec = now.ToUnixTimeSeconds();
        if (lastAlertSeconds is long last && nowSec < last + AlertThrottleSeconds)
            return null;

        lastAlertSeconds = nowSec;

        if (nowSec < deferralSeconds)
            return null;

        string axisName = axis == GuideAxis.Ra ? "RA" : "Dec";
        if (duration < MaxMaxDurationMs)
        {
            if (duration >= DefaultMaxDurationMs)
            {
                string msg;
                if (axis == GuideAxis.Ra)
                {
                    msg = CanPulseGuide
                        ? "The guider is not able to make sufficient corrections in RA. Check for cable snags, try re-doing your calibration, and check for problems with the mount mechanics."
                        : "The guider is not able to make sufficient corrections in RA. Check for cable snags, try re-doing your calibration, and confirm the ST-4 cable is working properly.";
                }
                else
                {
                    msg = CanPulseGuide
                        ? "The guider is not able to make sufficient corrections in Dec. If the side-of-pier has changed from where you last calibrated, check whether the reverse Dec output setting is wrong. If so, fix it and recalibrate. Otherwise, check for cable snags, try re-doing your calibration, and check for problems with the mount mechanics."
                        : "The guider is not able to make sufficient corrections in Dec. Check for cable snags, try re-doing your calibration and confirm the ST-4 cable is working properly.";
                }

                return new PulseLimitAlert(axis, PulseLimitAlertKind.InsufficientCorrection, duration, msg);
            }

            string s = $"Max {axisName} Duration setting";
            return new PulseLimitAlert(
                axis,
                PulseLimitAlertKind.MaxDurationTooLow,
                duration,
                $"Your {s} is preventing the guider from making adequate corrections to keep the guide star locked. Try restoring {s} to its default value to allow larger corrections.");
        }

        return new PulseLimitAlert(
            axis,
            PulseLimitAlertKind.AtAbsoluteMaximum,
            duration,
            $"Even using the maximum moves, the guider can't properly correct for the large guide star movements in {axisName}. Guiding will be impaired until you can eliminate the source of these problems.");
    }
}
