// SPDX-License-Identifier: MPL-2.0

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics.CodeAnalysis;
using NINA.Equipment.Equipment.MyGuider.Advanced;
using NINA.Equipment.Interfaces;
using NINA.GuideEngine.Coach;

namespace NINA.Equipment.Equipment.MyGuider.Internal;

/// <summary>Maps the engine's Guiding Coach records to the <see cref="IGuidingCoach"/> DTOs (same property names).</summary>
internal static class CoachMapping
{
    public static CoachOptions FromDto(AdvancedCoachOptions? o) => o is null
        ? new CoachOptions()
        : new CoachOptions
        {
            Steps = o.Steps?.ToList() ?? [],
            ExposureSeconds = o.ExposureSeconds?.ToList() ?? [],
            Gains = o.Gains?.ToList() ?? [],
            FramesPerCombination = o.FramesPerCombination,
            DriftSeconds = o.DriftSeconds,
            TrialSeconds = o.TrialSeconds,
            RepeatBaseline = o.RepeatBaseline,
            AllowCalibration = o.AllowCalibration,
        };

    public static AdvancedCoachStartResult ToDto(CoachStartResult r) => new()
    {
        Accepted = r.Accepted,
        Message = r.Message,
        MessageCode = r.MessageCode,
        MessageParameters = Params(r.MessageParameters),
        Status = ToDto(r.Status),
    };

    public static AdvancedCoachStatus ToDto(CoachStatus s) => new()
    {
        Phase = s.Phase,
        Message = s.Message,
        MessageCode = s.MessageCode,
        MessageParameters = Params(s.MessageParameters),
        SessionId = s.SessionId,
        StartedAt = s.StartedAt,
        Step = s.Step,
        Steps = s.Steps.Select(ToDto).ToList(),
        Progress = s.Progress,
        ElapsedSeconds = s.ElapsedSeconds,
        EstimatedTotalSeconds = s.EstimatedTotalSeconds,
        GainMin = s.GainMin,
        GainMax = s.GainMax,
        CurrentGain = s.CurrentGain,
        CurrentExposureSeconds = s.CurrentExposureSeconds,
        Camera = ToDto(s.Camera),
        Drift = ToDto(s.Drift),
        Response = ToDto(s.Response),
        Trials = s.Trials.Select(ToDto).ToList(),
        Findings = s.Findings.Select(ToDto).ToList(),
        Report = ToDto(s.Report),
    };

    public static AdvancedCoachFinding ToDto(CoachFinding f) => new()
    {
        Id = f.Id,
        Code = f.Code,
        Step = f.Step,
        Severity = f.Severity,
        Parameters = Params(f.Parameters),
        ImpactArcsec = f.ImpactArcsec,
        Changes = f.Changes.Select(ToDto).ToList(),
        Applied = f.Applied,
        Message = f.Message,
        Timestamp = f.Timestamp,
        ExpiresAt = f.ExpiresAt,
    };

    [return: NotNullIfNotNull(nameof(r))]
    public static AdvancedCoachReport? ToDto(CoachReport? r) => r is null ? null : new()
    {
        Id = r.Id,
        Timestamp = r.Timestamp,
        Night = r.Night,
        ProfileName = r.ProfileName,
        CameraName = r.CameraName,
        FocalLengthMm = r.FocalLengthMm,
        PixelScale = r.PixelScale,
        ImagingScale = r.ImagingScale,
        DeclinationDeg = r.DeclinationDeg,
        PierSide = r.PierSide,
        Steps = r.Steps.ToList(),
        GuidedRmsArcsec = r.GuidedRmsArcsec,
        GuidedRmsRaArcsec = r.GuidedRmsRaArcsec,
        GuidedRmsDecArcsec = r.GuidedRmsDecArcsec,
        GuidedSource = r.GuidedSource,
        SeeingArcsec = r.SeeingArcsec,
        CentroidNoiseArcsec = r.CentroidNoiseArcsec,
        MountArcsec = r.MountArcsec,
        BacklashArcsec = r.BacklashArcsec,
        PolarAlignmentErrorArcmin = r.PolarAlignmentErrorArcmin,
        PeriodicErrorAmplitudeArcsec = r.PeriodicErrorAmplitudeArcsec,
        Grade = r.Grade,
        GradeRatio = r.GradeRatio,
        Actions = r.Actions.ToList(),
        Findings = r.Findings.Select(ToDto).ToList(),
        Camera = ToDto(r.Camera),
        Drift = ToDto(r.Drift),
        Response = ToDto(r.Response),
        Trials = r.Trials.Select(ToDto).ToList(),
    };

    private static AdvancedCoachStepStatus ToDto(CoachStepStatus s) => new()
    {
        Name = s.Name,
        State = s.State,
        Detail = s.Detail,
        DetailCode = s.DetailCode,
        DetailParameters = Params(s.DetailParameters),
        Progress = s.Progress,
        ElapsedSeconds = s.ElapsedSeconds,
        EstimatedSeconds = s.EstimatedSeconds,
        Message = s.Message,
        MessageCode = s.MessageCode,
        MessageParameters = Params(s.MessageParameters),
    };

    private static AdvancedCoachCameraCheck? ToDto(CoachCameraCheck? c) => c is null ? null : new()
    {
        Results = c.Results.Select(ToDto).ToList(),
        Recommended = c.Recommended is null ? null : ToDto(c.Recommended),
    };

    private static AdvancedCoachCameraResult ToDto(CoachCameraResult r) => new()
    {
        ExposureSeconds = r.ExposureSeconds,
        Gain = r.Gain,
        Frames = r.Frames,
        Snr = r.Snr,
        Hfd = r.Hfd,
        Stars = r.Stars,
        Saturated = r.Saturated,
        JitterPx = r.JitterPx,
        JitterArcsec = r.JitterArcsec,
        Feasible = r.Feasible,
        Reason = r.Reason,
    };

    private static AdvancedCoachDrift? ToDto(CoachDrift? d) => d is null ? null : new()
    {
        ElapsedSeconds = d.ElapsedSeconds,
        TargetSeconds = d.TargetSeconds,
        Samples = d.Samples.Select(s => new AdvancedCoachSample { T = s.T, Ra = s.Ra, Dec = s.Dec }).ToList(),
        SnrAvg = d.SnrAvg,
        SeeingRaArcsec = d.SeeingRaArcsec,
        SeeingDecArcsec = d.SeeingDecArcsec,
        SeeingTotalArcsec = d.SeeingTotalArcsec,
        RaPeakToPeakArcsec = d.RaPeakToPeakArcsec,
        RaMaxRateArcsecPerSec = d.RaMaxRateArcsecPerSec,
        RaDriftArcsecPerMin = d.RaDriftArcsecPerMin,
        DecDriftArcsecPerMin = d.DecDriftArcsecPerMin,
        PeriodicErrorPeriodSeconds = d.PeriodicErrorPeriodSeconds,
        PeriodicErrorAmplitudeArcsec = d.PeriodicErrorAmplitudeArcsec,
        PeriodicErrorPhaseRad = d.PeriodicErrorPhaseRad,
        PeriodicErrorOffsetArcsec = d.PeriodicErrorOffsetArcsec,
        PolarAlignmentErrorArcmin = d.PolarAlignmentErrorArcmin,
        DeclinationAssumed = d.DeclinationAssumed,
        DriftLimitingExposureSeconds = d.DriftLimitingExposureSeconds,
        GustPercent = d.GustPercent,
    };

    private static AdvancedCoachResponse? ToDto(CoachResponse? r) => r is null ? null : new()
    {
        BacklashMs = r.BacklashMs,
        BacklashArcsec = r.BacklashArcsec,
        BacklashState = r.BacklashState,
        BacklashPoints = r.BacklashPoints.Select(p => new AdvancedCoachPoint { X = p.X, Y = p.Y }).ToList(),
        LargeMoveBacklashMs = r.LargeMoveBacklashMs,
        LargeMoveBacklashArcsec = r.LargeMoveBacklashArcsec,
        ReversalPulseMs = r.ReversalPulseMs,
        ReversalMoves = r.ReversalMoves.Select(ToDto).ToList(),
        Pulses = r.Pulses.Select(ToDto).ToList(),
        MinEffectivePulseRaMs = r.MinEffectivePulseRaMs,
        MinEffectivePulseDecMs = r.MinEffectivePulseDecMs,
        AsymmetryRa = r.AsymmetryRa,
        AsymmetryDec = r.AsymmetryDec,
        RateRatioRa = r.RateRatioRa,
        RateRatioDec = r.RateRatioDec,
    };

    private static AdvancedCoachPulse ToDto(CoachPulse p) => new()
    {
        Direction = p.Direction,
        DurationMs = p.DurationMs,
        ExpectedArcsec = p.ExpectedArcsec,
        MovedArcsec = p.MovedArcsec,
        Ratio = p.Ratio,
    };

    private static AdvancedCoachTrial ToDto(CoachTrial t) => new()
    {
        Id = t.Id,
        Kind = t.Kind,
        Settings = t.Settings.Select(ToDto).ToList(),
        State = t.State,
        ElapsedSeconds = t.ElapsedSeconds,
        Frames = t.Frames,
        RmsRaArcsec = t.RmsRaArcsec,
        RmsDecArcsec = t.RmsDecArcsec,
        RmsTotalArcsec = t.RmsTotalArcsec,
        PeakArcsec = t.PeakArcsec,
        OscillationIndex = t.OscillationIndex,
        SnrAvg = t.SnrAvg,
        IsWinner = t.IsWinner,
        Applied = t.Applied,
    };

    private static AdvancedCoachSettingChange ToDto(CoachSettingChange c) => new()
    {
        Name = c.Name,
        Value = c.Value,
        CurrentValue = c.CurrentValue,
    };

    /// <summary>Copies parameters (double/int/string/bool or null values).</summary>
    private static Dictionary<string, object?> Params(Dictionary<string, object?>? p) => p is null ? [] : new(p);
}
