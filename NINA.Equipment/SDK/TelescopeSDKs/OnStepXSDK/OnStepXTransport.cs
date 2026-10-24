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
    /// previous command can never be read as this one's. <see cref="SendBlindNow"/> is the exception: a command without
    /// a reply (a guide pulse) goes out while another command waits for its reply, since the line carries both ways at
    /// once and the controller handles commands in order.
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
        private readonly TimeSpan characterTime;
        private readonly Func<DateTime> utcNow;

        /// <summary>A command and its reply.</summary>
        private readonly object gate = new();

        /// <summary>Only the writing of one command, so commands never interleave on the line.</summary>
        private readonly object writeLock = new();

        /// <summary>When the last byte written so far is on the line (estimated from <see cref="characterTime"/>).</summary>
        private DateTime lineFreeAt = DateTime.MinValue;

        /// <param name="characterTime">How long one character takes on the line; 9600 baud 8N1 when omitted.</param>
        public OnStepXTransport(IOnStepXPort port, TimeSpan? firstByteTimeout = null, TimeSpan? interByteTimeout = null,
            TimeSpan? characterTime = null, Func<DateTime>? utcNow = null) {
            this.port = port ?? throw new ArgumentNullException(nameof(port));
            this.firstByteTimeout = firstByteTimeout ?? DefaultFirstByteTimeout;
            this.interByteTimeout = interByteTimeout ?? DefaultInterByteTimeout;
            this.characterTime = characterTime ?? CharacterTime(OnStepXSerialPort.DefaultBaudRate);
            this.utcNow = utcNow ?? (() => DateTime.UtcNow);
        }

        /// <summary>Start, 8 data and 1 stop bit per character.</summary>
        public static TimeSpan CharacterTime(int baudRate) => TimeSpan.FromMilliseconds(10_000.0 / baudRate);

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
        /// A command answered with '1' or '0': on '0' the controller's reason is read right after with :GE# (last command
        /// error, ProcessCmds.cpp). Writing stays locked from the command to :GE#, so not even a guide pulse
        /// (<see cref="SendBlindNow"/>) can overwrite the error first; pulses wait the few ms. The reason is null for any
        /// other reply.
        /// </summary>
        public (char? Reply, int? Error) SendForCharWithError(string command) {
            lock (gate)
            lock (writeLock) {
                WriteCommand(command);
                int b = port.ReadByte(firstByteTimeout);
                char? reply = b < 0 ? null : (char)b;
                if (reply is not null and not '#') {
                    DrainReply();
                }
                int? error = reply == '0' ? ReadLastError() : null;
                Logger.Trace($"OnStepX: {command} -> {(reply is { } c ? c.ToString() : "timeout")}{(error is { } e ? $", :GE# {e}" : string.Empty)}");
                return (reply, error);
            }
        }

        /// <summary>
        /// A command without a reply that still records an error (OnStepX :hC#), followed at once by :GE# for it, with
        /// writing locked in between as in <see cref="SendForCharWithError"/>; null when :GE# gives no number.
        /// </summary>
        public int? SendBlindThenError(string command) {
            lock (gate)
            lock (writeLock) {
                WriteCommand(command);
                int? error = ReadLastError();
                Logger.Trace($"OnStepX: {command}, :GE# {(error is { } e ? e.ToString(System.Globalization.CultureInfo.InvariantCulture) : "unreadable")}");
                return error;
            }
        }

        /// <summary>
        /// A command without a reply, written at once even while another command waits for its reply; it only waits
        /// for a command being written. Returns when its last character is on the line: the controller acts on it
        /// from then on.
        /// </summary>
        public DateTime SendBlindNow(string command) {
            var onLine = Write(command);
            Logger.Trace($"OnStepX: {command} (on the line {onLine:HH:mm:ss.fff})");
            return onLine;
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
            port.DiscardInBuffer();
            Write(command);
        }

        /// <summary>
        /// Writes one command and returns when its last character is on the line: the port's write returns once the
        /// bytes are queued, and characters queued before them go first.
        /// </summary>
        private DateTime Write(string command) {
            if (string.IsNullOrEmpty(command)) {
                throw new ArgumentException("empty command", nameof(command));
            }

            lock (writeLock) {
                port.Write(command);
                var now = utcNow();
                var start = lineFreeAt > now ? lineFreeAt : now;
                lineFreeAt = start + command.Length * characterTime;
                return lineFreeAt;
            }
        }

        // Caller holds gate. :GE# without clearing the input first: nothing else can be waiting, and the reply to the
        // command before is already read.
        private int? ReadLastError() {
            Write(":GE#");
            var reply = ReadUntilTerminator();
            return reply.Terminated && int.TryParse(reply.Text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int error)
                ? error
                : null;
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
