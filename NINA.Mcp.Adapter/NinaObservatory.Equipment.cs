// SPDX-License-Identifier: MPL-2.0

using NINA.Astrometry;
using NINA.Core.Enum;
using NINA.Core.Model.Equipment;
using NINA.Core.Utility;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Model;
using NINA.Mcp.Observatory;
using static System.FormattableString;

namespace NINA.Mcp.Adapter;

public sealed partial class NinaObservatory {

    // ----- Mount -----

    public async Task<OperationResult> SlewAsync(double raHours, double decDegrees, CancellationToken cancellationToken) {
        var info = telescopeMediator.GetInfo();

        if (info?.Connected != true) {
            return OperationResult.Fail("Mount is not connected.");
        }

        if (info.AtPark) {
            return OperationResult.Fail("Mount is parked. Unpark it first (unpark_mount).");
        }

        try {
            var target = new Coordinates(Angle.ByHours(raHours), Angle.ByDegree(decDegrees), Epoch.J2000);
            bool success;

            // TelescopeVM cancels its own wait on the token; stop the motion explicitly as well.
            using (cancellationToken.Register(StopSlew)) {
                success = await telescopeMediator.SlewToCoordinatesAsync(target, cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (!success) {
                return OperationResult.Fail("The mount did not complete the slew (rejected by the driver, below the horizon limit or parked); the pins log has details.");
            }

            return OperationResult.Ok(Invariant($"Slewed to RA {raHours:0.0000} h, Dec {decDegrees:+0.0000;-0.0000}° (J2000)."));
        } catch (OperationCanceledException) {
            throw;
        } catch (Exception ex) {
            Logger.Error("MCP: slew failed", ex);
            return OperationResult.Fail($"Slew failed: {ex.Message}");
        }
    }

    public async Task<OperationResult> ParkAsync(CancellationToken cancellationToken) {
        var info = telescopeMediator.GetInfo();

        if (info?.Connected != true) {
            return OperationResult.Fail("Mount is not connected.");
        }

        if (info.AtPark) {
            return OperationResult.Ok("Mount is already parked.");
        }

        try {
            bool success;

            using (cancellationToken.Register(StopSlew)) {
                success = await telescopeMediator.ParkTelescope(CreateProgress(), cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();

            return success
                ? OperationResult.Ok("Mount parked.")
                : OperationResult.Fail("The mount could not be parked; the pins log has details.");
        } catch (OperationCanceledException) {
            throw;
        } catch (Exception ex) {
            Logger.Error("MCP: park failed", ex);
            return OperationResult.Fail($"Park failed: {ex.Message}");
        }
    }

    public async Task<OperationResult> UnparkAsync(CancellationToken cancellationToken) {
        var info = telescopeMediator.GetInfo();

        if (info?.Connected != true) {
            return OperationResult.Fail("Mount is not connected.");
        }

        if (!info.AtPark) {
            return OperationResult.Ok("Mount is not parked.");
        }

        try {
            var success = await telescopeMediator.UnparkTelescope(CreateProgress(), cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();

            return success
                ? OperationResult.Ok("Mount unparked.")
                : OperationResult.Fail("The mount could not be unparked; the pins log has details.");
        } catch (OperationCanceledException) {
            throw;
        } catch (Exception ex) {
            Logger.Error("MCP: unpark failed", ex);
            return OperationResult.Fail($"Unpark failed: {ex.Message}");
        }
    }

    public async Task<OperationResult> FindHomeAsync(CancellationToken cancellationToken) {
        var info = telescopeMediator.GetInfo();

        if (info?.Connected != true) {
            return OperationResult.Fail("Mount is not connected.");
        }

        if (!info.CanFindHome) {
            return OperationResult.Fail("This mount cannot find its home position.");
        }

        try {
            bool success;

            using (cancellationToken.Register(StopSlew)) {
                success = await telescopeMediator.FindHome(CreateProgress(), cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();

            return success
                ? OperationResult.Ok("Mount is at home.")
                : OperationResult.Fail("The mount could not find home; the pins log has details.");
        } catch (OperationCanceledException) {
            throw;
        } catch (Exception ex) {
            Logger.Error("MCP: find home failed", ex);
            return OperationResult.Fail($"Find home failed: {ex.Message}");
        }
    }

    public OperationResult SetTracking(bool enabled) {
        var info = telescopeMediator.GetInfo();

        if (info?.Connected != true) {
            return OperationResult.Fail("Mount is not connected.");
        }

        if (!info.CanSetTrackingEnabled) {
            return OperationResult.Fail("This mount does not allow switching tracking.");
        }

        try {
            return telescopeMediator.SetTrackingEnabled(enabled)
                ? OperationResult.Ok(enabled ? "Tracking enabled." : "Tracking disabled.")
                : OperationResult.Fail("The mount rejected the tracking change.");
        } catch (Exception ex) {
            Logger.Error("MCP: setting tracking failed", ex);
            return OperationResult.Fail($"Setting tracking failed: {ex.Message}");
        }
    }

    public void StopSlew() {
        try {
            if (telescopeMediator.GetInfo()?.Connected == true) {
                telescopeMediator.StopSlew();
            }
        } catch (Exception ex) {
            Logger.Error("MCP: stop slew failed", ex);
        }
    }

    // ----- Camera -----

    public async Task<CaptureResult> CaptureAsync(CaptureRequest request, CancellationToken cancellationToken) {
        var info = cameraMediator.GetInfo();

        if (info?.Connected != true) {
            return new CaptureResult(false, "Camera is not connected.", null);
        }

        if (info.IsExposing) {
            return new CaptureResult(false, "The camera is already exposing (another capture or the sequence uses it).", null);
        }

        FilterInfo? filter = null;

        if (!string.IsNullOrWhiteSpace(request.Filter)) {
            if (filterWheelMediator.GetInfo()?.Connected != true) {
                return new CaptureResult(false, "A filter was requested but the filter wheel is not connected.", null);
            }

            filter = FindFilter(request.Filter, null);

            if (filter == null) {
                return new CaptureResult(false, $"Unknown filter '{request.Filter}'. Available: {AvailableFilterNames()}.", null);
            }
        }

        try {
            var binning = (short)Math.Max(1, request.Binning ?? info.BinX);
            var imageType = request.Save ? CaptureSequence.ImageTypes.LIGHT : CaptureSequence.ImageTypes.SNAPSHOT;
            var captureSequence = new CaptureSequence(request.ExposureSeconds, imageType, filter, new BinningMode(binning, binning), 1);

            if (request.Gain != null) {
                captureSequence.Gain = request.Gain.Value;
            }

            if (request.Offset != null) {
                captureSequence.Offset = request.Offset.Value;
            }

            var progress = CreateProgress();

            // ImagingVM changes the filter, aborts the exposure on cancellation and serializes access to the camera.
            var exposure = await imagingMediator.CaptureImage(captureSequence, cancellationToken, progress, request.Save ? string.Empty : "Snapshot");

            if (exposure == null) {
                return new CaptureResult(false, "The camera returned no image.", null);
            }

            var rendered = await imagingMediator.PrepareImage(exposure, new PrepareImageParameters(autoStretch: false, detectStars: false), cancellationToken);
            var analysed = await rendered.DetectStars(false, StarSensitivityEnum.Normal, NoiseReductionEnum.None, cancellationToken, progress);
            var imageData = analysed.RawImageData;
            var statistics = await imageData.Statistics.Task;
            var stars = imageData.StarDetectionAnalysis;

            if (request.Save) {
                await imageSaveMediator.Enqueue(imageData, Task.FromResult(analysed), progress, cancellationToken);
            }

            var usedFilter = filter?.Name ?? filterWheelMediator.GetInfo()?.SelectedFilter?.Name;

            var record = new ImageRecord(
                0,
                DateTimeOffset.Now,
                imageType,
                string.IsNullOrEmpty(usedFilter) ? null : usedFilter,
                request.ExposureSeconds,
                stars == null ? null : Positive(stars.HFR),
                stars?.DetectedStars,
                statistics == null ? null : Number(statistics.Mean),
                statistics == null ? null : Number(statistics.Median),
                statistics == null ? null : Number(statistics.StDev),
                Number(cameraMediator.GetInfo()?.Temperature ?? double.NaN),
                null,
                null,
                null);

            var message = request.Save
                ? Invariant($"Captured {request.ExposureSeconds:0.###} s light frame; it is being saved like a sequence frame.")
                : Invariant($"Captured {request.ExposureSeconds:0.###} s snapshot (not saved).");

            return new CaptureResult(true, message, record);
        } catch (OperationCanceledException) {
            throw;
        } catch (Exception ex) {
            Logger.Error("MCP: capture failed", ex);
            return new CaptureResult(false, $"Capture failed: {ex.Message}", null);
        }
    }

    public void AbortExposure() {
        try {
            var info = cameraMediator.GetInfo();

            if (info?.Connected == true && info.IsExposing) {
                cameraMediator.AbortExposure();
            }
        } catch (Exception ex) {
            Logger.Error("MCP: abort exposure failed", ex);
        }
    }

    public async Task<OperationResult> CoolCameraAsync(double targetCelsius, TimeSpan duration, CancellationToken cancellationToken) {
        var info = cameraMediator.GetInfo();

        if (info?.Connected != true) {
            return OperationResult.Fail("Camera is not connected.");
        }

        if (!info.CanSetTemperature) {
            return OperationResult.Fail("This camera has no regulated cooler.");
        }

        try {
            var success = await cameraMediator.CoolCamera(targetCelsius, duration, CreateProgress(), cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();

            var reached = Number(cameraMediator.GetInfo()?.Temperature ?? double.NaN);

            return success
                ? OperationResult.Ok(Invariant($"Cooled to {targetCelsius:0.#} °C (sensor now {(reached is null ? "unknown" : reached.Value.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture))} °C)."))
                : OperationResult.Fail("Cooling did not complete; the pins log has details.");
        } catch (OperationCanceledException) {
            throw;
        } catch (Exception ex) {
            Logger.Error("MCP: cooling failed", ex);
            return OperationResult.Fail($"Cooling failed: {ex.Message}");
        }
    }

    public async Task<OperationResult> WarmCameraAsync(TimeSpan duration, CancellationToken cancellationToken) {
        var info = cameraMediator.GetInfo();

        if (info?.Connected != true) {
            return OperationResult.Fail("Camera is not connected.");
        }

        if (!info.CanSetTemperature) {
            return OperationResult.Fail("This camera has no regulated cooler.");
        }

        try {
            var success = await cameraMediator.WarmCamera(duration, CreateProgress(), cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();

            return success
                ? OperationResult.Ok("Camera warmed up and cooler switched off.")
                : OperationResult.Fail("Warming did not complete; the pins log has details.");
        } catch (OperationCanceledException) {
            throw;
        } catch (Exception ex) {
            Logger.Error("MCP: warming failed", ex);
            return OperationResult.Fail($"Warming failed: {ex.Message}");
        }
    }

    // ----- Focuser -----

    public async Task<OperationResult> MoveFocuserAsync(int position, CancellationToken cancellationToken) {
        var info = focuserMediator.GetInfo();

        if (info?.Connected != true) {
            return OperationResult.Fail("Focuser is not connected.");
        }

        if (info.MaxStep > 0 && position > info.MaxStep) {
            return OperationResult.Fail($"Position {position} is beyond the focuser's maximum of {info.MaxStep}.");
        }

        var moveCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        lock (focuserMoveLock) {
            focuserMoveCts = moveCts;
        }

        try {
            // FocuserVM halts the focuser when the token is cancelled.
            var reached = await focuserMediator.MoveFocuser(position, moveCts.Token);

            cancellationToken.ThrowIfCancellationRequested();

            if (moveCts.IsCancellationRequested) {
                return OperationResult.Fail($"Focuser move was stopped at position {focuserMediator.GetInfo()?.Position}.");
            }

            return OperationResult.Ok($"Focuser at position {reached}.");
        } catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) {
            return OperationResult.Fail($"Focuser move was stopped at position {focuserMediator.GetInfo()?.Position}.");
        } catch (OperationCanceledException) {
            throw;
        } catch (Exception ex) {
            Logger.Error("MCP: focuser move failed", ex);
            return OperationResult.Fail($"Focuser move failed: {ex.Message}");
        } finally {
            lock (focuserMoveLock) {
                if (ReferenceEquals(focuserMoveCts, moveCts)) {
                    focuserMoveCts = null;
                }
            }

            moveCts.Dispose();
        }
    }

    public void StopFocuser() {
        lock (focuserMoveLock) {
            try {
                focuserMoveCts?.Cancel();
            } catch (ObjectDisposedException) {
                // The move finished in the meantime.
            }
        }
    }

    public async Task<AutofocusResult> RunAutofocusAsync(CancellationToken cancellationToken) {
        if (cameraMediator.GetInfo()?.Connected != true) {
            return new AutofocusResult(false, "Camera is not connected.", null, null, null, null);
        }

        if (focuserMediator.GetInfo()?.Connected != true) {
            return new AutofocusResult(false, "Focuser is not connected.", null, null, null, null);
        }

        // Same steps as the RunAutofocus sequence instruction: profile filter of the current position, window service
        // so that clients can follow the run, report appended to the image history.
        var windowService = windowServiceFactory.Create();

        try {
            FilterInfo? filter = null;
            var selected = filterWheelMediator.GetInfo()?.SelectedFilter;

            if (selected != null) {
                filter = FindFilter(null, selected.Position);
            }

            var autoFocus = autoFocusVMFactory.Create();
            windowService.Show(autoFocus, "Autofocus", System.Windows.ResizeMode.CanResize, System.Windows.WindowStyle.ToolWindow);

            var report = await autoFocus.StartAutoFocus(filter, cancellationToken, CreateProgress());

            cancellationToken.ThrowIfCancellationRequested();

            if (report == null) {
                return new AutofocusResult(false, "Autofocus did not produce a result (see the pins log).", null, null, filter?.Name, null);
            }

            imageHistory.AppendAutoFocusPoint(report);

            var position = report.CalculatedFocusPoint == null ? (int?)null : (int)Math.Round(report.CalculatedFocusPoint.Position);
            var hfr = report.CalculatedFocusPoint == null ? null : Positive(report.CalculatedFocusPoint.Value);

            return new AutofocusResult(
                true,
                $"Autofocus finished at position {position?.ToString() ?? "unknown"} ({report.Method}, {report.Fitting}).",
                position,
                hfr,
                string.IsNullOrEmpty(report.Filter) ? filter?.Name : report.Filter,
                Number(report.Temperature));
        } catch (OperationCanceledException) {
            throw;
        } catch (Exception ex) {
            Logger.Error("MCP: autofocus failed", ex);
            return new AutofocusResult(false, $"Autofocus failed: {ex.Message}", null, null, null, null);
        } finally {
            windowService.DelayedClose(TimeSpan.FromSeconds(10));
        }
    }

    // ----- Filter wheel -----

    public async Task<OperationResult> ChangeFilterAsync(string? name, int? position, CancellationToken cancellationToken) {
        if (filterWheelMediator.GetInfo()?.Connected != true) {
            return OperationResult.Fail("Filter wheel is not connected.");
        }

        var filter = FindFilter(name, position);

        if (filter == null) {
            var requested = name != null ? $"'{name}'" : $"position {position}";

            return OperationResult.Fail($"No filter {requested} in the profile. Available: {AvailableFilterNames()}.");
        }

        try {
            var result = await filterWheelMediator.ChangeFilter(filter, cancellationToken, CreateProgress());

            cancellationToken.ThrowIfCancellationRequested();

            var now = result ?? filterWheelMediator.GetInfo()?.SelectedFilter;

            return OperationResult.Ok($"Filter is now {now?.Name ?? filter.Name} (position {now?.Position ?? filter.Position}).");
        } catch (OperationCanceledException) {
            throw;
        } catch (Exception ex) {
            Logger.Error("MCP: filter change failed", ex);
            return OperationResult.Fail($"Filter change failed: {ex.Message}");
        }
    }

    // ----- Guider -----

    public async Task<OperationResult> StartGuidingAsync(bool forceCalibration, CancellationToken cancellationToken) {
        if (guiderMediator.GetInfo()?.Connected != true) {
            return OperationResult.Fail("Guider is not connected.");
        }

        try {
            var success = await guiderMediator.StartGuiding(forceCalibration, CreateProgress(), cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();

            return success
                ? OperationResult.Ok(forceCalibration ? "Calibrated and guiding." : "Guiding.")
                : OperationResult.Fail("Guiding could not be started (no guide star, calibration failed or the guide camera is busy); the pins log has details.");
        } catch (OperationCanceledException) {
            throw;
        } catch (Exception ex) {
            Logger.Error("MCP: start guiding failed", ex);
            return OperationResult.Fail($"Starting guiding failed: {ex.Message}");
        }
    }

    public async Task<OperationResult> StopGuidingAsync(CancellationToken cancellationToken) {
        if (guiderMediator.GetInfo()?.Connected != true) {
            return OperationResult.Ok("Guider is not connected; nothing to stop.");
        }

        try {
            var success = await guiderMediator.StopGuiding(cancellationToken);

            return success
                ? OperationResult.Ok("Guiding stopped.")
                : OperationResult.Fail("The guider did not confirm that guiding stopped.");
        } catch (OperationCanceledException) {
            throw;
        } catch (Exception ex) {
            Logger.Error("MCP: stop guiding failed", ex);
            return OperationResult.Fail($"Stopping guiding failed: {ex.Message}");
        }
    }

    // ----- Switches -----

    public async Task<OperationResult> SetSwitchAsync(int index, double value, CancellationToken cancellationToken) {
        var info = switchMediator.GetInfo();

        if (info?.Connected != true) {
            return OperationResult.Fail("Switch device is not connected.");
        }

        List<IWritableSwitch> writable = info.WritableSwitches?.ToList() ?? [];

        if (index < 0 || index >= writable.Count) {
            return OperationResult.Fail($"No writable switch with index {index}; there are {writable.Count} (see get_status).");
        }

        var target = writable[index];

        if (value < target.Minimum || value > target.Maximum) {
            return OperationResult.Fail(Invariant($"Value {value} is outside {target.Name}'s range {target.Minimum}..{target.Maximum}."));
        }

        if (target.StepSize > 0) {
            var steps = (value - target.Minimum) / target.StepSize;

            if (Math.Abs(steps - Math.Round(steps)) > 1e-6) {
                return OperationResult.Fail(Invariant($"Value {value} does not match {target.Name}'s step size {target.StepSize} (from {target.Minimum})."));
            }
        }

        try {
            await switchMediator.SetSwitchValue((short)index, value, CreateProgress(), cancellationToken);

            return OperationResult.Ok(Invariant($"Switch {index} ({target.Name}) set to {value}."));
        } catch (OperationCanceledException) {
            throw;
        } catch (Exception ex) {
            Logger.Error("MCP: setting a switch failed", ex);
            return OperationResult.Fail($"Setting switch {index} failed: {ex.Message}");
        }
    }
}
