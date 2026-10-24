// SPDX-License-Identifier: MPL-2.0 AND BSD-3-Clause
// Copyright (c) 2006-2010 Craig Stark
// Ported from PHD2 src/scope.cpp (SanityCheckCalibration) (a6c02722)

namespace NINA.GuideEngine.Calibration;

/// <summary>Calibration sanity issue types (PHD2 CalibrationIssueType).</summary>
public enum CalibrationIssueType
{
    None,

    /// <summary>Too few RA or Dec steps (CI_Steps).</summary>
    Steps,

    /// <summary>RA/Dec axes not orthogonal (CI_Angle).</summary>
    Angle,

    /// <summary>Different from the previous calibration (CI_Different).</summary>
    Different,

    /// <summary>RA/Dec rate ratio inconsistent with cos(dec) (CI_Rates).</summary>
    Rates,
}

/// <summary>A calibration sanity issue. These are advisory warnings the user may override.</summary>
/// <param name="Type">Issue type.</param>
/// <param name="Code">Stable error code.</param>
/// <param name="Message">PHD2 alert text.</param>
/// <param name="Detail">PHD2 detail text with the measured values.</param>
public sealed record CalibrationIssue(CalibrationIssueType Type, string Code, string Message, string Detail);

/// <summary>Result of a sanity check.</summary>
/// <param name="Issues">All detected issues, in PHD2 priority order (Steps, Angle, Rates, Different).</param>
/// <param name="ReportedIssue">The single issue PHD2 would flag (first in priority order), or null.</param>
/// <param name="ShouldAlert">False when there is no issue or the reported issue type is suppressed by the user.</param>
public sealed record CalibrationSanityResult(IReadOnlyList<CalibrationIssue> Issues, CalibrationIssue? ReportedIssue, bool ShouldAlert)
{
    public bool Passed => ReportedIssue is null;

    public CalibrationIssueType ReportedType => ReportedIssue?.Type ?? CalibrationIssueType.None;
}

/// <summary>Port of Scope::SanityCheckCalibration.</summary>
public static class CalibrationSanityChecker
{
    public const int MinSteps = 4; // CAL_ALERT_MINSTEPS
    public const double OrthogonalityToleranceDeg = 12.5; // CAL_ALERT_ORTHOGONALITY_TOLERANCE
    public const double DecRateDifference = 0.20; // CAL_ALERT_DECRATE_DIFFERENCE
    public const double AxisRatesTolerance = 0.20; // CAL_ALERT_AXISRATES_TOLERANCE

    /// <summary>
    /// Checks a just-completed calibration.
    /// </summary>
    /// <param name="newCal">The new calibration (with step counts, guide rates and pixel scale).</param>
    /// <param name="previousCal">The previous calibration, null if none.</param>
    /// <param name="decCompensationEnabled">PHD2 DecCompensationEnabled (rate ratio check is skipped when off).</param>
    /// <param name="suppressed">Issue types the user chose not to be alerted about.</param>
    public static CalibrationSanityResult Check(CalibrationData newCal, CalibrationData? previousCal,
        bool decCompensationEnabled = true, IReadOnlySet<CalibrationIssueType>? suppressed = null)
    {
        var issues = new List<CalibrationIssue>();
        int xSteps = newCal.RaStepCount;
        int ySteps = newCal.DecStepCount;

        // Too few steps
        if (xSteps < MinSteps || (ySteps < MinSteps && ySteps > 0)) // Dec guiding might be disabled
        {
            issues.Add(new CalibrationIssue(CalibrationIssueType.Steps, "CAL_FEW_STEPS",
                "Advisory: Calibration completed but few guide steps were used, so accuracy is questionable",
                $"Actual RA calibration steps = {xSteps}, Dec calibration steps = {ySteps}"));
        }

        // Non-orthogonal RA/Dec axes: delta from the nearest multiple of 90 degrees
        double nonOrtho = MountTransform.OrthogonalityErrorDegrees(newCal.XAngle, newCal.YAngle);
        if (nonOrtho > OrthogonalityToleranceDeg)
        {
            issues.Add(new CalibrationIssue(CalibrationIssueType.Angle, "CAL_NON_ORTHOGONAL",
                "Advisory: Calibration completed but RA/Dec axis angles are questionable and guiding may be impaired",
                $"Non-orthogonality = {nonOrtho:F3}"));
        }

        // RA/Dec rates should be related by cos(dec) but don't check if Dec is too high or Dec guiding is disabled.
        if (newCal.Declination is { } dec && newCal.HasDecCalibration &&
            Math.Abs(dec) <= CalibrationAdjuster.DecCompLimit && decCompensationEnabled)
        {
            double expectedRatio = Math.Cos(dec);
            double speedRatio;

            // Deviation from PHD2: PHD2 only tests raGuideSpeed > 0 and would use a bogus negative ratio when only the
            // Dec rate is unknown (-1); we fall back to 1.0 in that case.
            if (newCal.GuideRateRa is > 0.0 && newCal.GuideRateDec is { } decSpeed) // mounts with different RA/Dec speeds
            {
                speedRatio = decSpeed / newCal.GuideRateRa.Value;
            }
            else
            {
                speedRatio = 1.0;
            }

            double actualRatio = newCal.XRate * speedRatio / newCal.YRate;
            if (Math.Abs(expectedRatio - actualRatio) > AxisRatesTolerance)
            {
                issues.Add(new CalibrationIssue(CalibrationIssueType.Rates, "CAL_RATE_RATIO",
                    "Advisory: Calibration completed but RA and Dec rates vary by an unexpected amount (often caused by large Dec backlash)",
                    $"Expected ratio at dec={MountTransform.Degrees(dec):F1} is {expectedRatio:F3}, actual is {actualRatio:F3}"));
            }
        }

        // Finally check for a significantly different result but ignore differences if the configuration looks
        // quite different.
        if (previousCal is { IsValid: true } oldCal && Math.Abs(oldCal.PixelScale - newCal.PixelScale) < 0.1 &&
            Math.Abs(MountTransform.Degrees(oldCal.XAngle - newCal.XAngle)) < 5.0)
        {
            if (newCal.HasDecCalibration && oldCal.HasDecCalibration)
            {
                double newDecRate = newCal.YRate;
                if (newDecRate != 0.0 && Math.Abs(1.0 - oldCal.YRate / newDecRate) > DecRateDifference)
                {
                    issues.Add(new CalibrationIssue(CalibrationIssueType.Different, "CAL_DIFFERENT",
                        "Advisory: This calibration is substantially different from the previous one - have you changed configurations?",
                        $"Current/previous Dec rate ratio is {oldCal.YRate / newDecRate:F3}"));
                }
            }
        }

        // PHD2 evaluates in this order and only flags the first issue: Steps (skips the rest), Angle, Rates, then
        // Different only when nothing else was found.
        CalibrationIssue? reported = issues.FirstOrDefault(i => i.Type == CalibrationIssueType.Steps)
            ?? issues.FirstOrDefault(i => i.Type == CalibrationIssueType.Angle)
            ?? issues.FirstOrDefault(i => i.Type == CalibrationIssueType.Rates)
            ?? issues.FirstOrDefault(i => i.Type == CalibrationIssueType.Different);

        var ordered = issues.OrderBy(i => Priority(i.Type)).ToList();
        bool alert = reported is not null && (suppressed is null || !suppressed.Contains(reported.Type));
        return new CalibrationSanityResult(ordered, reported, alert);
    }

    private static int Priority(CalibrationIssueType t) => t switch
    {
        CalibrationIssueType.Steps => 0,
        CalibrationIssueType.Angle => 1,
        CalibrationIssueType.Rates => 2,
        _ => 3,
    };
}
