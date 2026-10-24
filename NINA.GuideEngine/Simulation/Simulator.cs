// SPDX-License-Identifier: MPL-2.0

using NINA.GuideEngine.Core;

namespace NINA.GuideEngine.Simulation;

/// <summary>Faults that can be injected into a running simulator (<see cref="Simulator.InjectFault"/>), e.g. to try the flight recorder.</summary>
public enum SimulatorFault
{
    /// <summary>The transparency ramps to 0 over 10 s, stays at 0 for 90 s and ramps back over 10 s.</summary>
    Clouds,

    /// <summary>The mount jumps by 15 sensor pixels in a random direction, once.</summary>
    Bump,

    /// <summary>
    /// Guide pulses have no effect for 60 s. At the start the RA axis stalls for a moment (a jump of 7 sensor pixels), so that
    /// the guider sends pulses large enough to notice.
    /// </summary>
    MountStopsResponding,

    /// <summary>The next 3 exposures fail.</summary>
    CameraFailure,

    /// <summary>Dec pulses move the wrong way for 120 s. A nudge of 3 sensor pixels in Dec at the start makes the guider correct.</summary>
    Runaway,
}

/// <summary>
/// Closed-loop sky / mount / camera simulator wired from a <see cref="SimulatorScenario"/>. Hand
/// <see cref="Camera"/> (ICameraSource), <see cref="Mount"/> (IPulseOutput, IMountState) and
/// <see cref="Clock"/> (IClock) to the engine; use the truth accessors to evaluate guiding.
/// </summary>
public sealed class Simulator
{
    // the injected faults, as documented on SimulatorFault
    private const double CloudSeconds = 110.0;
    private const double CloudRampSeconds = 10.0;
    private const double BumpPx = 15.0;
    private static readonly TimeSpan StallFor = TimeSpan.FromSeconds(60);
    private const double StallKickPx = 7.0;
    private const int CameraFailures = 3;
    private static readonly TimeSpan RunawayFor = TimeSpan.FromSeconds(120);
    private const double RunawayNudgePx = 3.0;

    private long bumpCount;

    /// <summary>Creates the simulator. Without <paramref name="clock"/> a fresh <see cref="Simulation.VirtualClock"/> is used.</summary>
    public Simulator(SimulatorScenario scenario, IClock? clock = null)
    {
        Scenario = scenario;
        Clock = clock ?? new VirtualClock();
        Mount = new SimulatedMount(scenario.Mount, Clock);

        // The random field needs the camera geometry; build a geometry-only camera first.
        var stars = scenario.Sky.Stars;
        if (stars == null)
        {
            var geometry = new SimulatedCamera(scenario.Camera with { HotPixelCount = 0, ColdPixelCount = 0 }, new SimulatedSky(scenario.Sky, []), Mount, Clock);
            stars = SimulatedSky.GenerateRandomField(scenario.Sky, geometry.SensorToSky, scenario.Camera.SensorWidth, scenario.Camera.SensorHeight);
        }

        Sky = new SimulatedSky(scenario.Sky, stars);
        Camera = new SimulatedCamera(scenario.Camera, Sky, Mount, Clock);
    }

    public SimulatorScenario Scenario { get; }

    public IClock Clock { get; }

    /// <summary>The virtual clock, or null when the simulator runs on another clock.</summary>
    public VirtualClock? VirtualClock => Clock as VirtualClock;

    public SimulatedMount Mount { get; }

    public SimulatedSky Sky { get; }

    public SimulatedCamera Camera { get; }

    /// <summary>Current simulator time in seconds.</summary>
    public double Now => Clock.NowSeconds();

    /// <summary>True mount pointing error (arcsec on the sky) at time <paramref name="t"/>.</summary>
    public SkyOffset TruePointingError(double t) => Mount.GetPointingError(t);

    /// <summary>True mount pointing error now.</summary>
    public SkyOffset TruePointingError() => Mount.GetPointingError(Now);

    /// <summary>True (seeing-free) star positions in frame pixels at time <paramref name="t"/>.</summary>
    public IReadOnlyList<StarTruth> TrueStarPositions(double t, int binning = 1) => Camera.StarPositionsAt(t, binning);

    /// <summary>Truth of the last rendered frame.</summary>
    public FrameTruth? LastFrameTruth => Camera.LastFrameTruth;

    /// <summary>
    /// Index of the brightest star whose position stays at least <paramref name="marginPx"/> from the
    /// frame edges (seeing-free, at time <paramref name="t"/>); -1 when none.
    /// </summary>
    public int BrightestStarIndex(double t = 0, int marginPx = 30, int binning = 1)
    {
        var pos = TrueStarPositions(t, binning);
        int wb = Camera.SensorWidth / binning, hb = Camera.SensorHeight / binning;
        int best = -1;
        for (int i = 0; i < pos.Count; i++)
        {
            var p = pos[i];
            if (p.X < marginPx || p.Y < marginPx || p.X > wb - 1 - marginPx || p.Y > hb - 1 - marginPx) continue;
            if (best < 0 || Sky.Stars[i].Magnitude < Sky.Stars[best].Magnitude) best = i;
        }

        return best;
    }

    /// <summary>Injects <paramref name="fault"/> now (see <see cref="SimulatorFault"/>). Thread-safe.</summary>
    public void InjectFault(SimulatorFault fault)
    {
        double now = Now;
        double pixel = Camera.Config.PixelScale;
        switch (fault)
        {
            case SimulatorFault.Clouds:
                Sky.AddTransparencyWindow(new TransparencyWindow(now, now + CloudSeconds, 0.0, CloudRampSeconds));
                break;
            case SimulatorFault.Bump:
                var rng = new SimRng((ulong)Scenario.Mount.Seed, 0xB0B0UL, (ulong)Interlocked.Increment(ref bumpCount));
                double angle = rng.Uniform(0, 2 * Math.PI);
                Mount.Bump(new SkyOffset(BumpPx * pixel * Math.Cos(angle), BumpPx * pixel * Math.Sin(angle)));
                break;
            case SimulatorFault.MountStopsResponding:
                Mount.StopRespondingFor(StallFor);
                Mount.Bump(new SkyOffset(StallKickPx * pixel, 0));
                break;
            case SimulatorFault.CameraFailure:
                Camera.InjectFaults(CaptureFault.Failure, CameraFailures);
                break;
            case SimulatorFault.Runaway:
                Mount.InvertDecPulsesFor(RunawayFor);
                Mount.Bump(new SkyOffset(0, RunawayNudgePx * pixel));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(fault));
        }
    }

    /// <summary>Convenience: <see cref="CaptureRequest"/> round trip on <see cref="Camera"/>.</summary>
    public Task<GuideFrame> CaptureAsync(double exposureMs, CancellationToken ct = default, int binning = 1) =>
        Camera.CaptureAsync(new CaptureRequest(exposureMs, binning), ct);
}
