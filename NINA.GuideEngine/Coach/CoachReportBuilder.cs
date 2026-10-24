// SPDX-License-Identifier: MPL-2.0

namespace NINA.GuideEngine.Coach;

/// <summary>Inputs of the report card.</summary>
public sealed record CoachReportInput
{
    public required string Id { get; init; }

    public DateTimeOffset Timestamp { get; init; }

    public string? ProfileName { get; init; }

    public string? CameraName { get; init; }

    public double? FocalLengthMm { get; init; }

    public double PixelScale { get; init; }

    public double? ImagingScale { get; init; }

    public double? DeclinationDeg { get; init; }

    public string? PierSide { get; init; }

    public IReadOnlyList<string> StepsDone { get; init; } = [];

    public IReadOnlyList<CoachFinding> Findings { get; init; } = [];

    public CoachCameraCheck? Camera { get; init; }

    public CoachDrift? Drift { get; init; }

    /// <summary>Semi-amplitude of the RA worm curve the Predictive algorithm learned (arcsec on the sky), null without one.</summary>
    public double? LearnedPeriodicErrorArcsec { get; init; }

    public CoachResponse? Response { get; init; }

    public IReadOnlyList<CoachTrial> Trials { get; init; } = [];

    /// <summary>Guiding statistics window before the session (fallback for the guided RMS): total, RA, Dec arcsec.</summary>
    public (double Total, double Ra, double Dec)? WindowRms { get; init; }
}

/// <summary>Builds the report card: guided RMS, quadrature error budget, grade and ranked actions (docs/COACH.md §3).</summary>
public static class CoachReportBuilder
{
    public const double ExcellentBelow = 0.5;
    public const double GoodBelow = 0.75;
    public const double FairBelow = 1.0;

    /// <summary>Grade of a guided RMS: relative to the imaging scale when known, else the RMS in arcsec with the same thresholds.</summary>
    public static (string Grade, double? Ratio) Grade(double? guidedArcsec, double? imagingScale)
    {
        if (guidedArcsec is not { } g || !double.IsFinite(g))
        {
            return (CoachGrades.Unknown, null);
        }

        double ratio = imagingScale is { } s && s > 0 ? g / s : g;
        string grade = ratio < ExcellentBelow ? CoachGrades.Excellent : ratio < GoodBelow ? CoachGrades.Good : ratio < FairBelow ? CoachGrades.Fair : CoachGrades.Poor;
        return (grade, Math.Round(ratio, 3));
    }

    /// <summary>Observing night (noon to noon, local time) of a timestamp.</summary>
    public static string Night(DateTimeOffset t) => t.ToLocalTime().AddHours(-12).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Builds the report and the report findings (returned separately and included in <see cref="CoachReport.Findings"/>).</summary>
    public static (CoachReport Report, IReadOnlyList<CoachFinding> ReportFindings) Build(CoachReportInput input)
    {
        var ts = input.Timestamp.UtcDateTime;

        // guided RMS: the winning trial (the current settings unless an alternative was significantly better), else the best
        // completed trial, else the guiding window before the session
        var completed = input.Trials.Where(t => t.State == CoachTrialStates.Done && t.RmsTotalArcsec is not null).ToList();
        var bestTrial = completed.FirstOrDefault(t => t.IsWinner) ?? completed.OrderBy(t => t.RmsTotalArcsec).FirstOrDefault();
        double? guided = null, guidedRa = null, guidedDec = null;
        string source = CoachGuidedSources.None;
        if (bestTrial is not null)
        {
            (guided, guidedRa, guidedDec, source) = (bestTrial.RmsTotalArcsec, bestTrial.RmsRaArcsec, bestTrial.RmsDecArcsec, CoachGuidedSources.Trials);
        }
        else if (input.WindowRms is { } w)
        {
            (guided, guidedRa, guidedDec, source) = (w.Total, w.Ra, w.Dec, CoachGuidedSources.Window);
        }

        double? seeing = input.Drift?.SeeingTotalArcsec;
        var rec = input.Camera?.Recommended;
        double? noise = rec is null ? null : CameraCheckAnalyzer.CentroidNoiseArcsec(rec.Hfd, rec.Snr, input.PixelScale);
        double? mount = guided is { } g && (seeing is not null || noise is not null)
            ? Math.Sqrt(Math.Max(0, g * g - Sq(seeing) - Sq(noise)))
            : null;

        var reportFindings = new List<CoachFinding>();
        if (guided is { } gg && gg > 0)
        {
            if (seeing is { } s && s * s >= 0.5 * gg * gg)
            {
                reportFindings.Add(CoachFindings.Create(CoachCodes.ReportSeeingLimited, CoachStepNames.Report, CoachSeverities.Good, ts,
                    new() { ["seeingArcsec"] = R(s), ["guidedArcsec"] = R(gg) }));
            }
            else if (mount is { } m && m * m >= 0.5 * gg * gg)
            {
                // about half of the mount part is typically recoverable by fixing the mount findings
                double impact = 0.5 * (gg - Math.Sqrt(Math.Max(0, gg * gg - m * m)));
                reportFindings.Add(CoachFindings.Create(CoachCodes.ReportMountLimited, CoachStepNames.Report, CoachSeverities.Warning, ts,
                    new() { ["mountArcsec"] = R(m), ["guidedArcsec"] = R(gg) }, impact));
            }
        }

        if (input.ImagingScale is not > 0)
        {
            reportFindings.Add(CoachFindings.Create(CoachCodes.ReportNoImagingScale, CoachStepNames.Report, CoachSeverities.Info, ts));
        }

        var findings = input.Findings.Select(f => f.ImpactArcsec is null ? WithEstimatedImpact(f, guidedRa, guidedDec) : f).Concat(reportFindings).ToList();
        var actions = findings
            .Where(f => f.Severity is CoachSeverities.Warning or CoachSeverities.Problem)
            .OrderByDescending(f => f.ImpactArcsec ?? -1)
            .ThenBy(f => f.Id, StringComparer.Ordinal)
            .Select(f => f.Id)
            .ToList();
        var (grade, ratio) = Grade(guided, input.ImagingScale);

        var report = new CoachReport
        {
            Id = input.Id,
            Timestamp = ts,
            Night = Night(input.Timestamp),
            ProfileName = input.ProfileName,
            CameraName = input.CameraName,
            FocalLengthMm = input.FocalLengthMm,
            PixelScale = input.PixelScale,
            ImagingScale = input.ImagingScale,
            DeclinationDeg = input.DeclinationDeg,
            PierSide = input.PierSide,
            Steps = input.StepsDone.ToList(),
            GuidedRmsArcsec = R(guided),
            GuidedRmsRaArcsec = R(guidedRa),
            GuidedRmsDecArcsec = R(guidedDec),
            GuidedSource = source,
            SeeingArcsec = R(seeing),
            CentroidNoiseArcsec = R(noise),
            MountArcsec = R(mount),
            BacklashArcsec = input.Response?.BacklashArcsec,
            PolarAlignmentErrorArcmin = input.Drift?.PolarAlignmentErrorArcmin,
            PeriodicErrorAmplitudeArcsec = input.LearnedPeriodicErrorArcsec ?? input.Drift?.PeriodicErrorAmplitudeArcsec,
            Grade = grade,
            GradeRatio = ratio,
            Actions = actions,
            Findings = findings,
            Camera = input.Camera,
            Drift = input.Drift,
            Response = input.Response,
            Trials = input.Trials.ToList(),
        };
        return (report, reportFindings);
    }

    /// <summary>Impact estimates that need the guided RMS (rate mismatch, asymmetry): the axis RMS share attributable to it.</summary>
    private static CoachFinding WithEstimatedImpact(CoachFinding f, double? guidedRa, double? guidedDec)
    {
        if (f.Code is not (CoachCodes.ResponseRateMismatch or CoachCodes.ResponseAsymmetry) || !f.Parameters.TryGetValue("ratio", out var r) || r is not double ratio)
        {
            return f;
        }

        double? axisRms = f.Parameters.TryGetValue("axis", out var a) && a is "Dec" ? guidedDec : guidedRa;
        if (axisRms is not { } rms)
        {
            return f;
        }

        double factor = f.Code == CoachCodes.ResponseRateMismatch ? Math.Abs(1 - ratio) : Math.Abs(1 - ratio) / 2;
        return f with { ImpactArcsec = Math.Round(Math.Min(rms, rms * factor), 3) };
    }

    private static double Sq(double? v) => v is { } d ? d * d : 0;

    private static double? R(double? v) => v is { } d && double.IsFinite(d) ? Math.Round(d, 3) : null;
}
