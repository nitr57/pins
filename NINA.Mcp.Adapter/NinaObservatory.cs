// SPDX-License-Identifier: MPL-2.0

using NINA.Core.Model;
using NINA.Core.Model.Equipment;
using NINA.Core.Utility;
using NINA.Core.Utility.WindowService;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Mcp.Adapter.Sequence;
using NINA.Mcp.Observatory;
using NINA.PlateSolving.Interfaces;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.Interfaces;
using NINA.WPF.Base.Interfaces.Mediator;
using NINA.WPF.Base.Interfaces.ViewModel;

namespace NINA.Mcp.Adapter;

/// <summary>
/// <see cref="IPinsObservatory"/> on top of the NINA mediators that pins registers in DI (see
/// <c>NINA/Utility/IoCBindings.cs</c>). Equipment members live in the partial files of this class; every sequence
/// member is delegated to <see cref="NinaSequenceService"/>.
/// </summary>
/// <remarks>
/// The calls mirror what the ninaAPI and Touch-N-Stars plugins do in pins today: mediator calls are made directly from
/// thread-pool threads (in headless pins there is no WPF dispatcher to marshal to). Expected failures are returned as
/// <see cref="OperationResult"/>; unexpected exceptions are logged and returned as failures as well.
/// </remarks>
public sealed partial class NinaObservatory : IPinsObservatory {

    private const string StatusSource = "MCP";

    private readonly ICameraMediator cameraMediator;
    private readonly ITelescopeMediator telescopeMediator;
    private readonly IFocuserMediator focuserMediator;
    private readonly IFilterWheelMediator filterWheelMediator;
    private readonly IGuiderMediator guiderMediator;
    private readonly IRotatorMediator rotatorMediator;
    private readonly IFlatDeviceMediator flatDeviceMediator;
    private readonly IDomeMediator domeMediator;
    private readonly ISwitchMediator switchMediator;
    private readonly IWeatherDataMediator weatherDataMediator;
    private readonly ISafetyMonitorMediator safetyMonitorMediator;

    private readonly IImagingMediator imagingMediator;
    private readonly IImageSaveMediator imageSaveMediator;
    private readonly IImageHistoryVM imageHistory;
    private readonly IAutoFocusVMFactory autoFocusVMFactory;
    private readonly IWindowServiceFactory windowServiceFactory;
    private readonly IApplicationStatusMediator applicationStatusMediator;
    private readonly IProfileService profileService;
    private readonly IPlateSolverFactory plateSolverFactory;
    private readonly IDomeFollower domeFollower;
    private readonly IAdvancedGuider internalGuider;

    private readonly NinaSequenceService sequence;

    private readonly object focuserMoveLock = new();
    private CancellationTokenSource? focuserMoveCts;

    private int mountMotionsInFlight;

    public NinaObservatory(
            ICameraMediator cameraMediator,
            ITelescopeMediator telescopeMediator,
            IFocuserMediator focuserMediator,
            IFilterWheelMediator filterWheelMediator,
            IGuiderMediator guiderMediator,
            IRotatorMediator rotatorMediator,
            IFlatDeviceMediator flatDeviceMediator,
            IDomeMediator domeMediator,
            ISwitchMediator switchMediator,
            IWeatherDataMediator weatherDataMediator,
            ISafetyMonitorMediator safetyMonitorMediator,
            IImagingMediator imagingMediator,
            IImageSaveMediator imageSaveMediator,
            IImageHistoryVM imageHistory,
            IAutoFocusVMFactory autoFocusVMFactory,
            IWindowServiceFactory windowServiceFactory,
            IApplicationStatusMediator applicationStatusMediator,
            IProfileService profileService,
            IPlateSolverFactory plateSolverFactory,
            IDomeFollower domeFollower,
            IAdvancedGuider internalGuider,
            NinaSequenceService sequence) {
        this.cameraMediator = cameraMediator ?? throw new ArgumentNullException(nameof(cameraMediator));
        this.telescopeMediator = telescopeMediator ?? throw new ArgumentNullException(nameof(telescopeMediator));
        this.focuserMediator = focuserMediator ?? throw new ArgumentNullException(nameof(focuserMediator));
        this.filterWheelMediator = filterWheelMediator ?? throw new ArgumentNullException(nameof(filterWheelMediator));
        this.guiderMediator = guiderMediator ?? throw new ArgumentNullException(nameof(guiderMediator));
        this.rotatorMediator = rotatorMediator ?? throw new ArgumentNullException(nameof(rotatorMediator));
        this.flatDeviceMediator = flatDeviceMediator ?? throw new ArgumentNullException(nameof(flatDeviceMediator));
        this.domeMediator = domeMediator ?? throw new ArgumentNullException(nameof(domeMediator));
        this.switchMediator = switchMediator ?? throw new ArgumentNullException(nameof(switchMediator));
        this.weatherDataMediator = weatherDataMediator ?? throw new ArgumentNullException(nameof(weatherDataMediator));
        this.safetyMonitorMediator = safetyMonitorMediator ?? throw new ArgumentNullException(nameof(safetyMonitorMediator));
        this.imagingMediator = imagingMediator ?? throw new ArgumentNullException(nameof(imagingMediator));
        this.imageSaveMediator = imageSaveMediator ?? throw new ArgumentNullException(nameof(imageSaveMediator));
        this.imageHistory = imageHistory ?? throw new ArgumentNullException(nameof(imageHistory));
        this.autoFocusVMFactory = autoFocusVMFactory ?? throw new ArgumentNullException(nameof(autoFocusVMFactory));
        this.windowServiceFactory = windowServiceFactory ?? throw new ArgumentNullException(nameof(windowServiceFactory));
        this.applicationStatusMediator = applicationStatusMediator ?? throw new ArgumentNullException(nameof(applicationStatusMediator));
        this.profileService = profileService ?? throw new ArgumentNullException(nameof(profileService));
        this.plateSolverFactory = plateSolverFactory ?? throw new ArgumentNullException(nameof(plateSolverFactory));
        this.domeFollower = domeFollower ?? throw new ArgumentNullException(nameof(domeFollower));
        this.internalGuider = internalGuider ?? throw new ArgumentNullException(nameof(internalGuider));
        this.sequence = sequence ?? throw new ArgumentNullException(nameof(sequence));
    }

    // ----- Equipment connection -----

    public async Task<OperationResult> ConnectAsync(DeviceKind kind, CancellationToken cancellationToken) {
        try {
            if (IsConnected(kind)) {
                return OperationResult.Ok($"{Describe(kind)} is already connected.");
            }

            var connected = kind switch {
                DeviceKind.Camera => await cameraMediator.Connect(),
                DeviceKind.Mount => await telescopeMediator.Connect(),
                DeviceKind.Focuser => await focuserMediator.Connect(),
                DeviceKind.FilterWheel => await filterWheelMediator.Connect(),
                DeviceKind.Guider => await guiderMediator.Connect(),
                DeviceKind.Rotator => await rotatorMediator.Connect(),
                DeviceKind.FlatDevice => await flatDeviceMediator.Connect(),
                DeviceKind.Dome => await domeMediator.Connect(),
                DeviceKind.Switch => await switchMediator.Connect(),
                DeviceKind.Weather => await weatherDataMediator.Connect(),
                DeviceKind.SafetyMonitor => await safetyMonitorMediator.Connect(),
                _ => false
            };

            cancellationToken.ThrowIfCancellationRequested();

            if (!connected) {
                return OperationResult.Fail(
                    $"{Describe(kind)} could not be connected. Check that a device is selected in the active profile and that it is powered and reachable; the pins log has details.");
            }

            return OperationResult.Ok($"{Describe(kind)} connected ({DeviceName(kind) ?? "unnamed device"}).");
        } catch (OperationCanceledException) {
            throw;
        } catch (Exception ex) {
            Logger.Error($"MCP: connecting the {Describe(kind)} failed", ex);
            return OperationResult.Fail($"Connecting the {Describe(kind)} failed: {ex.Message}");
        }
    }

    public async Task<OperationResult> DisconnectAsync(DeviceKind kind, CancellationToken cancellationToken) {
        try {
            if (!IsConnected(kind)) {
                return OperationResult.Ok($"{Describe(kind)} is not connected.");
            }

            switch (kind) {
                case DeviceKind.Camera:
                    await cameraMediator.Disconnect();
                    break;

                case DeviceKind.Mount:
                    await telescopeMediator.Disconnect();
                    break;

                case DeviceKind.Focuser:
                    await focuserMediator.Disconnect();
                    break;

                case DeviceKind.FilterWheel:
                    await filterWheelMediator.Disconnect();
                    break;

                case DeviceKind.Guider:
                    await guiderMediator.Disconnect();
                    break;

                case DeviceKind.Rotator:
                    await rotatorMediator.Disconnect();
                    break;

                case DeviceKind.FlatDevice:
                    await flatDeviceMediator.Disconnect();
                    break;

                case DeviceKind.Dome:
                    await domeMediator.Disconnect();
                    break;

                case DeviceKind.Switch:
                    await switchMediator.Disconnect();
                    break;

                case DeviceKind.Weather:
                    await weatherDataMediator.Disconnect();
                    break;

                case DeviceKind.SafetyMonitor:
                    await safetyMonitorMediator.Disconnect();
                    break;
            }

            return OperationResult.Ok($"{Describe(kind)} disconnected.");
        } catch (Exception ex) {
            Logger.Error($"MCP: disconnecting the {Describe(kind)} failed", ex);
            return OperationResult.Fail($"Disconnecting the {Describe(kind)} failed: {ex.Message}");
        }
    }

    // ----- Advanced sequence (delegated) -----

    public SequenceSnapshot GetSequence(int maxDepth) {
        return sequence.GetSequence(maxDepth);
    }

    public IReadOnlyList<SavedSequenceInfo> ListSavedSequences() {
        return sequence.ListSavedSequences();
    }

    public IReadOnlyList<string> ListTargetTemplates() {
        return sequence.ListTargetTemplates();
    }

    public Task<OperationResult> LoadSequenceAsync(string name, CancellationToken cancellationToken) {
        return sequence.LoadSequenceAsync(name, cancellationToken);
    }

    /// <summary>
    /// The meridian flip trigger fails the start validation without a mount; a camera-only plan (no mount connected)
    /// gets none, and the result says so.
    /// </summary>
    public async Task<OperationResult> AddTargetAsync(TargetRequest request, CancellationToken cancellationToken) {
        var skipFlip = request.TemplateName == null
            && request.MeridianFlip
            && telescopeMediator.GetInfo()?.Connected != true;

        if (skipFlip) {
            request = request with { MeridianFlip = false };
        }

        var result = await sequence.AddTargetAsync(request, cancellationToken);

        if (!skipFlip || !result.Success) {
            return result;
        }

        return OperationResult.Ok(
            result.Message
            + " No meridian flip trigger was added because no mount is connected; connect the mount and add the target again "
            + "if it needs one.");
    }

    public Task<OperationResult> ClearTargetsAsync(CancellationToken cancellationToken) {
        return sequence.ClearTargetsAsync(cancellationToken);
    }

    public Task<OperationResult> SaveSequenceAsync(string name, bool overwrite, CancellationToken cancellationToken) {
        return sequence.SaveSequenceAsync(name, overwrite, cancellationToken);
    }

    public Task<OperationResult> StartSequenceAsync(bool skipValidation, CancellationToken cancellationToken) {
        return sequence.StartSequenceAsync(skipValidation, cancellationToken);
    }

    public OperationResult StopSequence() {
        return sequence.StopSequence();
    }

    public OperationResult ResetSequence() {
        return sequence.ResetSequence();
    }

    // ----- Helpers -----

    /// <summary>Progress sink that shows MCP operations in the pins status bar (Touch-N-Stars shows it too).</summary>
    private IProgress<ApplicationStatus> CreateProgress() {
        return new StatusProgress(applicationStatusMediator);
    }

    private FilterInfo? FindFilter(string? name, int? position) {
        var filters = profileService.ActiveProfile?.FilterWheelSettings?.FilterWheelFilters;

        if (filters == null) {
            return null;
        }

        var list = filters.ToList();

        if (name != null) {
            return list.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.Ordinal))
                ?? list.FirstOrDefault(f => string.Equals(f.Name?.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        if (position != null) {
            return list.FirstOrDefault(f => f.Position == position.Value);
        }

        return null;
    }

    private string AvailableFilterNames() {
        var filters = profileService.ActiveProfile?.FilterWheelSettings?.FilterWheelFilters;

        if (filters == null || filters.Count == 0) {
            return "none configured";
        }

        return string.Join(", ", filters.ToList().Select(f => $"{f.Position}: {f.Name}"));
    }

    private static string Describe(DeviceKind kind) {
        return kind switch {
            DeviceKind.Camera => "Camera",
            DeviceKind.Mount => "Mount",
            DeviceKind.Focuser => "Focuser",
            DeviceKind.FilterWheel => "Filter wheel",
            DeviceKind.Guider => "Guider",
            DeviceKind.Rotator => "Rotator",
            DeviceKind.FlatDevice => "Flat device",
            DeviceKind.Dome => "Dome",
            DeviceKind.Switch => "Switch",
            DeviceKind.Weather => "Weather device",
            DeviceKind.SafetyMonitor => "Safety monitor",
            _ => kind.ToString()
        };
    }

    private static double? Number(double value) {
        return double.IsFinite(value) ? value : null;
    }

    private static double? Number(float value) {
        return float.IsFinite(value) ? value : null;
    }

    private sealed class StatusProgress : IProgress<ApplicationStatus> {

        private readonly IApplicationStatusMediator mediator;

        public StatusProgress(IApplicationStatusMediator mediator) {
            this.mediator = mediator;
        }

        public void Report(ApplicationStatus value) {
            if (value == null) {
                return;
            }

            try {
                if (string.IsNullOrEmpty(value.Source)) {
                    value.Source = StatusSource;
                }

                mediator.StatusUpdate(value);
            } catch (Exception ex) {
                Logger.Debug($"MCP: status update failed: {ex.Message}");
            }
        }
    }
}
