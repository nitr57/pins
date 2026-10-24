// SPDX-License-Identifier: MPL-2.0

using Microsoft.Extensions.Logging;
using NINA.Mcp.Jobs;
using NINA.Mcp.Protocol;
using NINA.Mcp.Test.Fakes;
using NINA.Mcp.Tools;
using NUnit.Framework;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NINA.Mcp.Test;

[TestFixture]
public class ProtocolDispatcherTests {

    private ProtocolCapturingLogger<McpDispatcher> logger = null!;
    private JobManager jobs = null!;

    [SetUp]
    public void SetUp() {
        logger = new ProtocolCapturingLogger<McpDispatcher>();
        jobs = new JobManager();
    }

    // ----- initialize -----

    [TestCase("2025-11-25", "2025-11-25")]
    [TestCase("2025-06-18", "2025-06-18")]
    [TestCase("2025-03-26", "2025-03-26")]
    [TestCase("2024-11-05", "2025-11-25")]
    [TestCase("2099-01-01", "2025-11-25")]
    [TestCase("", "2025-11-25")]
    public async Task Initialize_NegotiatesVersion(string requested, string expected) {
        var dispatcher = CreateDispatcher();

        var json = """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"VERSION","capabilities":{},"clientInfo":{"name":"test","version":"1"}}}"""
            .Replace("VERSION", requested);

        var result = await DispatchAsync(dispatcher, json);

        Assert.That(result.Response!["result"]!["protocolVersion"]!.GetValue<string>(), Is.EqualTo(expected));
        Assert.That(result.InitializedProtocolVersion, Is.EqualTo(expected));
    }

    [Test]
    public async Task Initialize_MissingParamsOrVersion_AnswersLatest() {
        var dispatcher = CreateDispatcher();

        var withoutParams = await DispatchAsync(dispatcher, """{"jsonrpc":"2.0","id":1,"method":"initialize"}""");
        var numericVersion = await DispatchAsync(dispatcher, """{"jsonrpc":"2.0","id":2,"method":"initialize","params":{"protocolVersion":5}}""");

        Assert.That(withoutParams.Response!["result"]!["protocolVersion"]!.GetValue<string>(), Is.EqualTo(McpProtocolVersions.Latest));
        Assert.That(numericVersion.Response!["result"]!["protocolVersion"]!.GetValue<string>(), Is.EqualTo(McpProtocolVersions.Latest));
    }

    [Test]
    public async Task Initialize_ResultShape() {
        var dispatcher = CreateDispatcher(
            options: new McpServerOptions { ServerVersion = "3.2.1" },
            instructions: "Use get_status first.");

        var result = await DispatchAsync(dispatcher, """{"jsonrpc":"2.0","id":"init-1","method":"initialize","params":{"protocolVersion":"2025-06-18"}}""");
        var response = result.Response!;

        Assert.That(response["jsonrpc"]!.GetValue<string>(), Is.EqualTo("2.0"));
        Assert.That(response["id"]!.GetValue<string>(), Is.EqualTo("init-1"));

        var body = response["result"]!;

        Assert.That(body["capabilities"]!.ToJsonString(), Is.EqualTo("""{"tools":{"listChanged":false}}"""));
        Assert.That(body["serverInfo"]!["name"]!.GetValue<string>(), Is.EqualTo("pins"));
        Assert.That(body["serverInfo"]!["title"]!.GetValue<string>(), Is.EqualTo("PI 'N' Stars"));
        Assert.That(body["serverInfo"]!["version"]!.GetValue<string>(), Is.EqualTo("3.2.1"));
        Assert.That(body["instructions"]!.GetValue<string>(), Is.EqualTo("Use get_status first."));
    }

    [Test]
    public async Task Initialize_EmptyInstructions_AreOmitted() {
        var dispatcher = CreateDispatcher(instructions: "");

        var result = await DispatchAsync(dispatcher, """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}""");

        Assert.That(result.Response!["result"]!.AsObject().ContainsKey("instructions"), Is.False);
    }

    [Test]
    public async Task Initialize_NonObjectParams_InvalidParams() {
        var dispatcher = CreateDispatcher();

        var result = await DispatchAsync(dispatcher, """{"jsonrpc":"2.0","id":1,"method":"initialize","params":[1]}""");

        AssertError(result, JsonRpcErrorCodes.InvalidParams);
        Assert.That(result.InitializedProtocolVersion, Is.Null);
    }

    // ----- lifecycle / ping / notifications -----

    [Test]
    public async Task Ping_ReturnsEmptyObject() {
        var dispatcher = CreateDispatcher();

        var result = await DispatchAsync(dispatcher, """{"jsonrpc":"2.0","id":7,"method":"ping"}""");

        Assert.That(result.Response!["result"]!.ToJsonString(), Is.EqualTo("{}"));
    }

    [TestCase("notifications/initialized")]
    [TestCase("notifications/cancelled")]
    [TestCase("notifications/whatever")]
    [TestCase("tools/list")]
    public async Task Notifications_ProduceNoResponse(string method) {
        var dispatcher = CreateDispatcher();

        var result = await DispatchAsync(dispatcher, $$$"""{"jsonrpc":"2.0","method":"{{{method}}}","params":{}}""");

        Assert.That(result.Response, Is.Null);
    }

    [Test]
    public async Task ClientResponses_ProduceNoResponse() {
        var dispatcher = CreateDispatcher();

        var success = await DispatchAsync(dispatcher, """{"jsonrpc":"2.0","id":1,"result":{}}""");
        var error = await DispatchAsync(dispatcher, """{"jsonrpc":"2.0","id":2,"error":{"code":-1,"message":"x"}}""");

        Assert.That(success.Response, Is.Null);
        Assert.That(error.Response, Is.Null);
    }

    // ----- ids -----

    [TestCase("1", "1")]
    [TestCase("-5", "-5")]
    [TestCase("1.5", "1.5")]
    [TestCase("12345678901234567890", "12345678901234567890")]
    [TestCase("\"abc\"", "\"abc\"")]
    [TestCase("\"\"", "\"\"")]
    public async Task Ids_AreEchoedExactly(string id, string expected) {
        var dispatcher = CreateDispatcher();

        var result = await DispatchAsync(dispatcher, $$"""{"jsonrpc":"2.0","id":{{id}},"method":"ping"}""");

        Assert.That(result.Response!["id"]!.ToJsonString(), Is.EqualTo(expected));
    }

    // ----- errors -----

    [TestCase("""[]""")]
    [TestCase("""42""")]
    [TestCase("""{"id":1,"method":"ping"}""")]
    [TestCase("""{"jsonrpc":"1.0","id":1,"method":"ping"}""")]
    [TestCase("""{"jsonrpc":2,"id":1,"method":"ping"}""")]
    [TestCase("""{"jsonrpc":"2.0","id":1}""")]
    [TestCase("""{"jsonrpc":"2.0","id":1,"method":5}""")]
    [TestCase("""{"jsonrpc":"2.0","id":{"a":1},"method":"ping"}""")]
    [TestCase("""{"jsonrpc":"2.0","id":[1],"method":"ping"}""")]
    [TestCase("""{"jsonrpc":"2.0","id":true,"method":"ping"}""")]
    [TestCase("""{"jsonrpc":"2.0","id":null,"method":"ping"}""")]
    public async Task InvalidMessages_InvalidRequest(string json) {
        var dispatcher = CreateDispatcher();

        var result = await DispatchAsync(dispatcher, json);

        AssertError(result, JsonRpcErrorCodes.InvalidRequest);
    }

    [Test]
    public async Task InvalidRequest_WithValidId_EchoesId() {
        var dispatcher = CreateDispatcher();

        var result = await DispatchAsync(dispatcher, """{"jsonrpc":"1.0","id":"x","method":"ping"}""");

        Assert.That(result.Response!["id"]!.GetValue<string>(), Is.EqualTo("x"));
    }

    [Test]
    public async Task InvalidRequest_WithInvalidId_HasNullId() {
        var dispatcher = CreateDispatcher();

        var result = await DispatchAsync(dispatcher, """{"jsonrpc":"2.0","id":{"a":1},"method":"ping"}""");

        Assert.That(result.Response!.ContainsKey("id"), Is.True);
        Assert.That(result.Response!["id"], Is.Null);
    }

    [TestCase("resources/list")]
    [TestCase("prompts/list")]
    [TestCase("completion/complete")]
    [TestCase("")]
    public async Task UnknownMethod_MethodNotFound(string method) {
        var dispatcher = CreateDispatcher();

        var result = await DispatchAsync(dispatcher, $$"""{"jsonrpc":"2.0","id":3,"method":"{{method}}"}""");

        AssertError(result, JsonRpcErrorCodes.MethodNotFound);
        Assert.That(result.Response!["id"]!.GetValue<int>(), Is.EqualTo(3));
    }

    [Test]
    public async Task UnexpectedFailureInMethod_InternalErrorAndLogged() {
        var broken = new ProtocolFakeTool("broken") {
            DescriptionOverride = () => throw new InvalidOperationException("boom")
        };
        var dispatcher = CreateDispatcher(tools: [broken]);

        var result = await DispatchAsync(dispatcher, """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""");

        AssertError(result, JsonRpcErrorCodes.InternalError);
        // The exception text stays in the log: the response also reaches unauthenticated clients.
        Assert.That(result.Response!["error"]!["message"]!.GetValue<string>(), Does.Not.Contain("boom").And.Contain("pins log"));
        Assert.That(logger.Entries.Any(entry => entry.Level == LogLevel.Error && entry.Exception is InvalidOperationException), Is.True);
    }

    // ----- tools/list -----

    [Test]
    public async Task ToolsList_Shape() {
        var tool = new ProtocolFakeTool("move_focuser", ToolAnnotations.Hardware);
        var dispatcher = CreateDispatcher(tools: [tool]);

        var result = await DispatchAsync(dispatcher, """{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}""");
        var body = result.Response!["result"]!.AsObject();

        Assert.That(body.ContainsKey("nextCursor"), Is.False);

        var item = body["tools"]!.AsArray().Single()!.AsObject();

        Assert.That(item.Select(pair => pair.Key), Is.EquivalentTo(new[] { "name", "title", "description", "inputSchema", "annotations" }));
        Assert.That(item["name"]!.GetValue<string>(), Is.EqualTo("move_focuser"));
        Assert.That(item["title"]!.GetValue<string>(), Is.EqualTo("Title of move_focuser"));
        Assert.That(item["description"]!.GetValue<string>(), Is.EqualTo("Description of move_focuser."));
        Assert.That(item["inputSchema"]!.ToJsonString(), Is.EqualTo(tool.InputSchema.ToJsonString()));
        Assert.That(
            item["annotations"]!.ToJsonString(),
            Is.EqualTo("""{"title":"Title of move_focuser","readOnlyHint":false,"destructiveHint":true,"idempotentHint":false,"openWorldHint":false}"""));
    }

    [Test]
    public async Task ToolsList_KeepsRegistrationOrder() {
        var dispatcher = CreateDispatcher(tools: [new ProtocolFakeTool("c"), new ProtocolFakeTool("a"), new ProtocolFakeTool("b")]);

        var result = await DispatchAsync(dispatcher, """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""");

        Assert.That(ToolNames(result), Is.EqualTo(new[] { "c", "a", "b" }));
    }

    [Test]
    public async Task ToolsList_DoesNotExposeSchemaInstance() {
        var tool = new ProtocolFakeTool("get_status");
        var dispatcher = CreateDispatcher(tools: [tool]);

        var first = await DispatchAsync(dispatcher, """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""");
        first.Response!["result"]!["tools"]![0]!["inputSchema"]!.AsObject().Add("mutated", true);

        Assert.That(tool.InputSchema.ContainsKey("mutated"), Is.False);
    }

    [Test]
    public async Task ToolsList_ReadOnlyMode_ListsOnlyAllowedTools() {
        var dispatcher = CreateDispatcher(
            options: new McpServerOptions { ReadOnly = true },
            tools: [
                new ProtocolFakeTool("get_status", ToolAnnotations.Query),
                new ProtocolFakeTool("slew_mount", ToolAnnotations.Hardware),
                new ProtocolFakeTool("load_sequence", ToolAnnotations.Change),
                new ProtocolFakeTool("stop_sequence", ToolAnnotations.Stop),
                new ProtocolFakeTool("stop_all", ToolAnnotations.Stop, allowedInReadOnlyMode: true)
            ]);

        var result = await DispatchAsync(dispatcher, """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""");

        Assert.That(ToolNames(result), Is.EqualTo(new[] { "get_status", "stop_all" }));
    }

    [Test]
    public async Task ToolsList_NormalMode_ListsEverything() {
        var dispatcher = CreateDispatcher(tools: [
            new ProtocolFakeTool("get_status", ToolAnnotations.Query),
            new ProtocolFakeTool("slew_mount", ToolAnnotations.Hardware)
        ]);

        var result = await DispatchAsync(dispatcher, """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""");

        Assert.That(ToolNames(result), Is.EqualTo(new[] { "get_status", "slew_mount" }));
    }

    [Test]
    public void DuplicateToolNames_Rejected() {
        Assert.Throws<InvalidOperationException>(() => CreateDispatcher(tools: [new ProtocolFakeTool("a"), new ProtocolFakeTool("a")]));
    }

    // ----- tools/call -----

    [Test]
    public async Task ToolsCall_Success_TextResult() {
        var tool = new ProtocolFakeTool("get_status", handler: (_, _, _) => Task.FromResult(ToolCallResult.Success("all good")));
        var dispatcher = CreateDispatcher(tools: [tool]);

        var result = await DispatchAsync(dispatcher, """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"get_status"}}""");

        Assert.That(
            result.Response!["result"]!.ToJsonString(),
            Is.EqualTo("""{"content":[{"type":"text","text":"all good"}],"isError":false}"""));
        Assert.That(tool.CallCount, Is.EqualTo(1));
    }

    [Test]
    public async Task ToolsCall_JsonResult_HasStructuredContent() {
        var tool = new ProtocolFakeTool("get_status", handler: (_, _, _) => Task.FromResult(ToolCallResult.Json(new { cameraTemperature = -10.5, connected = true })));
        var dispatcher = CreateDispatcher(tools: [tool]);

        var result = await DispatchAsync(dispatcher, """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"get_status","arguments":{}}}""");
        var body = result.Response!["result"]!;

        Assert.That(body["structuredContent"]!.ToJsonString(), Is.EqualTo("""{"cameraTemperature":-10.5,"connected":true}"""));
        Assert.That(body["content"]![0]!["text"]!.GetValue<string>(), Is.EqualTo("""{"cameraTemperature":-10.5,"connected":true}"""));
        Assert.That(body["isError"]!.GetValue<bool>(), Is.False);
    }

    [Test]
    public async Task ToolsCall_PassesArgumentsAndContext() {
        var tool = new ProtocolFakeTool("echo", handler: (arguments, _, _) => Task.FromResult(ToolCallResult.Success(arguments.GetString("value", required: true)!)));
        var context = new ToolContext(jobs, new McpServerOptions());
        var dispatcher = CreateDispatcher(tools: [tool], context: context);

        var result = await DispatchAsync(dispatcher, """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"echo","arguments":{"value":"hello"}}}""");

        Assert.That(result.Response!["result"]!["content"]![0]!["text"]!.GetValue<string>(), Is.EqualTo("hello"));
        Assert.That(tool.LastContext, Is.SameAs(context));
    }

    [Test]
    public async Task ToolsCall_ErrorResult_IsError() {
        var tool = new ProtocolFakeTool("slew_mount", ToolAnnotations.Hardware, (_, _, _) => Task.FromResult(ToolCallResult.Error("Mount not connected.")));
        var dispatcher = CreateDispatcher(tools: [tool]);

        var result = await DispatchAsync(dispatcher, """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"slew_mount","arguments":{}}}""");

        Assert.That(
            result.Response!["result"]!.ToJsonString(),
            Is.EqualTo("""{"content":[{"type":"text","text":"Mount not connected."}],"isError":true}"""));
    }

    [Test]
    public async Task ToolsCall_ToolArgumentException_IsErrorWithMessage() {
        var tool = new ProtocolFakeTool("echo", handler: (arguments, _, _) => {
            arguments.GetDouble("missing", required: true);
            return Task.FromResult(ToolCallResult.Success("unreachable"));
        });
        var dispatcher = CreateDispatcher(tools: [tool]);

        var result = await DispatchAsync(dispatcher, """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"echo","arguments":{}}}""");

        AssertToolError(result, "Argument 'missing' is required.");
        Assert.That(logger.Entries, Is.Empty);
    }

    [Test]
    public async Task ToolsCall_JobConflictException_IsErrorWithMessage() {
        var gate = new TaskCompletionSource<JobOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = jobs.Start("slew", "Slew to M31", "mount", _ => gate.Task);
        var tool = new ProtocolFakeTool("park_mount", ToolAnnotations.Hardware, (_, context, _) => {
            context.Jobs.Start("park", "Park", "mount", _ => Task.FromResult(JobOutcome.Ok()));
            return Task.FromResult(ToolCallResult.Success("unreachable"));
        });
        var dispatcher = CreateDispatcher(tools: [tool]);

        var result = await DispatchAsync(dispatcher, """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"park_mount"}}""");

        var body = result.Response!["result"]!;

        Assert.That(body["isError"]!.GetValue<bool>(), Is.True);
        Assert.That(body["content"]![0]!["text"]!.GetValue<string>(), Does.Contain(running.Id).And.Contain("Slew to M31"));
        Assert.That(logger.Entries, Is.Empty);

        gate.SetResult(JobOutcome.Ok());
    }

    [Test]
    public async Task ToolsCall_UnexpectedException_InternalErrorResultAndLogged() {
        var tool = new ProtocolFakeTool("get_status", handler: (_, _, _) => throw new NullReferenceException("oops"));
        var dispatcher = CreateDispatcher(tools: [tool]);

        var result = await DispatchAsync(dispatcher, """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"get_status"}}""");

        AssertToolError(result, "Internal error in get_status: oops");
        Assert.That(logger.Entries.Single().Level, Is.EqualTo(LogLevel.Error));
        Assert.That(logger.Entries.Single().Exception, Is.InstanceOf<NullReferenceException>());
    }

    [Test]
    public async Task ToolsCall_AsyncUnexpectedException_InternalErrorResult() {
        var tool = new ProtocolFakeTool("get_status", handler: async (_, _, _) => {
            await Task.Yield();
            throw new InvalidOperationException("late");
        });
        var dispatcher = CreateDispatcher(tools: [tool]);

        var result = await DispatchAsync(dispatcher, """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"get_status"}}""");

        AssertToolError(result, "Internal error in get_status: late");
    }

    [Test]
    public async Task ToolsCall_OperationCanceledNotFromRequest_IsInternalErrorResult() {
        var tool = new ProtocolFakeTool("get_status", handler: (_, _, _) => throw new TaskCanceledException("driver timeout"));
        var dispatcher = CreateDispatcher(tools: [tool]);

        var result = await DispatchAsync(dispatcher, """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"get_status"}}""");

        AssertToolError(result, "Internal error in get_status: driver timeout");
    }

    [Test]
    public void ToolsCall_RequestAborted_Propagates() {
        using var aborted = new CancellationTokenSource();
        var tool = new ProtocolFakeTool("get_status", handler: async (_, _, token) => {
            await aborted.CancelAsync();
            token.ThrowIfCancellationRequested();
            return ToolCallResult.Success("unreachable");
        });
        var dispatcher = CreateDispatcher(tools: [tool]);

        using var document = JsonDocument.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"get_status"}}""");

        Assert.ThrowsAsync(Is.InstanceOf<OperationCanceledException>(), () => dispatcher.DispatchAsync(document.RootElement, aborted.Token));
        Assert.That(logger.Entries, Is.Empty);
    }

    [TestCase("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"nope"}}""")]
    [TestCase("""{"jsonrpc":"2.0","id":1,"method":"tools/call"}""")]
    [TestCase("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":[]}""")]
    [TestCase("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{}}""")]
    [TestCase("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":3}}""")]
    [TestCase("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"get_status","arguments":[1]}}""")]
    [TestCase("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"get_status","arguments":"x"}}""")]
    public async Task ToolsCall_BadParams_InvalidParams(string json) {
        var dispatcher = CreateDispatcher(tools: [new ProtocolFakeTool("get_status")]);

        var result = await DispatchAsync(dispatcher, json);

        AssertError(result, JsonRpcErrorCodes.InvalidParams);
    }

    [Test]
    public async Task ToolsCall_NullArguments_TreatedAsEmpty() {
        var tool = new ProtocolFakeTool("get_status");
        var dispatcher = CreateDispatcher(tools: [tool]);

        var result = await DispatchAsync(dispatcher, """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"get_status","arguments":null}}""");

        Assert.That(result.Response!["result"]!["isError"]!.GetValue<bool>(), Is.False);
        Assert.That(tool.LastArguments!.Has("value"), Is.False);
    }

    [Test]
    public async Task ToolsCall_ReadOnlyMode_HiddenToolIsUnknown() {
        var slew = new ProtocolFakeTool("slew_mount", ToolAnnotations.Hardware);
        var stopAll = new ProtocolFakeTool("stop_all", ToolAnnotations.Stop, allowedInReadOnlyMode: true);
        var dispatcher = CreateDispatcher(options: new McpServerOptions { ReadOnly = true }, tools: [slew, stopAll]);

        var hidden = await DispatchAsync(dispatcher, """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"slew_mount","arguments":{}}}""");
        var allowed = await DispatchAsync(dispatcher, """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"stop_all","arguments":{}}}""");

        AssertError(hidden, JsonRpcErrorCodes.InvalidParams);
        Assert.That(hidden.Response!["error"]!["message"]!.GetValue<string>(), Does.Contain("read-only"));
        Assert.That(slew.CallCount, Is.Zero);
        Assert.That(allowed.Response!["result"]!["isError"]!.GetValue<bool>(), Is.False);
        Assert.That(stopAll.CallCount, Is.EqualTo(1));
    }

    [Test]
    public void SerializeResult_OmitsStructuredContentWhenAbsent() {
        var serialized = McpDispatcher.SerializeResult(ToolCallResult.Success("x"));

        Assert.That(serialized.ContainsKey("structuredContent"), Is.False);
        Assert.That(serialized["isError"]!.GetValue<bool>(), Is.False);
    }

    private McpDispatcher CreateDispatcher(
        McpServerOptions? options = null,
        IEnumerable<McpTool>? tools = null,
        string instructions = "instructions",
        ToolContext? context = null) {
        options ??= new McpServerOptions();

        return new McpDispatcher(
            tools ?? [],
            new McpServerInstructions(instructions),
            options,
            context ?? new ToolContext(jobs, options),
            logger);
    }

    private static async Task<McpDispatchResult> DispatchAsync(McpDispatcher dispatcher, string json) {
        using var document = JsonDocument.Parse(json);

        return await dispatcher.DispatchAsync(document.RootElement, CancellationToken.None);
    }

    private static void AssertError(McpDispatchResult result, int code) {
        Assert.That(result.Response, Is.Not.Null);
        Assert.That(result.Response!["jsonrpc"]!.GetValue<string>(), Is.EqualTo("2.0"));
        Assert.That(result.Response.ContainsKey("result"), Is.False);
        Assert.That(result.Response["error"]!["code"]!.GetValue<int>(), Is.EqualTo(code));
        Assert.That(result.Response["error"]!["message"]!.GetValue<string>(), Is.Not.Empty);
    }

    private static void AssertToolError(McpDispatchResult result, string message) {
        var body = result.Response!["result"]!;

        Assert.That(body["isError"]!.GetValue<bool>(), Is.True);
        Assert.That(body["content"]![0]!["type"]!.GetValue<string>(), Is.EqualTo("text"));
        Assert.That(body["content"]![0]!["text"]!.GetValue<string>(), Is.EqualTo(message));
    }

    private static IEnumerable<string> ToolNames(McpDispatchResult result) {
        return result.Response!["result"]!["tools"]!.AsArray().Select(tool => tool!["name"]!.GetValue<string>()).ToList();
    }
}
