// SPDX-License-Identifier: MPL-2.0

using NINA.Astrometry;
using NINA.Core.Model.Equipment;
using NINA.Core.Utility;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Model;
using NINA.Mcp.Observatory;
using static System.FormattableString;

namespace NINA.Mcp.Adapter;

public sealed partial class NinaObservatory {

    private const string CaptureBlockedMessage =
        "The camera is in use by another pins operation (flat wizard, framing, plate solving or a snapshot loop). "
        + "Wait for it to finish, then retry.";

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

            var success = await RunMountMotionAsync(
                () => telescopeMediator.SlewToCoordinatesAsync(target, cancellationToken),
                cancellationToken);

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
            await StopGuidingBeforeMountMotionAsync(cancellationToken);

            var success = await RunMountMotionAsync(
                () => telescopeMediator.ParkTelescope(CreateProgress(), cancellationToken),
                cancellationToken);

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
            await StopGuidingBeforeMountMotionAsync(cancellationToken);

            var success = await RunMountMotionAsync(
                () => telescopeMediator.FindHome(CreateProgress(), cancellationToken),
                cancellationToken);

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

    public async Task<OperationResult> SetTrackingAsync(bool enabled, CancellationToken cancellationToken) {
        var info = telescopeMediator.GetInfo();

        if (info?.Connected != true) {
            return OperationResult.Fail("Mount is not connected.");
        }

        if (!info.CanSetTrackingEnabled) {
            return OperationResult.Fail("This mount does not allow switching tracking.");
        }

        try {
            // TelescopeVM.SetTrackingEnabled returns the mount's tracking state after the change, not a success flag.
            var state = telescopeMediator.SetTrackingEnabled(enabled);

            // GetInfo is refreshed once per device polling interval: wait up to two of them for the new state.
            var pollingSeconds = profileService.ActiveProfile.ApplicationSettings.DevicePollingInterval;
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(Math.Max(pollingSeconds, 0.5) * 2 + 1);

            while (state != enabled && DateTime.UtcNow < deadline) {
                await Task.Delay(250, cancellationToken);
                state = telescopeMediator.GetInfo()?.TrackingEnabled == true;
            }

            return state == enabled
                ? OperationResult.Ok(enabled ? "Tracking enabled." : "Tracking disabled.")
                : OperationResult.Fail(enabled ? "The mount did not start tracking." : "The mount did not stop tracking.");
        } catch (OperationCanceledException) {
            throw;
        } catch (Exception ex) {
            Logger.Error("MCP: setting tracking failed", ex);
            return OperationResult.Fail($"Setting tracking failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Aborts a running slew, park or home. Sends nothing to an idle mount: on several drivers the abort command also
    /// stops tracking. A motion started over MCP counts as running even before the cached <c>Slewing</c> flag, which is
    /// refreshed only once per device polling interval, reports it.
    /// </summary>
    public void StopSlew() {
        try {
            var info = telescopeMediator.GetInfo();

            if (info?.Connected == true && (Volatile.Read(ref mountMotionsInFlight) > 0 || info.Slewing)) {
                telescopeMediator.StopSlew();
            }
        } catch (Exception ex) {
            Logger.Error("MCP: stop slew failed", ex);
        }
    }

    /// <summary>
    /// Like the ParkScope and FindHome instructions: no guide corrections to a mount on its way to park or home. A guider
    /// error must not keep the mount from parking, so it is only logged.
    /// </summary>
    private async Task StopGuidingBeforeMountMotionAsync(CancellationToken cancellationToken) {
        try {
            await guiderMediator.StopGuiding(cancellationToken);
        } catch (OperationCanceledException) {
            throw;
        } catch (Exception ex) {
            Logger.Error("MCP: stopping guiding before a mount motion failed", ex);
        }
    }

    /// <summary>
    /// Runs a slew, park or home with the mount marked as moving, and stops it when <paramref name="cancellationToken"/>
    /// is cancelled (TelescopeVM only cancels its own wait on the token, and the INDI driver sends no abort for it).
    /// </summary>
    private async Task<bool> RunMountMotionAsync(Func<Task<bool>> motion, CancellationToken cancellationToken) {
        Interlocked.Increment(ref mountMotionsInFlight);

        try {
            using (cancellationToken.Register(StopSlew)) {
                return await motion();
            }
        } finally {
            Interlocked.Decrement(ref mountMotionsInFlight);
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

        if (!TryTakeCaptureBlock(out var holdsCaptureBlock)) {
            return new CaptureResult(false, CaptureBlockedMessage, null);
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

            // ImagingVM assigned the history id; create the entry so that get_recent_images lists the frame (NINA only fills
            // existing entries when an image is saved).
            var imageId = exposure.MetaData?.Image?.Id ?? -1;

            if (imageId >= 0) {
                imageHistory.Add(imageId, imageType);
            }

            // Like TakeExposure for lights: ImageControlVM stretches and detects stars with the profile's image settings
            // (sensitivity, noise reduction, debayered HFR); detection on the linear render finds too few stars.
            var analysed = await imagingMediator.PrepareImage(exposure, new PrepareImageParameters(autoStretch: true, detectStars: true), cancellationToken);
            var imageData = analysed.RawImageData;
            var statistics = await imageData.Statistics.Task;
            var stars = imageData.StarDetectionAnalysis;

            if (imageId >= 0 && statistics != null) {
                imageHistory.PopulateStatistics(imageId, statistics);
            }

            if (request.Save) {
                await imageSaveMediator.Enqueue(imageData, Task.FromResult(analysed), progress, cancellationToken);
            }

            // The filter actually in place (a failed change leaves the old one).
            var usedFilterInfo = filterWheelMediator.GetInfo()?.Connected == true
                ? filterWheelMediator.GetInfo()?.SelectedFilter
                : null;
            var usedFilter = usedFilterInfo?.Name;

            var record = new ImageRecord(
                Math.Max(imageId, 0),
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

            // By position, like ChangeFilterAsync: names need not be unique.
            if (filter != null && usedFilterInfo?.Position != filter.Position) {
                return new CaptureResult(
                    false,
                    $"The frame was taken, but the filter wheel did not reach {filter.Name} (current filter: {usedFilter ?? "unknown"}).",
                    record);
            }

            var message = request.Save
                ? Invariant($"Captured {request.ExposureSeconds:0.###} s light frame; it is being saved like a sequence frame.")
                : Invariant($"Captured {request.ExposureSeconds:0.###} s snapshot (not saved).");

            return new CaptureResult(true, message, record);
        } catch (OperationCanceledException) {
            throw;
        } catch (Exception ex) {
            Logger.Error("MCP: capture failed", ex);
            return new CaptureResult(false, $"Capture failed: {ex.Message}", null);
        } finally {
            if (holdsCaptureBlock) {
                cameraMediator.ReleaseCaptureBlock(this);
            }
        }
    }

    /// <summary>
    /// Takes the camera capture block, as NINA's flat wizard, framing and snapshot tools do, so that none of them starts
    /// in the middle of an MCP capture or autofocus. While the advanced sequence runs it holds the block itself; a forced
    /// MCP call then goes ahead without it (ImagingVM serializes the single exposures).
    /// </summary>
    /// <returns>False when another pins operation holds the block.</returns>
    private bool TryTakeCaptureBlock(out bool taken) {
        taken = false;

        if (sequence.IsRunning()) {
            return true;
        }

        try {
            // Throws when another consumer holds the block; ICameraMediator has no non-throwing variant.
            cameraMediator.RegisterCaptureBlock(this);
            taken = true;

            return true;
        } catch (Exception) {
            return false;
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

        // the camera VM's flag (through the mediator): CameraInfo.TempChangeRunning is never set
        if (cameraMediator.TempChangeRunning) {
            return OperationResult.Fail("A cooling or warming ramp is already running (started in pins or Touch-N-Stars).");
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

        // the camera VM's flag (through the mediator): CameraInfo.TempChangeRunning is never set
        if (cameraMediator.TempChangeRunning) {
            return OperationResult.Fail("A cooling or warming ramp is already running (started in pins or Touch-N-Stars).");
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

        if (!TryTakeCaptureBlock(out var holdsCaptureBlock)) {
            return new AutofocusResult(false, CaptureBlockedMessage, null, null, null, null);
        }

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
            if (holdsCaptureBlock) {
                cameraMediator.ReleaseCaptureBlock(this);
            }

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

            // FilterWheelVM returns null when the position is unknown and the old filter after its timeout.
            var now = result ?? filterWheelMediator.GetInfo()?.SelectedFilter;

            if (now == null || now.Position != filter.Position) {
                return OperationResult.Fail(
                    $"The filter wheel did not reach {filter.Name} (position {filter.Position}); current filter: {now?.Name ?? "unknown"}.");
            }

            return OperationResult.Ok($"Filter is now {now.Name} (position {now.Position}).");
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

            // PHD2 returns false when it was already stopped; nothing is guiding either way.
            return success
                ? OperationResult.Ok("Guiding stopped.")
                : OperationResult.Ok("Guiding was not active (the guider reported nothing to stop).");
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
            // SwitchVM swallows its own timeout; read the value back.
            await switchMediator.SetSwitchValue((short)index, value, CreateProgress(), cancellationToken);

            var reached = ReadWritableSwitchValue(index);

            for (var attempt = 0; (reached is null || Math.Abs(reached.Value - value) > 1e-5) && attempt < 10; attempt++) {
                await Task.Delay(200, cancellationToken);
                reached = ReadWritableSwitchValue(index);
            }

            if (reached is null || Math.Abs(reached.Value - value) > 1e-5) {
                return OperationResult.Fail(Invariant($"Switch {index} ({target.Name}) did not reach {value}; it reports {reached?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "no value"}."));
            }

            return OperationResult.Ok(Invariant($"Switch {index} ({target.Name}) set to {value}."));
        } catch (OperationCanceledException) {
            throw;
        } catch (Exception ex) {
            Logger.Error("MCP: setting a switch failed", ex);
            return OperationResult.Fail($"Setting switch {index} failed: {ex.Message}");
        }
    }

    private double? ReadWritableSwitchValue(int index) {
        var switches = switchMediator.GetInfo()?.WritableSwitches;

        if (switches == null || index >= switches.Count) {
            return null;
        }

        return switches[index].Value;
    }
}
