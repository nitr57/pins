// SPDX-License-Identifier: MPL-2.0

using NINA.Core.Utility;
using NINA.Equipment.Equipment.MyGuider.Advanced;
using NINA.Equipment.Equipment.MyGuider.Internal;
using NINA.Equipment.Interfaces;
using NINA.Mcp.Observatory;

namespace NINA.Mcp.Adapter;

public sealed partial class NinaObservatory {

    /// <summary>Raw sample lists of the Guiding Coach, left out unless asked for: they make a report many times larger.</summary>
    private static readonly string[] CoachSampleFields = ["Samples", "BacklashPoints", "Pulses", "ReversalMoves"];

    /// <summary>Per-frame star lists of incident telemetry: positions for overlays, not needed to diagnose.</summary>
    private static readonly string[] IncidentFrameFields = ["Stars"];

    private const string NeedsInternalGuider =
        "needs the pins internal guider (select it as the guider in the profile); the selected guider does not provide it.";

    // ----- Guider diagnostics -----

    public GuiderReport GetGuiderReport(int maxSteps, int maxAlerts) {
        try {
            var info = guiderMediator.GetInfo();

            if (info?.Connected != true) {
                return new GuiderReport(
                    false,
                    null,
                    false,
                    "Guider is not connected. Incidents and Guiding Coach reports of earlier sessions stay readable (list_guide_incidents, get_guiding_coach_report).",
                    new Dictionary<string, object?>(),
                    [],
                    []);
            }

            var name = DisplayName(info);

            if (guiderMediator.GetDevice() is IAdvancedGuider advanced) {
                var status = PropertyBags.FromObject(advanced.GetStatus());

                List<IReadOnlyDictionary<string, object?>> alerts = maxAlerts > 0
                    ? PropertyBags.FromList(advanced.GetRecentAlerts(maxAlerts))
                    : [];

                List<IReadOnlyDictionary<string, object?>> steps = maxSteps > 0
                    ? PropertyBags.FromList(advanced.GetRecentSteps(maxSteps))
                    : [];

                return new GuiderReport(
                    true,
                    name,
                    true,
                    "pins internal guider. Window statistics cover the recent frames, session statistics the whole guiding session; alerts and steps are oldest first.",
                    status,
                    alerts,
                    steps);
            }

            var basic = new Dictionary<string, object?>();
            FillGuider(basic);

            return new GuiderReport(
                true,
                name,
                false,
                "This guider reports only its state and RMS. Statistics, timing, alerts, incidents and the Guiding Coach need the pins internal guider.",
                basic,
                [],
                []);
        } catch (Exception ex) {
            Logger.Error("MCP: reading the guider report failed", ex);

            return new GuiderReport(false, null, false, $"Reading the guider failed: {ex.Message}", new Dictionary<string, object?>(), [], []);
        }
    }

    public GuiderData GetCoachReport(int maxHistory, bool includeSamples) {
        try {
            if (!TryGetGuiderFeature<IGuidingCoach>("The Guiding Coach", out var coach, out var unavailable)) {
                return unavailable;
            }

            var exclude = includeSamples ? null : CoachSampleFields;

            List<IReadOnlyDictionary<string, object?>> history = maxHistory > 0
                ? PropertyBags.FromList(coach.GetCoachHistory(maxHistory), exclude)
                : [];

            var data = new Dictionary<string, object?> {
                ["session"] = PropertyBags.FromObject(coach.GetCoachStatus(), exclude),
                ["history"] = history
            };

            var message = history.Count == 0
                ? "No stored Guiding Coach report yet; session holds the running or last session (phase Idle before the first)."
                : "session is the running or last session; history holds the stored report cards, newest first.";

            return new GuiderData(true, message, data);
        } catch (Exception ex) {
            Logger.Error("MCP: reading the Guiding Coach report failed", ex);

            return GuiderData.Unavailable($"Reading the Guiding Coach report failed: {ex.Message}");
        }
    }

    public GuiderData GetGuideIncidents(int maxCount) {
        try {
            if (!TryGetGuiderFeature<IGuideIncidentRecorder>("The incident recorder", out var recorder, out var unavailable)) {
                return unavailable;
            }

            var list = recorder.GetIncidents();
            var incidents = list?.Incidents ?? new List<AdvancedIncidentSummary>();

            var data = new Dictionary<string, object?> {
                ["enabled"] = list?.Enabled,
                ["recordingId"] = list?.RecordingId,
                ["total"] = incidents.Count,
                ["usedBytes"] = list?.UsedBytes,
                ["budgetBytes"] = list?.BudgetBytes,
                ["maxIncidents"] = list?.MaxIncidents,
                ["incidents"] = PropertyBags.FromList(incidents.Take(Math.Max(0, maxCount)))
            };

            var message = list?.Enabled == false
                ? "The incident recorder is switched off in the internal guider settings; only earlier incidents are listed."
                : "Incidents newest first. get_guide_incident with an id has the triggers, diagnosis and telemetry.";

            return new GuiderData(true, message, data);
        } catch (Exception ex) {
            Logger.Error("MCP: listing guiding incidents failed", ex);

            return GuiderData.Unavailable($"Listing the guiding incidents failed: {ex.Message}");
        }
    }

    public GuiderData GetGuideIncident(string id, int maxFrames) {
        try {
            if (!TryGetGuiderFeature<IGuideIncidentRecorder>("The incident recorder", out var recorder, out var unavailable)) {
                return unavailable;
            }

            var incident = recorder.GetIncident(id);

            if (incident == null) {
                return GuiderData.Unavailable($"No incident with id '{id}'. list_guide_incidents lists the ids.");
            }

            var data = PropertyBags.FromObject(incident, ["Frames"]);
            var frames = incident.Frames ?? new List<AdvancedIncidentFrame>();
            var take = Math.Clamp(maxFrames, 0, frames.Count);

            data["framesTotal"] = frames.Count;
            data["framesIncluded"] = take;
            data["frames"] = PropertyBags.FromList(frames.Skip(frames.Count - take), IncidentFrameFields);

            return new GuiderData(
                true,
                take < frames.Count
                    ? $"Incident {id}: the last {take} of {frames.Count} frames are included (frames argument)."
                    : $"Incident {id}.",
                data);
        } catch (Exception ex) {
            Logger.Error("MCP: reading a guiding incident failed", ex);

            return GuiderData.Unavailable($"Reading the incident failed: {ex.Message}");
        }
    }

    /// <summary>
    /// The connected guider as <typeparamref name="T"/>, otherwise the internal guider singleton
    /// (<c>IoCBindings</c> registers one <c>InternalGuider</c> as <see cref="IAdvancedGuider"/>). The mediator only knows a
    /// device while it is connected, but stored coach reports and incidents must stay readable the morning after, when the
    /// guider is disconnected; the internal guider reads them from its stores without a connection.
    /// </summary>
    private bool TryGetGuiderFeature<T>(string feature, out T guider, out GuiderData unavailable) where T : class {
        guider = null!;
        unavailable = null!;

        if (guiderMediator.GetDevice() is T connected) {
            guider = connected;
            return true;
        }

        // Only while no other guider is connected and the profile's guider is the internal one: with PHD2 in use, old
        // internal guider data would be taken for the active guider's.
        var otherConnected = guiderMediator.GetInfo()?.Connected == true;
        var profileUsesInternal = string.Equals(
            profileService.ActiveProfile.GuiderSettings.GuiderName,
            InternalGuider.DeviceId,
            StringComparison.Ordinal);

        if (!otherConnected && profileUsesInternal && internalGuider is T stored) {
            guider = stored;
            return true;
        }

        unavailable = GuiderData.Unavailable($"{feature} {NeedsInternalGuider}");

        return false;
    }
}
