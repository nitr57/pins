// SPDX-License-Identifier: MPL-2.0

using NINA.GuideEngine.Algorithms;
using NINA.GuideEngine.Calibration;
using NINA.GuideEngine.Core;
using NINA.GuideEngine.MultiStar;

namespace NINA.GuideEngine.Guiding;

// The Dec backlash play model (docs/notes/PLAY-MODEL-BACKLASH.md): one instance for the guider's lifetime, so the gear's
// position and what was learned survive the corrector being rebuilt on a settings change. A calibration measures the
// shape of the play with either backlash model, so switching to the play model uses it.
public sealed partial class Guider
{
    private readonly PlayModelBacklash playBacklash = new();
    private int playBacklashLearnCount;
    private volatile PlayModelValues? playBacklashStatus;

    // the Dec play measured again before guiding starts after a slew, and where the play was last measured
    private DecPlayProbe? playProbe;
    private CalibrationAdjustment? playProbeAdjustment;
    private GuidePoint playProbeStart = GuidePoint.Invalid;
    private GuidePoint playProbeOrigin = GuidePoint.Invalid;
    private int playProbeLostFrames;
    private bool playProbeInvertedDec;
    private PierSide playProbePreviousPier = PierSide.Unknown;
    private MountSnapshot? playMeasuredAt;

    /// <summary>The play model's values (also while the fixed compensation is in use). Loop thread only (tests and diagnostics).</summary>
    internal PlayModelValues BacklashPlayModel => playBacklash.Values;

    /// <summary>
    /// The Dec backlash play model while it compensates (as of the last guide frame), null while it does not. Safe to read
    /// from any thread.
    /// </summary>
    public PlayModelValues? BacklashPlayModelStatus => playBacklashStatus;

    /// <summary>
    /// A play model learned in an earlier session (stored by the host from <see cref="BacklashPlayModelLearnedEvent"/>).
    /// Null keeps what this session knows.
    /// </summary>
    public void RestoreBacklashPlayModel(PlayModelState? state) => Post(() => playBacklash.Restore(state));

    /// <summary>
    /// Whether the settings use the play model: it needs the Predictive Dec algorithm, which is told what the pulses really
    /// moved (<see cref="IGuideAlgorithm.CorrectionApplied"/>). PHD2's algorithms keep acting on corrections the play model
    /// held back or the play swallowed, and guided a hard play worse with it than without in the simulations
    /// (docs/notes/PLAY-MODEL-BACKLASH.md); they get the fixed compensation.
    /// </summary>
    internal static bool UsesPlayModel(GuiderSettings s) =>
        s.Backlash.Model == BacklashModel.PlayModel && s.DecAlgorithm.Kind == GuideAlgorithmKind.Predictive;

    // the compensation for a corrector built from the settings s
    private IBacklashCompensation CreateBacklash(GuiderSettings s, PulseLimiter limiter)
    {
        if (!UsesPlayModel(s))
        {
            // the fixed compensation, with the pulse it learned before (Guider.Backlash.cs)
            return CreateFixedBacklash(s.Backlash, limiter);
        }

        playBacklash.Limiter = limiter;
        if (!ReferenceEquals(corrector.Backlash, playBacklash))
        {
            // the fixed compensation was in use: the model saw none of its pulses
            playBacklash.ResetState();
        }

        playBacklash.SetPriorFullReversal(s.Backlash.PulseMs);
        playBacklash.Enable(s.Backlash.Enabled);
        return playBacklash;
    }

    // a calibration completed: its Dec return measured the shape of the play
    private void PlayModelCalibrated(CalibrationData result, MountSnapshot snapshot, DateTimeOffset now)
    {
        if (result.HasDecCalibration && playBacklash.SeedFromCalibration(result.DecTakeUp, result.YRate))
        {
            playMeasuredAt = snapshot;
            playBacklashLearnCount = -1;
            EmitPlayModelLearned(now);
        }
    }

    // guiding starts with a kept calibration: after a slew the gear rests on an unknown side of its play, and the play may
    // have changed with the load (docs/notes/PLAY-MODEL-BACKLASH.md, "the play changed with the target"); it is measured
    // again before guiding starts. False when no measurement is needed.
    private bool StartPlayProbe(GuidePoint starPosition, CalibrationAdjustment adj, MountSnapshot snapshot, DateTimeOffset now,
        out IReadOnlyList<PulseCommand> pulses)
    {
        pulses = [];
        if (!PlayProbeNeeded(adj.Calibration, snapshot))
        {
            return false;
        }

        UseCalibration(adj);
        playProbeInvertedDec = false;
        playProbePreviousPier = playMeasuredAt?.PierSide ?? PierSide.Unknown;
        playProbeStart = starPosition;
        SetState(GuiderState.Calibrating);
        pulses = BeginPlayProbe(adj, starPosition, snapshot, now);
        return true;
    }

    // a measurement from the star's current position; guiding later locks on playProbeStart
    private IReadOnlyList<PulseCommand> BeginPlayProbe(CalibrationAdjustment adj, GuidePoint starPosition, MountSnapshot snapshot,
        DateTimeOffset now)
    {
        playProbe = new DecPlayProbe(adj.Calibration.YRate, settings.MaxDecDurationMs, PlayModelBacklash.SizesMs[^1]);
        playProbeAdjustment = adj;
        playProbeOrigin = starPosition;
        playProbeLostFrames = 0;
        return PlayProbeStep(starPosition, snapshot, now);
    }

    private bool PlayProbeNeeded(CalibrationData cal, MountSnapshot snapshot)
    {
        var backlash = settings.Backlash;
        if (!backlash.Enabled || !UsesPlayModel(settings) || !(backlash.RemeasureAfterSlewDeg > 0) || settings.DecGuideMode != DecGuideMode.Auto
            || IsCoachMeasuring || !cal.HasDecCalibration || !(cal.YRate > 0) || !double.IsFinite(cal.YRate))
        {
            return false;
        }

        // nothing measured in this session yet: the pointing since the last measurement is unknown
        return playMeasuredAt is not { } measured || MountStateWatcher.MovedFar(measured, snapshot, backlash.RemeasureAfterSlewDeg * 60);
    }

    private MultiStarFrameResult PlayProbeFrame(GuideFrame frame, MountSnapshot snapshot, DateTimeOffset now, out IReadOnlyList<PulseCommand> pulses)
    {
        pulses = [];
        var r = tracker.ProcessFrame(frame, GuidePoint.Invalid, TrackerState.Looping, now);
        if (!r.StarFound)
        {
            Emit(new StarLostEvent(now, frame.FrameNumber, 0, r.Primary.Mass, r.Primary.Snr, r.Primary.Hfd, 0, (int)r.ErrorCode, r.Status));
            if (++playProbeLostFrames * Math.Max(settings.ExposureMs, 1) / 1000.0 > settings.Safety.ReacquireTimeoutSec)
            {
                // guiding takes over the search for the star; the gear's position stays unknown
                playProbe!.Abort("star lost");
                pulses = EndPlayProbe(lastStarPosition, snapshot, now);
            }

            return r;
        }

        playProbeLostFrames = 0;
        lastStarPosition = r.Primary.Position;
        pulses = PlayProbeStep(r.Primary.Position, snapshot, now);
        return r;
    }

    private IReadOnlyList<PulseCommand> PlayProbeStep(GuidePoint starPosition, MountSnapshot snapshot, DateTimeOffset now)
    {
        var probe = playProbe!;
        double decPx = transform!.CameraToMount(starPosition - playProbeOrigin).Y;
        var pulse = probe.Update(decPx);
        if (pulse is { } p)
        {
            Emit(new CalibratingEvent(now)
            {
                Mount = MountName,
                Direction = "Backlash",
                Distance = Math.Abs(decPx),
                Position = starPosition,
                Step = (probe.NorthMs + probe.SouthMs) / probe.StepMs,
                State = probe.Status,
                Progress = probe.Progress,
            });
            return [p];
        }

        if (probe.Phase is DecPlayProbePhase.Measured or DecPlayProbePhase.Failed)
        {
            return EndPlayProbe(starPosition, snapshot, now);
        }

        return [];
    }

    // the measurement ended: guiding starts on the star's original position, the play model seeded from the return. A probe
    // whose North pulses moved the star South found the calibration's Dec direction wrong: Dec is inverted (after a meridian
    // flip) and measured again, or the mount is calibrated again; guiding with it would push Dec away (pins log
    // 2026-10-19: the probe said so, guiding started anyway and ran away by 270 px)
    private IReadOnlyList<PulseCommand> EndPlayProbe(GuidePoint starPosition, MountSnapshot snapshot, DateTimeOffset now)
    {
        var probe = playProbe!;
        var adj = playProbeAdjustment!;
        CancelPlayProbe();
        playMeasuredAt = snapshot;

        if (probe.DecReversed)
        {
            return PlayProbeFoundDecReversed(probe, adj, starPosition, snapshot, now);
        }

        bool measured = probe.Phase == DecPlayProbePhase.Measured;
        bool seeded = measured && playBacklash.SeedFromCalibration(probe.TakeUp, probe.RatePxPerMs);
        bool forgotten = false;
        if (!seeded && playProbePreviousPier != PierSide.Unknown && snapshot.PierSide != PierSide.Unknown && snapshot.PierSide != playProbePreviousPier)
        {
            // the play was learned on the other pier side and could not be measured on this one: start from the prior
            playBacklash.ForgetLearned();
            forgotten = true;
        }

        EnterGuiding(playProbeStart, adj, now);
        if (playProbeInvertedDec)
        {
            // the probe already inverted Dec after the flip, and the second measurement confirmed it: the post-flip self-check
            // has nothing left to find, and a later runaway is not a reason to invert back
            decFlip.Reset();
            flipInverted = true;
        }

        if (measured)
        {
            // the return's last pulses moved the star South
            playBacklash.GearEngaged(GuideDirection.South);
        }

        string rate = probe.RateMeasured
            ? $", Dec rate {probe.RatePxPerMs * 1000:0.000} px/s (calibrated {adj.Calibration.YRate * 1000:0.000} px/s)"
            : "";
        string message = !measured ? $"Dec play not measured after the slew: {probe.FailureReason}"
            : seeded ? $"Dec play measured after the slew: North {probe.NorthMs} ms, South {probe.SouthMs} ms, gear engaged South{rate}"
            : $"Dec play after the slew: gear engaged South (North {probe.NorthMs} ms, South {probe.SouthMs} ms), the return too short to fit the play{rate}";
        if (forgotten)
        {
            message += "; what was learned on the other pier side is forgotten";
        }

        Emit(new DecPlayProbeEvent(now, seeded, message));
        if (seeded || forgotten)
        {
            playBacklashLearnCount = -1;
            EmitPlayModelLearned(now);
        }

        return [];
    }

    private IReadOnlyList<PulseCommand> PlayProbeFoundDecReversed(DecPlayProbe probe, CalibrationAdjustment adj, GuidePoint starPosition,
        MountSnapshot snapshot, DateTimeOffset now)
    {
        if (adj.Flipped && settings.VerifyDecAfterFlip && !playProbeInvertedDec)
        {
            // the calibration was flipped for this pier side with the wrong Dec-flip setting: invert Dec (saving the setting)
            // and measure again, which confirms the new direction. The North pulses engaged the gear North
            Emit(new DecPlayProbeEvent(now, false, $"Dec play not measured after the slew: {probe.FailureReason}; Dec inverted for the flipped calibration"));
            playProbeInvertedDec = true;
            InvertDecAfterFlip(now);
            playBacklash.GearEngaged(GuideDirection.North);
            return BeginPlayProbe(adjustment!, starPosition, snapshot, now);
        }

        // not explained by a flip, or still wrong after inverting: the calibration does not fit the mount
        Emit(new DecPlayProbeEvent(now, false, $"Dec play not measured after the slew: {probe.FailureReason}; calibrating again"));
        Alert(GuideErrorCode.CalibrationInvalidated, "North pulses moved the star South: the calibration's Dec direction does not fit the mount");
        playBacklash.ResetState();
        corrector.DecAlgorithm.Reset();
        StartCalibration(starPosition, snapshot, now, out var pulses);
        return pulses;
    }

    private void CancelPlayProbe()
    {
        playProbe = null;
        playProbeAdjustment = null;
    }

    // the corrector counted the frame's Dec pulse as sent, but it was not: the gear's position is no longer known (the fixed
    // compensation keeps PHD2's behaviour)
    private void PlayModelPulsesNotSent()
    {
        playProbe?.Abort("a pulse was not sent");
        if (ReferenceEquals(corrector.Backlash, playBacklash))
        {
            playBacklash.ResetState();
        }
    }

    // guiding starts right after a calibration: its South return and nudges left the gear engaged South
    private void PlayModelAfterCalibration(CalibrationData result)
    {
        if (result.HasDecCalibration && result.DecTakeUp.Count > 0)
        {
            playBacklash.GearEngaged(GuideDirection.South);
        }
    }

    // after every guide frame: the status snapshot, and what was learned for the host to store
    private void EmitPlayModelLearned(DateTimeOffset now)
    {
        playBacklashStatus = settings.Backlash.Enabled && UsesPlayModel(settings) ? playBacklash.Values : null;
        if (playBacklash.LearnCount == playBacklashLearnCount)
        {
            return;
        }

        playBacklashLearnCount = playBacklash.LearnCount;
        Emit(new BacklashPlayModelLearnedEvent(now, playBacklash.State(now), playBacklash.Values, UsesPlayModel(settings)));
    }
}
