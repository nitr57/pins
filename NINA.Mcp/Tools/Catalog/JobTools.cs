// SPDX-License-Identifier: MPL-2.0

using NINA.Mcp.Jobs;
using NINA.Mcp.Observatory;

namespace NINA.Mcp.Tools.Catalog;

internal sealed class GetJobTool : ObservatoryTool {

    public GetJobTool(IPinsObservatory observatory) : base(observatory) {
    }

    public override string Name => "get_job";

    public override string Title => "Get background job";

    public override string Description =>
        "State of one background job started by a hardware tool (slew_mount, capture_image, run_autofocus, cool_camera, ...). "
        + "Returns { job: { id, kind, description, resource, state (running | succeeded | failed | cancelled), startedAt, "
        + "finishedAt, outcome: { success, message, data } } }. Set wait_seconds to long-poll: the call returns as soon as the "
        + "job finishes or the wait elapses. Finished jobs are kept for one hour.";

    public override ToolAnnotations Annotations => ToolAnnotations.Query;

    protected override void DefineArguments(ToolSchema arguments) {
        arguments.String("job_id", "Job id as returned by the tool that started it, e.g. \"job-7f3a2c\".", required: true, maxLength: 64);
        arguments.WaitSeconds(
            0,
            "Seconds to wait for the job to finish before returning (0-120, default 0 = return immediately).");
    }

    protected override async Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, ToolContext context, CancellationToken cancellationToken) {
        var id = arguments.GetString("job_id", required: true)!.Trim();
        var wait = JobPayload.ReadWait(arguments, 0);

        var job = await context.Jobs.WaitAsync(id, wait, cancellationToken).ConfigureAwait(false);

        if (job is null) {
            return ToolCallResult.Error($"Unknown job '{id}'. It may have expired (finished jobs are kept for one hour); see list_jobs.");
        }

        return JobPayload.Result(job);
    }
}

internal sealed class ListJobsTool : ObservatoryTool {

    public ListJobsTool(IPinsObservatory observatory) : base(observatory) {
    }

    public override string Name => "list_jobs";

    public override string Title => "List background jobs";

    public override string Description =>
        "All background jobs of this pins instance (from any MCP session): running jobs first, then finished jobs of the last "
        + "hour, newest first, in the same shape as get_job. Use it to find a job id after losing track of it, or to see "
        + "what currently holds the mount, camera, focuser or other device.";

    public override ToolAnnotations Annotations => ToolAnnotations.Query;

    protected override void DefineArguments(ToolSchema arguments) {
    }

    protected override Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, ToolContext context, CancellationToken cancellationToken) {
        var jobs = context.Jobs.List();

        var payload = new {
            running = jobs.Count(job => job.State == JobState.Running),
            jobs = jobs.Select(JobPayload.Describe).ToList()
        };

        return Task.FromResult(ToolCallResult.Json(payload));
    }
}

internal sealed class CancelJobTool : ObservatoryTool {

    public CancelJobTool(IPinsObservatory observatory) : base(observatory) {
    }

    public override string Name => "cancel_job";

    public override string Title => "Cancel background job";

    public override string Description =>
        "Requests cancellation of one running background job; the hardware operation is stopped where possible (exposure "
        + "aborted, slew stopped, focuser halted, autofocus aborted). Waits up to wait_seconds (default 10) for the job to end "
        + "and returns it in the get_job shape. Cancelling a job that already finished is not an error. Available even in "
        + "read-only mode and while a sequence runs. To stop everything at once use stop_all.";

    public override ToolAnnotations Annotations => ToolAnnotations.Stop;

    public override bool AllowedInReadOnlyMode => true;

    public override SequenceGuard Guard => SequenceGuard.None;

    protected override void DefineArguments(ToolSchema arguments) {
        arguments.String("job_id", "Id of the job to cancel, e.g. \"job-7f3a2c\".", required: true, maxLength: 64);
        arguments.WaitSeconds(
            10,
            "Seconds to wait for the job to end after the cancel request (0-120, default 10).");
    }

    protected override async Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, ToolContext context, CancellationToken cancellationToken) {
        var id = arguments.GetString("job_id", required: true)!.Trim();
        var wait = JobPayload.ReadWait(arguments, 10);

        if (context.Jobs.Get(id) is null) {
            return ToolCallResult.Error($"Unknown job '{id}'. See list_jobs.");
        }

        context.Jobs.Cancel(id);

        var job = await context.Jobs.WaitAsync(id, wait, cancellationToken).ConfigureAwait(false);

        if (job is null) {
            return ToolCallResult.Error($"Job '{id}' disappeared while cancelling. See list_jobs.");
        }

        return JobPayload.Result(job);
    }
}
