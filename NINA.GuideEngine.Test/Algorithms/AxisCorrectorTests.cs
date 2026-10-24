// SPDX-License-Identifier: MPL-2.0

using FluentAssertions;
using NUnit.Framework;
using NINA.GuideEngine.Algorithms;
using NINA.GuideEngine.Core;

namespace NINA.GuideEngine.Test.Algorithms;

[TestFixture]
public class AxisCorrectorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 23, 22, 0, 0, TimeSpan.Zero);
    private const double XRate = 0.005; // 1 px = 200 ms
    private const double YRate = 0.004; // 1 px = 250 ms

    private static AxisCorrector Identity() => new(new IdentityAlgorithm(), new IdentityAlgorithm());

    [Test]
    public void PositiveOffsetsAreCorrectedWestAndSouth()
    {
        var c = Identity().GuideStep(1.0, 2.0, XRate, YRate, T0);
        c.RADirection.Should().Be(GuideDirection.West);
        c.DECDirection.Should().Be(GuideDirection.South);
        c.RADuration.Should().Be(200);
        c.DECDuration.Should().Be(500);
        c.Pulses.Should().Equal(new PulseCommand(GuideDirection.West, 200), new PulseCommand(GuideDirection.South, 500));
        c.RaCorrectionPx.Should().BeApproximately(1.0, 1e-12);
        c.DecCorrectionPx.Should().BeApproximately(2.0, 1e-12);
    }

    [Test]
    public void NegativeOffsetsAreCorrectedEastAndNorth()
    {
        var c = Identity().GuideStep(-1.0, -2.0, XRate, YRate, T0);
        c.RADirection.Should().Be(GuideDirection.East);
        c.DECDirection.Should().Be(GuideDirection.North);
        c.RaCorrectionPx.Should().BeApproximately(-1.0, 1e-12);
        c.DecCorrectionPx.Should().BeApproximately(-2.0, 1e-12);
    }

    [Test]
    public void ZeroOffsetsReportEastNorthWithoutPulses()
    {
        var c = Identity().GuideStep(0, 0, XRate, YRate, T0);
        c.RADirection.Should().Be(GuideDirection.East);
        c.DECDirection.Should().Be(GuideDirection.North);
        c.Pulses.Should().BeEmpty();
    }

    [Test]
    public void DurationRoundsHalfUp()
    {
        AxisCorrector.DurationFor(0.25, 0.5).Should().Be(1); // 0.5 -> 1
        AxisCorrector.DurationFor(0.75, 0.5).Should().Be(2); // 1.5 -> 2
        AxisCorrector.DurationFor(-0.5, 0.005).Should().Be(100);
        AxisCorrector.DurationFor(1.0, 0.0).Should().Be(0);
        AxisCorrector.DurationFor(1.0, double.NaN).Should().Be(0);
        AxisCorrector.DurationFor(double.NaN, 0.5).Should().Be(0);
        AxisCorrector.DurationFor(double.PositiveInfinity, 0.5).Should().Be(0);
        AxisCorrector.DurationFor(double.NegativeInfinity, 0.5).Should().Be(0);
    }

    [Test]
    public void AlgorithmOutputDrivesDuration()
    {
        var corr = new AxisCorrector(new HysteresisAlgorithm(), new ResistSwitchAlgorithm());
        var c = corr.GuideStep(1.0, 0.1, XRate, YRate, T0);
        c.RADistanceRaw.Should().Be(1.0);
        c.RADistanceGuide.Should().BeApproximately(0.63, 1e-12);
        c.RADuration.Should().Be(126);
        c.DECDistanceGuide.Should().Be(0);
        c.DECDuration.Should().Be(0);
    }

    [Test]
    public void DecModeSuppressesDisallowedDirection()
    {
        var corr = Identity();
        corr.DecGuideMode = DecGuideMode.North;
        var c = corr.GuideStep(0, 1.0, XRate, YRate, T0);
        c.DECDirection.Should().Be(GuideDirection.South);
        c.DECDuration.Should().Be(0);
        c.DecSuppressedByMode.Should().BeTrue();
        corr.GuideStep(0, -1.0, XRate, YRate, T0).DECDuration.Should().Be(250);

        corr.DecGuideMode = DecGuideMode.Off;
        corr.GuideStep(0, -1.0, XRate, YRate, T0).DECDuration.Should().Be(0);
    }

    [Test]
    public void DriftModeGuidesTheDirectionItIsGivenAndReportsWhatIsSent()
    {
        var corr = Identity();
        corr.DecGuideMode = DecGuideMode.Drift;
        corr.MinPulseMs = 20;
        corr.GuideStep(0, -1.0, XRate, YRate, T0).DECDuration.Should().Be(250, "both directions until the guider picks one");

        corr.DriftDecDirection = GuideDirection.South;
        corr.EffectiveDecGuideMode.Should().Be(DecGuideMode.South);
        var north = corr.GuideStep(0, -1.0, XRate, YRate, T0);
        north.DECDuration.Should().Be(0);
        north.DecSuppressedByMode.Should().BeTrue();
        north.Pulses.Should().BeEmpty();
        north.DecCorrectionPx.Should().Be(0, "a suppressed request is no correction for the algorithm to learn");

        // the minimum pulse comes first (before the backlash compensation), then the direction filter: a short North
        // request rounded up to the minimum is still suppressed, and reported as not sent
        var shortNorth = corr.GuideStep(0, -0.06, XRate, YRate, T0);
        shortNorth.DECDuration.Should().Be(0);
        shortNorth.DecSuppressedByMode.Should().BeTrue();
        shortNorth.DecCorrectionPx.Should().Be(0);
        var south = corr.GuideStep(0, 0.06, XRate, YRate, T0);
        south.DECDuration.Should().Be(20, "15 ms South rounds up to the minimum");
        south.DecCorrectionPx.Should().BeApproximately(20 * YRate, 1e-12);
        corr.GuideStep(0, 0.02, XRate, YRate, T0).DecCorrectionPx.Should().Be(0, "5 ms rounds down to nothing");

        corr.DecGuideMode = DecGuideMode.Auto;
        corr.GuideStep(0, -1.0, XRate, YRate, T0).DECDuration.Should().Be(250, "the direction applies to the Drift mode only");
    }

    [Test]
    public void DecModeDoesNotAffectRecoveryMoves()
    {
        var corr = Identity();
        corr.DecGuideMode = DecGuideMode.Off;
        var c = corr.Move(0, 1.0, XRate, YRate, MoveOptions.RecoveryMove, T0)!;
        c.DECDuration.Should().Be(250);
    }

    [Test]
    public void ClampsAndFlagsLimitedAxes()
    {
        var corr = Identity();
        var c = corr.GuideStep(20.0, -20.0, XRate, YRate, T0);
        c.RADuration.Should().Be(2500);
        c.RALimited.Should().BeTrue();
        c.DECDuration.Should().Be(2500);
        c.DecLimited.Should().BeTrue();
    }

    [Test]
    public void RecoveryMovesAreNotClampedOrFiltered()
    {
        var corr = Identity();
        var c = corr.Move(20.0, 0, XRate, YRate, MoveOptions.RecoveryMove, T0)!;
        c.RADuration.Should().Be(4000);
        c.RALimited.Should().BeFalse();
    }

    [Test]
    public void ShortPulsesAreSentWithoutAMinimum()
    {
        Identity().GuideStep(0, -0.04, XRate, YRate, T0).DECDuration.Should().Be(10);
    }

    [TestCase(0.004, 0)] // 1 ms
    [TestCase(0.036, 0)] // 9 ms: below EQMod's default minimum, the driver would drop it
    [TestCase(0.04, 20)] // 10 ms: after a reversal it can leave a Sky-Watcher Dec motor running
    [TestCase(0.076, 20)] // 19 ms
    [TestCase(0.1, 25)]
    public void PulsesBelowTheMinimumAreRoundedToZeroOrTheMinimum(double decPx, int expectedMs)
    {
        var corr = Identity();
        corr.MinPulseMs = 20;
        var c = corr.GuideStep(0, -decPx, XRate, YRate, T0);
        c.DECDuration.Should().Be(expectedMs);
        c.Pulses.Should().HaveCount(expectedMs > 0 ? 1 : 0);
        c.DecCorrectionPx.Should().BeApproximately(-expectedMs * YRate, 1e-12, "the step reports what is sent");
    }

    [Test]
    public void MinimumAppliesToRaAndDeducedMovesButNotToOtherMoves()
    {
        var corr = new AxisCorrector(new FakeDeduce(0.03), new IdentityAlgorithm()) { MinPulseMs = 20 };
        corr.DeducedStep(XRate, YRate, T0)!.RADuration.Should().Be(0); // 6 ms
        corr.GuideStep(0.06, 0, XRate, YRate, T0).RADuration.Should().Be(20); // 12 ms
        corr.Move(0.03, 0, XRate, YRate, MoveOptions.RecoveryMove, T0)!.RADuration.Should().Be(6);
    }

    [Test]
    public void MinimumIsClampedToTheSmallestMaxDuration()
    {
        var corr = Identity();
        corr.MinPulseMs.Should().Be(0, "PHD2 sends any length");
        corr.MinPulseMs = 500;
        corr.MinPulseMs.Should().Be(PulseLimiter.MinMaxDurationMs);
        corr.MinPulseMs = -5;
        corr.MinPulseMs.Should().Be(0);
        new NINA.GuideEngine.Guiding.GuiderSettings().MinPulseMs.Should().Be(AxisCorrector.DefaultMinPulseMs);
    }

    [Test]
    public void RepeatedClampsRaiseAlertThroughPipeline()
    {
        var corr = Identity();
        for (int i = 0; i < 5; i++)
            corr.GuideStep(20.0, 0, XRate, YRate, T0.AddSeconds(i)).Alerts.Should().BeEmpty();
        corr.GuideStep(20.0, 0, XRate, YRate, T0.AddSeconds(5)).Alerts.Should().ContainSingle().Which.Axis.Should().Be(GuideAxis.Ra);
    }

    [Test]
    public void DitherStartsAlertGracePeriod()
    {
        var corr = Identity();
        corr.GuidingDithered(3, 2, T0);
        for (int i = 0; i < 10; i++)
            corr.GuideStep(20.0, 0, XRate, YRate, T0.AddSeconds(i)).Alerts.Should().BeEmpty();
    }

    [Test]
    public void BacklashCompIsAddedOnDecReversalAndExcludedFromCorrectionEstimate()
    {
        var corr = new AxisCorrector(new IdentityAlgorithm(), new IdentityAlgorithm(), backlash: new BacklashCompensation(300, 20, 600, enabled: true));
        corr.GuideStep(0, 1.0, XRate, YRate, T0).BacklashCompMs.Should().Be(0);
        var c = corr.GuideStep(0, -1.0, XRate, YRate, T0);
        c.BacklashCompMs.Should().Be(300);
        c.DECDuration.Should().Be(550);
        c.DecCorrectionPx.Should().BeApproximately(-1.0, 1e-12);
    }

    [Test]
    public void ARequestRoundedToNothingIsNoReversalForTheBacklashCompensation()
    {
        // South 100 ms, then North 2 ms: with the compensation at its 20 ms floor that is 22 ms, which a 50 ms minimum
        // drops. Counted as a reversal anyway, the next North pulse would go out without the compensation.
        var corr = new AxisCorrector(new IdentityAlgorithm(), new IdentityAlgorithm(), backlash: new BacklashCompensation(20, 20, 20, enabled: true))
        {
            MinPulseMs = 50,
        };
        corr.GuideStep(0, 0.4, XRate, YRate, T0).DECDuration.Should().Be(100);
        var dropped = corr.GuideStep(0, -0.008, XRate, YRate, T0);
        dropped.Pulses.Should().BeEmpty();
        dropped.BacklashCompMs.Should().Be(0);
        corr.Backlash.LastDirection.Should().Be(GuideDirection.South, "nothing went North");

        var north = corr.GuideStep(0, -0.24, XRate, YRate, T0);
        north.BacklashCompMs.Should().Be(20, "the first North pulse that goes out takes up the backlash");
        north.DECDuration.Should().Be(80);
        north.DecCorrectionPx.Should().BeApproximately(-60 * YRate, 1e-12);

        // PHD2 (no minimum): the short reversal carries the compensation
        var phd2 = new AxisCorrector(new IdentityAlgorithm(), new IdentityAlgorithm(), backlash: new BacklashCompensation(20, 20, 20, enabled: true));
        phd2.GuideStep(0, 0.4, XRate, YRate, T0);
        phd2.GuideStep(0, -0.008, XRate, YRate, T0).DECDuration.Should().Be(22);
    }

    [Test]
    public void NoBacklashCompensationWhileOneDecDirectionIsGuided()
    {
        var corr = new AxisCorrector(new IdentityAlgorithm(), new IdentityAlgorithm(), backlash: new BacklashCompensation(400, 20, 600, enabled: true))
        {
            DecGuideMode = DecGuideMode.Drift,
        };
        corr.GuideStep(0, -1.0, XRate, YRate, T0).DECDuration.Should().Be(250, "both directions until the guider picks one");
        corr.Backlash.LastDirection.Should().Be(GuideDirection.North);

        // South only: a suppressed North request is no reversal, and the South pulses carry no compensation (it would move
        // the mount while the step reports it as slack taken up)
        corr.DriftDecDirection = GuideDirection.South;
        corr.Backlash.LastDirection.Should().BeNull("the compensation starts over with another mode");
        corr.GuideStep(0, 1.0, XRate, YRate, T0).BacklashCompMs.Should().Be(0);
        corr.GuideStep(0, -1.0, XRate, YRate, T0).DecSuppressedByMode.Should().BeTrue();
        var south = corr.GuideStep(0, 1.0, XRate, YRate, T0);
        south.BacklashCompMs.Should().Be(0);
        south.DECDuration.Should().Be(250);
        south.DecCorrectionPx.Should().BeApproximately(1.0, 1e-12);

        // both directions again (the drift faded, the safety valve, settling): it starts over, then works as in Auto
        corr.DriftDecDirection = null;
        corr.GuideStep(0, -1.0, XRate, YRate, T0).BacklashCompMs.Should().Be(0, "the first pulse only sets the direction");
        corr.GuideStep(0, 1.0, XRate, YRate, T0).BacklashCompMs.Should().Be(400);

        // PHD2's North and South modes: none either
        corr.DecGuideMode = DecGuideMode.North;
        corr.GuideStep(0, -1.0, XRate, YRate, T0).BacklashCompMs.Should().Be(0);
    }

    [Test]
    public void GuidingDisabledProducesNoPulsesButAlgorithmsRun()
    {
        var hyst = new HysteresisAlgorithm();
        var corr = new AxisCorrector(hyst, new IdentityAlgorithm());
        corr.SetGuidingEnabled(false, T0);
        var c = corr.GuideStep(1.0, 1.0, XRate, YRate, T0);
        c.Pulses.Should().BeEmpty();
        c.RADistanceGuide.Should().BeApproximately(0.63, 1e-12);

        // manual moves are still allowed
        corr.Move(1.0, 0, XRate, YRate, MoveOptions.Manual, T0)!.RADuration.Should().Be(200);

        corr.SetGuidingEnabled(true, T0); // resets algorithms
        corr.GuideStep(1.0, 0, XRate, YRate, T0).RADistanceGuide.Should().BeApproximately(0.63, 1e-12);
    }

    [Test]
    public void DeducedStepWithNothingToDoReturnsNull()
    {
        Identity().DeducedStep(XRate, YRate, T0).Should().BeNull();
    }

    [Test]
    public void DeducedStepUsesDeduceResult()
    {
        var corr = new AxisCorrector(new FakeDeduce(0.5), new IdentityAlgorithm());
        var c = corr.DeducedStep(XRate, YRate, T0)!;
        c.Options.Should().Be(MoveOptions.DeducedMove);
        c.RADistanceRaw.Should().Be(0.5);
        c.RADuration.Should().Be(100);
    }

    [Test]
    public void StoppingResetsAlgorithmsAndBacklashDirection()
    {
        var corr = new AxisCorrector(new HysteresisAlgorithm(), new IdentityAlgorithm(), backlash: new BacklashCompensation(300, 20, 600, true));
        corr.GuideStep(1.0, 1.0, XRate, YRate, T0);
        corr.GuidingStopped();
        corr.Backlash.LastDirection.Should().BeNull();
        corr.GuideStep(1.0, 0, XRate, YRate, T0).RADistanceGuide.Should().BeApproximately(0.63, 1e-12);
    }

    [Test]
    public void CreateDefaultUsesPhd2Defaults()
    {
        var corr = AxisCorrector.CreateDefault(0.25);
        corr.RaAlgorithm.Should().BeOfType<HysteresisAlgorithm>().Which.MinMove.Should().Be(0.25);
        corr.DecAlgorithm.Should().BeOfType<ResistSwitchAlgorithm>().Which.MinMove.Should().Be(0.25);
        corr.DecGuideMode.Should().Be(DecGuideMode.Auto);
        corr.Limiter.MaxRaDurationMs.Should().Be(2500);
    }

    private sealed class FakeDeduce(double amount) : IGuideAlgorithm
    {
        public string Name => "fake";

        public double MinMove { get; set; } = -1;

        public IReadOnlyList<string> ParamNames => [];

        public double Result(double input) => input;

        public double DeduceResult() => amount;

        public void Reset()
        {
        }

        public bool TryGetParam(string name, out double value)
        {
            value = 0;
            return false;
        }

        public bool TrySetParam(string name, double value) => false;
    }
}
