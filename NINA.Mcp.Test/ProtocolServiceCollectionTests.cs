// SPDX-License-Identifier: MPL-2.0

using Microsoft.Extensions.DependencyInjection;
using NINA.Mcp.Jobs;
using NINA.Mcp.Test.Fakes;
using NINA.Mcp.Tools;
using NINA.Mcp.Transport;
using NUnit.Framework;
using System.Net;

namespace NINA.Mcp.Test;

[TestFixture]
public class ProtocolServiceCollectionTests {

    [Test]
    public void AddPinsMcp_RegistersCoreServicesAndToolCatalog() {
        var services = new ServiceCollection();
        var options = new McpServerOptions();

        services.AddPinsMcp(options);

        Assert.That(services.Any(descriptor => descriptor.ServiceType == typeof(McpServerInstructions)), Is.True);

        using var provider = services.BuildServiceProvider();

        Assert.That(provider.GetRequiredService<McpServerOptions>(), Is.SameAs(options));
        Assert.That(provider.GetRequiredService<IJobManager>(), Is.InstanceOf<JobManager>());
        Assert.That(provider.GetRequiredService<IJobManager>(), Is.SameAs(provider.GetRequiredService<IJobManager>()));
        Assert.That(provider.GetRequiredService<ToolContext>().Jobs, Is.SameAs(provider.GetRequiredService<IJobManager>()));
        Assert.That(provider.GetRequiredService<McpSessionStore>(), Is.Not.Null);
    }

    [Test]
    public async Task BrokenToolRegistration_FailsRequestsNotStartup() {
        var broken = new ProtocolFakeTool("a");
        var duplicate = new ProtocolFakeTool("a");

        await using var server = await ProtocolTestServer.StartAsync(tools: [broken, duplicate]);

        using var response = await server.PostAsync(ProtocolTestServer.InitializeBody("2025-11-25"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.InternalServerError));
    }
}
