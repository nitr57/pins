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
using System.Linq;
using System.Windows;
using System.Threading.Tasks;

namespace NINA.Core.SignalR {
    /// <summary>
    /// SignalR hub for real-time dialog data broadcasting
    /// </summary>
    public class DialogHub : Hub {
        public override async Task OnConnectedAsync() {
            await base.OnConnectedAsync();
            
            // Send all currently active dialogs to the newly connected client
            var activeDialogs = DialogBroadcaster.GetActiveDialogs();
            foreach (var dialog in activeDialogs) {
                await Clients.Caller.SendAsync("ReceiveDialog", dialog);
            }
        }

        public override async Task OnDisconnectedAsync(Exception exception) {
            await base.OnDisconnectedAsync(exception);
        }

        /// <summary>
        /// Client calls this to click a button in a dialog. Of several open dialogs of the content type, the newest is
        /// the one the client shows (its broadcast came last). Returns whether a button was clicked.
        /// </summary>
        public Task<bool> ClickDialogButton(string contentType, string buttonName) {
            try {
                var dialog = DialogService.GetAllDialogs()
                    .Where(d => d.ContentType == contentType)
                    .OrderByDescending(d => d.DialogId)
                    .FirstOrDefault();
                if (dialog == null) {
                    Logger.Warning($"Dialog button '{buttonName}' clicked, but no {contentType} dialog is open");
                    return Task.FromResult(false);
                }

                if (!DialogService.ClickButton(dialog.DialogId, buttonName)) {
                    Logger.Warning($"Dialog button '{buttonName}' clicked, but the {contentType} dialog has no such button");
                    return Task.FromResult(false);
                }

                return Task.FromResult(true);
            } catch (Exception ex) {
                Logger.Error($"Dialog button '{buttonName}' of {contentType} failed", ex);
                return Task.FromResult(false);
            }
        }
    }
}
