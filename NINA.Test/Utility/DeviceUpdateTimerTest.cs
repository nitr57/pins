#region "copyright"

/*
    Copyright (c) 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Core.Utility;
using NUnit.Framework;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Test.Utility {

    [TestFixture]
    public class DeviceUpdateTimerTest {

        /// <summary>
        /// Verifies the factory preserves delegates and interval values when constructing timer instances.
        /// </summary>
        [Test]
        public void Factory_Create_ReturnsTimerWithConfiguredDelegatesAndInterval() {
            DefaultDeviceUpateTimerFactory factory = new DefaultDeviceUpateTimerFactory();
            Dictionary<string, object> values = new Dictionary<string, object> { { "Connected", true } };

            IDeviceUpdateTimer timer = factory.Create(() => values, _ => { }, 1.25, "Camera");

            Assert.That(timer.GetValuesFunc(), Is.SameAs(values));
            Assert.That(timer.Interval, Is.EqualTo(1.25));
        }

        /// <summary>
        /// Verifies stopping a running timer emits the standard disconnected update in the cleanup path.
        /// </summary>
        [Test]
        public async Task RunThenStop_EmitsDisconnectedUpdate() {
            List<Dictionary<string, object>> updates = new List<Dictionary<string, object>>();
            DeviceUpdateTimer timer = new DeviceUpdateTimer(
                () => new Dictionary<string, object> { { "Connected", true }, { "Temperature", -10d } },
                values => {
                    lock (updates) {
                        updates.Add(new Dictionary<string, object>(values));
                    }
                },
                0.01,
                "TestDevice");

            Task runTask = timer.Run();
            Assert.That(SpinWait.SpinUntil(() => {
                lock (updates) {
                    return updates.Any();
                }
            }, System.TimeSpan.FromSeconds(2)), Is.True);

            await timer.Stop();
            await runTask.WaitAsync(System.TimeSpan.FromSeconds(2));

            Dictionary<string, object> lastUpdate;
            lock (updates) {
                lastUpdate = updates.Last();
            }
            Assert.That(lastUpdate, Contains.Key("Connected"));
            Assert.That(lastUpdate["Connected"], Is.EqualTo(false));
        }

        /// <summary>
        /// A poll that read the device before UpdateNow (a focuser position while it still moved) applies its values
        /// before UpdateNow's, never after them: the device's values after UpdateNow are the ones it read.
        /// </summary>
        [Test]
        public async Task UpdateNow_IsNotOverwrittenByAPollThatReadTheDeviceBefore() {
            List<int> applied = new List<int>();
            using ManualResetEventSlim pollReading = new ManualResetEventSlim();
            using ManualResetEventSlim releasePoll = new ManualResetEventSlim();
            int reads = 0;
            DeviceUpdateTimer timer = new DeviceUpdateTimer(
                () => {
                    // The first poll reads the old position and is slow to finish; every later read gets the new one
                    if (Interlocked.Increment(ref reads) == 1) {
                        pollReading.Set();
                        releasePoll.Wait(System.TimeSpan.FromSeconds(5));
                        return new Dictionary<string, object> { { "Position", 1 } };
                    }

                    return new Dictionary<string, object> { { "Position", 2 } };
                },
                values => {
                    if (values.TryGetValue("Position", out var position)) {
                        lock (applied) {
                            applied.Add((int)position);
                        }
                    }
                },
                0.05,
                "TestDevice");

            Task runTask = timer.Run();
            Assert.That(pollReading.Wait(System.TimeSpan.FromSeconds(2)), Is.True, "the first poll is reading");

            Task updateNow = timer.UpdateNow(CancellationToken.None);
            await Task.Delay(200);
            releasePoll.Set();
            await updateNow.WaitAsync(System.TimeSpan.FromSeconds(2));
            Assert.That(SpinWait.SpinUntil(() => {
                lock (applied) {
                    return applied.Contains(1);
                }
            }, System.TimeSpan.FromSeconds(2)), Is.True, "the first poll applied its values");

            await timer.Stop();
            await runTask.WaitAsync(System.TimeSpan.FromSeconds(2));

            List<int> sequence;
            lock (applied) {
                sequence = applied.ToList();
            }
            Assert.That(sequence.IndexOf(1), Is.LessThan(sequence.IndexOf(2)), $"applied in this order: {string.Join(", ", sequence)}");
            Assert.That(sequence.LastIndexOf(1), Is.EqualTo(0), "the old position is never applied after the new one");
        }
    }
}
