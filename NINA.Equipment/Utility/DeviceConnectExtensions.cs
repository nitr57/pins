#region "copyright"

/*
    Copyright © 2026 Nico Trost <nico.trost57@gmail.com> and the PI.N.S. contributors

    This file is part of PI 'N' Stars.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Core.Utility;
using NINA.Equipment.Interfaces;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Equipment.Utility {

    /// <summary>
    /// Makes a device connect attempt cancellable from the caller's point of view, even when the
    /// underlying driver ignores the token (blocking ASCOM/Alpaca <c>Connected = true</c>, a hung
    /// INDI handshake, ...).
    /// </summary>
    public static class DeviceConnectExtensions {

        // Cleanup tasks of connect attempts that were abandoned on cancellation, keyed by device Id
        // rather than by instance: the VMs wrap devices in fresh decorators on every attempt
        // (PersistSettingsCameraDecorator, backlash focuser decorators) and a rescan creates new
        // instances, but all of them still drive the same physical device. A new connect for the same
        // Id waits for the abandoned attempt to settle, so the two never run concurrently and the late
        // cleanup cannot tear down the newer connection.
        private static readonly ConcurrentDictionary<string, Task> abandonedConnects = new ConcurrentDictionary<string, Task>(StringComparer.Ordinal);

        /// <summary>
        /// Connects <paramref name="device"/> and returns as soon as either the connect completes or
        /// <paramref name="token"/> is cancelled. On cancellation an <see cref="OperationCanceledException"/>
        /// is thrown immediately; if the driver is still busy, its connect keeps running in the
        /// background and the device is disconnected again if it finishes successfully, so a late
        /// success cannot leave a half-adopted connection behind.
        /// </summary>
        /// <param name="device">The device to connect.</param>
        /// <param name="token">Cancels the connect from the caller's point of view.</param>
        /// <param name="disconnectAbandonedAlways">
        /// When true, an abandoned attempt is disconnected once it settles whatever its outcome (also when it
        /// failed or honoured the token), for devices whose connect leaves partial state that only
        /// <see cref="IDevice.Disconnect"/> cleans up (e.g. guiders starting an external process). By default only a
        /// late success is torn down.
        /// </param>
        public static async Task<bool> ConnectWithCancellation(this IDevice device, CancellationToken token, bool disconnectAbandonedAlways = false) {
            ArgumentNullException.ThrowIfNull(device);

            var key = GetKey(device);
            if (key != null && abandonedConnects.TryGetValue(key, out var previous) && !previous.IsCompleted) {
                Logger.Info($"{device.Name} - Waiting for a previously cancelled connect attempt to finish");
                await previous.WaitAsync(token).ConfigureAwait(false);
            }

            // Do not start a real connect (which a token-ignoring driver would run to completion
            // and then have to be torn down again) when the caller already gave up.
            token.ThrowIfCancellationRequested();

            // Run the driver's Connect off the caller's thread: some drivers block synchronously before they
            // hand back a Task (native SDK or serial-port init), which would otherwise make the cancel wait
            // for them, too.
            var connectTask = Task.Run(() => device.Connect(token));
            try {
                return await connectTask.WaitAsync(token).ConfigureAwait(false);
            } catch (OperationCanceledException) when (token.IsCancellationRequested) {
                // Also covers the race where the connect completed at the same moment the token
                // fired: the caller treats the attempt as cancelled, so a connection that did come
                // up must be torn down rather than leaked. ContinueWith runs immediately then.
                if (!connectTask.IsCompleted) {
                    Logger.Warning($"{device.Name} - Connect cancelled while the driver is still busy. Abandoning the attempt; the device will be disconnected if it connects late");
                }
                var cleanup = connectTask.ContinueWith(
                    t => CleanupAbandonedConnect(device, t, disconnectAbandonedAlways),
                    CancellationToken.None,
                    TaskContinuationOptions.None,
                    TaskScheduler.Default);
                if (key != null) {
                    abandonedConnects[key] = cleanup;
                    _ = cleanup.ContinueWith(
                        _ => abandonedConnects.TryRemove(new KeyValuePair<string, Task>(key, cleanup)),
                        CancellationToken.None,
                        TaskContinuationOptions.None,
                        TaskScheduler.Default);
                }
                throw;
            }
        }

        private static void CleanupAbandonedConnect(IDevice device, Task<bool> connectTask, bool disconnectAlways) {
            try {
                if (connectTask.IsFaulted) {
                    Logger.Debug($"{device.Name} - Abandoned connect attempt failed: {connectTask.Exception?.GetBaseException().Message}");
                }

                // Only a connect that actually reported success is torn down. A failed or cancelled
                // connect already cleaned up after itself (the device wrappers disconnect on error),
                // and probing Connected here could observe - and kill - a different connection.
                if (connectTask.IsCompletedSuccessfully && connectTask.Result) {
                    Logger.Info($"{device.Name} - Abandoned connect attempt completed after cancellation. Disconnecting");
                    device.Disconnect();
                } else if (disconnectAlways) {
                    Logger.Info($"{device.Name} - Abandoned connect attempt finished without connecting. Disconnecting to clean up partial state");
                    device.Disconnect();
                } else {
                    Logger.Info($"{device.Name} - Abandoned connect attempt finished without connecting");
                }
            } catch (Exception ex) {
                Logger.Error($"{device.Name} - Failed to clean up abandoned connect attempt", ex);
            }
        }

        private static string GetKey(IDevice device) {
            try {
                var id = device.Id;
                return string.IsNullOrEmpty(id) ? null : id;
            } catch {
                return null;
            }
        }
    }
}
