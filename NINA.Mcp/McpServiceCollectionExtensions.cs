// SPDX-License-Identifier: MPL-2.0

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NINA.Mcp.Jobs;
using NINA.Mcp.Protocol;
using NINA.Mcp.Tools;
using NINA.Mcp.Transport;

namespace NINA.Mcp;

public static class McpServiceCollectionExtensions {

    /// <summary>
    /// Registers the MCP server: options, job manager, dispatcher, transport and every tool (via
    /// <see cref="ToolServiceCollectionExtensions.AddPinsMcpTools"/>). The host must also register an
    /// <c>IPinsObservatory</c>.
    /// </summary>
    public static IServiceCollection AddPinsMcp(this IServiceCollection services, McpServerOptions options) {
        ArgumentNullException.ThrowIfNull(options);

        services.AddLogging();
        services.TryAddSingleton(TimeProvider.System);

        services.AddSingleton(options);
        services.AddSingleton<IJobManager>(provider => new JobManager(provider.GetRequiredService<TimeProvider>()));
        services.AddSingleton<ToolContext>();
        services.AddSingleton<McpDispatcher>();
        services.AddSingleton<McpSessionStore>();
        services.AddSingleton(new McpOriginValidator(options));
        services.AddSingleton(new McpBearerToken(options));
        services.AddSingleton<McpHttpEndpoint>();

        services.AddPinsMcpTools();

        return services;
    }

    /// <summary>
    /// Maps the Streamable HTTP endpoint (POST, GET, DELETE) at <see cref="McpServerOptions.Path"/>. Maps nothing when
    /// <see cref="AddPinsMcp"/> was not called or the server is disabled.
    /// </summary>
    /// <remarks>
    /// The endpoint (and with it every tool) is resolved on the first request, not here, so that a broken tool or
    /// adapter registration fails MCP requests with 500 instead of preventing pins from starting.
    /// </remarks>
    public static IEndpointRouteBuilder MapPinsMcp(this IEndpointRouteBuilder endpoints) {
        var options = endpoints.ServiceProvider.GetService<McpServerOptions>();

        if (options is null || !options.Enabled) {
            return endpoints;
        }

        endpoints.MapPost(options.Path, context => Resolve(context).HandlePostAsync(context));
        endpoints.MapGet(options.Path, context => Resolve(context).HandleGetAsync(context));
        endpoints.MapDelete(options.Path, context => Resolve(context).HandleDeleteAsync(context));

        return endpoints;
    }

    private static McpHttpEndpoint Resolve(HttpContext context) {
        return context.RequestServices.GetRequiredService<McpHttpEndpoint>();
    }
}
