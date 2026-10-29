#region "copyright"

/*
    Copyright © 2025 Nico Trost <nico.trost57@gmail.com> and the PI.N.S. contributors

    This file is part of PI 'N' Stars.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Microsoft.AspNetCore.SignalR;
using NINA.Core.Utility.Notification;
using System;
using System.Threading.Tasks;

namespace NINA.Core.SignalR {
    /// <summary>
    /// Service to broadcast notifications via SignalR
    /// </summary>
    public class SignalRNotificationBroadcaster {
        private readonly IHubContext<NotificationHub> _hubContext;

        public SignalRNotificationBroadcaster(IHubContext<NotificationHub> hubContext) {
            _hubContext = hubContext;
        }

        public async Task BroadcastNotificationAsync(NotificationMessage message) {
            try {
                if (message != null && _hubContext != null) {
                    await _hubContext.Clients.All.SendAsync("ReceiveNotification", message);
                }
            } catch (Exception ex) {
                Utility.Logger.Error($"Failed to broadcast notification via SignalR: {ex.Message}");
            }
        }
    }
}
