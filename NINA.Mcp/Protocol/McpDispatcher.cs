// SPDX-License-Identifier: MPL-2.0

using Microsoft.Extensions.Logging;
using NINA.Mcp.Jobs;
using NINA.Mcp.Tools;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NINA.Mcp.Protocol;

/// <summary>Result of dispatching one JSON-RPC message.</summary>
/// <param name="Response">The response to send, or null for notifications and client responses.</param>
/// <param name="InitializedProtocolVersion">Set when the message was a successful <c>initialize</c> request.</param>
public sealed record McpDispatchResult(JsonObject? Response, string? InitializedProtocolVersion = null) {

    public static McpDispatchResult None { get; } = new(Response: null);
}

/// <summary>
/// Transport independent MCP server: validates JSON-RPC 2.0 messages and handles the lifecycle (<c>initialize</c>,
/// notifications, <c>ping</c>) and the tool methods (<c>tools/list</c>, <c>tools/call</c>). Stateless; sessions are the
/// transport's business.
/// </summary>
public sealed class McpDispatcher {

    public const string ServerName = "pins";

    public const string ServerTitle = "PI 'N' Stars";

    private readonly IReadOnlyList<McpTool> tools;
    private readonly Dictionary<string, McpTool> toolsByName;
    private readonly McpServerInstructions instructions;
    private readonly McpServerOptions options;
    private readonly ToolContext toolContext;
    private readonly ILogger<McpDispatcher> logger;

    public McpDispatcher(
        IEnumerable<McpTool> tools,
        McpServerInstructions instructions,
        McpServerOptions options,
        ToolContext toolContext,
        ILogger<McpDispatcher> logger) {
        this.instructions = instructions;
        this.options = options;
        this.toolContext = toolContext;
        this.logger = logger;

        var visible = options.ReadOnly
            ? tools.Where(tool => tool.AllowedInReadOnlyMode)
            : tools;

        this.tools = visible.ToList();
        toolsByName = new Dictionary<string, McpTool>(StringComparer.Ordinal);

        foreach (var tool in this.tools) {
            if (!toolsByName.TryAdd(tool.Name, tool)) {
                throw new InvalidOperationException($"Duplicate MCP tool name '{tool.Name}'.");
            }
        }
    }

    /// <summary>The tools clients can see and call (read-only mode applied).</summary>
    public IReadOnlyList<McpTool> VisibleTools => tools;

    /// <summary>
    /// Handles one message of a POST body (a single message or one element of a batch).
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled (request aborted).</exception>
    public async Task<McpDispatchResult> DispatchAsync(JsonElement message, CancellationToken cancellationToken) {
        if (message.ValueKind != JsonValueKind.Object) {
            return Error(null, JsonRpcErrorCodes.InvalidRequest, "A JSON-RPC message must be an object.");
        }

        var hasId = message.TryGetProperty("id", out var idElement);
        var idIsValid = hasId && idElement.ValueKind is JsonValueKind.String or JsonValueKind.Number;
        var id = idIsValid ? JsonRpc.CopyId(idElement) : null;

        if (!message.TryGetProperty("jsonrpc", out var version)
            || version.ValueKind != JsonValueKind.String
            || version.GetString() != JsonRpc.Version) {
            return Error(id, JsonRpcErrorCodes.InvalidRequest, "The 'jsonrpc' member must be \"2.0\".");
        }

        if (!message.TryGetProperty("method", out var methodElement)) {
            if (hasId && (message.TryGetProperty("result", out _) || message.TryGetProperty("error", out _))) {
                // A response to a server request. The server sends none, so there is nothing to correlate.
                return McpDispatchResult.None;
            }

            return Error(id, JsonRpcErrorCodes.InvalidRequest, "The message has no 'method'.");
        }

        if (methodElement.ValueKind != JsonValueKind.String) {
            return Error(id, JsonRpcErrorCodes.InvalidRequest, "The 'method' member must be a string.");
        }

        if (hasId && !idIsValid) {
            return Error(null, JsonRpcErrorCodes.InvalidRequest, "The 'id' member must be a string or a number.");
        }

        var method = methodElement.GetString()!;

        if (!hasId) {
            // Notifications (notifications/initialized, notifications/cancelled, ...) need no action and get no answer.
            return McpDispatchResult.None;
        }

        message.TryGetProperty("params", out var parameters);

        try {
            switch (method) {
                case "initialize":
                    return Initialize(id, parameters);

                case "ping":
                    return new McpDispatchResult(JsonRpc.Result(id, new JsonObject()));

                case "tools/list":
                    return new McpDispatchResult(JsonRpc.Result(id, ListTools()));

                case "tools/call":
                    var callResult = await CallToolAsync(parameters, cancellationToken).ConfigureAwait(false);

                    return new McpDispatchResult(JsonRpc.Result(id, callResult));

                default:
                    return Error(id, JsonRpcErrorCodes.MethodNotFound, $"Method '{method}' is not supported.");
            }
        } catch (JsonRpcException ex) {
            return Error(id, ex.Code, ex.Message);
        } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
            throw;
        } catch (Exception ex) {
            logger.LogError(ex, "MCP method {Method} failed", method);

            return Error(id, JsonRpcErrorCodes.InternalError, $"Internal error: {ex.Message}");
        }
    }

    private McpDispatchResult Initialize(JsonNode? id, JsonElement parameters) {
        string? requested = null;

        if (parameters.ValueKind != JsonValueKind.Undefined) {
            if (parameters.ValueKind != JsonValueKind.Object) {
                throw new JsonRpcException(JsonRpcErrorCodes.InvalidParams, "The initialize params must be an object.");
            }

            if (parameters.TryGetProperty("protocolVersion", out var versionElement)
                && versionElement.ValueKind == JsonValueKind.String) {
                requested = versionElement.GetString();
            }
        }

        var negotiated = McpProtocolVersions.Negotiate(requested);

        var result = new JsonObject {
            ["protocolVersion"] = negotiated,
            ["capabilities"] = new JsonObject {
                ["tools"] = new JsonObject {
                    ["listChanged"] = false
                }
            },
            ["serverInfo"] = new JsonObject {
                ["name"] = ServerName,
                ["title"] = ServerTitle,
                ["version"] = options.ServerVersion
            }
        };

        if (!string.IsNullOrWhiteSpace(instructions.Text)) {
            result["instructions"] = instructions.Text;
        }

        return new McpDispatchResult(JsonRpc.Result(id, result), negotiated);
    }

    private JsonObject ListTools() {
        var items = new JsonArray();

        foreach (var tool in tools) {
            items.Add(DescribeTool(tool));
        }

        return new JsonObject {
            ["tools"] = items
        };
    }

    /// <summary>The <c>tools/list</c> entry of a tool.</summary>
    internal static JsonObject DescribeTool(McpTool tool) {
        var annotations = tool.Annotations;

        return new JsonObject {
            ["name"] = tool.Name,
            ["title"] = tool.Title,
            ["description"] = tool.Description,
            ["inputSchema"] = tool.InputSchema.DeepClone(),
            ["annotations"] = new JsonObject {
                ["title"] = tool.Title,
                ["readOnlyHint"] = annotations.ReadOnly,
                ["destructiveHint"] = annotations.Destructive,
                ["idempotentHint"] = annotations.Idempotent,
                ["openWorldHint"] = false
            }
        };
    }

    private async Task<JsonObject> CallToolAsync(JsonElement parameters, CancellationToken cancellationToken) {
        if (parameters.ValueKind != JsonValueKind.Object) {
            throw new JsonRpcException(JsonRpcErrorCodes.InvalidParams, "The tools/call params must be an object.");
        }

        if (!parameters.TryGetProperty("name", out var nameElement) || nameElement.ValueKind != JsonValueKind.String) {
            throw new JsonRpcException(JsonRpcErrorCodes.InvalidParams, "The tools/call params need a string 'name'.");
        }

        var name = nameElement.GetString()!;

        if (!toolsByName.TryGetValue(name, out var tool)) {
            var reason = options.ReadOnly ? " (or it is not available in read-only mode)" : string.Empty;

            throw new JsonRpcException(JsonRpcErrorCodes.InvalidParams, $"Unknown tool '{name}'{reason}.");
        }

        JsonElement? arguments = null;

        if (parameters.TryGetProperty("arguments", out var argumentsElement)
            && argumentsElement.ValueKind != JsonValueKind.Null) {
            if (argumentsElement.ValueKind != JsonValueKind.Object) {
                throw new JsonRpcException(JsonRpcErrorCodes.InvalidParams, "The tools/call 'arguments' must be an object.");
            }

            // Cloned: the request's JsonDocument is disposed after the response, but a tool may hand its arguments to a job.
            arguments = argumentsElement.Clone();
        }

        var result = await InvokeToolAsync(tool, arguments, cancellationToken).ConfigureAwait(false);

        return SerializeResult(result);
    }

    private async Task<ToolCallResult> InvokeToolAsync(McpTool tool, JsonElement? arguments, CancellationToken cancellationToken) {
        try {
            var result = await tool.InvokeAsync(new ToolArguments(arguments), toolContext, cancellationToken).ConfigureAwait(false);

            return result ?? ToolCallResult.Error($"Internal error in {tool.Name}: the tool returned no result.");
        } catch (ToolArgumentException ex) {
            return ToolCallResult.Error(ex.Message);
        } catch (JobConflictException ex) {
            return ToolCallResult.Error(ex.Message);
        } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
            throw;
        } catch (Exception ex) {
            logger.LogError(ex, "MCP tool {Tool} failed", tool.Name);

            return ToolCallResult.Error($"Internal error in {tool.Name}: {ex.Message}");
        }
    }

    /// <summary>The MCP <c>CallToolResult</c> object of a tool result.</summary>
    internal static JsonObject SerializeResult(ToolCallResult result) {
        var serialized = new JsonObject {
            ["content"] = new JsonArray {
                new JsonObject {
                    ["type"] = "text",
                    ["text"] = result.Text
                }
            }
        };

        if (result.Structured is not null) {
            serialized["structuredContent"] = result.Structured.DeepClone();
        }

        serialized["isError"] = result.IsError;

        return serialized;
    }

    private static McpDispatchResult Error(JsonNode? id, int code, string message) {
        return new McpDispatchResult(JsonRpc.Error(id, code, message));
    }
}
