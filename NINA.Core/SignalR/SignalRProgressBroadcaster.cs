#region "copyright"

/*
    Copyright © 2025-2026 Nico Trost <nico.trost57@gmail.com> and the PI.N.S. contributors

    This file is part of PI 'N' Stars.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Microsoft.AspNetCore.SignalR;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace NINA.Core.SignalR {
    /// <summary>
    /// Service to broadcast progress updates via SignalR
    /// </summary>
    public class SignalRProgressBroadcaster {
        private readonly IHubContext<ProgressHub> _hubContext;
        private static SignalRProgressBroadcaster _instance;
        private static readonly ConcurrentDictionary<string, ProgressMessage> _activeProgresses = new();

        public SignalRProgressBroadcaster(IHubContext<ProgressHub> hubContext) {
            _hubContext = hubContext;
            _instance = this;
        }

        /// <summary>
        /// Get all currently active progress items (for sending to newly connected clients)
        /// </summary>
        public static IList<ProgressMessage> GetActiveProgresses() {
            return new List<ProgressMessage>(_activeProgresses.Values);
        }

        public async Task BroadcastProgressAsync(ProgressMessage message) {
            try {
                if (message != null && _hubContext != null) {
                    if (message.State == "create" || message.State == "update") {
                        _activeProgresses[message.Source] = message;
                    } else if (message.State == "delete") {
                        _activeProgresses.TryRemove(message.Source, out _);
                    }
                    await _hubContext.Clients.All.SendAsync("ReceiveProgress", message).ConfigureAwait(false);
                }
            } catch (Exception ex) {
                Utility.Logger.Error($"Failed to broadcast progress via SignalR: {ex.Message}");
            }
        }
    }
}