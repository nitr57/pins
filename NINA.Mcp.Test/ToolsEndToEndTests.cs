// SPDX-License-Identifier: MPL-2.0

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NINA.Mcp.Observatory;
using NINA.Mcp.Protocol;
using NINA.Mcp.Test.Fakes;
using NINA.Mcp.Tools;
using NUnit.Framework;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NINA.Mcp.Test;

/// <summary>The real catalog through <c>AddPinsMcp</c>, the dispatcher and (once) the HTTP endpoint.</summary>
[TestFixture]
public class ToolsEndToEndTests {

    [Test]
    public async Task Dispatcher_ListsAllToolsAndInstructions() {
        await using var provider = BuildProvider(new McpServerOptions(), out _);
        var dispatcher = provider.GetRequiredService<McpDispatcher>();

        var initialize = await DispatchAsync(dispatcher, """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25"}}""");
        var list = await DispatchAsync(dispatcher, """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");

        Assert.That(initialize["result"]!["instructions"]!.GetValue<string>(), Does.Contain("stop_all"));

        var tools = list["result"]!["tools"]!.AsArray();

        Assert.That(tools, Has.Count.EqualTo(43));
        Assert.That(tools[0]!["name"]!.GetValue<string>(), Is.EqualTo("get_status"));

        var slew = tools.Single(tool => tool!["name"]!.GetValue<string>() == "slew_mount")!;

        Assert.That(slew["annotations"]!["destructiveHint"]!.GetValue<bool>(), Is.True);
        Assert.That(slew["annotations"]!["readOnlyHint"]!.GetValue<bool>(), Is.False);
        Assert.That(slew["inputSchema"]!["required"]!.AsArray().Select(node => node!.GetValue<string>()), Is.EqualTo(new[] { "ra", "dec" }));
    }

    [Test]
    public async Task Dispatcher_ReadOnlyMode_ListsQueriesPlusEmergencyStops() {
        await using var provider = BuildProvider(new McpServerOptions { ReadOnly = true }, out var observatory);
        var dispatcher = provider.GetRequiredService<McpDispatcher>();

        var list = await DispatchAsync(dispatcher, """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""");
        var names = list["result"]!["tools"]!.AsArray().Select(tool => tool!["name"]!.GetValue<string>()).ToList();

        Assert.That(names, Is.EqualTo(new[] {
            "get_status", "get_recent_images", "get_sequence", "list_saved_sequences", "list_target_templates", "get_job", "list_jobs",
            "get_guider_report", "get_guiding_coach_report", "list_guide_incidents", "get_guide_incident", "read_log", "plan_targets",
            "stop_all", "cancel_job"
        }));

        var slew = await DispatchAsync(dispatcher, """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"slew_mount","arguments":{"ra":1,"dec":1}}}""");
        var stop = await DispatchAsync(dispatcher, """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"stop_all","arguments":{}}}""");

        Assert.That(slew["error"]!["code"]!.GetValue<int>(), Is.EqualTo(JsonRpcErrorCodes.InvalidParams));
        Assert.That(stop["result"]!["isError"]!.GetValue<bool>(), Is.False);
        Assert.That(observatory.Calls, Does.Contain("StopSequence"));
        Assert.That(observatory.Calls, Does.Not.Contain("Slew"));
    }

    [Test]
    public async Task Dispatcher_ArgumentErrorsAndConflictsBecomeToolErrors() {
        await using var provider = BuildProvider(new McpServerOptions(), out var observatory);
        var dispatcher = provider.GetRequiredService<McpDispatcher>();

        var bad = await DispatchAsync(dispatcher, """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"slew_mount","arguments":{"ra":"25h","dec":0}}}""");

        Assert.That(bad["result"]!["isError"]!.GetValue<bool>(), Is.True);
        Assert.That(bad["result"]!["content"]![0]!["text"]!.GetValue<string>(), Does.Contain("'ra'"));

        observatory.Block("Slew");

        var first = await DispatchAsync(dispatcher, """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"slew_mount","arguments":{"ra":1,"dec":1,"wait_seconds":0}}}""");
        var second = await DispatchAsync(dispatcher, """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"park_mount","arguments":{}}}""");

        Assert.That(first["result"]!["structuredContent"]!["job"]!["state"]!.GetValue<string>(), Is.EqualTo("running"));
        Assert.That(second["result"]!["isError"]!.GetValue<bool>(), Is.True);
        Assert.That(second["result"]!["content"]![0]!["text"]!.GetValue<string>(), Does.Contain("The mount is busy"));

        observatory.Release("Slew");
    }

    [Test]
    public async Task Http_ToolsListAndCall() {
        var observatory = new ToolsFakeObservatory();
        var builder = WebApplication.CreateSlimBuilder();

        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IPinsObservatory>(observatory);
        builder.Services.AddPinsMcp(new McpServerOptions());

        await using var app = builder.Build();

        app.MapPinsMcp();
        await app.StartAsync();

        try {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
            var endpoint = new Uri(new Uri(address), "/mcp");

            using var client = new HttpClient();

            using var initialize = await PostAsync(client, endpoint, ProtocolTestServer.InitializeBody("2025-11-25"), null);
            var session = initialize.Headers.GetValues("Mcp-Session-Id").Single();

            using var listResponse = await PostAsync(client, endpoint, """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""", session);
            var list = JsonNode.Parse(await listResponse.Content.ReadAsStringAsync())!;

            Assert.That(list["result"]!["tools"]!.AsArray(), Has.Count.EqualTo(43));

            var call = """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"slew_mount","arguments":{"ra":"05h35m17.3s","dec":"-05°23'28\""}}}""";

            using var callResponse = await PostAsync(client, endpoint, call, session);
            var result = JsonNode.Parse(await callResponse.Content.ReadAsStringAsync())!["result"]!;

            Assert.That(result["isError"]!.GetValue<bool>(), Is.False, result.ToJsonString());
            Assert.That(result["structuredContent"]!["job"]!["state"]!.GetValue<string>(), Is.EqualTo("succeeded"));
            Assert.That(result["structuredContent"]!["job"]!["description"]!.GetValue<string>(), Does.Contain("-05°23'28\""));
            Assert.That(observatory.LastSlew!.Value.Ra, Is.EqualTo(5 + (35 / 60.0) + (17.3 / 3600)).Within(1e-9));
        } finally {
            await app.StopAsync();
        }
    }

    private static ServiceProvider BuildProvider(McpServerOptions options, out ToolsFakeObservatory observatory) {
        observatory = new ToolsFakeObservatory();

        var services = new ServiceCollection();
        services.AddSingleton<IPinsObservatory>(observatory);
        services.AddPinsMcp(options);

        return services.BuildServiceProvider();
    }

    private static async Task<JsonObject> DispatchAsync(McpDispatcher dispatcher, string json) {
        using var document = JsonDocument.Parse(json);

        var result = await dispatcher.DispatchAsync(document.RootElement, CancellationToken.None);

        return result.Response!;
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, Uri endpoint, string body, string? session) {
        var request = new HttpRequestMessage(HttpMethod.Post, endpoint) {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body))
        };

        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/json");
        request.Headers.Accept.ParseAdd("application/json");

        if (session is not null) {
            request.Headers.Add("Mcp-Session-Id", session);
        }

        return client.SendAsync(request);
    }
}
