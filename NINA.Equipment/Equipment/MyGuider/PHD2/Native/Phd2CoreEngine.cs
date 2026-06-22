#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System;
using System.Runtime.InteropServices;
using static NINA.Equipment.Equipment.MyGuider.PHD2.Native.Phd2CoreInterop;

namespace NINA.Equipment.Equipment.MyGuider.PHD2.Native {

    /// <summary>
    /// Managed, event-driven wrapper around the libphd2core C ABI (<see cref="Phd2CoreInterop"/>).
    /// Owns the native engine handle, keeps the callback delegates rooted for the engine's
    /// lifetime, and re-raises the native callbacks as .NET events.
    ///
    /// Threading: the native engine is host-driven and synchronous. Callbacks fire on whichever
    /// thread calls <see cref="PushFrame"/> (or the relevant mutator), before that call returns;
    /// the consumer is responsible for marshaling to a UI thread if needed.
    /// </summary>
    internal sealed class Phd2CoreEngine : IDisposable {
        private IntPtr handle;

        // The C ABI is non-reentrant per handle, so all native calls are serialized. The exposure
        // loop pushes frames on a background thread while UI / ninaAPI threads read state, lock
        // position, etc. Monitor is reentrant per-thread, so the synchronous callbacks that fire
        // inside phd_push_frame (on the same thread, while the lock is held) cannot deadlock.
        private readonly object gate = new object();

        // Rooted for the engine's lifetime so the native side's function pointers stay valid.
        private readonly Phd2PulseCallback pulseCb;
        private readonly Phd2GuideStepCallback stepCb;
        private readonly Phd2StateCallback stateCb;
        private readonly Phd2StarLostCallback starLostCb;
        private readonly Phd2StatusCallback settleCb;
        private readonly Phd2StatusCallback calCb;
        private Phd2Callbacks callbacks;

        public event Action<Phd2Pulse> PulseRequested;
        public event Action<Phd2GuideStep> GuideStepReceived;
        public event Action<Phd2State> StateChanged;
        public event Action StarLost;
        public event Action<bool> SettleDone;
        public event Action<bool> CalibrationDone;

        public Phd2CoreEngine(Phd2Config config) {
            pulseCb = HandlePulse;
            stepCb = HandleGuideStep;
            stateCb = HandleStateChange;
            starLostCb = HandleStarLost;
            settleCb = HandleSettleDone;
            calCb = HandleCalibrationDone;

            callbacks = new Phd2Callbacks {
                OnPulse = pulseCb,
                OnGuideStep = stepCb,
                OnStateChange = stateCb,
                OnStarLost = starLostCb,
                OnSettleDone = settleCb,
                OnCalibrationDone = calCb
            };

            lock (gate) {
                handle = phd_create(ref config, ref callbacks, IntPtr.Zero);
            }
            if (handle == IntPtr.Zero) {
                throw new InvalidOperationException("phd_create failed (libphd2core could not create the engine)");
            }
        }

        /// <summary>The engine's default configuration (from the native side).</summary>
        public static Phd2Config DefaultConfig() {
            phd_default_config(out var cfg);
            return cfg;
        }

        public static string Version() {
            var p = phd_version();
            return p == IntPtr.Zero ? string.Empty : Marshal.PtrToStringAnsi(p);
        }

        private void HandlePulse(ref Phd2Pulse pulse, IntPtr user) => PulseRequested?.Invoke(pulse);

        private void HandleGuideStep(ref Phd2GuideStep step, IntPtr user) => GuideStepReceived?.Invoke(step);

        private void HandleStateChange(Phd2State state, IntPtr user) => StateChanged?.Invoke(state);

        private void HandleStarLost(IntPtr user) => StarLost?.Invoke();

        private void HandleSettleDone(int success, IntPtr user) => SettleDone?.Invoke(success != 0);

        private void HandleCalibrationDone(int success, IntPtr user) => CalibrationDone?.Invoke(success != 0);

        private void ThrowIfDisposed() {
            if (handle == IntPtr.Zero) {
                throw new ObjectDisposedException(nameof(Phd2CoreEngine));
            }
        }

        public void SetParam(string key, string value) {
            lock (gate) {
                ThrowIfDisposed();
                phd_set_param(handle, key, value);
            }
        }

        public bool AutoSelectStar(ushort[] pixels, int width, int height) {
            lock (gate) {
                ThrowIfDisposed();
                return phd_auto_select_star(handle, pixels, width, height) != 0;
            }
        }

        public bool SelectStar(ushort[] pixels, int width, int height, double x, double y) {
            lock (gate) {
                ThrowIfDisposed();
                return phd_select_star(handle, pixels, width, height, x, y) != 0;
            }
        }

        public void SetCalibration(Phd2Calibration calibration) {
            lock (gate) {
                ThrowIfDisposed();
                phd_set_calibration(handle, ref calibration);
            }
        }

        public bool TryGetCalibration(out Phd2Calibration calibration) {
            lock (gate) {
                ThrowIfDisposed();
                return phd_get_calibration(handle, out calibration) != 0;
            }
        }

        public bool IsCalibrated {
            get {
                lock (gate) {
                    ThrowIfDisposed();
                    return phd_is_calibrated(handle) != 0;
                }
            }
        }

        public bool StartCalibration() {
            lock (gate) {
                ThrowIfDisposed();
                return phd_start_calibration(handle) != 0;
            }
        }

        /// <summary>Declination (radians) recorded with the next calibration; set from the mount.</summary>
        public void SetCalibrationDeclination(double declinationRadians) {
            lock (gate) {
                ThrowIfDisposed();
                phd_set_calibration_declination(handle, declinationRadians);
            }
        }

        /// <summary>Recompute the declination-compensated RA rate for the current pointing (radians).</summary>
        public void AdjustForDeclination(double declinationRadians) {
            lock (gate) {
                ThrowIfDisposed();
                phd_adjust_for_declination(handle, declinationRadians);
            }
        }

        public bool StartGuiding() {
            lock (gate) {
                ThrowIfDisposed();
                return phd_start_guiding(handle) != 0;
            }
        }

        public void StopGuiding() {
            lock (gate) {
                ThrowIfDisposed();
                phd_stop_guiding(handle);
            }
        }

        public bool Dither(double pixels, bool raOnly, Phd2CoreSettle settle) {
            lock (gate) {
                ThrowIfDisposed();
                return phd_dither(handle, pixels, raOnly ? 1 : 0, ref settle) != 0;
            }
        }

        public void StartSettling(Phd2CoreSettle settle) {
            lock (gate) {
                ThrowIfDisposed();
                phd_start_settling(handle, ref settle);
            }
        }

        public bool IsSettling {
            get {
                lock (gate) {
                    ThrowIfDisposed();
                    return phd_is_settling(handle) != 0;
                }
            }
        }

        public void SetDitherSeed(uint seed) {
            lock (gate) {
                ThrowIfDisposed();
                phd_set_dither_seed(handle, seed);
            }
        }

        public void PushFrame(ushort[] pixels, int width, int height, double exposureSeconds) {
            lock (gate) {
                ThrowIfDisposed();
                phd_push_frame(handle, pixels, width, height, exposureSeconds);
            }
        }

        public Phd2State State {
            get {
                lock (gate) {
                    ThrowIfDisposed();
                    return phd_get_state(handle);
                }
            }
        }

        public void GetLockPosition(out double x, out double y) {
            lock (gate) {
                ThrowIfDisposed();
                phd_get_lock_position(handle, out x, out y);
            }
        }

        public void GetCurrentPosition(out double x, out double y) {
            lock (gate) {
                ThrowIfDisposed();
                phd_get_current_position(handle, out x, out y);
            }
        }

        public double CurrentError {
            get {
                lock (gate) {
                    ThrowIfDisposed();
                    return phd_current_error(handle);
                }
            }
        }

        public void Dispose() {
            lock (gate) {
                if (handle != IntPtr.Zero) {
                    phd_destroy(handle);
                    handle = IntPtr.Zero;
                }
            }
            GC.SuppressFinalize(this);
        }

        ~Phd2CoreEngine() {
            // Finalizer runs only when no other references remain, so no managed lock is taken.
            if (handle != IntPtr.Zero) {
                phd_destroy(handle);
                handle = IntPtr.Zero;
            }
        }
    }
}
