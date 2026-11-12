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

namespace NINA.Equipment.Interfaces {

    /// <summary>
    /// Optional guide log archive of an <see cref="IAdvancedGuider"/>: the PHD2-format guide logs it wrote (one per
    /// night), so a UI can show past sessions in a log viewer. UIs discover it with <c>guider as IGuideLogArchive</c>.
    /// The logs are files of the guider, available also while it is not connected. Same stability rules as
    /// <see cref="IAdvancedGuider"/>.
    /// </summary>
    public interface IGuideLogArchive : IAdvancedGuider {

        /// <summary>The guide logs, newest night first. Throws when the log directory cannot be read.</summary>
        IReadOnlyList<AdvancedGuideLogFile> GetGuideLogs();

        /// <summary>
        /// Opens a guide log of <see cref="GetGuideLogs"/> for reading (the log being written can be read too, up to
        /// what it holds). Null when the name is unknown or not a guide log name; other errors (an unreadable file) are thrown.
        /// The caller disposes the stream.
        /// </summary>
        Stream? OpenGuideLog(string name);
    }
}
