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

    private const string JobKind = "secure";

    private readonly object startGate = new();

    public SecureObservatoryTool(IPinsObservatory observatory) : base(observatory) {
    }

    public override string Name => "secure_observatory";

    public override string Title => "Secure the observatory";

    public override string Description =>
        "Shuts the observatory down for the end of the night or for bad weather (rain, clouds, unsafe). In this order: stops "
        + "the advanced sequence, cancels every background job and aborts the exposure (like stop_all), stops guiding, parks "
        + "the mount, closes the dome shutter and the flat panel's dust cover, and optionally warms the camera. Steps for "
        + "devices that are not connected are skipped. The dome is only closed when the mount is parked (closing a roof on an "
        + "unparked telescope can damage it); a disconnected mount counts as not parked, because its position is unknown, "
        + "unless close_dome_without_mount is set. The dome step only succeeds when the dome then reports its shutter closed. "
        + "Not refused while a sequence runs and not blocked by other jobs; until the camera "
        + "warming starts it holds all devices, so nothing can unpark or open in between. Runs as a background job (parking and closing take "
        + "minutes): poll get_job; outcome.data lists every step. While a shutdown runs, calling it again returns that job "
        + "(its arguments are ignored) instead of starting a second one. Moves the telescope and the dome.";

    public override ToolAnnotations Annotations => ToolAnnotations.Hardware;

    public override SequenceGuard Guard => SequenceGuard.None;

    protected override bool StartsJob => true;

    protected override void DefineArguments(ToolSchema arguments) {
        arguments.Boolean("park", "Park the mount. Default true.", defaultValue: true);
        arguments.Boolean("close_dome", "Close the dome shutter (after the mount parked). Default true.", defaultValue: true);
        arguments.Boolean(
            "close_dome_without_mount",
            "Close the dome even though no mount is connected (its position is unknown). Only for sites without a mount "
            + "under the dome, or when the user has checked that the telescope cannot be hit. Default false.",
            defaultValue: false);
        arguments.Boolean("close_cover", "Close the flat panel's dust cover. Default true.", defaultValue: true);
        arguments.Boolean("warm_camera", "Warm the camera over 10 minutes at the end. Default false.", defaultValue: false);
    }

    protected override async Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, ToolContext context, CancellationToken cancellationToken) {
        var park = arguments.GetBool("park") ?? true;
        var closeDome = arguments.GetBool("close_dome") ?? true;
        var closeDomeWithoutMount = arguments.GetBool("close_dome_without_mount") ?? false;
        var closeCover = arguments.GetBool("close_cover") ?? true;
        var warmCamera = arguments.GetBool("warm_camera") ?? false;

        var wait = JobPayload.ReadWait(arguments, DefaultWaitSeconds);
        JobInfo job;

        // Checked and started under one lock: a retry (or a second client) joins the running shutdown instead of
        // cancelling it, which would abort its park and run two shutdowns at once.
        lock (startGate) {
            var running = context.Jobs.List().FirstOrDefault(candidate => candidate.Kind == JobKind && candidate.State == JobState.Running);

            if (running is not null) {
                job = running;
            } else {
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

                job = context.Jobs.Start(
                    JobKind,
                    "Secure the observatory",
                    null,
                    token => ShutDownAsync(context.Jobs, steps, park, closeDome, closeDomeWithoutMount, closeCover, warmCamera, token));
            }
        }

        var current = await context.Jobs.WaitAsync(job.Id, wait, cancellationToken).ConfigureAwait(false) ?? job;

        return JobPayload.Result(current);
    }

    private async Task<JobOutcome> ShutDownAsync(
        IJobManager jobs,
        List<SecureStep> steps,
        bool park,
        bool closeDome,
        bool closeDomeWithoutMount,
        bool closeCover,
        bool warmCamera,
        CancellationToken cancellationToken) {
        // In the job, not the request: a client that gives up during the grace period must not stop the shutdown.
        await WaitForJobsToEndAsync(jobs, cancellationToken).ConfigureAwait(false);

        // Best effort: nothing may unpark, slew, open or start the sequence while the shutdown runs. A job that ignored
        // its cancellation keeps its device; the shutdown continues without the hold.
        IDisposable? hold = null;

        try {
            hold = jobs.Reserve(StartSequenceTool.HardwareResources, "secure_observatory");
        } catch (JobConflictException ex) {
            // A warning, not a failure: the shutdown itself can still succeed.
            steps.Add(new SecureStep("hold_devices", true, $"Warning: could not hold every device ({ex.Message}); continuing."));
        }

        var status = Observatory.GetStatus();

        using (hold) {
            // Released on the cancel request already: an abandoned shutdown (a hung park or close) must not keep every
            // device, which would refuse all MCP hardware jobs and start_sequence until pins restarts.
            using var releaseOnCancel = cancellationToken.Register(() => hold?.Dispose());

            await ShutDownDevicesAsync(status, steps, park, closeDome, closeDomeWithoutMount, closeCover, cancellationToken).ConfigureAwait(false);
        }

        // After the hold: warming takes ten minutes and only needs the cooler, while the user may have to park or close by hand.
        if (warmCamera && Connected(status, DeviceKind.Camera)) {
            await StepAsync(steps, "warm_camera", () => Observatory.WarmCameraAsync(TimeSpan.FromMinutes(10), cancellationToken)).ConfigureAwait(false);
        }

        var failed = steps.Where(step => !step.Success).Select(step => step.Step).ToList();

        return failed.Count == 0
            ? JobOutcome.Ok("Observatory secured.", steps)
            : JobOutcome.Fail($"Not every step succeeded ({string.Join(", ", failed)}); see outcome.data and check get_status.", steps);
    }

    private async Task ShutDownDevicesAsync(
        ObservatoryStatus status,
        List<SecureStep> steps,
        bool park,
        bool closeDome,
        bool closeDomeWithoutMount,
        bool closeCover,
        CancellationToken cancellationToken) {
        if (Connected(status, DeviceKind.Guider)) {
            await StepAsync(steps, "stop_guiding", () => Observatory.StopGuidingAsync(cancellationToken)).ConfigureAwait(false);
        }

        // A disconnected mount is not parked: its position cannot be determined (as in NINA's DomeVM.CloseShutter).
        var mountConnected = Connected(status, DeviceKind.Mount);
        var parked = mountConnected && Property(status, DeviceKind.Mount, "atPark") is true;

        if (park && mountConnected) {
            var result = await StepAsync(steps, "park_mount", () => Observatory.ParkAsync(cancellationToken)).ConfigureAwait(false);

            parked = result.Success;
        }

        if (closeDome && Connected(status, DeviceKind.Dome)) {
            if (!mountConnected && !closeDomeWithoutMount) {
                steps.Add(new SecureStep(
                    "close_dome",
                    false,
                    "Skipped: no mount is connected, so it is unknown whether the telescope is parked, and closing the dome on "
                    + "an unparked telescope can damage it. Reconnect and park the mount, or, once the user confirmed the "
                    + "telescope is clear, call secure_observatory again with close_dome_without_mount: true (or close the "
                    + "dome with control_dome)."));
            } else if (mountConnected && !parked) {
                steps.Add(new SecureStep(
                    "close_dome",
                    false,
                    "Skipped: the mount is not parked, and closing the dome on an unparked telescope can damage it. Check the "
                    + "mount, then close the dome with control_dome."));
            } else {
                await StepAsync(steps, "close_dome", () => CloseDomeAsync(cancellationToken)).ConfigureAwait(false);
            }
        }

        if (closeCover && Connected(status, DeviceKind.FlatDevice) && Property(status, DeviceKind.FlatDevice, "coverState") is not null) {
            await StepAsync(steps, "close_cover", () => Observatory.ControlFlatDeviceAsync(FlatDeviceAction.CloseCover, null, cancellationToken)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// control_dome reports a shutter that is still moving or has no state as it is; the shutdown only counts a dome that
    /// reports closed, so that "Observatory secured." is never said over an open or jammed roof.
    /// </summary>
    private async Task<OperationResult> CloseDomeAsync(CancellationToken cancellationToken) {
        var result = await Observatory.ControlDomeAsync(DomeAction.CloseShutter, null, cancellationToken).ConfigureAwait(false);

        if (!result.Success) {
            return result;
        }

        var shutter = Property(Observatory.GetStatus(), DeviceKind.Dome, "shutter") as string;

        return shutter == "ShutterClosed"
            ? result
            : OperationResult.Fail(
                $"The close command completed, but the dome reports {shutter ?? "no shutter state"} instead of ShutterClosed: it may "
                + "still be moving or be stuck. Check the dome (get_status, a camera or a person) before relying on it.");
    }

    private static async Task WaitForJobsToEndAsync(IJobManager jobs, CancellationToken cancellationToken) {
        var deadline = DateTime.UtcNow + CancelGrace;

        // The shutdown job itself is running too.
        while (jobs.List().Any(job => job.State == JobState.Running && job.Kind != JobKind) && DateTime.UtcNow < deadline) {
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
