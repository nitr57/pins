// SPDX-License-Identifier: MPL-2.0

using NINA.Astrometry;
using NINA.Core.Model.Equipment;
using NINA.Core.Utility;
using NINA.Equipment.Equipment.MySwitch;
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

        var stoppedGuiding = false;

        try {
            var target = new Coordinates(Angle.ByHours(raHours), Angle.ByDegree(decDegrees), Epoch.J2000);

            // Like NINA's Slew instruction: the guide star is lost during the slew, so guiding stops first and starts
            // again afterwards when it ran (also when it was only looping, and also after a refused slew, as there).
            var guidingStopsBefore = Volatile.Read(ref guidingStops);
            stoppedGuiding = await StopGuidingBeforeMountMotionAsync(cancellationToken);

            var success = await RunMountMotionAsync(
                () => telescopeMediator.SlewToCoordinatesAsync(target, cancellationToken),
                cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();

            var slewText = success
                ? Invariant($"Slewed to RA {raHours:0.0000} h, Dec {decDegrees:+0.0000;-0.0000}° (J2000).")
                : "The mount did not complete the slew (rejected by the driver, below the horizon limit or parked); the pins log has details.";
            var guiding = string.Empty;

            if (stoppedGuiding) {
                var restart = await RestartGuidingAfterMountMotionAsync(guidingStopsBefore, cancellationToken);

                if (restart == GuidingRestart.Cancelled) {
                    // A failure after the cancel request ends the job as cancelled with this message.
                    return OperationResult.Fail($"{slewText} Guiding was stopped for the slew; starting it again was cancelled.");
                }

                guiding = restart switch {
                    GuidingRestart.Started => " Guiding was stopped for the slew and started again.",
                    GuidingRestart.LeftStopped => " Guiding was stopped for the slew and stays stopped, because stop_guiding was called meanwhile.",
                    _ => " Guiding was stopped for the slew and could not be started again (see the pins log); start it with start_guiding."
                };
            }

            // The slew's result stands either way; only the guiding restart can fail.
            return success
                ? OperationResult.Ok($"{slewText}{guiding}")
                : OperationResult.Fail($"{slewText}{guiding}");
        } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
            // A failure after the cancel request ends the job as cancelled with this message.
            return OperationResult.Fail($"Slew cancelled.{GuidingNotRestarted(stoppedGuiding)}");
        } catch (Exception ex) {
            Logger.Error("MCP: slew failed", ex);
            return OperationResult.Fail($"Slew failed: {ex.Message}{GuidingNotRestarted(stoppedGuiding)}");
        }
    }

    private static string GuidingNotRestarted(bool stoppedGuiding) {
        return stoppedGuiding ? " Guiding was stopped for the slew and was not restarted; start it with start_guiding." : string.Empty;
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
    /// Like the ParkScope, FindHome and SlewScopeToRaDec instructions: no guide corrections to a mount on its way. A guider
    /// error must not keep the mount from moving, so it is only logged.
    /// </summary>
    /// <returns>Whether the guider was stopped (it was guiding or looping).</returns>
    private async Task<bool> StopGuidingBeforeMountMotionAsync(CancellationToken cancellationToken) {
        try {
            return await guiderMediator.StopGuiding(cancellationToken);
        } catch (OperationCanceledException) {
            throw;
        } catch (Exception ex) {
            Logger.Error("MCP: stopping guiding before a mount motion failed", ex);

            return false;
        }
    }

    /// <summary>
    /// Restarts guiding that a slew or centering stopped, still inside the job: cancel_job and stop_all stop it too, and a
    /// guider that does not come up within five minutes is given up on (the result of the motion stands either way). Not
    /// when stop_guiding was called since <paramref name="guidingStopsBefore"/> (read before the motion stopped guiding),
    /// also while guiding started.
    /// </summary>
    private async Task<GuidingRestart> RestartGuidingAfterMountMotionAsync(int guidingStopsBefore, CancellationToken cancellationToken) {
        if (Volatile.Read(ref guidingStops) != guidingStopsBefore) {
            Logger.Info("MCP: guiding stays stopped after a mount motion: stop_guiding was called meanwhile");

            return GuidingRestart.LeftStopped;
        }

        var outcome = GuidingRestart.Failed;

        try {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(5));

            try {
                if (await guiderMediator.StartGuiding(false, CreateProgress(), timeout.Token)) {
                    outcome = GuidingRestart.Started;
                } else {
                    Logger.Warning("MCP: guiding could not be restarted after a mount motion");
                }
            } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
                Logger.Warning("MCP: restarting guiding after a mount motion was cancelled");
                outcome = GuidingRestart.Cancelled;
            } catch (OperationCanceledException) when (timeout.IsCancellationRequested) {
                Logger.Warning("MCP: restarting guiding after a mount motion timed out");
            }
        } catch (OperationCanceledException ex) {
            Logger.Warning($"MCP: the guider cancelled restarting guiding after a mount motion: {ex.Message}");
        } catch (Exception ex) {
            Logger.Error("MCP: restarting guiding after a mount motion failed", ex);
        }

        if (outcome != GuidingRestart.Started && cancellationToken.IsCancellationRequested) {
            outcome = GuidingRestart.Cancelled;
        }

        // A stop_guiding while guiding started (calibration can take minutes) wins: guiding is stopped again, also when
        // the start did not report success (PHD2 can be guiding after a retry that then timed out).
        if (Volatile.Read(ref guidingStops) != guidingStopsBefore) {
            await StopGuidingAgainAsync();
            Logger.Info("MCP: guiding stays stopped after a mount motion: stop_guiding was called during the restart");

            return outcome == GuidingRestart.Cancelled ? GuidingRestart.Cancelled : GuidingRestart.LeftStopped;
        }

        return outcome;
    }

    private async Task StopGuidingAgainAsync() {
        try {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

            await guiderMediator.StopGuiding(timeout.Token);
        } catch (Exception ex) {
            Logger.Error("MCP: stopping guiding again after a stop_guiding during its restart failed", ex);
        }
    }

    private enum GuidingRestart {
        Started,
        Failed,
        Cancelled,

        /// <summary>stop_guiding was called during the motion or the restart.</summary>
        LeftStopped
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

        // Also when it fails: whoever asked wants guiding stopped, so a running slew or centering must not restart it.
        // Counted before and after the stop, which can take seconds: a motion that read the count meanwhile, while the
        // guider still reported guiding, sees the second one.
        Interlocked.Increment(ref guidingStops);

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
        } finally {
            Interlocked.Increment(ref guidingStops);
        }
    }

    // ----- Switches -----

    public async Task<OperationResult> SetSwitchAsync(SwitchTarget switchTarget, double value, CancellationToken cancellationToken) {
        var info = switchMediator.GetInfo();

        if (info?.Connected != true) {
            return OperationResult.Fail("Switch device is not connected.");
        }

        List<IWritableSwitch> writable = info.WritableSwitches?.ToList() ?? [];
        IWritableSwitch target;

        if (switchTarget.ByIdentity) {
            // As SwitchVM.SetSwitchValueByIdentity matches: over all switches, exactly one, and it must be writable.
            var matches = writable.Cast<ISwitch>()
                .Concat(info.ReadonlySwitches ?? Enumerable.Empty<ISwitch>())
                .Where(candidate => SwitchIdentity(candidate, info) is { } identity
                    && string.Equals(identity.DeviceId, switchTarget.DeviceId, StringComparison.Ordinal)
                    && identity.ChannelId == switchTarget.ChannelId)
                .ToList();

            if (matches.Count != 1 || matches[0] is not IWritableSwitch match || !writable.Contains(match)) {
                return OperationResult.Fail($"No single writable switch {switchTarget}; read get_status again (the switch list may have changed).");
            }

            target = match;
        } else {
            var index = switchTarget.Index!.Value;

            if (index < 0 || index >= writable.Count) {
                return OperationResult.Fail($"No writable switch with index {index}; there are {writable.Count} (see get_status).");
            }

            target = writable[index];

            if (!string.Equals(target.Name, switchTarget.Name, StringComparison.Ordinal)) {
                return OperationResult.Fail($"Switch {index} is now \"{target.Name}\", not {switchTarget}: the switch list changed. Read get_status again.");
            }
        }

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
            // SwitchVM waits for the value itself and throws when it is not reached or the switch can't be read. Written by
            // identity also when the index form found the switch: a list rebuilt meanwhile then refuses instead of
            // writing whatever switch is at that index now.
            var identity = SwitchIdentity(target, info);

            if (identity is { } stable) {
                await switchMediator.SetSwitchValueByIdentity(stable.DeviceId, stable.ChannelId, value, CreateProgress(), cancellationToken);
            } else {
#pragma warning disable CS0618 // No device id, so no identity to write by (as the sequencer's Set Switch Value falls back).
                await switchMediator.SetSwitchValue((short)writable.IndexOf(target), value, CreateProgress(), cancellationToken);
#pragma warning restore CS0618
            }

            // From the live list: SwitchVM wrote the switch it resolved there, which is another object when the list was
            // rebuilt since the snapshot above.
            var reached = ReadSwitchValue(identity, writable.IndexOf(target), target.Name);

            if (reached is null) {
                return OperationResult.Fail($"Switch {target.Name} is no longer listed: the switch list changed, so the write may have gone to another switch. Read get_status again.");
            }

            if (Math.Abs(reached.Value - value) > 1e-5) {
                return OperationResult.Fail(Invariant($"Switch {target.Name} did not reach {value}; it reports {reached}."));
            }

            return OperationResult.Ok(Invariant($"Switch {target.Name} set to {value}."));
        } catch (OperationCanceledException) {
            throw;
        } catch (Exception ex) {
            Logger.Error("MCP: setting a switch failed", ex);
            return OperationResult.Fail($"Setting switch {target.Name} failed: {ex.Message}");
        }
    }

    /// <summary>
    /// The value of the switch with <paramref name="identity"/>, or without one at <paramref name="index"/> when it still has
    /// <paramref name="name"/>, now.
    /// </summary>
    private double? ReadSwitchValue((string DeviceId, short ChannelId)? identity, int index, string name) {
        var info = switchMediator.GetInfo();

        if (info?.Connected != true) {
            return null;
        }

        var writable = info.WritableSwitches?.ToList() ?? [];

        if (identity is not { } stable) {
            return index >= 0 && index < writable.Count && string.Equals(writable[index].Name, name, StringComparison.Ordinal)
                ? writable[index].Value
                : null;
        }

        var matches = writable.Cast<ISwitch>()
            .Concat(info.ReadonlySwitches ?? Enumerable.Empty<ISwitch>())
            .Where(candidate => SwitchIdentity(candidate, info) == stable)
            .ToList();

        return matches.Count == 1 ? matches[0].Value : null;
    }

    /// <summary>
    /// The stable identity of a switch, as the sequencer's Set Switch Value records it: a hub channel's own device and
    /// channel id, otherwise the switch device's id and the switch id; null without a device id.
    /// </summary>
    internal static (string DeviceId, short ChannelId)? SwitchIdentity(ISwitch candidate, SwitchInfo info) {
        if (candidate is ICompositeSwitchIdentity composite) {
            return string.IsNullOrEmpty(composite.DeviceId) ? null : (composite.DeviceId, composite.ChannelId);
        }

        return string.IsNullOrEmpty(info.DeviceId) ? null : (info.DeviceId, candidate.Id);
    }
}
