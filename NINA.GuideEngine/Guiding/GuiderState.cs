// SPDX-License-Identifier: MPL-2.0

namespace NINA.GuideEngine.Guiding;

/// <summary>
/// Guider application state. The first seven values correspond to PHD2's AppState
/// (Stopped, Selected, Calibrating, Guiding, LostLock, Paused, Looping); the rest are extensions.
/// Settling is not a state but a flag on top of <see cref="Guiding"/>, as in PHD2.
/// </summary>
public enum GuiderState
{
    Stopped,
    Selected,
    Calibrating,
    Guiding,
    LostLock,
    Paused,
    Looping,

    /// <summary>Star lost while guiding for longer than a frame; searching near the last position and then full frame.</summary>
    Reacquiring,

    /// <summary>Guiding stopped by a safety protocol or an unrecoverable error; see the last error.</summary>
    Failed,
}

public static class GuiderStateExtensions
{
    /// <summary>PHD2 AppState name for the event stream.</summary>
    public static string ToPhd2AppState(this GuiderState s) => s switch
    {
        GuiderState.Reacquiring => "LostLock",
        GuiderState.Failed => "Stopped",
        _ => s.ToString(),
    };

    public static bool IsCapturing(this GuiderState s) => s is not (GuiderState.Stopped or GuiderState.Failed);

    public static bool IsGuidingActive(this GuiderState s) => s is GuiderState.Guiding or GuiderState.LostLock or GuiderState.Reacquiring or GuiderState.Paused;
}
