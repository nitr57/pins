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
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Equipment.Interfaces {

    /// <summary>
    /// Optional Guiding Coach of an <see cref="IAdvancedGuider"/>: a guided session that measures the camera, the sky and
    /// the mount, tries settings and grades the result, plus live hints while guiding. UIs discover it with
    /// <c>guider as IGuidingCoach</c>. Same stability rules as <see cref="IAdvancedGuider"/>.
    /// </summary>
    public interface IGuidingCoach : IAdvancedGuider {

        /// <summary>
        /// Start a Guiding Coach session: a camera check (exposure × gain), sky and mount drift with the guiding output
        /// off, the mount response (Dec backlash, pulse response), guided trials of settings sets and a report card.
        /// Returns once the session was accepted (it runs in the background) or rejected; a rejection is reported
        /// only in the result and leaves the status of a running/last session untouched. Stops guiding/looping as needed; guiding that
        /// was active at the start is resumed with the original settings when the session ends (also after cancel).
        /// Progress via <see cref="IAdvancedGuider.AdvancedGuiderEvent"/> type <see cref="AdvancedGuiderEventTypes.Coach"/>.
        /// </summary>
        Task<AdvancedCoachStartResult> StartCoach(AdvancedCoachOptions options, CancellationToken ct);

        /// <summary>Skip the running step (its partial results are kept when usable).</summary>
        Task<bool> SkipCoachStep(CancellationToken ct);

        /// <summary>Cancel the session; temporary settings are restored and guiding output re-enabled.</summary>
        Task<bool> CancelCoach(CancellationToken ct);

        /// <summary>
        /// Current or last session (<see cref="AdvancedCoachPhases.Idle"/> before the first session); always carries the
        /// camera's gain range.
        /// </summary>
        AdvancedCoachStatus GetCoachStatus();

        /// <summary>
        /// Apply the setting changes of the given findings (<see cref="AdvancedCoachFinding.Id"/>) or trials
        /// (<see cref="AdvancedCoachActions.TrialPrefix"/> + <see cref="AdvancedCoachTrial.Id"/>) of the current/last
        /// session, or of active live hints (a hint applied this way is dismissed), to the guider settings.
        /// </summary>
        Task<bool> ApplyCoachActions(IReadOnlyCollection<string> ids, CancellationToken ct);

        /// <summary>Stored reports of the active profile, newest first, without the raw samples; at most <paramref name="maxCount"/>.</summary>
        IReadOnlyList<AdvancedCoachReport> GetCoachHistory(int maxCount);

        /// <summary>Hide a live hint (<see cref="AdvancedCoachFinding.Id"/>) for the rest of the guiding session.</summary>
        bool DismissHint(string id);
    }
}
