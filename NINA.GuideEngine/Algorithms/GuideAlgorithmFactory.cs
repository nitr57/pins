// SPDX-License-Identifier: MPL-2.0 AND BSD-3-Clause
// Copyright (c) 2012 Bret McKee
// Ported from PHD2 src/mount.cpp (Mount::CreateGuideAlgorithm) (a6c02722)

using System.Collections.Concurrent;
using NINA.GuideEngine.Core;

namespace NINA.GuideEngine.Algorithms;

/// <summary>Creates guide algorithms by kind (PHD2 <c>Mount::CreateGuideAlgorithm</c>).</summary>
public static class GuideAlgorithmFactory
{
    /// <summary>PHD2 default RA (X) algorithm for mounts.</summary>
    public const GuideAlgorithmKind DefaultRaAlgorithm = GuideAlgorithmKind.Hysteresis;

    /// <summary>PHD2 default Dec (Y) algorithm for mounts.</summary>
    public const GuideAlgorithmKind DefaultDecAlgorithm = GuideAlgorithmKind.ResistSwitch;

    private static readonly ConcurrentDictionary<GuideAlgorithmKind, Func<GuideAxis, IGuideAlgorithm>> Extra = new();

    /// <summary>
    /// Registers a creator for a kind not implemented in the engine core (e.g. GaussianProcess in v2).
    /// </summary>
    public static void Register(GuideAlgorithmKind kind, Func<GuideAxis, IGuideAlgorithm> creator) => Extra[kind] = creator;

    /// <summary>True when <see cref="Create"/> can build <paramref name="kind"/>.</summary>
    public static bool IsSupported(GuideAlgorithmKind kind) => kind switch
    {
        GuideAlgorithmKind.None or GuideAlgorithmKind.Identity or GuideAlgorithmKind.Hysteresis or GuideAlgorithmKind.Lowpass
            or GuideAlgorithmKind.Lowpass2 or GuideAlgorithmKind.ResistSwitch or GuideAlgorithmKind.Predictive => true,
        _ => Extra.ContainsKey(kind),
    };

    /// <summary>
    /// Creates an algorithm with PHD2 default parameters. <see cref="GuideAlgorithmKind.None"/> yields
    /// Identity as in PHD2.
    /// </summary>
    /// <exception cref="NotSupportedException">The kind is not available.</exception>
    public static IGuideAlgorithm Create(GuideAlgorithmKind kind, GuideAxis axis = GuideAxis.Ra)
    {
        switch (kind)
        {
            case GuideAlgorithmKind.None:
            case GuideAlgorithmKind.Identity:
                return new IdentityAlgorithm();
            case GuideAlgorithmKind.Hysteresis:
                return new HysteresisAlgorithm();
            case GuideAlgorithmKind.Lowpass:
                return new LowpassAlgorithm();
            case GuideAlgorithmKind.Lowpass2:
                return new Lowpass2Algorithm();
            case GuideAlgorithmKind.ResistSwitch:
                return new ResistSwitchAlgorithm();
            case GuideAlgorithmKind.Predictive:
                return new PredictiveAlgorithm(periodicError: axis == GuideAxis.Ra);
            default:
                if (Extra.TryGetValue(kind, out var creator))
                    return creator(axis);
                throw new NotSupportedException($"Guide algorithm {kind} is not available");
        }
    }

    /// <summary>
    /// Creates an algorithm and, if it has a min-move, sets it to <paramref name="minMove"/> (typically
    /// <see cref="MinMove.SmartDefault(double)"/>).
    /// </summary>
    public static IGuideAlgorithm Create(GuideAlgorithmKind kind, GuideAxis axis, double minMove)
    {
        var algo = Create(kind, axis);
        if (algo.MinMove >= 0.0)
            algo.MinMove = minMove;
        return algo;
    }

    /// <summary>Kind of an algorithm instance (<see cref="GuideAlgorithmKind.None"/> for unknown types).</summary>
    public static GuideAlgorithmKind KindOf(IGuideAlgorithm? algo) => algo switch
    {
        null => GuideAlgorithmKind.None,
        GuideAlgorithmBase b => b.Kind,
        _ => GuideAlgorithmKind.None,
    };
}
