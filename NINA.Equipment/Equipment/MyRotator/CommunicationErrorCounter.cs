#region "copyright"

/*
    Copyright © 2026 Nico Trost <nico.trost57@gmail.com> and the PI.N.S. contributors

    This file is part of PI 'N' Stars.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System;
using System.Collections.Generic;

namespace NINA.Equipment.Equipment.MyRotator {

    /// <summary>
    /// Decides when communication errors mean the device is lost: after <c>maxErrors</c> within <c>window</c>, with
    /// no successful read in between. Errors less than <c>coalesce</c> after the last counted one count as that one,
    /// so the several status reads of one poll that fail together during a single hang are one error.
    /// </summary>
    internal sealed class CommunicationErrorCounter {
        private readonly int maxErrors;
        private readonly TimeSpan window;
        private readonly TimeSpan coalesce;
        private readonly Queue<DateTime> errors = new();
        private readonly object gate = new();
        private DateTime lastCounted;

        public CommunicationErrorCounter(int maxErrors, TimeSpan window, TimeSpan coalesce) {
            this.maxErrors = maxErrors;
            this.window = window;
            this.coalesce = coalesce;
        }

        public TimeSpan Window => window;

        public int MaxErrors => maxErrors;

        /// <summary>
        /// Records an error at <paramref name="now"/>. Returns true while the connection is kept; false once the
        /// device counts as lost, which starts the count over. <paramref name="count"/> is the number of errors
        /// counted within the window, this one included.
        /// </summary>
        public bool RecordError(DateTime now, out int count) {
            lock (gate) {
                while (errors.Count > 0 && now - errors.Peek() > window) {
                    errors.Dequeue();
                }

                if (errors.Count == 0 || now - lastCounted >= coalesce) {
                    errors.Enqueue(now);
                    lastCounted = now;
                }

                count = errors.Count;
                if (count < maxErrors) {
                    return true;
                }

                errors.Clear();
                return false;
            }
        }

        /// <summary>
        /// A read that worked: the errors before it were transient.
        /// </summary>
        public void RecordSuccess() {
            lock (gate) {
                errors.Clear();
            }
        }
    }
}
