#region "copyright"

/*
    Copyright © 2025-2026 Nico Trost <nico.trost57@gmail.com> and the PI.N.S. contributors

    This file is part of PI 'N' Stars.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

#nullable enable annotations

using NINA.Equipment.Equipment.MyGuider.Advanced;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Equipment.Interfaces {

    /// <summary>
    /// Optional flight recorder of an <see cref="IAdvancedGuider"/>: it keeps the frames and telemetry around guiding
    /// incidents (star losses, runaways, mount and camera failures, spikes, manual marks) with a likely cause, so they can
    /// be replayed and shared the next morning. UIs discover it with <c>guider as IGuideIncidentRecorder</c>; new, saved
    /// and deleted incidents are announced through <see cref="IAdvancedGuider.AdvancedGuiderEvent"/> type
    /// <see cref="AdvancedGuiderEventTypes.Incident"/>. Same stability rules as <see cref="IAdvancedGuider"/>.
    /// </summary>
    public interface IGuideIncidentRecorder : IAdvancedGuider {

        /// <summary>
        /// The recorded incidents, newest first, with the storage budget. The list holds plain
        /// <see cref="AdvancedIncidentSummary"/> objects, never the full <see cref="AdvancedIncident"/>.
        /// </summary>
        AdvancedIncidentList GetIncidents();

        /// <summary>One incident with its per-frame telemetry, markers and diagnosis; null when unknown.</summary>
        AdvancedIncident? GetIncident(string id);

        /// <summary>
        /// A stored image of an incident frame: <paramref name="kind"/> "context" (the whole field, binned) or "key" (full
        /// resolution, kept at the key moments). Null when the frame has no such image. Pixels are as the guider saw them
        /// (after dark/defect correction).
        /// </summary>
        AdvancedIncidentImage? GetIncidentImage(string id, string kind, long frame);

        /// <summary>The star crops of an incident frame (primary first, then the secondaries); empty when none were kept.</summary>
        IReadOnlyList<AdvancedIncidentImage> GetIncidentCrops(string id, long frame);

        /// <summary>Keep (true) an incident so that the budget rotation skips it, or release it (false). False when unknown.</summary>
        bool SetIncidentKept(string id, bool kept);

        /// <summary>Deletes an incident. False when unknown.</summary>
        bool DeleteIncident(string id);

        /// <summary>Deletes all incidents that are not kept; returns how many were deleted.</summary>
        int DeleteAllIncidents();

        /// <summary>
        /// Records the last 2 minutes and the next 30 seconds as a manual incident with an optional note. Returns its id, or
        /// null with <paramref name="error"/> when refused (recorder off, or not guiding or calibrating).
        /// </summary>
        string? MarkIncident(string? note, out string error);

        /// <summary>
        /// Writes the incident as a zip to <paramref name="output"/>: frames as FITS, telemetry, diagnosis and settings as
        /// JSON, and the guide-log and PINS-log excerpts of its time (site location and home paths masked). False when unknown.
        /// </summary>
        Task<bool> WriteIncidentArchive(string id, Stream output, CancellationToken ct);
    }
}
