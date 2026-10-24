// SPDX-License-Identifier: MPL-2.0

using System.Security.Cryptography;

namespace NINA.Mcp.Observatory;

public enum EventSeverity {
    Info,
    Warning,
    Error,
    Critical
}

/// <summary>
/// Something that happened in pins: a device dropped, the safety monitor turned unsafe, an image could not be saved,
/// the guider raised an alert. <see cref="Kind"/> groups them (safety, device, sequence, image, guider, mount, camera).
/// <see cref="Data"/> is a JSON friendly property bag or null.
/// </summary>
public sealed record ObservatoryEvent(
    long Id,
    DateTimeOffset Time,
    string Kind,
    EventSeverity Severity,
    string Message,
    IReadOnlyDictionary<string, object?>? Data = null);

/// <summary>
/// One read of the event log. <see cref="NextSinceId"/> is the cursor for the next call; <see cref="More"/> is true
/// when more matching events are waiting; <see cref="Missed"/> is true when events after the cursor were already dropped
/// from the buffer (the client polled too rarely; less severe events are dropped first). <see cref="RunId"/> identifies
/// the pins run the ids belong to.
/// </summary>
public sealed record EventPage(IReadOnlyList<ObservatoryEvent> Events, long LatestId, long NextSinceId, bool More, bool Missed, string? RunId = null);

/// <summary>
/// The last <see cref="Capacity"/> events, so that an agent sees what happened between two polls (or while it was away)
/// instead of only the current state. Publishers are the adapter's event bridge (mediator events) and anything else in
/// pins; readers are the get_events tool, optionally long-polling. Thread-safe.
/// </summary>
/// <remarks>
/// When full, the oldest event of the lowest severity present is dropped: a flood of image or incident events pushes out
/// older info events first, never a critical UNSAFE while less severe ones remain.
/// </remarks>
public sealed class ObservatoryEventLog {

    public const int Capacity = 2000;

    private readonly object gate = new();
    private readonly List<ObservatoryEvent> events = [];
    private readonly TimeProvider timeProvider;

    private readonly long firstId;

    private long lastId;
    private long lastDroppedId;
    private TaskCompletionSource published = NewSignal();

    public ObservatoryEventLog() : this(TimeProvider.System) {
    }

    public ObservatoryEventLog(TimeProvider timeProvider) {
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        firstId = InitialId(timeProvider.GetUtcNow());
        lastId = firstId;
    }

    /// <summary>
    /// Ids of a run start above every id of an earlier run (four per second since 2025, below int.MaxValue until about
    /// 2042), so that a client's cursor from before a pins restart is always recognized: it is older than the oldest
    /// event and reported as <see cref="EventPage.Missed"/> instead of hiding the new run's first events.
    /// </summary>
    internal static long InitialId(DateTimeOffset now) {
        var seconds = (now - new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero)).Ticks / TimeSpan.TicksPerSecond;

        return Math.Max(0, seconds) * 4;
    }

    /// <summary>
    /// Random per run. Ids alone come from the wall clock, which can go back across a restart (a Pi without network time
    /// or RTC battery); a cursor passed with another run's id is reported as missed and reads from this run's start.
    /// </summary>
    public string RunId { get; } = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));

    public long LatestId {
        get {
            lock (gate) {
                return lastId;
            }
        }
    }

    public ObservatoryEvent Publish(string kind, EventSeverity severity, string message, IReadOnlyDictionary<string, object?>? data = null) {
        ArgumentException.ThrowIfNullOrEmpty(kind);
        ArgumentNullException.ThrowIfNull(message);

        ObservatoryEvent entry;
        TaskCompletionSource signal;

        lock (gate) {
            lastId++;
            entry = new ObservatoryEvent(lastId, timeProvider.GetLocalNow(), kind, severity, message, data);
            events.Add(entry);

            while (events.Count > Capacity) {
                DropOne();
            }

            signal = published;
            published = NewSignal();
        }

        // Outside the lock: waiting readers continue on their own threads.
        signal.TrySetResult();

        return entry;
    }

    /// <summary>
    /// Events after <paramref name="sinceId"/> that match <paramref name="filter"/>, oldest first, at most
    /// <paramref name="max"/>. A cursor beyond the newest id (the clock went back across a restart) or from another run
    /// (<paramref name="runId"/> given and not <see cref="RunId"/>) reads from the beginning and reports
    /// <see cref="EventPage.Missed"/>, like a cursor older than the oldest event.
    /// </summary>
    public EventPage Read(long sinceId, int max, Func<ObservatoryEvent, bool>? filter = null, string? runId = null) {
        lock (gate) {
            var reset = sinceId > lastId || (runId is not null && !string.Equals(runId, RunId, StringComparison.OrdinalIgnoreCase));

            if (reset) {
                sinceId = 0;
            }

            var matching = events
                .Where(entry => entry.Id > sinceId && (filter?.Invoke(entry) ?? true))
                .ToList();

            var page = matching.Take(Math.Max(1, max)).ToList();
            var more = matching.Count > page.Count;
            // Missed when an event after the cursor was dropped (since_id 0 means "from the start of this run"), or when the
            // cursor is from an earlier run.
            var missed = reset
                || (sinceId > 0 && sinceId < firstId)
                || lastDroppedId > Math.Max(sinceId, firstId);

            // Without more matching events the cursor jumps to the newest event, past events the filter left out.
            var next = more ? page[^1].Id : Math.Max(sinceId, lastId);

            return new EventPage(page, lastId, next, more, missed, RunId);
        }
    }

    /// <summary>The newest <paramref name="max"/> matching events, oldest first (for a first look without a cursor).</summary>
    public EventPage Recent(int max, Func<ObservatoryEvent, bool>? filter = null) {
        lock (gate) {
            var matching = events.Where(entry => filter?.Invoke(entry) ?? true).ToList();
            var page = matching.Skip(Math.Max(0, matching.Count - Math.Max(1, max))).ToList();

            return new EventPage(page, lastId, lastId, false, false, RunId);
        }
    }

    /// <summary>
    /// Like <see cref="Read"/>, but when nothing matches yet waits up to <paramref name="wait"/> for a matching event
    /// (long poll). Cancelling <paramref name="cancellationToken"/> ends the wait with what is there.
    /// </summary>
    public async Task<EventPage> WaitAsync(
        long sinceId,
        int max,
        Func<ObservatoryEvent, bool>? filter,
        TimeSpan wait,
        CancellationToken cancellationToken,
        string? runId = null) {
        var deadline = timeProvider.GetUtcNow() + wait;
        var cursor = sinceId;
        var cursorRun = runId;
        var missed = false;

        while (true) {
            EventPage page;
            Task signal;

            lock (gate) {
                page = Read(cursor, max, filter, cursorRun);
                signal = published.Task;
            }

            missed |= page.Missed;

            var remaining = deadline - timeProvider.GetUtcNow();

            if (page.Events.Count > 0 || remaining <= TimeSpan.Zero) {
                return page with { Missed = missed };
            }

            // Events the filter left out are skipped, so that the next round only wakes for new ones.
            cursor = page.NextSinceId;
            cursorRun = null;

            try {
                await signal.WaitAsync(remaining, timeProvider, cancellationToken).ConfigureAwait(false);
            } catch (TimeoutException) {
                return Read(cursor, max, filter) with { Missed = missed };
            } catch (OperationCanceledException) {
                return Read(cursor, max, filter) with { Missed = missed };
            }
        }
    }

    /// <summary>Drops the oldest event of the lowest severity present. Caller holds the lock.</summary>
    private void DropOne() {
        var lowest = events.Min(entry => entry.Severity);
        var index = events.FindIndex(entry => entry.Severity == lowest);

        lastDroppedId = Math.Max(lastDroppedId, events[index].Id);
        events.RemoveAt(index);
    }

    private static TaskCompletionSource NewSignal() {
        return new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
