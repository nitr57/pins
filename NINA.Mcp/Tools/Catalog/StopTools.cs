// SPDX-License-Identifier: MPL-2.0

using NINA.Mcp.Observatory;

namespace NINA.Mcp.Tools.Catalog;

/// <summary>
/// Emergency stop. Never refused: listed and callable in read-only mode and while the sequence runs. Every step runs even
/// when an earlier one fails.
/// </summary>
internal sealed class StopAllTool : ObservatoryTool {

    public StopAllTool(IPinsObservatory observatory) : base(observatory) {
    }

    public override string Name => "stop_all";

    public override string Title => "Stop everything";

    public override string Description =>
        "Emergency stop. In this order: stops the advanced sequence (so it cannot start its next item), cancels every "
        + "background job, aborts the running camera exposure, stops the mount if it is slewing and halts a focuser move "
        + "started over MCP. Every step runs even if an earlier one fails; the result lists each step. It does NOT park the "
        + "mount, stop guiding, warm the camera or disconnect anything; an idle mount is left alone (note that aborting a "
        + "slew stops tracking on some mount drivers). Safe to repeat. Use it whenever the user says stop/abort or something "
        + "looks wrong (unexpected movement, collision risk, clouds/rain). This tool is never refused: together with "
        + "cancel_job it stays available in read-only mode, and it needs no force while a sequence runs. Afterwards check "
        + "get_status.";

    public override ToolAnnotations Annotations => ToolAnnotations.Stop;

    public override bool AllowedInReadOnlyMode => true;

    public override SequenceGuard Guard => SequenceGuard.None;

    protected override void DefineArguments(ToolSchema arguments) {
    }

    protected override Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, ToolContext context, CancellationToken cancellationToken) {
        var steps = new List<object>();

        Step(steps, "stop_sequence", () => {
            var result = Observatory.StopSequence();

            return (result.Success, result.Message);
        });

        Step(steps, "cancel_jobs", () => {
            var cancel = context.Jobs.CancelAll();

            if (cancel.Errors.Count > 0) {
                return (false, $"Cancellation requested for {cancel.Requested} running job(s); stopping failed for {string.Join(" | ", cancel.Errors)}.");
            }

            return (true, $"Cancellation requested for {cancel.Requested} running job(s).");
        });

        Step(steps, "abort_exposure", () => {
            Observatory.AbortExposure();

            return (true, "Exposure abort requested.");
        });

        Step(steps, "stop_slew", () => {
            Observatory.StopSlew();

            return (true, "Slew stop requested.");
        });

        Step(steps, "stop_focuser", () => {
            Observatory.StopFocuser();

            return (true, "Focuser halt requested.");
        });

        var payload = new {
            stopped = true,
            steps,
            next = "Check get_status to confirm that the mount, camera and focuser are idle."
        };

        return Task.FromResult(ToolCallResult.Json(payload));
    }

    private static void Step(List<object> steps, string name, Func<(bool Success, string Message)> action) {
        try {
            var (success, message) = action();

            steps.Add(new {
                step = name,
                success,
                message
            });
        } catch (Exception ex) {
            steps.Add(new {
                step = name,
                success = false,
                message = $"Failed: {ex.Message}"
            });
        }
    }
}
