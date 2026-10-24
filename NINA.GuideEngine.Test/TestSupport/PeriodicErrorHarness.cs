// SPDX-License-Identifier: MPL-2.0

using NINA.GuideEngine.Algorithms;
using NINA.GuideEngine.Core;
using NINA.GuideEngine.Guiding;
using NINA.GuideEngine.Simulation;

namespace NINA.GuideEngine.Test.TestSupport;

/// <summary>
/// Closed-loop runs with the periodic-error prediction: a worm that behaves like a real one (180 teeth, the curve locked
/// to the RA axis angle, as large as on a cheap mount), guided with Predictive on both axes, with the simulator's true
/// RA error per frame.
/// </summary>
internal static class PeriodicErrorHarness
{
    private static readonly SettleParams Settle = new(1.5, 10, 120);

    internal static readonly double WormPeriod = Sidereal.DaySeconds / 180;

    internal static SimulatorScenario Worm(double raHours = 6.0, double siderealHours = 5.0, double decDeg = 20.0, int teeth = 180) => SimulatorScenario.GoodMount with
    {
        Name = $"Worm{teeth}",
        Mount = SimulatorScenario.GoodMount.Mount with
        {
            PeriodicError =
            [
                new PeriodicErrorTerm(15.0, Sidereal.DaySeconds / teeth),
                new PeriodicErrorTerm(5.0, Sidereal.DaySeconds / teeth / 2, 0.7),
                new PeriodicErrorTerm(2.0, Sidereal.DaySeconds / teeth / 3, 2.1),
            ],
            PeriodicErrorFollowsAxis = true,
            RightAscensionHours = raHours,
            SiderealTimeAtStartHours = siderealHours,
            DeclinationDeg = decDeg,
        },
    };

    internal static async Task<Run> Guide(SimulatorScenario scenario, bool periodicError, TimeSpan duration, PeriodicErrorModel? restore = null,
        bool mountInfo = true, int teeth = 0, (TimeSpan At, Action<Guider, Simulator> Do)? midway = null, Action<Guider>? prepare = null)
    {
        var clock = new VirtualClock();
        var sim = new Simulator(scenario, clock);
        var ra = new AlgorithmSettings(GuideAlgorithmKind.Predictive,
            new Dictionary<string, double> { ["periodicError"] = periodicError ? 1 : 0, ["wormTeeth"] = teeth });
        var settings = new GuiderSettings
        {
            FocalLengthMm = scenario.Camera.FocalLengthMm, ExposureMs = 2000, RaAlgorithm = ra, DecAlgorithm = new AlgorithmSettings(GuideAlgorithmKind.Predictive),
        };
        var guider = new Guider(sim.Camera, sim.Mount, mountInfo ? sim.Mount : new NoMountInfo(), clock, settings, ditherSeed: 5) { AutoStartLoop = false };
        if (restore is not null)
        {
            guider.RestorePeriodicError(restore);
        }

        prepare?.Invoke(guider);

        var run = new Run(sim, clock);
        guider.EventRaised += (_, e) =>
        {
            lock (run)
            {
                if (e is GuideStepEvent { IsSettling: false } s)
                {
                    run.Steps.Add((s.Timestamp, ((PredictiveAlgorithm)guider.RaAlgorithm).PredictingPeriodicError));
                    if (midway is { } m && !run.MidwayDone && s.Timestamp - clock.Epoch >= m.At)
                    {
                        run.MidwayDone = true;
                        m.Do(guider, sim);
                    }
                }
                else if (e is AlgorithmNoteEvent { Axis: GuideAxis.Ra, Kind: PredictiveNoteKind.PeriodicError } n)
                {
                    run.Notes.Add(n.Message);
                }
                else if (e is PeriodicErrorModelEvent m)
                {
                    run.Models.Add(m.Model);
                }
                else if (e is PeriodicErrorModelDiscardedEvent d)
                {
                    run.Discarded.Add(d.Model);
                }
            }
        };
        _ = guider.StartGuidingAsync(Settle);
        guider.StartLooping(clock.CancelAt(clock.Elapsed + duration));
        await guider.WaitForLoopAsync().Within(duration);
        run.Final = ((PredictiveAlgorithm)guider.RaAlgorithm).PeriodicError?.Describe();
        return run;
    }

    /// <summary>Guide steps with the true pointing error from the simulator.</summary>
    internal sealed class Run(Simulator sim, VirtualClock clock)
    {
        public List<(DateTimeOffset Time, bool Predicting)> Steps { get; } = [];

        public List<PeriodicErrorModel> Models { get; } = [];

        public List<PeriodicErrorModel> Discarded { get; } = [];

        public List<string> Notes { get; } = [];

        public bool MidwayDone { get; set; }

        /// <summary>The periodic-error fit at the end of the run.</summary>
        public string? Final { get; set; }

        public double RaRms(double fromMin, double toMin)
        {
            var a = Window(fromMin, toMin).Select(s => sim.Mount.GetPointingErrorBreakdown((s.Time - clock.Epoch).TotalSeconds).Total.Ra).ToArray();
            double mean = a.Average();
            return Math.Sqrt(a.Sum(v => (v - mean) * (v - mean)) / a.Length);
        }

        public double PredictingFraction(double fromMin, double toMin)
        {
            var w = Window(fromMin, toMin).ToList();
            return w.Count(s => s.Predicting) / (double)w.Count;
        }

        private IEnumerable<(DateTimeOffset Time, bool Predicting)> Window(double fromMin, double toMin) =>
            Steps.Where(s => (s.Time - clock.Epoch).TotalMinutes >= fromMin && (s.Time - clock.Epoch).TotalMinutes < toMin);
    }

    /// <summary>ST4 guiding without a mount connection, as the plugin reports it: tracking assumed, no coordinates or sidereal time.</summary>
    internal sealed class NoMountInfo : IMountState
    {
        public MountSnapshot GetSnapshot() => new() { IsConnected = true, IsTracking = true, PierSide = PierSide.Unknown };
    }
}
