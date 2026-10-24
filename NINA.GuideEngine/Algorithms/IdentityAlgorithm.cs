// SPDX-License-Identifier: MPL-2.0 AND BSD-3-Clause
// Copyright (c) 2012 Bret McKee
// Copyright (c) 2006-2010 Craig Stark
// Ported from PHD2 src/guide_algorithm_identity.cpp (a6c02722)

namespace NINA.GuideEngine.Algorithms;

/// <summary>Returns its input unchanged ("None" in the PHD2 UI).</summary>
public sealed class IdentityAlgorithm : GuideAlgorithmBase
{
    public override string Name => "Identity";

    public override GuideAlgorithmKind Kind => GuideAlgorithmKind.Identity;

    public override double Result(double input) => input;

    public override void Reset()
    {
    }

    protected override void RestoreDefaults()
    {
    }
}
