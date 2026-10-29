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
using System.Threading;
using ZWOptical.ASISDK;

namespace NINA.Test.Equipment.Camera {

    /// <summary>
    /// With two ZWO cameras in pins (imaging and guide camera), a call for one camera must not wait for a call on the
    /// other, e.g. the guide camera's exposure start for the imaging camera's download; calls on one camera and the
    /// SDK-wide calls still run alone.
    /// </summary>
    [TestFixture]
    [NonParallelizable]
    public class AsiSdkGateTest {
        private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(300);
        private static readonly TimeSpan Long = TimeSpan.FromSeconds(5);

        // Holds a scope on its own thread (the SDK lock is thread-affine) until released
        private sealed class Holder : IDisposable {
            private readonly ManualResetEventSlim held = new();
            private readonly ManualResetEventSlim release = new();
            private readonly Thread thread;

            public Holder(Action<Action> hold) {
                thread = new Thread(() => hold(() => {
                    held.Set();
                    release.Wait();
                })) { IsBackground = true };
                thread.Start();
                Assert.That(held.Wait(Long), Is.True, "the holder got its scope");
            }

            public void Dispose() {
                release.Set();
                thread.Join(Long);
            }
        }

        // Whether the call gets its scope within the timeout while the holder keeps its own
        private static bool Enters(Action<Action> call, TimeSpan timeout) {
            var entered = new ManualResetEventSlim();
            var thread = new Thread(() => call(entered.Set)) { IsBackground = true };
            thread.Start();
            bool result = entered.Wait(timeout);
            return result;
        }

        [Test]
        public void ACallOnAnotherCamera_DoesNotWait() {
            using var downloading = new Holder(inside => {
                using var scope = AsiSdkGate.Camera(1001);
                inside();
            });

            Assert.That(Enters(inside => {
                using var scope = AsiSdkGate.Camera(1002);
                inside();
            }, Long), Is.True);
        }

        [Test]
        public void ACallOnTheSameCamera_Waits() {
            var holder = new Holder(inside => {
                using var scope = AsiSdkGate.Camera(1003);
                inside();
            });
            var entered = new ManualResetEventSlim();
            var other = new Thread(() => {
                using var scope = AsiSdkGate.Camera(1003);
                entered.Set();
            }) { IsBackground = true };
            other.Start();

            Assert.That(entered.Wait(Short), Is.False, "not while the first call runs");
            holder.Dispose();
            Assert.That(entered.Wait(Long), Is.True, "once it is done");
        }

        [Test]
        public void AnSdkWideCall_WaitsForCameraCalls() {
            var holder = new Holder(inside => {
                using var scope = AsiSdkGate.Camera(1004);
                inside();
            });
            var entered = new ManualResetEventSlim();
            var scan = new Thread(() => {
                using var scope = AsiSdkGate.Sdk();
                entered.Set();
            }) { IsBackground = true };
            scan.Start();

            Assert.That(entered.Wait(Short), Is.False, "not while a camera call runs");
            holder.Dispose();
            Assert.That(entered.Wait(Long), Is.True);
        }

        [Test]
        public void ACameraCall_WaitsForAnSdkWideCall() {
            using (new Holder(inside => {
                using var scope = AsiSdkGate.Sdk();
                inside();
            })) {
                Assert.That(Enters(inside => {
                    using var scope = AsiSdkGate.Camera(1005);
                    inside();
                }, Short), Is.False);
            }
        }
    }
}
