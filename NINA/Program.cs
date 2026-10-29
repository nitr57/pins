#region "copyright"

/*
    Copyright © 2025 Nico Trost <nico.trost57@gmail.com> and the PI.N.S. contributors

    This file is part of PI 'N' Stars.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NINA.Astrometry;
using NINA.Core.Utility;
using NINA.Core.Utility.Notification;
using NINA.Core.SignalR;
using System;
using System.Globalization;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using NINA.Profile;
using System.Linq;
using NINA.Utility;
using NINA.WPF.Base.Interfaces.ViewModel;
using NINA.Equipment.Interfaces.ViewModel;
using NINA.Interfaces;
using NINA.Profile.Interfaces;
using NINA.ViewModel;
using NINA.ViewModel.Sequencer;
using NINA.ViewModel.Interfaces;
using NINA.Plugin.Interfaces;
using NINA.INDI;
using NINA.Equipment.Interfaces.Mediator;
using NINA.WPF.Base.ViewModel.Equipment.Camera;
using NINA.Mcp;
using NINA.Mcp.Adapter;

// Disable file watching to prevent inotify limit issues on Linux
Environment.SetEnvironmentVariable("DOTNET_USE_POLLING_FILE_WATCHER", "true");

// As upstream App.xaml.cs: number formatting and parsing must not follow the system locale
// (a de_DE Pi would hand ASTAP "-r 2,5"). Loc resolves its UI language from the profile separately.
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
System.Threading.Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;

// As upstream App.xaml.cs: log what takes the process down. An exception from async void code (a folder
// watcher, a synchronously throwing command) ends pins, systemd restarts it, and the log would just stop.
AppDomain.CurrentDomain.UnhandledException += (sender, e) => {
    if (e.ExceptionObject is Exception exception) {
        Logger.Error($"An unhandled domain exception has occurred of type {exception.GetType()}. Application must terminate: {e.IsTerminating}", exception);
    } else {
        Logger.Error("An unhandled domain exception has occurred.");
    }

    if (e.IsTerminating) {
        Logger.CloseAndFlush();
    }
};

// A faulted task nobody awaited doesn't end the process, but its exception would otherwise vanish
TaskScheduler.UnobservedTaskException += (sender, e) => {
    // A socket closed with a receive still pending ends it as aborted; when nobody awaited it, that is no error. Seen
    // from ASCOM Alpaca discovery's UDP sockets, but this matches any unobserved task that only holds such aborts.
    var onlyAbortedSocketReceives = e.Exception.InnerExceptions.All(ex => ex is SocketException { SocketErrorCode: SocketError.OperationAborted });

    if (onlyAbortedSocketReceives) {
        Logger.Debug($"An unobserved socket receive was aborted when its socket closed: {e.Exception.InnerException?.Message}");
        return;
    }

    Logger.Error($"An unobserved task exception has occurred of type {e.Exception.InnerException?.GetType() ?? e.Exception.GetType()}", e.Exception);
};

// Now build the full application with all services
var builder = WebApplication.CreateBuilder(args);
builder.Configuration["Kestrel:Endpoints:Http:Url"] = "http://0.0.0.0:4782";

// Clear all default logging providers to suppress ASP.NET Core noise
builder.Logging.ClearProviders();
builder.Logging.SetMinimumLevel(LogLevel.Warning);

// Add only SignalR services
builder.Services.AddCors(options => {
    options.AddPolicy("AllowAll", builder => {
        builder.AllowAnyOrigin()
               .AllowAnyMethod()
               .AllowAnyHeader()
               .DisallowCredentials(); // SignalR with * origin cannot use credentials
    });
});
builder.Services.AddSignalR();
builder.Services.AddSingleton<SignalRNotificationBroadcaster>();
// Progress broadcaster (SignalR)
builder.Services.AddSingleton<SignalRProgressBroadcaster>();
builder.Services.AddSingleton<IDialogBroadcaster, DialogBroadcaster>();
builder.Services.AddSingleton<IMyMessageBoxBroadcaster, MyMessageBoxBroadcaster>();

// Command line
var allArgs = Environment.GetCommandLineArgs();
// Filter out ASP.NET Core built-in options to avoid parsing errors in CommandLineOptions
var customArgs = allArgs.Where(arg => !arg.StartsWith("--urls") &&
                                     !arg.StartsWith("--environment") &&
                                     !arg.StartsWith("--contentroot") &&
                                     !arg.StartsWith("--applicationName") &&
                                     !arg.StartsWith("--webroot") &&
                                     !arg.StartsWith("--webconfig")).ToArray();
CommandLineOptions commandLineOptions = new(customArgs);
if (commandLineOptions.HasErrors) {
    Environment.Exit(-1);
}
if (commandLineOptions.Debug) {
    CoreUtil.DebugMode = commandLineOptions.Debug;
}

var startWithProfileId = commandLineOptions.ProfileId;

// Profile
ProfileService profileService = new();
if (!profileService.TryLoad(startWithProfileId)) {
    Environment.Exit(-1);
}

Logger.SetLogLevel(profileService.ActiveProfile.ApplicationSettings.LogLevel);

if (customArgs.Length > 0) {
    Logger.Info("Launched with command line arguments: " + string.Join(" ", customArgs));
}

// Register application services NOW that we have an active profile
new IoCBindings(profileService, commandLineOptions).Load(builder.Services);

// MCP endpoint for AI clients (Claude Code etc.) on this web host; configured through PINS_MCP_* environment variables
var mcpOptions = McpServerOptions.FromEnvironment(CoreUtil.Version);
builder.Services.AddPinsMcpForNina(mcpOptions);

var app = builder.Build();

// Initialize DialogBroadcaster early so plugins can access it
_ = app.Services.GetRequiredService<IDialogBroadcaster>();

// Same for the message box broadcaster: MyMessageBox.Show uses its static Instance, which otherwise
// only exists once the first client has connected to /hubs/messageboxes
_ = app.Services.GetRequiredService<IMyMessageBoxBroadcaster>();

// Initialize INDIClient early to start server and FIFO setup
_ = INDIClient.Instance;

// Now that profile is ready, instantiate services and Controller
_ = app.Services.GetService<IImageSaveController>();
_ = app.Services.GetService<IImagingVM>();
_ = app.Services.GetService<IApplicationVM>();
_ = app.Services.GetService<IEquipmentVM>();
_ = app.Services.GetService<GuideCameraVM>();
_ = app.Services.GetService<ISkyAtlasVM>();
_ = app.Services.GetService<ISequenceNavigationVM>();
_ = app.Services.GetService<IFramingAssistantVM>();
_ = app.Services.GetService<IFlatWizardVM>();
_ = app.Services.GetService<IOptionsVM>();
_ = app.Services.GetService<IApplicationDeviceConnectionVM>();
_ = app.Services.GetService<IVersionCheckVM>();
_ = app.Services.GetService<IApplicationStatusVM>();
_ = app.Services.GetService<IImageHistoryVM>();
_ = app.Services.GetService<IPluginsVM>();
_ = app.Services.GetService<GlobalObjects>();

// Enable CORS for SignalR connections from other ports/origins
app.UseCors("AllowAll");

// Map SignalR hubs only
app.MapHub<NotificationHub>("/hubs/notifications");
app.MapHub<DialogHub>("/hubs/dialogs");
app.MapHub<MyMessageBoxHub>("/hubs/messageboxes");
// Progress hub
app.MapHub<ProgressHub>("/hubs/progress");

// MCP endpoint (Streamable HTTP); maps nothing when PINS_MCP_ENABLED is off
app.MapPinsMcp();
if (mcpOptions.Enabled) {
    Logger.Info($"MCP endpoint enabled at {mcpOptions.Path} (token {(mcpOptions.BearerToken is null ? "off" : "on")}, read-only {(mcpOptions.ReadOnly ? "on" : "off")})");

    if (mcpOptions.BearerToken is null && !mcpOptions.ReadOnly) {
        Logger.Warning("MCP endpoint allows hardware control without PINS_MCP_TOKEN: anyone on the network can move the telescope");
    }
}

// Set up the notification broadcaster to use SignalR
var signalRBroadcaster = app.Services.GetRequiredService<SignalRNotificationBroadcaster>();
Notification.Broadcaster = async (message) => {
    await signalRBroadcaster.BroadcastNotificationAsync(message);
};

// Set up the progress broadcaster to use SignalR
var signalRProgressBroadcaster = app.Services.GetRequiredService<SignalRProgressBroadcaster>();
Progress.Broadcaster = async (message) => {
    await signalRProgressBroadcaster.BroadcastProgressAsync(message);
};

// Earth rotation parameters (UT1-UTC, polar motion) from the IERS, which upstream NINA updates at startup in App.xaml.cs.
// Only when they are outdated (the updater's own limit of 10 days) is anything logged: the update needs
// datacenter.iers.org, which a Pi on its own hotspot can't reach.
_ = Task.Run(async () => {
    try {
        var updater = new EarthRotationParameterUpdater();
        var lastAvailable = await updater.GetLastAvailableEarthRotationParameterDate();
        if (DateTime.Now - lastAvailable <= TimeSpan.FromDays(10)) {
            return;
        }

        try {
            using var dnsTimeout = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(5));
            await System.Net.Dns.GetHostAddressesAsync("datacenter.iers.org", dnsTimeout.Token);
        } catch (Exception) {
            Logger.Warning($"Earth rotation parameters are outdated (data up to {lastAvailable:yyyy-MM-dd}); no internet to update them from datacenter.iers.org");
            return;
        }

        await updater.Update();
    } catch (Exception ex) {
        Logger.Error("Checking the earth rotation parameters failed", ex);
    }
});

Logger.Info("Server fully initialized and running.");

// Register shutdown handler
var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
System.Windows.Application.ApplicationLifetime = lifetime;
lifetime.ApplicationStopping.Register(() => {
    Logger.Info("Application shutting down...");

    // First: indiserver ending from now on is the shutdown, not a crash, so no recovery may restart it and its drivers
    INDIClient.Instance.BeginShutdown();

    // Halt MCP jobs (slews, focuser moves, exposures, also a park) through their tokens before the equipment goes away
    // under them: whoever stops pins wants it stopped, and a park can be started again afterwards
    try {
        var stillRunning = app.Services.StopPinsMcpJobs(TimeSpan.FromSeconds(5));
        if (stillRunning > 0) {
            Logger.Warning($"{stillRunning} MCP job(s) did not stop before shutdown");
        }
    } catch (Exception ex) {
        Logger.Error("Failed to stop MCP jobs", ex);
    }

    // The guide camera isn't part of the equipment ApplicationVM.Closing disconnects. Stop the guider before
    // taking its camera away, and close the camera before Closing shuts down the camera SDKs (Atik).
    try {
        Nito.AsyncEx.AsyncContext.Run(async () => {
            await app.Services.GetRequiredService<IGuiderMediator>().Disconnect();
            await app.Services.GetRequiredService<IGuideCameraMediator>().Disconnect();
        });
    } catch (Exception ex) {
        Logger.Error("Failed to disconnect the guide camera", ex);
    }

    // ApplicationVM.Closing disposes the INDI client before it disconnects the equipment, which would cut off the
    // DISCONNECT that INDI devices send in the background, and the drivers then never close their hardware (cooler,
    // ports). So disconnect the equipment here first and let those handshakes finish while the server still runs;
    // Closing then finds it disconnected.
    try {
        if (app.Services.GetService<IApplicationDeviceConnectionVM>() is ApplicationDeviceConnectionVM deviceConnection) {
            Nito.AsyncEx.AsyncContext.Run(deviceConnection.DisconnectEquipment);
        }

        INDIClient.Instance.WaitForDeviceDisconnects(TimeSpan.FromSeconds(10));
    } catch (Exception ex) {
        Logger.Error("Failed to disconnect the equipment", ex);
    }

    // Call ApplicationVM.Closing() to properly disconnect equipment and cleanup
    var applicationVM = app.Services.GetService<IApplicationVM>();
    if (applicationVM != null) {
        try {
            // Use reflection to call the private Closing method
            var closingMethod = applicationVM.GetType().GetMethod("Closing",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            closingMethod?.Invoke(applicationVM, null);
        } catch (Exception ex) {
            Logger.Error("Failed to call ApplicationVM.Closing()", ex);
        }
    }
});

// Keep server running
app.Run();