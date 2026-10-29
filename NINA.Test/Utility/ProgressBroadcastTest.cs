#region "copyright"

/*
    Copyright © 2026 Nico Trost <nico.trost57@gmail.com> and the PI.N.S. contributors

    This file is part of PI 'N' Stars.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Core.Model;
using NINA.Core.SignalR;
using NINA.Core.Utility;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace NINA.Test.Utility {

    /// <summary>
    /// The progress messages Touch-N-Stars gets: in the order they were published, also when a send is slow, and with the
    /// last update of a throttled burst sent after the throttle interval instead of dropped.
    /// </summary>
    [TestFixture]
    [NonParallelizable]
    public class ProgressBroadcastTest {
        private readonly List<ProgressMessage> received = [];
        private Progress.ProgressBroadcaster previous = null!;
        private string source = null!;

        [SetUp]
        public void SetUp() {
            lock (received) {
                received.Clear();
            }

            previous = Progress.Broadcaster;
            // one source per test: the throttle state is static
            source = "Test " + Guid.NewGuid().ToString("N");
        }

        [TearDown]
        public void TearDown() {
            Progress.Broadcaster = previous;
        }

        private void Capture(Func<ProgressMessage, int> delayMs) {
            Progress.Broadcaster = async message => {
                await Task.Delay(delayMs(message));
                lock (received) {
                    received.Add(message);
                }
            };
        }

        private List<ProgressMessage> Received() {
            lock (received) {
                return received.Where(m => m.Source == source).ToList();
            }
        }

        private ApplicationStatus Status(string text, double progress = -1) => new() { Source = source, Status = text, Progress = progress };

        [Test]
        public async Task Messages_ArriveInTheOrderTheyWerePublished_AlsoWhenASendIsSlow() {
            // the create's send is slow; sent in parallel, the update and the delete would overtake it
            Capture(m => m.State == "create" ? 200 : 0);

            Progress.PublishNewStatus(Status("Exposing"));
            Progress.PublishUpdateStatus(Status("Downloading"));
            Progress.PublishRemoveStatus(Status(""));
            await Task.Delay(600);

            Assert.That(Received().Select(m => m.State), Is.EqualTo(new[] { "create", "update", "delete" }));
        }

        [Test]
        public async Task TheLastUpdateOfAThrottledBurst_IsSentAfterTheInterval() {
            Capture(_ => 0);

            Progress.PublishUpdateStatus(Status("Saving", 1));
            Progress.PublishUpdateStatus(Status("Saving", 2));
            Progress.PublishUpdateStatus(Status("Saving", 3));
            await Task.Delay(100);
            Assert.That(Received().Select(m => m.Progress), Is.EqualTo(new[] { 1.0 }), "the burst is throttled");

            await Task.Delay(700);

            Assert.That(Received().Select(m => m.Progress), Is.EqualTo(new[] { 1.0, 3.0 }), "the newest held-back update follows");
        }

        [Test]
        public async Task AHeldBackUpdate_NeverFollowsTheDelete() {
            Capture(_ => 0);

            Progress.PublishUpdateStatus(Status("Saving", 1));
            Progress.PublishUpdateStatus(Status("Saving", 2));
            Progress.PublishRemoveStatus(Status(""));
            await Task.Delay(800);

            Assert.That(Received().Select(m => m.State), Is.EqualTo(new[] { "update", "delete" }));
        }
    }
}
