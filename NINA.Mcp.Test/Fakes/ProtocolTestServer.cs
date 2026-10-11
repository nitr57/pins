// SPDX-License-Identifier: MPL-2.0

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using NINA.Mcp.Tools;
using System.Net.Http.Headers;
using System.Text;

namespace NINA.Mcp.Test.Fakes;

/// <summary>A real Kestrel host on a random loopback port with the MCP endpoint mapped and the tool catalog replaced by fakes.</summary>
internal sealed class ProtocolTestServer : IAsyncDisposable {

    public const string LatestVersion = "2025-11-25";

    private readonly WebApplication app;

    private ProtocolTestServer(WebApplication app, Uri endpoint, ProtocolManualTimeProvider time) {
        this.app = app;
        Endpoint = endpoint;
        Time = time;
        Client = new HttpClient {
            Timeout = TimeSpan.FromSeconds(30)
        };
    }

    public Uri Endpoint { get; }

    public HttpClient Client { get; }

    public ProtocolManualTimeProvider Time { get; }

    public IServiceProvider Services => app.Services;

    public static async Task<ProtocolTestServer> StartAsync(
        McpServerOptions? options = null,
        IEnumerable<McpTool>? tools = null,
        string instructions = "Test instructions.",
        bool addMcp = true) {
        options ??= new McpServerOptions();

        var time = new ProtocolManualTimeProvider();
        var builder = WebApplication.CreateSlimBuilder();

        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<TimeProvider>(time);

        if (addMcp) {
            builder.Services.AddPinsMcp(options);

            // Replace the real tool catalog: transport tests must not depend on the observatory.
            builder.Services.RemoveAll<McpTool>();
            builder.Services.RemoveAll<McpServerInstructions>();
            builder.Services.AddSingleton(new McpServerInstructions(instructions));

            foreach (var tool in tools ?? [new ProtocolFakeTool("get_status")]) {
                builder.Services.AddSingleton(tool);
            }
        }

        var app = builder.Build();

        app.MapPinsMcp();

        await app.StartAsync();

        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        var endpoint = new Uri(new Uri(address), options.Path);

        return new ProtocolTestServer(app, endpoint, time);
    }

    public HttpRequestMessage CreatePost(string body, string? sessionId = null, string contentType = "application/json") {
        var request = new HttpRequestMessage(HttpMethod.Post, Endpoint) {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body))
        };

        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");

        if (sessionId is not null) {
            request.Headers.Add("Mcp-Session-Id", sessionId);
            request.Headers.Add("MCP-Protocol-Version", LatestVersion);
        }

        return request;
    }

    public Task<HttpResponseMessage> PostAsync(string body, string? sessionId = null, CancellationToken cancellationToken = default) {
        return Client.SendAsync(CreatePost(body, sessionId), cancellationToken);
    }

    /// <summary>Runs initialize + notifications/initialized and returns the session id.</summary>
    public async Task<string> InitializeAsync(string? token = null, string? protocolVersion = null) {
        var request = CreatePost(InitializeBody(protocolVersion ?? LatestVersion));

        if (token is not null) {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        using var response = await Client.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var sessionId = response.Headers.GetValues("Mcp-Session-Id").Single();

        var initialized = CreatePost("""{"jsonrpc":"2.0","method":"notifications/initialized"}""", sessionId);

        if (token is not null) {
            initialized.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        using var initializedResponse = await Client.SendAsync(initialized);
        initializedResponse.EnsureSuccessStatusCode();

        return sessionId;
    }

    public static string InitializeBody(string protocolVersion, string id = "0") {
        return """{"jsonrpc":"2.0","id":ID,"method":"initialize","params":{"protocolVersion":"VERSION","capabilities":{},"clientInfo":{"name":"test","version":"1"}}}"""
            .Replace("ID", id)
            .Replace("VERSION", protocolVersion);
    }

    public async ValueTask DisposeAsync() {
        Client.Dispose();
        await app.StopAsync();
        await app.DisposeAsync();
    }
}
