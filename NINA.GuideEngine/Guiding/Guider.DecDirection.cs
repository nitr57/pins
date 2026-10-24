// SPDX-License-Identifier: MPL-2.0

using NINA.GuideEngine.Algorithms;
using NINA.GuideEngine.Core;

namespace NINA.GuideEngine.Guiding;

// Dec guide mode Drift: the open-loop Dec drift is measured while guiding in any mode; in Drift mode it picks the one Dec
// direction the corrector may pulse in. The drift always comes from the open-loop fit, also with the Predictive algorithm,
// whose own drift state follows Dec backlash (see docs/ALGORITHMS.md, "Dec drift estimator").
public sealed partial class Guider
{
    private readonly DecDriftEstimator decDrift = new();
    private readonly DecDirectionPolicy decPolicy = new();
    private volatile DecDirectionState? decDirectionState;

    /// <summary>
    /// State of <see cref="DecGuideMode.Drift"/> (the direction guided now, the drift it follows), null in the other modes.
    /// Updated every guide frame; safe to read from any thread.
    /// </summary>
    public DecDirectionState? DecDirection => decDirectionState;

    /// <summary>The open-loop Dec drift over the window (loop thread only: tests and diagnostics).</summary>
    internal DriftEstimate? OpenLoopDecDrift() => decDrift.Estimate();

    /// <summary>The Dec wander rate the drift's uncertainty assumes (px²/s; loop thread only: tests and diagnostics).</summary>
    internal double DecWanderRate() => decDrift.WanderRate(0);

    /// <summary>The Dec dead band the open-loop position is reconstructed with (px; loop thread only: tests and diagnostics).</summary>
    internal double DecBacklashPx() => decDrift.BacklashPx;

    /// <summary>The Dec direction the corrector may use for the current frame, null for both (loop thread only: tests).</summary>
    internal GuideDirection? DriftDecDirectionNow => corrector.DriftDecDirection;

    // guiding (re)started, a meridian flip, the Dec axis reversed, other pixels: drift and direction are learned anew. The
    // note goes out at once: after a flip while paused the star is usually lost, and the next measured frame may be far off
    private void ResetDecDirection(string reason)
    {
        decDrift.Reset();
        decPolicy.Reset();
        decPolicy.TakeNotes();
        corrector.DriftDecDirection = null;
        if (settings.DecGuideMode == DecGuideMode.Drift)
        {
            decPolicy.NoteReset(reason);
            PublishDecDirection(null);
            EmitDecDirectionNotes(clock.UtcNow);
        }
    }

    // the Dec guide mode changed: a newly chosen Drift mode starts with both directions, and picks one at once when the
    // drift measured so far has held for the minute already
    private void DecGuideModeChanged(DecGuideMode was, DecGuideMode now)
    {
        if (was == now)
        {
            return;
        }

        decPolicy.Reset(keepHold: true);
        decPolicy.TakeNotes();
        if (now == DecGuideMode.Drift)
        {
            decPolicy.NoteReset("Dec guide mode Drift");
            PublishDecDirection(null);
            EmitDecDirectionNotes(clock.UtcNow);
        }
        else
        {
            decDirectionState = null;
        }
    }

    /// <summary>
    /// One guide frame with a measured offset, before its correction is computed: the open-loop Dec position gets the
    /// sample (not while settling), and in Drift mode the direction the corrector may use is picked. In the other modes the
    /// policy only follows how long the drift has held (a Coach trial with Drift then guides one way from its start).
    /// </summary>
    private void UpdateDecDirection(DateTimeOffset now, double decOffsetPx, bool settling)
    {
        bool drift = settings.DecGuideMode == DecGuideMode.Drift;
        decDrift.GuardReversals = drift && decPolicy.Direction != DecGuideDirection.Both;

        // after guiding one direction the Dec gears may sit anywhere in their play: until a direction is picked again, the
        // drift must hold whatever the dead band
        decDrift.DoubtBacklash = drift && decPolicy.Faded && decPolicy.Direction == DecGuideDirection.Both;
        if (!settling)
        {
            decDrift.Add(now, decOffsetPx);
        }

        if (!drift)
        {
            decPolicy.Observe(now, decDrift.Estimate());
            corrector.DriftDecDirection = null;
            return;
        }

        var estimate = decDrift.Estimate();
        decPolicy.Update(now, estimate, decOffsetPx, settling, pixelScale);
        decDrift.GuardReversals = decPolicy.Direction != DecGuideDirection.Both;

        // settling (after a dither, at the start) guides both ways: a dither may put the star on the side the direction
        // cannot correct
        corrector.DriftDecDirection = settling ? null : decPolicy.Allowed;
        PublishDecDirection(estimate);
        EmitDecDirectionNotes(now);
    }

    private void EmitDecDirectionNotes(DateTimeOffset now)
    {
        foreach (var note in decPolicy.TakeNotes())
        {
            string message = note.Kind == DecDirectionNoteKind.Summary
                ? string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"{note.Message}, Dec dead band {decDrift.BacklashPx:0.#} px, {decDrift.Jumps} jump{(decDrift.Jumps == 1 ? "" : "s")} left out")
                : note.Message;
            Emit(new DecDirectionNoteEvent(now, note.Kind, decPolicy.Direction, message));
        }
    }

    private void PublishDecDirection(DriftEstimate? drift)
    {
        decDirectionState = new DecDirectionState
        {
            Direction = decPolicy.Direction,
            ValveOpen = decPolicy.ValveOpen,
            DriftPxPerSec = drift?.PxPerSec,
            DriftSigmaPxPerSec = drift?.SigmaPxPerSec,
            Switches = decPolicy.Switches,
            ValveOpenings = decPolicy.ValveOpenings,
        };
    }
}
