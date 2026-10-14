// SPDX-License-Identifier: MPL-2.0

using NINA.Mcp.Jobs;
using NINA.Mcp.Observatory;

namespace NINA.Mcp.Tools.Catalog;

internal sealed class PlateSolveTool : ObservatoryTool {

    /// <summary>The solve exposure may change the filter (profile plate solve filter); it must not run under a mount move.</summary>
    internal static readonly string[] SolveRequiresIdle = ["filter_wheel", "focuser", "mount", "rotator", "flat_device"];

    public PlateSolveTool(IPinsObservatory observatory) : base(observatory) {
    }

    public override string Name => "plate_solve";

    public override string Title => "Plate solve";

    public override string Description =>
        "Takes one exposure at the current position with the profile's plate solve settings (exposure, filter, binning, "
        + "gain, solver) and plate solves it. outcome.data holds the solved J2000 RA in hours and Dec in degrees, the position "
        + "angle (degrees), pixel scale (arcsec/px), whether the image is flipped, and the distance from the position the mount "
        + "reports (errorArcmin, raErrorArcsec, decErrorArcsec). Without a connected mount the solver searches blind. With "
        + "sync: true the mount (and a connected rotator) is synced to the solution, which corrects its pointing. Does not "
        + "move the mount; use center_target to slew onto coordinates. Runs as a background job holding the camera; refused "
        + "while a mount, focuser, filter wheel, rotator or flat device job runs. Solving can take a minute: poll get_job.";

    public override ToolAnnotations Annotations => ToolAnnotations.Hardware;

    protected override bool StartsJob => true;

    protected override void DefineArguments(ToolSchema arguments) {
        arguments.Boolean(
            "sync",
            "Sync the mount (and a connected rotator) to the solved position. Default false: only report it.",
            defaultValue: false);
    }

    protected override Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, ToolContext context, CancellationToken cancellationToken) {
        var sync = arguments.GetBool("sync") ?? false;

        var request = new JobRequest("plate_solve", sync ? "Plate solve and sync" : "Plate solve", "camera") {
            RequiresIdle = SolveRequiresIdle
        };

        return RunJobAsync(
            arguments,
            context,
            request,
            async token => ToOutcome(await Observatory.PlateSolveAsync(sync, token).ConfigureAwait(false)),
            cancellationToken);
    }

    internal static JobOutcome ToOutcome(PlateSolveOutcome outcome) {
        return outcome.Success
            ? JobOutcome.Ok(outcome.Message, outcome.Solution)
            : JobOutcome.Fail(outcome.Message, outcome.Solution);
    }
}

internal sealed class CenterTargetTool : ObservatoryTool {

    /// <summary>Centering exposes, changes the filter and stops guiding; it needs all of them free.</summary>
    internal static readonly string[] CenterRequiresIdle = ["camera", "guider", "filter_wheel", "focuser", "rotator", "flat_device"];

    /// <summary>
    /// Held besides the mount for the whole job (a job claims one resource): no capture, filter change, focuser,
    /// rotator or flat device move and no guiding start can sneak in between the centering exposures.
    /// </summary>
    internal static readonly string[] CenterReserves = ["camera", "guider"];

    /// <summary>Reserved besides <see cref="CenterReserves"/>, but kept after a cancel request (see <see cref="FilterRestoreGrace"/>).</summary>
    internal static readonly string[] CenterReservesFilter = ["filter_wheel"];

    /// <summary>
    /// After a cancel, NINA's centering solver still restores the original filter on a fresh five-minute token: the filter
    /// wheel stays reserved until the work returns, but at most this long, so that an abandoned job cannot keep it forever.
    /// </summary>
    internal static readonly TimeSpan FilterRestoreGrace = TimeSpan.FromMinutes(6);

    public CenterTargetTool(IPinsObservatory observatory) : base(observatory) {
    }

    public override string Name => "center_target";

    public override string Title => "Slew and center target";

    public override string Description =>
        "Slews to J2000 coordinates and centers them with plate solving, like NINA's Center instruction: expose, solve, "
        + "sync the mount (or apply the measured offset when syncing is disabled in the profile) and re-slew until the target "
        + "is within the profile's centering threshold (at most 10 rounds). Uses the profile's plate solve settings. Guiding "
        + "is stopped first and restarted afterwards when it was running (not when the slew to the target fails). The mount must be connected and unparked; the "
        + "camera connected. Moves the telescope: make sure the user expects it. outcome.data holds the last solution and its "
        + "remaining error from the target. Runs as a background job holding the mount, and also the camera, guider and filter "
        + "wheel until it ends (after a cancel the filter wheel until NINA has restored the filter); refused while a capture, plate solve, autofocus, filter change, focuser, rotator, flat device or "
        + "guiding start job runs. Takes one to several minutes: poll get_job. Stop it with cancel_job or stop_all.";

    public override ToolAnnotations Annotations => ToolAnnotations.Hardware;

    protected override bool StartsJob => true;

    protected override void DefineArguments(ToolSchema arguments) {
        arguments.RightAscension("ra", SlewMountTool.RightAscensionDescription, required: true);
        arguments.Declination("dec", SlewMountTool.DeclinationDescription, required: true);
    }

    protected override Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, ToolContext context, CancellationToken cancellationToken) {
        var ra = CoordinateParser.ReadRightAscension(arguments, "ra")!.Value;
        var dec = CoordinateParser.ReadDeclination(arguments, "dec")!.Value;

        var description = $"Center RA {CoordinateParser.FormatRightAscension(ra)} Dec {CoordinateParser.FormatDeclination(dec)}";

        var request = new JobRequest("center", description, "mount") {
            RequiresIdle = CenterRequiresIdle
        };

        return RunJobAsync(
            arguments,
            context,
            request,
            async token => {
                // Checked idle atomically with the claim above; a job that grabbed one of them in between fails this one.
                using var reservation = context.Jobs.Reserve(CenterReserves, description);
                using var filterReservation = context.Jobs.Reserve(CenterReservesFilter, description);

                // Released on the cancel request already: an abandoned job (hung driver) must not keep them forever.
                using var releaseOnCancel = token.Register(reservation.Dispose);

                // The filter wheel only once the solver restored the filter (the work returned), or after the grace.
                using var releaseFilterLater = token.Register(() => _ = Task.Delay(FilterRestoreGrace).ContinueWith(
                    _ => filterReservation.Dispose(),
                    TaskScheduler.Default));

                return PlateSolveTool.ToOutcome(await Observatory.CenterAsync(ra, dec, token).ConfigureAwait(false));
            },
            cancellationToken);
    }
}
