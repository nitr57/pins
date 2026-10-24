// SPDX-License-Identifier: MPL-2.0 AND BSD-3-Clause
// Copyright (c) 2012 Bret McKee
// Copyright (c) 2006-2010 Craig Stark
// Ported from PHD2 src/mount.cpp (AdjustCalibrationForScopePointing, FlipCalibration) (a6c02722)

using NINA.GuideEngine.Core;

namespace NINA.GuideEngine.Calibration;

/// <summary>Alerts raised when adapting a stored calibration to the current pointing.</summary>
public enum CalibrationAlertType
{
    /// <summary>Mount guide speeds differ by more than 5 % from calibration time.</summary>
    GuideSpeedChanged,

    /// <summary>The calibration has no pier side information, so meridian flips cannot be handled.</summary>
    NoPierSideInformation,

    /// <summary>Pixel size changed (≥ 1 µm): calibration invalidated.</summary>
    PixelSizeChanged,

    /// <summary>Binning differs from calibration: rates were rescaled.</summary>
    BinningChanged,

    /// <summary>The calibration was flipped for the opposite pier side.</summary>
    Flipped,

    /// <summary>Rotator position unknown at calibration time; recalibration needed.</summary>
    RotatorPositionUnknown,

    /// <summary>Calibration angles were rotated by the rotator delta.</summary>
    RotatorAdjusted,

    /// <summary>Calibration declination beyond ±60°: Dec compensation skipped, recalibration needed.</summary>
    CalibrationTooFarFromEquator,

    /// <summary>The Dec self-check detected inverted Dec corrections after a flip.</summary>
    DecInverted,
}

/// <summary>An alert from <see cref="CalibrationAdjuster"/>.</summary>
/// <param name="Type">Alert type.</param>
/// <param name="Code">Stable code.</param>
/// <param name="Message">User-facing message (PHD2 text where PHD2 has one).</param>
/// <param name="ShowToUser">False for purely informational entries PHD2 only writes to its debug log.</param>
public sealed record CalibrationAlert(CalibrationAlertType Type, string Code, string Message, bool ShowToUser = true);

/// <summary>Current pointing and equipment state for <see cref="CalibrationAdjuster.AdjustForScopePointing"/>.</summary>
public sealed record ScopePointing
{
    /// <summary>Mount state (declination, pier side, guide rates, rotator).</summary>
    public required MountSnapshot Mount { get; init; }

    /// <summary>Current camera binning.</summary>
    public int Binning { get; init; } = 1;

    /// <summary>Current (unbinned) camera pixel size, µm; 0 = unknown (no check).</summary>
    public double PixelSizeUm { get; init; }

    /// <summary>
    /// Rotator angle in degrees, null when no rotator. Defaults to <see cref="MountSnapshot.RotatorAngleDeg"/>.
    /// </summary>
    public double? RotatorAngleDeg { get; init; }

    /// <summary>PHD2 CalibrationFlipRequiresDecFlip (per-mount setting).</summary>
    public bool DecFlipRequired { get; init; }

    /// <summary>PHD2 UseDecComp (default on).</summary>
    public bool DecCompensationEnabled { get; init; } = true;

    /// <summary>PHD2 CanReportPosition: whether mount guide rates may be compared.</summary>
    public bool CanReportPosition { get; init; } = true;
}

/// <summary>Result of <see cref="CalibrationAdjuster.AdjustForScopePointing"/>.</summary>
/// <param name="Calibration">Adjusted calibration to persist and use (xRate NOT dec-compensated, as PHD2 m_cal).</param>
/// <param name="EffectiveXRate">RA rate to use for guiding (dec-compensated when applicable, PHD2 m_xRate).</param>
/// <param name="IsValid">False when the calibration must not be used (pixel size / image scale change).</param>
/// <param name="Flipped">True when the calibration was flipped for the other pier side (start the Dec self-check).</param>
/// <param name="DecCompensated">True when EffectiveXRate was dec-compensated.</param>
/// <param name="Alerts">Alerts in PHD2 order.</param>
public sealed record CalibrationAdjustment(CalibrationData Calibration, double EffectiveXRate, bool IsValid, bool Flipped,
    bool DecCompensated, IReadOnlyList<CalibrationAlert> Alerts)
{
    /// <summary>Calibration with <see cref="CalibrationData.XRate"/> replaced by <see cref="EffectiveXRate"/>.</summary>
    public CalibrationData Effective => Calibration with { XRate = EffectiveXRate };

    public bool CalibrationChanged { get; init; }
}

/// <summary>Port of Mount::AdjustCalibrationForScopePointing and Mount::FlipCalibration.</summary>
public static class CalibrationAdjuster
{
    /// <summary>Enable dec compensation when calibration declination is less than this (60°, PHD2 Scope::DEC_COMP_LIMIT).</summary>
    public const double DecCompLimit = Math.PI / 2.0 * 2.0 / 3.0;

    /// <summary>PHD2 FlipCalibration: xAngle += π; yAngle += π only when <paramref name="decFlipRequired"/>.</summary>
    public static CalibrationData FlipCalibration(CalibrationData cal, bool decFlipRequired)
    {
        double newX = cal.XAngle + Math.PI;
        double newY = cal.YAngle;

        if (decFlipRequired)
        {
            newY += Math.PI;
        }

        // Dec polarity changes when pier side changes, i.e. if Guide(NORTH) moves the star north on one side,
        // then Guide(NORTH) will move the star south on the other side of the pier.
        // For mounts with CalibrationFlipRequiresDecFlip, the parity does not change after the flip.
        GuideParity newDecParity = decFlipRequired ? cal.DecGuideParity : OppositeParity(cal.DecGuideParity);

        return cal with
        {
            XAngle = MountTransform.NormAngle(newX),
            YAngle = MountTransform.NormAngle(newY),
            PierSide = cal.PierSide.Opposite(),
            DecGuideParity = newDecParity,
        };
    }

    /// <summary>
    /// Inverts the Dec axis (yAngle += π, Dec parity inverted). Used when the post-flip Dec self-check
    /// (<see cref="DecFlipVerifier"/>) finds that Dec corrections have the wrong sign; the host should also toggle
    /// the per-mount DecFlipRequired setting.
    /// </summary>
    public static CalibrationData InvertDec(CalibrationData cal) => cal with
    {
        YAngle = MountTransform.NormAngle(cal.YAngle + Math.PI),
        DecGuideParity = OppositeParity(cal.DecGuideParity),
    };

    public static GuideParity OppositeParity(GuideParity p) => p switch
    {
        GuideParity.Even => GuideParity.Odd,
        GuideParity.Odd => GuideParity.Even,
        _ => p,
    };

    public static bool IsOppositeSide(PierSide a, PierSide b) =>
        (a == PierSide.East && b == PierSide.West) || (a == PierSide.West && b == PierSide.East);

    /// <summary>
    /// Adapts a stored calibration to the current pointing when guiding starts (port of
    /// Mount::AdjustCalibrationForScopePointing): guide rate change alert, missing pier side alert, pixel size and
    /// binning changes, pier flip, rotator delta and declination compensation, in PHD2 order.
    /// </summary>
    public static CalibrationAdjustment AdjustForScopePointing(CalibrationData stored, ScopePointing pointing)
    {
        var alerts = new List<CalibrationAlert>();
        CalibrationData cal = stored;
        bool valid = stored.IsValid;
        bool flipped = false;
        MountSnapshot mount = pointing.Mount;
        double? newDeclination = mount.DeclinationDeg is { } d ? MountTransform.Radians(d) : null;
        PierSide newPierSide = mount.PierSide;
        double? newRotatorAngle = pointing.RotatorAngleDeg ?? mount.RotatorAngleDeg;
        int binning = pointing.Binning;

        // See if the user has changed mount guide speeds after the last calibration. If so, raise an alert that can't be avoided
        if (pointing.CanReportPosition && cal.GuideRateRa is > 0 && cal.GuideRateDec is > 0 &&
            mount.GuideRateRa is { } currRa && mount.GuideRateDec is { } currDec)
        {
            if (Math.Abs(1.0 - currRa / cal.GuideRateRa.Value) > 0.05 || Math.Abs(1.0 - currDec / cal.GuideRateDec.Value) > 0.05)
            {
                alerts.Add(new CalibrationAlert(CalibrationAlertType.GuideSpeedChanged, "CAL_GUIDE_SPEED_CHANGED",
                    "Mount guide speeds are different from those used in last calibration.  Do a new calibration or reset " +
                    "mount guide speed settings to previous values. "));
            }
        }

        if (newPierSide != PierSide.Unknown && cal.PierSide == PierSide.Unknown)
        {
            alerts.Add(new CalibrationAlert(CalibrationAlertType.NoPierSideInformation, "CAL_NO_PIER_SIDE",
                "Current calibration did not have side-of-pier information, so the guider can't automatically correct for " +
                "meridian flips. You should do a fresh calibration to correct this problem."));
        }

        // Compensate for binning or pixel size changes.
        double scaleAdjustment = 1.0;

        // Deviation from PHD2: PHD2 compares the camera-reported pixel size with the profile pixel size; we compare
        // the current pixel size with the one stored in the calibration (same 1 µm threshold).
        if (pointing.PixelSizeUm > 0 && cal.PixelSizeUm > 0 && Math.Abs(pointing.PixelSizeUm - cal.PixelSizeUm) >= 1.0)
        {
            alerts.Add(new CalibrationAlert(CalibrationAlertType.PixelSizeChanged, "CAL_PIXEL_SIZE_CHANGED",
                "Profile pixel size doesn't match camera-reported pixel size.  Re-calibrate to restore correct guiding."));
            scaleAdjustment = pointing.PixelSizeUm / cal.PixelSizeUm;
        }

        if (binning != cal.Binning && cal.IsValid && binning > 0 && cal.Binning > 0)
        {
            // Deviation from PHD2: PHD2 computes the scale adjustment with integer division (binning / m_cal.binning)
            // and then treats any binning change as an image scale change that clears the calibration. As agreed in
            // ARCHITECTURE.md ("Calibration") we keep the rescaled calibration and only raise an advisory.
            double adj = (double)cal.Binning / binning;
            alerts.Add(new CalibrationAlert(CalibrationAlertType.BinningChanged, "CAL_BINNING_CHANGED",
                $"Binning changed from {cal.Binning} to {binning} since calibration; calibration rates were rescaled."));
            cal = cal with
            {
                XRate = cal.XRate * adj,
                YRate = cal.HasDecCalibration ? cal.YRate * adj : cal.YRate,
                Binning = binning,
                PixelScale = cal.PixelScale > 0 ? cal.PixelScale / adj : cal.PixelScale,
            };
        }

        // If the image scale has changed, the calibration is cleared (PHD2 HandleImageScaleChange)
        if (Math.Abs(scaleAdjustment - 1.0) >= 0.01)
        {
            valid = false;
        }

        if (IsOppositeSide(newPierSide, cal.PierSide) && valid)
        {
            CalibrationData before = cal;
            cal = FlipCalibration(cal, pointing.DecFlipRequired);
            flipped = true;
            alerts.Add(new CalibrationAlert(CalibrationAlertType.Flipped, "CAL_FLIPPED",
                $"Calibration flipped: {before.PierSide}({MountTransform.Degrees(before.XAngle):F0},{MountTransform.Degrees(before.YAngle):F0})" +
                $" -> {cal.PierSide}({MountTransform.Degrees(cal.XAngle):F0},{MountTransform.Degrees(cal.YAngle):F0})",
                ShowToUser: false));
        }

        if (newRotatorAngle is { } rot)
        {
            if (cal.RotatorAngleDeg is null)
            {
                // we do not know the rotator position at calibration time so cannot automatically adjust calibration
                alerts.Add(new CalibrationAlert(CalibrationAlertType.RotatorPositionUnknown, "CAL_ROTATOR_UNKNOWN",
                    "Rotator position has changed, recalibration is needed."));
                cal = cal with { RotatorAngleDeg = rot };
            }
            else
            {
                double da = rot - cal.RotatorAngleDeg.Value;
                if (Math.Abs(da) > 0.05)
                {
                    da = MountTransform.Radians(da);
                    cal = cal with
                    {
                        XAngle = MountTransform.NormAngle(cal.XAngle - da),
                        YAngle = MountTransform.NormAngle(cal.YAngle - da),
                        RotatorAngleDeg = rot,
                    };
                    alerts.Add(new CalibrationAlert(CalibrationAlertType.RotatorAdjusted, "CAL_ROTATOR_ADJUSTED",
                        $"Calibration rotated by {MountTransform.Degrees(da):F1}° for the new rotator position.", ShowToUser: false));
                }
            }
        }

        // Compensate RA guide rate for declination if the declination changed and we know both the calibration
        // declination and the current declination. The adjusted xRate is never persisted.
        bool decComp = false;
        double effectiveXRate = cal.XRate;
        if (newDeclination is { } newDec && cal.Declination is { } calDec && newDec != calDec)
        {
            // avoid division by zero and gross errors. If the user didn't calibrate somewhere near the celestial
            // equator, we don't do this
            if (Math.Abs(calDec) > DecCompLimit)
            {
                alerts.Add(new CalibrationAlert(CalibrationAlertType.CalibrationTooFarFromEquator, "CAL_TOO_FAR_FROM_EQUATOR",
                    "Calibration was too far from equator, recalibration is needed."));
            }
            else if (pointing.DecCompensationEnabled)
            {
                // Don't do a full dec comp too close to pole - xRate will become a huge number
                newDec = Math.Max(MountTransform.Radians(-89.0), Math.Min(MountTransform.Radians(89.0), newDec));
                effectiveXRate = cal.XRate / Math.Cos(calDec) * Math.Cos(newDec);
                decComp = true;
            }
        }

        return new CalibrationAdjustment(cal, effectiveXRate, valid, flipped, decComp, alerts)
        {
            CalibrationChanged = !ReferenceEquals(cal, stored),
        };
    }
}
