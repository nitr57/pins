// SPDX-License-Identifier: MPL-2.0

using NINA.Mcp.Observatory;

namespace NINA.Mcp.Test.Fakes;

/// <summary>
/// Hand-written <see cref="IPinsObservatory"/> for tool tests. Records every call by member name (e.g. "Slew"), keeps the
/// last arguments, and lets a test configure per-member results, exceptions and blocking gates.
/// </summary>
internal sealed class ToolsFakeObservatory : IPinsObservatory {

    private readonly object gate = new();
    private readonly List<string> calls = [];
    private readonly Dictionary<string, OperationResult> results = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Exception> exceptions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TaskCompletionSource> blocks = new(StringComparer.Ordinal);
    private readonly HashSet<string> blocksIgnoringCancellation = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TaskCompletionSource> started = new(StringComparer.Ordinal);

    public bool SequenceRunning { get; set; }

    public List<DeviceStatus> Devices { get; } = [
        new DeviceStatus(DeviceKind.Camera, true, "Fake Camera", new Dictionary<string, object?> { ["temperature"] = -10.0 }),
        new DeviceStatus(DeviceKind.FilterWheel, false, null, new Dictionary<string, object?>())
    ];

    public List<ImageRecord> Images { get; } = [];

    public List<SavedSequenceInfo> SavedSequences { get; } = [];

    public List<string> Templates { get; } = [];

    public SequenceSnapshot Sequence { get; set; } = new(true, false, "Fake sequence", []);

    public CaptureResult? CaptureResultOverride { get; set; }

    public AutofocusResult AutofocusResult { get; set; } = new(true, "Autofocus done", 12345, 2.1, "L", 8.5);

    public PlateSolution Solution { get; set; } = new(5.5883, -5.391, 92.5, 1.21, false, 0.42, -12.3, 21.0, null, null);

    public GuiderReport GuiderReport { get; set; } = new(
        true,
        "Internal Guider",
        true,
        "pins internal guider.",
        new Dictionary<string, object?> { ["state"] = "Guiding", ["windowStats"] = new Dictionary<string, object?> { ["rmsTotalArcsec"] = 0.62 } },
        [new Dictionary<string, object?> { ["codeName"] = "StarLost", ["severity"] = "Warning" }],
        []);

    public GuiderData CoachData { get; set; } = new(true, "Coach", new Dictionary<string, object?> { ["session"] = new Dictionary<string, object?> { ["phase"] = "Idle" } });

    public GuiderData IncidentsData { get; set; } = new(true, "Incidents", new Dictionary<string, object?> { ["total"] = 0 });

    public GuiderData IncidentData { get; set; } = new(true, "Incident", new Dictionary<string, object?> { ["id"] = "20261013-213012-StarLost" });

    public LogSource LogSource { get; set; } = new(null, "No log file in the fake.");

    public CatalogSearchResult CatalogResult { get; set; } = new(
        true,
        "1 object",
        [new CatalogObject("M 42", ["M 42", "NGC 1976", "Orion Nebula"], "BRTNB", "ORI", 5.5881, -5.3911, 4.0, null, 85, 60, null)]);

    public SystemHealth Health { get; set; } = new(
        new DateTimeOffset(2026, 10, 13, 21, 0, 0, TimeSpan.Zero),
        5.5,
        true,
        null,
        new DiskSpace("/home/pi/Pictures", 120, 230),
        new DiskSpace("/home/pi/.local/share/NINA", 120, 230),
        52.3,
        ThrottleState.Parse("throttled=0x0"),
        2048,
        8064,
        0.8);

    /// <summary>The dome's "shutter" status property after a successful close (null removes it).</summary>
    public string? DomeShutterAfterClose { get; set; } = "ShutterClosed";

    /// <summary>The night returned by GetNightSky; defaults to <see cref="ToolsNightSky.Create"/>.</summary>
    public Func<NightSkyRequest, NightSky> NightSkyFactory { get; set; } = request => ToolsNightSky.Create(request.Targets);

    // ----- Last arguments -----

    public (double Ra, double Dec)? LastSlew { get; private set; }

    public CaptureRequest? LastCapture { get; private set; }

    public DeviceKind? LastDevice { get; private set; }

    public (double Target, TimeSpan Duration)? LastCool { get; private set; }

    public TimeSpan? LastWarmDuration { get; private set; }

    public int? LastFocuserPosition { get; private set; }

    public (string? Name, int? Position)? LastFilter { get; private set; }

    public bool? LastForceCalibration { get; private set; }

    public (int Index, double Value)? LastSwitch { get; private set; }

    public bool? LastTracking { get; private set; }

    public int? LastSequenceDepth { get; private set; }

    public int? LastImageCount { get; private set; }

    public string? LastLoadedSequence { get; private set; }

    public TargetRequest? LastTarget { get; private set; }

    public (string Name, bool Overwrite)? LastSave { get; private set; }

    public bool? LastSkipValidation { get; private set; }

    public bool? LastPlateSolveSync { get; private set; }

    public (double Ra, double Dec)? LastCenter { get; private set; }

    public (int Steps, int Alerts)? LastGuiderReport { get; private set; }

    public (int History, bool Samples)? LastCoach { get; private set; }

    public int? LastIncidentCount { get; private set; }

    public (string Id, int Frames)? LastIncident { get; private set; }

    public bool? LastLogPreviousRun { get; private set; }

    public NightSkyRequest? LastNightSkyRequest { get; private set; }

    public (double Degrees, bool Mechanical)? LastRotator { get; private set; }

    public (FlatDeviceAction Action, int? Brightness)? LastFlatDevice { get; private set; }

    public (DomeAction Action, double? Azimuth)? LastDome { get; private set; }

    public CatalogSearch? LastCatalogSearch { get; private set; }

    /// <summary>Tokens handed to async members, for asserting that jobs run on their own token.</summary>
    public CancellationToken LastToken { get; private set; }

    public IReadOnlyList<string> Calls {
        get {
            lock (gate) {
                return calls.ToList();
            }
        }
    }

    // ----- Configuration -----

    public void SetResult(string member, OperationResult result) {
        lock (gate) {
            results[member] = result;
        }
    }

    public void Throw(string member, Exception exception) {
        lock (gate) {
            exceptions[member] = exception;
        }
    }

    /// <summary>
    /// Makes the async member wait until <see cref="Release"/>, or until its token is cancelled unless
    /// <paramref name="ignoreCancellation"/> is set (a device that does not react to a stop request).
    /// </summary>
    public void Block(string member, bool ignoreCancellation = false) {
        lock (gate) {
            blocks[member] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            if (ignoreCancellation) {
                blocksIgnoringCancellation.Add(member);
            } else {
                blocksIgnoringCancellation.Remove(member);
            }
        }
    }

    public void Release(string member) {
        lock (gate) {
            if (blocks.Remove(member, out var block)) {
                block.TrySetResult();
            }
        }
    }

    /// <summary>Completes once <paramref name="member"/> has been called.</summary>
    public Task WaitForCallAsync(string member) {
        return StartedSignal(member).Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    // ----- IPinsObservatory -----

    public ObservatoryStatus GetStatus() {
        Record("GetStatus");

        return new ObservatoryStatus(
            Devices.ToList(),
            SequenceRunning,
            SequenceRunning ? ["Smart Exposure"] : [],
            new DateTimeOffset(2026, 10, 11, 21, 0, 0, TimeSpan.Zero));
    }

    public IReadOnlyList<ImageRecord> GetRecentImages(int count) {
        Record("GetRecentImages");
        LastImageCount = count;

        return Images.Take(count).ToList();
    }

    public Task<OperationResult> ConnectAsync(DeviceKind kind, CancellationToken cancellationToken) {
        LastDevice = kind;

        return OperateAsync("Connect", cancellationToken);
    }

    public Task<OperationResult> DisconnectAsync(DeviceKind kind, CancellationToken cancellationToken) {
        LastDevice = kind;

        return OperateAsync("Disconnect", cancellationToken);
    }

    public Task<OperationResult> SlewAsync(double raHours, double decDegrees, CancellationToken cancellationToken) {
        LastSlew = (raHours, decDegrees);

        return OperateAsync("Slew", cancellationToken);
    }

    public Task<OperationResult> ParkAsync(CancellationToken cancellationToken) {
        return OperateAsync("Park", cancellationToken);
    }

    public Task<OperationResult> UnparkAsync(CancellationToken cancellationToken) {
        return OperateAsync("Unpark", cancellationToken);
    }

    public Task<OperationResult> FindHomeAsync(CancellationToken cancellationToken) {
        return OperateAsync("FindHome", cancellationToken);
    }

    public Task<OperationResult> SetTrackingAsync(bool enabled, CancellationToken cancellationToken) {
        LastTracking = enabled;

        return OperateAsync("SetTracking", cancellationToken);
    }

    public void StopSlew() {
        Operate("StopSlew");
    }

    public async Task<CaptureResult> CaptureAsync(CaptureRequest request, CancellationToken cancellationToken) {
        LastCapture = request;

        var result = await OperateAsync("Capture", cancellationToken);

        if (CaptureResultOverride is not null) {
            return CaptureResultOverride;
        }

        var image = new ImageRecord(
            1,
            null,
            "SNAPSHOT",
            request.Filter,
            request.ExposureSeconds,
            2.5,
            321,
            1000,
            990,
            50,
            -10,
            null,
            null,
            null);

        return new CaptureResult(result.Success, result.Message, image);
    }

    public void AbortExposure() {
        Operate("AbortExposure");
    }

    public Task<OperationResult> CoolCameraAsync(double targetCelsius, TimeSpan duration, CancellationToken cancellationToken) {
        LastCool = (targetCelsius, duration);

        return OperateAsync("CoolCamera", cancellationToken);
    }

    public Task<OperationResult> WarmCameraAsync(TimeSpan duration, CancellationToken cancellationToken) {
        LastWarmDuration = duration;

        return OperateAsync("WarmCamera", cancellationToken);
    }

    public Task<OperationResult> MoveFocuserAsync(int position, CancellationToken cancellationToken) {
        LastFocuserPosition = position;

        return OperateAsync("MoveFocuser", cancellationToken);
    }

    public async Task<AutofocusResult> RunAutofocusAsync(CancellationToken cancellationToken) {
        await OperateAsync("RunAutofocus", cancellationToken);

        return AutofocusResult;
    }

    public void StopFocuser() {
        Operate("StopFocuser");
    }

    public Task<OperationResult> ChangeFilterAsync(string? name, int? position, CancellationToken cancellationToken) {
        LastFilter = (name, position);

        return OperateAsync("ChangeFilter", cancellationToken);
    }

    public Task<OperationResult> StartGuidingAsync(bool forceCalibration, CancellationToken cancellationToken) {
        LastForceCalibration = forceCalibration;

        return OperateAsync("StartGuiding", cancellationToken);
    }

    public Task<OperationResult> StopGuidingAsync(CancellationToken cancellationToken) {
        return OperateAsync("StopGuiding", cancellationToken);
    }

    public Task<OperationResult> SetSwitchAsync(int index, double value, CancellationToken cancellationToken) {
        LastSwitch = (index, value);

        return OperateAsync("SetSwitch", cancellationToken);
    }

    public SequenceSnapshot GetSequence(int maxDepth) {
        Record("GetSequence");
        LastSequenceDepth = maxDepth;

        return Sequence;
    }

    public IReadOnlyList<SavedSequenceInfo> ListSavedSequences() {
        Record("ListSavedSequences");

        return SavedSequences.ToList();
    }

    public IReadOnlyList<string> ListTargetTemplates() {
        Record("ListTargetTemplates");

        return Templates.ToList();
    }

    public Task<OperationResult> LoadSequenceAsync(string name, CancellationToken cancellationToken) {
        LastLoadedSequence = name;

        return OperateAsync("LoadSequence", cancellationToken);
    }

    public Task<OperationResult> AddTargetAsync(TargetRequest request, CancellationToken cancellationToken) {
        LastTarget = request;

        return OperateAsync("AddTarget", cancellationToken);
    }

    public Task<OperationResult> ClearTargetsAsync(CancellationToken cancellationToken) {
        return OperateAsync("ClearTargets", cancellationToken);
    }

    public Task<OperationResult> SaveSequenceAsync(string name, bool overwrite, CancellationToken cancellationToken) {
        LastSave = (name, overwrite);

        return OperateAsync("SaveSequence", cancellationToken);
    }

    public Task<OperationResult> StartSequenceAsync(bool skipValidation, CancellationToken cancellationToken) {
        LastSkipValidation = skipValidation;

        return OperateAsync("StartSequence", cancellationToken);
    }

    public OperationResult StopSequence() {
        return Operate("StopSequence");
    }

    public OperationResult ResetSequence() {
        return Operate("ResetSequence");
    }

    public async Task<PlateSolveOutcome> PlateSolveAsync(bool sync, CancellationToken cancellationToken) {
        LastPlateSolveSync = sync;

        var result = await OperateAsync("PlateSolve", cancellationToken);

        return new PlateSolveOutcome(result.Success, result.Message, Solution);
    }

    public async Task<PlateSolveOutcome> CenterAsync(double raHours, double decDegrees, CancellationToken cancellationToken) {
        LastCenter = (raHours, decDegrees);

        var result = await OperateAsync("Center", cancellationToken);

        return new PlateSolveOutcome(result.Success, result.Message, Solution);
    }

    public GuiderReport GetGuiderReport(int maxSteps, int maxAlerts) {
        Record("GetGuiderReport");
        LastGuiderReport = (maxSteps, maxAlerts);

        return GuiderReport;
    }

    public GuiderData GetCoachReport(int maxHistory, bool includeSamples) {
        Record("GetCoachReport");
        LastCoach = (maxHistory, includeSamples);

        return CoachData;
    }

    public GuiderData GetGuideIncidents(int maxCount) {
        Record("GetGuideIncidents");
        LastIncidentCount = maxCount;

        return IncidentsData;
    }

    public GuiderData GetGuideIncident(string id, int maxFrames) {
        Record("GetGuideIncident");
        LastIncident = (id, maxFrames);

        return IncidentData;
    }

    public LogSource GetLogSource(bool previousRun) {
        Record("GetLogSource");
        LastLogPreviousRun = previousRun;

        return LogSource;
    }

    public NightSky GetNightSky(NightSkyRequest request) {
        Record("GetNightSky");
        LastNightSkyRequest = request;

        return NightSkyFactory(request);
    }

    public Task<OperationResult> MoveRotatorAsync(double degrees, bool mechanical, CancellationToken cancellationToken) {
        LastRotator = (degrees, mechanical);

        return OperateAsync("MoveRotator", cancellationToken);
    }

    public Task<OperationResult> ControlFlatDeviceAsync(FlatDeviceAction action, int? brightness, CancellationToken cancellationToken) {
        LastFlatDevice = (action, brightness);

        return OperateAsync("FlatDevice", cancellationToken);
    }

    public async Task<OperationResult> ControlDomeAsync(DomeAction action, double? azimuthDegrees, CancellationToken cancellationToken) {
        LastDome = (action, azimuthDegrees);

        var result = await OperateAsync("Dome", cancellationToken);

        if (result.Success && action == DomeAction.CloseShutter) {
            SetDeviceProperty(DeviceKind.Dome, "shutter", DomeShutterAfterClose);
        }

        return result;
    }

    public Task<CatalogSearchResult> FindTargetsAsync(CatalogSearch search, CancellationToken cancellationToken) {
        Record("FindTargets");
        LastCatalogSearch = search;

        return Task.FromResult(CatalogResult);
    }

    public SystemHealth GetSystemHealth() {
        Record("GetSystemHealth");

        return Health;
    }

    // ----- Helpers -----

    private void SetDeviceProperty(DeviceKind kind, string name, object? value) {
        lock (gate) {
            var index = Devices.FindIndex(device => device.Kind == kind);

            if (index < 0) {
                return;
            }

            var properties = new Dictionary<string, object?>(Devices[index].Properties);

            if (value is null) {
                properties.Remove(name);
            } else {
                properties[name] = value;
            }

            Devices[index] = Devices[index] with { Properties = properties };
        }
    }

    private void Record(string member) {
        lock (gate) {
            calls.Add(member);
        }

        StartedSignal(member).TrySetResult();
    }

    private TaskCompletionSource StartedSignal(string member) {
        lock (gate) {
            if (!started.TryGetValue(member, out var signal)) {
                signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                started[member] = signal;
            }

            return signal;
        }
    }

    private OperationResult Operate(string member) {
        Record(member);

        lock (gate) {
            if (exceptions.TryGetValue(member, out var exception)) {
                throw exception;
            }

            return results.TryGetValue(member, out var result) ? result : OperationResult.Ok($"{member} done");
        }
    }

    private async Task<OperationResult> OperateAsync(string member, CancellationToken cancellationToken) {
        LastToken = cancellationToken;

        TaskCompletionSource? block;
        bool ignoreCancellation;

        lock (gate) {
            blocks.TryGetValue(member, out block);
            ignoreCancellation = blocksIgnoringCancellation.Contains(member);
        }

        Record(member);

        if (block is not null) {
            await (ignoreCancellation ? block.Task : block.Task.WaitAsync(cancellationToken));
        }

        lock (gate) {
            if (exceptions.TryGetValue(member, out var exception)) {
                throw exception;
            }

            return results.TryGetValue(member, out var result) ? result : OperationResult.Ok($"{member} done");
        }
    }
}
