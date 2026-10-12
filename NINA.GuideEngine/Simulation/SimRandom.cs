// SPDX-License-Identifier: MPL-2.0

namespace NINA.GuideEngine.Simulation;

/// <summary>
/// Small, fast, seedable PRNG (xoshiro256**) with a stable algorithm so simulator output is identical
/// across .NET versions and platforms.
/// </summary>
internal sealed class SimRng
{
    private ulong s0, s1, s2, s3;
    private double spareGaussian;
    private bool hasSpare;

    public SimRng(ulong seed)
    {
        ulong x = seed;
        s0 = SplitMix(ref x);
        s1 = SplitMix(ref x);
        s2 = SplitMix(ref x);
        s3 = SplitMix(ref x);
    }

    public SimRng(params ulong[] keys)
        : this(Hash.Combine(keys))
    {
    }

    public ulong NextULong()
    {
        ulong result = RotL(s1 * 5, 7) * 9;
        ulong t = s1 << 17;
        s2 ^= s0;
        s3 ^= s1;
        s1 ^= s2;
        s0 ^= s3;
        s2 ^= t;
        s3 = RotL(s3, 45);
        return result;
    }

    /// <summary>Uniform in [0, 1).</summary>
    public double NextDouble() => (NextULong() >> 11) * (1.0 / (1UL << 53));

    /// <summary>Uniform in (0, 1].</summary>
    public double NextDoubleNonZero() => ((NextULong() >> 11) + 1) * (1.0 / (1UL << 53));

    public double Uniform(double min, double max) => min + (max - min) * NextDouble();

    /// <summary>Standard normal deviate (Marsaglia polar method).</summary>
    public double NextGaussian()
    {
        if (hasSpare)
        {
            hasSpare = false;
            return spareGaussian;
        }

        double u, v, s;
        do
        {
            u = 2.0 * NextDouble() - 1.0;
            v = 2.0 * NextDouble() - 1.0;
            s = u * u + v * v;
        }
        while (s >= 1.0 || s == 0.0);

        double f = Math.Sqrt(-2.0 * Math.Log(s) / s);
        spareGaussian = v * f;
        hasSpare = true;
        return u * f;
    }

    /// <summary>Exponential deviate with the given mean.</summary>
    public double NextExponential(double mean) => -mean * Math.Log(NextDoubleNonZero());

    /// <summary>Poisson deviate; exact (Knuth) for small means, normal approximation above 30.</summary>
    public int NextPoisson(double mean)
    {
        if (mean <= 0) return 0;
        if (mean >= 30) return Math.Max(0, (int)Math.Round(mean + Math.Sqrt(mean) * NextGaussian()));
        double l = Math.Exp(-mean), p = 1.0;
        int k = 0;
        do
        {
            k++;
            p *= NextDouble();
        }
        while (p > l);
        return k - 1;
    }

    private static ulong RotL(ulong x, int k) => (x << k) | (x >> (64 - k));

    private static ulong SplitMix(ref ulong x)
    {
        ulong z = x += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }
}

/// <summary>Counter-based hashing so random values can be looked up by (seed, stream, index) without storing sequences.</summary>
internal static class Hash
{
    public static ulong Mix(ulong z)
    {
        z += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    public static ulong Combine(params ulong[] keys)
    {
        ulong h = 0x243F6A8885A308D3UL;
        foreach (var k in keys) h = Mix(h ^ Mix(k));
        return h;
    }

    public static ulong Combine(ulong a, ulong b, ulong c) => Mix(Mix(Mix(0x243F6A8885A308D3UL ^ Mix(a)) ^ Mix(b)) ^ Mix(c));

    /// <summary>Standard normal deviate addressed by key (Box-Muller on two hashed uniforms).</summary>
    public static double Gaussian(ulong a, ulong b, ulong c)
    {
        ulong h1 = Combine(a, b, c);
        ulong h2 = Mix(h1 ^ 0xD1B54A32D192ED03UL);
        double u1 = ((h1 >> 11) + 1) * (1.0 / (1UL << 53));
        double u2 = (h2 >> 11) * (1.0 / (1UL << 53));
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }
}

/// <summary>
/// Poisson deviates of one fixed mean by inversion of the cumulative distribution, for the sky background of a
/// frame. Exact to double precision: the table sums the probabilities until the terms fall below 1e-17, and what is
/// left (the tail and the rounding, of order 1e-16) goes to its last value. O(1) per deviate through a guide table
/// (Chen and Asau 1974), where <see cref="SimRng.NextPoisson"/> costs O(mean).
/// </summary>
internal sealed class PoissonTable
{
    private const int GuideBits = 8;

    private readonly double[] cdf;
    private readonly int[] guide = new int[1 << GuideBits];

    public PoissonTable(double mean)
    {
        if (!(mean > 0 && mean < 700)) throw new ArgumentOutOfRangeException(nameof(mean), mean, "0 < mean < 700 (e^−mean must not underflow)");
        Mean = mean;
        var list = new List<double>();
        double p = Math.Exp(-mean), sum = p;
        list.Add(sum);
        for (int k = 1; k <= mean || p >= 1e-17; k++)
        {
            p *= mean / k;
            sum += p;
            list.Add(sum);
        }

        list[^1] = 1.0;
        cdf = [.. list];
        int j = 0;
        for (int i = 0; i < guide.Length; i++)
        {
            double u = (double)i / guide.Length;
            while (cdf[j] <= u) j++;
            guide[i] = j;
        }
    }

    public double Mean { get; }

    /// <summary>The largest value a deviate can take.</summary>
    public int MaxValue => cdf.Length - 1;

    /// <summary>A deviate from 64 random bits (the top 53 form the uniform that is inverted).</summary>
    public int Sample(ulong bits)
    {
        double u = (bits >> 11) * (1.0 / (1UL << 53));
        int k = guide[(int)(bits >> (64 - GuideBits))];
        while (cdf[k] <= u) k++;
        return k;
    }
}

/// <summary>
/// Pre-computed table of standard normal deviates used for the per-pixel noise field. Indexing it with
/// independent uniform 16-bit indices yields i.i.d. samples of a 65536-point empirical normal
/// distribution (exact mean 0, variance 1), which is statistically adequate for sensor noise and much
/// faster than generating a fresh deviate per pixel.
/// </summary>
internal static class GaussianTable
{
    public const int Size = 1 << 16;

    public static readonly float[] Values = Build();

    private static float[] Build()
    {
        var rng = new SimRng(0x5EED_6A55UL);
        var v = new double[Size];
        double sum = 0;
        for (int i = 0; i < Size; i++)
        {
            v[i] = rng.NextGaussian();
            sum += v[i];
        }

        double mean = sum / Size, ss = 0;
        for (int i = 0; i < Size; i++)
        {
            v[i] -= mean;
            ss += v[i] * v[i];
        }

        double scale = 1.0 / Math.Sqrt(ss / Size);
        var f = new float[Size];
        for (int i = 0; i < Size; i++) f[i] = (float)(v[i] * scale);
        return f;
    }
}
