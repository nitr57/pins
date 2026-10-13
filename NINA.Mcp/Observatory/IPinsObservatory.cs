// SPDX-License-Identifier: MPL-2.0

namespace NINA.Mcp.Observatory;

/// <summary>
/// What the MCP tools can see and do in pins. Implemented in <c>NINA.Mcp.Adapter</c> on top of the NINA mediators;
/// faked in the tests. Keeps <c>NINA.Mcp</c> free of NINA references.
/// </summary>
/// <remarks>
/// Rules for implementations:
/// <list type="bullet">
/// <item>Expected failures (device not connected, no such filter, sequence busy) are returned as
/// <see cref="OperationResult"/> with <c>Success = false</c> and a message for the model, not thrown.</item>
/// <item>Long operations honour their cancellation token: cancelling it stops the hardware operation where NINA allows
/// (abort exposure, stop slew, stop focuser) before the task completes.</item>
/// <item>Units: RA in hours (0..24), Dec, altitude, azimuth and rotation in degrees, temperatures in °C, exposure in
/// seconds, focuser positions in steps.</item>
/// <item>Property bags (<see cref="DeviceStatus.Properties"/>, <see cref="SequenceNode.Details"/>) hold only JSON friendly
/// values (string, bool, numbers, null, nested dictionaries/lists of those); NaN and infinities become null.</item>
/// </list>
/// </remarks>
public interface IPinsObservatory {

    // ----- State -----

    /// <summary>Snapshot of every device slot plus the sequence run state. Must not block on hardware.</summary>
    ObservatoryStatus GetStatus();

    /// <summary>The newest image history entries, newest first.</summary>
    IReadOnlyList<ImageRecord> GetRecentImages(int count);

    // ----- Equipment connection (uses the device selected in the active profile) -----

    Task<OperationResult> ConnectAsync(DeviceKind kind, CancellationToken cancellationToken);

    Task<OperationResult> DisconnectAsync(DeviceKind kind, CancellationToken cancellationToken);

    // ----- Mount -----

    /// <summary>Slews to J2000 coordinates and returns when the slew has finished.</summary>
    Task<OperationResult> SlewAsync(double raHours, double decDegrees, CancellationToken cancellationToken);

    Task<OperationResult> ParkAsync(CancellationToken cancellationToken);

    Task<OperationResult> UnparkAsync(CancellationToken cancellationToken);

    Task<OperationResult> FindHomeAsync(CancellationToken cancellationToken);

    /// <summary>Switches tracking and returns once the mount reports the new state (or the wait for it elapsed).</summary>
    Task<OperationResult> SetTrackingAsync(bool enabled, CancellationToken cancellationToken);

    /// <summary>Stops a running slew, park or home. No-op when nothing moves or no mount is connected.</summary>
    void StopSlew();

    // ----- Camera -----

    /// <summary>Takes one exposure (changing filter first when requested) and returns its statistics.</summary>
    Task<CaptureResult> CaptureAsync(CaptureRequest request, CancellationToken cancellationToken);

    /// <summary>Aborts a running exposure of the imaging camera. No-op when idle.</summary>
    void AbortExposure();

    /// <summary>Cools to <paramref name="targetCelsius"/> over <paramref name="duration"/> and returns when done.</summary>
    Task<OperationResult> CoolCameraAsync(double targetCelsius, TimeSpan duration, CancellationToken cancellationToken);

    Task<OperationResult> WarmCameraAsync(TimeSpan duration, CancellationToken cancellationToken);

    // ----- Focuser -----

    Task<OperationResult> MoveFocuserAsync(int position, CancellationToken cancellationToken);

    /// <summary>Runs NINA's autofocus (with the active profile's autofocus settings) on the current filter.</summary>
    Task<AutofocusResult> RunAutofocusAsync(CancellationToken cancellationToken);

    /// <summary>Halts a running focuser move. No-op when idle.</summary>
    void StopFocuser();

    // ----- Filter wheel -----

    /// <summary>Changes to the filter with the given name (case-insensitive) or zero-based position; exactly one is set.</summary>
    Task<OperationResult> ChangeFilterAsync(string? name, int? position, CancellationToken cancellationToken);

    // ----- Guider -----

    Task<OperationResult> StartGuidingAsync(bool forceCalibration, CancellationToken cancellationToken);

    Task<OperationResult> StopGuidingAsync(CancellationToken cancellationToken);

    // ----- Switches (power outputs, dew heaters) -----

    Task<OperationResult> SetSwitchAsync(int index, double value, CancellationToken cancellationToken);

    // ----- Advanced sequence -----

    /// <summary>Tree of the sequence currently loaded in the advanced sequencer.</summary>
    SequenceSnapshot GetSequence(int maxDepth);

    /// <summary>Sequence files in the profile's sequence folder.</summary>
    IReadOnlyList<SavedSequenceInfo> ListSavedSequences();

    /// <summary>Names of the saved target (deep sky object container) templates.</summary>
    IReadOnlyList<string> ListTargetTemplates();

    /// <summary>Loads a saved sequence (by <see cref="SavedSequenceInfo.Name"/>) into the advanced sequencer, replacing the current one.</summary>
    Task<OperationResult> LoadSequenceAsync(string name, CancellationToken cancellationToken);

    /// <summary>Adds a target to the advanced sequence, from a template or built from an exposure plan.</summary>
    Task<OperationResult> AddTargetAsync(TargetRequest request, CancellationToken cancellationToken);

    /// <summary>Removes all targets from the advanced sequence (start and end areas stay).</summary>
    Task<OperationResult> ClearTargetsAsync(CancellationToken cancellationToken);

    /// <summary>Saves the current advanced sequence under <paramref name="name"/> in the sequence folder.</summary>
    Task<OperationResult> SaveSequenceAsync(string name, bool overwrite, CancellationToken cancellationToken);

    /// <summary>Starts the advanced sequence and returns once it runs (does not wait for it to end).</summary>
    Task<OperationResult> StartSequenceAsync(bool skipValidation, CancellationToken cancellationToken);

    OperationResult StopSequence();

    /// <summary>Resets the progress of all items so that the sequence can run again.</summary>
    OperationResult ResetSequence();

    // ----- Plate solving (profile plate solve settings: exposure, filter, binning, gain, solver) -----

    /// <summary>
    /// Takes one plate solve exposure at the current position and solves it (blind when no mount position is known).
    /// With <paramref name="sync"/> the mount, and a connected rotator, are synced to the solution.
    /// </summary>
    Task<PlateSolveOutcome> PlateSolveAsync(bool sync, CancellationToken cancellationToken);

    /// <summary>
    /// Slews to J2000 coordinates and centers them with NINA's centering solver (solve, sync or offset, re-slew until
    /// within the profile's threshold). Guiding is stopped first and restarted afterwards when it was running.
    /// </summary>
    Task<PlateSolveOutcome> CenterAsync(double raHours, double decDegrees, CancellationToken cancellationToken);

    // ----- Guider diagnostics (rich data only from the pins internal guider) -----

    /// <summary>State, statistics, loop timing, recent alerts and (optionally) recent guide steps. Must not block on hardware.</summary>
    GuiderReport GetGuiderReport(int maxSteps, int maxAlerts);

    /// <summary>The current or last Guiding Coach session and the stored report cards, newest first.</summary>
    GuiderData GetCoachReport(int maxHistory, bool includeSamples);

    /// <summary>The flight recorder's incidents, newest first, at most <paramref name="maxCount"/>.</summary>
    GuiderData GetGuideIncidents(int maxCount);

    /// <summary>One incident with triggers, markers and diagnosis, plus at most <paramref name="maxFrames"/> frames of telemetry.</summary>
    GuiderData GetGuideIncident(string id, int maxFrames);

    // ----- Log -----

    /// <summary>
    /// The pins log file of this run, or with <paramref name="previousRun"/> the newest one of an earlier run (after a
    /// crash or restart). Read by the tools with <c>PinsLogReader</c>.
    /// </summary>
    LogSource GetLogSource(bool previousRun);

    // ----- Planning -----

    /// <summary>
    /// Sun, moon and target altitudes sampled over one observing night (local noon to noon) at the profile's site.
    /// <see cref="NightSkyRequest.Night"/> null means the current night: the one that contains now.
    /// </summary>
    NightSky GetNightSky(NightSkyRequest request);

    // ----- Rotator, flat device, dome -----

    /// <summary>
    /// Moves the rotator to a sky position angle (degrees, as synced by plate solving) or, with
    /// <paramref name="mechanical"/>, to a mechanical position. Returns when the move has finished.
    /// </summary>
    Task<OperationResult> MoveRotatorAsync(double degrees, bool mechanical, CancellationToken cancellationToken);

    /// <summary>Opens or closes the cover, switches the light, or sets the brightness (only for <see cref="FlatDeviceAction.SetBrightness"/>).</summary>
    Task<OperationResult> ControlFlatDeviceAsync(FlatDeviceAction action, int? brightness, CancellationToken cancellationToken);

    /// <summary>Dome shutter, park, home, azimuth and slaving. <paramref name="azimuthDegrees"/> only for <see cref="DomeAction.SlewToAzimuth"/>.</summary>
    Task<OperationResult> ControlDomeAsync(DomeAction action, double? azimuthDegrees, CancellationToken cancellationToken);
}

public enum DeviceKind {
    Camera,
    Mount,
    Focuser,
    FilterWheel,
    Guider,
    Rotator,
    FlatDevice,
    Dome,
    Switch,
    Weather,
    SafetyMonitor
}

public sealed record OperationResult(bool Success, string Message) {

    public static OperationResult Ok(string message) {
        return new OperationResult(true, message);
    }

    public static OperationResult Fail(string message) {
        return new OperationResult(false, message);
    }
}

public sealed record ObservatoryStatus(
    IReadOnlyList<DeviceStatus> Devices,
    bool SequenceRunning,
    IReadOnlyList<string> RunningSequenceItems,
    DateTimeOffset Time);

/// <summary>One device slot. <see cref="Properties"/> holds the device type's key readings (see the adapter docs).</summary>
public sealed record DeviceStatus(
    DeviceKind Kind,
    bool Connected,
    string? Name,
    IReadOnlyDictionary<string, object?> Properties);

public sealed record ImageRecord(
    int Id,
    DateTimeOffset? Time,
    string? ImageType,
    string? Filter,
    double? ExposureSeconds,
    double? Hfr,
    int? Stars,
    double? Mean,
    double? Median,
    double? StandardDeviation,
    double? CameraTemperature,
    double? GuidingRmsArcsec,
    string? TargetName,
    string? FilePath);

/// <summary>
/// One exposure. Null gain/offset use the profile defaults, null binning keeps the camera's current binning, null filter
/// keeps the current filter. <see cref="Save"/> writes the frame like a normal light frame (image type "LIGHT");
/// otherwise it is a snapshot that is analysed but not saved. Either way it is added to the image history.
/// </summary>
public sealed record CaptureRequest(
    double ExposureSeconds,
    string? Filter = null,
    int? Gain = null,
    int? Offset = null,
    int? Binning = null,
    bool Save = false);

public sealed record CaptureResult(bool Success, string Message, ImageRecord? Image);

public sealed record AutofocusResult(
    bool Success,
    string Message,
    int? Position,
    double? Hfr,
    string? Filter,
    double? Temperature);

public sealed record SequenceSnapshot(
    bool Initialized,
    bool Running,
    string? Name,
    IReadOnlyList<SequenceNode> Nodes);

/// <summary>
/// One sequence entity. <see cref="Path"/> is the slash separated index path from the root ("1/0/2"), stable while the
/// sequence is not edited. <see cref="Status"/> is NINA's run status (Created, Running, Finished, Failed, Skipped, Disabled).
/// </summary>
public sealed record SequenceNode(
    string Path,
    string Name,
    string Type,
    string Status,
    IReadOnlyDictionary<string, object?> Details,
    IReadOnlyList<SequenceNode> Children);

public sealed record SavedSequenceInfo(string Name, string FileName, DateTimeOffset LastModified);

/// <summary>
/// A target to add. Either <see cref="TemplateName"/> (a saved target template whose coordinates get replaced) or
/// <see cref="Exposures"/> (a target built from the standard instructions plus one exposure block per entry) is set.
/// </summary>
public sealed record TargetRequest(
    string Name,
    double RaHours,
    double DecDegrees,
    double? RotationDegrees = null,
    string? TemplateName = null,
    IReadOnlyList<ExposurePlanItem>? Exposures = null,
    bool SlewAndCenter = true,
    bool StartGuiding = true,
    bool AutofocusAtStart = false,
    int DitherEvery = 0);

/// <summary>One block of exposures. Null gain/offset/binning use the camera defaults of the profile.</summary>
public sealed record ExposurePlanItem(
    double ExposureSeconds,
    int Count,
    string? Filter = null,
    int? Gain = null,
    int? Offset = null,
    int? Binning = null);

/// <summary>Result of <see cref="IPinsObservatory.PlateSolveAsync"/> and <see cref="IPinsObservatory.CenterAsync"/>.</summary>
public sealed record PlateSolveOutcome(bool Success, string Message, PlateSolution? Solution);

/// <summary>
/// The last solve. Coordinates are J2000. <see cref="ErrorArcmin"/>, <see cref="RaErrorArcsec"/> and
/// <see cref="DecErrorArcsec"/> are the distance of the solution from the reference: the requested target when centering,
/// the mount's reported position otherwise (null without a reference).
/// </summary>
public sealed record PlateSolution(
    double RaHours,
    double DecDegrees,
    double PositionAngleDegrees,
    double PixelScaleArcsecPerPixel,
    bool Flipped,
    double? ErrorArcmin,
    double? RaErrorArcsec,
    double? DecErrorArcsec,
    bool? Synced,
    int? Attempts);

/// <summary>
/// Guider data for the diagnostic tools. <see cref="Status"/> and the list entries are property bags (see
/// <see cref="PropertyBags"/>). Without the internal guider <see cref="Advanced"/> is false and the bags hold only what
/// every guider reports (state and RMS).
/// </summary>
public sealed record GuiderReport(
    bool Connected,
    string? Name,
    bool Advanced,
    string Message,
    IReadOnlyDictionary<string, object?> Status,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Alerts,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Steps);

/// <summary>Coach or incident data: <see cref="Data"/> is a property bag, null when <see cref="Available"/> is false.</summary>
public sealed record GuiderData(bool Available, string Message, IReadOnlyDictionary<string, object?>? Data) {

    public static GuiderData Unavailable(string message) {
        return new GuiderData(false, message, null);
    }
}

/// <summary>A log file to read; <see cref="FilePath"/> is null (with the reason in <see cref="Message"/>) when there is none.</summary>
public sealed record LogSource(string? FilePath, string Message);

public sealed record NightSkyRequest(
    DateOnly? Night,
    TimeSpan Step,
    IReadOnlyList<SkyTargetRequest> Targets);

/// <summary>A target to plan, J2000.</summary>
public sealed record SkyTargetRequest(string Name, double RaHours, double DecDegrees);

/// <summary>
/// Sampled sky over one night. Every series has one value per <see cref="Times"/> entry. Times carry the pins box's
/// local UTC offset. Altitudes and azimuths in degrees (azimuth from north through east), illumination 0..1.
/// </summary>
public sealed record NightSky(
    bool Success,
    string Message,
    ObservingSite? Site,
    DateTimeOffset Now,
    IReadOnlyList<DateTimeOffset> Times,
    IReadOnlyList<double> SunAltitude,
    IReadOnlyList<double> MoonAltitude,
    double? MoonIllumination,
    string? MoonPhase,
    IReadOnlyList<TargetSky> Targets) {

    public static NightSky Fail(string message, DateTimeOffset now) {
        return new NightSky(false, message, null, now, [], [], [], null, null, []);
    }
}

public sealed record ObservingSite(double LatitudeDegrees, double LongitudeDegrees, double ElevationMeters, bool CustomHorizon);

/// <summary>
/// One target over the night. <see cref="HorizonAltitude"/> is the profile's custom horizon at the target's azimuth
/// (null entries without a custom horizon); <see cref="MoonSeparation"/> the angular distance to the moon in degrees.
/// </summary>
public sealed record TargetSky(
    string Name,
    double RaHours,
    double DecDegrees,
    double AltitudeNow,
    double AzimuthNow,
    IReadOnlyList<double> Altitude,
    IReadOnlyList<double> Azimuth,
    IReadOnlyList<double?> HorizonAltitude,
    IReadOnlyList<double> MoonSeparation);

public enum FlatDeviceAction {
    OpenCover,
    CloseCover,
    LightOn,
    LightOff,
    SetBrightness
}

public enum DomeAction {
    OpenShutter,
    CloseShutter,
    Park,
    FindHome,
    SlewToAzimuth,
    SyncToMount,
    StartFollowing,
    StopFollowing
}
