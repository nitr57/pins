// SPDX-License-Identifier: MPL-2.0 AND BSD-3-Clause
// Copyright (c) 2012 Bret McKee
// Ported from PHD2 src/mount.h (MountMoveOptionBits) (a6c02722)

namespace NINA.GuideEngine.Algorithms;

/// <summary>Mount move option bits (PHD2 <c>MountMoveOptionBits</c>).</summary>
[Flags]
public enum MoveOptions
{
    None = 0,

    /// <summary>Filter the move through the guide algorithm.</summary>
    AlgoResult = 1 << 0,

    /// <summary>Use the guide algorithm to deduce the move (paused or star lost).</summary>
    AlgoDeduce = 1 << 1,

    /// <summary>Use backlash compensation for this move.</summary>
    UseBlc = 1 << 2,

    /// <summary>Show the move on graphs / include in statistics.</summary>
    Graph = 1 << 3,

    /// <summary>Manual move: allowed even when guiding output is disabled.</summary>
    Manual = 1 << 4,

    /// <summary>PHD2 <c>MOVEOPTS_CALIBRATION_MOVE</c>.</summary>
    CalibrationMove = None,

    /// <summary>PHD2 <c>MOVEOPTS_GUIDE_STEP</c>.</summary>
    GuideStep = AlgoResult | UseBlc | Graph,

    /// <summary>PHD2 <c>MOVEOPTS_DEDUCED_MOVE</c>.</summary>
    DeducedMove = AlgoDeduce | UseBlc | Graph,

    /// <summary>PHD2 <c>MOVEOPTS_RECOVERY_MOVE</c> (fast recenter after dither/calibration).</summary>
    RecoveryMove = UseBlc,

    /// <summary>PHD2 <c>MOVEOPTS_AO_BUMP</c>.</summary>
    AoBump = UseBlc,
}
