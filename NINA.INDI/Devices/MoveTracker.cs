#region "copyright"

/*
    Copyright © 2026 Nico Trost <nico.trost57@gmail.com> and the PI.N.S. contributors

    This file is part of PI 'N' Stars.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.INDI.Enums;
using System;

namespace NINA.INDI.Devices {

    /// <summary>
    /// Whether a move commanded through a number property (FILTER_SLOT, ABS_ROTATOR_ANGLE) is still running. A driver
    /// that polls its device sends the property with its old value and state Ok now and then, also between the command
    /// and its own Busy answer; taken for the end of the move, that let a filter change report done at the old slot.
    /// So once a move was commanded, only an update that ends it counts: Alert (refused or failed), or an update that is
    /// not Busy after a Busy one or at the target (a driver that answers a short move without Busy). Without a commanded
    /// move, e.g. one started from the INDI control panel, the property's Busy state is the answer as before.
    /// </summary>
    internal sealed class MoveTracker {
        private readonly Func<double, double, bool> atTarget;
        private readonly object sync = new();
        private bool moving;
        private bool commanded;
        private bool busySeen;
        private double target;

        public MoveTracker(Func<double, double, bool> atTarget) {
            this.atTarget = atTarget;
        }

        public bool IsMoving {
            get {
                lock (sync) {
                    return moving;
                }
            }
        }

        /// <summary>Call before the command is sent, so its Busy answer can't arrive first.</summary>
        public void Commanded(double target) {
            lock (sync) {
                moving = true;
                commanded = true;
                busySeen = false;
                this.target = target;
            }
        }

        /// <summary>The move is over without the driver's answer (halt, timeout, lost connection).</summary>
        public void Ended() {
            lock (sync) {
                moving = false;
                commanded = false;
            }
        }

        public void Updated(PropertyState state, double? value) {
            lock (sync) {
                if (!commanded) {
                    moving = state == PropertyState.Busy;
                    return;
                }

                if (state == PropertyState.Busy) {
                    busySeen = true;
                    moving = true;
                    return;
                }

                if (state == PropertyState.Alert || busySeen || (value is double v && atTarget(v, target))) {
                    moving = false;
                    commanded = false;
                }
            }
        }
    }
}
