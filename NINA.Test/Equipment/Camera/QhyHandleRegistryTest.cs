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
using QHYCCD;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Test.Equipment.Camera {

    /// <summary>
    /// The QHY SDK keeps one handle per camera id, shared by the devices on that camera (camera and CFW), and is
    /// released only once no handle is open, so a QHY imaging camera and a QHY guide camera can be used together.
    /// </summary>
    [TestFixture]
    public class QhyHandleRegistryTest {
        private const string Main = "QHY268M-1234";
        private const string Guide = "QHY5III462C-5678";

        private FakeNative native = null!;
        private QhyHandleRegistry registry = null!;

        [SetUp]
        public void SetUp() {
            native = new FakeNative();
            registry = new QhyHandleRegistry(native);
        }

        private QhySdk Sdk() {
            return new QhySdk(registry);
        }

        [Test]
        public void TwoCameras_GetTheirOwnHandles() {
            var main = Sdk();
            var guide = Sdk();

            main.Open(Main);
            guide.Open(Guide);

            Assert.That(native.Opened, Is.EqualTo(new[] { Main, Guide }));
            Assert.That(main.OpenedShared, Is.False);
            Assert.That(guide.OpenedShared, Is.False);
        }

        [Test]
        public void ACameraAndItsFilterWheel_ShareOneHandle() {
            var camera = Sdk();
            var filterWheel = Sdk();

            camera.Open(Main);
            filterWheel.Open(Main);

            Assert.That(native.Opened, Has.Count.EqualTo(1));
            Assert.That(filterWheel.OpenedShared, Is.True);

            camera.Close();
            Assert.That(native.Closed, Is.Empty, "the filter wheel still uses the handle");

            filterWheel.Close();
            Assert.That(native.Closed, Is.EqualTo(new[] { native.HandleOf(Main) }));
        }

        [Test]
        public void ClosingOneCamera_KeepsTheSdkForTheOther() {
            var main = Sdk();
            var guide = Sdk();
            main.InitSdk();
            guide.InitSdk();
            main.Open(Main);
            guide.Open(Guide);

            main.Close();
            main.ReleaseSdk();

            Assert.That(native.Releases, Is.Zero);
            Assert.That(registry.IsInitialized, Is.True);

            guide.Close();
            guide.ReleaseSdk();

            Assert.That(native.Releases, Is.EqualTo(1));
            Assert.That(native.Inits, Is.EqualTo(1));
        }

        [Test]
        public void Open_InitializesAndScansTheSdk_AfterItWasReleased() {
            var camera = Sdk();
            camera.Open(Main);
            camera.Close();
            camera.ReleaseSdk();
            var scansBefore = native.Scans;

            camera.Open(Main);

            Assert.That(native.Inits, Is.EqualTo(2));
            Assert.That(native.Scans, Is.EqualTo(scansBefore + 1), "libqhyccd only knows a camera after a scan");
        }

        [Test]
        public void OpeningAnotherCamera_OnAnInstanceInUse_IsRefused() {
            var sdk = Sdk();
            sdk.Open(Main);

            Assert.Throws<QhySdk.QHYCameraException>(() => sdk.Open(Guide));
            Assert.DoesNotThrow(() => sdk.Open(Main));
            Assert.That(native.Opened, Has.Count.EqualTo(1));
        }

        [Test]
        public void Close_IsIdempotent() {
            var sdk = Sdk();
            sdk.Open(Main);

            sdk.Close();
            sdk.Close();

            Assert.That(native.Closed, Has.Count.EqualTo(1));
        }

        [Test]
        public void ACameraThatFailsToOpen_LeavesNothingOpen() {
            native.Unopenable.Add(Guide);
            var sdk = Sdk();

            Assert.Throws<QhySdk.QHYCameraException>(() => sdk.Open(Guide));

            sdk.ReleaseSdk();
            Assert.That(native.Releases, Is.EqualTo(1));
        }

        [Test]
        public async Task ClosingACamera_DoesNotBlockTheOtherCamera_WhileItsHandleIsBusy() {
            var main = Sdk();
            var guide = Sdk();
            main.Open(Main);
            var mainEntry = registry.Acquire(Main, out _);
            var busy = new ManualResetEventSlim();
            var finishReadout = new ManualResetEventSlim();

            // Simulates a readout holding the main camera's handle.
            var readout = Task.Run(() => {
                lock (mainEntry.Lock) {
                    busy.Set();
                    finishReadout.Wait();
                }
            });
            busy.Wait();

            registry.Return(mainEntry);
            var closing = Task.Run(main.Close);
            await Task.Delay(100);

            guide.Open(Guide);
            Assert.That(guide.Scan(), Is.EqualTo(2), "scan and open of the other camera don't wait for the readout");
            Assert.That(closing.IsCompleted, Is.False);

            finishReadout.Set();
            await readout;
            await closing;
            Assert.That(native.Closed, Is.EqualTo(new[] { native.HandleOf(Main) }));
        }

        [Test]
        public async Task ReopeningACamera_WaitsUntilItsOldHandleIsClosed() {
            var first = Sdk();
            first.Open(Main);
            native.BlockClose.Reset();

            var closing = Task.Run(first.Close);
            await WaitUntil(() => native.CloseRequested);

            var second = Sdk();
            var reopening = Task.Run(() => second.Open(Main));
            await Task.Delay(100);
            Assert.That(reopening.IsCompleted, Is.False, "the camera is opened again only after its old handle is closed");

            native.BlockClose.Set();
            await closing;
            await reopening;
            Assert.That(native.Opened, Is.EqualTo(new[] { Main, Main }));
        }

        private static async Task WaitUntil(Func<bool> condition) {
            var deadline = DateTime.UtcNow.AddSeconds(10);

            while (!condition()) {
                Assert.That(DateTime.UtcNow, Is.LessThan(deadline), "timed out");
                await Task.Delay(5);
            }
        }

        private sealed class FakeNative : QhyHandleRegistry.INative {
            private readonly Dictionary<string, IntPtr> handles = new();
            private int next = 0x1000;

            public List<string> Opened { get; } = new();

            public List<IntPtr> Closed { get; } = new();

            public HashSet<string> Unopenable { get; } = new();

            public ManualResetEventSlim BlockClose { get; } = new(true);

            public volatile bool CloseRequested;

            public int Inits { get; private set; }

            public int Releases { get; private set; }

            public int Scans { get; private set; }

            public IntPtr HandleOf(string id) {
                return handles[id];
            }

            public uint InitResource() {
                Inits++;
                return QhySdk.QHYCCD_SUCCESS;
            }

            public uint ReleaseResource() {
                Releases++;
                return QhySdk.QHYCCD_SUCCESS;
            }

            public uint Scan() {
                Scans++;
                return 2;
            }

            public IntPtr Open(string id) {
                if (Unopenable.Contains(id)) {
                    return IntPtr.Zero;
                }

                Opened.Add(id);
                handles[id] = new IntPtr(next++);
                return handles[id];
            }

            public uint Close(IntPtr handle) {
                CloseRequested = true;
                BlockClose.Wait();

                lock (Closed) {
                    Closed.Add(handle);
                }

                return QhySdk.QHYCCD_SUCCESS;
            }
        }
    }
}
