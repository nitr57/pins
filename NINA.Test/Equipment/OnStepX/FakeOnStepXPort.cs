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
    /// bare "0", except the commands the firmware never answers (moves, pulses, stops, move and tracking rates). Reads
    /// never wait, a missing byte is an immediate timeout.
    /// </summary>
    internal sealed class FakeOnStepXPort : IOnStepXPort {
        public const string Ack = "\u0006";

        private readonly Dictionary<string, Queue<string?>> replies = new();
        private readonly Queue<char> incoming = new();

        public List<string> Written { get; } = [];

        public string PortName => "/dev/fake";

        public bool IsOpen { get; private set; }

        public int Unread => incoming.Count;

        /// <summary>The reply to a command not scripted; the firmware answers an unknown command with a bare "0".</summary>
        public string? DefaultReply { get; set; } = "0";

        /// <summary>Thrown by every write while set, like a port whose device was unplugged.</summary>
        public Exception? FailWrites { get; set; }

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
            .On(":PbC#", "1#")
            .On(":GA#", "+48*40:00#")
            .On(":GZ#", "000*00:01#")
            .On(":GS#", "22:43:54#")
            .On(":GtH#", "+48*40:00.000#")
            .On(":GgH#", "-008*14:00.000#")
            .On(":GC#", "10/09/26#")
            .On(":GL#", "22:57:00#")
            .On(":GG#", "-02:00#")
            .On(":GX97#", "3.5#")
            .On(":hP#", "1")
            .On(":GX96#", "E#")
            .On(":Gh#", "-10*#")
            .On(":Go#", "85*#")
            .On(":GXE9#", "60#")
            .On(":GXEA#", "40#")
            .On(":GXTR#", "0.00000000#")
            .On(":GXTD#", "0.00000000#")
            .On(":hC#", (string?)null)
            .On(":GE#", "00#");

        public void Open() => IsOpen = true;

        public void Close() => IsOpen = false;

        public void Write(string text) {
            if (!IsOpen) {
                throw new InvalidOperationException("port closed");
            }
            if (FailWrites is { } failure) {
                throw failure;
            }

            Written.Add(text);
            string? reply = IsBlind(text) ? null : DefaultReply;
            if (replies.TryGetValue(text, out var sequence)) {
                reply = sequence.Count > 1 ? sequence.Dequeue() : sequence.Peek();
            }
            if (reply is not null) {
                Preload(reply);
            }
        }

        /// <summary>:Mg pulses, :Mn# moves, :Q stops, :R move rates, :TQ# and the other tracking rates, :hF#: no reply.</summary>
        private static bool IsBlind(string command) =>
            command.StartsWith(":Mg", StringComparison.Ordinal)
            || command is ":Mn#" or ":Ms#" or ":Me#" or ":Mw#" or ":TQ#" or ":TL#" or ":TS#" or ":TK#" or ":hF#"
            || command.StartsWith(":Q", StringComparison.Ordinal)
            || (command.Length == 4 && command.StartsWith(":R", StringComparison.Ordinal) && char.IsDigit(command[2]));

        public int ReadByte(TimeSpan timeout) => incoming.Count > 0 ? incoming.Dequeue() : -1;

        public void DiscardInBuffer() => incoming.Clear();

        public void Dispose() => IsOpen = false;
    }
}
