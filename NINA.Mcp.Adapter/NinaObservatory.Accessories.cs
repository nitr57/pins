// SPDX-License-Identifier: MPL-2.0

using NINA.Core.Utility;
using NINA.Equipment.Interfaces;
using NINA.Mcp.Observatory;
using static System.FormattableString;

namespace NINA.Mcp.Adapter;

public sealed partial class NinaObservatory {

    /// <summary>Upper bound for closing the shutter, which no MCP cancellation may stop.</summary>
    private static readonly TimeSpan ShutterCloseTimeout = TimeSpan.FromMinutes(10);

    // ----- Rotator -----

    /// <summary>RotatorVM halts the rotator when the token is cancelled and returns NaN when the move did not finish.</summary>
    public async Task<OperationResult> MoveRotatorAsync(double degrees, bool mechanical, CancellationToken cancellationToken) {
        var info = rotatorMediator.GetInfo();

        if (info?.Connected != true) {
            return OperationResult.Fail("Rotator is not connected.");
        }

        // RotatorVM.GetTargetPosition throws for sky angles until a plate solve synced the rotator.
        if (!mechanical && !info.Synced) {
            return OperationResult.Fail(
                "The rotator is not synced yet, so it has no sky position angle: run plate_solve with sync first, or pass mechanical: true.");
        }

        try {
            var target = (float)degrees;

            var reached = mechanical
                ? await rotatorMediator.MoveMechanical(target, cancellationToken)
                : await rotatorMediator.Move(target, cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();

            if (float.IsNaN(reached)) {
                return OperationResult.Fail("The rotator did not complete the move; the pins log has details.");
            }

            var kind = mechanical ? "mechanical position" : "sky position angle";

            return OperationResult.Ok(Invariant($"Rotator at {reached:0.##}° ({kind}; requested {degrees:0.##}°)."));
        } catch (OperationCanceledException) {
            throw;
        } catch (Exception ex) {
            Logger.Error("MCP: rotator move failed", ex);
            return OperationResult.Fail($"Rotator move failed: {ex.Message}");
        }
    }

    // ----- Flat device -----

    /// <summary>The flat device mediator reports no result; the outcome is checked against the device state afterwards.</summary>
    public async Task<OperationResult> ControlFlatDeviceAsync(FlatDeviceAction action, int? brightness, CancellationToken cancellationToken) {
        var info = flatDeviceMediator.GetInfo();

        if (info?.Connected != true) {
            return OperationResult.Fail("Flat device is not connected.");
        }

        if ((action is FlatDeviceAction.OpenCover or FlatDeviceAction.CloseCover) && !info.SupportsOpenClose) {
            return OperationResult.Fail("This flat device has no cover that can be opened or closed.");
        }

        // SupportsOnOff means a calibrator (light) is present; without one the drivers ignore brightness changes.
        if ((action is FlatDeviceAction.LightOn or FlatDeviceAction.LightOff or FlatDeviceAction.SetBrightness) && !info.SupportsOnOff) {
            return OperationResult.Fail("This flat device has no light (calibrator) that can be switched or dimmed.");
        }

        if (action == FlatDeviceAction.SetBrightness
            && (brightness is null || brightness < info.MinBrightness || brightness > info.MaxBrightness)) {
            return OperationResult.Fail(Invariant($"Brightness must be between {info.MinBrightness} and {info.MaxBrightness} (got {brightness})."));
        }

        try {
            var progress = CreateProgress();

            switch (action) {
                case FlatDeviceAction.OpenCover:
                    await flatDeviceMediator.OpenCover(progress, cancellationToken);
                    break;

                case FlatDeviceAction.CloseCover:
                    await flatDeviceMediator.CloseCover(progress, cancellationToken);
                    break;

                case FlatDeviceAction.LightOn:
                    await flatDeviceMediator.ToggleLight(true, progress, cancellationToken);
                    break;

                case FlatDeviceAction.LightOff:
                    await flatDeviceMediator.ToggleLight(false, progress, cancellationToken);
                    break;

                case FlatDeviceAction.SetBrightness:
                    await flatDeviceMediator.SetBrightness(brightness!.Value, progress, cancellationToken);
                    break;
            }

            cancellationToken.ThrowIfCancellationRequested();

            return await VerifyFlatDeviceAsync(action, brightness, cancellationToken);
        } catch (OperationCanceledException) {
            throw;
        } catch (Exception ex) {
            Logger.Error("MCP: flat device command failed", ex);
            return OperationResult.Fail($"Flat device command failed: {ex.Message}");
        }
    }

    /// <summary>GetInfo is refreshed once per device polling interval: wait up to two of them for the expected state.</summary>
    private async Task<OperationResult> VerifyFlatDeviceAsync(FlatDeviceAction action, int? brightness, CancellationToken cancellationToken) {
        var pollingSeconds = profileService.ActiveProfile.ApplicationSettings.DevicePollingInterval;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(Math.Max(pollingSeconds, 0.5) * 2 + 1);

        while (true) {
            var info = flatDeviceMediator.GetInfo();

            var reached = action switch {
                FlatDeviceAction.OpenCover => info.CoverState == CoverState.Open,
                FlatDeviceAction.CloseCover => info.CoverState == CoverState.Closed,
                FlatDeviceAction.LightOn => info.LightOn,
                FlatDeviceAction.LightOff => !info.LightOn,
                _ => info.Brightness == brightness
            };

            if (reached) {
                return action switch {
                    FlatDeviceAction.OpenCover => OperationResult.Ok("Cover open."),
                    FlatDeviceAction.CloseCover => OperationResult.Ok("Cover closed."),
                    FlatDeviceAction.LightOn => OperationResult.Ok(Invariant($"Light on (brightness {info.Brightness}).")),
                    FlatDeviceAction.LightOff => OperationResult.Ok("Light off."),
                    _ => OperationResult.Ok(Invariant($"Brightness set to {info.Brightness}."))
                };
            }

            if (DateTime.UtcNow >= deadline) {
                return OperationResult.Fail(
                    Invariant($"The flat device did not report the expected state (cover {info.CoverState}, light {(info.LightOn ? "on" : "off")}, brightness {info.Brightness}); the pins log has details."));
            }

            await Task.Delay(250, cancellationToken);
        }
    }

    // ----- Dome -----

    public async Task<OperationResult> ControlDomeAsync(DomeAction action, double? azimuthDegrees, CancellationToken cancellationToken) {
        var info = domeMediator.GetInfo();

        if (info?.Connected != true) {
            return OperationResult.Fail("Dome is not connected.");
        }

        var unsupported = action switch {
            DomeAction.OpenShutter or DomeAction.CloseShutter when !info.CanSetShutter => "This dome's shutter cannot be operated by the driver.",
            DomeAction.Park when !info.CanPark => "This dome cannot park.",
            DomeAction.FindHome when !info.CanFindHome => "This dome cannot find home.",
            DomeAction.SlewToAzimuth or DomeAction.SyncToMount or DomeAction.StartFollowing when !info.CanSetAzimuth => "This dome cannot be turned to an azimuth (e.g. a roll-off roof).",
            _ => null
        };

        if (unsupported != null) {
            return OperationResult.Fail(unsupported);
        }

        try {
            // DomeVM passes the token on, but the INDI dome only stops waiting: stop the motion itself on cancel. Never for
            // closing: stop_all and secure_observatory cancel every job, and a generic stop must not leave the roof open
            // (the close does not get the job's token either).
            using var stopOnCancel = action == DomeAction.CloseShutter
                ? default
                : cancellationToken.Register(StopDome);

            switch (action) {
                case DomeAction.OpenShutter:
                    return await ShutterResultAsync(await domeMediator.OpenShutter(cancellationToken), "open", ShutterState.ShutterOpen, cancellationToken);

                case DomeAction.CloseShutter:
                    return await ShutterResultAsync(await CloseShutterUncancellableAsync(cancellationToken), "close", ShutterState.ShutterClosed, cancellationToken);

                case DomeAction.Park:
                    if (!await domeMediator.Park(cancellationToken)) {
                        return OperationResult.Fail("The dome could not be parked; the pins log has details.");
                    }

                    // DomeVM reports success whenever the dome is connected: check the device.
                    return await WaitForStateAsync(() => domeMediator.GetInfo().AtPark, cancellationToken)
                        ? OperationResult.Ok("Dome parked.")
                        : OperationResult.Fail("The park command completed, but the dome does not report parked; the pins log has details.");

                case DomeAction.FindHome:
                    if (!await domeMediator.FindHome(cancellationToken)) {
                        return OperationResult.Fail("The dome could not find home; the pins log has details.");
                    }

                    // The INDI dome returns as soon as the command is sent: wait for the rotation to end (at most five minutes).
                    if (!await WaitWhileSlewingAsync(cancellationToken)) {
                        return OperationResult.Fail("The dome was still rotating five minutes after find home; check it.");
                    }

                    // Some INDI drivers have no at-home property, so a stopped dome that does not report it is not a failure.
                    return domeMediator.GetInfo().AtHome
                        ? OperationResult.Ok("Dome is at home.")
                        : OperationResult.Ok("The dome stopped after find home, but its driver does not report being at home.");

                case DomeAction.SlewToAzimuth:
                    if (domeMediator.IsFollowingScope) {
                        return OperationResult.Fail("The dome follows the telescope; stop_following first, or it would turn back.");
                    }

                    var target = azimuthDegrees!.Value;

                    if (!await domeMediator.SlewToAzimuth(target, cancellationToken)) {
                        return OperationResult.Fail("The dome could not turn to the azimuth; the pins log has details.");
                    }

                    var tolerance = Math.Max(profileService.ActiveProfile.DomeSettings.AzimuthTolerance_degrees, 1);

                    if (!await WaitForStateAsync(() => AzimuthDistance(domeMediator.GetInfo().Azimuth, target) <= tolerance, cancellationToken)) {
                        return OperationResult.Fail(Invariant(
                            $"The slew completed, but the dome reports azimuth {domeMediator.GetInfo().Azimuth:0.#}° instead of {target:0.#}°."));
                    }

                    return OperationResult.Ok(Invariant($"Dome turned to azimuth {domeMediator.GetInfo().Azimuth:0.#}°."));

                case DomeAction.SyncToMount:
                    if (telescopeMediator.GetInfo()?.Connected != true) {
                        return OperationResult.Fail("Mount is not connected; the dome needs its position.");
                    }

                    return await domeFollower.TriggerTelescopeSync()
                        ? OperationResult.Ok("Dome slit turned to the telescope.")
                        : OperationResult.Fail("The dome could not be synchronized to the telescope; the pins log has details.");

                case DomeAction.StartFollowing:
                    return await domeMediator.EnableFollowing(cancellationToken)
                        ? OperationResult.Ok("The dome now follows the telescope.")
                        : OperationResult.Fail("Dome following could not be enabled; the pins log has details.");

                case DomeAction.StopFollowing:
                    return await domeMediator.DisableFollowing(cancellationToken)
                        ? OperationResult.Ok("The dome no longer follows the telescope.")
                        : OperationResult.Fail("Dome following could not be disabled; the pins log has details.");

                default:
                    return OperationResult.Fail($"Unknown dome action {action}.");
            }
        } catch (OperationCanceledException) {
            throw;
        } catch (Exception ex) {
            Logger.Error("MCP: dome command failed", ex);
            return OperationResult.Fail($"Dome command failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Closes on a token of its own: DomeVM hands its token to the driver (ASCOM and Alpaca abort the shutter when it is
    /// cancelled) and to the park-before-close steps, and stop_all, secure_observatory and the pins shutdown cancel every
    /// job. <paramref name="cancellationToken"/> only ends the wait; the close itself goes on (at most ten minutes).
    /// </summary>
    private async Task<bool> CloseShutterUncancellableAsync(CancellationToken cancellationToken) {
        var timeout = new CancellationTokenSource(ShutterCloseTimeout);
        var close = domeMediator.CloseShutter(timeout.Token);

        _ = close.ContinueWith(
            task => {
                timeout.Dispose();

                if (cancellationToken.IsCancellationRequested) {
                    Logger.Info($"MCP: the dome close that a cancelled job started has ended ({(task.IsCompletedSuccessfully ? task.Result.ToString() : task.Status.ToString())})");
                }
            },
            TaskScheduler.Default);

        return await close.WaitAsync(cancellationToken);
    }

    /// <summary>
    /// DomeVM has already waited for the motion and one device update. An error or the opposite end state is a failure;
    /// a shutter still reported moving (or without state) is reported as it is.
    /// </summary>
    private async Task<OperationResult> ShutterResultAsync(bool success, string verb, ShutterState expected, CancellationToken cancellationToken) {
        if (!success) {
            return OperationResult.Fail($"The shutter did not {verb} (state {domeMediator.GetInfo()?.ShutterStatus.ToString() ?? "unknown"}); the pins log has details.");
        }

        await WaitForStateAsync(() => domeMediator.GetInfo().ShutterStatus == expected, cancellationToken);

        var state = domeMediator.GetInfo().ShutterStatus;
        var opposite = expected == ShutterState.ShutterOpen ? ShutterState.ShutterClosed : ShutterState.ShutterOpen;

        if (state == expected) {
            return OperationResult.Ok($"Shutter {(expected == ShutterState.ShutterOpen ? "open" : "closed")}.");
        }

        if (state == ShutterState.ShutterError || state == opposite) {
            return OperationResult.Fail($"The shutter command completed, but the dome reports {state}; check the dome before relying on it.");
        }

        return OperationResult.Ok($"The shutter command completed; the dome reports {state}. Check get_status again shortly.");
    }

    /// <summary>Polls <paramref name="reached"/> for up to two device polling intervals (GetInfo is refreshed once per interval).</summary>
    private async Task<bool> WaitForStateAsync(Func<bool> reached, CancellationToken cancellationToken) {
        var pollingSeconds = profileService.ActiveProfile.ApplicationSettings.DevicePollingInterval;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(Math.Max(pollingSeconds, 0.5) * 2 + 1);

        while (!reached()) {
            if (DateTime.UtcNow >= deadline) {
                return false;
            }

            await Task.Delay(250, cancellationToken);
        }

        return true;
    }

    /// <summary>Waits until the dome reports not slewing, after one polling interval for the state to appear.</summary>
    private async Task<bool> WaitWhileSlewingAsync(CancellationToken cancellationToken) {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));

        var pollingSeconds = profileService.ActiveProfile.ApplicationSettings.DevicePollingInterval;

        try {
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(pollingSeconds, 0.5)), timeout.Token);

            while (domeMediator.GetInfo().Slewing) {
                await Task.Delay(500, timeout.Token);
            }

            return true;
        } catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) {
            return false;
        }
    }

    private static double AzimuthDistance(double a, double b) {
        if (!double.IsFinite(a) || !double.IsFinite(b)) {
            return double.PositiveInfinity;
        }

        var difference = Math.Abs(a - b) % 360;

        return difference > 180 ? 360 - difference : difference;
    }

    /// <summary>Stops shutter and rotation (cancel_job, stop_all). Never throws: it runs from a token callback.</summary>
    private void StopDome() {
        try {
            if (domeMediator.GetDevice() is IDome dome) {
                dome.StopAll().ContinueWith(
                    task => Logger.Error("MCP: stopping the dome failed", task.Exception!),
                    TaskContinuationOptions.OnlyOnFaulted);
            }
        } catch (Exception ex) {
            Logger.Error("MCP: stopping the dome failed", ex);
        }
    }
}
