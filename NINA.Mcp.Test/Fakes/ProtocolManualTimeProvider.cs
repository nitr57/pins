// SPDX-License-Identifier: MPL-2.0

namespace NINA.Mcp.Test.Fakes;

/// <summary>A <see cref="TimeProvider"/> whose clock only moves with <see cref="Advance"/>; timers fire when passed.</summary>
internal sealed class ProtocolManualTimeProvider : TimeProvider {

    private readonly object gate = new();
    private readonly List<ManualTimer> timers = [];
    private DateTimeOffset now;

    public ProtocolManualTimeProvider() : this(new DateTimeOffset(2026, 10, 11, 20, 0, 0, TimeSpan.Zero)) {
    }

    public ProtocolManualTimeProvider(DateTimeOffset start) {
        now = start;
    }

    public override DateTimeOffset GetUtcNow() {
        lock (gate) {
            return now;
        }
    }

    public void Advance(TimeSpan delta) {
        List<ManualTimer> due;

        lock (gate) {
            now += delta;
            due = timers.Where(timer => timer.DueAt <= now).ToList();
        }

        foreach (var timer in due) {
            timer.Fire();
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) {
        var timer = new ManualTimer(this, callback, state);

        lock (gate) {
            timers.Add(timer);
        }

        timer.Change(dueTime, period);

        return timer;
    }

    private void Remove(ManualTimer timer) {
        lock (gate) {
            timers.Remove(timer);
        }
    }

    private sealed class ManualTimer : ITimer {

        private readonly ProtocolManualTimeProvider owner;
        private readonly TimerCallback callback;
        private readonly object? state;
        private TimeSpan period;

        public ManualTimer(ProtocolManualTimeProvider owner, TimerCallback callback, object? state) {
            this.owner = owner;
            this.callback = callback;
            this.state = state;
        }

        public DateTimeOffset DueAt { get; private set; } = DateTimeOffset.MaxValue;

        public bool Change(TimeSpan dueTime, TimeSpan period) {
            this.period = period;

            DueAt = dueTime == Timeout.InfiniteTimeSpan
                ? DateTimeOffset.MaxValue
                : owner.GetUtcNow() + dueTime;

            return true;
        }

        public void Fire() {
            if (period > TimeSpan.Zero && period != Timeout.InfiniteTimeSpan) {
                DueAt += period;
            } else {
                DueAt = DateTimeOffset.MaxValue;
            }

            callback(state);
        }

        public void Dispose() {
            owner.Remove(this);
        }

        public ValueTask DisposeAsync() {
            Dispose();

            return ValueTask.CompletedTask;
        }
    }
}
