// SPDX-License-Identifier: MPL-2.0

using NINA.GuideEngine.Core;

namespace NINA.GuideEngine.Test.TestSupport;

public enum PsfShape
{
    Gaussian,
    Moffat,
}

/// <summary>A synthetic star. Pixel (i, j) covers [i−0.5, i+0.5] × [j−0.5, j+0.5], so X/Y are in PHD2 pixel coordinates.</summary>
public sealed record SyntheticStar(double X, double Y, double Flux, double Fwhm = 3.0, PsfShape Shape = PsfShape.Gaussian, double MoffatBeta = 3.0);

/// <summary>
/// Deterministic synthetic guide-frame renderer for tests: pixel-integrated Gaussian or Moffat PSFs
/// with sub-pixel truth, sky background, Poisson shot noise (via a gain in e-/ADU), Gaussian read
/// noise, bias offset, hot pixels and clipping at a saturation level. Fixed seeds make every frame
/// reproducible.
/// </summary>
public sealed class StarFieldRenderer
{
    public StarFieldRenderer(int width, int height)
    {
        Width = width;
        Height = height;
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>Sky background in ADU (above bias).</summary>
    public double Background { get; set; } = 100;

    /// <summary>Bias/offset ADU added to every pixel (noise free).</summary>
    public double Bias { get; set; } = 500;

    /// <summary>Read noise, ADU rms.</summary>
    public double ReadNoise { get; set; } = 5;

    /// <summary>Gain in electrons per ADU for shot noise; 0 disables shot noise.</summary>
    public double Gain { get; set; } = 1.0;

    /// <summary>Clip level (ADU); pixels at or above are set to it.</summary>
    public ushort Saturation { get; set; } = 65535;

    public int BitsPerPixel { get; set; } = 16;

    public int Seed { get; set; } = 1;

    public List<SyntheticStar> Stars { get; } = new();

    /// <summary>Hot pixels (absolute value written after noise, before clipping).</summary>
    public List<(int X, int Y, ushort Value)> HotPixels { get; } = new();

    public StarFieldRenderer AddStar(double x, double y, double flux, double fwhm = 3.0, PsfShape shape = PsfShape.Gaussian)
    {
        Stars.Add(new SyntheticStar(x, y, flux, fwhm, shape));
        return this;
    }

    /// <summary>Noise-free expected ADU image above bias (background + stars).</summary>
    public double[] RenderExpected()
    {
        var img = new double[Width * Height];
        Array.Fill(img, Background);
        foreach (var s in Stars)
            AddPsf(img, s);
        return img;
    }

    public GuideFrame Render(int? seed = null)
    {
        var expected = RenderExpected();
        var rng = new Random(seed ?? Seed);
        var f = new GuideFrame(Width, Height) { BitsPerPixel = BitsPerPixel, ExposureMs = 2000 };
        double max = Math.Min(Saturation, (1 << BitsPerPixel) - 1);
        for (int i = 0; i < expected.Length; i++)
        {
            double v = expected[i];
            if (Gain > 0)
                v = Poisson(rng, v * Gain) / Gain;
            v += Bias + ReadNoise * Normal(rng);
            v = Math.Round(v);
            if (v < 0) v = 0;
            if (v > max) v = max;
            f.Pixels[i] = (ushort)v;
        }

        foreach (var (x, y, value) in HotPixels)
            f.Pixels[y * Width + x] = (ushort)Math.Min(value, max);
        return f;
    }

    private void AddPsf(double[] img, SyntheticStar s)
    {
        double sigma = s.Fwhm / 2.3548200450309493;
        int r = s.Shape == PsfShape.Gaussian ? (int)Math.Ceiling(6 * sigma) + 1 : (int)Math.Ceiling(8 * s.Fwhm) + 1;
        int x0 = Math.Max(0, (int)Math.Floor(s.X) - r), x1 = Math.Min(Width - 1, (int)Math.Ceiling(s.X) + r);
        int y0 = Math.Max(0, (int)Math.Floor(s.Y) - r), y1 = Math.Min(Height - 1, (int)Math.Ceiling(s.Y) + r);
        if (s.Shape == PsfShape.Gaussian)
        {
            // exact pixel integration: separable erf differences
            var fx = new double[x1 - x0 + 1];
            var fy = new double[y1 - y0 + 1];
            double k = 1.0 / (Math.Sqrt(2) * sigma);
            for (int x = x0; x <= x1; x++)
                fx[x - x0] = 0.5 * (Erf((x + 0.5 - s.X) * k) - Erf((x - 0.5 - s.X) * k));
            for (int y = y0; y <= y1; y++)
                fy[y - y0] = 0.5 * (Erf((y + 0.5 - s.Y) * k) - Erf((y - 0.5 - s.Y) * k));
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                    img[y * Width + x] += s.Flux * fx[x - x0] * fy[y - y0];
        }
        else
        {
            // Moffat, 8x8 supersampled, normalised: I(r) = (β−1)/(π α²) (1 + r²/α²)^−β
            double beta = s.MoffatBeta;
            double alpha = s.Fwhm / (2 * Math.Sqrt(Math.Pow(2, 1 / beta) - 1));
            double norm = (beta - 1) / (Math.PI * alpha * alpha);
            const int ss = 8;
            for (int y = y0; y <= y1; y++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    double sum = 0;
                    for (int j = 0; j < ss; j++)
                    {
                        double py = y - 0.5 + (j + 0.5) / ss - s.Y;
                        for (int i = 0; i < ss; i++)
                        {
                            double px = x - 0.5 + (i + 0.5) / ss - s.X;
                            sum += norm * Math.Pow(1 + (px * px + py * py) / (alpha * alpha), -beta);
                        }
                    }

                    img[y * Width + x] += s.Flux * sum / (ss * ss);
                }
            }
        }
    }

    public static double Normal(Random rng)
    {
        double u1 = 1.0 - rng.NextDouble();
        double u2 = rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
    }

    public static double Poisson(Random rng, double lambda)
    {
        if (lambda <= 0) return 0;
        if (lambda > 60)
            return Math.Max(0, Math.Round(lambda + Math.Sqrt(lambda) * Normal(rng)));
        double l = Math.Exp(-lambda), p = 1.0;
        int k = 0;
        do
        {
            k++;
            p *= rng.NextDouble();
        }
        while (p > l);
        return k - 1;
    }

    /// <summary>erf with |error| &lt; 1.2e-7 (Numerical Recipes erfc Chebyshev approximation).</summary>
    public static double Erf(double x)
    {
        double z = Math.Abs(x);
        double t = 1.0 / (1.0 + 0.5 * z);
        double ans = t * Math.Exp(-z * z - 1.26551223 + t * (1.00002368 + t * (0.37409196 + t * (0.09678418 + t * (-0.18628806 +
            t * (0.27886807 + t * (-1.13520398 + t * (1.48851587 + t * (-0.82215223 + t * 0.17087277)))))))));
        return x >= 0 ? 1.0 - ans : ans - 1.0;
    }
}
