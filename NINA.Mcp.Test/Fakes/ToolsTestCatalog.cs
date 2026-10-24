// SPDX-License-Identifier: MPL-2.0

using Microsoft.Extensions.DependencyInjection;
using NINA.Mcp.Jobs;
using NINA.Mcp.Tools;
using NUnit.Framework;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NINA.Mcp.Test.Fakes;

/// <summary>The real tool catalog (via <c>AddPinsMcpTools</c>) on a <see cref="ToolsFakeObservatory"/> and a real <see cref="JobManager"/>.</summary>
internal sealed class ToolsTestCatalog : IDisposable {

    private readonly ServiceProvider provider;

    public ToolsTestCatalog(McpServerOptions? options = null) {
        Observatory = new ToolsFakeObservatory();
        Jobs = new JobManager();
        Options = options ?? new McpServerOptions();
        Context = new ToolContext(Jobs, Options);

        var services = new ServiceCollection();
        services.AddSingleton<Observatory.IPinsObservatory>(Observatory);
        services.AddSingleton<TimeProvider>(Clock);
        services.AddPinsMcpTools();

        provider = services.BuildServiceProvider();
        Tools = provider.GetServices<McpTool>().ToList();
        Instructions = provider.GetRequiredService<McpServerInstructions>();
        Events = provider.GetRequiredService<Observatory.ObservatoryEventLog>();
    }

    public ToolsFakeObservatory Observatory { get; }

    /// <summary>The tools' clock: 2026-10-11 21:00 UTC, the fake observatory's status time.</summary>
    public ToolsFixedTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 10, 11, 21, 0, 0, TimeSpan.Zero));

    public JobManager Jobs { get; }

    public McpServerOptions Options { get; }

    public ToolContext Context { get; }

    public IReadOnlyList<McpTool> Tools { get; }

    public McpServerInstructions Instructions { get; }

    public Observatory.ObservatoryEventLog Events { get; }

    public McpTool Tool(string name) {
        return Tools.Single(tool => tool.Name == name);
    }

    /// <summary>Invokes a tool directly. <see cref="ToolArgumentException"/> propagates (the dispatcher would turn it into an error result).</summary>
    public Task<ToolCallResult> CallAsync(string name, string argumentsJson = "{}", CancellationToken cancellationToken = default) {
        using var document = JsonDocument.Parse(argumentsJson);

        var arguments = new ToolArguments(document.RootElement.Clone());

        return Tool(name).InvokeAsync(arguments, Context, cancellationToken);
    }

    /// <summary>Invokes a tool and expects a <see cref="ToolArgumentException"/>; returns its message.</summary>
    public string ArgumentError(string name, string argumentsJson) {
        var exception = Assert.ThrowsAsync<ToolArgumentException>(() => CallAsync(name, argumentsJson));

        return exception!.Message;
    }

    public static JsonObject Job(ToolCallResult result) {
        Assert.That(result.IsError, Is.False, result.Text);

        return result.Structured!["job"]!.AsObject();
    }

    public void Dispose() {
        Jobs.CancelAll();
        provider.Dispose();
    }
}
