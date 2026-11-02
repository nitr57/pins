#region "copyright"

/*
    Copyright © 2026 Nico Trost <nico.trost57@gmail.com> and the PI.N.S. contributors

    This file is part of PI 'N' Stars.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Microsoft.AspNetCore.SignalR;
using Moq;
using NINA.Core.MyMessageBox;
using NINA.Core.SignalR;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace NINA.Test.SystemWindowsCompat {

    /// <summary>
    /// Message boxes sent to Touch-N-Stars: a prompt that must wait for the user (a manual filter change, covering the
    /// scope for dark flats) waits without a time limit and is ended from outside by a token, and every client closes a
    /// box once it has ended, not only the one that answered. MyMessageBoxBroadcaster.Instance is static: the fixture
    /// puts the previous one back.
    /// </summary>
    [TestFixture]
    [NonParallelizable]
    public class MyMessageBoxBroadcastTest {
        private static readonly FieldInfo InstanceField =
            typeof(MyMessageBoxBroadcaster).GetField("_instance", BindingFlags.NonPublic | BindingFlags.Static)!;

        private readonly List<(string Method, object[] Args)> sent = [];
        private object? previousInstance;
        private MyMessageBoxBroadcaster broadcaster;

        [SetUp]
        public void SetUp() {
            previousInstance = InstanceField.GetValue(null);
            lock (sent) {
                sent.Clear();
            }

            var proxy = new Mock<IClientProxy>();
            proxy.Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
                .Callback<string, object[], CancellationToken>((method, args, _) => {
                    lock (sent) {
                        sent.Add((method, args));
                    }
                })
                .Returns(Task.CompletedTask);
            var clients = new Mock<IHubClients>();
            clients.Setup(c => c.All).Returns(proxy.Object);
            var hub = new Mock<IHubContext<MyMessageBoxHub>>();
            hub.Setup(h => h.Clients).Returns(clients.Object);
            broadcaster = new MyMessageBoxBroadcaster(hub.Object);
        }

        [TearDown]
        public void TearDown() {
            InstanceField.SetValue(null, previousInstance);
        }

        [Test]
        public async Task ATokenEndsTheBox_WithTheDefaultResult_AndEveryClientClosesIt() {
            using var cts = new CancellationTokenSource();
            var box = broadcaster.ShowMessageBoxAsync("Change to filter L", "Filter change", MessageBoxButton.OK, MessageBoxResult.OK, Timeout.InfiniteTimeSpan, cts.Token);
            var id = await SentBoxId();

            cts.Cancel();

            Assert.That(await box.WaitAsync(TimeSpan.FromSeconds(5)), Is.EqualTo(MessageBoxResult.OK));
            Assert.That(await Closed(), Is.EqualTo((id, "OK")));
            Assert.That(MyMessageBoxBroadcaster.GetActiveMessageBoxes(), Is.Empty, "a client connecting now would get the box again");
        }

        [Test]
        public async Task WithoutATimeLimit_TheBoxWaitsForTheAnswer() {
            var box = broadcaster.ShowMessageBoxAsync("Cover the scope", "Dark flats", MessageBoxButton.OKCancel, MessageBoxResult.OK, Timeout.InfiniteTimeSpan, CancellationToken.None);
            var id = await SentBoxId();

            await Task.Delay(300);
            Assert.That(box.IsCompleted, Is.False);

            await broadcaster.HandleMessageBoxResponseAsync(id, "Cancel");

            Assert.That(await box.WaitAsync(TimeSpan.FromSeconds(5)), Is.EqualTo(MessageBoxResult.Cancel));
            Assert.That(await Closed(), Is.EqualTo((id, "Cancel")));
        }

        [Test]
        public async Task ABoxThatTimesOut_IsClosedOnEveryClient() {
            var box = broadcaster.ShowMessageBoxAsync("Question", "Title", MessageBoxButton.YesNo, MessageBoxResult.No, TimeSpan.FromMilliseconds(200));
            var id = await SentBoxId();

            Assert.That(await box.WaitAsync(TimeSpan.FromSeconds(5)), Is.EqualTo(MessageBoxResult.No));
            Assert.That(await Closed(), Is.EqualTo((id, "No")));
        }

        [Test]
        public async Task AnAlreadyEndedToken_ShowsNothing() {
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var result = await broadcaster.ShowMessageBoxAsync("Change to filter L", "Filter change", MessageBoxButton.OK, MessageBoxResult.OK, Timeout.InfiniteTimeSpan, cts.Token);

            Assert.That(result, Is.EqualTo(MessageBoxResult.OK));
            Assert.That(Sent(), Is.Empty);
        }

        private List<(string Method, object[] Args)> Sent() {
            lock (sent) {
                return [.. sent];
            }
        }

        private async Task<string> SentBoxId() {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline) {
                var box = Sent().FirstOrDefault(s => s.Method == "ReceiveMessageBox");
                if (box.Method != null) {
                    return ((MyMessageBoxMessage)box.Args[0]).Id;
                }

                await Task.Delay(20);
            }

            throw new AssertionException("the box was not sent");
        }

        private async Task<(string Id, string Result)> Closed() {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline) {
                var close = Sent().FirstOrDefault(s => s.Method == "CloseMessageBox");
                if (close.Method != null) {
                    return ((string)close.Args[0], (string)close.Args[1]);
                }

                await Task.Delay(20);
            }

            throw new AssertionException("the close was not sent");
        }
    }
}
