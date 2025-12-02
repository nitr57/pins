#region "copyright"

/*
    Copyright © 2025 Nico Trost <nico.trost57@gmail.com> and the PI.N.S. contributors

    This file is part of PI 'N' Stars.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Core.Model;
using System.Threading.Tasks;

namespace NINA.Core.SignalR {
    /// <summary>
    /// Interface for broadcasting dialog data via SignalR
    /// </summary>
    public interface IDialogBroadcaster {
        /// <summary>
        /// Broadcasts dialog data to all connected clients
        /// </summary>
        Task BroadcastDialogAsync(DialogData data);

        /// <summary>
        /// Broadcasts a measurement update for the plate solve dialog
        /// </summary>
        Task BroadcastMeasurementAsync(NINA.Core.Model.DialogMeasurement measurement);

        /// <summary>
        /// Broadcasts status update for dialogs
        /// </summary>
        Task BroadcastStatusAsync(string status);

        /// <summary>
        /// Clears a dialog on all clients
        /// </summary>
        Task ClearDialogAsync(string contentType);
    }
}
