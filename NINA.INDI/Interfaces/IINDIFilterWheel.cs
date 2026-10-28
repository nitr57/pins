#region "copyright"

/*
    Copyright © 2025 Nico Trost <nico.trost57@gmail.com> and the PI.N.S. contributors

    This file is part of PI 'N' Stars.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System.Threading;
using System.Threading.Tasks;

namespace NINA.INDI.Interfaces {
    public interface IINDIFilterWheel : IINDIDevice {
        int[] FocusOffsets { get; }
        string[] Names { get; }
        int Position { get; set; }
        bool IsMoving { get; }

        /// <summary>
        /// Gives up on a move the driver did not report done in time. When no FILTER_SLOT update arrived
        /// since the command, Position reads the slot from before it again instead of the target that was sent.
        /// </summary>
        void AbandonMove();
    }
}