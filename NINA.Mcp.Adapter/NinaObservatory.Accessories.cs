// SPDX-License-Identifier: MPL-2.0

using NINA.Core.Utility;
using NINA.Equipment.Interfaces;
using NINA.Mcp.Observatory;
using static System.FormattableString;

namespace NINA.Mcp.Adapter;

public sealed partial class NinaObservatory {

    // ----- Rotator -----

    /// <summary>RotatorVM halts the rotator when the token is cancelled and returns NaN when the move did not finish.</summary>
    public async Task<OperationResult> MoveRotatorAsync(double degrees, bool mechanical, CancellationToken cancellationToken) {
        if (rotatorMediator.GetInfo()?.Connected != true) {
            return OperationResult.Fail("Rotator is not connected.");
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

        if ((action is FlatDeviceAction.LightOn or FlatDeviceAction.LightOff) && !info.SupportsOnOff) {
            return OperationResult.Fail("This flat device's light cannot be switched on or off.");
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
            switch (action) {
                case DomeAction.OpenShutter:
                    return ShutterResult(await domeMediator.OpenShutter(cancellationToken), "open", ShutterState.ShutterOpen);

                case DomeAction.CloseShutter:
                    return ShutterResult(await domeMediator.CloseShutter(cancellationToken), "close", ShutterState.ShutterClosed);

                case DomeAction.Park:
                    return await domeMediator.Park(cancellationToken)
                        ? OperationResult.Ok("Dome parked.")
                        : OperationResult.Fail("The dome could not be parked; the pins log has details.");

                case DomeAction.FindHome:
                    return await domeMediator.FindHome(cancellationToken)
                        ? OperationResult.Ok("Dome is at home.")
                        : OperationResult.Fail("The dome could not find home; the pins log has details.");

                case DomeAction.SlewToAzimuth:
                    if (domeMediator.IsFollowingScope) {
                        return OperationResult.Fail("The dome follows the telescope; stop_following first, or it would turn back.");
                    }

                    return await domeMediator.SlewToAzimuth(azimuthDegrees!.Value, cancellationToken)
                        ? OperationResult.Ok(Invariant($"Dome turned to azimuth {azimuthDegrees.Value:0.#}°."))
                        : OperationResult.Fail("The dome could not turn to the azimuth; the pins log has details.");

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

    private OperationResult ShutterResult(bool success, string verb, ShutterState expected) {
        var state = domeMediator.GetInfo()?.ShutterStatus;

        if (!success) {
            return OperationResult.Fail($"The shutter did not {verb} (state {state?.ToString() ?? "unknown"}); the pins log has details.");
        }

        return state == expected
            ? OperationResult.Ok($"Shutter {(expected == ShutterState.ShutterOpen ? "open" : "closed")}.")
            : OperationResult.Ok($"The shutter command completed; the dome reports {state?.ToString() ?? "unknown"} (it updates once per polling interval).");
    }
}
