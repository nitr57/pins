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

    OperationResult SetTracking(bool enabled);

    /// <summary>Stops a running slew. No-op when nothing moves or no mount is connected.</summary>
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
/// One exposure. Null fields keep the camera's current setting. <see cref="Save"/> writes the frame like a normal
/// light frame (image type "LIGHT"); otherwise it is a snapshot that is analysed but not saved.
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
