// SPDX-License-Identifier: MPL-2.0

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;
using NINA.GuideEngine.Core;
using NINA.GuideEngine.Stars;
using NINA.GuideEngine.Test.TestSupport;

namespace NINA.GuideEngine.Test.Parity;

/// <summary>
/// Compares Star.Find / GuideStar.AutoFind with PHD2's own star.cpp compiled into
/// NINA.GuideEngine/tools/phd2-parity/bin/phd2-parity (build with NINA.GuideEngine/tools/phd2-parity/build.sh). Skipped when the CLI
/// is not available. Override the CLI location with PHD2_PARITY_CLI.
/// </summary>
[TestFixture]
[Category("Parity")]
public class Phd2ParityTests
{
    private const double CentroidTol = 0.01;
    private const double RelTol = 1e-9;

    private string cli = string.Empty;
    private string workDir = string.Empty;

    [OneTimeSetUp]
    public void Setup()
    {
        cli = Environment.GetEnvironmentVariable("PHD2_PARITY_CLI") ?? string.Empty;
        if (cli.Length == 0)
        {
            var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "NINA.sln")))
                dir = dir.Parent;
            if (dir is not null)
                cli = Path.Combine(dir.FullName, "NINA.GuideEngine", "tools", "phd2-parity", "bin", "phd2-parity");
        }

        if (!File.Exists(cli))
            Assert.Ignore($"PHD2 parity CLI not found ({cli}); run NINA.GuideEngine/tools/phd2-parity/build.sh");
        workDir = Path.Combine(Path.GetTempPath(), "phd2-parity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
    }

    [OneTimeTearDown]
    public void TearDown()
    {
        if (workDir.Length > 0 && Directory.Exists(workDir))
            Directory.Delete(workDir, true);
    }

    private sealed record FindReq(int Sr, int X, int Y, StarFindMode Mode, double MinHfd, double MaxHfd, ushort MaxAdu);

    private sealed record Case(string Name, GuideFrame Frame, List<FindReq> Finds);

    private string WriteRaw(GuideFrame f, string name)
    {
        var path = Path.Combine(workDir, name + ".raw");
        var bytes = new byte[f.Pixels.Length * 2];
        Buffer.BlockCopy(f.Pixels, 0, bytes, 0, bytes.Length);
        if (!BitConverter.IsLittleEndian) throw new NotSupportedException();
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static string ImageLine(string path, GuideFrame f)
    {
        var sb = new StringBuilder($"image {path} {f.Width} {f.Height} {f.BitsPerPixel} {f.Pedestal}");
        if (!f.Subframe.IsEmpty)
            sb.Append($" {f.Subframe.X} {f.Subframe.Y} {f.Subframe.Width} {f.Subframe.Height}");
        return sb.ToString();
    }

    private List<JsonElement> RunCli(string job)
    {
        var jobPath = Path.Combine(workDir, "job-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(jobPath, job);
        var psi = new ProcessStartInfo(cli, jobPath) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        using var p = Process.Start(psi)!;
        string stdout = p.StandardOutput.ReadToEnd();
        string stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        p.ExitCode.Should().Be(0, stderr);
        return stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonDocument.Parse(l).RootElement.Clone()).ToList();
    }

    private static string F(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    private static List<Case> BuildFindCases()
    {
        var cases = new List<Case>();
        var rng = new Random(2024);
        for (int i = 0; i < 40; i++)
        {
            int w = 120, h = 100;
            double x = 50 + rng.NextDouble() * 20, y = 40 + rng.NextDouble() * 20;
            double flux = Math.Exp(Math.Log(800) + rng.NextDouble() * (Math.Log(400000) - Math.Log(800)));
            double fwhm = 1.5 + rng.NextDouble() * 4.5;
            var shape = i % 3 == 0 ? PsfShape.Moffat : PsfShape.Gaussian;
            int bpp = i % 7 == 0 ? 8 : i % 5 == 0 ? 12 : 16;
            var r = new StarFieldRenderer(w, h)
            {
                Seed = 500 + i,
                Background = bpp == 8 ? 5 + rng.NextDouble() * 10 : 50 + rng.NextDouble() * 400,
                Bias = bpp == 8 ? 10 : 200 + rng.Next(400),
                ReadNoise = bpp == 8 ? 1 + rng.NextDouble() * 2 : 2 + rng.NextDouble() * 12,
                Gain = bpp == 8 ? 0.05 : 0.5 + rng.NextDouble(),
                BitsPerPixel = bpp,
                Saturation = i % 4 == 0 ? (ushort)(bpp == 16 ? 30000 : (1 << bpp) - 1) : (ushort)((1 << bpp) - 1),
            };
            r.Stars.Add(new SyntheticStar(x, y, bpp == 8 ? flux / 100 : flux, fwhm, shape));
            if (i % 6 == 0)
                r.AddStar(x + 9.3, y - 4.1, flux * 0.3, fwhm); // neighbour inside the annulus
            if (i % 8 == 0)
                r.HotPixels.Add(((int)x + 3, (int)y - 2, 20000));
            var f = r.Render();
            if (i % 5 == 1) f.Pedestal = (ushort)rng.Next(50, 300);
            if (i % 9 == 2) f.Subframe = new IntRect(35, 25, 50, 50);
            var finds = new List<FindReq>
            {
                new(15, (int)x, (int)y, StarFindMode.Centroid, 1.5, 20, 0),
                new(15, (int)x + 6, (int)y - 5, StarFindMode.Centroid, 1.5, 20, 0),
                new(15, (int)x, (int)y, StarFindMode.Peak, 1.5, 20, 0),
                new(7, (int)x - 2, (int)y + 1, StarFindMode.Centroid, 1.5, 20, 0),
                new(15, (int)x, (int)y, StarFindMode.Centroid, 1.5, 3.0, 0),
                new(15, (int)x, (int)y, StarFindMode.Centroid, 1.5, 20, (ushort)Math.Min(r.Saturation, (1 << bpp) - 1)),
                new(15, (int)x, (int)y, StarFindMode.Centroid, 1.5, 20, (ushort)(r.Saturation / 2)),
                new(15, 3, 3, StarFindMode.Centroid, 1.5, 20, 0), // corner
                new(15, -40, -40, StarFindMode.Centroid, 1.5, 20, 0), // invalid -> error
                new(10, 15, 80, StarFindMode.Centroid, 0.1, 20, 0), // background only
            };
            cases.Add(new Case($"star{i}", f, finds));
        }

        // hot pixel only, pure noise, flat field
        var hp = new StarFieldRenderer(80, 80) { Seed = 1 };
        hp.HotPixels.Add((40, 40, 30000));
        cases.Add(new Case("hotpixel", hp.Render(), [new(15, 40, 40, StarFindMode.Centroid, 1.5, 20, 0)]));
        cases.Add(new Case("noise", new StarFieldRenderer(80, 80) { Seed = 2, ReadNoise = 25 }.Render(),
            [new(15, 40, 40, StarFindMode.Centroid, 1.5, 20, 0), new(15, 20, 60, StarFindMode.Centroid, 1.5, 20, 0)]));
        var flat = new GuideFrame(80, 80);
        Array.Fill(flat.Pixels, (ushort)1234);
        cases.Add(new Case("flat", flat, [new(15, 40, 40, StarFindMode.Centroid, 1.5, 20, 0)]));
        return cases;
    }

    [Test]
    public void StarFind_MatchesPhd2()
    {
        var cases = BuildFindCases();
        var job = new StringBuilder();
        foreach (var c in cases)
        {
            job.AppendLine(ImageLine(WriteRaw(c.Frame, c.Name), c.Frame));
            foreach (var q in c.Finds)
                job.AppendLine($"find {q.Sr} {q.X} {q.Y} {(q.Mode == StarFindMode.Peak ? 1 : 0)} {F(q.MinHfd)} {F(q.MaxHfd)} {q.MaxAdu}");
        }

        var output = RunCli(job.ToString()).Where(e => e.GetProperty("cmd").GetString() == "find").ToList();
        int k = 0, n = 0, found = 0;
        double maxPos = 0, maxRelMass = 0, maxRelSnr = 0, maxRelHfd = 0;
        var codes = new SortedDictionary<StarFindResult, int>();
        foreach (var c in cases)
        {
            foreach (var q in c.Finds)
            {
                var e = output[k++];
                var s = new Star();
                bool ok = s.Find(c.Frame, q.Sr, q.X, q.Y, q.Mode, q.MinHfd, q.MaxHfd, q.MaxAdu);
                string ctx = $"{c.Name} {q}";
                ok.Should().Be(e.GetProperty("found").GetBoolean(), ctx);
                ((int)s.LastFindResult).Should().Be(e.GetProperty("result").GetInt32(), ctx);
                double px = D(e, "x"), py = D(e, "y");
                double dpos = Math.Max(Math.Abs(s.X - px), Math.Abs(s.Y - py));
                dpos.Should().BeLessThan(CentroidTol, ctx);
                double rm = Rel(s.Mass, D(e, "mass"));
                double rs = Rel(s.Snr, D(e, "snr"));
                double rh = Rel(s.Hfd, D(e, "hfd"));
                rm.Should().BeLessThan(RelTol, ctx);
                rs.Should().BeLessThan(RelTol, ctx);
                rh.Should().BeLessThan(RelTol, ctx);
                ((int)s.PeakValue).Should().Be(e.GetProperty("peak").GetInt32(), ctx);
                maxPos = Math.Max(maxPos, dpos);
                maxRelMass = Math.Max(maxRelMass, rm);
                maxRelSnr = Math.Max(maxRelSnr, rs);
                maxRelHfd = Math.Max(maxRelHfd, rh);
                codes[s.LastFindResult] = codes.GetValueOrDefault(s.LastFindResult) + 1;
                n++;
                if (ok) found++;
            }
        }

        TestContext.Out.WriteLine($"Star.Find parity: {n} finds on {cases.Count} frames ({found} found)");
        TestContext.Out.WriteLine($"  result codes: {string.Join(", ", codes.Select(kv => $"{kv.Key}={kv.Value}"))}");
        TestContext.Out.WriteLine($"  max |dpos| = {maxPos:E2} px, max rel diff mass = {maxRelMass:E2}, SNR = {maxRelSnr:E2}, HFD = {maxRelHfd:E2}");
        codes.Keys.Should().Contain([StarFindResult.Ok, StarFindResult.Saturated, StarFindResult.LowSnr, StarFindResult.LowMass, StarFindResult.LowHfd,
            StarFindResult.HighHfd, StarFindResult.Error]);
    }

    private static double D(JsonElement e, string name)
    {
        var p = e.GetProperty(name);
        return p.ValueKind == JsonValueKind.String ? double.Parse(p.GetString()!, CultureInfo.InvariantCulture) : p.GetDouble();
    }

    private static double Rel(double a, double b)
    {
        if (a == b || (double.IsNaN(a) && double.IsNaN(b))) return 0;
        return Math.Abs(a - b) / Math.Max(Math.Abs(a), Math.Abs(b));
    }

    private sealed record AfCase(string Name, GuideFrame Frame, int Edge, IntRect Roi, int MaxStars, int Downsample, double Scale, ushort SatAdu);

    private static List<AfCase> BuildAutoFindCases()
    {
        var list = new List<AfCase>();
        var rng = new Random(77);
        for (int i = 0; i < 16; i++)
        {
            int w = 480, h = 360;
            var r = new StarFieldRenderer(w, h)
            {
                Seed = 900 + i,
                Background = 100 + rng.NextDouble() * 300,
                ReadNoise = 3 + rng.NextDouble() * 10,
                Saturation = i % 3 == 0 ? (ushort)40000 : (ushort)65535,
                BitsPerPixel = i % 5 == 4 ? 12 : 16,
            };
            if (r.BitsPerPixel == 12) r.Saturation = 4095;
            int nStars = 3 + rng.Next(25);
            for (int s = 0; s < nStars; s++)
            {
                double flux = Math.Exp(Math.Log(2000) + rng.NextDouble() * (Math.Log(3_000_000) - Math.Log(2000)));
                if (r.BitsPerPixel == 12) flux /= 16;
                r.Stars.Add(new SyntheticStar(10 + rng.NextDouble() * (w - 20), 10 + rng.NextDouble() * (h - 20), flux, 1.8 + rng.NextDouble() * 3.5,
                    s % 4 == 0 ? PsfShape.Moffat : PsfShape.Gaussian));
            }

            for (int hpx = 0; hpx < 30; hpx++)
                r.HotPixels.Add((rng.Next(w), rng.Next(h), (ushort)rng.Next(2000, r.Saturation)));
            var f = r.Render();
            if (i % 4 == 1) f.Pedestal = 150;
            list.Add(new AfCase($"af{i}", f, i % 3 == 2 ? 25 : 0, i % 7 == 3 ? new IntRect(100, 60, 300, 250) : IntRect.Empty,
                i % 6 == 5 ? 1 : 12, i % 4 == 2 ? 0 : i % 4 == 3 ? 2 : 1, i % 2 == 0 ? 0.5 : 1.3, i % 5 == 1 ? (ushort)38000 : (ushort)0));
        }

        return list;
    }

    [Test]
    public void AutoFind_MatchesPhd2()
    {
        var cases = BuildAutoFindCases();
        var job = new StringBuilder();
        foreach (var c in cases)
        {
            job.AppendLine(ImageLine(WriteRaw(c.Frame, c.Name), c.Frame));
            job.AppendLine($"autofind {c.Edge} 15 {c.Roi.X} {c.Roi.Y} {c.Roi.Width} {c.Roi.Height} {c.MaxStars} {c.Downsample} {F(c.Scale)} 1.5 20 6 " +
                $"{(c.SatAdu > 0 ? 1 : 0)} {c.SatAdu}");
        }

        var output = RunCli(job.ToString()).Where(e => e.GetProperty("cmd").GetString() == "autofind").ToList();
        int samePrimary = 0, totalStars = 0;
        double maxPos = 0, maxRel = 0;
        for (int i = 0; i < cases.Count; i++)
        {
            var c = cases[i];
            var e = output[i];
            var o = new StarFinderOptions
            {
                AutoFindScoring = false,
                AutoFindDownsample = c.Downsample,
                PixelScale = c.Scale,
                SaturationAdu = c.SatAdu,
            };
            var d = new AutoFindDiagnostics();
            var stars = GuideStar.AutoFind(c.Frame, c.Edge, 15, c.Roi, c.MaxStars, o, d);
            bool found = e.GetProperty("found").GetBoolean();
            (stars.Count > 0).Should().Be(found, c.Name);
            if (!found) continue;
            d.PrimaryPeak.X.Should().Be(D(e, "x"), c.Name);
            d.PrimaryPeak.Y.Should().Be(D(e, "y"), c.Name);
            samePrimary++;
            var ps = e.GetProperty("stars").EnumerateArray().ToList();
            stars.Count.Should().Be(ps.Count, c.Name);
            for (int k = 0; k < ps.Count; k++)
            {
                string ctx = $"{c.Name} star {k}";
                double dp = Math.Max(Math.Abs(stars[k].X - D(ps[k], "x")), Math.Abs(stars[k].Y - D(ps[k], "y")));
                dp.Should().BeLessThan(CentroidTol, ctx);
                double rel = Math.Max(Rel(stars[k].Snr, D(ps[k], "snr")), Rel(stars[k].Mass, D(ps[k], "mass")));
                rel = Math.Max(rel, Rel(stars[k].Hfd, D(ps[k], "hfd")));
                rel.Should().BeLessThan(RelTol, ctx);
                ((int)stars[k].LastFindResult).Should().Be(ps[k].GetProperty("result").GetInt32(), ctx);
                stars[k].OffsetFromPrimary.X.Should().BeApproximately(D(ps[k], "ofsx"), CentroidTol, ctx);
                stars[k].OffsetFromPrimary.Y.Should().BeApproximately(D(ps[k], "ofsy"), CentroidTol, ctx);
                maxPos = Math.Max(maxPos, dp);
                maxRel = Math.Max(maxRel, rel);
                totalStars++;
            }
        }

        TestContext.Out.WriteLine(
            $"AutoFind parity: {cases.Count} fields, same primary in {samePrimary} found fields, {totalStars} list stars, max |dpos| = {maxPos:E2} px, max rel diff = {maxRel:E2}");
        samePrimary.Should().BeGreaterThan(cases.Count / 2);
    }
}
