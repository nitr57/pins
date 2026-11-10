// SPDX-License-Identifier: MPL-2.0

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NINA.Mcp;
using NINA.Mcp.Adapter;
using System.Collections.Generic;
using System.Linq;

namespace NINA.Test.Mcp {

    /// <summary>The MCP server's Information lines reach the pins log through the host's Warning minimum.</summary>
    [TestFixture]
    public class McpLoggingFilterTest {

        [Test]
        public void McpInformation_PassesTheHostsMinimumLevelAndConfiguredDefault() {
            var services = new ServiceCollection();

            // NINA/appsettings.json, should the host ever load it from its content root.
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["LogLevel:Default"] = "Error" })
                .Build();

            // As NINA/Program.cs configures the host's logging.
            services.AddLogging(logging => {
                logging.AddConfiguration(configuration);
                logging.ClearProviders();
                logging.SetMinimumLevel(LogLevel.Warning);
            });
            services.AddPinsMcpForNina(new McpServerOptions { Enabled = true });

            using var provider = services.BuildServiceProvider();
            var factory = provider.GetRequiredService<ILoggerFactory>();
            var rules = provider.GetRequiredService<IOptions<LoggerFilterOptions>>().Value.Rules;

            Assert.That(
                rules.Any(rule => rule.ProviderName == null && rule.CategoryName == null && rule.LogLevel == LogLevel.Error),
                Is.True,
                "the configured default is in place, so the assertions below show the MCP rule beating it");

            Assert.That(factory.CreateLogger("NINA.Mcp.Jobs.JobManager").IsEnabled(LogLevel.Information), Is.True);
            Assert.That(factory.CreateLogger("NINA.Mcp.Protocol.McpDispatcher").IsEnabled(LogLevel.Information), Is.True);
            Assert.That(factory.CreateLogger("NINA.Mcp.Jobs.JobManager").IsEnabled(LogLevel.Debug), Is.False, "the provider forwards from Information on");
        }
    }
}
