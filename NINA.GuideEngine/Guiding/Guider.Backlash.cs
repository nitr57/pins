// SPDX-License-Identifier: MPL-2.0

using NINA.GuideEngine.Algorithms;

namespace NINA.GuideEngine.Guiding;

// The Dec backlash pulse the adaptive compensation learned (PHD2 keeps it in its profile). It survives a rebuild of the
// corrector (any settings change) and, through the host, the session, as long as the configured pulse is the one it was
// learned from. The adaptive range stays that of the configured pulse, so a learned pulse never widens it.
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
        corrector.Backlash.SetPulseWidth(BacklashPulse(s), s.FloorMs, BacklashCeiling(s));
        BacklashPulseInUse();
    });

    private BacklashCompensation CreateBacklash(BacklashSettings s, PulseLimiter limiter)
    {
        var backlash = new BacklashCompensation(BacklashPulse(s), s.FloorMs, BacklashCeiling(s), s.Enabled, limiter);
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

    // publishes the pulse in use; returns it when it changed since the last call
    private int? BacklashPulseInUse()
    {
        var b = corrector.Backlash;
        backlashPulseMs = b.IsEnabled ? b.PulseWidthMs : 0;
        if (b.PulseWidthMs == backlashPulseSeen)
        {
            return null;
        }

        backlashPulseSeen = b.PulseWidthMs;
        return b.PulseWidthMs;
    }
}
