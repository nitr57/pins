// SPDX-License-Identifier: MPL-2.0

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NINA.Mcp.Observatory;
using NINA.Mcp.Tools.Catalog;

namespace NINA.Mcp.Tools;

public static class ToolServiceCollectionExtensions {

    /// <summary>
    /// Registers every pins MCP tool as an <see cref="McpTool"/> singleton (in the order they are listed to clients)
    /// and the server <see cref="McpServerInstructions"/>. Requires an <c>IPinsObservatory</c> registration.
    /// </summary>
    public static IServiceCollection AddPinsMcpTools(this IServiceCollection services) {
        services.AddSingleton(provider => new McpServerInstructions(
            PinsInstructions.Build(provider.GetService<McpServerOptions>()?.ReadOnly ?? false)));

        // Filled by the host (the adapter's event bridge), read by get_events.
        services.TryAddSingleton<ObservatoryEventLog>();

        // Status and queries.
        Add(services, observatory => new GetStatusTool(observatory));
        Add(services, observatory => new GetRecentImagesTool(observatory));
        Add(services, observatory => new GetSequenceTool(observatory));
        Add(services, observatory => new ListSavedSequencesTool(observatory));
        Add(services, observatory => new ListTargetTemplatesTool(observatory));
        Add(services, observatory => new GetJobTool(observatory));
        Add(services, observatory => new ListJobsTool(observatory));
        Add(services, observatory => new GetGuiderReportTool(observatory));
        Add(services, observatory => new GetCoachReportTool(observatory));
        Add(services, observatory => new ListGuideIncidentsTool(observatory));
        Add(services, observatory => new GetGuideIncidentTool(observatory));
        Add(services, observatory => new ReadLogTool(observatory));
        Add(services, observatory => new PlanTargetsTool(observatory));
        Add(services, observatory => new FindTargetsTool(observatory));
        Add(services, observatory => new GetSessionSummaryTool(observatory));
        Add(services, observatory => new GetSystemHealthTool(observatory));
        services.AddSingleton<McpTool>(provider => new GetEventsTool(
            provider.GetRequiredService<IPinsObservatory>(),
            provider.GetRequiredService<ObservatoryEventLog>()));

        // Stop tools.
        Add(services, observatory => new StopAllTool(observatory));
        Add(services, observatory => new CancelJobTool(observatory));
        Add(services, observatory => new StopSequenceTool(observatory));
        Add(services, observatory => new StopGuidingTool(observatory));
        Add(services, observatory => new SecureObservatoryTool(observatory));

        // Hardware.
        Add(services, observatory => new ConnectDeviceTool(observatory));
        Add(services, observatory => new DisconnectDeviceTool(observatory));
        Add(services, observatory => new SlewMountTool(observatory));
        Add(services, MountMotionTool.Park);
        Add(services, MountMotionTool.Unpark);
        Add(services, MountMotionTool.Home);
        Add(services, observatory => new SetTrackingTool(observatory));
        Add(services, observatory => new CaptureImageTool(observatory));
        Add(services, observatory => new CoolCameraTool(observatory));
        Add(services, observatory => new WarmCameraTool(observatory));
        Add(services, observatory => new MoveFocuserTool(observatory));
        Add(services, observatory => new RunAutofocusTool(observatory));
        Add(services, observatory => new ChangeFilterTool(observatory));
        Add(services, observatory => new StartGuidingTool(observatory));
        Add(services, observatory => new SetSwitchTool(observatory));
        Add(services, observatory => new PlateSolveTool(observatory));
        Add(services, observatory => new CenterTargetTool(observatory));
        Add(services, observatory => new MoveRotatorTool(observatory));
        Add(services, observatory => new ControlFlatDeviceTool(observatory));
        Add(services, observatory => new ControlDomeTool(observatory));

        // Advanced sequence.
        Add(services, observatory => new LoadSequenceTool(observatory));
        Add(services, observatory => new AddTargetTool(observatory));
        Add(services, observatory => new ClearTargetsTool(observatory));
        Add(services, observatory => new SaveSequenceTool(observatory));
        Add(services, observatory => new StartSequenceTool(observatory));
        Add(services, observatory => new ResetSequenceTool(observatory));

        return services;
    }

    private static void Add(IServiceCollection services, Func<IPinsObservatory, McpTool> create) {
        services.AddSingleton(provider => create(provider.GetRequiredService<IPinsObservatory>()));
    }
}
