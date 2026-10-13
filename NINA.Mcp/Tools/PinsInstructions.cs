// SPDX-License-Identifier: MPL-2.0

namespace NINA.Mcp.Tools;

/// <summary>The compact guide returned as <c>instructions</c> from <c>initialize</c>.</summary>
internal static class PinsInstructions {

    private const string ReadOnlyNote =
        """

        READ-ONLY MODE: this server only exposes queries plus stop_all and cancel_job. Hardware and sequence editing tools are not available (the administrator enables them by setting PINS_MCP_TOKEN on the pins box). Do not plan steps that need them; describe what the user would have to do instead.
        """;

    /// <summary>The instructions, with a note on the restricted tool set when the server runs read-only.</summary>
    public static string Build(bool readOnly) {
        return readOnly ? Text + ReadOnlyNote : Text;
    }

    public const string Text =
        """
        pins (PI 'N' Stars) is a headless Linux build of N.I.N.A. that controls a real telescope: mount, camera, focuser, filter wheel, guider, rotator, dome, switches. Tools act on physical hardware outdoors; confirm with the user before moving the mount, switching power or starting a sequence, and never guess coordinates.

        Units and formats
        - Coordinates are J2000. RA in hours (0 <= ra < 24) as number or string: 05h35m17.3s, 5:35:17.3, 5 35 17.3. Dec in degrees (-90..90) as number or string: -05°23'28", -5:23:28, -5 23 28. RA in degrees must be divided by 15.
        - Temperatures °C, exposures seconds, focuser positions steps, guiding RMS arcsec, HFR pixels.

        Jobs
        Long operations (connect, slew, park, capture, cool, focus, filter change, guiding, switches, plate solving, centering, rotator, flat panel, dome) run as background jobs. The tool waits up to wait_seconds (default 30, max 55) and returns { job: { id, state, outcome } }. If state is "running", poll get_job (with wait_seconds to long-poll) or check get_status. A device busy with another job refuses new jobs; cancel_job stops one job.

        Safety guard
        While the advanced sequence runs, hardware and editing tools are refused so they cannot disturb it. Pass force: true only when the user explicitly wants that; load_sequence, clear_targets, reset_sequence and start_sequence need the sequence stopped (stop_sequence). stop_all is the emergency stop: it stops the sequence, cancels all jobs, aborts the exposure, stops slewing and the focuser. It is never refused, not even in read-only mode (cancel_job neither). Use it when the user says stop or something looks wrong.

        Typical imaging session
        1. get_status (what is connected, sequence state, running jobs).
        2. connect_device for camera, mount, focuser, filter_wheel, guider as needed.
        3. cool_camera (e.g. -10 °C, poll until reached); unpark_mount.
        4. Choose targets with plan_targets (transit, hours above the altitude limit in the dark, moon). Either center_target (or slew_mount) + capture_image to frame manually, or build the plan: clear_targets, add_target (name, ra, dec, exposures or a template from list_target_templates); or load_sequence from list_saved_sequences.
        5. run_autofocus and start_guiding (a target from add_target does centering, guiding and optional autofocus itself).
        6. start_sequence, then monitor with get_status, get_sequence and get_recent_images (HFR rising → focus; star count dropping → clouds).
        7. End: stop_sequence, park_mount, warm_camera, disconnect_device.

        Diagnosing problems
        - Guiding: get_guider_report (RMS, drift, polar alignment error, loop timing, alerts with fixes); list_guide_incidents and get_guide_incident (flight recorder: what happened frame by frame and the likely cause); get_guiding_coach_report.
        - Failures (sequence aborted, slew, solve or autofocus failed, device dropped): read_log, warnings and errors by default; widen the level or filter by text around the time of the failure.
        """;
}
