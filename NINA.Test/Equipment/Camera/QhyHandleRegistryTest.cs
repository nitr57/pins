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
using System.Linq;
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
        }

        [Test]
        public void ACameraAndItsFilterWheel_ShareOneHandle() {
            var camera = Sdk();
            var filterWheel = Sdk();

            camera.Open(Main);
            filterWheel.Open(Main);

            Assert.That(native.Opened, Has.Count.EqualTo(1));

            camera.Close();
            Assert.That(native.Closed, Is.Empty, "the filter wheel still uses the handle");

            filterWheel.Close();
            Assert.That(native.Closed, Is.EqualTo(new[] { native.HandleOf(Main) }));
        }

        [Test]
        public void InitCameraIfNeeded_SkipsAHandleTheCameraAlreadyInitialized() {
            var camera = Sdk();
            var filterWheel = Sdk();
            camera.Open(Main);
            filterWheel.Open(Main);

            camera.InitCamera();
            filterWheel.InitCameraIfNeeded();

            Assert.That(native.CameraInits, Has.Count.EqualTo(1), "the filter wheel must not reset the camera's setup");
        }

        [Test]
        public void InitCameraIfNeeded_InitializesAHandleThatIsOnlyOpen_AndAgainAfterAReopen() {
            var filterWheelScan = Sdk();
            var filterWheel = Sdk();

            // A filter wheel rescan holds the handle without initializing it.
            filterWheelScan.Open(Main);
            filterWheel.Open(Main);
            filterWheel.InitCameraIfNeeded();

            Assert.That(native.CameraInits, Has.Count.EqualTo(1));

            filterWheelScan.Close();
            filterWheel.Close();
            filterWheel.Open(Main);
            filterWheel.InitCameraIfNeeded();

            Assert.That(native.CameraInits, Has.Count.EqualTo(2), "a reopened camera has a new handle");
        }

        [Test]
        public void AFailedInit_IsRepeatedByTheNextInitCameraIfNeeded() {
            var camera = Sdk();
            var filterWheel = Sdk();
            camera.Open(Main);
            filterWheel.Open(Main);
            camera.InitCamera();

            native.InitCameraResult = QhySdk.QHYCCD_ERROR;
            Assert.Throws<QhySdk.QHYCameraException>(camera.InitCamera);

            native.InitCameraResult = QhySdk.QHYCCD_SUCCESS;
            filterWheel.InitCameraIfNeeded();

            Assert.That(native.CameraInits, Has.Count.EqualTo(3));
        }

        [Test]
        public void ACallWithoutAnOpenHandle_GetsAZeroHandle_UnderTheSdkLock() {
            native.Registry = registry;
            var sdk = Sdk();

            Assert.That(
                () => sdk.InitCamera(),
                Throws.TypeOf<QhySdk.QHYCameraException>().With.Message.Contains("QHYInitCamera()"));

            Assert.That(native.CameraInits, Is.EqualTo(new[] { IntPtr.Zero }));
            Assert.That(native.CameraInitUnderSdkLock, Is.True, "a zero-handle call must not overlap a release of the SDK");
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
        public void ReturningAHandleTooOften_DoesNotCloseTheNextHandleOfThatCamera() {
            var first = registry.Acquire(Main);
            registry.Return(first);
            var second = registry.Acquire(Main);

            registry.Return(first);

            Assert.That(native.Closed, Has.Count.EqualTo(1));
            Assert.That(second.RefCount, Is.EqualTo(1));
            Assert.That(registry.Release(), Is.False, "the second handle is still open");
        }

        [Test]
        public void ACallWaitingForAHandle_GetsAZeroHandle_OnceTheHandleWasClosed() {
            var entry = registry.Acquire(Main);

            using (entry.Enter(out var whileOpen)) {
                Assert.That(whileOpen, Is.EqualTo(native.HandleOf(Main)));
            }

            // A caller that read the entry before its device closed it reaches the lock only afterwards.
            registry.Return(entry);

            using (entry.Enter(out var afterClose)) {
                Assert.That(afterClose, Is.EqualTo(IntPtr.Zero));
            }
        }

        [Test]
        public void CloseQHYCCD_RunsUnderTheSdkLock() {
            native.Registry = registry;
            var sdk = Sdk();
            sdk.Open(Main);

            sdk.Close();

            Assert.That(native.ClosedUnderSdkLock, Is.True, "closing changes the SDK's device list like open and scan");
        }

        [Test]
        public void AFailedRelease_KeepsTheSdkInitialized() {
            var sdk = Sdk();
            sdk.InitSdk();
            native.ReleaseResult = QhySdk.QHYCCD_ERROR;

            Assert.Throws<QhySdk.QHYCameraException>(() => sdk.ReleaseSdk());
            Assert.That(registry.IsInitialized, Is.True);

            native.ReleaseResult = QhySdk.QHYCCD_SUCCESS;
            sdk.ReleaseSdk();

            Assert.That(registry.IsInitialized, Is.False);
            Assert.That(native.Inits, Is.EqualTo(1));
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
            var mainEntry = registry.Acquire(Main);
            var busy = new ManualResetEventSlim();
            var finishReadout = new ManualResetEventSlim();

            // Simulates a readout holding the main camera's handle.
            var readout = Task.Run(() => {
                lock (mainEntry.Lock) {
                    busy.Set();
                    finishReadout.Wait();
                }
            });

            try {
                Assert.That(busy.Wait(MaxWait), Is.True);

                registry.Return(mainEntry);
                var closing = Task.Run(main.Close);

                // The last reference is gone, so the close is past its bookkeeping and waits for the readout.
                await WaitUntil(() => mainEntry.RefCount == 0);

                var other = Task.Run(() => {
                    guide.Open(Guide);
                    return guide.Scan();
                });

                Assert.That(other.Wait(MaxWait), Is.True, "scan and open of the other camera don't wait for the readout");
                Assert.That(other.Result, Is.EqualTo(2));
                Assert.That(closing.IsCompleted, Is.False);

                finishReadout.Set();
                await readout;
                await closing;
                Assert.That(native.Closed, Is.EqualTo(new[] { native.HandleOf(Main) }));
            } finally {
                finishReadout.Set();
            }
        }

        [Test]
        public async Task ReopeningACamera_WaitsUntilItsOldHandleIsClosed() {
            var first = Sdk();
            first.Open(Main);
            var entry = registry.Acquire(Main);
            var busy = new ManualResetEventSlim();
            var finishReadout = new ManualResetEventSlim();

            // Simulates a readout holding the old handle, so its close waits outside the SDK lock.
            var readout = Task.Run(() => {
                lock (entry.Lock) {
                    busy.Set();
                    finishReadout.Wait();
                }
            });

            try {
                Assert.That(busy.Wait(MaxWait), Is.True);

                registry.Return(entry);
                var closing = Task.Run(first.Close);
                await WaitUntil(() => entry.RefCount == 0);

                var second = Sdk();
                var reopening = Task.Run(() => second.Open(Main));

                Assert.That(Task.Run(registry.Scan).Wait(MaxWait), Is.True, "the SDK lock is free while the close waits");
                await Task.Delay(100);
                Assert.That(reopening.IsCompleted, Is.False, "the camera is opened again only after its old handle is closed");
                Assert.That(native.Opened, Has.Count.EqualTo(1));

                finishReadout.Set();
                await readout;
                await closing;
                Assert.That(reopening.Wait(MaxWait), Is.True);
                Assert.That(native.Calls, Is.EqualTo(new[] { "open " + Main, "close " + Main, "open " + Main }));
            } finally {
                finishReadout.Set();
            }
        }

        private static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(10);

        private static async Task WaitUntil(Func<bool> condition) {
            var deadline = DateTime.UtcNow + MaxWait;

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

            public List<string> Calls { get; } = new();

            public List<IntPtr> CameraInits { get; } = new();

            public QhyHandleRegistry? Registry { get; set; }

            public bool ClosedUnderSdkLock { get; private set; }

            public bool CameraInitUnderSdkLock { get; private set; }

            public uint ReleaseResult { get; set; } = QhySdk.QHYCCD_SUCCESS;

            public uint InitCameraResult { get; set; } = QhySdk.QHYCCD_SUCCESS;

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
                return ReleaseResult;
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
                Calls.Add("open " + id);
                handles[id] = new IntPtr(next++);
                return handles[id];
            }

            public uint Close(IntPtr handle) {
                ClosedUnderSdkLock = Registry?.SdkLock.IsHeldByCurrentThread == true;

                lock (Closed) {
                    Closed.Add(handle);
                    Calls.Add("close " + handles.First(h => h.Value == handle).Key);
                }

                return QhySdk.QHYCCD_SUCCESS;
            }

            public uint InitCamera(IntPtr handle) {
                CameraInitUnderSdkLock = Registry?.SdkLock.IsHeldByCurrentThread == true;
                CameraInits.Add(handle);

                // Like libqhyccd, reject a call without a handle.
                return handle == IntPtr.Zero ? QhySdk.QHYCCD_ERROR : InitCameraResult;
            }
        }
    }
}
