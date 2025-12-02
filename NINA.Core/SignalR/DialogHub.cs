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
using System;
using System.Windows;
using System.Threading.Tasks;

namespace NINA.Core.SignalR {
    /// <summary>
    /// SignalR hub for real-time dialog data broadcasting
    /// </summary>
    public class DialogHub : Hub {
        public override async Task OnConnectedAsync() {
            await base.OnConnectedAsync();
        }

        public override async Task OnDisconnectedAsync(Exception exception) {
            await base.OnDisconnectedAsync(exception);
        }

        /// <summary>
        /// Client calls this to click a button in a dialog
        /// </summary>
        public async Task ClickDialogButton(string contentType, string buttonName) {
            try {
                // Find the dialog by ContentType and click the button
                var dialogs = DialogService.GetAllDialogs();
                foreach (var dialog in dialogs) {
                    if (dialog.ContentType == contentType) {
                        bool success = DialogService.ClickButton(dialog.DialogId, buttonName);
                        return;
                    }
                }
            } catch {
            }
            
            await Task.CompletedTask;
        }
    }
}
