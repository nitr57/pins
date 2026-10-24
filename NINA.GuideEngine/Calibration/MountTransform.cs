// SPDX-License-Identifier: MPL-2.0 AND BSD-3-Clause
// Copyright (c) 2012 Bret McKee
// Copyright (c) 2006-2010 Craig Stark
// Ported from PHD2 src/mount.cpp, src/image_math.h (a6c02722)

using NINA.GuideEngine.Core;

namespace NINA.GuideEngine.Calibration;

/// <summary>
/// Camera ⇄ mount coordinate transform (port of Mount::TransformCameraCoordinatesToMountCoordinates and
/// Mount::TransformMountCoordinatesToCameraCoordinates) plus rate-based pulse helpers.
/// Mount X is along the RA axis (positive = star displaced in the East-pulse direction, corrected by a West
/// pulse), mount Y along the Dec axis (positive = displaced in the North-pulse direction, corrected by South).
/// </summary>
public sealed class MountTransform
{
    /// <summary>Creates a transform from a calibration (PHD2 Mount::SetCalibration).</summary>
    /// <param name="calibration">Calibration angles and rates.</param>
    /// <param name="effectiveXRate">RA rate to use for pulse lengths (dec-compensated, PHD2 m_xRate); defaults to the calibration's.</param>
    public MountTransform(CalibrationData calibration, double? effectiveXRate = null)
        : this(calibration.XAngle, calibration.YAngle, effectiveXRate ?? calibration.XRate, calibration.YRate)
    {
    }

    public MountTransform(double xAngle, double yAngle, double xRate, double yRate)
    {
        XAngle = xAngle;
        YAngle = yAngle;
        XRate = xRate;
        YRate = yRate;
        YAngleError = NormAngle(xAngle - yAngle + Math.PI / 2.0);
    }

    public double XAngle { get; }

    public double YAngle { get; }

    /// <summary>RA rate px/ms used for pulse lengths.</summary>
    public double XRate { get; }

    /// <summary>Dec rate px/ms used for pulse lengths.</summary>
    public double YRate { get; }

    /// <summary>PHD2 m_yAngleError = norm(xAngle - yAngle + π/2), the orthogonality error (≈ π when Dec is mirrored).</summary>
    public double YAngleError { get; }

    /// <summary>Port of TransformCameraCoordinatesToMountCoordinates. Returns invalid for an invalid input.</summary>
    public GuidePoint CameraToMount(GuidePoint cameraVector)
    {
        if (!cameraVector.IsValid)
        {
            return GuidePoint.Invalid;
        }

        double hyp = cameraVector.Distance();
        double cameraTheta = PhdAngle(cameraVector.X, cameraVector.Y);

        double xAngle = cameraTheta - XAngle; // RA axis rotation vs camera X axis
        double yAngle = cameraTheta - (XAngle + YAngleError); // YAngleError is the orthogonality error

        return new GuidePoint(Math.Cos(xAngle) * hyp, Math.Sin(yAngle) * hyp);
    }

    /// <summary>Port of TransformMountCoordinatesToCameraCoordinates. Returns invalid for an invalid input.</summary>
    public GuidePoint MountToCamera(GuidePoint mountVector)
    {
        if (!mountVector.IsValid)
        {
            return GuidePoint.Invalid;
        }

        double hyp = mountVector.Distance();
        double mountTheta = PhdAngle(mountVector.X, mountVector.Y);

        if (Math.Abs(YAngleError) > Math.PI / 2.0)
        {
            mountTheta = -mountTheta;
        }

        double xAngle = mountTheta + XAngle;
        return new GuidePoint(Math.Cos(xAngle) * hyp, Math.Sin(xAngle) * hyp);
    }

    /// <summary>RA pulse for a mount-X distance in px: PHD2 MoveOffset (dir = dist &gt; 0 ? West : East, ms = ROUND(|dist / xRate|)).</summary>
    public PulseCommand RaPulse(double mountXPx) =>
        new(mountXPx > 0.0 ? GuideDirection.West : GuideDirection.East, PulseMs(mountXPx, XRate));

    /// <summary>Dec pulse for a mount-Y distance in px: PHD2 MoveOffset (dir = dist &gt; 0 ? South : North).</summary>
    public PulseCommand DecPulse(double mountYPx) =>
        new(mountYPx > 0.0 ? GuideDirection.South : GuideDirection.North, PulseMs(mountYPx, YRate));

    /// <summary>Pulse length for a distance at a rate: PHD2 ROUND(fabs(dist / rate)); 0 for an unusable rate.</summary>
    public static int PulseMs(double distancePx, double ratePxPerMs)
    {
        if (!(ratePxPerMs > 0.0) || double.IsInfinity(ratePxPerMs))
        {
            return 0;
        }

        double ms = Math.Floor(Math.Abs(distancePx / ratePxPerMs) + 0.5);
        return ms >= int.MaxValue ? int.MaxValue : (int)ms;
    }

    /// <summary>Expected star movement in px for a pulse (inverse of <see cref="PulseMs"/>).</summary>
    public static double PulseDistancePx(int durationMs, double ratePxPerMs) => durationMs * ratePxPerMs;

    /// <summary>PHD2 norm_angle: value in [-π, π).</summary>
    public static double NormAngle(double val) => Norm(val, -Math.PI, Math.PI);

    /// <summary>PHD2 norm(val, start, end).</summary>
    public static double Norm(double val, double start, double end)
    {
        double range = end - start;
        double ofs = val - start;
        return val - Math.Floor(ofs / range) * range;
    }

    /// <summary>Delta from the nearest multiple of 90°, degrees (PHD2 orthoError).</summary>
    public static double OrthogonalityErrorDegrees(double xAngle, double yAngle) =>
        Degrees(Math.Abs(Math.Abs(NormAngle(xAngle - yAngle)) - Math.PI / 2.0));

    public static double Degrees(double radians) => radians * 180.0 / Math.PI;

    public static double Radians(double degrees) => degrees * Math.PI / 180.0;

    /// <summary>PHD_Point::Angle semantics: 0 for the zero vector, else atan2(y, x).</summary>
    internal static double PhdAngle(double dx, double dy) => dx != 0 || dy != 0 ? Math.Atan2(dy, dx) : 0.0;

    /// <summary>PHD_Point a.Angle(b): angle of the vector from b to a.</summary>
    internal static double PhdAngle(GuidePoint a, GuidePoint b) => PhdAngle(a.X - b.X, a.Y - b.Y);
}
