#region "copyright"

/*
    Copyright © 2026 Nico Trost <nico.trost57@gmail.com> and the PI.N.S. contributors

    This file is part of PI 'N' Stars.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

#nullable enable

using NINA.Core.Utility;
using System;
using System.Text;

namespace NINA.Equipment.SDK.TelescopeSDKs.OnStepXSDK {

    /// <summary>
    /// A reply read up to the '#' terminator, without it. <see cref="Terminated"/> is false when no '#' arrived: an
    /// empty reply is a timeout, a single character is OnStepX's bare error or "unsupported" answer (an unknown
    /// command is answered with "0" and no '#').
    /// </summary>
    public readonly record struct OnStepXReply(string Text, bool Terminated) {
        public bool IsEmpty => Text.Length == 0;

        public override string ToString() => Terminated ? Text + "#" : Text;
    }

    /// <summary>
    /// One command at a time to an OnStepX controller, with the reply types INDI's LX200_OnStep uses: none, a single
    /// character, or text up to '#'. Like INDI, every command starts from an empty input buffer, so a late reply to the
    /// previous command can never be read as this one's.
    /// </summary>
    public sealed class OnStepXTransport : IDisposable {

        /// <summary>
        /// Wait for the first byte of a reply. INDI allows 100 ms on serial; the UMi answers within 25 ms, and the
        /// longer wait only costs time when the mount is not answering at all.
        /// </summary>
        public static readonly TimeSpan DefaultFirstByteTimeout = TimeSpan.FromSeconds(1);

        /// <summary>Wait between the bytes of a reply, INDI's serial timeout. Also how long a bare "0" takes to read.</summary>
        public static readonly TimeSpan DefaultInterByteTimeout = TimeSpan.FromMilliseconds(100);

        /// <summary>INDI's RB_MAX_LEN: a longer reply is cut off.</summary>
        public const int MaxReplyLength = 64;

        /// <summary>
        /// After the character a single-character read wants, the rest of a longer reply (e.g. "n-Step#" after the
        /// 'O' of :GVP#) is read away until '#' or this long without a byte: it is still on its way when the next
        /// command clears the input buffer, and would be read as that command's reply.
        /// </summary>
        public static readonly TimeSpan DrainQuietTime = TimeSpan.FromMilliseconds(20);

        private readonly IOnStepXPort port;
        private readonly TimeSpan firstByteTimeout;
        private readonly TimeSpan interByteTimeout;
        private readonly object gate = new();

        public OnStepXTransport(IOnStepXPort port, TimeSpan? firstByteTimeout = null, TimeSpan? interByteTimeout = null) {
            this.port = port ?? throw new ArgumentNullException(nameof(port));
            this.firstByteTimeout = firstByteTimeout ?? DefaultFirstByteTimeout;
            this.interByteTimeout = interByteTimeout ?? DefaultInterByteTimeout;
        }

        public string PortName => port.PortName;

        public bool IsOpen => port.IsOpen;

        public void Open() {
            lock (gate) {
                if (!port.IsOpen) {
                    port.Open();
                }
            }
        }

        public void Close() {
            lock (gate) {
                if (port.IsOpen) {
                    port.Close();
                }
            }
        }

        /// <summary>A command without a reply (INDI sendOnStepCommandBlind).</summary>
        public void SendBlind(string command) {
            lock (gate) {
                WriteCommand(command);
                Logger.Trace($"OnStepX: {command}");
            }
        }

        /// <summary>
        /// A command answered with one character (INDI getCommandSingleCharResponse, sendOnStepCommand); null when
        /// nothing arrived. The rest of a longer reply is read away (<see cref="DrainQuietTime"/>).
        /// </summary>
        public char? SendForChar(string command) {
            lock (gate) {
                WriteCommand(command);
                int b = port.ReadByte(firstByteTimeout);
                char? reply = b < 0 ? null : (char)b;
                if (reply is not null and not '#') {
                    DrainReply();
                }
                Logger.Trace($"OnStepX: {command} -> {(reply is { } c ? c.ToString() : "timeout")}");
                return reply;
            }
        }

        /// <summary>
        /// A command answered with text up to '#' (INDI getCommandDoubleResponse, getCommandIntResponse,
        /// getCommandSingleCharErrorOrLongResponse).
        /// </summary>
        public OnStepXReply SendForString(string command) {
            lock (gate) {
                WriteCommand(command);
                var reply = ReadUntilTerminator();
                Logger.Trace($"OnStepX: {command} -> {(reply.IsEmpty ? "timeout" : reply.ToString())}");
                return reply;
            }
        }

        public void Dispose() {
            lock (gate) {
                port.Dispose();
            }
        }

        // Caller holds gate.
        private void WriteCommand(string command) {
            if (string.IsNullOrEmpty(command)) {
                throw new ArgumentException("empty command", nameof(command));
            }

            port.DiscardInBuffer();
            port.Write(command);
        }

        // Caller holds gate.
        private void DrainReply() {
            for (int i = 0; i < MaxReplyLength; i++) {
                int b = port.ReadByte(DrainQuietTime);
                if (b is < 0 or '#') {
                    return;
                }
            }
        }

        // Caller holds gate.
        private OnStepXReply ReadUntilTerminator() {
            var text = new StringBuilder();
            var timeout = firstByteTimeout;
            while (text.Length < MaxReplyLength) {
                int b = port.ReadByte(timeout);
                if (b < 0) {
                    return new OnStepXReply(text.ToString(), false);
                }

                if (b == '#') {
                    return new OnStepXReply(text.ToString(), true);
                }

                text.Append((char)b);
                timeout = interByteTimeout;
            }

            Logger.Warning($"OnStepX: reply longer than {MaxReplyLength} characters, cut off: {text}");
            return new OnStepXReply(text.ToString(), false);
        }
    }
}
