// SPDX-License-Identifier: MPL-2.0 AND BSD-3-Clause
// Copyright (c) 2006-2010 Craig Stark. Copyright (c) 2012 Bret McKee. All rights reserved. See THIRD_PARTY_NOTICES.md.
// Ported from PHD2 src/myframe.cpp (MyFrame::Dither, DitherSpiral) and src/guider.cpp (MoveLockPosition,
// dither fast recenter in UpdateGuideState) (a6c02722)

using NINA.GuideEngine.Core;

namespace NINA.GuideEngine.Guiding;

public enum DitherMode
{
    Random,
    Spiral,
}

/// <summary>A planned dither: the mount-axis delta (px) and the resulting camera delta and lock position.</summary>
public sealed record DitherPlan(GuidePoint MountDelta, GuidePoint CameraDelta, GuidePoint NewLockPosition, bool LockPositionValid);

/// <summary>Generates dither offsets and computes the new lock position (PHD2 dither semantics).</summary>
public sealed class DitherPlanner
{
    private readonly Random random;
    private readonly DitherSpiral spiral = new();

    public DitherPlanner(int? seed = null)
    {
        random = seed is null ? new Random() : new Random(seed.Value);
    }

    public DitherMode Mode { get; set; } = DitherMode.Random;

    /// <summary>PHD2 dither scale factor (default 1.0).</summary>
    public double ScaleFactor { get; set; } = 1.0;

    /// <summary>Reset the spiral when guiding starts (PHD2 MyFrame::StartGuiding).</summary>
    public void ResetSpiral() => spiral.Reset();

    /// <summary>Random or spiral mount-axis offset for <paramref name="amount"/> pixels.</summary>
    public GuidePoint NextMountOffset(double amount, bool raOnly)
    {
        amount *= ScaleFactor;
        if (Mode == DitherMode.Spiral)
        {
            var (ra, dec) = spiral.Next(amount, raOnly);
            return new GuidePoint(ra, dec);
        }

        double dRa = amount * (random.NextDouble() * 2.0 - 1.0);
        double dDec = raOnly ? 0.0 : amount * (random.NextDouble() * 2.0 - 1.0);
        return new GuidePoint(dRa, dDec);
    }

    /// <summary>
    /// Applies a mount-axis delta to the lock position, reflecting it along the RA/Dec axes when the
    /// result would be invalid (too close to the edge), as PHD2's MoveLockPosition does.
    /// </summary>
    /// <param name="mountToCamera">Mount → camera transform from the calibration.</param>
    /// <param name="isValidLockPosition">Lock position validity (edge distance vs search region).</param>
    public static DitherPlan Plan(GuidePoint lockPosition, GuidePoint mountDelta, int frameWidth, int frameHeight,
        Func<GuidePoint, GuidePoint> mountToCamera, Func<GuidePoint, bool> isValidLockPosition)
    {
        GuidePoint bestCamera = default, bestMount = default;
        double dbest = double.NegativeInfinity;

        for (int q = 0; q < 4; q++)
        {
            int sx = 1 - ((q & 1) << 1);
            int sy = 1 - (q & 2);

            var tmpMount = new GuidePoint(mountDelta.X * sx, mountDelta.Y * sy);
            var tmpCamera = mountToCamera(tmpMount);
            var tmpLock = lockPosition + tmpCamera;

            if (isValidLockPosition(tmpLock))
            {
                return new DitherPlan(tmpMount, tmpCamera, tmpLock, true);
            }

            double d = EdgeDistance(tmpLock, frameWidth, frameHeight);
            if (q == 0 || d > dbest)
            {
                bestCamera = tmpCamera;
                bestMount = tmpMount;
                dbest = d;
            }
        }

        var newLock = lockPosition + bestCamera;
        return new DitherPlan(bestMount, bestCamera, newLock, IsInsideFrame(newLock, frameWidth, frameHeight));
    }

    /// <summary>PHD2 GuiderMultiStar::IsValidLockPosition.</summary>
    public static bool IsValidLockPosition(GuidePoint pt, int frameWidth, int frameHeight, int searchRegion) =>
        pt.X >= 1 + searchRegion && pt.X + 1 + searchRegion < frameWidth && pt.Y >= 1 + searchRegion && pt.Y + 1 + searchRegion < frameHeight;

    private static bool IsInsideFrame(GuidePoint p, int w, int h) => p.X >= 0 && p.X < w && p.Y >= 0 && p.Y < h;

    private static double EdgeDistance(GuidePoint p, int w, int h) => Math.Min(p.X, Math.Min(w - p.X, Math.Min(p.Y, h - p.Y)));

    /// <summary>Port of PHD2 DitherSpiral.</summary>
    private sealed class DitherSpiral
    {
        private int x;
        private int y;
        private int dx;
        private int dy;
        private bool prevRaOnly;

        public DitherSpiral() => Reset();

        public void Reset()
        {
            x = y = 0;
            dx = -1;
            dy = 0;
            prevRaOnly = false;
        }

        public (double Ra, double Dec) Next(double amount, bool raOnly)
        {
            if (raOnly != prevRaOnly)
            {
                Reset();
                prevRaOnly = raOnly;
            }

            if (raOnly)
            {
                // x = 0,1,-1,-2,2,3,-3,-4,4,5,...
                Rot();
                int x0 = x;
                if (dy == 0)
                {
                    x = -x;
                }
                else
                {
                    x += dy;
                }

                return ((x - x0) * amount, 0.0);
            }

            if (x == y || (x > 0 && x == -y) || (x <= 0 && y == 1 - x))
            {
                Rot();
            }

            x += dx;
            y += dy;
            return (dx * amount, dy * amount);
        }

        private void Rot()
        {
            int t = -dx;
            dx = dy;
            dy = t;
        }
    }
}

/// <summary>
/// Fast recenter after a dither: large direct moves (bypassing the guide algorithms) of
/// 0.7 × search region until less than 0.5 px remains on both axes (PHD2 m_ditherRecenter*).
/// </summary>
public sealed class FastRecenter
{
    private GuidePoint remaining = GuidePoint.Invalid;
    private GuidePoint step;
    private int dirX;
    private int dirY;

    public bool IsActive => remaining.IsValid;

    public void Cancel() => remaining = GuidePoint.Invalid;

    /// <summary>Start recentering after a dither with mount delta <paramref name="mountDelta"/> (px).</summary>
    public void Start(GuidePoint mountDelta, double maxMovePixels)
    {
        double dist = mountDelta.Distance();
        if (dist == 0)
        {
            // zero-length dithers only trigger settling
            remaining = GuidePoint.Invalid;
            return;
        }

        remaining = new GuidePoint(Math.Abs(mountDelta.X), Math.Abs(mountDelta.Y));
        dirX = mountDelta.X < 0.0 ? 1 : -1;
        dirY = mountDelta.Y < 0.0 ? 1 : -1;
        double f = maxMovePixels * 0.7 / remaining.Distance();
        step = new GuidePoint(f * remaining.X, f * remaining.Y);
    }

    /// <summary>
    /// Returns the next mount-axis offset (px, same sign convention as a guide offset) to correct,
    /// and whether recentering finished with this step (the caller then resets the distance tracker).
    /// </summary>
    public (GuidePoint MountOffset, bool Done) NextStep()
    {
        if (!remaining.IsValid) throw new InvalidOperationException("fast recenter not active");
        var s = new GuidePoint(Math.Min(remaining.X, step.X), Math.Min(remaining.Y, step.Y));
        remaining = new GuidePoint(remaining.X - s.X, remaining.Y - s.Y);
        bool done = false;
        if (remaining.X < 0.5 && remaining.Y < 0.5)
        {
            remaining = GuidePoint.Invalid;
            done = true;
        }

        return (new GuidePoint(s.X * dirX, s.Y * dirY), done);
    }
}
