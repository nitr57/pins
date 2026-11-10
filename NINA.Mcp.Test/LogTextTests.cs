// SPDX-License-Identifier: MPL-2.0

using NINA.Mcp.Logs;
using NUnit.Framework;
using System.Text.Json;

namespace NINA.Mcp.Test;

[TestFixture]
public class LogTextTests {

    [TestCase(null, "")]
    [TestCase("", "")]
    [TestCase("one line", "one line")]
    [TestCase("first\r\nsecond\n\tthird  ", "first second third")]
    [TestCase("\n leading", "leading")]
    [TestCase("M42\u001b[2J\0end", "M42 [2J end")]
    [TestCase("line\u2028separator\u0085next", "line separator next")]
    public void Compact_PutsTextOnOneLine(string? text, string expected) {
        Assert.That(LogText.Compact(text), Is.EqualTo(expected));
    }

    [Test]
    public void Compact_CutsLongText() {
        var compact = LogText.Compact(new string('x', 400), 300);

        Assert.That(compact, Has.Length.EqualTo(301));
        Assert.That(compact, Does.EndWith("x…"));
    }

    [Test]
    public void Compact_CutAtASpace_StillShowsTheCut() {
        var compact = LogText.Compact(new string('x', 300) + " more", 300);

        Assert.That(compact, Is.EqualTo(new string('x', 300) + "…"));
    }

    [Test]
    public void Compact_DoesNotSplitASurrogatePair() {
        // 299 characters, then a star emoji (two UTF-16 chars) across the limit
        var compact = LogText.Compact(new string('x', 299) + "\U0001F31F tail", 300);

        Assert.That(compact, Is.EqualTo(new string('x', 299) + "…"));
    }

    [Test]
    public void Compact_SurrogatePairAfterASpace_LeavesNoSpaceBeforeTheCut() {
        var compact = LogText.Compact("ab \U0001F31F", 4);

        Assert.That(compact, Is.EqualTo("ab…"));
    }

    [Test]
    public void Compact_NegativeLimit_IsAnEmptyCut() {
        Assert.That(LogText.Compact("text", -5), Is.EqualTo("…"));
    }

    [Test]
    public void Compact_TextOfExactlyTheLimit_IsKept() {
        var text = new string('x', 300);

        Assert.That(LogText.Compact(text, 300), Is.EqualTo(text));
    }

    [Test]
    public void Arguments_None_AreAnEmptyObject() {
        Assert.That(LogText.Arguments(null), Is.EqualTo("{}"));
    }

    [Test]
    public void Arguments_AreCompactJsonWithoutEscapedText() {
        var arguments = Parse("""
            {
              "name": "M42 \"Orion\" Nebüla",
              "exposure_seconds": 2.5,
              "frames": [1, 2]
            }
            """);

        Assert.That(LogText.Arguments(arguments), Is.EqualTo("""{"name":"M42 \"Orion\" Nebüla","exposure_seconds":2.5,"frames":[1,2]}"""));
    }

    [TestCase("password")]
    [TestCase("WiFiPASS")]
    [TestCase("api_key")]
    [TestCase("APIKey")]
    [TestCase("apikey")]
    [TestCase("SSHPassword")]
    [TestCase("passwd")]
    [TestCase("pwd")]
    [TestCase("passphrase")]
    [TestCase("bearerToken")]
    [TestCase("client-secret")]
    [TestCase("credentials")]
    [TestCase("tokens")]
    [TestCase("passwords")]
    [TestCase("api_key2")]
    [TestCase("password1")]
    public void Arguments_SecretValues_AreRedacted(string key) {
        var arguments = Parse($$$"""{"{{{key}}}":"hunter2","nested":{"{{{key}}}":{"deep":"x"}},"list":[{"{{{key}}}":1}]}""");

        var logged = LogText.Arguments(arguments);

        Assert.That(logged, Does.Not.Contain("hunter2").And.Not.Contain("deep").And.Not.Contain(":1"));
        Assert.That(logged, Does.Contain($"\"{key}\":\"***\""));
    }

    [TestCase("keyframe")]
    [TestCase("passes")]
    [TestCase("device_id")]
    [TestCase("monkey")]
    [TestCase("compass")]
    [TestCase("max_size_arcmin")]
    [TestCase("skip_validation")]
    public void Arguments_KeysThatOnlyContainASecretWord_AreKept(string key) {
        var arguments = Parse($$"""{"{{key}}":"visible"}""");

        Assert.That(LogText.Arguments(arguments), Does.Contain("visible"));
    }

    [Test]
    public void Arguments_DuplicateKeys_AreLoggedAsSent() {
        // JsonDocument accepts them; logging must not keep the tool from running
        var arguments = Parse("""{"ra":1,"ra":2,"token":"a","token":"b"}""");

        Assert.That(LogText.Arguments(arguments), Is.EqualTo("""{"ra":1,"ra":2,"token":"***","token":"***"}"""));
    }

    [Test]
    public void Arguments_ThatCannotBeRead_DoNotThrow() {
        JsonElement disposed;

        using (var document = JsonDocument.Parse("""{"ra":1}""")) {
            disposed = document.RootElement;
        }

        Assert.That(LogText.Arguments(disposed), Does.StartWith("(arguments not readable: "));
    }

    [Test]
    public void Arguments_LongArguments_AreCut() {
        var arguments = Parse($$"""{"value":"{{new string('a', 1000)}}"}""");

        Assert.That(LogText.Arguments(arguments), Has.Length.EqualTo(LogText.MaxArgumentsLength + 1));
    }

    private static JsonElement Parse(string json) {
        using var document = JsonDocument.Parse(json);

        return document.RootElement.Clone();
    }
}
