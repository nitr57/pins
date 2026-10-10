// SPDX-License-Identifier: MPL-2.0

namespace NINA.GuideEngine.Guiding;

public enum GuideErrorSeverity
{
    Info,
    Warning,

    /// <summary>Stops guiding (triggers a push notification in the host).</summary>
    Critical,
}

/// <summary>Stable error codes shared by logs, API and UI. Never renumber; append only.</summary>
public enum GuideErrorCode
{
    None = 0,

    // Stars 100-199
    StarLost = 100,
    StarReacquireTimeout = 101,
    StarSaturated = 102,
    StarLowSnr = 103,
    StarMassChanged = 104,
    NoStarFound = 105,
    LockPositionNearEdge = 106,
    PrimaryEstimatedFromSecondaries = 107,
    StarReacquired = 108,
    SecondaryStarsRefreshed = 109,

    // Calibration 200-299
    CalibrationFailedRaNoMove = 200,
    CalibrationFailedDecNoMove = 201,
    CalibrationFailedBacklash = 202,
    CalibrationFailedStarLost = 203,
    CalibrationFewSteps = 210,
    CalibrationNotOrthogonal = 211,
    CalibrationRateRatio = 212,
    CalibrationDecRateChanged = 213,
    CalibrationInvalidated = 220,
    CalibrationHighDeclination = 221,
    CalibrationGuideRateChanged = 222,
    NotCalibrated = 223,
    CalibrationFlipped = 224,
    DecFlipCorrected = 225,
    CalibrationReturnIncomplete = 226,

    // Mount / output 300-399
    PulseLimitReached = 300,
    RunawayDetected = 301,
    MountNotResponding = 302,
    MountSlewing = 303,
    MountParked = 304,
    MountTrackingOff = 305,
    MountDisconnected = 306,
    PulseOutputFailed = 307,
    PierSideUnknown = 308,

    // Camera 400-499
    CameraCaptureFailed = 400,
    CameraReconnecting = 401,
    CameraFailed = 402,
    CameraDisconnected = 403,
    FrameProcessingSlow = 404,

    // Settle / dither 500-599
    SettleTimeout = 500,
    DitherFailed = 501,

    Internal = 900,
}

public sealed record GuideErrorInfo(GuideErrorCode Code, GuideErrorSeverity Severity, string Title, string Explanation, string Fix);

/// <summary>Human-readable explanation and suggested fix per error code (English; UI translates by code).</summary>
public static class GuideErrorCatalog
{
    private static readonly Dictionary<GuideErrorCode, GuideErrorInfo> Entries = new[]
    {
        E(GuideErrorCode.StarLost, GuideErrorSeverity.Warning, "Guide star lost",
            "The guide star could not be found in the last frame. No corrections are sent while it is lost.",
            "Check for clouds, obstructions or dew. The guider keeps searching automatically."),
        E(GuideErrorCode.StarReacquireTimeout, GuideErrorSeverity.Critical, "Guide star could not be reacquired",
            "The star stayed lost longer than the reacquire timeout; guiding was stopped.",
            "Check the sky and optics, then restart guiding (the sequencer's Restore Guiding trigger can do this)."),
        E(GuideErrorCode.StarSaturated, GuideErrorSeverity.Warning, "Guide star saturated",
            "The guide star's peak is saturated, which degrades centroid precision.",
            "Reduce exposure or gain, or let auto-selection choose a fainter star."),
        E(GuideErrorCode.StarLowSnr, GuideErrorSeverity.Warning, "Low star SNR",
            "The guide star's signal-to-noise ratio is low; centroids become noisy.",
            "Increase exposure or gain, or check focus of the guide camera."),
        E(GuideErrorCode.StarMassChanged, GuideErrorSeverity.Warning, "Star brightness changed abruptly",
            "The star's brightness changed more than the mass-change threshold; the frame was ignored.",
            "Usually passing clouds. If it persists, raise the mass-change threshold."),
        E(GuideErrorCode.NoStarFound, GuideErrorSeverity.Warning, "No suitable guide star",
            "Automatic star selection found no star meeting the SNR and saturation criteria.",
            "Increase exposure/gain, check focus and that the guide scope isn't covered."),
        E(GuideErrorCode.LockPositionNearEdge, GuideErrorSeverity.Warning, "Lock position too close to the edge",
            "The requested lock position is too close to the frame edge for the search region.",
            "Choose a star further from the edge or reduce the dither amount."),
        E(GuideErrorCode.PrimaryEstimatedFromSecondaries, GuideErrorSeverity.Info, "Primary star estimated",
            "The primary star was not measurable; its position was estimated from the secondary stars.",
            "No action needed. Frequent occurrences suggest choosing a different primary."),
        E(GuideErrorCode.StarReacquired, GuideErrorSeverity.Info, "Guide star reacquired",
            "The guide star was found again and guiding resumed.", "No action needed."),
        E(GuideErrorCode.SecondaryStarsRefreshed, GuideErrorSeverity.Info, "Secondary stars found again",
            "The secondary guide stars were no longer where they were expected (the guide star may have changed to a neighbouring star, or the field moved), so they were found again around the guide star.",
            "No action needed. If it happens often, check for clouds and that the guide star is not lost."),
        E(GuideErrorCode.CalibrationFailedRaNoMove, GuideErrorSeverity.Critical, "Calibration failed: RA did not move",
            "The star did not move far enough in RA during calibration.",
            "Check the guide output connection (mount/ST4), the guide rate, and that the mount is tracking and unparked."),
        E(GuideErrorCode.CalibrationFailedDecNoMove, GuideErrorSeverity.Critical, "Calibration failed: Dec did not move",
            "The star did not move far enough in Dec during calibration.",
            "Check Dec backlash, balance, cable snags; or calibrate/guide RA-only."),
        E(GuideErrorCode.CalibrationFailedBacklash, GuideErrorSeverity.Critical, "Calibration failed: Dec backlash not cleared",
            "Dec backlash could not be cleared before the Dec calibration move.",
            "Reduce Dec backlash (gear mesh, balance slightly east-heavy in Dec) and recalibrate."),
        E(GuideErrorCode.CalibrationFailedStarLost, GuideErrorSeverity.Critical, "Calibration failed: star lost",
            "The calibration star was lost too often during calibration.",
            "Choose a brighter star away from the edge and retry."),
        E(GuideErrorCode.CalibrationFewSteps, GuideErrorSeverity.Warning, "Calibration used few steps",
            "Calibration completed with fewer than 4 steps on an axis, so it may be inaccurate.",
            "Reduce the calibration step size and recalibrate."),
        E(GuideErrorCode.CalibrationNotOrthogonal, GuideErrorSeverity.Warning, "Calibration axes not perpendicular",
            "The measured RA and Dec axes are more than 12.5° from perpendicular.",
            "Check for Dec backlash, cable snag or wind during calibration; recalibrate."),
        E(GuideErrorCode.CalibrationRateRatio, GuideErrorSeverity.Warning, "Unexpected RA/Dec rate ratio",
            "The ratio of RA to Dec calibration rates doesn't match the declination.",
            "Check the mount's reported declination and guide rates; recalibrate near Dec 0 if possible."),
        E(GuideErrorCode.CalibrationDecRateChanged, GuideErrorSeverity.Warning, "Dec rate differs from last calibration",
            "The Dec rate differs by more than 20% from the previous calibration with the same setup.",
            "Recalibrate; check for backlash or a mechanical problem."),
        E(GuideErrorCode.CalibrationInvalidated, GuideErrorSeverity.Info, "Calibration invalidated",
            "The stored calibration no longer matches the camera, binning or focal length.",
            "A new calibration will run when guiding starts."),
        E(GuideErrorCode.CalibrationHighDeclination, GuideErrorSeverity.Warning, "Calibration taken at high declination",
            "The calibration was made above |60°| declination, so declination compensation is unreliable.",
            "Recalibrate closer to the celestial equator for best results."),
        E(GuideErrorCode.CalibrationGuideRateChanged, GuideErrorSeverity.Warning, "Guide rate changed",
            "The mount's guide rate differs by more than 5% from the calibration.",
            "Recalibrate, or restore the previous guide rate."),
        E(GuideErrorCode.NotCalibrated, GuideErrorSeverity.Warning, "Not calibrated",
            "Guiding requires a calibration.", "Start guiding with calibration."),
        E(GuideErrorCode.CalibrationFlipped, GuideErrorSeverity.Info, "Calibration flipped for pier side",
            "The calibration was adjusted for the new pier side after a meridian flip.", "No action needed."),
        E(GuideErrorCode.DecFlipCorrected, GuideErrorSeverity.Warning, "Dec direction corrected after flip",
            "Dec corrections increased the error after the meridian flip, so the Dec direction was inverted and the mount setting saved.",
            "No action needed; recalibrating is recommended if guiding looks unstable."),
        E(GuideErrorCode.CalibrationReturnIncomplete, GuideErrorSeverity.Warning, "Calibration return move incomplete",
            "The star did not return near its starting point during calibration; this may indicate backlash or a slipping clutch.",
            "Check the mount mechanics and recalibrate."),
        E(GuideErrorCode.PulseLimitReached, GuideErrorSeverity.Warning, "Correction limited by max pulse",
            "Several consecutive corrections were clamped to the maximum pulse length.",
            "Check for a large drift (polar alignment, cable snag) or increase the max pulse duration."),
        E(GuideErrorCode.RunawayDetected, GuideErrorSeverity.Critical, "Runaway guiding detected",
            "Corrections kept growing without reducing the error (e.g. wrong calibration sign). Guiding was stopped to protect the session.",
            "Clear the calibration and recalibrate; verify the pier-side/Dec-flip mount setting."),
        E(GuideErrorCode.MountNotResponding, GuideErrorSeverity.Critical, "Mount not responding to corrections",
            "Several large corrections produced no star movement.",
            "Check the guide output connection (mount driver/ST4 cable) and that the mount is tracking."),
        E(GuideErrorCode.MountSlewing, GuideErrorSeverity.Info, "Mount slewing — guiding paused",
            "Guiding pauses automatically while the mount slews.",
            "Guiding resumes after the slew. If this appears while the mount is not slewing, turn off the guider setting 'Pause while mount slews'."),
        E(GuideErrorCode.MountParked, GuideErrorSeverity.Warning, "Mount parked — guiding paused",
            "The mount reports parked; no corrections are sent.", "Unpark the mount and restart guiding."),
        E(GuideErrorCode.MountTrackingOff, GuideErrorSeverity.Warning, "Tracking off — guiding paused",
            "The mount is not tracking; no corrections are sent.",
            "Enable tracking; guiding resumes automatically. If the mount is tracking but its driver cannot report it, turn off the guider setting 'Pause when tracking is off'."),
        E(GuideErrorCode.MountDisconnected, GuideErrorSeverity.Critical, "Mount disconnected",
            "The mount or guide output disconnected.", "Reconnect the mount and restart guiding."),
        E(GuideErrorCode.PulseOutputFailed, GuideErrorSeverity.Warning, "Guide pulse failed",
            "A guide pulse could not be sent.", "Check the mount/guide port connection."),
        E(GuideErrorCode.PierSideUnknown, GuideErrorSeverity.Warning, "Pier side unknown",
            "The mount does not report its pier side; meridian flips cannot be compensated automatically.",
            "Recalibrate after each meridian flip, or use a driver that reports pier side."),
        E(GuideErrorCode.CameraCaptureFailed, GuideErrorSeverity.Warning, "Guide exposure failed",
            "A guide camera exposure failed or timed out; retrying.", "Check the guide camera USB connection and power."),
        E(GuideErrorCode.CameraReconnecting, GuideErrorSeverity.Warning, "Reconnecting guide camera",
            "Repeated exposure failures; reconnecting the guide camera.", "Check the guide camera USB connection and power."),
        E(GuideErrorCode.CameraFailed, GuideErrorSeverity.Critical, "Guide camera failed",
            "The guide camera could not deliver frames after retries and a reconnect. Guiding was stopped.",
            "Check cables/power, reconnect the guide camera and restart guiding."),
        E(GuideErrorCode.CameraDisconnected, GuideErrorSeverity.Critical, "Guide camera disconnected",
            "The guide camera is not connected.", "Connect the guide camera."),
        E(GuideErrorCode.FrameProcessingSlow, GuideErrorSeverity.Warning, "Frame processing slow",
            "Processing a guide frame took longer than expected, which delays corrections.",
            "Reduce the max number of guide stars, use binning or a subframe, or check CPU load."),
        E(GuideErrorCode.SettleTimeout, GuideErrorSeverity.Warning, "Settling timed out",
            "Guiding did not settle within the timeout.", "Increase settle tolerance/timeout, or check guiding performance."),
        E(GuideErrorCode.DitherFailed, GuideErrorSeverity.Warning, "Dither failed",
            "Dither requires active, calibrated guiding.", "Start guiding before dithering."),
        E(GuideErrorCode.Internal, GuideErrorSeverity.Critical, "Internal guider error",
            "An unexpected error occurred in the guider.", "See the log for details and report the issue."),
    }.ToDictionary(e => e.Code);

    public static GuideErrorInfo Get(GuideErrorCode code) =>
        Entries.TryGetValue(code, out var e) ? e : new GuideErrorInfo(code, GuideErrorSeverity.Warning, code.ToString(), code.ToString(), string.Empty);

    public static IReadOnlyCollection<GuideErrorInfo> All => Entries.Values;

    private static GuideErrorInfo E(GuideErrorCode c, GuideErrorSeverity s, string title, string explanation, string fix) => new(c, s, title, explanation, fix);
}
