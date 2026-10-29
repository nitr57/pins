#region "copyright"

/*
    Copyright © 2025 Nico Trost <nico.trost57@gmail.com> and the PI.N.S. contributors

    This file is part of PI 'N' Stars.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Core.Utility;
using NINA.INDI.Enums;
using NINA.INDI.Interfaces;
using NINA.INDI.Protocol;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.INDI.Devices {

    public class INDIFilterWheel : INDIDevice, IINDIFilterWheel {

        public override void OnTextPropertyUpdated(INDITextProperty p) {
            base.OnTextPropertyUpdated(p);

            switch (p.Name) {
                case "FILTER_NAME":
                    // Filter names updated
                    Logger.Debug($"Filter names updated: {p.Texts.Count} filters");
                    break;
            }
        }

        // MoveToPositionAsync (and the IndiFilterWheel position setter) wait on IsMoving, which only a FILTER_SLOT
        // update that ends the move clears (see MoveTracker)
        protected override void OnConnectionLost() {
            _move.Ended();
        }

        public override void OnNumberPropertyUpdated(INDINumberProperty p) {
            base.OnNumberPropertyUpdated(p);

            switch (p.Name) {
                case "FILTER_SLOT":
                    // The local copy now holds the driver's value, so AbandonMove leaves it alone
                    Interlocked.Increment(ref _slotUpdates);

                    _move.Updated(p.State, p.Numbers.FirstOrDefault(n => n.Name == "FILTER_SLOT_VALUE")?.Value);
                    break;
            }
        }

        public override void OnSwitchPropertyUpdated(INDISwitchProperty p) {
            base.OnSwitchPropertyUpdated(p);
        }

        public override void OnBlobPropertyUpdated(INDIBlobProperty p) {
            base.OnBlobPropertyUpdated(p);
        }





        public INDIFilterWheel(INDIDeviceInfo device) : base(device) {
        }

        /// <summary>
        /// Specify critical properties that must arrive before Connect() completes
        /// </summary>
        protected override string[] GetRequiredConnectionProperties() {
            return ["FILTER_SLOT"];
        }

        // FILTER_SLOT_VALUE counts the slots from 1
        private readonly MoveTracker _move = new((slot, target) => Math.Abs(slot - target) < 0.5);
        public bool IsMoving => _move.IsMoving;

        // SetNumberValue writes the target into the local FILTER_SLOT_VALUE before sending it, so
        // Position reads the target until the driver answers. The slot from before the command and
        // the count of FILTER_SLOT updates at that time let AbandonMove put the old slot back when
        // no answer arrived.
        private int _slotUpdates;
        private int _slotUpdatesAtCommand;
        private double? _slotBeforeCommand;

        public int[] FocusOffsets {
            get {
                var prop = GetNumberProperty("FILTER_FOCUS_OFFSET");
                if (prop == null) return Array.Empty<int>();

                var offsets = new int[prop.Numbers.Count];
                for (int i = 0; i < prop.Numbers.Count; i++) {
                    offsets[i] = (int)prop.Numbers[i].Value;
                }
                return offsets;
            }
        }

        public string[] Names {
            get {
                var prop = GetTextProperty("FILTER_NAME");
                if (prop == null) return Array.Empty<string>();

                var names = new string[prop.Texts.Count];
                for (int i = 0; i < prop.Texts.Count; i++) {
                    names[i] = prop.Texts[i].Value ?? $"Filter {i + 1}";
                }
                return names;
            }
        }

        public int Position {
            get => (int)GetNumberPropertyValue("FILTER_SLOT", "FILTER_SLOT_VALUE").GetValueOrDefault(1) - 1;
            set {
                if (!Connected) {
                    Logger.Warning("Cannot set filter position: not connected");
                    return;
                }

                _slotBeforeCommand = GetNumberPropertyValue("FILTER_SLOT", "FILTER_SLOT_VALUE");
                _slotUpdatesAtCommand = Volatile.Read(ref _slotUpdates);
                _move.Commanded(value + 1);
                SetNumberValue("FILTER_SLOT", "FILTER_SLOT_VALUE", value + 1);
                Logger.Info($"Commanded filter wheel to position {value}");
            }
        }

        public void AbandonMove() {
            _move.Ended();

            // Under the lock the receive loop holds while it writes FILTER_SLOT and counts the update here: an answer
            // that arrives now is either counted before the check or written over the reset, never overwritten by it
            INDIClient.RunUnderPropertyLock(() => {
                if (Volatile.Read(ref _slotUpdates) != _slotUpdatesAtCommand || _slotBeforeCommand is not double slotBefore) {
                    // The driver answered, so the local copy holds its value
                    return;
                }

                var number = GetNumberProperty("FILTER_SLOT")?.Numbers.FirstOrDefault(n => n.Name == "FILTER_SLOT_VALUE");
                if (number == null) {
                    return;
                }

                Logger.Warning($"Filter wheel driver never answered the move to position {(int)number.Value - 1}, position stays at {(int)slotBefore - 1}");
                number.Value = slotBefore;
            });
        }

        // Ceiling for MoveToPositionAsync's poll loop. IsMoving is only ever cleared by a
        // driver update (FILTER_SLOT ending the move), so a driver that stops replying
        // mid-move would otherwise leave the caller polling forever with only its own
        // cancellation token as a way out. Internal so tests can shorten it.
        internal TimeSpan MoveTimeout { get; set; } = TimeSpan.FromMinutes(1);

        // Only the tests drive moves through this; the equipment layer (IndiFilterWheel) sets Position, waits on
        // IsMoving and calls AbandonMove itself
        public async Task MoveToPositionAsync(int position, CancellationToken ct = default) {
            if (!Connected) {
                throw new InvalidOperationException("Cannot move filter wheel: not connected");
            }

            // Initiate the move
            Position = position;

            // Wait for move to complete
            var started = DateTime.UtcNow;
            while (_move.IsMoving && !ct.IsCancellationRequested) {
                if (DateTime.UtcNow - started > MoveTimeout) {
                    AbandonMove();
                    throw new TimeoutException($"Filter wheel did not report move completion within {MoveTimeout.TotalSeconds:F0}s");
                }
                await Task.Delay(100, ct);
            }

            ThrowIfServerConnectionLost("Filter change");

            Logger.Debug($"Filter wheel reached position {Position}");
        }

        // Action/Command* are inherited from INDIDevice — the redeclarations that used to
        // live here hid the base virtuals (CS0114) without changing behavior.
    }
}