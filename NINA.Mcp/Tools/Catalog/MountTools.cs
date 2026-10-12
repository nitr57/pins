// SPDX-License-Identifier: MPL-2.0

using NINA.Mcp.Jobs;
using NINA.Mcp.Observatory;

namespace NINA.Mcp.Tools.Catalog;

internal sealed class SlewMountTool : ObservatoryTool {

    public const string RightAscensionDescription =
        "J2000 right ascension in HOURS (0 <= ra < 24): a number like 5.5883 or a sexagesimal string like \"05h35m17.3s\", "
        + "\"5:35:17.3\" or \"5 35 17.3\". Degrees are not accepted: divide degrees by 15.";

    public const string DeclinationDescription =
        "J2000 declination in DEGREES (-90..90): a number like -5.391 or a sexagesimal string like \"-05°23'28\\\"\", "
        + "\"-5:23:28\", \"-5d23m28s\" or \"-5 23 28\". The sign applies to the whole value (\"-00:30:00\" is -0.5°).";

    public SlewMountTool(IPinsObservatory observatory) : base(observatory) {
    }

    public override string Name => "slew_mount";

    public override string Title => "Slew mount";

    public override string Description =>
        "Slews the mount to J2000 coordinates (plain goto: no plate solving or centering; add_target with slew_and_center "
        + "does that in a sequence). The mount must be connected and unparked (unpark_mount). Moves the telescope: make sure "
        + "the user expects it. If guiding is active, stop_guiding first (the guide star is lost during the slew). Runs as a "
        + "background job holding the mount; refused while a capture, autofocus or guiding start job runs. Stop it with "
        + "cancel_job or stop_all. Afterwards check the position with get_status.";

    public override ToolAnnotations Annotations => ToolAnnotations.Hardware;

    protected override bool StartsJob => true;

    protected override void DefineArguments(ToolSchema arguments) {
        arguments.RightAscension("ra", RightAscensionDescription, required: true);
        arguments.Declination("dec", DeclinationDescription, required: true);
    }

    protected override Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, ToolContext context, CancellationToken cancellationToken) {
        var ra = CoordinateParser.ReadRightAscension(arguments, "ra")!.Value;
        var dec = CoordinateParser.ReadDeclination(arguments, "dec")!.Value;

        var description = $"Slew to RA {CoordinateParser.FormatRightAscension(ra)} Dec {CoordinateParser.FormatDeclination(dec)}";

        var target = new {
            raHours = ra,
            decDegrees = dec
        };

        return RunJobAsync(
            arguments,
            context,
            new JobRequest("slew", description, "mount") {
                RequiresIdle = MountMotionTool.SlewRequiresIdle
            },
            async token => JobPayload.ToOutcome(await Observatory.SlewAsync(ra, dec, token).ConfigureAwait(false), target),
            cancellationToken);
    }
}

/// <summary>park_mount, unpark_mount and home_mount: argument-less mount jobs.</summary>
internal sealed class MountMotionTool : ObservatoryTool {

    /// <summary>No mount motion while guiding calibrates; no slew or home under a running exposure or autofocus.</summary>
    internal static readonly string[] SlewRequiresIdle = ["guider", "camera"];

    /// <summary>Parking stays possible during an exposure (weather): it only waits for a guiding start.</summary>
    internal static readonly string[] ParkRequiresIdle = ["guider"];

    private readonly string name;
    private readonly string title;
    private readonly string description;
    private readonly string jobDescription;
    private readonly IReadOnlyList<string> requiresIdle;
    private readonly Func<IPinsObservatory, CancellationToken, Task<OperationResult>> action;

    private MountMotionTool(
        IPinsObservatory observatory,
        string name,
        string title,
        string description,
        string jobDescription,
        IReadOnlyList<string> requiresIdle,
        Func<IPinsObservatory, CancellationToken, Task<OperationResult>> action) : base(observatory) {
        this.name = name;
        this.title = title;
        this.description = description;
        this.jobDescription = jobDescription;
        this.requiresIdle = requiresIdle;
        this.action = action;
    }

    public override string Name => name;

    public override string Title => title;

    public override string Description => description;

    public override ToolAnnotations Annotations => ToolAnnotations.Hardware;

    protected override bool StartsJob => true;

    public static MountMotionTool Park(IPinsObservatory observatory) {
        return new MountMotionTool(
            observatory,
            "park_mount",
            "Park mount",
            "Moves the mount to its park position and stops tracking (end of session, bad weather). Moves the telescope; "
            + "guiding is stopped first. Runs as a background job holding the mount; refused while a guiding start job runs. "
            + "Use unpark_mount before the next slew.",
            "Park mount",
            ParkRequiresIdle,
            (target, token) => target.ParkAsync(token));
    }

    public static MountMotionTool Unpark(IPinsObservatory observatory) {
        return new MountMotionTool(
            observatory,
            "unpark_mount",
            "Unpark mount",
            "Unparks the mount so that it can slew and track. Some mounts move slightly. Runs as a background job holding "
            + "the mount. Typical next steps: set_tracking, slew_mount.",
            "Unpark mount",
            ParkRequiresIdle,
            (target, token) => target.UnparkAsync(token));
    }

    public static MountMotionTool Home(IPinsObservatory observatory) {
        return new MountMotionTool(
            observatory,
            "home_mount",
            "Find mount home",
            "Moves the mount to its home position (driver 'find home'; not every mount supports it). Moves the telescope; "
            + "guiding is stopped first. Runs as a background job holding the mount; refused while a capture, autofocus or "
            + "guiding start job runs.",
            "Find mount home",
            SlewRequiresIdle,
            (target, token) => target.FindHomeAsync(token));
    }

    protected override void DefineArguments(ToolSchema arguments) {
    }

    protected override Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, ToolContext context, CancellationToken cancellationToken) {
        var kind = name.Replace("_mount", string.Empty, StringComparison.Ordinal);

        return RunJobAsync(
            arguments,
            context,
            new JobRequest(kind, jobDescription, "mount") {
                RequiresIdle = requiresIdle
            },
            async token => JobPayload.ToOutcome(await action(Observatory, token).ConfigureAwait(false)),
            cancellationToken);
    }
}

internal sealed class SetTrackingTool : ObservatoryTool {

    public SetTrackingTool(IPinsObservatory observatory) : base(observatory) {
    }

    public override string Name => "set_tracking";

    public override string Title => "Set mount tracking";

    public override string Description =>
        "Turns sidereal tracking of the mount on or off and returns once the mount reports the new state (a few seconds at "
        + "most). Tracking must be on for imaging; turn it off to keep a parked or idle mount from drifting into the pier. "
        + "Refused while a slew, park, unpark or home job holds the mount.";

    public override ToolAnnotations Annotations => ToolAnnotations.Hardware;

    protected override void DefineArguments(ToolSchema arguments) {
        arguments.Boolean("enabled", "True to start tracking, false to stop it.", required: true);
    }

    protected override async Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, ToolContext context, CancellationToken cancellationToken) {
        var enabled = arguments.GetBool("enabled", required: true)!.Value;

        // Not a job, but it must not switch tracking under a running mount job (or let one start meanwhile).
        using var reservation = context.Jobs.Reserve(["mount"], enabled ? "set_tracking on" : "set_tracking off");

        return FromOperation(await Observatory.SetTrackingAsync(enabled, cancellationToken).ConfigureAwait(false));
    }
}
