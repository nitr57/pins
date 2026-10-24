// SPDX-License-Identifier: MPL-2.0

using NINA.GuideEngine.Core;

namespace NINA.GuideEngine.Simulation;

/// <summary>Ground truth for one star (frame pixel coordinates at the frame's binning).</summary>
/// <param name="Index">Index into <see cref="SimulatedSky.Stars"/>.</param>
/// <param name="X">Expected centroid X: mean position over the exposure including seeing (noise free).</param>
/// <param name="Y">Expected centroid Y.</param>
/// <param name="NoSeeingX">Mean position over the exposure from the mount pointing only.</param>
/// <param name="NoSeeingY">Mean position over the exposure from the mount pointing only.</param>
/// <param name="FluxElectrons">Total electrons deposited by the star in the frame (0 when occluded).</param>
/// <param name="Magnitude">Instrumental magnitude.</param>
/// <param name="InFrame">True when the position lies inside the frame (and subframe, if any).</param>
public sealed record StarTruth(int Index, double X, double Y, double NoSeeingX, double NoSeeingY, double FluxElectrons, double Magnitude, bool InFrame);

/// <summary>Ground truth of one rendered frame.</summary>
/// <param name="MeanPointingError">Mount pointing error averaged over the exposure (arcsec on the sky, no seeing).</param>
public sealed record FrameTruth(long FrameNumber, double StartSec, double ExposureMs, int Binning, PierSide PierSide, SkyOffset MeanPointingError, double Transparency, IReadOnlyList<StarTruth> Stars);

/// <summary>Kinds of injectable capture faults.</summary>
public enum CaptureFault
{
    /// <summary>The exposure completes but the download fails (exception after the exposure time).</summary>
    Failure,

    /// <summary>No image arrives; exception after exposure + <see cref="CameraSimConfig.TimeoutMs"/>.</summary>
    Timeout,

    /// <summary>The camera drops off; captures fail until <see cref="SimulatedCamera.ReconnectAsync"/>.</summary>
    Disconnect,
}

/// <summary>
/// Simulated guide camera: renders the <see cref="SimulatedSky"/> as seen through the
/// <see cref="SimulatedMount"/> onto a 16-bit sensor with a physical noise model.
/// </summary>
/// <remarks>
/// <para>
/// Sky→sensor mapping (unbinned pixels, y down): a sky vector (East e, North n) in arcsec maps to
/// <c>(e·E + n·N) / scale</c> with <c>E = (cos θ, sin θ)</c> and <c>N = (−sin θ, cos θ)</c>
/// (<c>N = (sin θ, −cos θ)</c> when <see cref="CameraSimConfig.Mirrored"/>), θ =
/// <see cref="CameraSimConfig.CameraAngleDeg"/>, plus 180° on pier side West. A star sits at
/// <c>centre + map(starOffset − pointingError + seeing)</c>, so a West pulse (pointing moves West)
/// moves stars along +E on the sensor. The sensor centre is ((W−1)/2, (H−1)/2); pixel centres are
/// integer coordinates; binned coordinates are <c>(x − (b−1)/2) / b</c>.
/// </para>
/// <para>
/// Image model per binned pixel in electrons: sky + dark current (×b²) + stars (Moffat PSF, flux
/// normalised to the rendered window, motion over the exposure integrated with up to
/// <see cref="CameraSimConfig.MaxMotionSubSamples"/> PSF copies), then Poisson shot noise and Gaussian
/// read noise, converted to ADU with the conversion gain of the requested gain setting (see
/// <see cref="CameraSimConfig.GainUnitsPerDecade"/>), bias added, fixed hot/cold pixels applied and clamped to
/// <see cref="MaxAdu"/>. Noise is seeded per (seed, frame, row), so frames are reproducible.
/// </para>
/// </remarks>
public sealed class SimulatedCamera : ICameraSource, IGainRange
{
    private readonly object renderGate = new();
    private readonly object faultGate = new();
    private readonly IClock clock;
    private readonly Queue<CaptureFault> injectedFaults = new();
    private readonly (int X, int Y, ushort Value)[] hotPixels;
    private readonly (int X, int Y)[] coldPixels;
    private readonly double cosT;
    private readonly double sinT;
    private float[] starBuffer = [];
    private double[] psfWeights = [];
    private long frameCounter;
    private CancellationTokenSource? currentCapture;
    private volatile FrameTruth? lastTruth;
    private int currentGain;

    public SimulatedCamera(CameraSimConfig config, SimulatedSky sky, SimulatedMount mount, IClock clock)
    {
        Config = config;
        Sky = sky;
        Mount = mount;
        this.clock = clock;
        cosT = Math.Cos(config.CameraAngleDeg * Math.PI / 180.0);
        sinT = Math.Sin(config.CameraAngleDeg * Math.PI / 180.0);
        MaxAdu = (ushort)Math.Min(config.MaxAdu, (1 << Math.Clamp(config.BitsPerPixel, 8, 16)) - 1);
        currentGain = ClampGain(config.DefaultGain);

        var rng = new SimRng((ulong)config.DefectSeed, 0xDEFEC7UL);
        hotPixels = new (int, int, ushort)[Math.Max(0, config.HotPixelCount)];
        for (int i = 0; i < hotPixels.Length; i++)
        {
            int x = (int)(rng.NextDouble() * config.SensorWidth), y = (int)(rng.NextDouble() * config.SensorHeight);
            ushort v = rng.NextDouble() < 0.5
                ? MaxAdu
                : (ushort)Math.Min(MaxAdu, config.BiasAdu + rng.Uniform(0.1, 0.8) * (MaxAdu - config.BiasAdu));
            hotPixels[i] = (x, y, v);
        }

        coldPixels = new (int, int)[Math.Max(0, config.ColdPixelCount)];
        for (int i = 0; i < coldPixels.Length; i++)
            coldPixels[i] = ((int)(rng.NextDouble() * config.SensorWidth), (int)(rng.NextDouble() * config.SensorHeight));
    }

    public CameraSimConfig Config { get; }

    public SimulatedSky Sky { get; }

    public SimulatedMount Mount { get; }

    public string Name => Config.Name;

    public bool IsConnected { get; set; } = true;

    public int SensorWidth => Config.SensorWidth;

    public int SensorHeight => Config.SensorHeight;

    public double PixelSizeUm => Config.PixelSizeUm;

    public int MaxBinning => Config.MaxBinning;

    /// <summary>Saturation level: <see cref="CameraSimConfig.MaxAdu"/> clamped to the bit depth.</summary>
    public ushort MaxAdu { get; }

    public int BitsPerPixel => Config.BitsPerPixel;

    public int? GainMin => Config.GainMin;

    public int? GainMax => Config.GainMax;

    /// <summary>Gain of the last capture (or <see cref="CameraSimConfig.DefaultGain"/>); requests without a gain keep it.</summary>
    public int? CurrentGain => Volatile.Read(ref currentGain);

    /// <summary>Unbinned image scale, arcsec/px.</summary>
    public double PixelScale => Config.PixelScale;

    /// <summary>Guide optics equivalent of this camera (for engine code needing the image scale).</summary>
    public GuideOptics Optics => new(Config.FocalLengthMm, Config.PixelSizeUm);

    /// <summary>Truth of the most recently rendered frame, null before the first frame.</summary>
    public FrameTruth? LastFrameTruth => lastTruth;

    /// <summary>Number of captures started (including failed ones).</summary>
    public long FrameCount => Interlocked.Read(ref frameCounter);

    /// <summary>Hot pixel pattern (unbinned sensor coordinates and ADU value).</summary>
    public IReadOnlyList<(int X, int Y, ushort Value)> HotPixels => hotPixels;

    /// <summary>Cold (dead) pixel pattern, unbinned sensor coordinates; rendered as 0 ADU.</summary>
    public IReadOnlyList<(int X, int Y)> ColdPixels => coldPixels;

    /// <summary>Queues <paramref name="count"/> faults for the next captures.</summary>
    public void InjectFaults(CaptureFault kind, int count = 1)
    {
        lock (faultGate)
        {
            for (int i = 0; i < count; i++) injectedFaults.Enqueue(kind);
        }
    }

    public async Task<GuideFrame> CaptureAsync(CaptureRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!IsConnected) throw new GuideCameraException("simulated camera is not connected");
        int bin = Math.Max(1, request.Binning);
        if (bin > MaxBinning) throw new GuideCameraException($"binning {bin} not supported (max {MaxBinning})");

        int gain = request.Gain is { } g ? ClampGain(g) : Volatile.Read(ref currentGain);
        Volatile.Write(ref currentGain, gain);
        long frameNo = Interlocked.Increment(ref frameCounter);
        double t0 = clock.NowSeconds();
        var startTime = clock.UtcNow;
        double expMs = Math.Max(0, request.ExposureMs);
        var fault = NextFault(frameNo);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Interlocked.Exchange(ref currentCapture, cts);
        try
        {
            if (fault == CaptureFault.Timeout)
            {
                await clock.Delay(TimeSpan.FromMilliseconds(expMs + Config.TimeoutMs), cts.Token).ConfigureAwait(false);
                throw new GuideCameraException("simulated exposure timed out");
            }

            await clock.Delay(TimeSpan.FromMilliseconds(expMs), cts.Token).ConfigureAwait(false);
            if (fault == CaptureFault.Failure) throw new GuideCameraException("simulated exposure failed");
            if (fault == CaptureFault.Disconnect)
            {
                IsConnected = false;
                throw new GuideCameraException("simulated camera disconnected");
            }

            var frame = Render(t0, expMs, bin, request.Subframe, frameNo, request.Offset, gain);
            frame.StartTime = startTime;
            await clock.Delay(TimeSpan.FromMilliseconds(Config.DownloadMs), cts.Token).ConfigureAwait(false);
            return frame;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new GuideCameraException("simulated exposure aborted");
        }
        finally
        {
            Interlocked.CompareExchange(ref currentCapture, null, cts);
        }
    }

    public Task AbortAsync()
    {
        try
        {
            Volatile.Read(ref currentCapture)?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // capture already finished
        }

        return Task.CompletedTask;
    }

    public Task ReconnectAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        IsConnected = true;
        return Task.CompletedTask;
    }

    /// <summary>Converts a sky displacement (arcsec, East/North) to a sensor displacement in binned pixels.</summary>
    public (double Dx, double Dy) SkyToSensorVector(SkyOffset v, PierSide pierSide, int binning = 1)
    {
        double c = cosT, s = sinT;
        if (pierSide == PierSide.West)
        {
            c = -c;
            s = -s;
        }

        double nx = Config.Mirrored ? s : -s, ny = Config.Mirrored ? -c : c;
        double scale = Config.PixelScale * binning;
        return ((v.Ra * c + v.Dec * nx) / scale, (v.Ra * s + v.Dec * ny) / scale);
    }

    /// <summary>
    /// Inverse mapping for pier side East and zero pointing error: unbinned sensor position to the sky
    /// offset from the field centre.
    /// </summary>
    public SkyOffset SensorToSky(double x, double y)
    {
        double dx = (x - (SensorWidth - 1) / 2.0) * Config.PixelScale;
        double dy = (y - (SensorHeight - 1) / 2.0) * Config.PixelScale;
        double nx = Config.Mirrored ? sinT : -sinT, ny = Config.Mirrored ? -cosT : cosT;
        return new SkyOffset(dx * cosT + dy * sinT, dx * nx + dy * ny);
    }

    /// <summary>Frame pixel position (at <paramref name="binning"/>) of a sky point at <paramref name="relativeToPointing"/> from the optical axis.</summary>
    public GuidePoint SkyToSensor(SkyOffset relativeToPointing, PierSide pierSide, int binning = 1)
    {
        var (dx, dy) = SkyToSensorVector(relativeToPointing, pierSide, 1);
        double xu = (SensorWidth - 1) / 2.0 + dx, yu = (SensorHeight - 1) / 2.0 + dy;
        double off = (binning - 1) / 2.0;
        return new GuidePoint((xu - off) / binning, (yu - off) / binning);
    }

    /// <summary>True (seeing-free) star positions at time <paramref name="t"/> for the current pier side.</summary>
    public IReadOnlyList<StarTruth> StarPositionsAt(double t, int binning = 1)
    {
        var pointing = Mount.GetPointingError(t);
        var side = Mount.PierSide;
        int wb = SensorWidth / binning, hb = SensorHeight / binning;
        var list = new StarTruth[Sky.Stars.Count];
        for (int i = 0; i < list.Length; i++)
        {
            var st = Sky.Stars[i];
            var p = SkyToSensor(new SkyOffset(st.EastArcsec, st.NorthArcsec) - pointing, side, binning);
            bool inFrame = p.X >= 0 && p.Y >= 0 && p.X <= wb - 1 && p.Y <= hb - 1;
            list[i] = new StarTruth(i, p.X, p.Y, p.X, p.Y, 0, st.Magnitude, inFrame);
        }

        return list;
    }

    /// <summary>
    /// Renders a frame for an exposure starting at <paramref name="t0"/> seconds without touching the
    /// clock. Used by <see cref="CaptureAsync"/>; public for tests and benchmarks.
    /// </summary>
    public GuideFrame Render(double t0, double exposureMs, int binning, IntRect subframe, long frameNumber, int? biasOverride = null, int? gain = null)
    {
        int b = Math.Max(1, binning);
        int wb = SensorWidth / b, hb = SensorHeight / b;
        var full = new IntRect(0, 0, wb, hb);
        var roi = subframe.IsEmpty ? full : subframe.Intersect(full);
        var frame = new GuideFrame(wb, hb)
        {
            Subframe = subframe.IsEmpty ? IntRect.Empty : roi,
            BitsPerPixel = Config.BitsPerPixel,
            Binning = b,
            ExposureMs = exposureMs,
            FrameNumber = frameNumber,
        };

        lock (renderGate)
        {
            if (starBuffer.Length < wb * hb) starBuffer = new float[wb * hb];
            else Array.Clear(starBuffer, 0, wb * hb);

            var truth = RenderStars(t0, exposureMs, b, wb, hb, roi, frameNumber);
            AddNoise(frame, roi, exposureMs, b, frameNumber, biasOverride ?? Config.BiasAdu, ElectronsPerAdu(gain ?? Volatile.Read(ref currentGain)));
            ApplyDefects(frame, roi, b);
            lastTruth = truth;
        }

        return frame;
    }

    private CaptureFault? NextFault(long frameNo)
    {
        lock (faultGate)
        {
            if (injectedFaults.Count > 0) return injectedFaults.Dequeue();
        }

        double pf = Config.ExposureFailureProbability, pt = Config.ExposureTimeoutProbability;
        if (pf <= 0 && pt <= 0) return null;
        double u = new SimRng((ulong)Config.NoiseSeed, 0xFA17UL, (ulong)frameNo).NextDouble();
        if (u < pf) return CaptureFault.Failure;
        if (u < pf + pt) return CaptureFault.Timeout;
        return null;
    }

    private FrameTruth RenderStars(double t0, double exposureMs, int b, int wb, int hb, IntRect roi, long frameNumber)
    {
        var sky = Sky.Config;
        double expSec = exposureMs / 1000.0;
        int k = expSec <= 0
            ? 1
            : (int)Math.Clamp(Math.Ceiling(expSec / Math.Max(sky.SeeingCoherenceMs / 1000.0, 1e-3)), 1, Math.Max(1, Config.MaxMotionSubSamples));
        double slice = expSec / k;

        // sample the mount and atmosphere once per sub-sample
        var side = Mount.PierSide;
        var pointing = new SkyOffset[k];
        var transparency = new double[k];
        var meanPointing = SkyOffset.Zero;
        double meanTransparency = 0;
        for (int j = 0; j < k; j++)
        {
            double tm = t0 + (j + 0.5) * slice;
            pointing[j] = Mount.GetPointingError(tm);
            transparency[j] = Sky.TransparencyAt(tm);
            meanPointing += pointing[j] * (1.0 / k);
            meanTransparency += transparency[j] / k;
        }

        double fwhmPx = sky.FwhmArcsec / (Config.PixelScale * b);
        double beta = Math.Max(1.01, sky.MoffatBeta);
        double alpha = fwhmPx / (2.0 * Math.Sqrt(Math.Pow(2.0, 1.0 / beta) - 1.0));
        double rCut = alpha * Math.Sqrt(Math.Pow(1e-3, 1.0 / (1.0 - beta)) - 1.0);
        int radius = (int)Math.Clamp(Math.Ceiling(rCut), 3, 48);
        int os = Config.PsfOversampling > 0 ? Config.PsfOversampling : fwhmPx < 2.5 ? 3 : 1;

        var stars = new StarTruth[Sky.Stars.Count];
        for (int i = 0; i < stars.Length; i++)
        {
            var st = Sky.Stars[i];
            var starSky = new SkyOffset(st.EastArcsec, st.NorthArcsec);
            double rate = Math.Pow(10.0, -0.4 * (st.Magnitude - Config.ZeroPointMagnitude));
            double sx = 0, sy = 0, sf = 0, nx = 0, ny = 0, px = 0, py = 0;
            for (int j = 0; j < k; j++)
            {
                double ta = t0 + j * slice;
                var seeing = Sky.SeeingOffset(i, ta, ta + Math.Max(slice, 1e-6));
                var clean = SkyToSensor(starSky - pointing[j], side, b);
                var p = SkyToSensor(starSky - pointing[j] + seeing, side, b);
                double tm = ta + 0.5 * slice;
                double flux = Sky.IsOccluded(i, tm) ? 0 : rate * slice * transparency[j];
                if (expSec <= 0) flux = 0;
                px += p.X / k;
                py += p.Y / k;
                nx += clean.X / k;
                ny += clean.Y / k;
                sx += p.X * flux;
                sy += p.Y * flux;
                sf += flux;
                if (flux > 0) RenderPsf(p.X, p.Y, flux, alpha, beta, radius, os, wb, roi);
            }

            double mx = sf > 0 ? sx / sf : px, my = sf > 0 ? sy / sf : py;
            bool inFrame = roi.Contains(mx, my);
            stars[i] = new StarTruth(i, mx, my, nx, ny, sf, st.Magnitude, inFrame);
        }

        return new FrameTruth(frameNumber, t0, exposureMs, b, side, meanPointing, meanTransparency, stars);
    }

    private void RenderPsf(double cx, double cy, double flux, double alpha, double beta, int radius, int os, int wb, IntRect roi)
    {
        int ix = (int)Math.Round(cx), iy = (int)Math.Round(cy);
        int x0 = Math.Max(ix - radius, roi.Left), x1 = Math.Min(ix + radius, roi.Right);
        int y0 = Math.Max(iy - radius, roi.Top), y1 = Math.Min(iy + radius, roi.Bottom);

        // normalise over the full (unclipped) window so clipping at the edge loses flux like a real sensor
        int n = 2 * radius + 1;
        if (psfWeights.Length < n * n) psfWeights = new double[n * n];
        double inv2 = 1.0 / (alpha * alpha);
        bool beta3 = beta == 3.0;
        double sum = 0;
        for (int yy = 0; yy < n; yy++)
        {
            double py = iy - radius + yy - cy;
            for (int xx = 0; xx < n; xx++)
            {
                double pxc = ix - radius + xx - cx;
                double w = 0;
                for (int sy = 0; sy < os; sy++)
                {
                    double dy = py + (sy + 0.5) / os - 0.5;
                    for (int sx = 0; sx < os; sx++)
                    {
                        double dx = pxc + (sx + 0.5) / os - 0.5;
                        double q = 1.0 / (1.0 + (dx * dx + dy * dy) * inv2);
                        w += beta3 ? q * q * q : Math.Pow(q, beta);
                    }
                }

                psfWeights[yy * n + xx] = w;
                sum += w;
            }
        }

        if (sum <= 0) return;
        double scale = flux / sum;
        for (int y = y0; y <= y1; y++)
        {
            int row = y * wb, wrow = (y - (iy - radius)) * n - (ix - radius);
            for (int x = x0; x <= x1; x++) starBuffer[row + x] += (float)(psfWeights[wrow + x] * scale);
        }
    }

    /// <summary>Conversion gain (electrons per ADU) at a gain setting.</summary>
    public double ElectronsPerAdu(int gain) =>
        Config.GainElectronsPerAdu / Math.Pow(10.0, gain / Math.Max(1e-6, Config.GainUnitsPerDecade));

    private int ClampGain(int gain) => Math.Clamp(gain, Math.Min(Config.GainMin, Config.GainMax), Math.Max(Config.GainMin, Config.GainMax));

    private void AddNoise(GuideFrame frame, IntRect roi, double exposureMs, int b, long frameNumber, double bias, double electronsPerAdu)
    {
        double expSec = exposureMs / 1000.0;
        double baseE = (Config.SkyBackgroundElectronsPerSec + Config.DarkCurrentElectronsPerSec) * expSec * b * b;
        double rn = Config.ReadNoiseElectrons;
        double rn2 = rn * rn;
        double invGain = 1.0 / Math.Max(1e-6, electronsPerAdu);
        double baseSigma = Math.Sqrt(baseE + rn2);
        bool baseGaussian = baseE >= 20;
        // below 20 e⁻ the background is Poisson; one table per frame instead of a Knuth draw (O(mean)) per pixel
        var basePoisson = !baseGaussian && baseE > 0 ? new PoissonTable(baseE) : null;
        int wb = frame.Width;
        double maxVal = MaxAdu;
        var pixels = frame.Pixels;
        var stars = starBuffer;
        var table = GaussianTable.Values;
        ulong seed = (ulong)Config.NoiseSeed;

        Parallel.For(roi.Top, roi.Bottom + 1, y =>
        {
            var rng = new SimRng(Hash.Combine(seed, (ulong)frameNumber, (ulong)y));
            ulong bits = 0;
            int left = 0;
            int row = y * wb;
            for (int x = roi.Left; x <= roi.Right; x++)
            {
                if (left == 0)
                {
                    bits = rng.NextULong();
                    left = 4;
                }

                double g = table[(int)(bits & 0xFFFF)];
                bits >>= 16;
                left--;

                double s = stars[row + x];
                double e;
                if (s == 0 && baseGaussian) e = baseE + baseSigma * g;
                else if (s == 0 && basePoisson != null) e = basePoisson.Sample(rng.NextULong()) + rn * g;
                else
                {
                    double mu = baseE + s;
                    e = mu >= 20 ? mu + Math.Sqrt(mu + rn2) * g : rng.NextPoisson(mu) + rn * g;
                }

                double adu = bias + e * invGain;
                pixels[row + x] = adu <= 0 ? (ushort)0 : adu >= maxVal ? (ushort)maxVal : (ushort)(adu + 0.5);
            }
        });
    }

    private void ApplyDefects(GuideFrame frame, IntRect roi, int b)
    {
        foreach (var (x, y, v) in hotPixels)
        {
            int bx = x / b, by = y / b;
            if (roi.Contains(bx, by) && bx < frame.Width && by < frame.Height)
                frame[bx, by] = Math.Max(frame[bx, by], v);
        }

        foreach (var (x, y) in coldPixels)
        {
            int bx = x / b, by = y / b;
            if (roi.Contains(bx, by) && bx < frame.Width && by < frame.Height) frame[bx, by] = 0;
        }
    }
}
