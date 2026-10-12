// SPDX-License-Identifier: MPL-2.0

using System.Globalization;
using System.IO.Compression;
using NUnit.Framework;

namespace NINA.GuideEngine.Test.Algorithms;

/// <summary>
/// A night of a ProxiSky UMi strain-wave mount, replayed from a PHD2 guide log (Data/umi/&lt;mount&gt;/&lt;night&gt;.csv.gz,
/// see Data/umi/README.md): the guiding runs with their metadata and frames, offsets and corrections in mount px.
/// </summary>
internal sealed record UmiNight(string Mount, string Date, double PixelScale, IReadOnlyList<UmiRun> Runs)
{
    public const double SiderealDaySeconds = 86164.0905;

    /// <summary>All nights of all mounts (or of one), oldest first.</summary>
    public static IEnumerable<UmiNight> All(string? mount = null)
    {
        string root = Path.Combine(TestContext.CurrentContext.TestDirectory, "Data", "umi");
        foreach (string dir in Directory.GetDirectories(root).Order(StringComparer.Ordinal))
        {
            if (mount is not null && Path.GetFileName(dir) != mount)
            {
                continue;
            }

            foreach (string file in Directory.GetFiles(dir, "*.csv.gz").Order(StringComparer.Ordinal))
            {
                yield return Load(file);
            }
        }
    }

    public static UmiNight Load(string file)
    {
        string mount = Path.GetFileName(Path.GetDirectoryName(file)!);
        string date = Path.GetFileName(file).Split('.')[0];
        var meta = new Dictionary<int, Dictionary<string, string>>();
        var frames = new Dictionary<int, List<UmiFrame>>();
        string[]? header = null;
        double pixelScale = 0;
        using var reader = new StreamReader(new GZipStream(File.OpenRead(file), CompressionMode.Decompress));
        while (reader.ReadLine() is { } line)
        {
            if (line.StartsWith("#run,run,", StringComparison.Ordinal))
            {
                header = line.Split(',')[1..];
                continue;
            }

            if (line.StartsWith("#run,", StringComparison.Ordinal) && header is not null)
            {
                var v = line.Split(',')[1..];
                meta[int.Parse(v[0], CultureInfo.InvariantCulture)] = header.Zip(v).ToDictionary(p => p.First, p => p.Second);
                continue;
            }

            if (line.StartsWith("# PHD2", StringComparison.Ordinal) && line.IndexOf("\"/px", StringComparison.Ordinal) is int k and > 0)
            {
                pixelScale = double.Parse(line[(line.LastIndexOf(' ', k) + 1)..k], CultureInfo.InvariantCulture);
                continue;
            }

            if (line.Length == 0 || line[0] == '#' || line.StartsWith("frame", StringComparison.Ordinal))
            {
                continue;
            }

            var f = line.Split(',');
            double D(int i) => double.Parse(f[i], CultureInfo.InvariantCulture);
            var frame = new UmiFrame(int.Parse(f[0], CultureInfo.InvariantCulture), D(1), f[2] == "1", f[3] == "1", D(4), D(5), D(6), D(7),
                D(9), D(10), D(11));
            int run = int.Parse(f[8], CultureInfo.InvariantCulture);
            if (!frames.TryGetValue(run, out var list))
            {
                frames[run] = list = [];
            }

            list.Add(frame);
        }

        var runs = frames.OrderBy(p => p.Key).Select(p => new UmiRun(p.Key, meta[p.Key], p.Value)).ToList();
        return new UmiNight(mount, date, pixelScale, runs);
    }
}

/// <summary>One frame: offsets and corrections in mount px (corrections reduce a positive offset).</summary>
internal sealed record UmiFrame(int Frame, double TimeSec, bool Settling, bool Dither, double RaPx, double DecPx,
    double RaCorrPx, double DecCorrPx, double DitherRaPx, double DitherDecPx, double Snr);

/// <summary>A guiding run (PHD2 "Guiding Begins" to "Guiding Ends") with its header values.</summary>
internal sealed class UmiRun
{
    public UmiRun(int number, IReadOnlyDictionary<string, string> meta, IReadOnlyList<UmiFrame> frames)
    {
        Number = number;
        Meta = meta;
        Frames = frames;
        double V(string k) => double.Parse(meta[k], CultureInfo.InvariantCulture);
        HourAngle = V("ha_h");
        DeclinationDeg = V("dec_deg");
        PierSide = meta["pier"];
        RaRatePxPerSec = V("ra_rate_px_s");
        DecRatePxPerSec = V("dec_rate_px_s");
        ExposureMs = V("exposure_ms");
        MaxRaMs = V("max_ra_ms");
        MaxDecMs = V("max_dec_ms");
    }

    public int Number { get; }

    public IReadOnlyDictionary<string, string> Meta { get; }

    public IReadOnlyList<UmiFrame> Frames { get; }

    /// <summary>Hour angle at the start of the run (h, from the PHD2 header, 0.01 h resolution).</summary>
    public double HourAngle { get; }

    public double DeclinationDeg { get; }

    public string PierSide { get; }

    public double RaRatePxPerSec { get; }

    public double DecRatePxPerSec { get; }

    public double ExposureMs { get; }

    public double MaxRaMs { get; }

    public double MaxDecMs { get; }

    public double SpanSec => Frames.Count > 1 ? Frames[^1].TimeSec - Frames[0].TimeSec : 0;

    /// <summary>RA axis angle (h) at a frame: hour angle + 12 h on the West pier side, as the guider forms it.</summary>
    public double AxisHours(UmiFrame f) =>
        HourAngle + (f.TimeSec - Frames[0].TimeSec) * 1.0027379 / 3600 + (PierSide == "West" ? 12 : 0);

    /// <summary>
    /// Open-loop position (mount px) per frame: offset + cumulative lock change + cumulative corrections, the corrections
    /// scaled by <paramref name="pulseEffect"/> (how far a calibrated pulse really moves the star on that axis).
    /// </summary>
    public double[] OpenLoop(bool ra, double pulseEffect = 1.0)
    {
        var y = new double[Frames.Count];
        double lockSum = 0, corrSum = 0;
        for (int i = 0; i < Frames.Count; i++)
        {
            var f = Frames[i];
            lockSum += ra ? f.DitherRaPx : f.DitherDecPx;
            y[i] = (ra ? f.RaPx : f.DecPx) + lockSum + pulseEffect * corrSum;
            corrSum += ra ? f.RaCorrPx : f.DecCorrPx;
        }

        return y;
    }
}
