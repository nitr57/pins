#region "copyright"

/*
    Copyright © 2026 Nico Trost <nico.trost57@gmail.com> and the PI.N.S. contributors

    This file is part of PI 'N' Stars.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace NINA.Equipment.Utility {

    /// <summary>
    /// Tracks the cancellation sources of all connect calls a device view model currently has in flight,
    /// including calls still queued behind its connect semaphore. A single field would be overwritten by a
    /// queued call, so <see cref="CancelAll"/> would no longer reach the attempt that is actually hanging.
    /// Sources of finished calls are removed (not cancelled), so tokens a driver might keep using after a
    /// successful connect are never cancelled later.
    /// </summary>
    public sealed class PendingConnects {
        private readonly object sync = new object();
        private readonly HashSet<CancellationTokenSource> pending = new HashSet<CancellationTokenSource>();

        /// <summary>Registers a new connect call and returns its cancellation source.</summary>
        public CancellationTokenSource Begin() {
            var cts = new CancellationTokenSource();
            lock (sync) {
                pending.Add(cts);
            }
            return cts;
        }

        /// <summary>Unregisters a finished connect call.</summary>
        public void End(CancellationTokenSource cts) {
            if (cts == null) {
                return;
            }
            lock (sync) {
                pending.Remove(cts);
            }
        }

        /// <summary>Cancels every connect call that is currently in flight or queued.</summary>
        public void CancelAll() {
            CancellationTokenSource[] snapshot;
            lock (sync) {
                snapshot = pending.ToArray();
            }
            foreach (var cts in snapshot) {
                try {
                    cts.Cancel();
                } catch {
                }
            }
        }
    }
}
