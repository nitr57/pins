// SPDX-License-Identifier: MPL-2.0

namespace NINA.GuideEngine.Simulation;

/// <summary>
/// The simulated sky: a fixed star field plus the atmosphere (seeing image motion, transparency,
/// occlusions). Pure functions of time; thread-safe.
/// </summary>
/// <remarks>
/// Seeing image motion is modelled per star as piecewise-constant Gaussian noise on cells of
/// <see cref="SkySimConfig.SeeingCoherenceMs"/>, split into a component common to all stars and an
/// independent per-star component (<see cref="SkySimConfig.SeeingCommonFraction"/> of the variance is
/// common). The slow layers (<see cref="SkySimConfig.SeeingLayers"/>) add motion common to all stars on cells of their own.
/// Cell values are hashed from (seed, stream, cell index), so evaluation order does not matter.
/// </remarks>
public sealed class SimulatedSky
{
    // hash streams of the slow layers (2 per layer), far from the fast ones (0, 1, 16 + 2 per star)
    private const ulong LayerStreams = 0x5EE0_0000UL;

    private readonly double cellSec;
    private readonly double cellSigma;
    private readonly double commonWeight;
    private readonly double independentWeight;
    private readonly ulong seed;
    private readonly object addGate = new();
    private TransparencyWindow[] addedTransparency = [];

    public SimulatedSky(SkySimConfig config, IReadOnlyList<SimStar> stars)
    {
        Config = config;
        Stars = stars;
        cellSec = Math.Max(config.SeeingCoherenceMs, 1.0) / 1000.0;
        cellSigma = Math.Max(0, config.SeeingJitterArcsec) * Math.Sqrt(1.0 / cellSec);
        double f = Math.Clamp(config.SeeingCommonFraction, 0, 1);
        commonWeight = Math.Sqrt(f);
        independentWeight = Math.Sqrt(1 - f);
        seed = (ulong)config.SeeingSeed;
    }

    public SkySimConfig Config { get; }

    public IReadOnlyList<SimStar> Stars { get; }

    /// <summary>
    /// Generates a random star field of <see cref="SkySimConfig.RandomStarCount"/> stars uniformly spread
    /// over a sensor of <paramref name="width"/> x <paramref name="height"/> px (minus <paramref name="marginPx"/>),
    /// converted to sky offsets with <paramref name="sensorToSky"/>. Magnitudes follow a
    /// <c>N(m) ∝ 10^(0.3 m)</c> luminosity function between the configured limits.
    /// </summary>
    public static IReadOnlyList<SimStar> GenerateRandomField(SkySimConfig config, Func<double, double, SkyOffset> sensorToSky, int width, int height, int marginPx = 16)
    {
        var rng = new SimRng((ulong)config.StarFieldSeed, 0xF1E1DUL);
        var list = new List<SimStar>(config.RandomStarCount);
        double m0 = config.BrightestMagnitude, m1 = Math.Max(config.FaintestMagnitude, m0);
        const double k = 0.3 * 2.302585092994046; // ln(10)·0.3
        for (int i = 0; i < config.RandomStarCount; i++)
        {
            double x = rng.Uniform(marginPx, width - 1 - marginPx);
            double y = rng.Uniform(marginPx, height - 1 - marginPx);
            // inverse CDF of density ∝ exp(k m) on [m0, m1]
            double u = rng.NextDouble();
            double mag = m1 > m0
                ? Math.Log(Math.Exp(k * m0) + u * (Math.Exp(k * m1) - Math.Exp(k * m0))) / k
                : m0;
            var s = sensorToSky(x, y);
            list.Add(new SimStar(s.Ra, s.Dec, mag));
        }

        return list;
    }

    /// <summary>Adds a transparency change at run time (e.g. clouds injected from a UI); thread-safe.</summary>
    public void AddTransparencyWindow(TransparencyWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        lock (addGate)
        {
            Volatile.Write(ref addedTransparency, [.. addedTransparency, window]);
        }
    }

    /// <summary>Atmospheric transmission (0..1) at time <paramref name="t"/>.</summary>
    public double TransparencyAt(double t)
    {
        double tr = 1.0;
        foreach (var w in Config.Transparency.Concat(Volatile.Read(ref addedTransparency)))
        {
            if (t < w.StartSec || t > w.EndSec) continue;
            double v = w.Transmission;
            if (w.RampSec > 0)
            {
                if (t < w.StartSec + w.RampSec) v = 1 + (w.Transmission - 1) * (t - w.StartSec) / w.RampSec;
                else if (t > w.EndSec - w.RampSec) v = 1 + (w.Transmission - 1) * (w.EndSec - t) / w.RampSec;
            }

            tr *= Math.Clamp(v, 0, 1);
        }

        return tr;
    }

    /// <summary>True when star <paramref name="starIndex"/> is occluded (flux 0) at time <paramref name="t"/>.</summary>
    public bool IsOccluded(int starIndex, double t)
    {
        foreach (var o in Config.Occlusions)
        {
            if (t >= o.StartSec && t <= o.EndSec && (o.StarIndex == null || o.StarIndex == starIndex)) return true;
        }

        return false;
    }

    /// <summary>
    /// Mean seeing displacement of star <paramref name="starIndex"/> (arcsec on the sky, RA East / Dec North)
    /// averaged over [<paramref name="t0"/>, <paramref name="t1"/>].
    /// </summary>
    public SkyOffset SeeingOffset(int starIndex, double t0, double t1)
    {
        if (t1 <= t0) t1 = t0 + 1e-9;
        var offset = FastSeeingOffset(starIndex, t0, t1);
        var layers = Config.SeeingLayers;
        for (int i = 0; i < layers.Count; i++) offset += LayerOffset(i, layers[i], t0, t1);
        return offset;
    }

    // a slow layer, common to all stars: the time-weighted mean of its cells over [t0, t1]
    private SkyOffset LayerOffset(int layer, SeeingLayer config, double t0, double t1)
    {
        if (!(config.CellSec > 0) || !(config.SigmaArcsec > 0)) return SkyOffset.Zero;
        long c0 = (long)Math.Floor(t0 / config.CellSec);
        long c1 = (long)Math.Floor(t1 / config.CellSec);
        double ra = 0, dec = 0;
        ulong stream = LayerStreams + 2 * (ulong)layer;
        for (long c = c0; c <= c1; c++)
        {
            double w = Math.Min(t1, (c + 1) * config.CellSec) - Math.Max(t0, c * config.CellSec);
            if (w <= 0) continue;
            ra += w * Hash.Gaussian(seed, stream, (ulong)c);
            dec += w * Hash.Gaussian(seed, stream + 1, (ulong)c);
        }

        double norm = config.SigmaArcsec / (t1 - t0);
        return new SkyOffset(ra * norm, dec * norm);
    }

    private SkyOffset FastSeeingOffset(int starIndex, double t0, double t1)
    {
        if (cellSigma <= 0) return SkyOffset.Zero;
        long c0 = (long)Math.Floor(t0 / cellSec);
        long c1 = (long)Math.Floor(t1 / cellSec);
        double ra = 0, dec = 0;
        ulong starStream = 16 + 2 * (ulong)starIndex;
        for (long c = c0; c <= c1; c++)
        {
            double a = Math.Max(t0, c * cellSec), b = Math.Min(t1, (c + 1) * cellSec);
            if (b <= a) continue;
            double w = b - a;
            ulong uc = (ulong)c;
            ra += w * (commonWeight * Hash.Gaussian(seed, 0, uc) + independentWeight * Hash.Gaussian(seed, starStream, uc));
            dec += w * (commonWeight * Hash.Gaussian(seed, 1, uc) + independentWeight * Hash.Gaussian(seed, starStream + 1, uc));
        }

        double norm = cellSigma / (t1 - t0);
        return new SkyOffset(ra * norm, dec * norm);
    }
}
