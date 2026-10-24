// SPDX-License-Identifier: MPL-2.0

namespace NINA.GuideEngine.Guiding;

/// <summary>
/// Engine faults: exceptions of optional components (flight recorder, Coach callbacks) that the guider swallows so the
/// guide loop carries on, reported to the host as <see cref="EngineFaultEvent"/>.
/// </summary>
public sealed partial class Guider
{
    private readonly EngineFaults faults = new();

    // reports a swallowed exception (rate-limited per source); callable from any thread, never throws
    private void ReportFault(string source, Exception ex)
    {
        try
        {
            if (faults.Report(clock.UtcNow, source, ex) is { } e)
            {
                PublishEvent(e);
            }
        }
        catch (Exception)
        {
            // reporting must not break the guide loop either, and there is nowhere left to report to
        }
    }
}
