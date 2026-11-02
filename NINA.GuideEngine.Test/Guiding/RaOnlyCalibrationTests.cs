// SPDX-License-Identifier: MPL-2.0

using FluentAssertions;
using NUnit.Framework;
using NINA.GuideEngine.Calibration;
using NINA.GuideEngine.Core;
using NINA.GuideEngine.Guiding;
using NINA.GuideEngine.Simulation;
using NINA.GuideEngine.Test.TestSupport;

namespace NINA.GuideEngine.Test.Guiding;

/// <summary>
/// A calibration made with Dec guiding off has no Dec rate, so it cannot guide Dec: as PHD2's Scope::IsCalibrated, it does
/// not count when Dec is guided, and guiding calibrates again instead of sending 0 ms Dec pulses all night.
/// </summary>
[TestFixture]
public class RaOnlyCalibrationTests
{
    private static readonly SettleParams Settle = new(1.5, 10, 120);

    private sealed class Run
    {
        private readonly object gate = new();
        private readonly List<GuiderEvent> events = [];

        public Run(DecGuideMode decGuideMode)
        {
            Clock = new VirtualClock();
            Sim = new Simulator(SimulatorScenario.GoodMount, Clock);
            Settings = new GuiderSettings
            {
                FocalLengthMm = SimulatorScenario.GoodMount.Camera.FocalLengthMm,
                ExposureMs = 2000,
                DecGuideMode = decGuideMode,
            };
            Guider = new Guider(Sim.Camera, Sim.Mount, Sim.Mount, Clock, Settings, ditherSeed: 5) { AutoStartLoop = false };
            Guider.EventRaised += (_, e) =>
            {
                if (e is not FrameReadyEvent)
                {
                    lock (gate)
                    {
                        events.Add(e);
                    }
                }

                OnEvent?.Invoke(e);
            };
        }

        public VirtualClock Clock { get; }

        public Simulator Sim { get; }

        public GuiderSettings Settings { get; }

        public Guider Guider { get; }

        public Action<GuiderEvent>? OnEvent { get; set; }

        public List<AlertEvent> Alerts()
        {
            lock (gate)
            {
                return [.. events.OfType<AlertEvent>()];
            }
        }

        public void ClearEvents()
        {
            lock (gate)
            {
                events.Clear();
            }
        }

        public async Task RunFor(TimeSpan duration)
        {
            Guider.StartLooping(Clock.CancelAt(Clock.Elapsed + duration));
            await Guider.WaitForLoopAsync().Within(duration);
        }

        /// <summary>Calibrates and guides with the guide mode given to the constructor.</summary>
        public async Task<CalibrationData> CalibrateAndGuide()
        {
            var guide = Guider.StartGuidingAsync(Settle);
            await RunFor(TimeSpan.FromMinutes(4));
            (await guide).Success.Should().BeTrue();
            return Guider.Calibration!;
        }
    }

    [Test]
    public async Task A_calibration_made_with_Dec_off_is_made_again_when_Dec_is_guided()
    {
        var run = new Run(DecGuideMode.Off);
        var raOnly = await run.CalibrateAndGuide();
        raOnly.HasDecCalibration.Should().BeFalse("Dec guiding was off");

        run.Guider.StopGuiding();
        run.Guider.UpdateSettings(run.Settings with { DecGuideMode = DecGuideMode.Auto });
        run.ClearEvents();
        var guide = run.Guider.StartGuidingAsync(Settle);
        await run.RunFor(TimeSpan.FromMinutes(6));

        (await guide).Success.Should().BeTrue();
        run.Alerts().Should().Contain(a => a.Code == GuideErrorCode.CalibrationInvalidated);
        run.Guider.Calibration.Should().NotBeSameAs(raOnly);
        run.Guider.Calibration!.HasDecCalibration.Should().BeTrue("the new calibration measured Dec");
    }

    [Test]
    public async Task With_Dec_off_the_calibration_is_used_again()
    {
        var run = new Run(DecGuideMode.Off);
        var raOnly = await run.CalibrateAndGuide();

        run.Guider.StopGuiding();
        run.ClearEvents();
        var guide = run.Guider.StartGuidingAsync(Settle);
        await run.RunFor(TimeSpan.FromMinutes(2));

        (await guide).Success.Should().BeTrue();
        run.Alerts().Should().NotContain(a => a.Code == GuideErrorCode.CalibrationInvalidated);
        run.Guider.Calibration.Should().BeSameAs(raOnly);
    }

    [Test]
    public async Task Switching_Dec_on_while_guiding_on_a_calibration_without_Dec_warns()
    {
        var run = new Run(DecGuideMode.Off);
        bool settled = false;
        bool switched = false;
        int stepsAfterSwitch = 0;
        run.OnEvent = e =>
        {
            if (e is SettleDoneEvent)
            {
                settled = true;
            }
            else if (switched && e is GuideStepEvent)
            {
                stepsAfterSwitch++;
            }
            else if (settled && !switched && e is GuideStepEvent { IsSettling: false })
            {
                switched = true;
                run.Guider.UpdateSettings(run.Settings with { DecGuideMode = DecGuideMode.Auto });
            }
        };

        var guide = run.Guider.StartGuidingAsync(Settle);
        await run.RunFor(TimeSpan.FromMinutes(5));

        (await guide).Success.Should().BeTrue();
        run.Guider.Calibration!.HasDecCalibration.Should().BeFalse();
        switched.Should().BeTrue();
        run.Alerts().Should().Contain(a => a.Code == GuideErrorCode.NotCalibrated);
        stepsAfterSwitch.Should().BeGreaterThan(10, "the warning does not stop guiding");
    }

    [Test]
    public async Task Switching_Dec_on_while_paused_on_a_calibration_without_Dec_warns()
    {
        var run = new Run(DecGuideMode.Off);
        bool settled = false;
        bool switched = false;
        run.OnEvent = e =>
        {
            if (e is SettleDoneEvent)
            {
                settled = true;
            }
            else if (settled && !switched && e is GuideStepEvent { IsSettling: false })
            {
                // as for a meridian flip or a dither: paused, then resumed on the same calibration
                switched = true;
                run.Guider.Pause();
                run.Guider.UpdateSettings(run.Settings with { DecGuideMode = DecGuideMode.Auto });
                run.Guider.Resume();
            }
        };

        var guide = run.Guider.StartGuidingAsync(Settle);
        await run.RunFor(TimeSpan.FromMinutes(5));

        (await guide).Success.Should().BeTrue();
        switched.Should().BeTrue();
        run.Guider.Calibration!.HasDecCalibration.Should().BeFalse();
        run.Alerts().Should().Contain(a => a.Code == GuideErrorCode.NotCalibrated);
    }

    [Test]
    public async Task Switching_Dec_on_during_a_calibration_without_Dec_warns_when_it_ends()
    {
        var run = new Run(DecGuideMode.Off);
        bool switched = false;
        run.OnEvent = e =>
        {
            if (!switched && e is CalibratingEvent)
            {
                switched = true;
                run.Guider.UpdateSettings(run.Settings with { DecGuideMode = DecGuideMode.Auto });
            }
        };

        var guide = run.Guider.StartGuidingAsync(Settle);
        await run.RunFor(TimeSpan.FromMinutes(5));

        (await guide).Success.Should().BeTrue();
        switched.Should().BeTrue();
        run.Guider.Calibration!.HasDecCalibration.Should().BeFalse("the calibration kept the mode it started with");
        run.Alerts().Should().Contain(a => a.Code == GuideErrorCode.NotCalibrated);
    }
}
