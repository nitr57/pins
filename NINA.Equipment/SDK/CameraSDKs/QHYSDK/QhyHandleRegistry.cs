#region "copyright"

/*
    Copyright © 2026 Nico Trost <nico.trost57@gmail.com> and the PI.N.S. contributors

    This file is part of PI 'N' Stars.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Core.Utility;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;

namespace QHYCCD {

    /// <summary>
    /// Process-wide bookkeeping of the QHY SDK and its camera handles.
    ///
    /// InitQHYCCDResource/ReleaseQHYCCDResource and ScanQHYCCD act on every camera at once, so they run under
    /// <see cref="SdkLock"/>, and the SDK is released only when no handle is open or closing. Handles are per
    /// camera id: the main camera and the guide camera get separate handles, while a camera and the CFW plugged
    /// into it share one (reference counted). Every call on a handle is serialized by that handle's
    /// <see cref="Entry.Lock"/>, so a readout on one camera never waits for the other camera. This mirrors the
    /// INDI QHY driver: one SDK init per process, one handle per camera opened by id, one lock per camera.
    /// </summary>
    internal sealed class QhyHandleRegistry {

        /// <summary>The native calls the registry needs; replaced by a fake in tests.</summary>
        internal interface INative {

            uint InitResource();

            uint ReleaseResource();

            uint Scan();

            IntPtr Open(string id);

            uint Close(IntPtr handle);
        }

        internal sealed class Entry(string id, IntPtr handle) {

            public string Id { get; } = id;

            public IntPtr Handle { get; } = handle;

            public Lock Lock { get; } = new();

            public int RefCount { get; set; }

            public ManualResetEventSlim Closed { get; } = new(false);
        }

        private readonly INative native;
        private readonly Dictionary<string, Entry> open = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Entry> closing = new(StringComparer.Ordinal);
        private bool initialized;

        public QhyHandleRegistry(INative native) {
            this.native = native;
        }

        /// <summary>Serializes the process-wide SDK calls (init, release, scan, id and model lookups).</summary>
        public Lock SdkLock { get; } = new();

        public bool IsInitialized {
            get {
                lock (SdkLock) {
                    return initialized;
                }
            }
        }

        public void Init() {
            lock (SdkLock) {
                InitLocked();
            }
        }

        /// <summary>Releases the SDK unless a handle is still open or being closed. Returns whether it released it.</summary>
        public bool Release() {
            lock (SdkLock) {
                Logger.Trace($"QhyccdSdk: Release requested, open={open.Count}, closing={closing.Count}");

                if (!initialized || open.Count > 0 || closing.Count > 0) {
                    return false;
                }

                initialized = false;
                QhySdk.CheckReturn(native.ReleaseResource(), MethodBase.GetCurrentMethod());
                return true;
            }
        }

        public uint Scan() {
            lock (SdkLock) {
                return native.Scan();
            }
        }

        /// <summary>
        /// Takes a reference on the handle of camera <paramref name="id"/>, opening it if no device holds it yet.
        /// <paramref name="shared"/> tells whether another device (e.g. the camera of a CFW) already had it open.
        /// Initializes the SDK if needed, and waits if the same camera is still being closed.
        /// </summary>
        public Entry Acquire(string id, out bool shared) {
            while (true) {
                Entry pending;

                lock (SdkLock) {
                    if (!closing.TryGetValue(id, out pending)) {
                        InitLocked();

                        shared = open.TryGetValue(id, out var entry);
                        if (!shared) {
                            Logger.Trace($"QhyccdSdk: Opening {id}");

                            var handle = native.Open(id);
                            if (handle == IntPtr.Zero) {
                                throw new QhySdk.QHYCameraException("Unable to open camera", MethodBase.GetCurrentMethod(), [id]);
                            }

                            entry = new Entry(id, handle);
                            open[id] = entry;
                        }

                        entry.RefCount++;
                        Logger.Trace($"QhyccdSdk: Opened {id}, refCount={entry.RefCount}");
                        return entry;
                    }
                }

                // Opening the camera again before its old handle is closed would hand the SDK the same device twice.
                pending.Closed.Wait();
            }
        }

        /// <summary>
        /// Drops a reference taken by <see cref="Acquire"/>. The last reference closes the handle, after any call
        /// still running on it, without holding <see cref="SdkLock"/>, so the other camera can be opened, closed or
        /// scanned meanwhile.
        /// </summary>
        public void Return(Entry entry) {
            lock (SdkLock) {
                entry.RefCount--;
                Logger.Trace($"QhyccdSdk: Closing {entry.Id}, refCount={entry.RefCount}");

                if (entry.RefCount > 0) {
                    return;
                }

                open.Remove(entry.Id);
                closing[entry.Id] = entry;
            }

            uint result;
            try {
                lock (entry.Lock) {
                    result = native.Close(entry.Handle);
                }
            } finally {
                lock (SdkLock) {
                    closing.Remove(entry.Id);
                }

                entry.Closed.Set();
            }

            QhySdk.CheckReturn(result, MethodBase.GetCurrentMethod());
        }

        private void InitLocked() {
            if (initialized) {
                return;
            }

            Logger.Trace("QhyccdSdk: Initializing QHY SDK");
            QhySdk.CheckReturn(native.InitResource(), MethodBase.GetCurrentMethod());
            initialized = true;

            // libqhyccd fills its device list in ScanQHYCCD; the INDI driver warns that opening without a scan crashes.
            native.Scan();
        }
    }
}
