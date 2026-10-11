// SPDX-License-Identifier: MPL-2.0

using Microsoft.AspNetCore.Http;
using NINA.Mcp.Protocol;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NINA.Mcp.Transport;

/// <summary>
/// MCP Streamable HTTP transport, server side, JSON responses only (no SSE): POST carries client messages, GET is
/// refused with 405 (no server-initiated stream), DELETE ends a session.
/// </summary>
/// <remarks>
/// Every verb first checks the <c>Origin</c> (403) and the optional bearer token (401). POST then checks the content
/// type (415), the <c>MCP-Protocol-Version</c> header (400), the body size (413), the JSON (400 / -32700) and the
/// session (400 missing, 404 unknown or expired); <c>initialize</c> needs no session and creates one.
/// </remarks>
public sealed class McpHttpEndpoint {

    public const string SessionIdHeader = "Mcp-Session-Id";

    public const string ProtocolVersionHeader = "MCP-Protocol-Version";

    private const string JsonContentType = "application/json; charset=utf-8";

    private static readonly JsonDocumentOptions DocumentOptions = new() {
        MaxDepth = 64
    };

    private static readonly JsonWriterOptions WriterOptions = new() {
        Encoder = McpJson.Options.Encoder
    };

    private readonly McpDispatcher dispatcher;
    private readonly McpSessionStore sessions;
    private readonly McpOriginValidator originValidator;
    private readonly McpBearerToken bearerToken;
    private readonly McpServerOptions options;

    public McpHttpEndpoint(
        McpDispatcher dispatcher,
        McpSessionStore sessions,
        McpOriginValidator originValidator,
        McpBearerToken bearerToken,
        McpServerOptions options) {
        this.dispatcher = dispatcher;
        this.sessions = sessions;
        this.originValidator = originValidator;
        this.bearerToken = bearerToken;
        this.options = options;
    }

    public async Task HandlePostAsync(HttpContext context) {
        if (!await CheckSecurityAsync(context).ConfigureAwait(false)) {
            return;
        }

        var request = context.Request;

        if (!request.HasJsonContentType()) {
            await WriteTransportErrorAsync(context, StatusCodes.Status415UnsupportedMediaType, "Content-Type must be application/json.").ConfigureAwait(false);
            return;
        }

        if (!await CheckProtocolVersionHeaderAsync(context).ConfigureAwait(false)) {
            return;
        }

        if (request.ContentLength > options.MaxRequestBodyBytes) {
            await WriteBodyTooLargeAsync(context).ConfigureAwait(false);
            return;
        }

        var cancellationToken = context.RequestAborted;

        try {
            var body = await ReadBodyAsync(request, options.MaxRequestBodyBytes, cancellationToken).ConfigureAwait(false);

            if (body is null) {
                await WriteBodyTooLargeAsync(context).ConfigureAwait(false);
                return;
            }

            JsonDocument document;

            try {
                document = JsonDocument.Parse(body, DocumentOptions);
            } catch (JsonException) {
                var error = JsonRpc.Error(null, JsonRpcErrorCodes.ParseError, "Parse error: the body is not valid JSON.");

                await WriteJsonAsync(context, StatusCodes.Status400BadRequest, error).ConfigureAwait(false);
                return;
            }

            using (document) {
                await HandleMessageAsync(context, document.RootElement, cancellationToken).ConfigureAwait(false);
            }
        } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
            // The client went away; there is nobody to answer.
        } catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge) {
            await WriteBodyTooLargeAsync(context).ConfigureAwait(false);
        }
    }

    public async Task HandleGetAsync(HttpContext context) {
        if (!await CheckSecurityAsync(context).ConfigureAwait(false)) {
            return;
        }

        context.Response.Headers.Allow = "POST, DELETE";

        await WriteTransportErrorAsync(
            context,
            StatusCodes.Status405MethodNotAllowed,
            "This server does not offer a server-initiated event stream. Use POST.").ConfigureAwait(false);
    }

    public async Task HandleDeleteAsync(HttpContext context) {
        if (!await CheckSecurityAsync(context).ConfigureAwait(false)) {
            return;
        }

        if (!await CheckProtocolVersionHeaderAsync(context).ConfigureAwait(false)) {
            return;
        }

        var sessionId = GetSessionId(context.Request);

        if (sessionId is null) {
            await WriteTransportErrorAsync(context, StatusCodes.Status400BadRequest, $"Missing {SessionIdHeader} header.").ConfigureAwait(false);
            return;
        }

        if (!sessions.Remove(sessionId)) {
            await WriteSessionNotFoundAsync(context).ConfigureAwait(false);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status204NoContent;
    }

    private async Task HandleMessageAsync(HttpContext context, JsonElement root, CancellationToken cancellationToken) {
        if (root.ValueKind == JsonValueKind.Array) {
            await HandleBatchAsync(context, root, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (IsInitializeRequest(root)) {
            await HandleInitializeAsync(context, root, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!await CheckSessionAsync(context).ConfigureAwait(false)) {
            return;
        }

        var result = await dispatcher.DispatchAsync(root, cancellationToken).ConfigureAwait(false);

        if (result.Response is null) {
            context.Response.StatusCode = StatusCodes.Status202Accepted;
            return;
        }

        // A message so broken that its id is unknown is rejected at the HTTP level as well.
        var status = IsErrorWithoutId(result.Response) ? StatusCodes.Status400BadRequest : StatusCodes.Status200OK;

        await WriteJsonAsync(context, status, result.Response).ConfigureAwait(false);
    }

    private async Task HandleInitializeAsync(HttpContext context, JsonElement root, CancellationToken cancellationToken) {
        var result = await dispatcher.DispatchAsync(root, cancellationToken).ConfigureAwait(false);

        if (result.InitializedProtocolVersion is not null) {
            var session = sessions.Create(result.InitializedProtocolVersion);

            context.Response.Headers[SessionIdHeader] = session.Id;
        }

        await WriteJsonAsync(context, StatusCodes.Status200OK, result.Response!).ConfigureAwait(false);
    }

    private const string BatchProtocolVersion = "2025-03-26";

    private const int MaxBatchSize = 16;

    private async Task HandleBatchAsync(HttpContext context, JsonElement root, CancellationToken cancellationToken) {
        if (root.GetArrayLength() == 0) {
            var error = JsonRpc.Error(null, JsonRpcErrorCodes.InvalidRequest, "Empty batch.");

            await WriteJsonAsync(context, StatusCodes.Status400BadRequest, error).ConfigureAwait(false);
            return;
        }

        if (!await CheckSessionAsync(context).ConfigureAwait(false)) {
            return;
        }

        // JSON-RPC batching exists only in protocol 2025-03-26; later versions removed it.
        var session = sessions.Get(GetSessionId(context.Request)!);

        if (session is not null && session.ProtocolVersion != BatchProtocolVersion) {
            var error = JsonRpc.Error(null, JsonRpcErrorCodes.InvalidRequest, $"Batches are only supported with protocol version {BatchProtocolVersion}.");

            await WriteJsonAsync(context, StatusCodes.Status400BadRequest, error).ConfigureAwait(false);
            return;
        }

        if (root.GetArrayLength() > MaxBatchSize) {
            var error = JsonRpc.Error(null, JsonRpcErrorCodes.InvalidRequest, $"Batches are limited to {MaxBatchSize} messages.");

            await WriteJsonAsync(context, StatusCodes.Status400BadRequest, error).ConfigureAwait(false);
            return;
        }

        var responses = new JsonArray();

        foreach (var message in root.EnumerateArray()) {
            if (IsInitializeRequest(message)) {
                var id = JsonRpc.CopyId(message.GetProperty("id"));

                responses.Add(JsonRpc.Error(id, JsonRpcErrorCodes.InvalidRequest, "initialize must not be part of a batch."));
                continue;
            }

            var result = await dispatcher.DispatchAsync(message, cancellationToken).ConfigureAwait(false);

            if (result.Response is not null) {
                responses.Add(result.Response);
            }
        }

        if (responses.Count == 0) {
            context.Response.StatusCode = StatusCodes.Status202Accepted;
            return;
        }

        await WriteJsonAsync(context, StatusCodes.Status200OK, responses).ConfigureAwait(false);
    }

    /// <summary>Origin (403), then bearer token (401). False when a response has been written.</summary>
    private async Task<bool> CheckSecurityAsync(HttpContext context) {
        var request = context.Request;
        var origin = request.Headers.Origin.ToString();

        if (!originValidator.IsAllowed(origin)) {
            await WriteTransportErrorAsync(context, StatusCodes.Status403Forbidden, "Origin not allowed.").ConfigureAwait(false);
            return false;
        }

        if (!bearerToken.IsAuthorized(request.Headers.Authorization.ToString())) {
            context.Response.Headers.WWWAuthenticate = "Bearer";

            await WriteTransportErrorAsync(context, StatusCodes.Status401Unauthorized, "Missing or invalid bearer token.").ConfigureAwait(false);
            return false;
        }

        if (!string.IsNullOrEmpty(origin)) {
            // Lets browser clients (e.g. a web chat panel) read the session id across origins.
            context.Response.Headers.AccessControlExposeHeaders = SessionIdHeader;
        }

        return true;
    }

    private static async Task<bool> CheckProtocolVersionHeaderAsync(HttpContext context) {
        if (!context.Request.Headers.TryGetValue(ProtocolVersionHeader, out var values)) {
            return true;
        }

        var version = values.ToString().Trim();

        if (McpProtocolVersions.IsSupported(version)) {
            return true;
        }

        var supported = string.Join(", ", McpProtocolVersions.Supported);

        await WriteTransportErrorAsync(
            context,
            StatusCodes.Status400BadRequest,
            $"Unsupported {ProtocolVersionHeader} '{version}'. Supported: {supported}.").ConfigureAwait(false);

        return false;
    }

    /// <summary>Missing session id → 400, unknown or expired → 404. False when a response has been written.</summary>
    private async Task<bool> CheckSessionAsync(HttpContext context) {
        var sessionId = GetSessionId(context.Request);

        if (sessionId is null) {
            await WriteTransportErrorAsync(
                context,
                StatusCodes.Status400BadRequest,
                $"Missing {SessionIdHeader} header. Send initialize first.").ConfigureAwait(false);

            return false;
        }

        if (sessions.Get(sessionId) is null) {
            await WriteSessionNotFoundAsync(context).ConfigureAwait(false);
            return false;
        }

        return true;
    }

    private static string? GetSessionId(HttpRequest request) {
        var value = request.Headers[SessionIdHeader].ToString().Trim();

        return value.Length == 0 ? null : value;
    }

    private static bool IsInitializeRequest(JsonElement message) {
        return message.ValueKind == JsonValueKind.Object
            && message.TryGetProperty("method", out var method)
            && method.ValueKind == JsonValueKind.String
            && method.ValueEquals("initialize")
            && message.TryGetProperty("id", out var id)
            && id.ValueKind is JsonValueKind.String or JsonValueKind.Number;
    }

    private static bool IsErrorWithoutId(JsonObject response) {
        return response.ContainsKey("error") && response["id"] is null;
    }

    /// <summary>The body, or null when it exceeds <paramref name="limit"/> bytes.</summary>
    private static async Task<byte[]?> ReadBodyAsync(HttpRequest request, long limit, CancellationToken cancellationToken) {
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];

        while (true) {
            var read = await request.Body.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);

            if (read == 0) {
                break;
            }

            if (buffer.Length + read > limit) {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private Task WriteBodyTooLargeAsync(HttpContext context) {
        return WriteTransportErrorAsync(
            context,
            StatusCodes.Status413PayloadTooLarge,
            $"The request body exceeds {options.MaxRequestBodyBytes} bytes.");
    }

    private static Task WriteSessionNotFoundAsync(HttpContext context) {
        return WriteTransportErrorAsync(
            context,
            StatusCodes.Status404NotFound,
            "Unknown or expired session. Start a new session with initialize.");
    }

    /// <summary>An HTTP error with a JSON-RPC error body without id, as the Streamable HTTP transport suggests.</summary>
    private static Task WriteTransportErrorAsync(HttpContext context, int status, string message) {
        return WriteJsonAsync(context, status, JsonRpc.Error(null, JsonRpcErrorCodes.InvalidRequest, message));
    }

    private static async Task WriteJsonAsync(HttpContext context, int status, JsonNode body) {
        var bytes = Serialize(body);
        var response = context.Response;

        response.StatusCode = status;
        response.ContentType = JsonContentType;
        response.ContentLength = bytes.Length;

        await response.Body.WriteAsync(bytes, context.RequestAborted).ConfigureAwait(false);
    }

    /// <summary>UTF-8 JSON with the escaping of <see cref="McpJson.Options"/> (a JsonNode ignores the serializer's encoder).</summary>
    internal static byte[] Serialize(JsonNode body) {
        using var buffer = new MemoryStream();

        using (var writer = new Utf8JsonWriter(buffer, WriterOptions)) {
            body.WriteTo(writer, McpJson.Options);
        }

        return buffer.ToArray();
    }
}
