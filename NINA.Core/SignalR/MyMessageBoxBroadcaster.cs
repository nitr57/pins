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
using System.Collections.Generic;
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
        private static readonly ConcurrentDictionary<string, MyMessageBox.MyMessageBoxMessage> _activeMessageBoxes = new();

        public MyMessageBoxBroadcaster(IHubContext<MyMessageBoxHub> hubContext) {
            _hubContext = hubContext;
            _pendingRequests = new ConcurrentDictionary<string, TaskCompletionSource<MessageBoxResult>>();
            _instance = this;
        }

        /// <summary>
        /// Get the singleton instance (for use by plugins that don't have DI access)
        /// </summary>
        public static IMyMessageBoxBroadcaster Instance => _instance;

        /// <summary>
        /// Get all currently pending message boxes (for sending to newly connected clients)
        /// </summary>
        public static IList<MyMessageBox.MyMessageBoxMessage> GetActiveMessageBoxes() {
            return new List<MyMessageBox.MyMessageBoxMessage>(_activeMessageBoxes.Values);
        }

        public Task<MessageBoxResult> ShowMessageBoxAsync(string messageBoxText, string caption, MessageBoxButton button, MessageBoxResult defaultResult, TimeSpan timeout = default) {
            return ShowMessageBoxAsync(messageBoxText, caption, button, defaultResult, timeout, CancellationToken.None);
        }

        public async Task<MessageBoxResult> ShowMessageBoxAsync(string messageBoxText, string caption, MessageBoxButton button, MessageBoxResult defaultResult, TimeSpan timeout, CancellationToken token) {
            if (timeout == default) {
                timeout = TimeSpan.FromMinutes(5); // Default timeout of 5 minutes
            }

            if (token.IsCancellationRequested) {
                return defaultResult;
            }

            var message = new MyMessageBoxMessage {
                Title = caption,
                Text = messageBoxText,
                Button = button.ToString(),
                DefaultResult = defaultResult.ToString()
            };

            // continuations run on their own: a token cancelled by the caller (e.g. a socket handler) must not run the rest
            // of this method, the close broadcast included, inside its Cancel()
            var tcs = new TaskCompletionSource<MessageBoxResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingRequests.TryAdd(message.Id, tcs);
            var sent = false;
            var finalResult = defaultResult;

            try {
                if (_hubContext != null) {
                    Logger.Info($"Broadcasting message box {message.Id}: '{messageBoxText}' with button type {button}");
                    _activeMessageBoxes[message.Id] = message;
                    await _hubContext.Clients.All.SendAsync("ReceiveMessageBox", message);
                    sent = true;
                    Logger.Info($"Message box {message.Id} sent to clients");
                } else {
                    Logger.Warning($"HubContext is null, cannot broadcast message box {message.Id}");
                    return defaultResult;
                }

                using (var cts = new CancellationTokenSource(timeout))
                using (cts.Token.Register(() => {
                    if (tcs.TrySetResult(defaultResult)) {
                        Logger.Warning($"MessageBox request {message.Id} timed out. Using default result: {defaultResult}");
                    }
                }))
                using (token.Register(() => {
                    if (tcs.TrySetResult(defaultResult)) {
                        Logger.Info($"MessageBox request {message.Id} was ended without an answer. Using default result: {defaultResult}");
                    }
                })) {
                    Logger.Info($"Waiting for response to message box {message.Id}...");
                    var result = await tcs.Task;
                    Logger.Info($"Message box {message.Id} returned result: {result}");
                    finalResult = result;
                    return result;
                }
            } catch (Exception ex) {
                Logger.Error($"Failed to broadcast message box via SignalR: {ex.Message}");
                return defaultResult;
            } finally {
                _pendingRequests.TryRemove(message.Id, out _);
                _activeMessageBoxes.TryRemove(message.Id, out _);

                // the box stayed open on every client but the one that answered, also after a timeout or an end from
                // outside; the app closes it on CloseMessageBox
                if (sent) {
                    try {
                        await _hubContext.Clients.All.SendAsync("CloseMessageBox", message.Id, finalResult.ToString());
                    } catch (Exception ex) {
                        Logger.Error($"Failed to broadcast the close of message box {message.Id}: {ex.Message}");
                    }
                }
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
