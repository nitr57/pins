// SPDX-License-Identifier: MPL-2.0

using NINA.Astrometry;
using NINA.Core.Model.Equipment;
using NINA.Core.Utility;
using NINA.Equipment.Model;
using NINA.Mcp.Observatory;
using NINA.PlateSolving;
using static System.FormattableString;

namespace NINA.Mcp.Adapter;

public sealed partial class NinaObservatory {

    // ----- Plate solving -----

    /// <summary>
    /// Like the SolveAndSync instruction: one capture solve with the profile's plate solve settings, then optionally a
    /// mount and rotator sync. Holds the camera capture block (unless the sequence runs), like <see cref="CaptureAsync"/>.
    /// </summary>
    public async Task<PlateSolveOutcome> PlateSolveAsync(bool sync, CancellationToken cancellationToken) {
        var camera = cameraMediator.GetInfo();

        if (camera?.Connected != true) {
            return new PlateSolveOutcome(false, "Camera is not connected.", null);
        }

        if (camera.IsExposing) {
            return new PlateSolveOutcome(false, "The camera is already exposing (another capture or the sequence uses it).", null);
        }

        var mountConnected = telescopeMediator.GetInfo()?.Connected == true;

        if (sync && !mountConnected) {
            return new PlateSolveOutcome(false, "sync needs a connected mount. Connect it, or solve without sync.", null);
        }

        if (!TryTakeCaptureBlock(out var holdsCaptureBlock)) {
            return new PlateSolveOutcome(false, CaptureBlockedMessage, null);
        }

        try {
            var reference = mountConnected ? telescopeMediator.GetCurrentPosition() : null;
            var settings = profileService.ActiveProfile.PlateSolveSettings;
            var plateSolver = plateSolverFactory.GetPlateSolver(settings);
            var blindSolver = plateSolverFactory.GetBlindSolver(settings);
            var solver = plateSolverFactory.GetCaptureSolver(plateSolver, blindSolver, imagingMediator, filterWheelMediator);

            var parameter = new CaptureSolverParameter();
            FillSolveParameter(parameter, reference);

            var result = await solver.Solve(CreatePlateSolveSequence(), parameter, null, CreateProgress(), cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();

            if (result == null || !result.Success || result.Coordinates == null) {
                return new PlateSolveOutcome(
                    false,
                    "Plate solving failed (no solution). Check focus, exposure and the solver settings of the profile; the pins log has details (read_log).",
                    null);
            }

            bool? synced = null;

            if (sync) {
                synced = await telescopeMediator.Sync(result.Coordinates);

                if (rotatorMediator.GetInfo()?.Connected == true) {
                    rotatorMediator.Sync((float)result.PositionAngle);
                }
            }

            var solution = ToSolution(result, reference, synced, null);
            var position = Invariant($"RA {result.Coordinates.RA:0.0000} h, Dec {result.Coordinates.Dec:+0.0000;-0.0000}°, position angle {result.PositionAngle:0.0}°");

            if (synced == false) {
                return new PlateSolveOutcome(false, $"Solved at {position}, but the mount rejected the sync.", solution);
            }

            return new PlateSolveOutcome(
                true,
                synced == true ? $"Solved at {position}; mount synced." : $"Solved at {position}.",
                solution);
        } catch (OperationCanceledException) {
            throw;
        } catch (Exception ex) {
            Logger.Error("MCP: plate solve failed", ex);
            return new PlateSolveOutcome(false, $"Plate solve failed: {ex.Message}", null);
        } finally {
            if (holdsCaptureBlock) {
                cameraMediator.ReleaseCaptureBlock(this);
            }
        }
    }

    /// <summary>
    /// Like the Center instruction: slew, then NINA's centering solver (solve, sync or measured offset, re-slew until
    /// within the profile threshold). Guiding is stopped first and restarted when it was running.
    /// </summary>
    public async Task<PlateSolveOutcome> CenterAsync(double raHours, double decDegrees, CancellationToken cancellationToken) {
        var mount = telescopeMediator.GetInfo();

        if (mount?.Connected != true) {
            return new PlateSolveOutcome(false, "Mount is not connected.", null);
        }

        if (mount.AtPark) {
            return new PlateSolveOutcome(false, "Mount is parked. Unpark it first (unpark_mount).", null);
        }

        var camera = cameraMediator.GetInfo();

        if (camera?.Connected != true) {
            return new PlateSolveOutcome(false, "Camera is not connected.", null);
        }

        if (camera.IsExposing) {
            return new PlateSolveOutcome(false, "The camera is already exposing (another capture or the sequence uses it).", null);
        }

        if (!TryTakeCaptureBlock(out var holdsCaptureBlock)) {
            return new PlateSolveOutcome(false, CaptureBlockedMessage, null);
        }

        var stoppedGuiding = false;

        try {
            var target = new Coordinates(Angle.ByHours(raHours), Angle.ByDegree(decDegrees), Epoch.J2000);

            stoppedGuiding = await guiderMediator.StopGuiding(cancellationToken);

            var slewed = await RunMountMotionAsync(
                () => telescopeMediator.SlewToCoordinatesAsync(target, cancellationToken),
                cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();

            if (!slewed) {
                return new PlateSolveOutcome(false, "The mount did not complete the slew to the target (rejected by the driver, below the horizon limit or parked); the pins log has details.", null);
            }

            await SyncDomeIfNotFollowingAsync();

            var settings = profileService.ActiveProfile.PlateSolveSettings;
            var plateSolver = plateSolverFactory.GetPlateSolver(settings);
            var blindSolver = plateSolverFactory.GetBlindSolver(settings);
            var solver = plateSolverFactory.GetCenteringSolver(plateSolver, blindSolver, imagingMediator, telescopeMediator, filterWheelMediator, domeMediator, domeFollower);

            var parameter = new CenterSolveParameter {
                Threshold = settings.Threshold,
                NoSync = profileService.ActiveProfile.TelescopeSettings.NoSync
            };
            FillSolveParameter(parameter, target);

            PlateSolveResult? result;

            // The correction slews are mount motions too: stop_all and cancellation must be able to stop them.
            Interlocked.Increment(ref mountMotionsInFlight);

            try {
                using (cancellationToken.Register(StopSlew)) {
                    result = await solver.Center(CreatePlateSolveSequence(), parameter, null, CreateProgress(), cancellationToken);
                }
            } finally {
                Interlocked.Decrement(ref mountMotionsInFlight);
            }

            cancellationToken.ThrowIfCancellationRequested();

            var attempts = (result as CenteringSolveResult)?.Attempts?.Count;

            if (result == null || result.Coordinates == null) {
                return new PlateSolveOutcome(false, "Centering failed: plate solving found no solution. The pins log has details (read_log).", null);
            }

            var solution = ToSolution(result, target, null, attempts);
            var error = Invariant($"{solution.ErrorArcmin ?? double.NaN:0.00}'");

            if (!result.Success) {
                return new PlateSolveOutcome(
                    false,
                    $"Centering failed after {attempts?.ToString() ?? "several"} attempt(s); the last solve was {error} from the target. The pins log has details (read_log).",
                    solution);
            }

            return new PlateSolveOutcome(
                true,
                Invariant($"Centered within {settings.Threshold:0.##}' after {attempts?.ToString() ?? "?"} attempt(s); remaining error {error}."),
                solution);
        } catch (OperationCanceledException) {
            throw;
        } catch (Exception ex) {
            Logger.Error("MCP: centering failed", ex);
            return new PlateSolveOutcome(false, $"Centering failed: {ex.Message}", null);
        } finally {
            if (holdsCaptureBlock) {
                cameraMediator.ReleaseCaptureBlock(this);
            }

            if (stoppedGuiding && !cancellationToken.IsCancellationRequested) {
                await RestartGuidingAfterCenterAsync();
            }
        }
    }

    private void FillSolveParameter(CaptureSolverParameter parameter, Coordinates? reference) {
        var profile = profileService.ActiveProfile;
        var settings = profile.PlateSolveSettings;

        parameter.Attempts = settings.NumberOfAttempts;
        parameter.Binning = settings.Binning;
        parameter.Coordinates = reference;
        parameter.DownSampleFactor = settings.DownSampleFactor;
        parameter.FocalLength = profile.TelescopeSettings.FocalLength;
        parameter.MaxObjects = settings.MaxObjects;
        parameter.PixelSize = profile.CameraSettings.PixelSize;
        parameter.ReattemptDelay = TimeSpan.FromMinutes(settings.ReattemptDelay);
        parameter.Regions = settings.Regions;
        parameter.SearchRadius = settings.SearchRadius;
        parameter.BlindFailoverEnabled = settings.BlindFailoverEnabled;
    }

    private CaptureSequence CreatePlateSolveSequence() {
        var settings = profileService.ActiveProfile.PlateSolveSettings;

        var sequence = new CaptureSequence(
            settings.ExposureTime,
            CaptureSequence.ImageTypes.SNAPSHOT,
            settings.Filter,
            new BinningMode(settings.Binning, settings.Binning),
            1);

        sequence.Gain = settings.Gain;

        return sequence;
    }

    /// <summary>As the Center instruction does after its slew: turn the slit to the scope when the dome does not follow it.</summary>
    private async Task SyncDomeIfNotFollowingAsync() {
        try {
            var dome = domeMediator.GetInfo();

            if (dome?.Connected == true && dome.CanSetAzimuth && !domeFollower.IsFollowing) {
                if (!await domeFollower.TriggerTelescopeSync()) {
                    Logger.Warning("MCP: synchronizing the dome to the scope did not complete before centering");
                }
            }
        } catch (Exception ex) {
            Logger.Error("MCP: synchronizing the dome to the scope failed", ex);
        }
    }

    /// <summary>Restarts guiding that centering stopped. Its own token: the job's may already be done.</summary>
    private async Task RestartGuidingAfterCenterAsync() {
        try {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));

            if (!await guiderMediator.StartGuiding(false, CreateProgress(), timeout.Token)) {
                Logger.Warning("MCP: guiding could not be restarted after centering");
            }
        } catch (Exception ex) {
            Logger.Error("MCP: restarting guiding after centering failed", ex);
        }
    }

    /// <summary>
    /// The solution with its distance from <paramref name="reference"/> (target or mount position); the RA error is the
    /// on-sky distance along RA (scaled by cos Dec).
    /// </summary>
    private static PlateSolution ToSolution(PlateSolveResult result, Coordinates? reference, bool? synced, int? attempts) {
        var solved = result.Coordinates;
        double? errorArcmin = null;
        double? raErrorArcsec = null;
        double? decErrorArcsec = null;

        if (reference != null) {
            var separation = reference - solved;
            var raDegrees = AstroUtil.EuclidianModulus(separation.RA.Degree + 180, 360) - 180;

            errorArcmin = Number(separation.Distance.ArcMinutes);
            raErrorArcsec = Number(raDegrees * 3600 * Math.Cos(solved.Transform(reference.Epoch).Dec * Math.PI / 180));
            decErrorArcsec = Number(separation.Dec.ArcSeconds);
        }

        return new PlateSolution(
            solved.RA,
            solved.Dec,
            result.PositionAngle,
            result.Pixscale,
            result.Flipped,
            errorArcmin,
            raErrorArcsec,
            decErrorArcsec,
            synced,
            attempts);
    }
}
