// SPDX-License-Identifier: MPL-2.0

using NINA.GuideEngine.Algorithms;

namespace NINA.GuideEngine.Guiding;

// The Dec backlash pulse the adaptive compensation learned (PHD2 keeps it in its profile). It survives a settings change
// (the compensation is updated in place, or rebuilt after the play model was in use) and, through the host, the
// session, as long as the configured pulse is the one it was learned from. The adaptive range stays that of the
// configured pulse, so a learned pulse never widens it.
public sealed partial class Guider
{
    private BacklashState? storedBacklash;

    // the pulse the compensation used after the last guide step (a change is a learned pulse)
    private int backlashPulseSeen;

    // the pulse in use, for the host's status: 0 while the compensation is off
    private volatile int backlashPulseMs;

    /// <summary>The Dec backlash compensation pulse in use (ms), null while the compensation is off.</summary>
    public int? DecBacklashPulseMs => backlashPulseMs > 0 ? backlashPulseMs : null;

    /// <summary>
    /// A pulse learned in an earlier session (stored by the host from <see cref="BacklashPulseLearnedEvent"/>): used while
    /// the configured pulse is the one it was learned from. Null forgets it.
    /// </summary>
    public void RestoreBacklash(BacklashState? state) => Post(() =>
    {
        storedBacklash = state;
        var s = settings.Backlash;
        if (corrector.Backlash is BacklashCompensation fixedPulse)
        {
            fixedPulse.SetPulseWidth(BacklashPulse(s), s.FloorMs, BacklashCeiling(s));
        }

        BacklashPulseInUse();
    });

    private BacklashCompensation CreateFixedBacklash(BacklashSettings s, PulseLimiter limiter)
    {
        var backlash = new BacklashCompensation(BacklashPulse(s), s.FloorMs, BacklashCeiling(s), s.Enabled, limiter);
        backlashPulseSeen = backlash.PulseWidthMs;
        backlashPulseMs = backlash.IsEnabled ? backlash.PulseWidthMs : 0;
        return backlash;
    }

    // the compensation in use takes the settings and keeps its last direction and history (SetPulseWidth leaves equal values
    // alone and clears the history only on a change of more than 100 ms, as in PHD2)
    private BacklashCompensation UpdateFixedBacklash(BacklashCompensation backlash, BacklashSettings s)
    {
        int pulse = BacklashPulse(s);
        backlash.SetPulseWidth(pulse, s.FloorMs, BacklashCeiling(s));

        // off at a 0 ms pulse, as the constructor does
        backlash.Enable(s.Enabled && backlash.PulseWidthMs > 0);

        // the pulse set from the settings is not one the compensation learned
        backlashPulseSeen = backlash.PulseWidthMs;
        backlashPulseMs = backlash.IsEnabled ? backlash.PulseWidthMs : 0;
        return backlash;
    }

    // the learned pulse while the setting is the one it was learned from (within the setting's range), else the setting
    private int BacklashPulse(BacklashSettings s)
    {
        if (storedBacklash is not { } learned || learned.SetPulseMs != s.PulseMs)
        {
            return s.PulseMs;
        }

        return Math.Clamp(learned.PulseMs, Math.Min(s.FloorMs, s.PulseMs), Math.Max(BacklashCeiling(s), s.PulseMs));
    }

    private static int BacklashCeiling(BacklashSettings s) => s.CeilingMs > 0 ? s.CeilingMs : BacklashCompensation.DefaultCeiling(s.PulseMs);

    // after a guide step: the adaptive compensation may have changed its pulse
    private void BacklashStepDone(DateTimeOffset now)
    {
        if (BacklashPulseInUse() is not { } pulse)
        {
            return;
        }

        storedBacklash = new BacklashState(settings.Backlash.PulseMs, pulse);
        Emit(new BacklashPulseLearnedEvent(now, storedBacklash));
    }

    // publishes the pulse in use; returns it when it changed since the last call. Only the fixed compensation learns a pulse:
    // the play model's pulse follows the gear from step to step and is kept by the play model itself
    private int? BacklashPulseInUse()
    {
        if (corrector.Backlash is not BacklashCompensation b)
        {
            backlashPulseMs = 0;
            return null;
        }

        backlashPulseMs = b.IsEnabled ? b.PulseWidthMs : 0;
        if (b.PulseWidthMs == backlashPulseSeen)
        {
            return null;
        }

        backlashPulseSeen = b.PulseWidthMs;
        return b.PulseWidthMs;
    }
}
