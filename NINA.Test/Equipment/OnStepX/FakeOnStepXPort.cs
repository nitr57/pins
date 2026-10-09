#region "copyright"

/*
    Copyright © 2026 Nico Trost <nico.trost57@gmail.com> and the PI.N.S. contributors

    This file is part of PI 'N' Stars.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Equipment.SDK.TelescopeSDKs.OnStepXSDK;
using System;
using System.Collections.Generic;

namespace NINA.Test.Equipment.OnStepX {

    /// <summary>
    /// A scripted OnStepX controller. A command not scripted is answered like the firmware answers an unknown one: a
    /// bare "0". Reads never wait, a missing byte is an immediate timeout.
    /// </summary>
    internal sealed class FakeOnStepXPort : IOnStepXPort {
        public const string Ack = "\u0006";

        private readonly Dictionary<string, Queue<string?>> replies = new();
        private readonly Queue<char> incoming = new();

        public List<string> Written { get; } = [];

        public string PortName => "/dev/fake";

        public bool IsOpen { get; private set; }

        public int Unread => incoming.Count;

        /// <summary>The replies to <paramref name="command"/> in order, the last one repeating; null is no reply.</summary>
        public FakeOnStepXPort On(string command, params string?[] replySequence) {
            replies[command] = new Queue<string?>(replySequence);
            return this;
        }

        /// <summary>Bytes already waiting before the next command, e.g. a late reply.</summary>
        public void Preload(string bytes) {
            foreach (char c in bytes) {
                incoming.Enqueue(c);
            }
        }

        /// <summary>A UMi17S as it answered on 2026-10-09: at home, not tracking.</summary>
        public static FakeOnStepXPort Umi17S() => new FakeOnStepXPort()
            .On(Ack, "P")
            .On(":GVP#", "On-Step#")
            .On(":GVN#", "10.20a#")
            .On(":GVD#", "Dec  1 2025#")
            .On(":GVT#", "11:55:45#")
            .On(":GU#", "nNpEW260#")
            .On(":GR#", "04:00:15#")
            .On(":GD#", "+90*00:00#")
            .On(":Gm#", "W#")
            .On(":GX90#", "1.00#")
            .On(":Pbvg#", "UMi17S|1.0.7#")
            .On(":Pbc#", "1#")
            .On(":PbC#", "1#");

        public void Open() => IsOpen = true;

        public void Close() => IsOpen = false;

        public void Write(string text) {
            if (!IsOpen) {
                throw new InvalidOperationException("port closed");
            }

            Written.Add(text);
            string? reply = "0";
            if (replies.TryGetValue(text, out var sequence)) {
                reply = sequence.Count > 1 ? sequence.Dequeue() : sequence.Peek();
            }
            if (reply is not null) {
                Preload(reply);
            }
        }

        public int ReadByte(TimeSpan timeout) => incoming.Count > 0 ? incoming.Dequeue() : -1;

        public void DiscardInBuffer() => incoming.Clear();

        public void Dispose() => IsOpen = false;
    }
}
