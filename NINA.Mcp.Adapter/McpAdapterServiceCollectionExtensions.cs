// SPDX-License-Identifier: MPL-2.0

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NINA.Mcp.Adapter.Sequence;
using NINA.Mcp.Observatory;

namespace NINA.Mcp.Adapter;

public static class McpAdapterServiceCollectionExtensions {

    /// <summary>
    /// Registers the MCP server (<see cref="McpServiceCollectionExtensions.AddPinsMcp"/>) with the NINA-backed
    /// <see cref="IPinsObservatory"/>. Call it on the pins web host's service collection; the NINA services it needs are
    /// registered by <c>IoCBindings</c> in the same collection and resolved lazily on the first MCP request, so the
    /// order of the two registrations does not matter. Does nothing when the endpoint is disabled.
    /// </summary>
    public static IServiceCollection AddPinsMcpForNina(this IServiceCollection services, McpServerOptions options) {
        ArgumentNullException.ThrowIfNull(options);

        if (!options.Enabled) {
            return services;
        }

        services.AddPinsMcp(options);
        services.AddSingleton<ILoggerProvider, NinaLoggerProvider>();
        services.AddSingleton<NinaSequenceService>();
        services.AddSingleton<IPinsObservatory, NinaObservatory>();

        // Records NINA's events for get_events from the start on; it never fails the pins start.
        services.AddHostedService<NinaEventBridge>();

        return services;
    }
}
