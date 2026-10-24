// SPDX-License-Identifier: MPL-2.0

using NINA.GuideEngine.Core;

namespace NINA.GuideEngine.Simulation;

/// <summary>
/// Deterministic virtual time source for tests and the closed-loop simulator.
/// </summary>
/// <remarks>
/// <para>
/// Semantics:
/// </para>
/// <list type="bullet">
/// <item><description>
/// Time only moves when <see cref="Delay"/>, <see cref="Advance"/> or <see cref="AdvanceTo"/> are
/// called. Nothing ever waits in real time.
/// </description></item>
/// <item><description>
/// <see cref="Delay"/> atomically advances the clock by the requested amount and returns an
/// already-completed task. A caller that awaits it therefore continues synchronously on the same
/// thread, so a single async loop (expose → process → pulse → expose ...) driven by the simulator runs
/// deterministically: every run with the same seeds produces the same sequence of virtual timestamps.
/// </description></item>
/// <item><description>
/// Delays are serialised, not overlapped: two concurrent <see cref="Delay"/> calls of 100 ms each
/// advance the clock by 200 ms in total. Consequently "simultaneous" RA and Dec pulses issued with
/// <c>Task.WhenAll</c> are simulated back-to-back in virtual time (the resulting displacement is the
/// same, only the elapsed time differs). Use <see cref="SystemClock"/> if true overlap matters.
/// </description></item>
/// <item><description>
/// A delay whose token is already cancelled does not advance time and returns a cancelled task.
/// Zero or negative delays are no-ops.
/// </description></item>
/// <item><description>
/// Because awaiting never yields, a loop that never stops would block its thread forever. Stop such a
/// loop through a token from <see cref="CancelAt"/>, which is cancelled as soon as virtual time
/// reaches the deadline; the loop's next <see cref="Delay"/> then throws
/// <see cref="OperationCanceledException"/>.
/// </description></item>
/// </list>
/// All members are thread-safe.
/// </remarks>
public sealed class VirtualClock : IClock
{
    /// <summary>Default wall-clock time at virtual time zero.</summary>
    public static readonly DateTimeOffset DefaultEpoch = new(2026, 1, 15, 20, 0, 0, TimeSpan.Zero);

    private readonly object gate = new();
    private readonly List<(long DeadlineTicks, CancellationTokenSource Cts)> deadlines = [];
    private long elapsedTicks;
    private long delayCount;

    public VirtualClock(DateTimeOffset? epoch = null)
    {
        Epoch = epoch ?? DefaultEpoch;
    }

    /// <summary>Wall-clock time corresponding to virtual time zero.</summary>
    public DateTimeOffset Epoch { get; }

    /// <summary>Virtual time elapsed since construction.</summary>
    public TimeSpan Elapsed => TimeSpan.FromTicks(Interlocked.Read(ref elapsedTicks));

    /// <summary>Virtual time elapsed since construction in seconds (full tick resolution).</summary>
    public double ElapsedSeconds => Interlocked.Read(ref elapsedTicks) / (double)TimeSpan.TicksPerSecond;

    public DateTimeOffset UtcNow => Epoch + Elapsed;

    public long ElapsedMs => Interlocked.Read(ref elapsedTicks) / TimeSpan.TicksPerMillisecond;

    /// <summary>Number of non-trivial <see cref="Delay"/> calls so far (diagnostics).</summary>
    public long DelayCount => Interlocked.Read(ref delayCount);

    /// <summary>Advances virtual time by <paramref name="delay"/> and returns a completed task (see remarks on the class).</summary>
    public Task Delay(TimeSpan delay, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return Task.FromCanceled(ct);
        if (delay <= TimeSpan.Zero) return Task.CompletedTask;
        Interlocked.Increment(ref delayCount);
        Advance(delay);
        return Task.CompletedTask;
    }

    /// <summary>Moves virtual time forward by <paramref name="delta"/> (ignored when not positive).</summary>
    public void Advance(TimeSpan delta)
    {
        if (delta <= TimeSpan.Zero) return;
        List<CancellationTokenSource>? due;
        lock (gate)
        {
            elapsedTicks += delta.Ticks;
            due = CollectDue();
        }

        CancelAll(due);
    }

    /// <summary>Moves virtual time forward to <paramref name="elapsed"/>; does nothing when already past it.</summary>
    public void AdvanceTo(TimeSpan elapsed)
    {
        List<CancellationTokenSource>? due;
        lock (gate)
        {
            if (elapsed.Ticks <= elapsedTicks) return;
            elapsedTicks = elapsed.Ticks;
            due = CollectDue();
        }

        CancelAll(due);
    }

    /// <summary>
    /// Returns a token that is cancelled once virtual time reaches <paramref name="elapsed"/> (virtual time
    /// since construction). Already cancelled when that time has passed.
    /// </summary>
    public CancellationToken CancelAt(TimeSpan elapsed)
    {
        var cts = new CancellationTokenSource();
        lock (gate)
        {
            if (elapsed.Ticks > elapsedTicks)
            {
                deadlines.Add((elapsed.Ticks, cts));
                return cts.Token;
            }
        }

        cts.Cancel();
        return cts.Token;
    }

    /// <summary>Returns a token cancelled after <paramref name="delta"/> of further virtual time.</summary>
    public CancellationToken CancelAfter(TimeSpan delta) => CancelAt(Elapsed + delta);

    private List<CancellationTokenSource>? CollectDue()
    {
        List<CancellationTokenSource>? due = null;
        for (int i = deadlines.Count - 1; i >= 0; i--)
        {
            if (deadlines[i].DeadlineTicks <= elapsedTicks)
            {
                (due ??= []).Add(deadlines[i].Cts);
                deadlines.RemoveAt(i);
            }
        }

        return due;
    }

    private static void CancelAll(List<CancellationTokenSource>? due)
    {
        if (due == null) return;
        foreach (var cts in due) cts.Cancel();
    }
}

/// <summary>Helpers to read an <see cref="IClock"/> with sub-millisecond precision where available.</summary>
public static class ClockExtensions
{
    /// <summary>Seconds since the clock's epoch; full resolution for <see cref="VirtualClock"/>.</summary>
    public static double NowSeconds(this IClock clock) =>
        clock is VirtualClock v ? v.ElapsedSeconds : clock.ElapsedMs / 1000.0;
}
