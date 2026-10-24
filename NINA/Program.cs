using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NINA.Core.Utility;
using NINA.Core.Utility.Notification;
using NINA.Core.SignalR;
using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using NINA.Profile;
using System.Linq;
using NINA.Utility;
using NINA.WPF.Base.Interfaces.ViewModel;
using NINA.Equipment.Interfaces.ViewModel;
using NINA.Interfaces;
using NINA.Profile.Interfaces;
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

Logger.Info("Server fully initialized and running.");

// Register shutdown handler
var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
System.Windows.Application.ApplicationLifetime = lifetime;
lifetime.ApplicationStopping.Register(() => {
    Logger.Info("Application shutting down...");

    // Halt MCP jobs (slews, focuser moves, exposures) through their tokens before the equipment goes away under them
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