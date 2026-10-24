// SPDX-License-Identifier: MPL-2.0

using FluentAssertions;
using NUnit.Framework;
using NINA.GuideEngine.Calibration;

namespace NINA.GuideEngine.Test.Calibration;

[TestFixture]
public class CalibrationSanityCheckerTests
{
    private static CalibrationData Good() => new()
    {
        XAngle = 0.3,
        YAngle = 0.3 + Math.PI / 2,
        XRate = 0.00375 * Math.Cos(MountTransform.Radians(30)),
        YRate = 0.00375,
        Declination = MountTransform.Radians(30),
        RaStepCount = 10,
        DecStepCount = 9,
        GuideRateRa = 0.5,
        GuideRateDec = 0.5,
        PixelScale = 2.0,
    };

    [Test]
    public void GoodCalibrationPasses()
    {
        CalibrationSanityResult r = CalibrationSanityChecker.Check(Good(), null);
        r.Passed.Should().BeTrue();
        r.Issues.Should().BeEmpty();
        r.ShouldAlert.Should().BeFalse();
        r.ReportedType.Should().Be(CalibrationIssueType.None);
    }

    [Test]
    public void TooFewSteps()
    {
        CalibrationSanityChecker.Check(Good() with { RaStepCount = 3 }, null).ReportedType.Should().Be(CalibrationIssueType.Steps);
        CalibrationSanityChecker.Check(Good() with { DecStepCount = 2 }, null).ReportedType.Should().Be(CalibrationIssueType.Steps);

        // Dec not calibrated (0 steps) is fine
        CalibrationSanityChecker.Check(Good() with { DecStepCount = 0, YRate = 0 }, null).Passed.Should().BeTrue();
    }

    [Test]
    public void NonOrthogonal()
    {
        CalibrationSanityResult r = CalibrationSanityChecker.Check(Good() with { YAngle = 0.3 + MountTransform.Radians(103) }, null);
        r.ReportedType.Should().Be(CalibrationIssueType.Angle);
        r.ReportedIssue!.Detail.Should().StartWith("Non-orthogonality = 13.0");

        CalibrationSanityChecker.Check(Good() with { YAngle = 0.3 + MountTransform.Radians(102) }, null).Passed.Should().BeTrue();

        // mirrored (-90) is orthogonal
        CalibrationSanityChecker.Check(Good() with { YAngle = 0.3 - Math.PI / 2 }, null).Passed.Should().BeTrue();
    }

    [Test]
    public void RateRatio()
    {
        // expected cos(30) = 0.866; actual 0.6 -> issue
        CalibrationSanityResult r = CalibrationSanityChecker.Check(Good() with { XRate = 0.00375 * 0.6 }, null);
        r.ReportedType.Should().Be(CalibrationIssueType.Rates);

        // ratio corrected by guide speeds: Dec speed half of RA speed -> Dec rate is half
        CalibrationSanityChecker.Check(Good() with { YRate = 0.00375 / 2, GuideRateDec = 0.25 }, null).Passed.Should().BeTrue();

        // skipped when dec comp disabled, dec unknown or |dec| > 60
        CalibrationSanityChecker.Check(Good() with { XRate = 0.001 }, null, decCompensationEnabled: false).Passed.Should().BeTrue();
        CalibrationSanityChecker.Check(Good() with { XRate = 0.001, Declination = null }, null).Passed.Should().BeTrue();
        CalibrationSanityChecker.Check(Good() with { XRate = 0.001, Declination = MountTransform.Radians(61) }, null).Passed.Should().BeTrue();
    }

    [Test]
    public void DifferentFromPrevious()
    {
        CalibrationData prev = Good() with { YRate = 0.005 };
        CalibrationSanityChecker.Check(Good(), prev).ReportedType.Should().Be(CalibrationIssueType.Different);

        // ignored when scale or angle differ
        CalibrationSanityChecker.Check(Good(), prev with { PixelScale = 3.0 }).Passed.Should().BeTrue();
        CalibrationSanityChecker.Check(Good(), prev with { XAngle = 0.5 }).Passed.Should().BeTrue();
        CalibrationSanityChecker.Check(Good(), prev with { YRate = 0.0042 }).Passed.Should().BeTrue();
    }

    [Test]
    public void FirstIssueReportedButAllExposed()
    {
        CalibrationData bad = Good() with { RaStepCount = 2, YAngle = 0.3 + MountTransform.Radians(120), XRate = 0.001 };
        CalibrationSanityResult r = CalibrationSanityChecker.Check(bad, Good() with { YRate = 0.01 });
        r.ReportedType.Should().Be(CalibrationIssueType.Steps);
        r.Issues.Select(i => i.Type).Should().Equal(CalibrationIssueType.Steps, CalibrationIssueType.Angle,
            CalibrationIssueType.Rates, CalibrationIssueType.Different);
        r.ShouldAlert.Should().BeTrue();
        r.ReportedIssue!.Message.Should().Contain("few guide steps");
    }

    [Test]
    public void RatesReportedBeforeDifferent()
    {
        CalibrationSanityResult r = CalibrationSanityChecker.Check(Good() with { XRate = 0.001 }, Good() with { YRate = 0.01 });
        r.ReportedType.Should().Be(CalibrationIssueType.Rates);
    }

    [Test]
    public void SuppressedIssueDoesNotAlert()
    {
        CalibrationSanityResult r = CalibrationSanityChecker.Check(Good() with { RaStepCount = 2 }, null,
            suppressed: new HashSet<CalibrationIssueType> { CalibrationIssueType.Steps });
        r.ReportedType.Should().Be(CalibrationIssueType.Steps);
        r.ShouldAlert.Should().BeFalse();
    }
}
