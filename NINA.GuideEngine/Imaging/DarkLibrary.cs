// SPDX-License-Identifier: MPL-2.0 AND BSD-3-Clause
// Copyright (c) 2006-2010 Craig Stark.
// Copyright (c) 2014 Bruce Waddington
// Ported from PHD2 src/camera.cpp, src/image_math.cpp, src/darks_dialog.cpp (a6c02722)

using NINA.GuideEngine.Core;

namespace NINA.GuideEngine.Imaging;

/// <summary>How the light-frame median entering the pedestal formula is determined.</summary>
public enum DarkPedestalMode
{
    /// <summary>
    /// Behaviour of a running PHD2: <c>Subtract()</c> reads <c>light.MedianADU</c> before
    /// <c>CalcStats()</c> has run for the new frame, so the light median is effectively 0 and the
    /// pedestal equals the dark median. Keeps the background well above 0 (no clipping of noise).
    /// </summary>
    Phd2Runtime,

    /// <summary>
    /// The formula as written: <c>pedestal = max(median(dark ROI) − median(light ROI), 0)</c> using
    /// the real median of the light frame.
    /// </summary>
    LightMedian,
}

/// <summary>A master dark frame held by the <see cref="DarkLibrary"/>.</summary>
public sealed class DarkFrame
{
    public DarkFrame(GuideFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        Frame = frame;
        MedianAdu = ImageMath.HistogramStats(frame.Pixels, frame.Width, new IntRect(0, 0, frame.Width, frame.Height)).Median;
    }

    public GuideFrame Frame { get; }

    public double ExposureMs => Frame.ExposureMs;

    public int Binning => Frame.Binning;

    /// <summary>Pre-computed full-frame median ADU (PHD2 usImage::MedianADU).</summary>
    public ushort MedianAdu { get; }
}

/// <summary>
/// Dark library: master darks keyed by binning and exposure, PHD2 dark selection and subtraction with
/// pedestal. Thread-safe for concurrent <see cref="Subtract(GuideFrame)"/> and library updates.
/// </summary>
public sealed class DarkLibrary
{
    /// <summary>PHD2 default number of frames averaged into one master dark.</summary>
    public const int DefaultFramesPerDark = 5;

    private readonly object gate = new();

    // Deviation from PHD2: PHD2 keys darks by exposure only (the library belongs to one camera binning and
    // is invalidated when binning changes); we key by (binning, exposure) and select within the binning.
    private readonly SortedDictionary<(int Binning, double ExposureMs), DarkFrame> darks = new();

    public DarkPedestalMode PedestalMode { get; set; } = DarkPedestalMode.Phd2Runtime;

    /// <summary>Where the library came from (e.g. its file name), for FITS headers of recorded frames; null when unknown.</summary>
    public string? Name { get; set; }

    public int Count
    {
        get
        {
            lock (gate) return darks.Count;
        }
    }

    public IReadOnlyList<DarkFrame> Darks
    {
        get
        {
            lock (gate) return darks.Values.ToList();
        }
    }

    /// <summary>Adds (or replaces) the dark for its exposure and binning (PHD2 AddDark).</summary>
    public void Add(GuideFrame dark)
    {
        ArgumentNullException.ThrowIfNull(dark);
        var df = new DarkFrame(dark);
        lock (gate) darks[(dark.Binning, dark.ExposureMs)] = df;
    }

    public void Clear()
    {
        lock (gate) darks.Clear();
    }

    /// <summary>
    /// Selects the dark with the smallest exposure ≥ <paramref name="exposureMs"/>, else the one with the
    /// longest exposure (PHD2 GuideCamera::SelectDark), among darks with the given binning.
    /// </summary>
    public DarkFrame? SelectDark(double exposureMs, int binning = 1)
    {
        lock (gate)
        {
            DarkFrame? current = null;
            foreach (var kv in darks)
            {
                if (kv.Key.Binning != binning) continue;
                current = kv.Value;
                if (kv.Key.ExposureMs >= exposureMs) break;
            }

            return current;
        }
    }

    /// <summary>Selects the matching dark for the light frame and subtracts it. Returns false when no compatible dark exists.</summary>
    public bool Subtract(GuideFrame light)
    {
        ArgumentNullException.ThrowIfNull(light);
        var dark = SelectDark(light.ExposureMs, light.Binning);
        return dark is not null && Subtract(light, dark, PedestalMode);
    }

    /// <summary>
    /// PHD2 dark subtraction: <c>pedestal = max(median(dark ROI) − median(light), 0)</c>,
    /// <c>out = clamp(light + pedestal − dark, 0, 65535)</c>. Only the valid region (subframe) is processed.
    /// Sets <see cref="GuideFrame.Pedestal"/> (needed for saturation detection). Returns false when the
    /// frames are incompatible.
    /// </summary>
    public static bool Subtract(GuideFrame light, DarkFrame dark, DarkPedestalMode mode = DarkPedestalMode.Phd2Runtime)
    {
        ArgumentNullException.ThrowIfNull(light);
        ArgumentNullException.ThrowIfNull(dark);
        var d = dark.Frame;
        if (light.Width != d.Width || light.Height != d.Height)
            return false;

        IntRect roi;
        ushort medianDark;
        if (!light.Subframe.IsEmpty)
        {
            roi = light.Subframe;
            medianDark = ImageMath.MedianValueInRoi(d.Pixels, d.Width, roi);
        }
        else
        {
            roi = new IntRect(0, 0, light.Width, light.Height);
            medianDark = dark.MedianAdu;
        }

        // Deviation from PHD2 (only with DarkPedestalMode.LightMedian): uses the actual light median;
        // the default Phd2Runtime mode reproduces PHD2's effective behaviour (light median not yet computed = 0).
        ushort medianLight = mode == DarkPedestalMode.LightMedian
            ? ImageMath.MedianValueInRoi(light.Pixels, light.Width, roi)
            : (ushort)0;

        // PHD2 only assigns the pedestal when the dark is brighter; the light frame starts at 0.
        if (medianDark > medianLight)
            light.Pedestal = (ushort)(medianDark - medianLight);

        int pedestal = light.Pedestal;
        var lp = light.Pixels;
        var dp = d.Pixels;
        int w = light.Width;
        for (int y = roi.Top; y <= roi.Bottom; y++)
        {
            int row = y * w;
            for (int x = roi.Left; x <= roi.Right; x++)
            {
                int i = row + x;
                int v = lp[i] + pedestal - dp[i];
                if (v < 0) v = 0; // hot pixel in dark frame isn't present in light frame
                else if (v > 65535) v = 65535;
                lp[i] = (ushort)v;
            }
        }

        return true;
    }
}

/// <summary>
/// Averages N dark frames into a master dark (PHD2 DarksDialog::CreateMasterDarkFrame: integer sum
/// divided by the frame count).
/// </summary>
public sealed class MasterDarkBuilder
{
    private uint[]? sum;
    private GuideFrame? first;

    public int FrameCount { get; private set; }

    /// <summary>Adds one dark frame. All frames must share size and binning.</summary>
    public void Add(GuideFrame dark)
    {
        ArgumentNullException.ThrowIfNull(dark);
        if (first is null)
        {
            first = dark;
            sum = new uint[dark.Pixels.Length];
        }
        else if (dark.Width != first.Width || dark.Height != first.Height || dark.Binning != first.Binning)
        {
            throw new ArgumentException("dark frame size/binning mismatch", nameof(dark));
        }

        var s = sum!;
        var p = dark.Pixels;
        for (int i = 0; i < p.Length; i++)
            s[i] += p[i];
        FrameCount++;
    }

    /// <summary>Builds the averaged master dark (exposure and binning taken from the first frame).</summary>
    public GuideFrame Build()
    {
        if (first is null || sum is null) throw new InvalidOperationException("no dark frames added");
        var master = new GuideFrame(first.Width, first.Height)
        {
            ExposureMs = first.ExposureMs,
            Binning = first.Binning,
            BitsPerPixel = first.BitsPerPixel,
            StartTime = first.StartTime,
        };
        uint n = (uint)FrameCount;
        var m = master.Pixels;
        for (int i = 0; i < m.Length; i++)
            m[i] = (ushort)(sum[i] / n);
        return master;
    }

    /// <summary>Convenience: averages the given frames.</summary>
    public static GuideFrame Average(IEnumerable<GuideFrame> frames)
    {
        ArgumentNullException.ThrowIfNull(frames);
        var b = new MasterDarkBuilder();
        foreach (var f in frames) b.Add(f);
        return b.Build();
    }
}
