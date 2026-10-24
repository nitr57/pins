// SPDX-License-Identifier: MPL-2.0

using NINA.Mcp.Jobs;
using NINA.Mcp.Observatory;
using System.Globalization;

namespace NINA.Mcp.Tools;

/// <summary>
/// The one shape every job-related tool returns: <c>{ job: { id, kind, description, resource, state, startedAt,
/// finishedAt, outcome }, next }</c>. <c>outcome</c> (success, message, data) is present once the job finished;
/// <c>next</c> tells the model what to do.
/// </summary>
public static class JobPayload {

    /// <summary>
    /// Upper bound of every <c>wait_seconds</c> argument. Stays below the 60 s request timeout many MCP clients use
    /// (e.g. the TypeScript SDK default), so a waiting call returns its job id before the client gives up.
    /// </summary>
    public const double MaxWaitSeconds = 55;

    /// <summary>Default <c>wait_seconds</c> of tools that start a job.</summary>
    public const double DefaultStartWaitSeconds = 30;

    public static object Describe(JobInfo job) {
        return new {
            id = job.Id,
            kind = job.Kind,
            description = job.Description,
            resource = job.Resource,
            state = job.State,
            cancelRequested = job.CancelRequested,
            startedAt = job.StartedAt,
            finishedAt = job.FinishedAt,
            outcome = job.Outcome is null
                ? null
                : new {
                    success = job.Outcome.Success,
                    message = job.Outcome.Message,
                    data = job.Outcome.Data
                }
        };
    }

    /// <summary>The tool result for one job. Always a success result: the job's own failure is in <c>state</c>/<c>outcome</c>.</summary>
    public static ToolCallResult Result(JobInfo job) {
        return ToolCallResult.Json(new {
            job = Describe(job),
            next = NextStep(job)
        });
    }

    /// <summary><see cref="Result(JobInfo)"/> of a cancelled job, plus the stop commands that failed (if any).</summary>
    public static ToolCallResult Result(JobInfo job, IReadOnlyList<string> stopErrors) {
        if (stopErrors.Count == 0) {
            return Result(job);
        }

        return ToolCallResult.Json(new {
            job = Describe(job),
            stopErrors,
            next = "Stopping the device reported an error (see stopErrors): it may still be moving. Check get_status; "
                + "use stop_all or the device's own controls if it is."
        });
    }

    /// <summary>Reads <c>wait_seconds</c> (absent → <paramref name="defaultSeconds"/>), validated to 0..<see cref="MaxWaitSeconds"/>.</summary>
    public static TimeSpan ReadWait(ToolArguments arguments, double defaultSeconds) {
        var seconds = arguments.GetDouble("wait_seconds") ?? defaultSeconds;

        if (seconds < 0 || seconds > MaxWaitSeconds) {
            throw new ToolArgumentException(
                string.Create(CultureInfo.InvariantCulture, $"Argument 'wait_seconds' must be between 0 and {MaxWaitSeconds:0} (got {seconds})."));
        }

        return TimeSpan.FromSeconds(seconds);
    }

    /// <summary>Wraps an operation result as a job outcome.</summary>
    public static JobOutcome ToOutcome(OperationResult result, object? data = null) {
        return result.Success
            ? JobOutcome.Ok(result.Message, data)
            : JobOutcome.Fail(result.Message, data);
    }

    private static string NextStep(JobInfo job) {
        return job.State switch {
            JobState.Running when job.CancelRequested =>
                $"Cancellation was requested but the operation has not stopped yet. Poll get_job with id '{job.Id}'; if it "
                + $"does not end, call cancel_job with abandon: true to release the {job.Resource ?? "job"} (the device may still be busy).",
            JobState.Running => $"Still running. Poll get_job with id '{job.Id}' (use wait_seconds to long-poll) or stop it with cancel_job.",
            JobState.Succeeded => "Finished successfully.",
            JobState.Cancelled => "The job was cancelled.",
            JobState.Abandoned => "The job was abandoned and its resource released, but the device may still be busy. Check get_status before using it.",
            _ => "The job failed; see outcome.message. Check get_status before retrying."
        };
    }
}
