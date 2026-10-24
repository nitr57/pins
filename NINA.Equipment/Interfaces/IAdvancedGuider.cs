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
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Equipment.Interfaces {

    /// <summary>
    /// Optional extension of <see cref="IGuider"/> for guiders that run inside PINS (the internal guider)
    /// and can expose rich live data to UIs: frames with star overlays, per-frame guide steps, calibration,
    /// statistics, alerts and editable settings. UIs discover it with <c>guiderMediator.GetDevice() as IAdvancedGuider</c>,
    /// and the optional <see cref="IGuidingCoach"/> and <see cref="IGuideIncidentRecorder"/> the same way.
    /// The DTOs (namespace <c>NINA.Equipment.Equipment.MyGuider.Advanced</c>) are plain, JSON-serializable classes.
    /// Implementations must be thread-safe.
    /// </summary>
    /// <remarks>
    /// Experimental: this contract changes together with its implementer and its consumers, which are built with pins.
    /// </remarks>
    public interface IAdvancedGuider : IGuider {

        /// <summary>Snapshot of the guider state, current star, calibration progress, statistics and last error.</summary>
        AdvancedGuiderStatus GetStatus();

        /// <summary>Most recent guide steps (oldest first), at most <paramref name="maxCount"/>.</summary>
        IReadOnlyList<AdvancedGuideStep> GetRecentSteps(int maxCount);

        /// <summary>Most recent alerts/events (oldest first), at most <paramref name="maxCount"/>.</summary>
        IReadOnlyList<AdvancedGuiderAlert> GetRecentAlerts(int maxCount);

        /// <summary>Latest processed guide frame with star overlays, or null when no frame was taken yet.</summary>
        AdvancedGuiderFrame? GetLatestFrame();

        /// <summary>Current calibration, or null when not calibrated.</summary>
        AdvancedGuiderCalibration? GetCalibration();

        /// <summary>All settings with metadata for building a settings form.</summary>
        IReadOnlyList<AdvancedGuiderSetting> GetSettings();

        /// <summary>Change a setting by name (value as invariant-culture string). Returns false with an error message when rejected.</summary>
        bool TrySetSetting(string name, string value, out string error);

        /// <summary>Start looping exposures without guiding (for framing/focusing the guide camera).</summary>
        Task<bool> StartLooping(CancellationToken ct);

        /// <summary>Stop looping/guiding and stop exposures.</summary>
        Task<bool> StopLooping(CancellationToken ct);

        /// <summary>Pause or resume guiding (exposures continue while paused).</summary>
        Task<bool> SetPaused(bool paused, CancellationToken ct);

        /// <summary>Dither by up to <paramref name="pixels"/> guide pixels and wait for settling.</summary>
        Task<bool> DitherBy(double pixels, bool raOnly, CancellationToken ct);

        /// <summary>
        /// Select the star nearest (<paramref name="x"/>, <paramref name="y"/>) (camera px as in <see cref="GetLatestFrame"/>, within the
        /// search region) as the guide star, like clicking a star in PHD2; in multi-star mode its secondary stars are found around it.
        /// Only while looping without guiding. Completes once the next frame was processed.
        /// </summary>
        Task<AdvancedStarSelectionResult> SelectGuideStar(double x, double y, CancellationToken ct);

        /// <summary>
        /// Build a dark library for exposures between <paramref name="minExposureSeconds"/> and <paramref name="maxExposureSeconds"/>
        /// (PHD2's standard exposure steps), <paramref name="framesPerExposure"/> frames each. The guide scope must be covered and
        /// the guider stopped. Progress is reported through <see cref="AdvancedGuiderEvent"/> with type
        /// <see cref="AdvancedGuiderEventTypes.Darks"/>.
        /// </summary>
        Task<bool> BuildDarkLibrary(double minExposureSeconds, double maxExposureSeconds, int framesPerExposure, CancellationToken ct);

        /// <summary>
        /// Raised for every guide step, alert, state change, calibration step, settle update, new frame and the other
        /// types of <see cref="AdvancedGuiderEventTypes"/>, which also documents each payload.
        /// </summary>
        event EventHandler<AdvancedGuiderEventArgs>? AdvancedGuiderEvent;
    }
}
