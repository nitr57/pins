// SPDX-License-Identifier: MPL-2.0

using NINA.GuideEngine.Core;
using NINA.GuideEngine.Simulation;

namespace NINA.GuideEngine.Test.Simulation;

internal static class SimTestHelpers
{
    /// <summary>A noise-free-ish scenario: one star at the field centre, no tracking errors, no seeing motion, no defects.</summary>
    public static SimulatorScenario Clean(double magnitude = 7.0) => new()
    {
        Name = "Clean",
        Mount = new MountSimConfig { DeclinationDeg = 0.0 },
        Sky = new SkySimConfig
        {
            Stars = [new SimStar(0, 0, magnitude)],
            SeeingJitterArcsec = 0,
        },
        Camera = new CameraSimConfig { HotPixelCount = 0, ColdPixelCount = 0 },
    };

    /// <summary>Background-subtracted intensity-weighted centroid in a square window.</summary>
    public static (double X, double Y, double Flux) Centroid(GuideFrame f, double cx, double cy, int radius = 12)
    {
        int x0 = (int)Math.Round(cx), y0 = (int)Math.Round(cy);

        // background: median of a ring just outside the window
        var ring = new List<double>();
        for (int y = y0 - radius - 6; y <= y0 + radius + 6; y++)
        {
            for (int x = x0 - radius - 6; x <= x0 + radius + 6; x++)
            {
                if (x < 0 || y < 0 || x >= f.Width || y >= f.Height) continue;
                if (Math.Abs(x - x0) <= radius + 2 && Math.Abs(y - y0) <= radius + 2) continue;
                ring.Add(f[x, y]);
            }
        }

        ring.Sort();
        double bg = ring[ring.Count / 2];
        double sx = 0, sy = 0, s = 0;
        for (int y = y0 - radius; y <= y0 + radius; y++)
        {
            for (int x = x0 - radius; x <= x0 + radius; x++)
            {
                double v = f[x, y] - bg;
                sx += v * x;
                sy += v * y;
                s += v;
            }
        }

        return (sx / s, sy / s, s);
    }

    public static (double Mean, double Sigma) FrameStats(GuideFrame f, IntRect r)
    {
        double sum = 0, sum2 = 0;
        long n = 0;
        for (int y = r.Top; y <= r.Bottom; y++)
        {
            for (int x = r.Left; x <= r.Right; x++)
            {
                double v = f[x, y];
                sum += v;
                sum2 += v * v;
                n++;
            }
        }

        double mean = sum / n;
        return (mean, Math.Sqrt(sum2 / n - mean * mean));
    }
}
