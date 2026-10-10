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
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Test.Equipment.OnStepX {

    [TestFixture]
    public class OnStepXTransportTest {

        private static OnStepXTransport Open(FakeOnStepXPort port) {
            var transport = new OnStepXTransport(port);
            transport.Open();
            return transport;
        }

        [Test]
        public void SendForString_ReturnsTheReplyWithoutTheTerminator() {
            var port = FakeOnStepXPort.Umi17S();

            var reply = Open(port).SendForString(":GVN#");

            Assert.That(reply.Text, Is.EqualTo("10.20a"));
            Assert.That(reply.Terminated, Is.True);
            Assert.That(port.Written, Is.EqualTo(new[] { ":GVN#" }));
        }

        [Test]
        public void SendForString_UnknownCommand_ReturnsTheBareZeroUnterminated() {
            var port = FakeOnStepXPort.Umi17S();

            var reply = Open(port).SendForString(":GXZZ#");

            Assert.That(reply.Text, Is.EqualTo("0"));
            Assert.That(reply.Terminated, Is.False);
        }

        [Test]
        public void SendForString_NoReply_IsEmpty() {
            var port = new FakeOnStepXPort().On(":GU#", (string?)null);

            var reply = Open(port).SendForString(":GU#");

            Assert.That(reply.IsEmpty, Is.True);
            Assert.That(reply.Terminated, Is.False);
        }

        [Test]
        public void SendForString_DiscardsALateReplyToTheCommandBefore() {
            var port = FakeOnStepXPort.Umi17S();
            var transport = Open(port);
            port.Preload("W#");

            var reply = transport.SendForString(":GU#");

            Assert.That(reply.Text, Is.EqualTo("nNpEW260"));
        }

        [Test]
        public void SendForString_CutsOffAnOverlongReply() {
            var port = new FakeOnStepXPort().On(":GU#", new string('x', 100) + "#");

            var reply = Open(port).SendForString(":GU#");

            Assert.That(reply.Text, Has.Length.EqualTo(OnStepXTransport.MaxReplyLength));
            Assert.That(reply.Terminated, Is.False);
        }

        [Test]
        public void SendForChar_ReadsTheFirstCharacterAndTheRestOfTheReplyAway() {
            var port = FakeOnStepXPort.Umi17S();

            char? reply = Open(port).SendForChar(":GVP#");

            Assert.That(reply, Is.EqualTo('O'));
            Assert.That(port.Unread, Is.Zero, "the rest of \"On-Step#\" would otherwise be read as the next reply");
        }

        [Test]
        public void SendForChar_NoReply_IsNull() {
            var port = new FakeOnStepXPort().On(FakeOnStepXPort.Ack, (string?)null);

            Assert.That(Open(port).SendForChar(FakeOnStepXPort.Ack), Is.Null);
        }

        [Test]
        public void SendBlind_WritesTheCommandAndReadsNothing() {
            var port = new FakeOnStepXPort().On(":Q#", "x");

            Open(port).SendBlind(":Q#");

            Assert.That(port.Written, Is.EqualTo(new[] { ":Q#" }));
            Assert.That(port.Unread, Is.EqualTo(1));
        }

        [Test]
        public void SendBlindNow_GoesOutWhileAReplyIsAwaited() {
            var port = new BlockingPort();
            var transport = new OnStepXTransport(port, firstByteTimeout: TimeSpan.FromSeconds(5));
            transport.Open();
            var read = Task.Run(() => transport.SendForString(":GR#"));
            Assert.That(SpinWait.SpinUntil(() => port.Written.Contains(":GR#"), 2000), Is.True);

            var pulse = Task.Run(() => transport.SendBlindNow(":Mgn0100#"));

            Assert.That(pulse.Wait(1000), Is.True, "the pulse waited for the reply to :GR#");
            Assert.That(read.IsCompleted, Is.False);
            port.Reply("04:00:15#");
            Assert.That(read.Result.Text, Is.EqualTo("04:00:15"));
        }

        [Test]
        public void SendBlindNow_ReturnsWhenTheCommandIsOnTheLine() {
            // 1 ms per character; the pulse queues behind the 4 characters of :GU#
            var now = new DateTime(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc);
            var port = FakeOnStepXPort.Umi17S();
            var transport = new OnStepXTransport(port, characterTime: TimeSpan.FromMilliseconds(1), utcNow: () => now);
            transport.Open();

            transport.SendForString(":GU#");
            var onLine = transport.SendBlindNow(":Mgn0100#");

            Assert.That(onLine, Is.EqualTo(now + TimeSpan.FromMilliseconds(4 + 9)));
        }

        [Test]
        public void CharacterTime_9600Baud() {
            Assert.That(OnStepXTransport.CharacterTime(9600).TotalMilliseconds, Is.EqualTo(1.0417).Within(0.0001));
        }

        [Test]
        public void Send_OnAClosedPort_Throws() {
            var transport = new OnStepXTransport(FakeOnStepXPort.Umi17S());

            Assert.That(() => transport.SendForString(":GU#"), Throws.InvalidOperationException);
        }

        /// <summary>A port whose reply arrives only when the test sends it.</summary>
        private sealed class BlockingPort : IOnStepXPort {
            private readonly BlockingCollection<char> incoming = new();

            public ConcurrentQueue<string> Written { get; } = new();

            public string PortName => "/dev/blocking";

            public bool IsOpen { get; private set; }

            public void Reply(string text) {
                foreach (char c in text) {
                    incoming.Add(c);
                }
            }

            public void Open() => IsOpen = true;

            public void Close() => IsOpen = false;

            public void Write(string text) => Written.Enqueue(text);

            public int ReadByte(TimeSpan timeout) => incoming.TryTake(out char c, timeout) ? c : -1;

            public void DiscardInBuffer() {
                while (incoming.TryTake(out _)) {
                }
            }

            public void Dispose() => IsOpen = false;
        }
    }
}
