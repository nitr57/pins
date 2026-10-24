// SPDX-License-Identifier: MPL-2.0

using System.Text.Json;
using System.Text.Json.Nodes;

namespace NINA.Mcp.Protocol;

/// <summary>Builders for JSON-RPC 2.0 response objects.</summary>
public static class JsonRpc {

    public const string Version = "2.0";

    public static JsonObject Result(JsonNode? id, JsonNode result) {
        return new JsonObject {
            ["jsonrpc"] = Version,
            ["id"] = id,
            ["result"] = result
        };
    }

    /// <summary>An error response. <paramref name="id"/> is null when the request id could not be determined.</summary>
    public static JsonObject Error(JsonNode? id, int code, string message) {
        return new JsonObject {
            ["jsonrpc"] = Version,
            ["id"] = id,
            ["error"] = new JsonObject {
                ["code"] = code,
                ["message"] = message
            }
        };
    }

    /// <summary>A copy of a request id (string or number) that is independent of the parsed document and echoes it verbatim.</summary>
    public static JsonNode? CopyId(JsonElement id) {
        return JsonValue.Create(id.Clone());
    }
}
