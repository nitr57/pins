// SPDX-License-Identifier: MPL-2.0 AND BSD-3-Clause
// Copyright (c) 2008-2010 Craig Stark
// Copyright (c) 2013 Bret McKee
// Copyright (c) 2015 Andy Galasso and Bruce Waddington
// Ported from PHD2 src/graph.cpp (UpdateStats, AppendData) and src/guiding_assistant.cpp (polar alignment
// error) (a6c02722); drift reconstruction, duty, SNR and session totals are original.

using NINA.GuideEngine.Algorithms;

namespace NINA.GuideEngine.Stats;

/// <summary>One guide step as seen by <see cref="GuidingStatistics"/>.</summary>
public sealed record GuideStepSample
{
    /// <summary>Time the frame was taken (or processed).</summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>RA mount-axis offset (px), PHD2 <c>RADistanceRaw</c>.</summary>
    public double RaPx { get; init; }

    /// <summary>Dec mount-axis offset (px), PHD2 <c>DECDistanceRaw</c>.</summary>
    public double DecPx { get; init; }

    /// <summary>
    /// Correction applied after this frame (px), signed so that a positive value reduces a positive
    /// offset (<see cref="GuideCorrection.RaCorrectionPx"/>). Used to reconstruct the uncorrected drift.
    /// </summary>
    public double RaCorrectionPx { get; init; }

    /// <summary>Dec counterpart of <see cref="RaCorrectionPx"/>.</summary>
    public double DecCorrectionPx { get; init; }

    public int RaDurationMs { get; init; }

    public int DecDurationMs { get; init; }

    public bool RaLimited { get; init; }

    public bool DecLimited { get; init; }

    /// <summary>Primary star SNR, if known.</summary>
    public double? Snr { get; init; }

    /// <summary>Number of stars used (multi-star), if known.</summary>
    public int? StarCount { get; init; }

    /// <summary>
    /// True for frames during dithering or settling. They are excluded from RMS, peak, drift, duty and
    /// SNR statistics (PHD2 <c>m_noDither*</c>) but count as frames and for the oscillation index.
    /// </summary>
    public bool Excluded { get; init; }

    /// <summary>Builds a sample from a correction result.</summary>
    public static GuideStepSample FromCorrection(GuideCorrection c, DateTimeOffset timestamp, double? snr = null, int? starCount = null, bool excluded = false) => new()
    {
        Timestamp = timestamp,
        RaPx = c.RADistanceRaw,
        DecPx = c.DECDistanceRaw,
        RaCorrectionPx = c.RaCorrectionPx,
        DecCorrectionPx = c.DecCorrectionPx,
        RaDurationMs = c.RADuration,
        DecDurationMs = c.DECDuration,
        RaLimited = c.RALimited,
        DecLimited = c.DecLimited,
        Snr = snr,
        StarCount = starCount,
        Excluded = excluded,
    };
}

/// <summary>Statistics over a set of guide steps (a window or the whole session).</summary>
public sealed record GuidingStatsBlock
{
    /// <summary>Frames considered (including excluded dither/settle frames).</summary>
    public int Frames { get; init; }

    /// <summary>Frames used for RMS/peak/drift/duty/SNR (excluded frames removed).</summary>
    public int IncludedFrames { get; init; }

    /// <summary>RA RMS: population σ about the mean (px).</summary>
    public double RmsRaPx { get; init; }

    public double RmsDecPx { get; init; }

    /// <summary>hypot(RA RMS, Dec RMS) (px).</summary>
    public double RmsTotalPx { get; init; }

    public double RmsRaArcsec { get; init; }

    public double RmsDecArcsec { get; init; }

    public double RmsTotalArcsec { get; init; }

    /// <summary>Maximum |RA offset| (px).</summary>
    public double PeakRaPx { get; init; }

    public double PeakDecPx { get; init; }

    public double PeakRaArcsec { get; init; }

    public double PeakDecArcsec { get; init; }

    /// <summary>Uncorrected RA drift (px/min, linear fit); null with fewer than 2 points or no time span.</summary>
    public double? RaDriftPxPerMin { get; init; }

    public double? DecDriftPxPerMin { get; init; }

    public double? RaDriftArcsecPerMin { get; init; }

    public double? DecDriftArcsecPerMin { get; init; }

    /// <summary>Polar alignment error estimate (arcmin): 3.8197·|Dec drift px/min|·scale/cos(dec).</summary>
    public double? PolarAlignmentErrorArcmin { get; init; }

    /// <summary>True when the declination was unknown and 0° was assumed (the PAE is then a lower bound).</summary>
    public bool PolarAlignmentDecAssumed { get; init; }

    /// <summary>RA oscillation index: 1 − sameSideCount/(n−1).</summary>
    public double OscillationIndex { get; init; }

    /// <summary>PHD2 alert: index &gt; 0.6 or &lt; 0.15 (only with ≥ 2 frames).</summary>
    public bool OscillationAlert { get; init; }

    /// <summary>Percent of included frames with a non-zero RA pulse.</summary>
    public double RaDutyPercent { get; init; }

    public double DecDutyPercent { get; init; }

    public double? SnrMin { get; init; }

    public double? SnrAvg { get; init; }

    public double? SnrLast { get; init; }

    public double? StarCountAvg { get; init; }

    /// <summary>Frames whose RA pulse was clamped by the max duration.</summary>
    public int RaLimitedCount { get; init; }

    public int DecLimitedCount { get; init; }
}

/// <summary>Immutable statistics snapshot for API/UI.</summary>
public sealed record GuidingStatsSnapshot
{
    public required GuidingStatsBlock Window { get; init; }

    /// <summary>Cumulative since <see cref="GuidingStatistics.Start"/>.</summary>
    public required GuidingStatsBlock Session { get; init; }

    /// <summary>Configured window length (frames).</summary>
    public int WindowSize { get; init; }

    /// <summary>Image scale used for arcsec values (″/px).</summary>
    public double PixelScale { get; init; }

    public double? DeclinationDeg { get; init; }

    public DateTimeOffset? StartTime { get; init; }

    public DateTimeOffset Timestamp { get; init; }

    public TimeSpan Elapsed { get; init; }

    /// <summary>Guide steps since start (all).</summary>
    public int FrameCount { get; init; }

    /// <summary>Frames with no star since start.</summary>
    public int StarLostCount { get; init; }
}

/// <summary>
/// Rolling and cumulative guiding statistics (PHD2 graph statistics plus drift/PAE, duty and SNR).
/// Not thread-safe; time comes from the samples.
/// </summary>
/// <remarks>
/// RMS and peak follow PHD2 <c>GraphLogClientWindow::UpdateStats</c>: population σ over the last
/// <see cref="WindowSize"/> included frames (PHD2 keeps a separate window of non-dither frames), total =
/// hypot. The oscillation index uses the last <see cref="WindowSize"/> frames of any kind like PHD2.
/// Drift: while guiding, the raw offsets hide the drift, so the uncorrected position is reconstructed as
/// the running sum of (offset change + previous correction) between consecutive included frames and fitted
/// linearly over time; gaps around excluded frames (dither/settle) are bridged without the lock jump.
/// PHD2's Guiding Assistant measures the same quantity with guiding disabled.
/// </remarks>
public sealed class GuidingStatistics
{
    /// <summary>PHD2 default graph length.</summary>
    public const int DefaultWindowSize = 100;

    /// <summary>Oscillation index above this raises an alert (PHD2).</summary>
    public const double OscillationAlertHigh = 0.6;

    /// <summary>Oscillation index below this raises an alert (PHD2).</summary>
    public const double OscillationAlertLow = 0.15;

    /// <summary>Barrett's constant relating Dec drift (px/min·″/px) to polar misalignment (arcmin).</summary>
    public const double PolarAlignmentFactor = 3.8197;

    private readonly Queue<GuideStepSample> allWindow = new();
    private readonly Queue<GuideStepSample> includedWindow = new();
    private WindowedAxisStats winRa;
    private WindowedAxisStats winDec;
    private WindowedAxisStats winDriftRa;
    private WindowedAxisStats winDriftDec;
    private readonly SessionAccumulator session = new();
    private GuideStepSample? previous;
    private double reconRa;
    private double reconDec;
    private int windowSize;

    public GuidingStatistics(int windowSize = DefaultWindowSize, double pixelScale = 1.0)
    {
        if (windowSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(windowSize));
        this.windowSize = windowSize;
        PixelScale = pixelScale;
        winRa = new WindowedAxisStats(windowSize);
        winDec = new WindowedAxisStats(windowSize);
        winDriftRa = new WindowedAxisStats(windowSize);
        winDriftDec = new WindowedAxisStats(windowSize);
    }

    /// <summary>Image scale ″/px used for arcsec values and the PAE.</summary>
    public double PixelScale { get; set; }

    /// <summary>Current declination in degrees; null = unknown (PAE assumes 0° and flags it).</summary>
    public double? DeclinationDeg { get; set; }

    public int WindowSize => windowSize;

    public DateTimeOffset? StartTime { get; private set; }

    public int FrameCount => session.Frames;

    public int StarLostCount { get; private set; }

    /// <summary>Changes the window length, trimming older frames.</summary>
    public void ChangeWindowSize(int frames)
    {
        if (frames <= 0)
            throw new ArgumentOutOfRangeException(nameof(frames));
        windowSize = frames;
        winRa.ChangeWindowSize(frames);
        winDec.ChangeWindowSize(frames);
        winDriftRa.ChangeWindowSize(frames);
        winDriftDec.ChangeWindowSize(frames);
        Trim(allWindow);
        Trim(includedWindow);
    }

    /// <summary>Starts a new session (StartGuiding): clears window and session statistics.</summary>
    public void Start(DateTimeOffset startTime)
    {
        Clear();
        StartTime = startTime;
    }

    /// <summary>Clears all statistics.</summary>
    public void Clear()
    {
        allWindow.Clear();
        includedWindow.Clear();
        winRa = new WindowedAxisStats(windowSize);
        winDec = new WindowedAxisStats(windowSize);
        winDriftRa = new WindowedAxisStats(windowSize);
        winDriftDec = new WindowedAxisStats(windowSize);
        session.Clear();
        previous = null;
        reconRa = 0;
        reconDec = 0;
        StarLostCount = 0;
        StartTime = null;
    }

    /// <summary>Counts a frame where the star was lost.</summary>
    public void AddStarLost(DateTimeOffset timestamp)
    {
        StartTime ??= timestamp;
        StarLostCount++;
    }

    /// <summary>Adds a guide step.</summary>
    public void Add(GuideStepSample s)
    {
        StartTime ??= s.Timestamp;
        double tMin = (s.Timestamp - StartTime.Value).TotalMinutes;

        allWindow.Enqueue(s);
        Trim(allWindow);
        session.AddFrame(s, previous);

        if (!s.Excluded)
        {
            if (previous is { Excluded: false } p)
            {
                reconRa += (s.RaPx - p.RaPx) + p.RaCorrectionPx;
                reconDec += (s.DecPx - p.DecPx) + p.DecCorrectionPx;
            }

            includedWindow.Enqueue(s);
            Trim(includedWindow);
            winRa.AddGuideInfo(tMin, s.RaPx, s.RaDurationMs);
            winDec.AddGuideInfo(tMin, s.DecPx, s.DecDurationMs);
            winDriftRa.AddGuideInfo(tMin, reconRa, 0);
            winDriftDec.AddGuideInfo(tMin, reconDec, 0);
            session.AddIncluded(s, tMin, reconRa, reconDec);
        }

        previous = s;
    }

    private void Trim(Queue<GuideStepSample> q)
    {
        while (q.Count > windowSize)
            q.Dequeue();
    }

    /// <summary>Returns an immutable snapshot.</summary>
    public GuidingStatsSnapshot GetSnapshot(DateTimeOffset now)
    {
        return new GuidingStatsSnapshot
        {
            Window = BuildWindow(),
            Session = session.Build(this),
            WindowSize = windowSize,
            PixelScale = PixelScale,
            DeclinationDeg = DeclinationDeg,
            StartTime = StartTime,
            Timestamp = now,
            Elapsed = StartTime is { } st && now > st ? now - st : TimeSpan.Zero,
            FrameCount = session.Frames,
            StarLostCount = StarLostCount,
        };
    }

    private GuidingStatsBlock BuildWindow()
    {
        // Oscillation index over all frames in the window (PHD2 m_raSameSides).
        int sameSides = 0;
        double? prevRa = null;
        int raLimited = 0;
        int decLimited = 0;
        foreach (var s in allWindow)
        {
            if (prevRa is double pr && s.RaPx * pr > 0.0)
                sameSides++;
            prevRa = s.RaPx;
            if (s.RaLimited)
                raLimited++;
            if (s.DecLimited)
                decLimited++;
        }

        var inc = new IncludedAccumulator();
        foreach (var s in includedWindow)
            inc.Add(s);

        double peakRa = Math.Max(Math.Abs(winRa.MaxDisplacement), Math.Abs(winRa.MinDisplacement));
        double peakDec = Math.Max(Math.Abs(winDec.MaxDisplacement), Math.Abs(winDec.MinDisplacement));

        return Compose(
            frames: allWindow.Count,
            included: includedWindow.Count,
            rmsRa: winRa.PopulationSigma,
            rmsDec: winDec.PopulationSigma,
            peakRa: peakRa,
            peakDec: peakDec,
            driftRa: Slope(winDriftRa),
            driftDec: Slope(winDriftDec),
            sameSides: sameSides,
            inc: inc,
            raLimited: raLimited,
            decLimited: decLimited);
    }

    private static double? Slope(AxisStats a)
    {
        if (a.Count < 2)
            return null;
        a.GetLinearFitResults(out double slope, out _);
        return double.IsFinite(slope) ? slope : null;
    }

    private GuidingStatsBlock Compose(int frames, int included, double rmsRa, double rmsDec, double peakRa, double peakDec, double? driftRa, double? driftDec, int sameSides, IncludedAccumulator inc, int raLimited, int decLimited)
    {
        double scale = PixelScale;
        double rmsTot = Math.Sqrt(rmsRa * rmsRa + rmsDec * rmsDec);

        double osc = 0.0;
        bool oscAlert = false;
        if (frames >= 2)
        {
            osc = 1.0 - (double)sameSides / (frames - 1);
            oscAlert = osc > OscillationAlertHigh || osc < OscillationAlertLow;
        }

        double? pae = null;
        bool decAssumed = DeclinationDeg is null;
        if (driftDec is double dd)
        {
            // polar alignment error from Barrett:
            // http://celestialwonders.com/articles/polaralignment/PolarAlignmentAccuracy.pdf
            double cosdec = DeclinationDeg is double dec ? Math.Cos(dec * Math.PI / 180.0) : 1.0; // assume declination 0
            pae = PolarAlignmentFactor * Math.Abs(dd) * scale / cosdec;
        }

        return new GuidingStatsBlock
        {
            Frames = frames,
            IncludedFrames = included,
            RmsRaPx = rmsRa,
            RmsDecPx = rmsDec,
            RmsTotalPx = rmsTot,
            RmsRaArcsec = rmsRa * scale,
            RmsDecArcsec = rmsDec * scale,
            RmsTotalArcsec = rmsTot * scale,
            PeakRaPx = peakRa,
            PeakDecPx = peakDec,
            PeakRaArcsec = peakRa * scale,
            PeakDecArcsec = peakDec * scale,
            RaDriftPxPerMin = driftRa,
            DecDriftPxPerMin = driftDec,
            RaDriftArcsecPerMin = driftRa * scale,
            DecDriftArcsecPerMin = driftDec * scale,
            PolarAlignmentErrorArcmin = pae,
            PolarAlignmentDecAssumed = decAssumed,
            OscillationIndex = osc,
            OscillationAlert = oscAlert,
            RaDutyPercent = inc.Count > 0 ? 100.0 * inc.RaPulses / inc.Count : 0.0,
            DecDutyPercent = inc.Count > 0 ? 100.0 * inc.DecPulses / inc.Count : 0.0,
            SnrMin = inc.SnrCount > 0 ? inc.SnrMin : null,
            SnrAvg = inc.SnrCount > 0 ? inc.SnrSum / inc.SnrCount : null,
            SnrLast = inc.SnrLast,
            StarCountAvg = inc.StarCountN > 0 ? (double)inc.StarCountSum / inc.StarCountN : null,
            RaLimitedCount = raLimited,
            DecLimitedCount = decLimited,
        };
    }

    private sealed class IncludedAccumulator
    {
        public int Count;
        public int RaPulses;
        public int DecPulses;
        public int SnrCount;
        public double SnrSum;
        public double SnrMin = double.MaxValue;
        public double? SnrLast;
        public long StarCountSum;
        public int StarCountN;

        public void Add(GuideStepSample s)
        {
            Count++;
            if (s.RaDurationMs > 0)
                RaPulses++;
            if (s.DecDurationMs > 0)
                DecPulses++;
            if (s.Snr is double snr)
            {
                SnrCount++;
                SnrSum += snr;
                SnrMin = Math.Min(SnrMin, snr);
                SnrLast = snr;
            }

            if (s.StarCount is int sc)
            {
                StarCountSum += sc;
                StarCountN++;
            }
        }

        public void Clear()
        {
            Count = RaPulses = DecPulses = SnrCount = StarCountN = 0;
            SnrSum = 0;
            SnrMin = double.MaxValue;
            SnrLast = null;
            StarCountSum = 0;
        }
    }

    private sealed class SessionAccumulator
    {
        private readonly DescriptiveStats ra = new();
        private readonly DescriptiveStats dec = new();
        private readonly AxisStats driftRa = new();
        private readonly AxisStats driftDec = new();
        private readonly IncludedAccumulator inc = new();
        private double peakRa;
        private double peakDec;
        private int sameSides;
        private int raLimited;
        private int decLimited;

        public int Frames { get; private set; }

        public void Clear()
        {
            ra.ClearAll();
            dec.ClearAll();
            driftRa.ClearAll();
            driftDec.ClearAll();
            inc.Clear();
            peakRa = peakDec = 0;
            sameSides = raLimited = decLimited = 0;
            Frames = 0;
        }

        public void AddFrame(GuideStepSample s, GuideStepSample? previous)
        {
            Frames++;
            if (previous is not null && s.RaPx * previous.RaPx > 0.0)
                sameSides++;
            if (s.RaLimited)
                raLimited++;
            if (s.DecLimited)
                decLimited++;
        }

        public void AddIncluded(GuideStepSample s, double tMin, double reconRa, double reconDec)
        {
            ra.AddValue(s.RaPx);
            dec.AddValue(s.DecPx);
            peakRa = Math.Max(peakRa, Math.Abs(s.RaPx));
            peakDec = Math.Max(peakDec, Math.Abs(s.DecPx));
            driftRa.AddGuideInfo(tMin, reconRa, 0);
            driftDec.AddGuideInfo(tMin, reconDec, 0);
            inc.Add(s);
        }

        public GuidingStatsBlock Build(GuidingStatistics owner) => owner.Compose(
            frames: Frames,
            included: inc.Count,
            rmsRa: ra.PopulationSigma,
            rmsDec: dec.PopulationSigma,
            peakRa: peakRa,
            peakDec: peakDec,
            driftRa: Slope(driftRa),
            driftDec: Slope(driftDec),
            sameSides: sameSides,
            inc: inc,
            raLimited: raLimited,
            decLimited: decLimited);
    }
}
