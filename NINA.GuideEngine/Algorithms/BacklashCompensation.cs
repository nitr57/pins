// SPDX-License-Identifier: MPL-2.0 AND BSD-3-Clause
// Copyright (c) 2015 Bruce Waddington and Andy Galasso
// Ported from PHD2 src/backlash_comp.cpp (BacklashComp, BLCHistory, BLCEvent) (a6c02722)

using NINA.GuideEngine.Core;

namespace NINA.GuideEngine.Algorithms;

/// <summary>One recorded backlash-compensation event (PHD2 <c>BLCEvent</c>).</summary>
/// <param name="Corrections">[0] = trigger deflection, [1] = first follow-on miss, [2] = optional second miss.</param>
public sealed record BacklashEvent(IReadOnlyList<(long TimeSeconds, double Miss)> Corrections, bool InitialOvershoot, bool InitialUndershoot, bool StictionSeen);

/// <summary>
/// A Dec backlash pulse the adaptive compensation arrived at (<see cref="PulseMs"/>), and the configured pulse it started
/// from (<see cref="SetPulseMs"/>). PHD2 writes the adjusted pulse over its setting; here the setting stays, and the
/// learned pulse is used only while the setting is still the one it was learned from.
/// </summary>
public sealed record BacklashState(int SetPulseMs, int PulseMs);

/// <summary>
/// Dec backlash compensation (port of PHD2 <c>BacklashComp</c>). When the Dec direction reverses on a
/// guide move, a fixed extra pulse is added. In adaptive mode (ceiling − floor ≥ 20 ms) the follow-on
/// deflections are tracked and the pulse is increased by at most 10 % or decreased by at most 20 %
/// per adjustment, bounded by floor and ceiling.
/// </summary>
/// <remarks>
/// Call order per guide step (as in PHD2 <c>Mount::MoveOffset</c>): <see cref="TrackResults"/> with the
/// raw Dec offset before the algorithm runs, then <see cref="Apply"/> with the algorithm's Dec
/// distance and the requested pulse duration.
/// </remarks>
public sealed class BacklashCompensation
{
    /// <summary>Minimum pulse (ms); small enough to effectively disable BLC.</summary>
    public const int MinCompAmount = 20;

    /// <summary>Maximum pulse (ms).</summary>
    public const int MaxCompAmount = 8000;

    private readonly PulseLimiter? limiter;
    private readonly BlcHistory history = new();
    private bool compActive;
    private GuideDirection? lastDirection;
    private int adjustmentFloor;
    private int adjustmentCeiling;
    private int pulseWidth;
    private bool fixedSize;
    private long? timeBase;

    /// <param name="pulseWidthMs">Initial compensation pulse (ms).</param>
    /// <param name="floorMs">Adaptive floor; invalid values become 20.</param>
    /// <param name="ceilingMs">Adaptive ceiling; values below the pulse become min(1.5 × pulse, 8000).</param>
    /// <param name="enabled">Enabled; forced off when <paramref name="pulseWidthMs"/> is 0 (PHD2 load behaviour).</param>
    /// <param name="limiter">Optional limiter whose Dec max duration is raised to the pulse width when needed.</param>
    public BacklashCompensation(int pulseWidthMs = 0, int floorMs = 0, int ceilingMs = 0, bool enabled = false, PulseLimiter? limiter = null)
    {
        this.limiter = limiter;
        compActive = pulseWidthMs > 0 && enabled;
        SetCompValues(pulseWidthMs, floorMs, ceilingMs);
        lastDirection = null;
    }

    public bool IsEnabled => compActive;

    public int PulseWidthMs => pulseWidth;

    public int FloorMs => adjustmentFloor;

    public int CeilingMs => adjustmentCeiling;

    /// <summary>True when floor and ceiling are within 20 ms of each other (no adaptive adjustment).</summary>
    public bool IsFixedSize => fixedSize;

    /// <summary>Last Dec direction seen by <see cref="Apply"/> (null = none).</summary>
    public GuideDirection? LastDirection => lastDirection;

    /// <summary>True while follow-on deflections of the last compensation are being recorded.</summary>
    public bool TrackingWindowOpen => history.WindowOpen;

    /// <summary>Number of adaptive pulse adjustments made so far.</summary>
    public int AdjustmentCount { get; private set; }

    /// <summary>Recent compensation events (up to 10), oldest first.</summary>
    public IReadOnlyList<BacklashEvent> History => history.Snapshot();

    public static int PulseMinValue => MinCompAmount;

    public static int PulseMaxValue => MaxCompAmount;

    /// <summary>The ceiling of the adaptive range for a pulse when none is set: 1.5 × the pulse, at most 8000 ms.</summary>
    public static int DefaultCeiling(int pulseMs) => (int)Math.Min(1.50 * pulseMs, MaxCompAmount);

    private void SetCompValues(int requestedSize, int floor, int ceiling)
    {
        pulseWidth = Math.Clamp(requestedSize, 0, MaxCompAmount);

        // Deviation from PHD2: PHD2 compares the int floor with an unsigned constant, so a negative floor
        // slips through unchanged; here it becomes the 20 ms minimum like any other floor below 20.
        if (floor > pulseWidth || floor < MinCompAmount) // Coming from GA or user input makes no sense
            adjustmentFloor = MinCompAmount;
        else
            adjustmentFloor = floor;
        if (ceiling < pulseWidth)
            adjustmentCeiling = DefaultCeiling(pulseWidth);
        else
            adjustmentCeiling = Math.Min(ceiling, MaxCompAmount);
        fixedSize = Math.Abs(adjustmentCeiling - adjustmentFloor) < MinCompAmount;
        if (limiter is not null && pulseWidth > limiter.MaxDecDurationMs && compActive)
            limiter.SetMaxDecDuration(pulseWidth);
    }

    /// <summary>Sets pulse, floor and ceiling (ceiling 0 = compute default). Clears history on changes &gt; 100 ms.</summary>
    public void SetPulseWidth(int ms, int floor, int ceiling)
    {
        if (pulseWidth != ms || adjustmentFloor != floor || adjustmentCeiling != ceiling)
        {
            int oldBlc = pulseWidth;
            SetCompValues(ms, floor, ceiling);
            if (Math.Abs(pulseWidth - oldBlc) > 100)
            {
                history.ClearHistory();
                history.CloseWindow();
            }
        }
    }

    public void Enable(bool enable)
    {
        if (compActive != enable && enable)
        {
            // Deviation from PHD2: PHD2 calls ResetBLCState() here while m_compActive is still false, which
            // makes the intended reset a no-op; we actually forget the stale direction.
            compActive = true;
            ResetState();
        }

        compActive = enable;
    }

    /// <summary>Forgets the last direction (PHD2 <c>ResetBLCState</c>; called on guiding stop).</summary>
    public void ResetState()
    {
        if (compActive)
        {
            lastDirection = null;
            history.CloseWindow();
        }
    }

    /// <summary>
    /// Records the raw Dec offset resulting from prior moves and adapts the pulse if needed
    /// (PHD2 <c>TrackBLCResults</c>).
    /// </summary>
    /// <param name="options">Options of the move about to be made.</param>
    /// <param name="yRawOffset">Raw Dec mount offset (px) before the guide algorithm.</param>
    /// <param name="decMinMove">Dec algorithm min-move (negative = none, treated as 0).</param>
    /// <param name="yRate">Calibrated Dec rate, px/ms.</param>
    /// <param name="now">Timestamp, only recorded in the history.</param>
    public void TrackResults(MoveOptions options, double yRawOffset, double decMinMove, double yRate, DateTimeOffset now = default)
    {
        if (!compActive)
            return;

        if ((options & MoveOptions.UseBlc) == 0)
        {
            // Calibration-type move that can move mount in Dec w/out notifying blc about direction
            ResetState();
            return;
        }

        // only track algorithm result moves, do not track "fast recovery after dither" moves or deduced
        // moves or AO bump moves
        bool isAlgoResultMove = (options & MoveOptions.AlgoResult) != 0;
        if (!isAlgoResultMove)
        {
            // non-algo blc move occurred before follow-up data were acquired for previous blc
            history.CloseWindow();
            return;
        }

        if (!history.WindowOpen || fixedSize)
            return;

        // Record the history even if residual error is zero. Sign convention has nothing to do with N or S
        // direction - only whether we needed more correction (+) or less (-)
        GuideDirection dir = yRawOffset > 0.0 ? GuideDirection.South : GuideDirection.North;
        double yDistance = Math.Abs(yRawOffset);
        double miss = dir == lastDirection ? yDistance : -yDistance;

        double minMove = Math.Max(decMinMove, 0.0); // Algo w/ no min-move returns -1

        history.AddDeflection(Seconds(now), miss, minMove);

        if (!history.AdjustmentNeeded(miss, minMove, yRate, out double adjustment))
            return;

        int newBlc;
        double nominalBlc = pulseWidth + adjustment;
        if (nominalBlc > pulseWidth)
        {
            newBlc = Round(Math.Min(pulseWidth * 1.1, nominalBlc));
            if (newBlc > adjustmentCeiling)
                newBlc = adjustmentCeiling;
        }
        else
        {
            newBlc = Round(Math.Max(0.8 * pulseWidth, nominalBlc));
            if (newBlc < adjustmentFloor)
                newBlc = adjustmentFloor;
        }

        AdjustmentCount++;
        SetCompValues(newBlc, adjustmentFloor, adjustmentCeiling);
    }

    /// <summary>
    /// Adds the compensation pulse to <paramref name="yAmountMs"/> when the Dec direction reverses
    /// (PHD2 <c>ApplyBacklashComp</c>). Returns the amount added (0 or the pulse width).
    /// </summary>
    /// <param name="options">Move options.</param>
    /// <param name="yGuideDistance">Dec distance after the algorithm (px); sign selects the direction.</param>
    /// <param name="yAmountMs">Requested Dec pulse duration, updated in place.</param>
    /// <param name="now">Timestamp, only recorded in the history.</param>
    public int Apply(MoveOptions options, double yGuideDistance, ref int yAmountMs, DateTimeOffset now = default)
    {
        if ((options & MoveOptions.UseBlc) == 0)
            return 0;
        if (!compActive || pulseWidth <= 0 || yGuideDistance == 0.0)
            return 0;

        int added = 0;
        GuideDirection dir = yGuideDistance > 0.0 ? GuideDirection.South : GuideDirection.North;
        bool isAlgoResultMove = (options & MoveOptions.AlgoResult) != 0;

        if (lastDirection is not null && dir != lastDirection)
        {
            yAmountMs += pulseWidth;
            added = pulseWidth;

            if (isAlgoResultMove)
            {
                // Only track results or make adjustments for algorithm-controlled blc's
                history.RecordNewBlcPulse(Seconds(now), yGuideDistance);
            }
            else
            {
                history.CloseWindow();
            }
        }

        lastDirection = dir;
        return added;
    }

    private long Seconds(DateTimeOffset now)
    {
        long s = now.ToUnixTimeSeconds();
        timeBase ??= s;
        return s - timeBase.Value;
    }

    private static int Round(double x) => (int)Math.Floor(x + 0.5);

    private sealed class BlcEvent
    {
        public readonly List<(long TimeSeconds, double Miss)> Corrections = new(3);
        public bool InitialOvershoot;
        public bool InitialUndershoot;
        public bool StictionSeen;

        public BlcEvent(long timeSecs, double amount)
        {
            Corrections.Add((timeSecs, amount));
        }

        public int InfoCount => Corrections.Count;

        public void AddEventInfo(long timeSecs, double amount, double minMove)
        {
            // Correction[0] is the deflection that triggered the BLC in the first place. Correction[1] is the
            // first delta after the pulse was issued, Correction[2] is the (optional) subsequent delta, needed
            // to detect stiction
            if (InfoCount < 3)
            {
                Corrections.Add((timeSecs, amount)); // Regardless of size relative to min-move
                if (Math.Abs(amount) > minMove)
                {
                    if (InfoCount == 2)
                    {
                        if (amount > 0)
                            InitialUndershoot = true;
                        else
                            InitialOvershoot = true;
                    }
                    else if (InfoCount == 3)
                    {
                        StictionSeen = amount < 0 && Corrections[1].Miss > 0; // 2nd follow-on miss was an over-shoot
                    }
                }
            }
        }
    }

    private struct RecentStats
    {
        public int ShortCount;
        public int LongCount;
        public int StictionCount;
        public double AvgInitialMiss;
        public double AvgStictionAmount;
    }

    private sealed class BlcHistory
    {
        private const int EntryCapacity = 3;
        private const int HistoryDepth = 10;
        private readonly List<BlcEvent> blcEvents = new();
        private int blcIndex;

        public bool WindowOpen { get; private set; }

        public IReadOnlyList<BacklashEvent> Snapshot() => blcEvents
            .Select(e => new BacklashEvent(e.Corrections.ToArray(), e.InitialOvershoot, e.InitialUndershoot, e.StictionSeen))
            .ToArray();

        public void CloseWindow() => WindowOpen = false;

        public void RecordNewBlcPulse(long when, double triggerDeflection)
        {
            if (blcEvents.Count >= HistoryDepth)
                blcEvents.RemoveAt(0);
            blcEvents.Add(new BlcEvent(when, triggerDeflection));
            blcIndex = blcEvents.Count - 1;
            WindowOpen = true;
        }

        public bool AddDeflection(long when, double amt, double minMove)
        {
            if (blcIndex >= 0 && blcIndex < blcEvents.Count && blcEvents[blcIndex].InfoCount < EntryCapacity)
            {
                blcEvents[blcIndex].AddEventInfo(when, amt, minMove);
                return true;
            }

            CloseWindow();
            return false;
        }

        private void RemoveOldestOvershoots(int howMany)
        {
            for (int ct = 1; ct <= howMany; ct++)
            {
                for (int inx = 0; inx < blcEvents.Count - 1; inx++)
                {
                    if (blcEvents[inx].InitialOvershoot)
                    {
                        blcEvents.RemoveAt(inx);
                        blcIndex = blcEvents.Count - 1;
                        break;
                    }
                }
            }
        }

        private void RemoveOldestStictions(int howMany)
        {
            for (int ct = 1; ct <= howMany; ct++)
            {
                for (int inx = 0; inx < blcEvents.Count - 1; inx++)
                {
                    if (blcEvents[inx].StictionSeen)
                    {
                        blcEvents.RemoveAt(inx);
                        blcIndex = blcEvents.Count - 1;
                        break;
                    }
                }
            }
        }

        public void ClearHistory()
        {
            blcEvents.Clear();
            CloseWindow();
        }

        // Stats over some number of recent events, returns the average initial miss
        private double GetStats(int numEvents, ref RecentStats results)
        {
            int bottom = Math.Max(0, blcIndex - (numEvents - 1));
            double sum = 0;
            double stictionSum = 0;
            int ct = 0;
            for (int inx = blcIndex; inx >= bottom; inx--)
            {
                var evt = blcEvents[inx];
                if (evt.InitialOvershoot)
                    results.LongCount++;
                else
                    results.ShortCount++;
                if (evt.StictionSeen)
                {
                    results.StictionCount++;
                    stictionSum += evt.Corrections[2].Miss;
                }

                // Average only the initial misses immediately following the blcs
                if (evt.InfoCount > 1)
                {
                    sum += evt.Corrections[1].Miss;
                    ct++;
                }
            }

            results.AvgInitialMiss = ct > 0 ? sum / ct : 0;
            results.AvgStictionAmount = results.StictionCount > 0 ? stictionSum / results.StictionCount : 0;
            return results.AvgInitialMiss;
        }

        public bool AdjustmentNeeded(double miss, double minMove, double yRate, out double correction)
        {
            bool adjust = false;
            var stats = default(RecentStats);
            correction = 0;
            if (blcIndex < 0 || blcIndex >= blcEvents.Count)
                return false;

            double avgInitMiss = GetStats(HistoryDepth, ref stats);
            var currEvent = blcEvents[blcIndex];

            if (Math.Abs(miss) >= minMove) // Most recent miss was big enough to look at
            {
                int corr = (int)(Math.Floor(Math.Abs(avgInitMiss) / yRate) + 0.5); // unsigned correction value
                if (miss > 0)
                {
                    // UNDER-SHOOT
                    if (avgInitMiss > 0)
                    {
                        // Might want to increase the blc value - but check for stiction and history of
                        // over-corrections. Don't make any changes before getting two follow-on displacements
                        // after last BLC
                        if (currEvent.InfoCount == EntryCapacity)
                        {
                            if (stats.StictionCount > 2)
                            {
                                // Under-shoot, no adjustment because of stiction history
                            }
                            else if (stats.LongCount >= 2)
                            {
                                // Under-shoot; no adjustment because of over-shoot history
                            }
                            else
                            {
                                adjust = true;
                                correction = corr;
                            }
                        }
                    }
                    else
                    {
                        // Under-shoot, no adjustment, avgInitialMiss <= 0
                        CloseWindow();
                    }
                }
                else
                {
                    // OVER-SHOOT, miss < 0
                    if (currEvent.StictionSeen)
                    {
                        if (stats.StictionCount > 1) // Seeing and low min-move can look like stiction, don't over-react
                        {
                            double stictionCorr = (int)(Math.Floor(Math.Abs(stats.AvgStictionAmount) / yRate) + 0.5);
                            correction = -stictionCorr;
                            RemoveOldestStictions(1);
                            adjust = true;
                        }
                    }
                    else if (stats.LongCount > stats.ShortCount && blcIndex >= 4)
                    {
                        // Recent history of over-shoots
                        correction = -corr;
                        RemoveOldestOvershoots(2);
                        adjust = true;
                    }
                    else if (avgInitMiss <= -0.1)
                    {
                        // Average miss indicates over-shooting
                        correction = -corr;
                        adjust = true;
                    }
                    else
                    {
                        // Over-shoot, no adjustment based on avgInitialMiss
                        CloseWindow();
                    }
                }
            }

            if (adjust)
                CloseWindow();
            return adjust;
        }
    }
}
