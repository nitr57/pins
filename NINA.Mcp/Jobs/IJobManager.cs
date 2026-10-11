// SPDX-License-Identifier: MPL-2.0

namespace NINA.Mcp.Jobs;

/// <summary>
/// Runs long operations (slews, autofocus, exposures, cooling) in the background so that they outlive the HTTP request
/// that started them. A tool starts a job, waits a bounded time for it, and returns either the finished outcome or the
/// job id the model polls with <c>get_job</c>.
/// </summary>
/// <remarks>
/// A job may claim a <c>resource</c> ("mount", "camera", "focuser", ...). Starting a job whose resource is held by a
/// running job throws <see cref="JobConflictException"/>. Finished jobs are kept for inspection for a limited time.
/// Jobs are cancelled through their own token (never the request's), by <see cref="Cancel"/> or <see cref="CancelAll"/>.
/// </remarks>
public interface IJobManager {

    /// <summary>Starts <paramref name="work"/> on the thread pool and returns immediately.</summary>
    /// <exception cref="JobConflictException">Another running job holds <paramref name="resource"/>.</exception>
    JobInfo Start(string kind, string description, string? resource, Func<CancellationToken, Task<JobOutcome>> work);

    /// <summary>
    /// Starts <paramref name="work"/> and waits up to <paramref name="wait"/> for it to finish. Cancelling
    /// <paramref name="requestCancellation"/> stops the wait, not the job. Returns the job's state at that point.
    /// </summary>
    /// <exception cref="JobConflictException">Another running job holds <paramref name="resource"/>.</exception>
    Task<JobInfo> StartAndWaitAsync(
        string kind,
        string description,
        string? resource,
        Func<CancellationToken, Task<JobOutcome>> work,
        TimeSpan wait,
        CancellationToken requestCancellation);

    /// <summary>Waits up to <paramref name="wait"/> for an existing job to finish. Null when the id is unknown.</summary>
    Task<JobInfo?> WaitAsync(string id, TimeSpan wait, CancellationToken requestCancellation);

    JobInfo? Get(string id);

    /// <summary>Running jobs first, then finished ones, newest first.</summary>
    IReadOnlyList<JobInfo> List();

    /// <summary>Requests cancellation. False when the id is unknown or the job already finished.</summary>
    bool Cancel(string id);

    /// <summary>Requests cancellation of every running job. Returns how many were running.</summary>
    int CancelAll();
}

public enum JobState {
    Running,
    Succeeded,
    Failed,
    Cancelled
}

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
    JobOutcome? Outcome);

public sealed class JobConflictException : Exception {

    public JobConflictException(string resource, JobInfo runningJob)
        : base($"The {resource} is busy with job {runningJob.Id} ({runningJob.Description}). Wait for it, or cancel it with cancel_job.") {
        Resource = resource;
        RunningJob = runningJob;
    }

    public string Resource { get; }

    public JobInfo RunningJob { get; }
}
