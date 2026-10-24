// SPDX-License-Identifier: MPL-2.0

using NINA.Mcp.Observatory;

namespace NINA.Mcp.Tools.Catalog;

internal sealed class GetGuiderReportTool : ObservatoryTool {

    public GetGuiderReportTool(IPinsObservatory observatory) : base(observatory) {
    }

    public override string Name => "get_guider_report";

    public override string Title => "Get guider report";

    public override string Description =>
        "Detailed guiding diagnostics. With the pins internal guider (advanced: true): state, calibration, guide star "
        + "(SNR, HFD), statistics of the recent window and of the whole session (RMS RA/Dec/total in arcsec and px, peak, "
        + "RA/Dec drift in arcsec/min, polar alignment error estimate in arcmin, RA oscillation index, pulse duty %, star "
        + "losses), loop timing (frame rate and where each cycle's milliseconds go: exposure, camera download, processing, "
        + "pulses), what the Predictive algorithm learned (periodic error, drift), Dec backlash pulse, active coaching hints, "
        + "the newest alerts with explanation and suggested fix (and the flight recorder incident they started), and "
        + "optionally the newest guide steps (error and pulses per frame). Other guiders (e.g. PHD2) only report state and RMS. "
        + "Use it to judge a night's guiding and to explain star losses or bad RMS; get_guide_incident has the frame-by-frame "
        + "record of an incident. Side-effect free.";

    public override ToolAnnotations Annotations => ToolAnnotations.Query;

    protected override void DefineArguments(ToolSchema arguments) {
        arguments.Integer("steps", "Number of newest guide steps to include (0-200). Default 0.", minimum: 0, maximum: 200, defaultValue: 0);
        arguments.Integer("alerts", "Number of newest alerts to include (0-100). Default 20.", minimum: 0, maximum: 100, defaultValue: 20);
    }

    protected override Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, ToolContext context, CancellationToken cancellationToken) {
        var report = Observatory.GetGuiderReport(arguments.GetInt("steps") ?? 0, arguments.GetInt("alerts") ?? 20);

        var payload = new {
            connected = report.Connected,
            name = report.Name,
            advanced = report.Advanced,
            message = report.Message,
            status = report.Status,
            alerts = report.Alerts,
            steps = report.Steps
        };

        return Task.FromResult(ToolCallResult.Json(payload));
    }
}

internal sealed class GetCoachReportTool : ObservatoryTool {

    public GetCoachReportTool(IPinsObservatory observatory) : base(observatory) {
    }

    public override string Name => "get_guiding_coach_report";

    public override string Title => "Get Guiding Coach report";

    public override string Description =>
        "Results of the internal guider's Guiding Coach: the running or last session (phase, steps with progress, camera "
        + "check of exposure × gain, sky and mount drift, mount response with Dec backlash and pulse response, guided trials "
        + "of settings, findings) and the stored report cards, newest first (grade, error budget: seeing, centroid noise and "
        + "mount in arcsec RMS, polar alignment error, periodic error, ranked findings with the setting changes they suggest). "
        + "Raw drift samples and pulse lists are left out unless include_samples is set. Only the pins internal guider has a "
        + "Guiding Coach; the session itself is started from Touch-N-Stars. Side-effect free.";

    public override ToolAnnotations Annotations => ToolAnnotations.Query;

    protected override void DefineArguments(ToolSchema arguments) {
        arguments.Integer("history", "Number of stored report cards to include, newest first (0-10). Default 3.", minimum: 0, maximum: 10, defaultValue: 3);
        arguments.Boolean("include_samples", "Include raw drift samples, backlash points and pulse lists. Default false.", defaultValue: false);
    }

    protected override Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, ToolContext context, CancellationToken cancellationToken) {
        var data = Observatory.GetCoachReport(arguments.GetInt("history") ?? 3, arguments.GetBool("include_samples") ?? false);

        return Task.FromResult(GuiderDataResult(data));
    }

    internal static ToolCallResult GuiderDataResult(GuiderData data) {
        if (!data.Available) {
            return ToolCallResult.Error(data.Message);
        }

        return ToolCallResult.Json(new {
            message = data.Message,
            data = data.Data
        });
    }
}

internal sealed class ListGuideIncidentsTool : ObservatoryTool {

    public ListGuideIncidentsTool(IPinsObservatory observatory) : base(observatory) {
    }

    public override string Name => "list_guide_incidents";

    public override string Title => "List guiding incidents";

    public override string Description =>
        "The internal guider's flight recorder: incidents (star lost, runaway, mount not responding, camera failures, "
        + "spikes, manual marks) newest first, each with id, start and end time, kind, occurrences, whether it is still "
        + "ongoing, the likely cause found by the diagnosis, frame count and size, plus whether recording is enabled and the "
        + "storage budget. Use get_guide_incident with an id for the triggers, the diagnosis with its evidence and the "
        + "frame-by-frame telemetry. Only the pins internal guider records incidents. Side-effect free.";

    public override ToolAnnotations Annotations => ToolAnnotations.Query;

    protected override void DefineArguments(ToolSchema arguments) {
        arguments.Integer("count", "Maximum number of incidents, newest first (1-100). Default 20.", minimum: 1, maximum: 100, defaultValue: 20);
    }

    protected override Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, ToolContext context, CancellationToken cancellationToken) {
        var data = Observatory.GetGuideIncidents(arguments.GetInt("count") ?? 20);

        return Task.FromResult(GetCoachReportTool.GuiderDataResult(data));
    }
}

internal sealed class GetGuideIncidentTool : ObservatoryTool {

    public GetGuideIncidentTool(IPinsObservatory observatory) : base(observatory) {
    }

    public override string Name => "get_guide_incident";

    public override string Title => "Get guiding incident";

    public override string Description =>
        "One incident of the internal guider's flight recorder (id from list_guide_incidents or from an alert in "
        + "get_guider_report): triggers with error codes, markers (dither, settle, slew, notes), the diagnosis (likely cause, "
        + "message and evidence such as SNR drop, star mass collapse, mount not moving), and with frames > 0 the newest "
        + "frames of telemetry: per frame time, state, star found or lost status, offsets and RA/Dec error in arcsec, pulses "
        + "sent, SNR, star mass, HFD and the mount state. Images are not returned. Side-effect free.";

    public override ToolAnnotations Annotations => ToolAnnotations.Query;

    protected override void DefineArguments(ToolSchema arguments) {
        arguments.String("id", "Incident id as listed by list_guide_incidents.", required: true, maxLength: 100, pattern: "^[A-Za-z0-9-]+$");
        arguments.Integer("frames", "Number of telemetry frames to include, the last ones of the incident (0-600). Default 60.", minimum: 0, maximum: 600, defaultValue: 60);
    }

    protected override Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, ToolContext context, CancellationToken cancellationToken) {
        var id = arguments.GetString("id", required: true)!.Trim();
        var data = Observatory.GetGuideIncident(id, arguments.GetInt("frames") ?? 60);

        return Task.FromResult(GetCoachReportTool.GuiderDataResult(data));
    }
}
