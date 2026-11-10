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
using NINA.Core.Model;
using NINA.Core.Utility;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Core.SignalR {
    /// <summary>
    /// Service to broadcast dialog data via SignalR
    /// </summary>
    public class DialogBroadcaster : IDialogBroadcaster {
        private readonly IHubContext<DialogHub> _hubContext;
        private static IDialogBroadcaster _instance;
        private static readonly ConcurrentDictionary<string, DialogData> _activeDialogs = new();

        // Sequence-based ordering: prevents a stale ReceiveDialog from arriving at clients
        // after a ClearDialog for the same contentType was already sent.
        private static readonly SemaphoreSlim _broadcastLock = new SemaphoreSlim(1, 1);
        private static readonly ConcurrentDictionary<string, long> _clearSequences = new();
        private static long _globalSequence = 0;

        public DialogBroadcaster(IHubContext<DialogHub> hubContext) {
            _hubContext = hubContext;
            _instance = this; // Store singleton instance for static access
        }

        /// <summary>
        /// Get the singleton instance (for use by plugins that don't have DI access)
        /// </summary>
        public static IDialogBroadcaster Instance => _instance;
        
        /// <summary>
        /// Get all currently active dialogs (for sending to newly connected clients)
        /// </summary>
        public static IList<DialogData> GetActiveDialogs() {
            return new List<DialogData>(_activeDialogs.Values);
        }

        public async Task BroadcastDialogAsync(DialogData data) {
            try {
                if (data != null && _hubContext != null) {
                    // Capture a sequence number before entering the lock.
                    // ClearDialogAsync also increments this counter before its lock acquisition,
                    // so if clearSeq > broadcastSeq the dialog was already closed and we must
                    // suppress this stale broadcast to prevent the dialog re-appearing on the client.
                    long broadcastSeq = Interlocked.Increment(ref _globalSequence);

                    // The send is started under the lock, which keeps the order against a clear, and awaited after it:
                    // awaited inside, a client that stopped reading held every later dialog and clear back
                    Task send;
                    await _broadcastLock.WaitAsync();
                    try {
                        if (data.Active) {
                            if (_clearSequences.TryGetValue(data.ContentType, out var clearSeq) && clearSeq > broadcastSeq) {
                                Logger.Debug($"[DialogBroadcaster] Suppressing stale ReceiveDialog for {data.ContentType} (broadcastSeq={broadcastSeq}, clearSeq={clearSeq})");
                                return;
                            }
                            _activeDialogs[data.ContentType] = data;
                        }

                        send = _hubContext.Clients.All.SendAsync("ReceiveDialog", data);
                    } finally {
                        _broadcastLock.Release();
                    }

                    await send;
                } else {
                    Logger.Warning($"Cannot broadcast dialog: data={data != null}, hubContext={_hubContext != null}");
                }
            } catch (Exception ex) {
                Logger.Error($"Failed to broadcast dialog via SignalR: {ex.Message}");
            }
        }

        public async Task BroadcastMeasurementAsync(DialogMeasurement measurement) {
            try {
                if (measurement != null && _hubContext != null) {
                    await _hubContext.Clients.All.SendAsync("ReceiveMeasurement", measurement);
                }
            } catch (Exception ex) {
                Logger.Error($"Failed to broadcast measurement via SignalR: {ex.Message}");
            }
        }

        public async Task BroadcastStatusAsync(string status) {
            try {
                if (!string.IsNullOrEmpty(status) && _hubContext != null) {
                    await _hubContext.Clients.All.SendAsync("ReceiveDialogStatus", status);
                }
            } catch (Exception ex) {
                Logger.Error($"Failed to broadcast dialog status via SignalR: {ex.Message}");
            }
        }

        public async Task ClearDialogAsync(string contentType) {
            try {
                if (_hubContext != null) {
                    // Record the clear intent with a sequence number BEFORE acquiring the lock.
                    // BroadcastDialogAsync compares its own sequence against this value inside the
                    // lock and suppresses the send if the clear sequence is higher (i.e. occurred
                    // after the broadcast was initiated but before it could acquire the lock).
                    long clearSeq = Interlocked.Increment(ref _globalSequence);
                    _clearSequences[contentType] = clearSeq;

                    Task send;
                    await _broadcastLock.WaitAsync();
                    try {
                        _activeDialogs.TryRemove(contentType, out _);
                        send = _hubContext.Clients.All.SendAsync("ClearDialog", contentType);
                    } finally {
                        _broadcastLock.Release();
                    }

                    await send;
                }
            } catch (Exception ex) {
                Logger.Error($"Failed to clear dialog via SignalR: {ex.Message}");
            }
        }
    }
}
