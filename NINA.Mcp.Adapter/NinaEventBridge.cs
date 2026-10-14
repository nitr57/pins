// SPDX-License-Identifier: MPL-2.0

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NINA.Core.Utility;
using NINA.Equipment.Equipment;
using NINA.Equipment.Equipment.MyGuider;
using NINA.Equipment.Equipment.MyGuider.Advanced;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Interfaces.ViewModel;
using NINA.Mcp.Observatory;
using NINA.Sequencer.Interfaces.Mediator;
using NINA.WPF.Base.Interfaces.Mediator;
using static System.FormattableString;

namespace NINA.Mcp.Adapter;

/// <summary>
/// Copies NINA's mediator events into the <see cref="ObservatoryEventLog"/> that get_events reads, from pins start on
/// (so that events before the first MCP request are not lost). Every subscription is optional: a service that cannot be
/// resolved is logged and skipped, and nothing here may fail the pins start or throw into a mediator.
/// </summary>
public sealed class NinaEventBridge : IHostedService {

    /// <summary>How often the connection state of the devices is compared with the last one.</summary>
    private static readonly TimeSpan ConnectionPollInterval = TimeSpan.FromSeconds(3);

    private static readonly TimeSpan IncidentSavedInterval = TimeSpan.FromMinutes(1);

    private readonly IServiceProvider services;
    private readonly ObservatoryEventLog log;
    private readonly List<Action> unsubscribers = [];
    private readonly List<DeviceWatch> devices = [];
    private readonly CancellationTokenSource stopping = new();

    private readonly object incidentGate = new();
    private DateTime lastIncidentSavedAt = DateTime.MinValue;
    private int suppressedIncidentSaves;

    public NinaEventBridge(IServiceProvider services, ObservatoryEventLog log) {
        this.services = services ?? throw new ArgumentNullException(nameof(services));
        this.log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public Task StartAsync(CancellationToken cancellationToken) {
        // pins disconnects all equipment while stopping, before StopAsync runs: stop watching first.
        Subscribe<IHostApplicationLifetime>(lifetime => lifetime.ApplicationStopping.Register(() => stopping.Cancel()));

        Subscribe<ICameraMediator>(mediator => WatchDevice(mediator, "camera"));
        Subscribe<ITelescopeMediator>(mediator => {
            WatchDevice(mediator, "mount");
            WatchMount(mediator);
        });
        Subscribe<IFocuserMediator>(mediator => WatchDevice(mediator, "focuser"));
        Subscribe<IFilterWheelMediator>(mediator => WatchDevice(mediator, "filter_wheel"));
        Subscribe<IGuiderMediator>(mediator => {
            WatchGuiderDrops(mediator, WatchDevice(mediator, "guider"));
            WatchGuiding(mediator);
        });
        Subscribe<IRotatorMediator>(mediator => WatchDevice(mediator, "rotator"));
        Subscribe<IFlatDeviceMediator>(mediator => WatchDevice(mediator, "flat_device"));
        Subscribe<IDomeMediator>(mediator => WatchDevice(mediator, "dome"));
        Subscribe<ISwitchMediator>(mediator => WatchDevice(mediator, "switch"));
        Subscribe<IWeatherDataMediator>(mediator => WatchDevice(mediator, "weather"));
        Subscribe<ISafetyMonitorMediator>(mediator => {
            WatchSafety(mediator, WatchDevice(mediator, "safety_monitor"));
        });
        Subscribe<ICameraMediator>(WatchCamera);
        Subscribe<IImageSaveMediator>(WatchImageSaving);
        Subscribe<ISequenceMediator>(WatchSequence);
        Subscribe<IAdvancedGuider>(WatchInternalGuider);

        _ = Task.Run(() => PollConnectionsAsync(stopping.Token));

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) {
        stopping.Cancel();

        lock (unsubscribers) {
            foreach (var unsubscribe in unsubscribers) {
                try {
                    unsubscribe();
                } catch (Exception ex) {
                    Logger.Debug($"MCP: unsubscribing an event failed: {ex.Message}");
                }
            }

            unsubscribers.Clear();
        }

        return Task.CompletedTask;
    }

    private void Subscribe<T>(Action<T> watch) where T : class {
        try {
            var service = services.GetService<T>();

            if (service == null) {
                Logger.Debug($"MCP: no {typeof(T).Name} registered; its events are not recorded");
                return;
            }

            watch(service);
        } catch (Exception ex) {
            Logger.Error($"MCP: subscribing to the events of {typeof(T).Name} failed", ex);
        }
    }

    private void OnUnsubscribe(Action unsubscribe) {
        lock (unsubscribers) {
            unsubscribers.Add(unsubscribe);
        }
    }

    // ----- Watchers -----

    /// <summary>
    /// Connections are tracked by polling the device info, not by the Connected/Disconnected events alone: several VMs
    /// raise Disconnected at the start of every connect, and a device that drops (USB, INDI) raises no event at all, its
    /// poll loop only marks it disconnected. The Disconnected event only tells a requested disconnect from a lost one
    /// (see <see cref="DeviceConnectionTracker"/>).
    /// </summary>
    private DeviceWatch WatchDevice<THandler, TConsumer, TInfo>(IDeviceMediator<THandler, TConsumer, TInfo> mediator, string device)
        where THandler : IDeviceVM<TInfo>
        where TConsumer : IDeviceConsumer<TInfo> {
        var watch = new DeviceWatch(device, () => mediator.GetInfo() is DeviceInfo info && info.Connected);

        Func<object, EventArgs, Task> disconnected = (_, _) => {
            watch.Tracker?.DisconnectRequested(DateTime.UtcNow);

            return Task.CompletedTask;
        };

        mediator.Disconnected += disconnected;
        OnUnsubscribe(() => mediator.Disconnected -= disconnected);

        lock (devices) {
            devices.Add(watch);
        }

        return watch;
    }

    /// <summary>
    /// When the guider device reports disconnected, GuiderVM broadcasts the info while it still holds the device and then
    /// calls its own Disconnect, which raises Disconnected like a requested disconnect. A requested disconnect drops the
    /// device before its broadcast. So a broadcast that turns a connected guider disconnected while the VM still holds a
    /// device is a lost connection.
    /// </summary>
    private void WatchGuiderDrops(IGuiderMediator mediator, DeviceWatch watch) {
        var consumer = new GuiderDropConsumer(mediator, () => watch.Tracker?.ConnectionDropped(DateTime.UtcNow));

        mediator.RegisterConsumer(consumer);
        OnUnsubscribe(() => mediator.RemoveConsumer(consumer));
    }

    private async Task PollConnectionsAsync(CancellationToken cancellationToken) {
        List<DeviceWatch> watches;

        lock (devices) {
            watches = devices.ToList();
        }

        foreach (var watch in watches) {
            watch.Tracker = new DeviceConnectionTracker(watch.Device, IsConnected(watch, false));

            // A device connected before the bridge started (an unsafe monitor) gets its connect handling too.
            if (watch.Tracker.WasConnected) {
                RunOnConnected(watch);
            }
        }

        try {
            while (!cancellationToken.IsCancellationRequested) {
                await Task.Delay(ConnectionPollInterval, cancellationToken);

                foreach (var watch in watches) {
                    await CheckConnectionAsync(watch);
                }
            }
        } catch (OperationCanceledException) {
            // pins stops.
        } catch (Exception ex) {
            Logger.Error("MCP: watching device connections failed", ex);
        }
    }

    private async Task CheckConnectionAsync(DeviceWatch watch) {
        var tracker = watch.Tracker!;

        var changes = tracker.Poll(IsConnected(watch, tracker.WasConnected), DateTime.UtcNow);

        foreach (var change in changes) {
            await Publish("device", change.Severity, change.Message, Data("device", watch.Device));
        }

        if (changes.Any(change => change.Connected)) {
            RunOnConnected(watch);
        }
    }

    private static void RunOnConnected(DeviceWatch watch) {
        try {
            watch.OnConnected?.Invoke();
        } catch (Exception ex) {
            Logger.Debug($"MCP: handling the connection of the {watch.Device} failed: {ex.Message}");
        }
    }

    private static bool IsConnected(DeviceWatch watch, bool fallback) {
        try {
            return watch.IsConnected();
        } catch (Exception) {
            return fallback;
        }
    }

    /// <summary>
    /// The safety monitor VM raises "unsafe" when it is disconnected or its poll stops (its last values are reset), and
    /// "safe" right after connecting: only changes of a connected monitor are real. A monitor that is already unsafe when
    /// it connects raises nothing, so its state is published on the poll that sees it connected; the same state is never
    /// published twice in a row within one connection.
    /// </summary>
    private void WatchSafety(ISafetyMonitorMediator mediator, DeviceWatch watch) {
        var gate = new object();
        bool? published = null;

        void PublishSafety(bool isSafe) {
            lock (gate) {
                if (published == isSafe) {
                    return;
                }

                published = isSafe;
            }

            Publish(
                "safety",
                isSafe ? EventSeverity.Info : EventSeverity.Critical,
                isSafe ? "The safety monitor reports safe." : "The safety monitor reports UNSAFE.",
                Data("isSafe", isSafe));
        }

        EventHandler<IsSafeEventArgs> changed = (_, args) => {
            if (mediator.GetInfo()?.Connected != true) {
                return;
            }

            PublishSafety(args.IsSafe);
        };

        // The VM takes the state of a monitor that is unsafe when it connects without raising IsSafeChanged.
        watch.OnConnected = () => {
            lock (gate) {
                published = null;
            }

            var info = mediator.GetInfo();

            if (info?.Connected == true && !info.IsSafe) {
                PublishSafety(false);
            }
        };

        mediator.IsSafeChanged += changed;
        OnUnsubscribe(() => mediator.IsSafeChanged -= changed);
    }

    private void WatchMount(ITelescopeMediator mediator) {
        Func<object, AfterMeridianFlipEventArgs, Task> flipped = (_, args) => Publish(
            "mount",
            args.Success ? EventSeverity.Info : EventSeverity.Error,
            args.Success ? "Meridian flip done." : "Meridian flip failed.",
            Data("success", args.Success));
        Func<object, EventArgs, Task> parked = (_, _) => Publish("mount", EventSeverity.Info, "Mount parked.");
        Func<object, EventArgs, Task> unparked = (_, _) => Publish("mount", EventSeverity.Info, "Mount unparked.");

        mediator.AfterMeridianFlip += flipped;
        mediator.Parked += parked;
        mediator.Unparked += unparked;

        OnUnsubscribe(() => {
            mediator.AfterMeridianFlip -= flipped;
            mediator.Parked -= parked;
            mediator.Unparked -= unparked;
        });
    }

    private void WatchGuiding(IGuiderMediator mediator) {
        Func<object, EventArgs, Task> started = (_, _) => Publish("guider", EventSeverity.Info, "Guiding started.");
        Func<object, EventArgs, Task> stopped = (_, _) => Publish("guider", EventSeverity.Info, "Guiding stopped.");

        mediator.GuidingStarted += started;
        mediator.GuidingStopped += stopped;

        OnUnsubscribe(() => {
            mediator.GuidingStarted -= started;
            mediator.GuidingStopped -= stopped;
        });
    }

    private void WatchCamera(ICameraMediator mediator) {
        Func<object, EventArgs, Task> timeout = (_, _) => Publish(
            "camera",
            EventSeverity.Error,
            "The camera download timed out (USB bandwidth, cable or driver).");

        mediator.DownloadTimeout += timeout;
        OnUnsubscribe(() => mediator.DownloadTimeout -= timeout);
    }

    private void WatchImageSaving(IImageSaveMediator mediator) {
        EventHandler<ImageSavedEventArgs> saved = (_, args) => {
            var stars = args.StarDetectionAnalysis;
            var file = args.PathToImage?.IsFile == true ? Path.GetFileName(args.PathToImage.LocalPath) : null;

            var data = new Dictionary<string, object?> {
                ["file"] = file,
                ["filter"] = string.IsNullOrEmpty(args.Filter) ? null : args.Filter,
                ["exposureSeconds"] = Finite(args.Duration),
                ["hfr"] = stars == null ? null : Finite(stars.HFR),
                ["stars"] = stars?.DetectedStars
            };

            Publish("image", EventSeverity.Info, Invariant($"Image saved: {file ?? "(unknown file)"}."), data);
        };

        Func<object, ImageSaveFailedEventArgs, Task> failed = (_, args) => {
            var diskFull = args.IsDiskFull || IsNoSpaceLeft(args.Exception);

            return Publish(
                "image",
                EventSeverity.Error,
                diskFull
                    ? "Saving an image failed: the disk is full."
                    : $"Saving an image failed: {args.Exception?.Message ?? "unknown error"}.",
                new Dictionary<string, object?> {
                    ["path"] = args.FilePath,
                    ["stage"] = args.FailureStage.ToString(),
                    ["diskFull"] = diskFull
                });
        };

        mediator.ImageSaved += saved;
        mediator.ImageSaveFailed += failed;

        OnUnsubscribe(() => {
            mediator.ImageSaved -= saved;
            mediator.ImageSaveFailed -= failed;
        });
    }

    /// <summary>
    /// The sequence mediator forwards its events to the sequencer view models, which exist only once the plugins are
    /// loaded (after the web host started on a Pi): subscribe when it reports initialized.
    /// </summary>
    private void WatchSequence(ISequenceMediator mediator) {
        Func<object, EventArgs, Task> starting = (_, _) => Publish("sequence", EventSeverity.Info, "Sequence starting.");
        Func<object, EventArgs, Task> finished = (_, _) => Publish(
            "sequence",
            EventSeverity.Info,
            "Sequence ended (finished, stopped or failed); get_sequence shows where.");

        _ = Task.Run(async () => {
            try {
                while (!mediator.Initialized) {
                    await Task.Delay(500, stopping.Token);
                }

                mediator.SequenceStarting += starting;
                mediator.SequenceFinished += finished;

                OnUnsubscribe(() => {
                    mediator.SequenceStarting -= starting;
                    mediator.SequenceFinished -= finished;
                });
            } catch (OperationCanceledException) {
                // pins stops before the sequencer was ready.
            } catch (Exception ex) {
                Logger.Error("MCP: subscribing to the sequence events failed", ex);
            }
        });
    }

    /// <summary>
    /// Alerts (a star loss raises one alert when reacquiring starts) and saved incidents of the internal guider; steps
    /// and frames are far too many.
    /// </summary>
    private void WatchInternalGuider(IAdvancedGuider guider) {
        EventHandler<AdvancedGuiderEventArgs> handler = (_, args) => {
            try {
                switch (args.Type) {
                    case AdvancedGuiderEventTypes.Alert when args.Payload is AdvancedGuiderAlert alert:
                        Publish(
                            "guider",
                            alert.Severity switch {
                                AdvancedGuiderAlertSeverities.Critical => EventSeverity.Critical,
                                AdvancedGuiderAlertSeverities.Warning => EventSeverity.Warning,
                                _ => EventSeverity.Info
                            },
                            string.IsNullOrEmpty(alert.Detail) ? alert.Title : $"{alert.Title}: {alert.Detail}",
                            new Dictionary<string, object?> {
                                ["code"] = alert.CodeName,
                                ["fix"] = alert.Fix,
                                ["incidentId"] = alert.IncidentId
                            });
                        break;

                    case AdvancedGuiderEventTypes.Incident when args.Payload is AdvancedIncidentEvent incident:
                        // "started" comes before the incident is stored; only "saved" can be opened with get_guide_incident.
                        if (incident.Action == "started") {
                            Publish(
                                "guider",
                                EventSeverity.Warning,
                                $"Guiding incident started ({incident.Summary?.Kind ?? "unknown"}); the flight recorder is recording.",
                                new Dictionary<string, object?> { ["incidentId"] = incident.Id });
                        } else if (incident.Action == "saved" && TakeIncidentSavedSlot(out var suppressed)) {
                            var more = suppressed > 0
                                ? $" {suppressed} more incident(s) were saved in the minute before; list_guide_incidents has them all."
                                : string.Empty;

                            Publish(
                                "guider",
                                EventSeverity.Info,
                                $"Guiding incident saved ({incident.Summary?.Kind ?? "unknown"}, {incident.Summary?.FrameCount ?? 0} frames); get_guide_incident has the details.{more}",
                                new Dictionary<string, object?> {
                                    ["incidentId"] = incident.Id,
                                    ["cause"] = incident.Summary?.Cause,
                                    ["endReason"] = incident.Summary?.EndReason,
                                    ["suppressedBefore"] = suppressed > 0 ? suppressed : null
                                });
                        }

                        break;
                }
            } catch (Exception ex) {
                Logger.Debug($"MCP: recording a guider event failed: {ex.Message}");
            }
        };

        guider.AdvancedGuiderEvent += handler;
        OnUnsubscribe(() => guider.AdvancedGuiderEvent -= handler);
    }

    /// <summary>
    /// At most one "incident saved" event per <see cref="IncidentSavedInterval"/>: a trigger that fires every few frames
    /// (the Spike flood) would otherwise fill the event log. <paramref name="suppressed"/> counts the ones left out before.
    /// </summary>
    private bool TakeIncidentSavedSlot(out int suppressed) {
        lock (incidentGate) {
            var now = DateTime.UtcNow;

            if (now - lastIncidentSavedAt < IncidentSavedInterval) {
                suppressedIncidentSaves++;
                suppressed = 0;

                return false;
            }

            suppressed = suppressedIncidentSaves;
            suppressedIncidentSaves = 0;
            lastIncidentSavedAt = now;

            return true;
        }
    }

    // ----- Helpers -----

    /// <summary>Records an event; never throws into the mediator that raised it.</summary>
    private Task Publish(string kind, EventSeverity severity, string message, IReadOnlyDictionary<string, object?>? data = null) {
        try {
            log.Publish(kind, severity, message, data);
        } catch (Exception ex) {
            Logger.Debug($"MCP: recording an event failed: {ex.Message}");
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// <see cref="ImageSaveFailedEventArgs.IsDiskFull"/> knows only the Windows error codes; on Linux an IOException's
    /// HResult is the errno, ENOSPC (28) for a full disk.
    /// </summary>
    private static bool IsNoSpaceLeft(Exception? exception) {
        const int NoSpaceLeftOnDevice = 28;

        return exception switch {
            null => false,
            _ when OperatingSystem.IsWindows() => false,
            IOException io when (io.HResult & 0xffff) == NoSpaceLeftOnDevice => true,
            AggregateException aggregate => aggregate.InnerExceptions.Any(IsNoSpaceLeft),
            _ => IsNoSpaceLeft(exception.InnerException)
        };
    }

    private static Dictionary<string, object?> Data(string key, object? value) {
        return new Dictionary<string, object?> { [key] = value };
    }

    private static double? Finite(double value) {
        return double.IsFinite(value) ? value : null;
    }

    private sealed class DeviceWatch {

        public DeviceWatch(string device, Func<bool> isConnected) {
            Device = device;
            IsConnected = isConnected;
        }

        public string Device { get; }

        public Func<bool> IsConnected { get; }

        /// <summary>Created with the first poll; events before it have nothing to compare with.</summary>
        public DeviceConnectionTracker? Tracker { get; set; }

        /// <summary>Runs on the poll that sees the device connected.</summary>
        public Action? OnConnected { get; set; }
    }

    private sealed class GuiderDropConsumer : IGuiderConsumer {

        private readonly IGuiderMediator mediator;
        private readonly Action dropped;
        private bool connected;

        public GuiderDropConsumer(IGuiderMediator mediator, Action dropped) {
            this.mediator = mediator;
            this.dropped = dropped;
        }

        public void UpdateDeviceInfo(GuiderInfo deviceInfo) {
            var wasConnected = connected;

            connected = deviceInfo?.Connected == true;

            if (wasConnected && !connected && HoldsDevice()) {
                dropped();
            }
        }

        public void Dispose() {
        }

        private bool HoldsDevice() {
            try {
                return mediator.GetDevice() != null;
            } catch (Exception) {
                return false;
            }
        }
    }
}
