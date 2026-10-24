// SPDX-License-Identifier: MPL-2.0

namespace NINA.GuideEngine.Guiding;

/// <summary>
/// Rate limit of <see cref="EngineFaultEvent"/>: one report per source and <see cref="Interval"/>, so that a fault that
/// repeats every frame doesn't flood the host's log. Thread-safe.
/// </summary>
internal sealed class EngineFaults
{
    /// <summary>Shortest time between two reports of the same source.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    private readonly Dictionary<string, (DateTimeOffset Reported, int Suppressed)> sources = [];
    private readonly object gate = new();

    /// <summary>The event reporting <paramref name="ex"/> in <paramref name="source"/>, null while that source waits out <see cref="Interval"/>.</summary>
    public EngineFaultEvent? Report(DateTimeOffset now, string source, Exception ex)
    {
        lock (gate)
        {
            int suppressed = 0;
            if (sources.TryGetValue(source, out var last))
            {
                if (now >= last.Reported && now - last.Reported < Interval)
                {
                    sources[source] = last with { Suppressed = last.Suppressed + 1 };
                    return null;
                }

                suppressed = last.Suppressed;
            }

            sources[source] = (now, 0);
            return new EngineFaultEvent(now, source, $"{ex.GetType().Name}: {ex.Message}", suppressed);
        }
    }
}
