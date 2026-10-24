// SPDX-License-Identifier: MPL-2.0

namespace NINA.GuideEngine.Core;

/// <summary>Time source for the engine so tests and the simulator can run in virtual time.</summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }

    /// <summary>Monotonic milliseconds since an arbitrary epoch.</summary>
    long ElapsedMs { get; }

    Task Delay(TimeSpan delay, CancellationToken ct);
}

public sealed class SystemClock : IClock
{
    private readonly System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();

    public static SystemClock Instance { get; } = new();

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public long ElapsedMs => sw.ElapsedMilliseconds;

    public Task Delay(TimeSpan delay, CancellationToken ct) => delay <= TimeSpan.Zero ? Task.CompletedTask : Task.Delay(delay, ct);
}
