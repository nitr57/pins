// SPDX-License-Identifier: MPL-2.0

using FluentAssertions;
using NUnit.Framework;
using NINA.GuideEngine.Algorithms;
using NINA.GuideEngine.Core;

namespace NINA.GuideEngine.Test.Algorithms;

[TestFixture]
public class GuideAlgorithmTests
{
    [Test]
    public void HysteresisBlendsWithLastMoveAndRemembersZeroedOutput()
    {
        var h = new HysteresisAlgorithm();
        h.Result(1.0).Should().BeApproximately(0.63, 1e-12); // (0.9*1 + 0.1*0)*0.7
        h.Result(1.0).Should().BeApproximately((0.9 + 0.1 * 0.63) * 0.7, 1e-12);
        h.Result(0.1).Should().Be(0.0); // below min-move, lastMove becomes 0
        h.Result(1.0).Should().BeApproximately(0.63, 1e-12);
    }

    [Test]
    public void HysteresisLifecycleFollowsPhd2Base()
    {
        var h = new HysteresisAlgorithm();
        h.Result(1.0);
        h.GuidingStarted(); // no-op in PHD2
        h.Result(1.0).Should().BeApproximately((0.9 + 0.063) * 0.7, 1e-12);
        h.GuidingResumed(); // resets
        h.Result(1.0).Should().BeApproximately(0.63, 1e-12);
        h.GuidingDithered(3);
        h.Result(1.0).Should().BeApproximately(0.63, 1e-12);
    }

    [Test]
    public void HysteresisParamValidationMatchesPhd2()
    {
        var h = new HysteresisAlgorithm();
        h.ParamNames.Should().Equal("minMove", "hysteresis", "aggression");
        h.TrySetParam("hysteresis", 1.5).Should().BeFalse();
        h.Hysteresis.Should().Be(0.99); // clipped
        h.TrySetParam("aggression", 2.5).Should().BeFalse();
        h.Aggression.Should().Be(0.7); // default
        h.TrySetParam("aggression", 2.0).Should().BeTrue();
        h.TrySetParam("minMove", -1).Should().BeFalse();
        h.MinMove.Should().Be(0.2);
        h.TrySetParam("bogus", 1).Should().BeFalse();
        h.TryGetParam("bogus", out _).Should().BeFalse();
    }

    [Test]
    public void Lowpass2UsesAttenuatedInputUntilFourPointsThenSlope()
    {
        var lp = new Lowpass2Algorithm();
        lp.Result(0.5).Should().BeApproximately(0.4, 1e-12);
        lp.Result(0.5).Should().BeApproximately(0.4, 1e-12);
        lp.Result(0.5).Should().BeApproximately(0.4, 1e-12);

        // 4 points, flat -> slope 0 -> 0 output (no reject since |0| <= |in|)
        lp.Result(0.5).Should().Be(0.0);
        lp.HistoryCount.Should().Be(4);
    }

    [Test]
    public void Lowpass2OutlierDumpsHistory()
    {
        var lp = new Lowpass2Algorithm();
        for (int i = 0; i < 5; i++)
            lp.Result(0.3);
        lp.Result(0.9).Should().BeApproximately(0.72, 1e-12); // |in| > 4*minMove -> in * 0.8, reset
        lp.HistoryCount.Should().Be(0);
    }

    [Test]
    public void Lowpass2RampReturnsSlopeTimesCount()
    {
        var lp = new Lowpass2Algorithm();
        lp.TrySetParam("aggressiveness", 100).Should().BeTrue();
        lp.Result(0.2);
        lp.Result(0.3);
        lp.Result(0.4);
        lp.Result(0.5).Should().BeApproximately(0.1 * 4, 1e-12); // slope 0.1 per frame, n = 4
    }

    [Test]
    public void Lowpass2NegativeAggressivenessLeavesValueUnchanged()
    {
        var lp = new Lowpass2Algorithm();
        lp.TrySetParam("aggressiveness", 60).Should().BeTrue();
        lp.TrySetParam("aggressiveness", -5).Should().BeFalse();
        lp.Aggressiveness.Should().Be(60);
    }

    [Test]
    public void ResistSwitchFastSwitchSeedsHistory()
    {
        var rs = new ResistSwitchAlgorithm();
        rs.Result(0.7).Should().Be(0.7); // > 3*minMove -> history [0 x7, 0.7 x3] -> switch to +1
        rs.CurrentSide.Should().Be(1);
        rs.History.Should().Equal(0, 0, 0, 0, 0, 0, 0, 0.7, 0.7, 0.7);
        rs.Result(-0.3).Should().Be(0.0); // opposite side, not compelling -> veto
        rs.Result(0.3).Should().Be(0.3);
    }

    [Test]
    public void ResistSwitchWithoutFastSwitchNeedsThreeSamples()
    {
        var rs = new ResistSwitchAlgorithm();
        rs.TrySetParam("fastSwitch", 0).Should().BeTrue();
        rs.Result(0.7).Should().Be(0.0);
        rs.Result(0.7).Should().Be(0.0);
        rs.Result(0.7).Should().Be(0.7); // decHistory = 3, newest (2.1) > oldest (0)
        rs.CurrentSide.Should().Be(1);
    }

    [Test]
    public void ResistSwitchResetOnDitherAndSettleDoneIsNoOp()
    {
        var rs = new ResistSwitchAlgorithm();
        rs.Result(0.7);
        rs.GuidingDitherSettleDone(true);
        rs.CurrentSide.Should().Be(1);
        rs.GuidingDithered(2.0);
        rs.CurrentSide.Should().Be(0);
        rs.History.Should().OnlyContain(v => v == 0.0).And.HaveCount(10);
    }

    [Test]
    public void ResistSwitchParamValidation()
    {
        var rs = new ResistSwitchAlgorithm();
        rs.ParamNames.Should().Equal("minMove", "fastSwitch", "aggression");
        rs.TrySetParam("minMove", 0).Should().BeFalse();
        rs.MinMove.Should().Be(0.2);
        rs.TrySetParam("aggression", 1.5).Should().BeFalse();
        rs.Aggression.Should().Be(1.0);
        rs.TrySetParam("aggression", 0.5).Should().BeTrue();
        rs.Result(0.8).Should().Be(0.4);
    }

    [Test]
    public void LowpassClampsToInputAndZeroFills()
    {
        var lp = new LowpassAlgorithm();

        // history of 9 zeros + 1.0: median 0, slope of [0 x9, 1] over 10 points = 0.0545..
        double slope = (10 * 9.0 - 45 * 1.0) / (10 * 285.0 - 45 * 45.0);
        lp.Result(1.0).Should().BeApproximately(5 * slope, 1e-12);
        lp.Result(0.1).Should().Be(0.0);
    }

    [Test]
    public void IdentityHasNoMinMove()
    {
        var id = new IdentityAlgorithm();
        id.MinMove.Should().Be(-1);
        id.HasMinMove.Should().BeFalse();
        id.Result(1.234).Should().Be(1.234);
        id.ParamNames.Should().BeEmpty();
    }

    [Test]
    public void FactoryCreatesByKind()
    {
        GuideAlgorithmFactory.Create(GuideAlgorithmKind.None).Should().BeOfType<IdentityAlgorithm>();
        GuideAlgorithmFactory.Create(GuideAlgorithmKind.Hysteresis).Should().BeOfType<HysteresisAlgorithm>();
        GuideAlgorithmFactory.Create(GuideAlgorithmKind.Lowpass).Should().BeOfType<LowpassAlgorithm>();
        GuideAlgorithmFactory.Create(GuideAlgorithmKind.Lowpass2).Should().BeOfType<Lowpass2Algorithm>();
        GuideAlgorithmFactory.Create(GuideAlgorithmKind.ResistSwitch).Should().BeOfType<ResistSwitchAlgorithm>();
        var act = () => GuideAlgorithmFactory.Create(GuideAlgorithmKind.ZFilter);
        act.Should().Throw<NotSupportedException>();
        GuideAlgorithmFactory.Create(GuideAlgorithmKind.Hysteresis, GuideAxis.Ra, 0.33).MinMove.Should().Be(0.33);
        GuideAlgorithmFactory.Create(GuideAlgorithmKind.Identity, GuideAxis.Ra, 0.33).MinMove.Should().Be(-1);
    }

    [Test]
    public void ResetParamsRestoresDefaultsAndAppliesSmartMinMove()
    {
        var h = new HysteresisAlgorithm();
        h.TrySetParam("aggression", 1.2);
        h.ResetParams(0.25);
        h.Aggression.Should().Be(0.7);
        h.MinMove.Should().Be(0.25);
    }

    [TestCase(1.0, 0.3063)]
    [TestCase(3.0, 0.2031)]
    [TestCase(10.0, 0.16698)]
    [TestCase(10000.0, 0.15151548)]
    public void SmartMinMoveFromScale(double scale, double expected)
    {
        MinMove.SmartDefault(scale).Should().BeApproximately(expected, 1e-9);
    }

    [Test]
    public void SmartMinMoveFloorIs015()
    {
        MinMove.SmartDefault(-1.0).Should().Be(0.15); // max(..., 0.15)
    }

    [Test]
    public void SmartMinMoveFromOptics()
    {
        double scale = 206.265 * 3.75 * 2 / 200.0;
        MinMove.SmartDefault(200, 3.75, 2).Should().BeApproximately(0.1515 + 0.1548 / scale, 1e-12);
        MinMove.SmartDefault(0, 3.75, 1).Should().Be(0.2);
    }
}
