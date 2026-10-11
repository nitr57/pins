// SPDX-License-Identifier: MPL-2.0

using NUnit.Framework;

namespace NINA.Mcp.Test;

[TestFixture]
public class McpServerOptionsTests {

    [Test]
    public void Defaults() {
        var options = new McpServerOptions();

        Assert.That(options.Enabled, Is.True);
        Assert.That(options.Path, Is.EqualTo("/mcp"));
        Assert.That(options.BearerToken, Is.Null);
        Assert.That(options.AllowedOrigins, Is.Empty);
        Assert.That(options.ReadOnly, Is.False);
        Assert.That(options.SessionIdleTimeout, Is.EqualTo(TimeSpan.FromHours(1)));
        Assert.That(options.MaxRequestBodyBytes, Is.EqualTo(1024 * 1024));
    }

    [Test]
    public void FromEnvironment_NothingSet_UsesDefaults() {
        var options = McpServerOptions.FromEnvironment("1.2.3", _ => null);

        Assert.That(options.Enabled, Is.True);
        Assert.That(options.BearerToken, Is.Null);
        Assert.That(options.AllowedOrigins, Is.Empty);
        Assert.That(options.ReadOnly, Is.False);
        Assert.That(options.ServerVersion, Is.EqualTo("1.2.3"));
    }

    [TestCase("0", false)]
    [TestCase("false", false)]
    [TestCase("No", false)]
    [TestCase(" OFF ", false)]
    [TestCase("1", true)]
    [TestCase("true", true)]
    [TestCase("yes", true)]
    [TestCase("on", true)]
    [TestCase("", true)]
    [TestCase("maybe", true)]
    public void FromEnvironment_Enabled(string value, bool expected) {
        var options = McpServerOptions.FromEnvironment("1", Variables(("PINS_MCP_ENABLED", value)));

        Assert.That(options.Enabled, Is.EqualTo(expected));
    }

    [TestCase("1", true)]
    [TestCase("TRUE", true)]
    [TestCase("0", false)]
    [TestCase("", false)]
    [TestCase("garbage", false)]
    public void FromEnvironment_ReadOnly(string value, bool expected) {
        var options = McpServerOptions.FromEnvironment("1", Variables(("PINS_MCP_READONLY", value)));

        Assert.That(options.ReadOnly, Is.EqualTo(expected));
    }

    [TestCase("  abc123  ", "abc123")]
    [TestCase("", null)]
    [TestCase("   ", null)]
    public void FromEnvironment_Token(string value, string? expected) {
        var options = McpServerOptions.FromEnvironment("1", Variables(("PINS_MCP_TOKEN", value)));

        Assert.That(options.BearerToken, Is.EqualTo(expected));
    }

    [Test]
    public void FromEnvironment_AllowedOrigins_SplitAndTrimmed() {
        var options = McpServerOptions.FromEnvironment(
            "1",
            Variables(("PINS_MCP_ALLOWED_ORIGINS", " https://a.example , ,http://b.example:8080,")));

        Assert.That(options.AllowedOrigins, Is.EqualTo(new[] { "https://a.example", "http://b.example:8080" }));
    }

    private static Func<string, string?> Variables(params (string Name, string Value)[] values) {
        var map = values.ToDictionary(value => value.Name, value => value.Value);

        return name => map.TryGetValue(name, out var value) ? value : null;
    }
}
