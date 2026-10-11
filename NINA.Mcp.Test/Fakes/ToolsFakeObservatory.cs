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

    /// <summary>Makes the async member wait until <see cref="Release"/> (or until its token is cancelled).</summary>
    public void Block(string member) {
        lock (gate) {
            blocks[member] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
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

    public OperationResult SetTracking(bool enabled) {
        LastTracking = enabled;

        return Operate("SetTracking");
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

    // ----- Helpers -----

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

        lock (gate) {
            blocks.TryGetValue(member, out block);
        }

        Record(member);

        if (block is not null) {
            await block.Task.WaitAsync(cancellationToken);
        }

        lock (gate) {
            if (exceptions.TryGetValue(member, out var exception)) {
                throw exception;
            }

            return results.TryGetValue(member, out var result) ? result : OperationResult.Ok($"{member} done");
        }
    }
}
