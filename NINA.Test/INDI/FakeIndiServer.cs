#region "copyright"

/*
    Copyright © 2026 Nico Trost <nico.trost57@gmail.com> and the PI.N.S. contributors

    This file is part of PI 'N' Stars.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace NINA.Test.INDI {

    /// <summary>
    /// Loopback stand-in for indiserver: accepts connections, records what the client sends and sends raw XML.
    /// Pair it with <c>new INDIClient(server.Port, startServer: false)</c> and <c>INDIClient.SetInstanceForTests</c>.
    /// </summary>
    internal sealed class FakeIndiServer : IDisposable {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly Channel<Connection> accepted = Channel.CreateUnbounded<Connection>();
        private readonly List<Connection> connections = [];
        private readonly CancellationTokenSource cts = new();

        public FakeIndiServer() {
            listener.Start();
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            _ = AcceptLoopAsync();
        }

        public int Port { get; }

        private async Task AcceptLoopAsync() {
            try {
                while (true) {
                    var connection = new Connection(await listener.AcceptTcpClientAsync(cts.Token));
                    lock (connections) {
                        connections.Add(connection);
                    }
                    accepted.Writer.TryWrite(connection);
                }
            } catch (Exception) {
                // stopped
            }
        }

        public async Task<Connection> NextConnectionAsync(TimeSpan timeout) {
            using var timeoutCts = new CancellationTokenSource(timeout);
            return await accepted.Reader.ReadAsync(timeoutCts.Token);
        }

        public void Dispose() {
            cts.Cancel();
            listener.Stop();
            lock (connections) {
                foreach (var connection in connections) {
                    connection.Close();
                }
            }
            cts.Dispose();
        }

        internal sealed class Connection {
            private readonly TcpClient tcp;
            private readonly NetworkStream stream;
            private readonly StringBuilder received = new();
            private readonly TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public Connection(TcpClient tcp) {
                this.tcp = tcp;
                stream = tcp.GetStream();
                _ = ReadLoopAsync();
            }

            /// <summary>Completes once the client has closed this connection (or it was closed here).</summary>
            public Task Closed => closed.Task;

            /// <summary>Everything the client has sent on this connection so far.</summary>
            public string Received {
                get {
                    lock (received) {
                        return received.ToString();
                    }
                }
            }

            private async Task ReadLoopAsync() {
                var buffer = new byte[65536];
                var decoder = Encoding.UTF8.GetDecoder();
                var chars = new char[Encoding.UTF8.GetMaxCharCount(buffer.Length)];
                try {
                    int n;
                    while ((n = await stream.ReadAsync(buffer)) > 0) {
                        // The decoder keeps a multi-byte character split across two reads intact.
                        var count = decoder.GetChars(buffer, 0, n, chars, 0);
                        lock (received) {
                            received.Append(chars, 0, count);
                        }
                    }
                } catch (Exception) {
                    // closed
                }
                closed.TrySetResult();
            }

            public void Send(string xml) {
                var bytes = Encoding.UTF8.GetBytes(xml);
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush();
            }

            /// <summary>Waits until <see cref="Received"/> satisfies <paramref name="condition"/>; fails the test on timeout.</summary>
            public async Task WaitUntilAsync(Func<string, bool> condition, string because, TimeSpan timeout) {
                var deadline = DateTime.UtcNow + timeout;
                while (!condition(Received)) {
                    if (DateTime.UtcNow > deadline) {
                        Assert.Fail($"Timed out waiting until {because}; received: {Received}");
                    }
                    await Task.Delay(10);
                }
            }

            public Task WaitForAsync(string text, TimeSpan timeout) {
                return WaitUntilAsync(r => r.Contains(text, StringComparison.Ordinal), $"the client sent {text}", timeout);
            }

            public void Close() {
                tcp.Close();
            }
        }
    }
}
