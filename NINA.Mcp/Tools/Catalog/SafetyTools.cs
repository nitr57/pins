// SPDX-License-Identifier: MPL-2.0

using NINA.Mcp.Jobs;
using NINA.Mcp.Observatory;

namespace NINA.Mcp.Tools.Catalog;

/// <summary>
/// End-of-night and bad-weather shutdown: stops everything like stop_all, then parks the mount and closes the dome and
/// the dust cover. Never refused by the sequence guard (it stops the sequence itself) and claims no resource, because
/// it must not be blocked by the jobs it replaces; it cancels them first.
/// </summary>
internal sealed class SecureObservatoryTool : ObservatoryTool {

    /// <summary>How long cancelled jobs get to end before the shutdown moves hardware.</summary>
    internal static readonly TimeSpan CancelGrace = TimeSpan.FromSeconds(10);

    public SecureObservatoryTool(IPinsObservatory observatory) : base(observatory) {
    }

    public override string Name => "secure_observatory";

    public override string Title => "Secure the observatory";

    public override string Description =>
        "Shuts the observatory down for the end of the night or for bad weather (rain, clouds, unsafe). In this order: stops "
        + "the advanced sequence, cancels every background job and aborts the exposure (like stop_all), stops guiding, parks "
        + "the mount, closes the dome shutter and the flat panel's dust cover, and optionally warms the camera. Steps for "
        + "devices that are not connected are skipped. The dome is only closed after the mount reports parked (closing a roof "
        + "on an unparked telescope can damage it), unless park is false. Not refused while a sequence runs and not blocked by "
        + "other jobs. Runs as a background job (parking and closing take minutes): poll get_job; outcome.data lists every "
        + "step. Moves the telescope and the dome.";

    public override ToolAnnotations Annotations => ToolAnnotations.Hardware;

    public override SequenceGuard Guard => SequenceGuard.None;

    protected override bool StartsJob => true;

    protected override void DefineArguments(ToolSchema arguments) {
        arguments.Boolean("park", "Park the mount. Default true.", defaultValue: true);
        arguments.Boolean("close_dome", "Close the dome shutter (after the mount parked). Default true.", defaultValue: true);
        arguments.Boolean("close_cover", "Close the flat panel's dust cover. Default true.", defaultValue: true);
        arguments.Boolean("warm_camera", "Warm the camera over 10 minutes at the end. Default false.", defaultValue: false);
    }

    protected override async Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, ToolContext context, CancellationToken cancellationToken) {
        var park = arguments.GetBool("park") ?? true;
        var closeDome = arguments.GetBool("close_dome") ?? true;
        var closeCover = arguments.GetBool("close_cover") ?? true;
        var warmCamera = arguments.GetBool("warm_camera") ?? false;

        var steps = new List<SecureStep>();

        // Before the job starts, so that it does not cancel itself.
        Step(steps, "stop_sequence", () => Observatory.StopSequence());
        Step(steps, "cancel_jobs", () => {
            var cancel = context.Jobs.CancelAll();

            return cancel.Errors.Count == 0
                ? OperationResult.Ok($"Cancellation requested for {cancel.Requested} running job(s).")
                : OperationResult.Fail($"Cancellation requested for {cancel.Requested} running job(s); stopping failed for {string.Join(" | ", cancel.Errors)}.");
        });
        Step(steps, "abort_exposure", () => {
            Observatory.AbortExposure();

            return OperationResult.Ok("Exposure abort requested.");
        });

        await WaitForJobsToEndAsync(context.Jobs, cancellationToken).ConfigureAwait(false);

        return await RunJobAsync(
            arguments,
            context,
            new JobRequest("secure", "Secure the observatory", null),
            token => ShutDownAsync(steps, park, closeDome, closeCover, warmCamera, token),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<JobOutcome> ShutDownAsync(
        List<SecureStep> steps,
        bool park,
        bool closeDome,
        bool closeCover,
        bool warmCamera,
        CancellationToken cancellationToken) {
        var status = Observatory.GetStatus();

        if (Connected(status, DeviceKind.Guider)) {
            await StepAsync(steps, "stop_guiding", () => Observatory.StopGuidingAsync(cancellationToken)).ConfigureAwait(false);
        }

        var mountConnected = Connected(status, DeviceKind.Mount);
        var parked = !mountConnected || Property(status, DeviceKind.Mount, "atPark") is true;

        if (park && mountConnected) {
            var result = await StepAsync(steps, "park_mount", () => Observatory.ParkAsync(cancellationToken)).ConfigureAwait(false);

            parked = result.Success;
        }

        if (closeDome && Connected(status, DeviceKind.Dome)) {
            if (park && !parked) {
                steps.Add(new SecureStep(
                    "close_dome",
                    false,
                    "Skipped: the mount did not park, and closing the dome on an unparked telescope can damage it. Check the "
                    + "mount, then close the dome with control_dome."));
            } else {
                await StepAsync(steps, "close_dome", () => Observatory.ControlDomeAsync(DomeAction.CloseShutter, null, cancellationToken)).ConfigureAwait(false);
            }
        }

        if (closeCover && Connected(status, DeviceKind.FlatDevice) && Property(status, DeviceKind.FlatDevice, "coverState") is not null) {
            await StepAsync(steps, "close_cover", () => Observatory.ControlFlatDeviceAsync(FlatDeviceAction.CloseCover, null, cancellationToken)).ConfigureAwait(false);
        }

        if (warmCamera && Connected(status, DeviceKind.Camera)) {
            await StepAsync(steps, "warm_camera", () => Observatory.WarmCameraAsync(TimeSpan.FromMinutes(10), cancellationToken)).ConfigureAwait(false);
        }

        var failed = steps.Where(step => !step.Success).Select(step => step.Step).ToList();

        return failed.Count == 0
            ? JobOutcome.Ok("Observatory secured.", steps)
            : JobOutcome.Fail($"Not every step succeeded ({string.Join(", ", failed)}); see outcome.data and check get_status.", steps);
    }

    private static async Task WaitForJobsToEndAsync(IJobManager jobs, CancellationToken cancellationToken) {
        var deadline = DateTime.UtcNow + CancelGrace;

        while (jobs.List().Any(job => job.State == JobState.Running) && DateTime.UtcNow < deadline) {
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool Connected(ObservatoryStatus status, DeviceKind kind) {
        return status.Devices.Any(device => device.Kind == kind && device.Connected);
    }

    private static object? Property(ObservatoryStatus status, DeviceKind kind, string name) {
        var device = status.Devices.FirstOrDefault(candidate => candidate.Kind == kind && candidate.Connected);

        return device is not null && device.Properties.TryGetValue(name, out var value) ? value : null;
    }

    private static void Step(List<SecureStep> steps, string name, Func<OperationResult> action) {
        try {
            var result = action();

            steps.Add(new SecureStep(name, result.Success, result.Message));
        } catch (Exception ex) {
            steps.Add(new SecureStep(name, false, $"Failed: {ex.Message}"));
        }
    }

    private static async Task<OperationResult> StepAsync(List<SecureStep> steps, string name, Func<Task<OperationResult>> action) {
        OperationResult result;

        try {
            result = await action().ConfigureAwait(false);
        } catch (OperationCanceledException) {
            throw;
        } catch (Exception ex) {
            result = OperationResult.Fail($"Failed: {ex.Message}");
        }

        steps.Add(new SecureStep(name, result.Success, result.Message));

        return result;
    }

    internal sealed record SecureStep(string Step, bool Success, string Message);
}
