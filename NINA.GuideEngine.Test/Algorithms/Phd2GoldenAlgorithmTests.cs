// SPDX-License-Identifier: MPL-2.0

using FluentAssertions;
using NUnit.Framework;
using NINA.GuideEngine.Algorithms;
using NINA.GuideEngine.Core;
using NINA.GuideEngine.Test.Golden;

namespace NINA.GuideEngine.Test.Algorithms;

/// <summary>Replays sequences recorded from the compiled PHD2 algorithm sources; results must match bit for bit.</summary>
[TestFixture]
public class Phd2GoldenAlgorithmTests
{
    private static IEnumerable<string> AlgorithmCases() => GoldenData.CaseNames("algorithms");

    private static IEnumerable<string> BacklashCases() => GoldenData.CaseNames("backlash");

    [TestCaseSource(nameof(AlgorithmCases))]
    public void AlgorithmMatchesPhd2(string caseName)
    {
        var c = GoldenData.Case("algorithms", caseName);
        var kind = Enum.Parse<GuideAlgorithmKind>(c.S("kind"));
        var algo = GuideAlgorithmFactory.Create(kind, kind == GuideAlgorithmKind.ResistSwitch ? GuideAxis.Dec : GuideAxis.Ra);

        int step = 0;
        foreach (var op in c.GetProperty("ops").EnumerateArray())
        {
            switch (op.S("op"))
            {
                case "setParam":
                    algo.TrySetParam(op.S("name"), op.D("value")).Should().Be(op.B("ok"), "setParam {0}", op.S("name"));
                    if (op.TryGetProperty("readBack", out var rb))
                    {
                        algo.TryGetParam(op.S("name"), out double v).Should().BeTrue();
                        v.Should().Be(GoldenData.Num(rb), "read back of {0}", op.S("name"));
                    }

                    break;
                case "dithered":
                    algo.GuidingDithered(op.D("amount"));
                    break;
                case "resumed":
                    algo.GuidingResumed();
                    break;
                case "settleDone":
                    algo.GuidingDitherSettleDone(op.B("success"));
                    break;
                case "result":
                    double outp = algo.Result(op.D("in"));
                    outp.Should().Be(op.D("out"), "step {0} input {1}", step, op.D("in"));
                    step++;
                    break;
                default:
                    Assert.Fail($"unknown op {op.S("op")}");
                    break;
            }
        }

        foreach (var p in c.GetProperty("finalParams").EnumerateArray())
        {
            algo.TryGetParam(p.S("name"), out double v).Should().BeTrue();
            v.Should().Be(p.D("value"), p.S("name"));
        }

        algo.ParamNames.Should().Equal(c.GetProperty("finalParams").EnumerateArray().Select(p => p.S("name")));
    }

    [TestCaseSource(nameof(BacklashCases))]
    public void BacklashCompensationMatchesPhd2(string caseName)
    {
        var c = GoldenData.Case("backlash", caseName);
        double yRate = c.D("yRate");
        double minMove = c.D("minMove");
        var blc = new BacklashCompensation(c.I("pulse"), c.I("floor"), c.I("ceiling"), enabled: true);
        var t0 = new DateTimeOffset(2026, 9, 23, 22, 0, 0, TimeSpan.Zero);

        int n = 0;
        foreach (var op in c.GetProperty("ops").EnumerateArray())
        {
            var now = t0.AddSeconds(2 * n++);
            switch (op.S("op"))
            {
                case "init":
                    break;
                case "track":
                    blc.TrackResults((MoveOptions)op.I("opts"), op.D("y"), minMove, yRate, now);
                    break;
                case "apply":
                    int req = op.I("req");
                    blc.Apply((MoveOptions)op.I("opts"), op.D("y"), ref req, now);
                    req.Should().Be(op.I("outReq"), "apply at op {0}", n);
                    break;
                case "reset":
                    blc.ResetState();
                    break;
            }

            blc.PulseWidthMs.Should().Be(op.I("pulse"), "pulse after op {0} ({1})", n, op.S("op"));
            blc.FloorMs.Should().Be(op.I("floor"));
            blc.CeilingMs.Should().Be(op.I("ceiling"));
        }
    }
}
