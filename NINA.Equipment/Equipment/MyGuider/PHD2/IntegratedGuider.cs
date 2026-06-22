#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Astrometry;
using NINA.Core.Enum;
using NINA.Core.Interfaces;
using NINA.Core.Model;
using NINA.Core.Utility;
using NINA.Equipment.Equipment.MyGuider.PHD2.Native;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Image.Interfaces;
using NINA.Profile.Interfaces;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using static NINA.Equipment.Equipment.MyGuider.PHD2.Native.Phd2CoreInterop;

namespace NINA.Equipment.Equipment.MyGuider.PHD2 {

    /// <summary>
    /// In-process guider driven by the headless libphd2core engine (via <see cref="Phd2CoreEngine"/>).
    /// NINA owns the hardware: a guide camera (<see cref="IGuideCameraSource"/>) supplies frames and
    /// the mount pulses come back out through <see cref="ITelescopeMediator.PulseGuide"/>. This replaces
    /// the external PHD2 process + RPC for everyday guiding.
    ///
    /// The engine is host-driven and synchronous: the exposure loop captures a frame, calls
    /// <see cref="Phd2CoreEngine.PushFrame"/> (which raises <see cref="Phd2CoreEngine.PulseRequested"/>
    /// and <see cref="Phd2CoreEngine.GuideStepReceived"/> inline), applies the pulse, and waits out the
    /// pulse before the next exposure.
    /// </summary>
    public class IntegratedGuider : BaseINPC, IGuider {
        private const double ArcsecPerRadianMm = 206.265; // 206265 arcsec/rad / 1000 (micron->mm)
        private const int PulseSettleBufferMs = 50;
        private const double DefaultExposureSeconds = 2.0;

        private readonly IProfileService profileService;
        private readonly ITelescopeMediator telescopeMediator;
        private readonly IGuideCameraSource camera;

        private Phd2CoreEngine engine;
        private CancellationTokenSource loopCts;
        private Task loopTask;
        private readonly Stopwatch clock = new Stopwatch();
        private int frameCounter;
        private volatile int lastPulseMs;
        private volatile bool pendingStartGuiding;

        // Latest frame + guide step, surfaced to the UI (guide image, star profile, RMS).
        private volatile GuideFrame lastFrame;
        private Phd2GuideStep lastStep;
        private volatile bool hasStep;

        private TaskCompletionSource<bool> guidingStartedTcs;
        private TaskCompletionSource<bool> settleTcs;

        public IntegratedGuider(IProfileService profileService, ITelescopeMediator telescopeMediator, IGuideCameraSource camera) {
            this.profileService = profileService;
            this.telescopeMediator = telescopeMediator;
            this.camera = camera;
        }

        public string Name => "PHD2_Integrated";
        public string DisplayName => "PHD2 (Integrated)";
        public string Id => "PHD2_Integrated";
        public string Category => "Guiders";
        public string Description => "Headless PHD2 guiding engine integrated into N.I.N.A.";
        public string DriverInfo => "libphd2core";

        public string DriverVersion {
            get {
                // Calls into the native lib; guard so the equipment chooser can list this
                // guider even if libphd2core is not deployed yet.
                try {
                    return Phd2CoreEngine.Version();
                } catch (Exception) {
                    return "unknown";
                }
            }
        }

        public bool HasSetupDialog => false;

        private bool connected;

        public bool Connected {
            get => connected;
            private set {
                connected = value;
                RaisePropertyChanged();
            }
        }

        private double pixelScale = 1.0;

        public double PixelScale {
            get => pixelScale;
            set {
                pixelScale = value;
                RaisePropertyChanged();
            }
        }

        public string State {
            get {
                switch (engine?.State) {
                    case Phd2State.Guiding: return "Guiding";
                    case Phd2State.Calibrating: return "Calibrating";
                    case Phd2State.Selected: return "Selected";
                    case Phd2State.Selecting: return "LostLock";
                    default: return "Stopped";
                }
            }
        }

        /// <summary>The most recent guide-camera frame as image data (for serving the guide image).</summary>
        public IImageData LastGuideImage => lastFrame?.ImageData;

        /// <summary>Snapshot of the current guiding state for the UI (star, lock, error, calibration).</summary>
        public IntegratedGuiderState GetIntegratedState() {
            var s = new IntegratedGuiderState {
                State = State,
                IsCalibrated = engine?.IsCalibrated ?? false,
                PixelScale = PixelScale,
            };
            if (engine != null) {
                engine.GetCurrentPosition(out var sx, out var sy);
                engine.GetLockPosition(out var lx, out var ly);
                s.StarX = sx;
                s.StarY = sy;
                s.LockX = lx;
                s.LockY = ly;
                s.ErrorPx = engine.CurrentError;
                if (engine.TryGetCalibration(out var cal)) {
                    s.CalXRate = cal.XRate;
                    s.CalYRate = cal.YRate;
                    s.CalXAngle = cal.XAngle;
                    s.CalYAngle = cal.YAngle;
                    s.CalDeclination = cal.Declination;
                }
            }
            if (hasStep) {
                s.StarFound = lastStep.StarFound != 0;
                s.Snr = lastStep.Snr;
                s.Hfd = lastStep.Hfd;
                s.Mass = lastStep.Mass;
            }
            if (lastFrame != null) {
                s.FrameWidth = lastFrame.Width;
                s.FrameHeight = lastFrame.Height;
            }
            return s;
        }

        public bool CanClearCalibration => true;
        public bool CanSetShiftRate => false;
        public bool ShiftEnabled => false;
        public bool CanGetLockPosition => true;
        public SiderealShiftTrackingRate ShiftRate => SiderealShiftTrackingRate.Disabled;

        public event EventHandler<IGuideStep> GuideEvent;

        public async Task<bool> Connect(CancellationToken token) {
            if (!await camera.Connect(token)) {
                Connected = false;
                return false;
            }

            PixelScale = ComputePixelScale(camera.PixelSizeMicrons);

            var cfg = Phd2CoreEngine.DefaultConfig();
            cfg.PixelScale = PixelScale > 0 ? PixelScale : 1.0;

            engine = new Phd2CoreEngine(cfg);
            engine.PulseRequested += OnPulseRequested;
            engine.GuideStepReceived += OnGuideStepReceived;
            engine.StateChanged += OnStateChanged;
            engine.StarLost += OnStarLost;
            engine.SettleDone += OnSettleDone;
            engine.CalibrationDone += OnCalibrationDone;

            profileService.ActiveProfile.GuiderSettings.GuiderName = Id;
            Connected = true;
            return true;
        }

        public void Disconnect() {
            StopLoop();
            if (engine != null) {
                engine.PulseRequested -= OnPulseRequested;
                engine.GuideStepReceived -= OnGuideStepReceived;
                engine.StateChanged -= OnStateChanged;
                engine.StarLost -= OnStarLost;
                engine.SettleDone -= OnSettleDone;
                engine.CalibrationDone -= OnCalibrationDone;
                engine.Dispose();
                engine = null;
            }
            camera.Disconnect();
            Connected = false;
        }

        public async Task<bool> AutoSelectGuideStar() {
            if (engine == null) { return false; }
            var frame = await camera.CaptureFrame(ExposureSeconds(), CancellationToken.None);
            return engine.AutoSelectStar(frame.Pixels, frame.Width, frame.Height);
        }

        public async Task<bool> StartGuiding(bool forceCalibration, IProgress<ApplicationStatus> progress, CancellationToken ct) {
            if (engine == null) { return false; }

            // Ensure a star is selected.
            if (engine.State == Phd2State.Stopped) {
                if (!await AutoSelectGuideStar()) { return false; }
            }

            guidingStartedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            StartLoop();

            // Record the mount declination so the calibration (and later RA-rate compensation)
            // are referenced to the current pointing.
            engine.SetCalibrationDeclination(GetMountDeclinationRadians());

            if (forceCalibration || !engine.IsCalibrated) {
                if (!engine.StartCalibration()) {
                    guidingStartedTcs.TrySetResult(false);
                }
            } else {
                engine.AdjustForDeclination(GetMountDeclinationRadians());
                // Synchronously transitions to Guiding (fires OnStateChanged) inside this call.
                engine.StartGuiding();
            }

            return await WaitFor(guidingStartedTcs.Task, ct);
        }

        public async Task<bool> StopGuiding(CancellationToken ct) {
            StopLoop();
            engine?.StopGuiding();
            RaisePropertyChanged(nameof(State));
            return await Task.FromResult(true);
        }

        public async Task<bool> Dither(IProgress<ApplicationStatus> progress, CancellationToken ct) {
            if (engine == null || engine.State != Phd2State.Guiding) { return false; }

            var s = profileService.ActiveProfile.GuiderSettings;
            var settle = BuildSettle();
            settleTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            if (!engine.Dither(s.DitherPixels, s.DitherRAOnly, settle)) {
                return false;
            }
            return await WaitFor(settleTcs.Task, ct);
        }

        public async Task<bool> ClearCalibration(CancellationToken ct) {
            if (engine != null) {
                // No dedicated clear in the ABI; an invalid calibration clears the engine's state.
                engine.SetCalibration(new Phd2Calibration { IsValid = 0 });
            }
            return await Task.FromResult(true);
        }

        public async Task<LockPosition> GetLockPosition() {
            if (engine == null) { return null; }
            engine.GetLockPosition(out var x, out var y);
            return await Task.FromResult(new LockPosition((float)x, (float)y));
        }

        public Task<bool> SetShiftRate(SiderealShiftTrackingRate shiftTrackingRate, CancellationToken ct) => Task.FromResult(false);

        public Task<bool> StopShifting(CancellationToken ct) => Task.FromResult(true);

        public IList<string> SupportedActions => new List<string>();

        public string Action(string actionName, string actionParameters) => throw new NotImplementedException();

        public string SendCommandString(string command, bool raw) => throw new NotImplementedException();

        public bool SendCommandBool(string command, bool raw) => throw new NotImplementedException();

        public void SendCommandBlind(string command, bool raw) => throw new NotImplementedException();

        public void SetupDialog() { }

        #region engine callbacks

        private void OnPulseRequested(Phd2Pulse pulse) {
            ApplyPulse(pulse.RaDirection, pulse.RaDurationMs);
            ApplyPulse(pulse.DecDirection, pulse.DecDurationMs);
            lastPulseMs = Math.Max(pulse.RaDurationMs, pulse.DecDurationMs);
        }

        private void ApplyPulse(Phd2Direction dir, int durationMs) {
            if (dir == Phd2Direction.None || durationMs <= 0) { return; }
            var gd = dir switch {
                Phd2Direction.North => GuideDirections.guideNorth,
                Phd2Direction.South => GuideDirections.guideSouth,
                Phd2Direction.East => GuideDirections.guideEast,
                Phd2Direction.West => GuideDirections.guideWest,
                _ => GuideDirections.guideNorth
            };
            telescopeMediator.PulseGuide(gd, durationMs);
        }

        private void OnGuideStepReceived(Phd2GuideStep step) {
            lastStep = step;
            hasStep = true;
            var guideStep = new IntegratedGuideStep {
                Frame = ++frameCounter,
                Time = clock.Elapsed.TotalSeconds,
                RADistanceRaw = step.RaRaw,
                DECDistanceRaw = step.DecRaw,
                RADuration = step.RaDurationMs / 1000.0,
                DECDuration = step.DecDurationMs / 1000.0
            };
            GuideEvent?.Invoke(this, guideStep);
        }

        private void OnStateChanged(Phd2State state) {
            RaisePropertyChanged(nameof(State));
            if (state == Phd2State.Guiding) {
                guidingStartedTcs?.TrySetResult(true);
            }
        }

        private void OnStarLost() {
            // Surfaced through the next guide step (star_found == 0); nothing extra to do yet.
        }

        private void OnSettleDone(bool success) {
            settleTcs?.TrySetResult(success);
        }

        private void OnCalibrationDone(bool success) {
            if (success) {
                // Fired from inside PushFrame; defer StartGuiding to the loop to avoid
                // re-entering the engine mid-frame.
                pendingStartGuiding = true;
            } else {
                guidingStartedTcs?.TrySetResult(false);
            }
        }

        #endregion engine callbacks

        #region exposure loop

        private void StartLoop() {
            if (loopTask != null && !loopTask.IsCompleted) { return; }
            frameCounter = 0;
            clock.Restart();
            loopCts = new CancellationTokenSource();
            loopTask = Task.Run(() => ExposureLoop(loopCts.Token));
        }

        private void StopLoop() {
            try {
                loopCts?.Cancel();
                loopTask?.Wait(2000);
            } catch (AggregateException) {
                // loop cancellation
            } finally {
                loopCts?.Dispose();
                loopCts = null;
                loopTask = null;
                clock.Stop();
            }
        }

        private async Task ExposureLoop(CancellationToken token) {
            try {
                while (!token.IsCancellationRequested) {
                    var exposureSeconds = ExposureSeconds();
                    var frame = await camera.CaptureFrame(exposureSeconds, token);
                    token.ThrowIfCancellationRequested();
                    lastFrame = frame;

                    // Keep the RA rate compensated for the mount's current declination.
                    if (engine.State == Phd2State.Guiding) {
                        engine.AdjustForDeclination(GetMountDeclinationRadians());
                    }

                    lastPulseMs = 0;
                    engine.PushFrame(frame.Pixels, frame.Width, frame.Height, exposureSeconds);

                    if (pendingStartGuiding) {
                        // Calibration finished on this frame; start guiding now that PushFrame returned.
                        pendingStartGuiding = false;
                        engine.StartGuiding();
                    }

                    if (lastPulseMs > 0) {
                        await Task.Delay(lastPulseMs + PulseSettleBufferMs, token);
                    }
                }
            } catch (OperationCanceledException) {
                // normal stop
            } catch (Exception ex) {
                Logger.Error($"IntegratedGuider exposure loop failed: {ex}");
            }
        }

        #endregion exposure loop

        private double ExposureSeconds() {
            var ms = profileService.ActiveProfile.GuiderSettings.PHD2ExposureMs;
            return ms.HasValue && ms.Value > 0 ? ms.Value / 1000.0 : DefaultExposureSeconds;
        }

        /// <summary>
        /// Current mount declination in radians, or the engine's UNKNOWN sentinel when no mount is
        /// connected (the engine then skips declination compensation).
        /// </summary>
        private double GetMountDeclinationRadians() {
            try {
                var info = telescopeMediator.GetInfo();
                if (info == null || !info.Connected) { return UnknownDeclination; }
                var coords = telescopeMediator.GetCurrentPosition();
                if (coords == null) { return UnknownDeclination; }
                return coords.Dec * Math.PI / 180.0;
            } catch (Exception) {
                return UnknownDeclination;
            }
        }

        private double ComputePixelScale(double pixelSizeMicrons) {
            var guiderSettings = profileService.ActiveProfile.GuiderSettings;
            double focalLength = guiderSettings.PHD2FocalLength ?? profileService.ActiveProfile.TelescopeSettings.FocalLength;
            if (focalLength > 0 && pixelSizeMicrons > 0) {
                return ArcsecPerRadianMm * pixelSizeMicrons / focalLength;
            }
            return PixelScale;
        }

        private Phd2CoreSettle BuildSettle() {
            var s = profileService.ActiveProfile.GuiderSettings;
            return new Phd2CoreSettle {
                TolerancePx = s.SettlePixels,
                SettleTimeSec = s.SettleTime,
                TimeoutSec = s.SettleTimeout,
                Frames = 99999
            };
        }

        private static async Task<bool> WaitFor(Task<bool> task, CancellationToken ct) {
            try {
                return await task.WaitAsync(ct);
            } catch (OperationCanceledException) {
                return false;
            }
        }
    }

    /// <summary>Serializable snapshot of the integrated guider's current state for the API/UI.</summary>
    public class IntegratedGuiderState {
        public string State { get; set; }
        public bool IsCalibrated { get; set; }
        public double PixelScale { get; set; }

        public bool StarFound { get; set; }
        public double StarX { get; set; }
        public double StarY { get; set; }
        public double LockX { get; set; }
        public double LockY { get; set; }
        public double ErrorPx { get; set; }
        public double Snr { get; set; }
        public double Hfd { get; set; }
        public double Mass { get; set; }

        public int FrameWidth { get; set; }
        public int FrameHeight { get; set; }

        public double CalXRate { get; set; }
        public double CalYRate { get; set; }
        public double CalXAngle { get; set; }
        public double CalYAngle { get; set; }
        public double CalDeclination { get; set; }
    }
}
