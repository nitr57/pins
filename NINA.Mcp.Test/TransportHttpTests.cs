// SPDX-License-Identifier: MPL-2.0

using NINA.Mcp.Protocol;
using NINA.Mcp.Test.Fakes;
using NINA.Mcp.Tools;
using NUnit.Framework;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace NINA.Mcp.Test;

[TestFixture]
[NonParallelizable]
public class TransportHttpTests {

    private const string Ping = """{"jsonrpc":"2.0","id":1,"method":"ping"}""";

    private readonly List<ProtocolTestServer> servers = [];

    [TearDown]
    public async Task TearDown() {
        foreach (var server in servers) {
            await server.DisposeAsync();
        }

        servers.Clear();
    }

    // ----- initialize and sessions -----

    [Test]
    public async Task Initialize_CreatesSessionAndReturnsResult() {
        var server = await StartAsync(instructions: "Hello model.");

        using var response = await server.PostAsync(ProtocolTestServer.InitializeBody("2025-06-18", "\"a\""));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(response.Content.Headers.ContentType!.MediaType, Is.EqualTo("application/json"));

        var sessionId = response.Headers.GetValues("Mcp-Session-Id").Single();

        Assert.That(sessionId, Does.Match("^[\\x21-\\x7E]+$"));
        Assert.That(sessionId, Has.Length.GreaterThanOrEqualTo(32));

        var body = await ReadJsonAsync(response);

        Assert.That(body["id"]!.GetValue<string>(), Is.EqualTo("a"));
        Assert.That(body["result"]!["protocolVersion"]!.GetValue<string>(), Is.EqualTo("2025-06-18"));
        Assert.That(body["result"]!["serverInfo"]!["name"]!.GetValue<string>(), Is.EqualTo("pins"));
        Assert.That(body["result"]!["instructions"]!.GetValue<string>(), Is.EqualTo("Hello model."));
    }

    [Test]
    public async Task Initialize_UnsupportedVersion_AnswersLatest() {
        var server = await StartAsync();

        using var response = await server.PostAsync(ProtocolTestServer.InitializeBody("2024-11-05"));
        var body = await ReadJsonAsync(response);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(body["result"]!["protocolVersion"]!.GetValue<string>(), Is.EqualTo(McpProtocolVersions.Latest));
    }

    [Test]
    public async Task Initialize_InvalidParams_NoSession() {
        var server = await StartAsync();

        using var response = await server.PostAsync("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":"x"}""");
        var body = await ReadJsonAsync(response);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(body["error"]!["code"]!.GetValue<int>(), Is.EqualTo(JsonRpcErrorCodes.InvalidParams));
        Assert.That(response.Headers.Contains("Mcp-Session-Id"), Is.False);
    }

    [Test]
    public async Task EachInitialize_CreatesNewSession() {
        var server = await StartAsync();

        var first = await server.InitializeAsync();
        var second = await server.InitializeAsync();

        Assert.That(first, Is.Not.EqualTo(second));
    }

    [Test]
    public async Task Request_WithSession_Succeeds() {
        var server = await StartAsync();
        var sessionId = await server.InitializeAsync();

        using var response = await server.PostAsync(Ping, sessionId);
        var body = await ReadJsonAsync(response);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(body.ToJsonString(), Is.EqualTo("""{"jsonrpc":"2.0","id":1,"result":{}}"""));
    }

    [Test]
    public async Task Request_WithoutSession_400() {
        var server = await StartAsync();

        using var response = await server.PostAsync(Ping);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        await AssertTransportErrorAsync(response, "Mcp-Session-Id");
    }

    [Test]
    public async Task Request_UnknownSession_404() {
        var server = await StartAsync();

        using var response = await server.PostAsync(Ping, "0123456789abcdef0123456789abcdef");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        await AssertTransportErrorAsync(response, "session");
    }

    [Test]
    public async Task Request_ExpiredSession_404() {
        var server = await StartAsync(new McpServerOptions { SessionIdleTimeout = TimeSpan.FromMinutes(5) });
        var sessionId = await server.InitializeAsync();

        server.Time.Advance(TimeSpan.FromMinutes(4));

        using (var stillAlive = await server.PostAsync(Ping, sessionId)) {
            Assert.That(stillAlive.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        }

        server.Time.Advance(TimeSpan.FromMinutes(6));

        using var expired = await server.PostAsync(Ping, sessionId);

        Assert.That(expired.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task Delete_EndsSession() {
        var server = await StartAsync();
        var sessionId = await server.InitializeAsync();

        using var delete = new HttpRequestMessage(HttpMethod.Delete, server.Endpoint);
        delete.Headers.Add("Mcp-Session-Id", sessionId);

        using var deleteResponse = await server.Client.SendAsync(delete);

        Assert.That(deleteResponse.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));

        using var after = await server.PostAsync(Ping, sessionId);

        Assert.That(after.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task Delete_UnknownSession_404() {
        var server = await StartAsync();

        using var delete = new HttpRequestMessage(HttpMethod.Delete, server.Endpoint);
        delete.Headers.Add("Mcp-Session-Id", "nope");

        using var response = await server.Client.SendAsync(delete);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task Delete_WithoutSession_400() {
        var server = await StartAsync();

        using var response = await server.Client.DeleteAsync(server.Endpoint);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task Get_405WithAllowHeader() {
        var server = await StartAsync();
        var sessionId = await server.InitializeAsync();

        using var request = new HttpRequestMessage(HttpMethod.Get, server.Endpoint);
        request.Headers.Accept.ParseAdd("text/event-stream");
        request.Headers.Add("Mcp-Session-Id", sessionId);

        using var response = await server.Client.SendAsync(request);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.MethodNotAllowed));
        Assert.That(response.Content.Headers.Allow, Is.EquivalentTo(new[] { "POST", "DELETE" }));
    }

    // ----- notifications, responses, batches -----

    [Test]
    public async Task Notification_202EmptyBody() {
        var server = await StartAsync();
        var sessionId = await server.InitializeAsync();

        using var response = await server.PostAsync("""{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":3}}""", sessionId);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
        Assert.That(await response.Content.ReadAsStringAsync(), Is.Empty);
    }

    [Test]
    public async Task ClientResponse_202() {
        var server = await StartAsync();
        var sessionId = await server.InitializeAsync();

        using var response = await server.PostAsync("""{"jsonrpc":"2.0","id":"s1","result":{}}""", sessionId);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
    }

    [Test]
    public async Task Batch_ReturnsArrayOfResponsesForRequestsOnly() {
        var server = await StartAsync();
        var sessionId = await server.InitializeAsync();

        using var response = await server.PostAsync(
            """
            [
              {"jsonrpc":"2.0","id":1,"method":"ping"},
              {"jsonrpc":"2.0","method":"notifications/initialized"},
              {"jsonrpc":"2.0","id":"two","method":"tools/list"},
              {"jsonrpc":"2.0","id":3,"method":"nope"},
              {"jsonrpc":"2.0","id":4,"method":"initialize","params":{}}
            ]
            """,
            sessionId);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsArray();

        Assert.That(body, Has.Count.EqualTo(4));
        Assert.That(body[0]!["id"]!.GetValue<int>(), Is.EqualTo(1));
        Assert.That(body[0]!["result"]!.ToJsonString(), Is.EqualTo("{}"));
        Assert.That(body[1]!["id"]!.GetValue<string>(), Is.EqualTo("two"));
        Assert.That(body[1]!["result"]!["tools"]!.AsArray(), Has.Count.EqualTo(1));
        Assert.That(body[2]!["error"]!["code"]!.GetValue<int>(), Is.EqualTo(JsonRpcErrorCodes.MethodNotFound));
        Assert.That(body[3]!["id"]!.GetValue<int>(), Is.EqualTo(4));
        Assert.That(body[3]!["error"]!["code"]!.GetValue<int>(), Is.EqualTo(JsonRpcErrorCodes.InvalidRequest));
    }

    [Test]
    public async Task Batch_OnlyNotifications_202() {
        var server = await StartAsync();
        var sessionId = await server.InitializeAsync();

        using var response = await server.PostAsync(
            """[{"jsonrpc":"2.0","method":"notifications/initialized"},{"jsonrpc":"2.0","id":9,"result":{}}]""",
            sessionId);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
        Assert.That(await response.Content.ReadAsStringAsync(), Is.Empty);
    }

    [Test]
    public async Task Batch_Empty_400InvalidRequest() {
        var server = await StartAsync();
        var sessionId = await server.InitializeAsync();

        using var response = await server.PostAsync("[]", sessionId);
        var body = await ReadJsonAsync(response);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(body["error"]!["code"]!.GetValue<int>(), Is.EqualTo(JsonRpcErrorCodes.InvalidRequest));
    }

    [Test]
    public async Task Batch_WithoutSession_400() {
        var server = await StartAsync();

        using var response = await server.PostAsync($"[{Ping}]");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task Batch_InvalidElement_GetsErrorEntry() {
        var server = await StartAsync();
        var sessionId = await server.InitializeAsync();

        using var response = await server.PostAsync($"[1, {Ping}]", sessionId);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsArray();

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(body, Has.Count.EqualTo(2));
        Assert.That(body[0]!["error"]!["code"]!.GetValue<int>(), Is.EqualTo(JsonRpcErrorCodes.InvalidRequest));
        Assert.That(body[0]!["id"], Is.Null);
        Assert.That(body[1]!["result"], Is.Not.Null);
    }

    // ----- JSON-RPC errors over HTTP -----

    [TestCase("{not json")]
    [TestCase("")]
    [TestCase("{\"jsonrpc\":\"2.0\",")]
    public async Task ParseError_400WithNullId(string body) {
        var server = await StartAsync();
        var sessionId = await server.InitializeAsync();

        using var response = await server.PostAsync(body, sessionId);
        var json = await ReadJsonAsync(response);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(json["error"]!["code"]!.GetValue<int>(), Is.EqualTo(JsonRpcErrorCodes.ParseError));
        Assert.That(json.ContainsKey("id"), Is.True);
        Assert.That(json["id"], Is.Null);
    }

    [Test]
    public async Task InvalidRequestWithoutId_400() {
        var server = await StartAsync();
        var sessionId = await server.InitializeAsync();

        using var response = await server.PostAsync("""{"foo":"bar"}""", sessionId);
        var json = await ReadJsonAsync(response);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(json["error"]!["code"]!.GetValue<int>(), Is.EqualTo(JsonRpcErrorCodes.InvalidRequest));
    }

    [Test]
    public async Task InvalidRequestWithId_200WithError() {
        var server = await StartAsync();
        var sessionId = await server.InitializeAsync();

        using var response = await server.PostAsync("""{"jsonrpc":"1.0","id":5,"method":"ping"}""", sessionId);
        var json = await ReadJsonAsync(response);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(json["id"]!.GetValue<int>(), Is.EqualTo(5));
        Assert.That(json["error"]!["code"]!.GetValue<int>(), Is.EqualTo(JsonRpcErrorCodes.InvalidRequest));
    }

    [Test]
    public async Task MethodNotFound_OverHttp() {
        var server = await StartAsync();
        var sessionId = await server.InitializeAsync();

        using var response = await server.PostAsync("""{"jsonrpc":"2.0","id":5,"method":"resources/list"}""", sessionId);
        var json = await ReadJsonAsync(response);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(json["error"]!["code"]!.GetValue<int>(), Is.EqualTo(JsonRpcErrorCodes.MethodNotFound));
    }

    // ----- tools over HTTP -----

    [Test]
    public async Task ToolsListAndCall_OverHttp() {
        var tool = new ProtocolFakeTool("get_status", handler: (_, _, _) => Task.FromResult(ToolCallResult.Json(new { ok = true })));
        var server = await StartAsync(tools: [tool, new ProtocolFakeTool("slew_mount", ToolAnnotations.Hardware)]);
        var sessionId = await server.InitializeAsync();

        using var list = await server.PostAsync("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""", sessionId);
        var listBody = await ReadJsonAsync(list);

        Assert.That(
            listBody["result"]!["tools"]!.AsArray().Select(item => item!["name"]!.GetValue<string>()),
            Is.EqualTo(new[] { "get_status", "slew_mount" }));

        using var call = await server.PostAsync("""{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"get_status","arguments":{}}}""", sessionId);
        var callBody = await ReadJsonAsync(call);

        var result = callBody["result"]!;

        Assert.That(result["content"]![0]!["type"]!.GetValue<string>(), Is.EqualTo("text"));
        Assert.That(result["content"]![0]!["text"]!.GetValue<string>(), Is.EqualTo("""{"ok":true}"""));
        Assert.That(result["structuredContent"]!.ToJsonString(), Is.EqualTo("""{"ok":true}"""));
        Assert.That(result["isError"]!.GetValue<bool>(), Is.False);
    }

    [Test]
    public async Task Responses_UseCompactEscaping() {
        var tool = new ProtocolFakeTool("get_status", handler: (_, _, _) => Task.FromResult(ToolCallResult.Json(new { temperature = "-10 °C", note = "a<b" })));
        var server = await StartAsync(tools: [tool]);
        var sessionId = await server.InitializeAsync();

        using var call = await server.PostAsync("""{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"get_status"}}""", sessionId);
        var raw = await call.Content.ReadAsStringAsync();

        Assert.That(raw, Does.Contain("°").And.Contain("a<b").And.Contain("\\\"temperature\\\""));
        Assert.That(raw, Does.Not.Contain("\\u0022").And.Not.Contain("\\u00B0"));
    }

    [Test]
    public async Task ReadOnlyMode_OverHttp() {
        var slew = new ProtocolFakeTool("slew_mount", ToolAnnotations.Hardware);
        var server = await StartAsync(
            new McpServerOptions { ReadOnly = true },
            tools: [new ProtocolFakeTool("get_status"), slew]);
        var sessionId = await server.InitializeAsync();

        using var list = await server.PostAsync("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""", sessionId);
        var listBody = await ReadJsonAsync(list);

        Assert.That(listBody["result"]!["tools"]!.AsArray().Select(item => item!["name"]!.GetValue<string>()), Is.EqualTo(new[] { "get_status" }));

        using var call = await server.PostAsync("""{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"slew_mount","arguments":{}}}""", sessionId);
        var callBody = await ReadJsonAsync(call);

        Assert.That(callBody["error"]!["code"]!.GetValue<int>(), Is.EqualTo(JsonRpcErrorCodes.InvalidParams));
        Assert.That(slew.CallCount, Is.Zero);
    }

    [Test]
    public async Task ToolCall_ClientAborts_ToolTokenCancelled() {
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tool = new ProtocolFakeTool("get_status", handler: async (_, _, token) => {
            using var registration = token.Register(() => cancelled.TrySetResult());
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return ToolCallResult.Success("unreachable");
        });
        var server = await StartAsync(tools: [tool]);
        var sessionId = await server.InitializeAsync();

        using var abort = new CancellationTokenSource();
        var call = server.PostAsync("""{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"get_status"}}""", sessionId, abort.Token);

        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await abort.CancelAsync();

        Assert.ThrowsAsync(Is.InstanceOf<OperationCanceledException>(), async () => await call);
        Assert.DoesNotThrowAsync(() => cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10)));

        // The server is still healthy afterwards.
        using var ping = await server.PostAsync(Ping, sessionId);

        Assert.That(ping.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    // ----- protocol version header -----

    [TestCase("2025-11-25", HttpStatusCode.OK)]
    [TestCase("2025-06-18", HttpStatusCode.OK)]
    [TestCase("2025-03-26", HttpStatusCode.OK)]
    [TestCase("2024-11-05", HttpStatusCode.BadRequest)]
    [TestCase("garbage", HttpStatusCode.BadRequest)]
    [TestCase("", HttpStatusCode.BadRequest)]
    public async Task ProtocolVersionHeader(string version, HttpStatusCode expected) {
        var server = await StartAsync();
        var sessionId = await server.InitializeAsync();

        using var request = server.CreatePost(Ping);
        request.Headers.Add("Mcp-Session-Id", sessionId);
        request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", version);

        using var response = await server.Client.SendAsync(request);

        Assert.That(response.StatusCode, Is.EqualTo(expected));
    }

    [Test]
    public async Task ProtocolVersionHeader_Absent_Ok() {
        var server = await StartAsync();
        var sessionId = await server.InitializeAsync();

        using var request = server.CreatePost(Ping);
        request.Headers.Add("Mcp-Session-Id", sessionId);

        using var response = await server.Client.SendAsync(request);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task ProtocolVersionHeader_UnsupportedOnInitialize_400() {
        var server = await StartAsync();

        using var request = server.CreatePost(ProtocolTestServer.InitializeBody("2025-11-25"));
        request.Headers.Add("MCP-Protocol-Version", "1999-01-01");

        using var response = await server.Client.SendAsync(request);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(response.Headers.Contains("Mcp-Session-Id"), Is.False);
    }

    [Test]
    public async Task ProtocolVersionHeader_UnsupportedOnDelete_400() {
        var server = await StartAsync();
        var sessionId = await server.InitializeAsync();

        using var delete = new HttpRequestMessage(HttpMethod.Delete, server.Endpoint);
        delete.Headers.Add("Mcp-Session-Id", sessionId);
        delete.Headers.Add("MCP-Protocol-Version", "1999-01-01");

        using var response = await server.Client.SendAsync(delete);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    // ----- content type and size -----

    [TestCase("application/json")]
    [TestCase("application/json; charset=utf-8")]
    [TestCase("Application/JSON")]
    public async Task ContentType_JsonAccepted(string contentType) {
        var server = await StartAsync();

        using var request = server.CreatePost(ProtocolTestServer.InitializeBody("2025-11-25"), contentType: contentType);
        using var response = await server.Client.SendAsync(request);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [TestCase("text/plain")]
    [TestCase("application/x-www-form-urlencoded")]
    [TestCase("multipart/form-data; boundary=x")]
    [TestCase("text/event-stream")]
    public async Task ContentType_NotJson_415(string contentType) {
        var server = await StartAsync();

        using var request = server.CreatePost(ProtocolTestServer.InitializeBody("2025-11-25"), contentType: contentType);
        using var response = await server.Client.SendAsync(request);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.UnsupportedMediaType));
    }

    [Test]
    public async Task ContentType_Missing_415() {
        var server = await StartAsync();

        using var request = new HttpRequestMessage(HttpMethod.Post, server.Endpoint) {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(ProtocolTestServer.InitializeBody("2025-11-25")))
        };

        using var response = await server.Client.SendAsync(request);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.UnsupportedMediaType));
    }

    [Test]
    public async Task BodyTooLarge_ContentLength_413() {
        var server = await StartAsync(new McpServerOptions { MaxRequestBodyBytes = 1024 });

        var padding = new string(' ', 2048);

        using var response = await server.PostAsync(ProtocolTestServer.InitializeBody("2025-11-25") + padding);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.RequestEntityTooLarge));
        Assert.That(response.Headers.Contains("Mcp-Session-Id"), Is.False);
    }

    [Test]
    public async Task BodyTooLarge_Chunked_413() {
        var server = await StartAsync(new McpServerOptions { MaxRequestBodyBytes = 1024 });

        var bytes = Encoding.UTF8.GetBytes(ProtocolTestServer.InitializeBody("2025-11-25") + new string(' ', 4096));

        using var request = new HttpRequestMessage(HttpMethod.Post, server.Endpoint) {
            Content = new StreamContent(new NonSeekableStream(bytes))
        };
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/json");
        request.Headers.TransferEncodingChunked = true;

        using var response = await server.Client.SendAsync(request);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.RequestEntityTooLarge));
    }

    [Test]
    public async Task BodyAtLimit_Accepted() {
        var initialize = ProtocolTestServer.InitializeBody("2025-11-25");
        var server = await StartAsync(new McpServerOptions { MaxRequestBodyBytes = initialize.Length + 10 });

        using var response = await server.PostAsync(initialize + new string(' ', 10));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    // ----- origin -----

    [TestCase("http://localhost:4782")]
    [TestCase("http://127.0.0.1:4782")]
    [TestCase("http://192.168.1.20:4782")]
    [TestCase("http://100.100.1.2:4782")]
    [TestCase("http://[::1]:4782")]
    [TestCase("http://pins.local:4782")]
    [TestCase("https://chat.example.org")]
    public async Task Origin_Allowed(string origin) {
        var server = await StartAsync(new McpServerOptions { AllowedOrigins = ["https://chat.example.org"] });

        using var request = server.CreatePost(ProtocolTestServer.InitializeBody("2025-11-25"));
        request.Headers.Add("Origin", origin);

        using var response = await server.Client.SendAsync(request);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(response.Headers.GetValues("Access-Control-Expose-Headers").Single(), Is.EqualTo("Mcp-Session-Id"));
    }

    [TestCase("http://evil.com")]
    [TestCase("http://127.0.0.1.nip.io:4782")]
    [TestCase("http://localhost.evil.com")]
    [TestCase("https://chat.example.org:8443")]
    [TestCase("http://8.8.8.8")]
    [TestCase("null")]
    public async Task Origin_Rejected_403OnEveryVerb(string origin) {
        var server = await StartAsync(new McpServerOptions { AllowedOrigins = ["https://chat.example.org"] });
        var sessionId = await server.InitializeAsync();

        using var post = server.CreatePost(Ping, sessionId);
        post.Headers.Add("Origin", origin);
        using var get = new HttpRequestMessage(HttpMethod.Get, server.Endpoint);
        get.Headers.Add("Origin", origin);
        using var delete = new HttpRequestMessage(HttpMethod.Delete, server.Endpoint);
        delete.Headers.Add("Origin", origin);
        delete.Headers.Add("Mcp-Session-Id", sessionId);

        using var postResponse = await server.Client.SendAsync(post);
        using var getResponse = await server.Client.SendAsync(get);
        using var deleteResponse = await server.Client.SendAsync(delete);

        Assert.That(postResponse.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        Assert.That(getResponse.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        Assert.That(deleteResponse.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));

        // The rejected DELETE did not end the session.
        using var ping = await server.PostAsync(Ping, sessionId);

        Assert.That(ping.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task Origin_CheckedBeforeEverythingElse() {
        var server = await StartAsync(new McpServerOptions { BearerToken = "t0ken" });

        using var request = server.CreatePost("{not json", contentType: "text/plain");
        request.Headers.Add("Origin", "http://evil.com");

        using var response = await server.Client.SendAsync(request);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
    }

    // ----- bearer token -----

    [Test]
    public async Task Token_Missing_401WithChallenge() {
        var server = await StartAsync(new McpServerOptions { BearerToken = "t0ken" });

        using var response = await server.PostAsync(ProtocolTestServer.InitializeBody("2025-11-25"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That(response.Headers.WwwAuthenticate.Single().Scheme, Is.EqualTo("Bearer"));
        Assert.That(response.Headers.Contains("Mcp-Session-Id"), Is.False);
    }

    [Test]
    public async Task Token_Wrong_401() {
        var server = await StartAsync(new McpServerOptions { BearerToken = "t0ken" });

        using var request = server.CreatePost(ProtocolTestServer.InitializeBody("2025-11-25"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "wrong");

        using var response = await server.Client.SendAsync(request);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task Token_Right_Ok_AndRequiredOnEveryRequest() {
        var server = await StartAsync(new McpServerOptions { BearerToken = "t0ken" });
        var sessionId = await server.InitializeAsync("t0ken");

        using var withToken = server.CreatePost(Ping, sessionId);
        withToken.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "t0ken");

        using var withTokenResponse = await server.Client.SendAsync(withToken);
        using var withoutTokenResponse = await server.PostAsync(Ping, sessionId);
        using var getWithoutToken = await server.Client.GetAsync(server.Endpoint);

        Assert.That(withTokenResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(withoutTokenResponse.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That(getWithoutToken.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    // ----- mapping -----

    [Test]
    public async Task CustomPath_IsUsed() {
        var server = await StartAsync(new McpServerOptions { Path = "/api/mcp" });

        using var response = await server.PostAsync(ProtocolTestServer.InitializeBody("2025-11-25"));

        Assert.That(server.Endpoint.AbsolutePath, Is.EqualTo("/api/mcp"));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task Disabled_MapsNothing() {
        var server = await StartAsync(new McpServerOptions { Enabled = false });

        using var response = await server.PostAsync(ProtocolTestServer.InitializeBody("2025-11-25"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task WithoutAddPinsMcp_MapPinsMcpIsNoOp() {
        var server = await StartAsync(addMcp: false);

        using var response = await server.PostAsync(ProtocolTestServer.InitializeBody("2025-11-25"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    private async Task<ProtocolTestServer> StartAsync(
        McpServerOptions? options = null,
        IEnumerable<McpTool>? tools = null,
        string instructions = "Test instructions.",
        bool addMcp = true) {
        var server = await ProtocolTestServer.StartAsync(options, tools, instructions, addMcp);
        servers.Add(server);

        return server;
    }

    private static async Task<JsonObject> ReadJsonAsync(HttpResponseMessage response) {
        var text = await response.Content.ReadAsStringAsync();

        return JsonNode.Parse(text)!.AsObject();
    }

    private static async Task AssertTransportErrorAsync(HttpResponseMessage response, string messagePart) {
        var body = await ReadJsonAsync(response);

        Assert.That(body["jsonrpc"]!.GetValue<string>(), Is.EqualTo("2.0"));
        Assert.That(body["error"]!["message"]!.GetValue<string>(), Does.Contain(messagePart).IgnoreCase);
    }

    /// <summary>Forces HttpClient to send a chunked body without Content-Length.</summary>
    private sealed class NonSeekableStream : MemoryStream {

        public NonSeekableStream(byte[] buffer) : base(buffer) {
        }

        public override bool CanSeek => false;
    }
}
