// SPDX-License-Identifier: MPL-2.0

using NINA.Mcp.Test.Fakes;
using NINA.Mcp.Transport;
using NUnit.Framework;
using System.Net;

namespace NINA.Mcp.Test;

[TestFixture]
public class TransportSecurityTests {

    // ----- Origin -----

    [TestCase(null)]
    [TestCase("")]
    [TestCase("http://localhost")]
    [TestCase("http://localhost:4782")]
    [TestCase("https://LOCALHOST:8443")]
    [TestCase("http://localhost.:4782")]
    [TestCase("http://127.0.0.1:4782")]
    [TestCase("http://127.255.10.1")]
    [TestCase("http://10.0.0.5:4782")]
    [TestCase("http://172.16.0.1")]
    [TestCase("http://172.31.255.254")]
    [TestCase("http://192.168.1.20:4782")]
    [TestCase("http://169.254.12.1")]
    [TestCase("http://100.64.0.1")]
    [TestCase("http://100.101.102.103:4782")]
    [TestCase("http://100.127.255.255")]
    [TestCase("http://[::1]:4782")]
    [TestCase("http://[fd7a:115c:a1e0::1]:4782")]
    [TestCase("http://[fc00::1]")]
    [TestCase("http://[fe80::1]")]
    [TestCase("http://[::ffff:192.168.1.20]")]
    [TestCase("http://pins.local:4782")]
    [TestCase("http://astro-pi.LOCAL")]
    [TestCase("http://pins-pi")]
    [TestCase("http://PINS-PI:4782")]
    [TestCase("http://pins-pi.local")]
    public void Origin_Allowed(string? origin) {
        var validator = new McpOriginValidator(new McpServerOptions(), "pins-pi");

        Assert.That(validator.IsAllowed(origin), Is.True);
    }

    [TestCase("http://evil.com")]
    [TestCase("https://evil.com:4782")]
    [TestCase("http://127.0.0.1.nip.io:4782")]
    [TestCase("http://192.168.1.20.sslip.io")]
    [TestCase("http://localhost.evil.com")]
    [TestCase("http://evil-localhost")]
    [TestCase("http://pins-pi.evil.com")]
    [TestCase("http://local")]
    [TestCase("http://evil.localhost.com")]
    [TestCase("http://8.8.8.8")]
    [TestCase("http://172.32.0.1")]
    [TestCase("http://172.15.255.255")]
    [TestCase("http://192.169.0.1")]
    [TestCase("http://100.128.0.1")]
    [TestCase("http://100.63.255.255")]
    [TestCase("http://169.253.1.1")]
    [TestCase("http://[2001:4860:4860::8888]")]
    [TestCase("http://[fec0::1]")]
    [TestCase("http://[::ffff:8.8.8.8]")]
    [TestCase("null")]
    [TestCase("not a url")]
    [TestCase("file:///etc/passwd")]
    public void Origin_Rejected(string origin) {
        var validator = new McpOriginValidator(new McpServerOptions(), "pins-pi");

        Assert.That(validator.IsAllowed(origin), Is.False);
    }

    [TestCase("https://my.host", true)]
    [TestCase("https://my.host:443", true)]
    [TestCase("https://MY.HOST", true)]
    [TestCase("https://my.host/", true)]
    [TestCase("http://my.host", false)]
    [TestCase("https://my.host:8443", false)]
    [TestCase("https://other.host", false)]
    [TestCase("https://sub.my.host", false)]
    [TestCase("http://tns.example.org:8080", true)]
    [TestCase("http://tns.example.org", false)]
    public void Origin_ConfiguredOrigins_MatchSchemeHostAndPortExactly(string origin, bool expected) {
        var options = new McpServerOptions {
            AllowedOrigins = ["https://my.host", "http://tns.example.org:8080", "garbage"]
        };
        var validator = new McpOriginValidator(options, "pins-pi");

        Assert.That(validator.IsAllowed(origin), Is.EqualTo(expected));
    }

    [Test]
    public void Origin_EmptyMachineName_DoesNotAllowEverything() {
        var validator = new McpOriginValidator(new McpServerOptions(), "");

        Assert.That(validator.IsAllowed("http://evil.com"), Is.False);
    }

    [Test]
    public void Origin_DefaultConstructorUsesMachineName() {
        var validator = new McpOriginValidator(new McpServerOptions());

        Assert.That(validator.IsAllowed($"http://{Environment.MachineName}:4782"), Is.True);
    }

    [TestCase("127.0.0.1", true)]
    [TestCase("0.0.0.0", false)]
    [TestCase("255.255.255.255", false)]
    [TestCase("::", false)]
    [TestCase("::1", true)]
    public void IsLocalAddress(string address, bool expected) {
        Assert.That(McpOriginValidator.IsLocalAddress(IPAddress.Parse(address)), Is.EqualTo(expected));
    }

    // ----- Bearer token -----

    [TestCase(null)]
    [TestCase("")]
    [TestCase("Bearer anything")]
    public void Token_NotConfigured_EverythingAuthorized(string? header) {
        var token = new McpBearerToken(new McpServerOptions());

        Assert.That(token.IsRequired, Is.False);
        Assert.That(token.IsAuthorized(header), Is.True);
    }

    [TestCase(null, false)]
    [TestCase("", false)]
    [TestCase("Bearer", false)]
    [TestCase("Bearer ", false)]
    [TestCase("Bearer s3cret-wrong", false)]
    [TestCase("Bearer s3cre", false)]
    [TestCase("Bearer S3CRET-TOKEN", false)]
    [TestCase("Basic s3cret-token", false)]
    [TestCase("s3cret-token", false)]
    [TestCase("Bearer s3cret-token", true)]
    [TestCase("bearer s3cret-token", true)]
    [TestCase("Bearer  s3cret-token ", true)]
    public void Token_Configured(string? header, bool expected) {
        var token = new McpBearerToken(new McpServerOptions { BearerToken = "s3cret-token" });

        Assert.That(token.IsRequired, Is.True);
        Assert.That(token.IsAuthorized(header), Is.EqualTo(expected));
    }

    // ----- Sessions -----

    [Test]
    public void Sessions_CreateAndGet() {
        var time = new ProtocolManualTimeProvider();
        var store = new McpSessionStore(new McpServerOptions(), time);

        var session = store.Create("2025-06-18");

        Assert.That(session.Id, Does.Match("^[0-9a-f]{32}$"));
        Assert.That(session.ProtocolVersion, Is.EqualTo("2025-06-18"));
        Assert.That(store.Get(session.Id), Is.SameAs(session));
        Assert.That(store.Get("unknown"), Is.Null);
    }

    [Test]
    public void Sessions_IdsAreUnique() {
        var store = new McpSessionStore(new McpServerOptions(), new ProtocolManualTimeProvider());

        var ids = Enumerable.Range(0, 50).Select(_ => store.Create("2025-11-25").Id).ToList();

        Assert.That(ids, Is.Unique);
    }

    [Test]
    public void Sessions_ExpireWhenIdle_AndUseExtendsLifetime() {
        var time = new ProtocolManualTimeProvider();
        var store = new McpSessionStore(new McpServerOptions { SessionIdleTimeout = TimeSpan.FromMinutes(10) }, time);
        var session = store.Create("2025-11-25");

        time.Advance(TimeSpan.FromMinutes(9));
        Assert.That(store.Get(session.Id), Is.Not.Null);

        time.Advance(TimeSpan.FromMinutes(9));
        Assert.That(store.Get(session.Id), Is.Not.Null);

        time.Advance(TimeSpan.FromMinutes(11));
        Assert.That(store.Get(session.Id), Is.Null);
        Assert.That(store.Count, Is.Zero);
    }

    [Test]
    public void Sessions_CreateSweepsExpired() {
        var time = new ProtocolManualTimeProvider();
        var store = new McpSessionStore(new McpServerOptions { SessionIdleTimeout = TimeSpan.FromMinutes(10) }, time);

        store.Create("2025-11-25");
        store.Create("2025-11-25");
        time.Advance(TimeSpan.FromMinutes(11));

        store.Create("2025-11-25");

        Assert.That(store.Count, Is.EqualTo(1));
    }

    [Test]
    public void Sessions_CappedByEvictingLeastRecentlyUsed() {
        var time = new ProtocolManualTimeProvider();
        var store = new McpSessionStore(new McpServerOptions(), time);
        var first = store.Create("2025-11-25");
        time.Advance(TimeSpan.FromSeconds(1));
        var second = store.Create("2025-11-25");

        for (var i = 2; i < McpSessionStore.MaxSessions; i++) {
            time.Advance(TimeSpan.FromSeconds(1));
            store.Create("2025-11-25");
        }

        time.Advance(TimeSpan.FromSeconds(1));
        store.Get(first.Id);

        time.Advance(TimeSpan.FromSeconds(1));
        var newest = store.Create("2025-11-25");

        Assert.That(store.Count, Is.EqualTo(McpSessionStore.MaxSessions));
        Assert.That(store.Get(first.Id), Is.Not.Null);
        Assert.That(store.Get(second.Id), Is.Null);
        Assert.That(store.Get(newest.Id), Is.Not.Null);
    }

    [Test]
    public void Sessions_Remove() {
        var time = new ProtocolManualTimeProvider();
        var store = new McpSessionStore(new McpServerOptions { SessionIdleTimeout = TimeSpan.FromMinutes(10) }, time);
        var live = store.Create("2025-11-25");
        var stale = store.Create("2025-11-25");

        Assert.That(store.Remove(live.Id), Is.True);
        Assert.That(store.Get(live.Id), Is.Null);
        Assert.That(store.Remove(live.Id), Is.False);
        Assert.That(store.Remove("unknown"), Is.False);

        time.Advance(TimeSpan.FromMinutes(11));

        Assert.That(store.Remove(stale.Id), Is.False);
    }
}
