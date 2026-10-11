// SPDX-License-Identifier: MPL-2.0

using NINA.Mcp.Jobs;
using NINA.Mcp.Observatory;
using System.Globalization;
using System.Text.Json.Nodes;

namespace NINA.Mcp.Tools;

/// <summary>How a tool behaves while the advanced sequence is running.</summary>
internal enum SequenceGuard {

    /// <summary>Always allowed (queries, emergency stops, harmless file writes).</summary>
    None,

    /// <summary>Refused unless the caller passes <c>force: true</c>; the schema gets a <c>force</c> property.</summary>
    Forceable,

    /// <summary>Always refused (operations that would edit or replace the running sequence itself).</summary>
    Strict
}

/// <summary>What a tool runs as a background job.</summary>
/// <param name="Kind">Short job kind ("slew", "capture", ...).</param>
/// <param name="Description">Human readable description shown in job listings.</param>
/// <param name="Resource">The resource the job claims atomically (null: none).</param>
internal sealed record JobRequest(string Kind, string Description, string? Resource) {

    /// <summary>
    /// Further resources that must not be held by a running job when this one starts (checked before starting, not
    /// atomically). Used where one operation physically depends on several devices, e.g. no focuser move during an exposure.
    /// </summary>
    public IReadOnlyList<string> RequiresIdle { get; init; } = [];
}

/// <summary>
/// Base class of every catalog tool: declares arguments once (<see cref="DefineArguments"/>), validates every call
/// against them, applies the sequence-running safety guard (<see cref="Guard"/>, with <c>force</c>), and runs long
/// operations as jobs with the shared <c>wait_seconds</c> handling and payload shape.
/// </summary>
internal abstract class ObservatoryTool : McpTool {

    public const string ForceArgument = "force";

    public const string WaitArgument = "wait_seconds";

    private readonly Lazy<ToolSchema> schema;
    private readonly Lazy<JsonObject> inputSchema;

    protected ObservatoryTool(IPinsObservatory observatory) {
        Observatory = observatory;
        schema = new Lazy<ToolSchema>(BuildSchema);
        inputSchema = new Lazy<JsonObject>(() => schema.Value.ToJson());
    }

    public sealed override JsonObject InputSchema => inputSchema.Value;

    /// <summary>The declared arguments, including the automatically added <c>wait_seconds</c> and <c>force</c>.</summary>
    public ToolSchema Schema => schema.Value;

    /// <summary>Default: read-only tools are never guarded, all others need <c>force</c> while the sequence runs.</summary>
    public virtual SequenceGuard Guard => Annotations.ReadOnly ? SequenceGuard.None : SequenceGuard.Forceable;

    /// <summary>True for tools that run a background job; adds the <c>wait_seconds</c> argument.</summary>
    protected virtual bool StartsJob => false;

    protected virtual double DefaultWaitSeconds => JobPayload.DefaultStartWaitSeconds;

    protected IPinsObservatory Observatory { get; }

    public sealed override async Task<ToolCallResult> InvokeAsync(
        ToolArguments arguments,
        ToolContext context,
        CancellationToken cancellationToken) {
        Schema.Validate(arguments);

        var refusal = CheckSequenceGuard(arguments);

        if (refusal is not null) {
            return refusal;
        }

        return await ExecuteAsync(arguments, context, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Declares the tool's own arguments. <c>wait_seconds</c> and <c>force</c> are added by the base class.</summary>
    protected abstract void DefineArguments(ToolSchema arguments);

    /// <summary>Runs the tool. Arguments are already validated against the schema and the guard has passed.</summary>
    protected abstract Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, ToolContext context, CancellationToken cancellationToken);

    /// <summary>
    /// Starts <paramref name="work"/> as a job and waits up to <c>wait_seconds</c> for it. Returns the shared job payload.
    /// </summary>
    /// <exception cref="JobConflictException">The resource (or one of <see cref="JobRequest.RequiresIdle"/>) is busy.</exception>
    protected async Task<ToolCallResult> RunJobAsync(
        ToolArguments arguments,
        ToolContext context,
        JobRequest request,
        Func<CancellationToken, Task<JobOutcome>> work,
        CancellationToken cancellationToken) {
        var wait = JobPayload.ReadWait(arguments, DefaultWaitSeconds);

        EnsureIdle(context.Jobs, request.RequiresIdle);

        var job = await context.Jobs.StartAndWaitAsync(
            request.Kind,
            request.Description,
            request.Resource,
            work,
            wait,
            cancellationToken).ConfigureAwait(false);

        return JobPayload.Result(job);
    }

    /// <summary>Success with <c>{ success: true, message, ... }</c>, or an error result with the message.</summary>
    protected static ToolCallResult FromOperation(OperationResult result) {
        if (!result.Success) {
            return ToolCallResult.Error(result.Message);
        }

        return ToolCallResult.Json(new {
            success = true,
            message = result.Message
        });
    }

    private static void EnsureIdle(IJobManager jobs, IReadOnlyList<string> resources) {
        if (resources.Count == 0) {
            return;
        }

        foreach (var job in jobs.List()) {
            if (job.State != JobState.Running || job.Resource is null) {
                continue;
            }

            var busy = resources.FirstOrDefault(resource => string.Equals(resource, job.Resource, StringComparison.OrdinalIgnoreCase));

            if (busy is not null) {
                throw new JobConflictException(busy, job);
            }
        }
    }

    private ToolCallResult? CheckSequenceGuard(ToolArguments arguments) {
        if (Guard == SequenceGuard.None) {
            return null;
        }

        if (Guard == SequenceGuard.Forceable && arguments.GetBool(ForceArgument) == true) {
            return null;
        }

        if (!Observatory.GetStatus().SequenceRunning) {
            return null;
        }

        if (Guard == SequenceGuard.Strict) {
            return ToolCallResult.Error(
                $"Refused: {Name} is not possible while the advanced sequence is running. "
                + "Stop the sequence first with stop_sequence (or stop_all in an emergency), then retry.");
        }

        return ToolCallResult.Error(
            $"Refused: the advanced sequence is running and {Name} could interfere with it. "
            + "Check get_status / get_sequence, stop the sequence with stop_sequence, or retry with force: true only if the user "
            + "explicitly wants this while the sequence runs.");
    }

    private ToolSchema BuildSchema() {
        var built = new ToolSchema();

        DefineArguments(built);

        if (StartsJob) {
            var description = string.Create(
                CultureInfo.InvariantCulture,
                $"Seconds to wait for the operation to finish before returning (0-{JobPayload.MaxWaitSeconds:0}, default {DefaultWaitSeconds:0}). ");

            built.WaitSeconds(
                DefaultWaitSeconds,
                description
                + "If it is still running, the result holds the job id: poll get_job. The operation continues in the background either way.");
        }

        if (Guard == SequenceGuard.Forceable) {
            built.Boolean(
                ForceArgument,
                "Run even though the advanced sequence is running. Only set this when the user explicitly asked for it; "
                + "without it the call is refused while a sequence runs.",
                defaultValue: false);
        }

        return built;
    }
}
