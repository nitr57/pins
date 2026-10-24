// SPDX-License-Identifier: MPL-2.0

using NINA.GuideEngine.Algorithms;
using NINA.GuideEngine.Calibration;
using NINA.GuideEngine.Core;

namespace NINA.GuideEngine.Guiding;

// The pulse model (docs/notes/DEC-PULSE-MODEL.md): what a pulse really moves, learned from the dithers while guiding in any
// mode, and used to size the pulses while GuiderSettings.PulseModel is on. It learns with the setting off too, so switching
// it on uses what is known.
public sealed partial class Guider
{
    private readonly PulseModel pulseModel = new();
    private PulseModelState? storedPulseModel;

    // the frame's pulses belong to the pulse model (a guiding frame), with the calibrated rates they were sized from
    private bool pulseModelFrame;
    private (double X, double Y) pulseModelRates;

    /// <summary>The pulse model's values in use: neutral while it is off. Loop thread only (tests and diagnostics).</summary>
    internal PulseModelValues PulseModelInUse => settings.PulseModel ? pulseModel.Values : PulseModelValues.Neutral;

    /// <summary>The pulse model's estimates, also while it is off. Loop thread only (tests and diagnostics).</summary>
    internal PulseModelValues PulseModelLearned => pulseModel.Values;

    /// <summary>
    /// A pulse model learned in an earlier session (stored by the host from <see cref="PulseModelLearnedEvent"/>): used when
    /// guiding starts with the calibration it was learned with. Null forgets it.
    /// </summary>
    public void RestorePulseModel(PulseModelState? state) => Post(() => storedPulseModel = state);

    // the rates the pulses are sized with: the calibrated ones times the effect in use
    private (double X, double Y) PulseRates(MountTransform t)
    {
        var v = PulseModelInUse;
        return (t.XRate * v.RaEffect, t.YRate * v.DecEffect);
    }

    // guiding starts: the model is relative to the calibration in use (a new calibration starts over)
    private void StartPulseModel()
    {
        pulseModel.Interrupt(mountMoved: true);
        if (calibration is { } cal)
        {
            pulseModel.UseCalibration(cal.Timestamp, storedPulseModel);
        }
    }

    // a guiding frame measured the offsets (not paused)
    private void PulseModelFrameMeasured(double raPx, double decPx, DateTimeOffset now)
    {
        if (!pulseModel.FrameMeasured(raPx, decPx, now, out bool changed))
        {
            return;
        }

        if (pulseModel.State(now) is { } state)
        {
            storedPulseModel = state;
            Emit(new PulseModelLearnedEvent(now, state));
        }

        if (changed)
        {
            EmitPulseModelInUse(now);
        }
    }

    private void PulseModelPulsesSent(IReadOnlyList<PulseCommand> pulses)
    {
        if (pulseModelFrame)
        {
            pulseModel.PulsesSent(pulses, pulseModelRates.X, pulseModelRates.Y);
        }
    }

    private void EmitPulseModelInUse(DateTimeOffset now)
    {
        if (settings.PulseModel)
        {
            Emit(new PulseModelUpdatedEvent(now, pulseModel.Values));
        }
    }
}
