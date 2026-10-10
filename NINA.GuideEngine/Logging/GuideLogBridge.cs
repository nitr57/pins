// SPDX-License-Identifier: MPL-2.0

using System.Globalization;
using NINA.GuideEngine.Algorithms;
using NINA.GuideEngine.Core;
using NINA.GuideEngine.Guiding;

namespace NINA.GuideEngine.Logging;

/// <summary>Host-provided context for the guide log header.</summary>
public sealed record GuideLogContext
{
    public string EquipmentProfile { get; init; } = "";

    public string CameraName { get; init; } = "";

    public string MountName { get; init; } = "";

    public int SensorWidth { get; init; }

    public int SensorHeight { get; init; }

    public double PixelSizeUm { get; init; }

    public int? DarkExposureMs { get; init; }

    public bool DefectMapInUse { get; init; }
}

/// <summary>
/// Writes a PHD2-compatible guide log from a <see cref="Guider"/>'s event stream. Subscribe once; dispose to
/// detach (the <see cref="GuidingLog"/> itself is owned by the host).
/// </summary>
public sealed class GuideLogBridge : IDisposable
{
    private readonly Guider guider;
    private readonly GuidingLog log;
    private readonly IMountState mount;
    private readonly Func<GuideLogContext> context;
    private bool calibrating;
    private bool guiding;

    public GuideLogBridge(Guider guider, GuidingLog log, IMountState mount, Func<GuideLogContext> context)
    {
        this.guider = guider;
        this.log = log;
        this.mount = mount;
        this.context = context;
        guider.EventRaised += OnEvent;
    }

    public void Dispose() => guider.EventRaised -= OnEvent;

    private void OnEvent(object? sender, GuiderEvent e)
    {
        switch (e)
        {
            case StartCalibrationEvent:
                calibrating = true;
                log.StartCalibration(BuildHeader());
                break;
            case CalibratingEvent c when calibrating && Enum.TryParse<GuideLogCalibrationDirection>(c.Direction, out var dir):
                log.CalibrationStep(new GuideLogCalibrationStep(dir, c.Step, c.Dx, c.Dy, c.Position.X, c.Position.Y, c.Distance));
                break;
            case CalibrationCompleteEvent cc:
                calibrating = false;
                log.CalibrationComplete(cc.Mount);
                break;
            case CalibrationFailedEvent cf:
                calibrating = false;
                log.CalibrationFailed(cf.Reason);
                break;
            case StartGuidingEvent:
                guiding = true;
                log.GuidingStarted(BuildHeader());
                break;
            case GuidingStoppedEvent when guiding:
                guiding = false;
                log.GuidingStopped();
                break;
            case GuideStepEvent s when guiding:
                log.GuideStep(new GuideLogStep
                {
                    FrameNumber = s.Frame,
                    TimeSec = s.Time,
                    CameraDx = s.Dx,
                    CameraDy = s.Dy,
                    RaRawDistance = s.RaDistanceRaw,
                    DecRawDistance = s.DecDistanceRaw,
                    RaGuideDistance = s.RaDistanceGuide,
                    DecGuideDistance = s.DecDistanceGuide,
                    RaDurationMs = s.RaDuration,
                    RaDirection = s.RaDirection,
                    DecDurationMs = s.DecDuration,
                    DecDirection = s.DecDirection,
                    StarMass = s.StarMass,
                    Snr = s.Snr,
                    ErrorCode = s.ErrorCode,
                });
                break;
            case StarLostEvent l when guiding || calibrating:
                log.NotifyStarLost(new GuideLogFrameDropped
                {
                    FrameNumber = l.Frame,
                    TimeSec = l.Time,
                    StarMass = l.StarMass,
                    Snr = l.Snr,
                    ErrorCode = l.ErrorCode,
                    Status = l.Status,
                });
                break;
            case GuidingDitheredEvent d when guiding:
                log.NotifyGuidingDithered(d.Dx, d.Dy, guider.LockPosition);
                break;
            case SettleBeginEvent when guiding:
                log.NotifySettlingStateChange(GuidingLog.SettlingStarted);
                break;
            case SettleDoneEvent sd when guiding:
                log.NotifySettlingStateChange(sd.Succeeded ? GuidingLog.SettlingComplete : GuidingLog.SettlingFailed);
                break;
            case SettingsChangedEvent when guiding:
                log.SettingsChanged(BuildHeader());
                break;
            case LockPositionSetEvent lp when guiding:
                log.NotifySetLockPosition(new GuidePoint(lp.X, lp.Y));
                break;
            case AlgorithmNoteEvent n when guiding && n.Kind is not (PredictiveNoteKind.Summary or PredictiveNoteKind.Restart or PredictiveNoteKind.Fit
                or PredictiveNoteKind.Noise):
                // the periodic summaries, the restarts at every dither, the fit diagnostics and the frames trusted less go to
                // the debug log only
                log.Info($"Predictive {n.Axis}: {n.Message}");
                break;
            case PulseModelUpdatedEvent pm when guiding:
                log.Info(PulseModelText(pm));
                break;
            case DecDirectionNoteEvent d when guiding && d.Kind is not (DecDirectionNoteKind.Summary or DecDirectionNoteKind.Reset):
                // the direction changes and the safety valve; summaries and resets go to the debug log only
                log.Info($"Dec guide direction: {d.Message}");
                break;
            case AlertEvent { Code: GuideErrorCode.SecondaryStarsRefreshed } a when guiding:
                log.Info($"Secondary stars found again: {a.Detail}");
                break;
            case AlertEvent a when (guiding || calibrating) && a.Severity != GuideErrorSeverity.Info:
                log.ServerCommand($"ALERT {(int)a.Code} {a.Code}: {a.Message}{(a.Detail is null ? "" : " - " + a.Detail)}");
                break;
        }
    }

    private GuideLogHeader BuildHeader()
    {
        var ctx = context();
        var s = guider.Settings;
        MountSnapshot? snap = null;
        try
        {
            snap = mount.GetSnapshot();
        }
        catch
        {
            // header only
        }

        double? speedRa = snap?.GuideRateRa * Sidereal.ArcsecPerSecond;
        double? speedDec = snap?.GuideRateDec * Sidereal.ArcsecPerSecond;
        return new GuideLogHeader
        {
            EquipmentProfile = ctx.EquipmentProfile,
            Global = new GuideLogGlobalSettings
            {
                DitherScale = s.DitherScale,
                PixelScale = guider.PixelScale,
                Binning = s.Binning,
                FocalLengthMm = s.FocalLengthMm > 0 ? (int)Math.Round(s.FocalLengthMm) : null,
                NoiseReduction = s.NoiseReduction switch
                {
                    Imaging.NoiseReduction.Mean2x2 => GuideLogNoiseReduction.Mean2x2,
                    Imaging.NoiseReduction.Median3x3 => GuideLogNoiseReduction.Median3x3,
                    _ => GuideLogNoiseReduction.None,
                },
            },
            Guider = new GuideLogGuiderSettings
            {
                SearchRegionPx = guider.SearchRegion,
                MassChangeThreshold = s.MultiStar.MassChangeThresholdEnabled ? s.MultiStar.MassChangeThreshold : null,
                MultiStar = s.MultiStar.MultiStarEnabled,
                StarListSize = s.StarFinder.MaxStars,
            },
            Camera = new GuideLogCameraSettings
            {
                Name = ctx.CameraName,
                Gain = s.Gain,
                FullWidth = ctx.SensorWidth,
                FullHeight = ctx.SensorHeight,
                DarkExposureMs = ctx.DarkExposureMs,
                DefectMapInUse = ctx.DefectMapInUse,
                PixelSizeUm = ctx.PixelSizeUm,
            },
            ExposureMs = (int)s.ExposureMs,
            Mount = new GuideLogMountSettings
            {
                Name = ctx.MountName,
                Calibration = guider.Calibration,
                XAlgorithm = Summary(guider.RaAlgorithm),
                YAlgorithm = Summary(guider.DecAlgorithm),
                Backlash = new GuideLogBacklash(s.Backlash.Enabled, s.Backlash.PulseMs),
                PulseModel = s.PulseModel,
                MaxRaDurationMs = s.MaxRaDurationMs,
                MaxDecDurationMs = s.MaxDecDurationMs,
                DecGuideMode = s.DecGuideMode,
                RaGuideSpeedArcsecPerSec = speedRa,
                DecGuideSpeedArcsecPerSec = speedDec,
            },
            CalibrationSettings = new GuideLogCalibrationSettings(s.Calibration.StepMs, s.Calibration.DistancePx, s.Calibration.AssumeOrthogonal),
            Pointing = new GuideLogPointing
            {
                RaHours = snap?.RightAscensionHours,
                DecDeg = snap?.DeclinationDeg,
                PierSide = snap?.PierSide ?? PierSide.Unknown,
                RotatorDeg = snap?.RotatorAngleDeg,
            },
            LockPosition = guider.LockPosition,
        };
    }

    /// <summary>The guide-log line of a pulse model update: the pulse factors in use and the estimates behind them.</summary>
    public static string PulseModelText(PulseModelUpdatedEvent e)
    {
        var v = e.Values;
        static string Estimate(PulseResponseEstimate? est) =>
            est is { } x ? string.Create(CultureInfo.InvariantCulture, $"effect {x.Effect:F2} ± {x.EffectSigma:F2}") : "learning";
        double windows = Math.Max(v.Ra?.Windows ?? 0, v.Dec?.Windows ?? 0);
        return string.Create(CultureInfo.InvariantCulture,
            $"Pulse model: RA pulses ×{e.RaPulseFactor:F2} ({Estimate(v.Ra)}), Dec pulses ×{e.DecPulseFactor:F2} ({Estimate(v.Dec)}), from {windows:F0} dithers");
    }

    private static GuideLogAlgorithm Summary(IGuideAlgorithm a)
    {
        double P(string n, double d) => a.TryGetParam(n, out var v) ? v : d;
        return a switch
        {
            HysteresisAlgorithm => GuideLogAlgorithm.Hysteresis(P("hysteresis", 0.1), P("aggression", 0.7), P("minMove", 0.2)),
            ResistSwitchAlgorithm => GuideLogAlgorithm.ResistSwitch(P("minMove", 0.2), P("aggression", 1.0), P("fastSwitch", 1) != 0),
            Lowpass2Algorithm => GuideLogAlgorithm.Lowpass2(P("aggressiveness", 80), P("minMove", 0.2)),
            LowpassAlgorithm => GuideLogAlgorithm.Lowpass(P("slopeWeight", 5), P("minMove", 0.2)),
            PredictiveAlgorithm predictive => new GuideLogAlgorithm(predictive.Name, predictive.SettingsSummary),
            _ => GuideLogAlgorithm.None,
        };
    }
}
