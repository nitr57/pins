// SPDX-License-Identifier: MPL-2.0

using System.Security.Cryptography;

namespace NINA.Mcp.Jobs;

/// <summary>
/// In-memory <see cref="IJobManager"/>. Jobs run on the thread pool with their own cancellation token. Finished jobs are
/// kept for <see cref="FinishedRetention"/>, at most <see cref="MaxFinishedJobs"/> of them.
/// </summary>
public sealed class JobManager : IJobManager {

    public static readonly TimeSpan FinishedRetention = TimeSpan.FromHours(1);

    public const int MaxFinishedJobs = 50;

    private static readonly TimeSpan MaxWait = TimeSpan.FromDays(1);

    private readonly TimeProvider timeProvider;
    private readonly object gate = new();
    private readonly Dictionary<string, JobEntry> jobs = new(StringComparer.Ordinal);

    public JobManager(TimeProvider timeProvider) {
        this.timeProvider = timeProvider;
    }

    public JobManager() : this(TimeProvider.System) {
    }

    public JobInfo Start(string kind, string description, string? resource, Func<CancellationToken, Task<JobOutcome>> work) {
        ArgumentNullException.ThrowIfNull(work);

        var entry = Register(kind, description, resource);

        _ = Task.Run(() => RunAsync(entry, work));

        return entry.Snapshot();
    }

    public async Task<JobInfo> StartAndWaitAsync(
        string kind,
        string description,
        string? resource,
        Func<CancellationToken, Task<JobOutcome>> work,
        TimeSpan wait,
        CancellationToken requestCancellation) {
        ArgumentNullException.ThrowIfNull(work);

        var entry = Register(kind, description, resource);

        _ = Task.Run(() => RunAsync(entry, work));

        return await WaitForEntryAsync(entry, wait, requestCancellation).ConfigureAwait(false);
    }

    public async Task<JobInfo?> WaitAsync(string id, TimeSpan wait, CancellationToken requestCancellation) {
        JobEntry? entry;

        lock (gate) {
            Prune();
            jobs.TryGetValue(id, out entry);
        }

        if (entry is null) {
            return null;
        }

        return await WaitForEntryAsync(entry, wait, requestCancellation).ConfigureAwait(false);
    }

    public JobInfo? Get(string id) {
        lock (gate) {
            Prune();

            return jobs.TryGetValue(id, out var entry) ? entry.Snapshot() : null;
        }
    }

    public IReadOnlyList<JobInfo> List() {
        List<JobInfo> snapshots;

        lock (gate) {
            Prune();
            snapshots = jobs.Values.Select(entry => entry.Snapshot()).ToList();
        }

        var running = snapshots
            .Where(job => job.State == JobState.Running)
            .OrderByDescending(job => job.StartedAt);

        var finished = snapshots
            .Where(job => job.State != JobState.Running)
            .OrderByDescending(job => job.FinishedAt);

        return running.Concat(finished).ToList();
    }

    public bool Cancel(string id) {
        JobEntry? entry;

        lock (gate) {
            if (!jobs.TryGetValue(id, out entry) || entry.State != JobState.Running) {
                return false;
            }
        }

        // Outside the lock: cancellation callbacks run synchronously and may call back into the manager.
        entry.RequestCancel();

        return true;
    }

    public int CancelAll() {
        List<JobEntry> running;

        lock (gate) {
            running = jobs.Values.Where(entry => entry.State == JobState.Running).ToList();
        }

        foreach (var entry in running) {
            entry.RequestCancel();
        }

        return running.Count;
    }

    private JobEntry Register(string kind, string description, string? resource) {
        lock (gate) {
            Prune();

            if (!string.IsNullOrEmpty(resource)) {
                var holder = jobs.Values.FirstOrDefault(entry =>
                    entry.State == JobState.Running
                    && string.Equals(entry.Resource, resource, StringComparison.OrdinalIgnoreCase));

                if (holder is not null) {
                    throw new JobConflictException(resource, holder.Snapshot());
                }
            }

            var id = NewId();

            while (jobs.ContainsKey(id)) {
                id = NewId();
            }

            var entry = new JobEntry(id, kind, description, resource, timeProvider.GetUtcNow(), gate);
            jobs.Add(id, entry);

            return entry;
        }
    }

    private async Task RunAsync(JobEntry entry, Func<CancellationToken, Task<JobOutcome>> work) {
        JobState state;
        JobOutcome outcome;

        try {
            var result = await work(entry.Token).ConfigureAwait(false);

            if (result is null) {
                state = JobState.Failed;
                outcome = JobOutcome.Fail("The job returned no outcome.");
            } else if (result.Success) {
                state = JobState.Succeeded;
                outcome = result;
            } else {
                // Work that reports failure after a cancel request was most likely stopped by it.
                state = entry.Token.IsCancellationRequested ? JobState.Cancelled : JobState.Failed;
                outcome = result;
            }
        } catch (OperationCanceledException) when (entry.Token.IsCancellationRequested) {
            state = JobState.Cancelled;
            outcome = JobOutcome.Fail("The job was cancelled.");
        } catch (Exception ex) {
            state = JobState.Failed;
            outcome = JobOutcome.Fail(ex.Message);
        }

        entry.Finish(state, outcome, timeProvider.GetUtcNow());
    }

    private async Task<JobInfo> WaitForEntryAsync(JobEntry entry, TimeSpan wait, CancellationToken requestCancellation) {
        if (wait <= TimeSpan.Zero || entry.Completion.IsCompleted) {
            return entry.Snapshot();
        }

        if (wait > MaxWait) {
            wait = MaxWait;
        }

        try {
            await entry.Completion.WaitAsync(wait, timeProvider, requestCancellation).ConfigureAwait(false);
        } catch (TimeoutException) {
            // Still running; the caller polls later.
        } catch (OperationCanceledException) when (requestCancellation.IsCancellationRequested) {
            // The request went away. The job keeps running.
        }

        return entry.Snapshot();
    }

    /// <summary>Drops finished jobs older than the retention and the oldest beyond the cap. Caller holds the lock.</summary>
    private void Prune() {
        var now = timeProvider.GetUtcNow();

        var finished = jobs.Values
            .Where(entry => entry.State != JobState.Running)
            .OrderByDescending(entry => entry.FinishedAt)
            .ToList();

        for (var i = 0; i < finished.Count; i++) {
            var entry = finished[i];
            var expired = now - entry.FinishedAt!.Value > FinishedRetention;

            if (expired || i >= MaxFinishedJobs) {
                jobs.Remove(entry.Id);
            }
        }
    }

    private static string NewId() {
        return "job-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(3));
    }

    private sealed class JobEntry {

        private readonly object gate;
        private readonly CancellationTokenSource cancellation = new();
        private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public JobEntry(string id, string kind, string description, string? resource, DateTimeOffset startedAt, object gate) {
            Id = id;
            Kind = kind;
            Description = description;
            Resource = resource;
            StartedAt = startedAt;
            this.gate = gate;
        }

        public string Id { get; }

        public string Kind { get; }

        public string Description { get; }

        public string? Resource { get; }

        public DateTimeOffset StartedAt { get; }

        /// <summary>Written under the manager's lock.</summary>
        public JobState State { get; private set; } = JobState.Running;

        public DateTimeOffset? FinishedAt { get; private set; }

        public JobOutcome? Outcome { get; private set; }

        public CancellationToken Token => cancellation.Token;

        public Task Completion => completion.Task;

        /// <summary>
        /// Cancels the job's token. Never throws: <see cref="CancellationTokenSource.Cancel()"/> runs every registered
        /// callback (driver halts such as stop slew or abort exposure) and only then throws an <see cref="AggregateException"/>
        /// if one of them failed. The token is cancelled either way, so a failing callback must not stop
        /// <see cref="CancelAll"/> from reaching the remaining jobs.
        /// </summary>
        public Exception? RequestCancel() {
            try {
                cancellation.Cancel();

                return null;
            } catch (AggregateException ex) {
                return ex;
            } catch (ObjectDisposedException) {
                return null;
            }
        }

        public void Finish(JobState state, JobOutcome outcome, DateTimeOffset finishedAt) {
            lock (gate) {
                State = state;
                Outcome = outcome;
                FinishedAt = finishedAt;
            }

            completion.TrySetResult();
        }

        public JobInfo Snapshot() {
            lock (gate) {
                return new JobInfo(Id, Kind, Description, Resource, State, StartedAt, FinishedAt, Outcome);
            }
        }
    }
}
