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
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Equipment.Utility {

    /// <summary>
    /// Makes a device connect attempt cancellable from the caller's point of view, even when the
    /// underlying driver ignores the token (blocking ASCOM/Alpaca <c>Connected = true</c>, a hung
    /// INDI handshake, ...).
    /// </summary>
    public static class DeviceConnectExtensions {

        // Cleanup tasks of connect attempts that were abandoned on cancellation, keyed by device
        // instance. A new connect on the same instance waits for the previous attempt to settle so
        // the two never run concurrently on one driver.
        private static readonly ConditionalWeakTable<IDevice, Task> abandonedConnects = new ConditionalWeakTable<IDevice, Task>();

        /// <summary>
        /// Connects <paramref name="device"/> and returns as soon as either the connect completes or
        /// <paramref name="token"/> is cancelled. On cancellation an <see cref="OperationCanceledException"/>
        /// is thrown immediately; if the driver is still busy, its connect keeps running in the
        /// background and the device is disconnected again once it finishes, so a late success
        /// cannot leave a half-adopted connection behind.
        /// </summary>
        public static async Task<bool> ConnectWithCancellation(this IDevice device, CancellationToken token) {
            ArgumentNullException.ThrowIfNull(device);

            if (abandonedConnects.TryGetValue(device, out var previous) && !previous.IsCompleted) {
                Logger.Info($"{device.Name} - Waiting for a previously cancelled connect attempt to finish");
                await previous.WaitAsync(token).ConfigureAwait(false);
            }

            var connectTask = device.Connect(token);
            try {
                return await connectTask.WaitAsync(token).ConfigureAwait(false);
            } catch (OperationCanceledException) when (token.IsCancellationRequested && !connectTask.IsCompleted) {
                Logger.Warning($"{device.Name} - Connect cancelled while the driver is still busy. Abandoning the attempt; the device will be disconnected if it connects late");
                var cleanup = connectTask.ContinueWith(
                    t => CleanupAbandonedConnect(device, t),
                    CancellationToken.None,
                    TaskContinuationOptions.None,
                    TaskScheduler.Default);
                abandonedConnects.AddOrUpdate(device, cleanup);
                throw;
            }
        }

        private static void CleanupAbandonedConnect(IDevice device, Task<bool> connectTask) {
            try {
                if (connectTask.IsFaulted) {
                    Logger.Debug($"{device.Name} - Abandoned connect attempt failed: {connectTask.Exception?.GetBaseException().Message}");
                }

                var lateSuccess = connectTask.IsCompletedSuccessfully && connectTask.Result;
                if (lateSuccess || IsConnectedSafe(device)) {
                    Logger.Info($"{device.Name} - Abandoned connect attempt completed after cancellation. Disconnecting");
                    device.Disconnect();
                } else {
                    Logger.Info($"{device.Name} - Abandoned connect attempt finished without connecting");
                }
            } catch (Exception ex) {
                Logger.Error($"{device.Name} - Failed to clean up abandoned connect attempt", ex);
            }
        }

        private static bool IsConnectedSafe(IDevice device) {
            try {
                return device.Connected;
            } catch {
                return false;
            }
        }
    }
}
