// SPDX-License-Identifier: MPL-2.0

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NINA.Core.Utility;
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

    /// <summary>A lost guide star raises an event per frame; one per minute is enough.</summary>
    private static readonly TimeSpan StarLostInterval = TimeSpan.FromMinutes(1);

    private readonly IServiceProvider services;
    private readonly ObservatoryEventLog log;
    private readonly List<Action> unsubscribers = [];

    private DateTime lastStarLost = DateTime.MinValue;

    public NinaEventBridge(IServiceProvider services, ObservatoryEventLog log) {
        this.services = services ?? throw new ArgumentNullException(nameof(services));
        this.log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public Task StartAsync(CancellationToken cancellationToken) {
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

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) {
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

    private void WatchDevice<THandler, TConsumer, TInfo>(IDeviceMediator<THandler, TConsumer, TInfo> mediator, string device)
        where THandler : IDeviceVM<TInfo>
        where TConsumer : IDeviceConsumer<TInfo> {
        Func<object, EventArgs, Task> connected = (_, _) => Publish("device", EventSeverity.Info, $"{device} connected.", Data("device", device));
        Func<object, EventArgs, Task> disconnected = (_, _) => Publish("device", EventSeverity.Warning, $"{device} disconnected.", Data("device", device));

        mediator.Connected += connected;
        mediator.Disconnected += disconnected;

        OnUnsubscribe(() => {
            mediator.Connected -= connected;
            mediator.Disconnected -= disconnected;
        });
    }

    private void WatchSafety(ISafetyMonitorMediator mediator) {
        EventHandler<IsSafeEventArgs> changed = (_, args) => Publish(
            "safety",
            args.IsSafe ? EventSeverity.Info : EventSeverity.Critical,
            args.IsSafe ? "The safety monitor reports safe." : "The safety monitor reports UNSAFE.",
            Data("isSafe", args.IsSafe));

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

    private void WatchSequence(ISequenceMediator mediator) {
        Func<object, EventArgs, Task> starting = (_, _) => Publish("sequence", EventSeverity.Info, "Advanced sequence starting.");
        Func<object, EventArgs, Task> finished = (_, _) => Publish("sequence", EventSeverity.Info, "Advanced sequence finished or stopped.");

        mediator.SequenceStarting += starting;
        mediator.SequenceFinished += finished;

        OnUnsubscribe(() => {
            mediator.SequenceStarting -= starting;
            mediator.SequenceFinished -= finished;
        });
    }

    /// <summary>Alerts, incidents and (rate-limited) star losses of the internal guider; steps and frames are far too many.</summary>
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
                        if (incident.Action == "started") {
                            Publish(
                                "guider",
                                EventSeverity.Warning,
                                $"Guiding incident recorded ({incident.Summary?.Kind ?? "unknown"}); get_guide_incident {incident.Id} has the details.",
                                new Dictionary<string, object?> { ["incidentId"] = incident.Id });
                        }

                        break;

                    case AdvancedGuiderEventTypes.StarLost:
                        var now = DateTime.UtcNow;

                        if (now - lastStarLost >= StarLostInterval) {
                            lastStarLost = now;
                            Publish("guider", EventSeverity.Warning, "Guide star lost.");
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
}
