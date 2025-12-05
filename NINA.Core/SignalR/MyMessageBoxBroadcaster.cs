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
using NINA.Core.MyMessageBox;
using NINA.Core.Utility;
using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace NINA.Core.SignalR {
    /// <summary>
    /// Service to broadcast message boxes via SignalR and collect responses
    /// </summary>
    public class MyMessageBoxBroadcaster : IMyMessageBoxBroadcaster {
        private readonly IHubContext<MyMessageBoxHub> _hubContext;
        private static IMyMessageBoxBroadcaster _instance;
        private readonly ConcurrentDictionary<string, TaskCompletionSource<MessageBoxResult>> _pendingRequests;

        public MyMessageBoxBroadcaster(IHubContext<MyMessageBoxHub> hubContext) {
            _hubContext = hubContext;
            _pendingRequests = new ConcurrentDictionary<string, TaskCompletionSource<MessageBoxResult>>();
            _instance = this;
        }

        /// <summary>
        /// Get the singleton instance (for use by plugins that don't have DI access)
        /// </summary>
        public static IMyMessageBoxBroadcaster Instance => _instance;

        public async Task<MessageBoxResult> ShowMessageBoxAsync(string messageBoxText, string caption, MessageBoxButton button, MessageBoxResult defaultResult, TimeSpan timeout = default) {
            if (timeout == default) {
                timeout = TimeSpan.FromMinutes(5); // Default timeout of 5 minutes
            }

            var message = new MyMessageBoxMessage {
                Title = caption,
                Text = messageBoxText,
                Button = button.ToString(),
                DefaultResult = defaultResult.ToString()
            };

            var tcs = new TaskCompletionSource<MessageBoxResult>();
            _pendingRequests.TryAdd(message.Id, tcs);

            try {
                if (_hubContext != null) {
                    Logger.Info($"Broadcasting message box {message.Id}: '{messageBoxText}' with button type {button}");
                    await _hubContext.Clients.All.SendAsync("ReceiveMessageBox", message);
                    Logger.Info($"Message box {message.Id} sent to clients");
                } else {
                    Logger.Warning($"HubContext is null, cannot broadcast message box {message.Id}");
                    return defaultResult;
                }

                using (var cts = new CancellationTokenSource(timeout)) {
                    cts.Token.Register(() => {
                        if (tcs.TrySetResult(defaultResult)) {
                            Logger.Warning($"MessageBox request {message.Id} timed out. Using default result: {defaultResult}");
                        }
                    });

                    Logger.Info($"Waiting for response to message box {message.Id}...");
                    var result = await tcs.Task;
                    Logger.Info($"Message box {message.Id} returned result: {result}");
                    return result;
                }
            } catch (Exception ex) {
                Logger.Error($"Failed to broadcast message box via SignalR: {ex.Message}");
                return defaultResult;
            } finally {
                _pendingRequests.TryRemove(message.Id, out _);
            }
        }

        public async Task HandleMessageBoxResponseAsync(string messageBoxId, string result) {
            if (_pendingRequests.TryRemove(messageBoxId, out var tcs)) {
                if (System.Enum.TryParse<MessageBoxResult>(result, out var parsedResult)) {
                    tcs.TrySetResult(parsedResult);
                } else {
                    Logger.Warning($"Failed to parse MessageBoxResult from '{result}'");
                    tcs.TrySetResult(MessageBoxResult.None);
                }
            }
            await Task.CompletedTask;
        }
    }
}
