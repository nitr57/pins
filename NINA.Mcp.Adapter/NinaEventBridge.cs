// SPDX-License-Identifier: MPL-2.0

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NINA.Core.Utility;
using NINA.Equipment.Equipment;
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

    /// <summary>A connection that ends within this time after a Disconnected event was asked for, not lost.</summary>
    private static readonly TimeSpan ExpectedDisconnectWindow = TimeSpan.FromSeconds(15);

    private readonly IServiceProvider services;
    private readonly ObservatoryEventLog log;
    private readonly List<Action> unsubscribers = [];
    private readonly List<DeviceWatch> devices = [];
    private readonly CancellationTokenSource stopping = new();

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
            WatchDevice(mediator, "guider");
            WatchGuiding(mediator);
        });
        Subscribe<IRotatorMediator>(mediator => WatchDevice(mediator, "rotator"));
        Subscribe<IFlatDeviceMediator>(mediator => WatchDevice(mediator, "flat_device"));
        Subscribe<IDomeMediator>(mediator => WatchDevice(mediator, "dome"));
        Subscribe<ISwitchMediator>(mediator => WatchDevice(mediator, "switch"));
        Subscribe<IWeatherDataMediator>(mediator => WatchDevice(mediator, "weather"));
        Subscribe<ISafetyMonitorMediator>(mediator => {
            WatchDevice(mediator, "safety_monitor");
            WatchSafety(mediator);
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
    /// poll loop only marks it disconnected. The Disconnected event only tells a requested disconnect from a lost one.
    /// </summary>
    private void WatchDevice<THandler, TConsumer, TInfo>(IDeviceMediator<THandler, TConsumer, TInfo> mediator, string device)
        where THandler : IDeviceVM<TInfo>
        where TConsumer : IDeviceConsumer<TInfo> {
        var watch = new DeviceWatch(device, () => mediator.GetInfo() is DeviceInfo info && info.Connected);

        Func<object, EventArgs, Task> disconnected = (_, _) => {
            watch.DisconnectRequestedAt = DateTime.UtcNow;

            return Task.CompletedTask;
        };

        mediator.Disconnected += disconnected;
        OnUnsubscribe(() => mediator.Disconnected -= disconnected);

        lock (devices) {
            devices.Add(watch);
        }
    }

    private async Task PollConnectionsAsync(CancellationToken cancellationToken) {
        List<DeviceWatch> watches;

        lock (devices) {
            watches = devices.ToList();
        }

        foreach (var watch in watches) {
            watch.WasConnected = IsConnected(watch);
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

    /// <summary>
    /// A VM marks its info disconnected before it raises Disconnected (a driver disconnect can take seconds in between),
    /// so a disconnect seen without a request is only judged one poll later.
    /// </summary>
    private async Task CheckConnectionAsync(DeviceWatch watch) {
        var connected = IsConnected(watch);

        if (watch.PendingLossSince is { } since && !connected) {
            watch.PendingLossSince = null;

            if (watch.DisconnectRequestedAt >= since - ExpectedDisconnectWindow) {
                await Publish("device", EventSeverity.Info, $"{watch.Device} disconnected.", Data("device", watch.Device));
            } else {
                await Publish(
                    "device",
                    EventSeverity.Error,
                    $"{watch.Device} lost its connection (not disconnected by request): check cable, power and driver.",
                    Data("device", watch.Device));
            }
        }

        if (connected == watch.WasConnected) {
            return;
        }

        watch.WasConnected = connected;

        if (connected) {
            watch.PendingLossSince = null;
            await Publish("device", EventSeverity.Info, $"{watch.Device} connected.", Data("device", watch.Device));
        } else if (DateTime.UtcNow - watch.DisconnectRequestedAt <= ExpectedDisconnectWindow) {
            await Publish("device", EventSeverity.Info, $"{watch.Device} disconnected.", Data("device", watch.Device));
        } else {
            watch.PendingLossSince = DateTime.UtcNow;
        }
    }

    private static bool IsConnected(DeviceWatch watch) {
        try {
            return watch.IsConnected();
        } catch (Exception) {
            return watch.WasConnected;
        }
    }

    /// <summary>
    /// The safety monitor VM raises "unsafe" when it is disconnected or its poll stops (its last values are reset), and
    /// "safe" right after connecting: only changes of a connected monitor are real.
    /// </summary>
    private void WatchSafety(ISafetyMonitorMediator mediator) {
        EventHandler<IsSafeEventArgs> changed = (_, args) => {
            if (mediator.GetInfo()?.Connected != true) {
                return;
            }

            Publish(
                "safety",
                args.IsSafe ? EventSeverity.Info : EventSeverity.Critical,
                args.IsSafe ? "The safety monitor reports safe." : "The safety monitor reports UNSAFE.",
                Data("isSafe", args.IsSafe));
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

        Func<object, ImageSaveFailedEventArgs, Task> failed = (_, args) => Publish(
            "image",
            EventSeverity.Error,
            args.IsDiskFull
                ? "Saving an image failed: the disk is full."
                : $"Saving an image failed: {args.Exception?.Message ?? "unknown error"}.",
            new Dictionary<string, object?> {
                ["path"] = args.FilePath,
                ["stage"] = args.FailureStage.ToString(),
                ["diskFull"] = args.IsDiskFull
            });

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
                        } else if (incident.Action == "saved") {
                            Publish(
                                "guider",
                                EventSeverity.Info,
                                $"Guiding incident saved ({incident.Summary?.Kind ?? "unknown"}, {incident.Summary?.FrameCount ?? 0} frames); get_guide_incident has the details.",
                                new Dictionary<string, object?> {
                                    ["incidentId"] = incident.Id,
                                    ["cause"] = incident.Summary?.Cause,
                                    ["endReason"] = incident.Summary?.EndReason
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

        public bool WasConnected { get; set; }

        public DateTime DisconnectRequestedAt { get; set; } = DateTime.MinValue;

        /// <summary>When a disconnect without a request was seen; judged on the next poll.</summary>
        public DateTime? PendingLossSince { get; set; }
    }
}
