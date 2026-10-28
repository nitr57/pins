// SPDX-License-Identifier: MPL-2.0

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Security.Cryptography;

namespace NINA.Mcp.Jobs;

/// <summary>
/// In-memory <see cref="IJobManager"/>. Jobs run on the thread pool with their own cancellation token. Finished jobs are
/// kept for <see cref="FinishedRetention"/>, at most <see cref="MaxFinishedJobs"/> of them. Disposing it cancels every
/// running job and refuses new ones.
/// </summary>
public sealed class JobManager : IJobManager, IDisposable {

    public static readonly TimeSpan FinishedRetention = TimeSpan.FromHours(1);

    public const int MaxFinishedJobs = 50;

    private static readonly TimeSpan MaxWait = TimeSpan.FromDays(1);

    private readonly TimeProvider timeProvider;
    private readonly ILogger logger;
    private readonly object gate = new();
    private readonly Dictionary<string, JobEntry> jobs = new(StringComparer.Ordinal);
    private readonly List<Reservation> reservations = [];
    private bool shutDown;

    public JobManager(TimeProvider timeProvider, ILogger<JobManager>? logger = null) {
        this.timeProvider = timeProvider;
        this.logger = logger ?? NullLogger<JobManager>.Instance;
    }

    public JobManager() : this(TimeProvider.System) {
    }

    public JobInfo Start(
        string kind,
        string description,
        string? resource,
        Func<CancellationToken, Task<JobOutcome>> work,
        IReadOnlyCollection<string>? requiresIdle = null) {
        ArgumentNullException.ThrowIfNull(work);

        var entry = Register(kind, description, resource, requiresIdle);

        _ = Task.Run(() => RunAsync(entry, work));

        return entry.Snapshot();
    }

    public async Task<JobInfo> StartAndWaitAsync(
        string kind,
        string description,
        string? resource,
        Func<CancellationToken, Task<JobOutcome>> work,
        TimeSpan wait,
        CancellationToken requestCancellation,
        IReadOnlyCollection<string>? requiresIdle = null) {
        ArgumentNullException.ThrowIfNull(work);

        var entry = Register(kind, description, resource, requiresIdle);

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

    public JobCancelResult Cancel(string id) {
        JobEntry? entry;

        lock (gate) {
            if (!jobs.TryGetValue(id, out entry) || entry.State != JobState.Running) {
                return new JobCancelResult(0, []);
            }
        }

        // Outside the lock: cancellation callbacks run synchronously and may call back into the manager.
        var error = RequestCancel(entry);

        return new JobCancelResult(1, error is null ? Array.Empty<string>() : [error]);
    }

    public JobCancelResult CancelAll() {
        List<JobEntry> running;

        lock (gate) {
            running = jobs.Values.Where(entry => entry.State == JobState.Running).ToList();
        }

        var errors = new List<string>();

        foreach (var entry in running) {
            var error = RequestCancel(entry);

            if (error is not null) {
                errors.Add(error);
            }
        }

        return new JobCancelResult(running.Count, errors);
    }

    public JobInfo? Abandon(string id) {
        JobEntry? entry;

        lock (gate) {
            if (!jobs.TryGetValue(id, out entry)) {
                return null;
            }
        }

        if (entry.State == JobState.Running) {
            RequestCancel(entry);

            var outcome = JobOutcome.Fail(
                "Abandoned: the operation did not stop after the cancel request. Its resource was released, but the device "
                + "may still be busy; check get_status before using it.");

            if (entry.Finish(JobState.Abandoned, outcome, timeProvider.GetUtcNow())) {
                logger.LogWarning("MCP job {Id} ({Description}) abandoned; its work is still running", entry.Id, entry.Description);
            }
        }

        return entry.Snapshot();
    }

    public IDisposable Reserve(IReadOnlyCollection<string> resources, string description) {
        ArgumentNullException.ThrowIfNull(resources);

        lock (gate) {
            ThrowIfShutDown();
            Prune();

            foreach (var resource in resources) {
                ThrowIfHeld(resource);
            }

            var reservation = new Reservation(this, resources.ToList(), description);
            reservations.Add(reservation);

            return reservation;
        }
    }

    public int Shutdown(TimeSpan wait, IReadOnlyCollection<string>? letFinish = null, TimeSpan letFinishWait = default) {
        List<JobEntry> running;

        lock (gate) {
            shutDown = true;
            running = jobs.Values.Where(entry => entry.State == JobState.Running).ToList();
        }

        var finishing = running.Where(entry => letFinish?.Contains(entry.Kind) == true).ToList();

        // Outside the lock: cancellation callbacks run synchronously and may call back into the manager.
        foreach (var entry in running.Except(finishing)) {
            RequestCancel(entry);
        }

        foreach (var entry in finishing) {
            logger.LogInformation("Letting MCP job {Id} ({Description}) finish before shutdown", entry.Id, entry.Description);
        }

        var maxWait = finishing.Count > 0 && letFinishWait > wait ? letFinishWait : wait;

        if (running.Count > 0 && maxWait > TimeSpan.Zero) {
            try {
                Task.WhenAll(running.Select(entry => entry.Completion)).Wait(maxWait);
            } catch (AggregateException) {
                // Completions never fault; nothing to report.
            }
        }

        lock (gate) {
            return jobs.Values.Count(entry => entry.State == JobState.Running);
        }
    }

    public void Dispose() {
        Shutdown(TimeSpan.Zero);
    }

    private string? RequestCancel(JobEntry entry) {
        var error = entry.RequestCancel();

        if (error is null) {
            return null;
        }

        logger.LogError(error, "Stopping MCP job {Id} ({Description}) failed", entry.Id, entry.Description);

        var messages = error is AggregateException aggregate
            ? aggregate.InnerExceptions.Select(inner => inner.Message)
            : [error.Message];

        return $"{entry.Id} ({entry.Description}): {string.Join("; ", messages)}";
    }

    private JobEntry Register(string kind, string description, string? resource, IReadOnlyCollection<string>? requiresIdle) {
        lock (gate) {
            ThrowIfShutDown();
            Prune();

            if (!string.IsNullOrEmpty(resource)) {
                ThrowIfHeld(resource);
            }

            // Under the same lock as the claim: two jobs that each require the other's resource idle cannot both start.
            foreach (var idle in requiresIdle ?? []) {
                ThrowIfHeld(idle);
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

    /// <summary>Throws when a running job or a reservation holds <paramref name="resource"/>. Caller holds the lock.</summary>
    private void ThrowIfHeld(string resource) {
        var holder = jobs.Values.FirstOrDefault(entry =>
            entry.State == JobState.Running
            && string.Equals(entry.Resource, resource, StringComparison.OrdinalIgnoreCase));

        if (holder is not null) {
            throw new JobConflictException(resource, holder.Snapshot());
        }

        var reservation = reservations.FirstOrDefault(candidate => candidate.Resources.Contains(resource, StringComparer.OrdinalIgnoreCase));

        if (reservation is not null) {
            throw new JobConflictException(resource, reservation.Description);
        }
    }

    private void ThrowIfShutDown() {
        if (shutDown) {
            throw new InvalidOperationException("pins is shutting down; no new MCP jobs are started.");
        }
    }

    private void Release(Reservation reservation) {
        lock (gate) {
            reservations.Remove(reservation);
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

        if (!entry.Finish(state, outcome, timeProvider.GetUtcNow())) {
            logger.LogInformation(
                "Abandoned MCP job {Id} ({Description}) ended late: {State}, {Message}",
                entry.Id,
                entry.Description,
                state,
                outcome.Message);
        }

        entry.DisposeCancellation();
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

    private sealed class Reservation : IDisposable {

        private readonly JobManager owner;
        private int disposed;

        public Reservation(JobManager owner, IReadOnlyList<string> resources, string description) {
            this.owner = owner;
            Resources = resources;
            Description = description;
        }

        public IReadOnlyList<string> Resources { get; }

        public string Description { get; }

        public void Dispose() {
            if (Interlocked.Exchange(ref disposed, 1) == 0) {
                owner.Release(this);
            }
        }
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

        /// <summary>Ends the job once. False when it already ended (an abandoned job's late result is dropped).</summary>
        public bool Finish(JobState state, JobOutcome outcome, DateTimeOffset finishedAt) {
            lock (gate) {
                if (State != JobState.Running) {
                    return false;
                }

                State = state;
                Outcome = outcome;
                FinishedAt = finishedAt;
            }

            completion.TrySetResult();

            return true;
        }

        /// <summary>Called once the work has returned, so that no callback can be registered on the token any more.</summary>
        public void DisposeCancellation() {
            cancellation.Dispose();
        }

        public JobInfo Snapshot() {
            lock (gate) {
                return new JobInfo(Id, Kind, Description, Resource, State, StartedAt, FinishedAt, Outcome) {
                    CancelRequested = cancellation.IsCancellationRequested
                };
            }
        }
    }
}
