// SPDX-License-Identifier: MPL-2.0

namespace NINA.Mcp.Jobs;

/// <summary>
/// Runs long operations (slews, autofocus, exposures, cooling) in the background so that they outlive the HTTP request
/// that started them. A tool starts a job, waits a bounded time for it, and returns either the finished outcome or the
/// job id the model polls with <c>get_job</c>.
/// </summary>
/// <remarks>
/// A job may claim a <c>resource</c> ("mount", "camera", "focuser", ...) and require further resources to be idle.
/// Starting a job whose resource or required-idle resources are held by a running job or a <see cref="Reserve"/>
/// reservation throws <see cref="JobConflictException"/>; the check and the claim are one atomic step. Finished jobs are
/// kept for inspection for a limited time. Jobs are cancelled through their own token (never the request's), by
/// <see cref="Cancel"/> or <see cref="CancelAll"/>; a job whose work ignores the token can be <see cref="Abandon"/>ed.
/// </remarks>
public interface IJobManager {

    /// <summary>Starts <paramref name="work"/> on the thread pool and returns immediately.</summary>
    /// <exception cref="JobConflictException">
    /// <paramref name="resource"/> or one of <paramref name="requiresIdle"/> is held by a running job or a reservation.
    /// </exception>
    JobInfo Start(
        string kind,
        string description,
        string? resource,
        Func<CancellationToken, Task<JobOutcome>> work,
        IReadOnlyCollection<string>? requiresIdle = null);

    /// <summary>
    /// Starts <paramref name="work"/> and waits up to <paramref name="wait"/> for it to finish. Cancelling
    /// <paramref name="requestCancellation"/> stops the wait, not the job. Returns the job's state at that point.
    /// </summary>
    /// <exception cref="JobConflictException">
    /// <paramref name="resource"/> or one of <paramref name="requiresIdle"/> is held by a running job or a reservation.
    /// </exception>
    Task<JobInfo> StartAndWaitAsync(
        string kind,
        string description,
        string? resource,
        Func<CancellationToken, Task<JobOutcome>> work,
        TimeSpan wait,
        CancellationToken requestCancellation,
        IReadOnlyCollection<string>? requiresIdle = null);

    /// <summary>Waits up to <paramref name="wait"/> for an existing job to finish. Null when the id is unknown.</summary>
    Task<JobInfo?> WaitAsync(string id, TimeSpan wait, CancellationToken requestCancellation);

    JobInfo? Get(string id);

    /// <summary>Running jobs first, then finished ones, newest first.</summary>
    IReadOnlyList<JobInfo> List();

    /// <summary>
    /// Requests cancellation. <see cref="JobCancelResult.Requested"/> is 0 when the id is unknown or the job already
    /// finished; <see cref="JobCancelResult.Errors"/> lists failing stop callbacks (driver halts).
    /// </summary>
    JobCancelResult Cancel(string id);

    /// <summary>Requests cancellation of every running job.</summary>
    JobCancelResult CancelAll();

    /// <summary>
    /// Requests cancellation and, if the job is still running, ends it as <see cref="JobState.Abandoned"/>: its resource
    /// is released although its work has not returned. The work's eventual result is logged and dropped. Null when the
    /// id is unknown; a job that already ended is returned unchanged.
    /// </summary>
    JobInfo? Abandon(string id);

    /// <summary>
    /// Claims <paramref name="resources"/> for a short operation that is not a job (starting the sequence, switching
    /// tracking) until the returned handle is disposed. Jobs claiming or requiring one of them conflict meanwhile.
    /// </summary>
    /// <exception cref="JobConflictException">A running job or another reservation holds one of the resources.</exception>
    IDisposable Reserve(IReadOnlyCollection<string> resources, string description);

    /// <summary>
    /// Refuses new jobs and reservations, cancels every running job and waits up to <paramref name="wait"/> for them to
    /// end. Running jobs of a kind in <paramref name="letFinish"/> are not cancelled (cancelling a park stops the mount
    /// short of its park position); while one runs the wait is up to <paramref name="letFinishWait"/>. Returns how many
    /// are still running.
    /// </summary>
    int Shutdown(TimeSpan wait, IReadOnlyCollection<string>? letFinish = null, TimeSpan letFinishWait = default);
}

public enum JobState {
    Running,
    Succeeded,
    Failed,
    Cancelled,

    /// <summary>Released after a cancel request although its work had not returned (see <see cref="IJobManager.Abandon"/>).</summary>
    Abandoned
}

/// <summary>Result of a cancel request: how many running jobs it reached and the stop callbacks that failed.</summary>
public sealed record JobCancelResult(int Requested, IReadOnlyList<string> Errors);

/// <summary>What a job's work returns. <see cref="Data"/> is serialized with <c>McpJson.Options</c>.</summary>
public sealed record JobOutcome(bool Success, string? Message = null, object? Data = null) {

    public static JobOutcome Ok(string? message = null, object? data = null) {
        return new JobOutcome(true, message, data);
    }

    public static JobOutcome Fail(string message, object? data = null) {
        return new JobOutcome(false, message, data);
    }
}

/// <summary>Snapshot of a job. <see cref="Outcome"/> is set once the job is no longer running.</summary>
public sealed record JobInfo(
    string Id,
    string Kind,
    string Description,
    string? Resource,
    JobState State,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    JobOutcome? Outcome) {

    /// <summary>True once cancellation was requested, also while the job is still running.</summary>
    public bool CancelRequested { get; init; }
}

public sealed class JobConflictException : Exception {

    public JobConflictException(string resource, JobInfo runningJob)
        : base($"The {resource} is busy with job {runningJob.Id} ({runningJob.Description}). Wait for it, or cancel it with cancel_job.") {
        Resource = resource;
        RunningJob = runningJob;
    }

    public JobConflictException(string resource, string operation)
        : base($"The {resource} is in use by a short operation ({operation}). Retry in a moment.") {
        Resource = resource;
    }

    public string Resource { get; }

    /// <summary>The job holding the resource; null when a reservation holds it.</summary>
    public JobInfo? RunningJob { get; }
}
