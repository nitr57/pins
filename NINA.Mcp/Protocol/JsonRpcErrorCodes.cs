// SPDX-License-Identifier: MPL-2.0

namespace NINA.Mcp.Protocol;

/// <summary>JSON-RPC 2.0 error codes used by the MCP endpoint.</summary>
public static class JsonRpcErrorCodes {

    /// <summary>The body is not valid JSON.</summary>
    public const int ParseError = -32700;

    /// <summary>Valid JSON, but not a valid JSON-RPC message (or rejected by the transport).</summary>
    public const int InvalidRequest = -32600;

    public const int MethodNotFound = -32601;

    /// <summary>Malformed parameters, unknown tool or tool hidden by read-only mode.</summary>
    public const int InvalidParams = -32602;

    public const int InternalError = -32603;
}

/// <summary>Thrown inside the dispatcher to answer a request with a JSON-RPC error.</summary>
internal sealed class JsonRpcException : Exception {

    public JsonRpcException(int code, string message) : base(message) {
        Code = code;
    }

    public int Code { get; }
}
