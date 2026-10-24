// SPDX-License-Identifier: MPL-2.0

using NINA.GuideEngine.Core;

namespace NINA.GuideEngine.Algorithms;

/// <summary>Which Dec backlash compensation the guider uses.</summary>
public enum BacklashModel
{
    /// <summary>PHD2's fixed pulse at every Dec reversal, adjusted slowly (<see cref="BacklashCompensation"/>).</summary>
    Fixed,

    /// <summary>
    /// The gear's position inside its play is tracked, a pulse that has to take up play is made only when the offset
    /// clearly needs it, and it is lengthened by the play that is left (<see cref="PlayModelBacklash"/>).
    /// </summary>
    PlayModel,
}

/// <summary>
/// Dec backlash compensation as used by <see cref="AxisCorrector"/>. Call order per move: <see cref="TrackResults"/> with
/// the raw Dec offset, the guide algorithm, <see cref="HoldMove"/>, <see cref="Apply"/>, then
/// <see cref="PulseSent"/> with the Dec pulse as computed.
/// </summary>
public interface IBacklashCompensation
{
    bool IsEnabled { get; }

    /// <summary>
    /// The pulse a reversal gets now (ms): the fixed pulse, or the play model's estimate of the play a reversal from the
    /// side the gear rests on would take up.
    /// </summary>
    int PulseWidthMs { get; }

    /// <summary>Last Dec direction a move went (null = none or unknown).</summary>
    GuideDirection? LastDirection { get; }

    /// <summary>The raw Dec offset (mount px) of the frame before the guide algorithm runs.</summary>
    void TrackResults(MoveOptions options, double yRawOffset, double decMinMove, double yRate, DateTimeOffset now = default);

    /// <summary>
    /// True when a Dec move of <paramref name="yGuideDistance"/> (px, after the algorithm) is to be skipped because it
    /// would first have to take up play (a reversal, or a gear floating inside its play) and the offset does not need it.
    /// False by default.
    /// </summary>
    bool HoldMove(MoveOptions options, double yGuideDistance, double yRate, DateTimeOffset now = default) => false;

    /// <summary>Adds the compensation to <paramref name="yAmountMs"/> and returns the amount added (ms).</summary>
    int Apply(MoveOptions options, double yGuideDistance, ref int yAmountMs, DateTimeOffset now = default);

    /// <summary>
    /// Pulse time (ms) of <paramref name="durationMs"/> that moves the star, given the <paramref name="compensationMs"/>
    /// <see cref="Apply"/> added: by default all but the compensation, which only takes up slack.
    /// </summary>
    double EffectiveMs(GuideDirection direction, int durationMs, int compensationMs) => Math.Max(0, durationMs - compensationMs);

    /// <summary>A Dec pulse that moves the gear (sent or about to be sent). Ignored by default.</summary>
    void PulseSent(GuideDirection direction, int durationMs)
    {
    }

    /// <summary>
    /// The gear was driven far enough in <paramref name="direction"/> to rest against that side (end of a calibration).
    /// Ignored by default.
    /// </summary>
    void GearEngaged(GuideDirection direction)
    {
    }

    /// <summary>Forgets where the gear is (the mount may have moved it unaccounted).</summary>
    void ResetState();
}
