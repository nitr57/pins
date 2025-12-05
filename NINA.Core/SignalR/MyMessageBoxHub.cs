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
using NINA.Core.Utility;
using System;
using System.Threading.Tasks;
using System.Windows;

namespace NINA.Core.SignalR {
    /// <summary>
    /// SignalR hub for real-time message box broadcasting and response handling
    /// </summary>
    public class MyMessageBoxHub : Hub {
        private readonly IMyMessageBoxBroadcaster _broadcaster;

        public MyMessageBoxHub(IMyMessageBoxBroadcaster broadcaster) {
            _broadcaster = broadcaster;
        }

        public override async Task OnConnectedAsync() {
            Logger.Info($"Client connected to MyMessageBoxHub: {Context.ConnectionId}");
            await base.OnConnectedAsync();
        }

        public override async Task OnDisconnectedAsync(Exception exception) {
            Logger.Info($"Client disconnected from MyMessageBoxHub: {Context.ConnectionId}");
            await base.OnDisconnectedAsync(exception);
        }

        /// <summary>
        /// Handle message box response from the client
        /// </summary>
        public async Task RespondToMessageBox(string messageBoxId, string result) {
            Logger.Info($"Received message box response: ID={messageBoxId}, Result={result}");
            await _broadcaster.HandleMessageBoxResponseAsync(messageBoxId, result);
        }
    }
}
