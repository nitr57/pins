// SPDX-License-Identifier: MPL-2.0 AND BSD-3-Clause
// Copyright (c) 2013 Bruce Waddington
// Copyright (c) 2006-2010 Craig Stark
// Ported from PHD2 src/calstep_dialog.cpp, src/scope.cpp (CheckCalibrationDuration) (a6c02722)

using NINA.GuideEngine.Core;

namespace NINA.GuideEngine.Calibration;

/// <summary>Recommended calibration step and distance.</summary>
/// <param name="StepMs">Calibration pulse duration, ms.</param>
/// <param name="DistancePx">Calibration distance, px.</param>
/// <param name="ImageScale">Image scale used, ″/px.</param>
/// <param name="GuideSpeed">Guide speed used, multiple of sidereal.</param>
/// <param name="GuideSpeedKnown">False when the mount did not report a guide rate.</param>
public sealed record CalibrationStepRecommendation(int StepMs, int DistancePx, double ImageScale, double GuideSpeed, bool GuideSpeedKnown);

/// <summary>Result of <see cref="CalibrationStepCalculator.CheckCalibrationDuration"/>.</summary>
/// <param name="StepMs">Step to use (unchanged when no refinement was needed).</param>
/// <param name="DistancePx">Distance to use.</param>
/// <param name="StepChanged">True when the step was recomputed; the host should persist it.</param>
/// <param name="DistanceChanged">True when the distance was recomputed because of a binning change.</param>
/// <param name="Reason">"binning" or "mount guide speed" when the step changed.</param>
public sealed record CalibrationStepCheck(int StepMs, int DistancePx, bool StepChanged, bool DistanceChanged, string? Reason);

/// <summary>Calibration step-size calculator (port of PHD2 CalstepDialog and Scope::CheckCalibrationDuration).</summary>
public static class CalibrationStepCalculator
{
    public const int DefaultSteps = 12;
    public const int DefaultDistancePx = 25;
    public const int DefaultCalibrationDurationMs = 750;
    public const double DefaultMountGuideSpeed = 0.5;
    public const double MinSteps = 6.0;
    public const double MaxSteps = 60.0;
    public const double MinGuideSpeed = 0.10;
    public const double MaxGuideSpeed = 2.0;
    public const double MinDeclinationDeg = -60.0;
    public const double MaxDeclinationDeg = 60.0;
    public const int MinDistancePx = 10;
    public const int MaxDistancePx = 200;

    /// <summary>PHD2 MyFrame::GetPixelScale.</summary>
    public static double PixelScale(double pixelSizeUm, double focalLengthMm, int binning) =>
        206.265 * pixelSizeUm * binning / focalLengthMm;

    /// <summary>CalstepDialog::GetCalibrationDistance: ceil(max(25 px, 20″ / scale)).</summary>
    public static int GetCalibrationDistance(double focalLengthMm, double pixelSizeUm, int binning)
    {
        double pixelScale = PixelScale(pixelSizeUm, focalLengthMm, binning);
        const double NominalDistanceArcsecs = 20.0;
        const double MinDistance = DefaultDistancePx;
        return (int)Math.Ceiling(Math.Max(MinDistance, NominalDistanceArcsecs / pixelScale));
    }

    public static int GetCalibrationDistance(GuideOptics optics) =>
        GetCalibrationDistance(optics.FocalLengthMm, optics.PixelSizeUm, optics.Binning);

    /// <summary>CalstepDialog::GetCalibrationStepSize.</summary>
    /// <returns>Pulse size in ms rounded up to the next multiple of 50 ms.</returns>
    public static int GetCalibrationStepSize(double focalLengthMm, double pixelSizeUm, int binning, double guideSpeed,
        int desiredSteps, double declinationDeg, int distancePx, out double imageScale)
    {
        imageScale = PixelScale(pixelSizeUm, focalLengthMm, binning); // arc-sec per pixel
        double totalDuration = distancePx * imageScale / (15.0 * guideSpeed); // 15 arc-sec/sec is sidereal rate
        double pulse = totalDuration / desiredSteps * 1000.0; // milliseconds at DEC=0
        double maxPulse = totalDuration / MinSteps * 1000.0; // max pulse size to still get MIN steps
        pulse = Math.Min(maxPulse, pulse / Math.Cos(MountTransform.Radians(declinationDeg))); // UI forces abs(Dec) <= 60 degrees
        return (int)Math.Ceiling(pulse / 50.0) * 50; // round up to nearest 50 ms
    }

    /// <summary>
    /// Guide speed as the CalstepDialog derives it from the mount: the larger of the RA/Dec rates, at least
    /// <see cref="MinGuideSpeed"/>; null when the mount reports none.
    /// </summary>
    public static double? GuideSpeedFromMount(MountSnapshot? mount)
    {
        double? ra = mount?.GuideRateRa is > 0 ? mount.GuideRateRa : null;
        double? dec = mount?.GuideRateDec is > 0 ? mount.GuideRateDec : null;
        if (ra is null && dec is null)
        {
            return null;
        }

        double speed = (ra ?? 0) >= (dec ?? 0) ? ra!.Value : dec!.Value;
        return Math.Max(speed, MinGuideSpeed);
    }

    /// <summary>
    /// Recommended step and distance for the optics (CalstepDialog defaults / OnReset). When the guide rate is
    /// unknown the PHD2 default step of 750 ms is returned. Declination is clamped to the dialog's ±60° range and
    /// its absolute value is used, as in CalstepDialog::DoRecalc.
    /// </summary>
    public static CalibrationStepRecommendation Recommend(GuideOptics optics, MountSnapshot? mount,
        int desiredSteps = DefaultSteps, double? declinationDeg = null)
    {
        int distance = optics.FocalLengthMm > 0 && optics.PixelSizeUm > 0
            ? GetCalibrationDistance(optics)
            : DefaultDistancePx;
        double? speed = GuideSpeedFromMount(mount);
        if (speed is null || optics.FocalLengthMm <= 0 || optics.PixelSizeUm <= 0)
        {
            return new CalibrationStepRecommendation(DefaultCalibrationDurationMs, distance, optics.PixelScale,
                speed ?? DefaultMountGuideSpeed, speed is not null);
        }

        double dec = Math.Abs(Math.Clamp(declinationDeg ?? mount?.DeclinationDeg ?? 0.0, MinDeclinationDeg, MaxDeclinationDeg));
        int step = GetCalibrationStepSize(optics.FocalLengthMm, optics.PixelSizeUm, optics.Binning, speed.Value,
            desiredSteps, dec, distance, out double scale);
        return new CalibrationStepRecommendation(step, distance, scale, speed.Value, true);
    }

    /// <summary>
    /// Port of Scope::CheckCalibrationDuration: at the start of a calibration, recompute the distance when the
    /// binning changed, and the step when binning changed or the mount RA guide rate differs by more than 5 % from
    /// the one recorded with the previous calibration. Nothing is refined on the very first calibration or when the
    /// mount does not report guide rates.
    /// </summary>
    /// <param name="currentDurationMs">Current calibration step.</param>
    /// <param name="currentDistancePx">Current calibration distance.</param>
    /// <param name="optics">Current optics (binning = current camera binning).</param>
    /// <param name="currentRaGuideRate">Mount-reported RA guide rate (× sidereal), null if unknown.</param>
    /// <param name="lastCalibration">Previous calibration (PHD2 CalibrationDetails), null if none.</param>
    public static CalibrationStepCheck CheckCalibrationDuration(int currentDurationMs, int currentDistancePx,
        GuideOptics optics, double? currentRaGuideRate, CalibrationData? lastCalibration)
    {
        int origBinning = lastCalibration?.Binning ?? 1; // PHD2 default for orig_binning
        bool binningChange = optics.Binning != origBinning;
        int distance = currentDistancePx;
        bool distanceChanged = false;

        if (binningChange && optics.FocalLengthMm > 0 && optics.PixelSizeUm > 0)
        {
            int newDistance = GetCalibrationDistance(optics);
            if (newDistance != currentDistancePx)
            {
                distance = newDistance;
                distanceChanged = true;
            }
        }

        double lastRaSpeed = lastCalibration?.GuideRateRa ?? -1.0;
        // Don't check the step size on very first calibration and don't adjust if the reported mount guide speeds are bogus
        if (currentRaGuideRate is not { } raSpeed || raSpeed <= 0 || lastRaSpeed <= 0)
        {
            return new CalibrationStepCheck(currentDurationMs, distance, false, distanceChanged, null);
        }

        bool refineStepSize = binningChange || Math.Abs(1.0 - raSpeed / lastRaSpeed) > 0.05;
        if (!refineStepSize || optics.FocalLengthMm <= 0 || optics.PixelSizeUm <= 0)
        {
            return new CalibrationStepCheck(currentDurationMs, distance, false, distanceChanged, null);
        }

        int step = GetCalibrationStepSize(optics.FocalLengthMm, optics.PixelSizeUm, optics.Binning, raSpeed,
            DefaultSteps, 0.0, distance, out _);
        return new CalibrationStepCheck(step, distance, step != currentDurationMs, distanceChanged,
            binningChange ? "binning" : "mount guide speed");
    }
}
