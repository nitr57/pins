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

using NINA.Equipment.Interfaces;
using System;

namespace NINA.Equipment.Equipment.MyGuider.Advanced {

    /// <summary>A guide log of <see cref="IGuideLogArchive"/>.</summary>
    public class AdvancedGuideLogFile {
        /// <summary>File name, e.g. InternalGuider_GuideLog_2026-10-12.txt; the key for <see cref="IGuideLogArchive.OpenGuideLog"/>.</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>The night (noon to noon, local time) as yyyy-MM-dd, null when the name carries no date.</summary>
        public string? Night { get; set; }

        public long SizeBytes { get; set; }

        /// <summary>Last write, UTC.</summary>
        public DateTime ModifiedUtc { get; set; }

        /// <summary>True for the log the guider is writing right now.</summary>
        public bool Active { get; set; }

        /// <summary>
        /// Time zone of the log's wall-clock times (PHD2 logs local time): the host's zone id (IANA on Linux) and its
        /// current UTC offset in minutes, for clients that do not know the id.
        /// </summary>
        public string TimeZone { get; set; } = string.Empty;

        public int UtcOffsetMinutes { get; set; }
    }
}
