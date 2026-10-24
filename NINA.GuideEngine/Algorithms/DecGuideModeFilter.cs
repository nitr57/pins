// SPDX-License-Identifier: MPL-2.0 AND BSD-3-Clause
// Copyright (c) 2006-2010 Craig Stark
// Ported from PHD2 src/scope.cpp (Scope::MoveAxis) (a6c02722)

using NINA.GuideEngine.Core;

namespace NINA.GuideEngine.Algorithms;

/// <summary>
/// Applies the Dec guide mode to a Dec pulse. As in PHD2 <c>Scope::MoveAxis</c> the mode only affects
/// algorithm-result and deduced (dead-reckoning) moves; calibration, recovery and manual moves pass
/// through unchanged.
/// </summary>
public static class DecGuideModeFilter
{
    /// <summary>
    /// True when the mode permits a Dec move in <paramref name="direction"/>. <see cref="DecGuideMode.Drift"/> permits
    /// both like Auto: the direction it picks is applied by <see cref="AxisCorrector.DriftDecDirection"/>.
    /// </summary>
    public static bool Allows(DecGuideMode mode, GuideDirection direction)
    {
        if (direction.Axis() != GuideAxis.Dec)
            return true;
        return !(mode == DecGuideMode.Off
            || (direction == GuideDirection.South && mode == DecGuideMode.North)
            || (direction == GuideDirection.North && mode == DecGuideMode.South));
    }

    /// <summary>
    /// Returns the duration to issue: 0 when the mode forbids the direction and the move is an
    /// algorithm/deduced move, otherwise <paramref name="durationMs"/>. RA directions are never filtered.
    /// </summary>
    public static int Apply(DecGuideMode mode, GuideDirection direction, int durationMs, bool isAlgorithmOrDeducedMove = true)
    {
        if (!isAlgorithmOrDeducedMove)
            return durationMs;
        return Allows(mode, direction) ? durationMs : 0;
    }
}
