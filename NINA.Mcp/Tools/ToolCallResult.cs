// SPDX-License-Identifier: MPL-2.0

using NINA.Mcp.Protocol;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NINA.Mcp.Tools;

/// <summary>
/// Result of a tool call. Serialized by the protocol layer as an MCP <c>CallToolResult</c>:
/// one text content block (<see cref="Text"/>), <c>structuredContent</c> (<see cref="Structured"/>, when set) and <c>isError</c>.
/// </summary>
public sealed class ToolCallResult {

    private ToolCallResult(string text, JsonObject? structured, bool isError) {
        Text = text;
        Structured = structured;
        IsError = isError;
    }

    public string Text { get; }

    public JsonObject? Structured { get; }

    public bool IsError { get; }

    /// <summary>Success with a JSON payload. The text block carries the same JSON for clients without structured content support.</summary>
    public static ToolCallResult Json(object payload) {
        var node = JsonSerializer.SerializeToNode(payload, payload.GetType(), McpJson.Options);

        var structured = node as JsonObject ?? new JsonObject { ["value"] = node };

        return new ToolCallResult(structured.ToJsonString(McpJson.Options), structured, isError: false);
    }

    /// <summary>Success with plain text only.</summary>
    public static ToolCallResult Success(string text) {
        return new ToolCallResult(text, structured: null, isError: false);
    }

    /// <summary>Failure the model can react to.</summary>
    public static ToolCallResult Error(string message) {
        return new ToolCallResult(message, structured: null, isError: true);
    }
}
