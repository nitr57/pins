#region "copyright"

/*
    Copyright © 2026 Nico Trost <nico.trost57@gmail.com> and the PI.N.S. contributors

    This file is part of PI 'N' Stars.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

namespace NINA.Equipment.Interfaces {

    /// <summary>
    /// A focuser whose Move returns only once the device itself reports that it has stopped at the target,
    /// polled from the device rather than inferred from a command acknowledgement. FocuserVM then reads its
    /// state right after the move instead of waiting for the next poll of the device update timer.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="IFocuser"/> so that plugin focusers keep loading. Don't add it to drivers that
    /// can report a move as done while the motor still runs, such as INDI drivers that acknowledge
    /// ABS_FOCUS_POSITION with Ok right after sending the command: for them the poll wait is the only time
    /// the focuser gets to finish before the next exposure.
    /// </remarks>
    public interface IFocuserReportsMoveEnd {
    }
}
