// SPDX-License-Identifier: MPL-2.0

using NINA.Mcp.Jobs;
using System.Text.Json.Nodes;

namespace NINA.Mcp.Tools;

/// <summary>
/// One MCP tool. Tools are registered in DI as <see cref="McpTool"/> singletons and listed by the server in registration order.
/// </summary>
/// <remarks>
/// Contract between the tool catalog and the protocol layer:
/// <list type="bullet">
/// <item><see cref="InvokeAsync"/> returns a <see cref="ToolCallResult"/>; failures the model can react to (device not connected,
/// sequence running, bad argument) are results with <see cref="ToolCallResult.IsError"/> set, never JSON-RPC errors.</item>
/// <item>A <see cref="ToolArgumentException"/> thrown from <see cref="InvokeAsync"/> is turned into an error result by the server.</item>
/// <item>Any other exception is logged by the server and reported as an error result with the exception message.</item>
/// <item>The cancellation token is the HTTP request's. Work that must outlive the request goes through <see cref="ToolContext.Jobs"/>.</item>
/// </list>
/// </remarks>
public abstract class McpTool {

    /// <summary>Unique tool name, snake_case, ASCII letters, digits and underscores only.</summary>
    public abstract string Name { get; }

    /// <summary>Short human-readable title shown by clients.</summary>
    public abstract string Title { get; }

    /// <summary>Description for the model: what the tool does, units, side effects and when to use it.</summary>
    public abstract string Description { get; }

    /// <summary>JSON Schema (type "object") of the arguments. Must not be mutated after construction.</summary>
    public abstract JsonObject InputSchema { get; }

    /// <summary>Behavior hints. Tools that are not <see cref="ToolAnnotations.ReadOnly"/> are hidden in read-only mode.</summary>
    public abstract ToolAnnotations Annotations { get; }

    /// <summary>
    /// Whether the tool stays listed and callable when the server runs in read-only mode. Defaults to
    /// <see cref="ToolAnnotations.ReadOnly"/>; emergency stop tools override it to stay available.
    /// </summary>
    public virtual bool AllowedInReadOnlyMode => Annotations.ReadOnly;

    public abstract Task<ToolCallResult> InvokeAsync(ToolArguments arguments, ToolContext context, CancellationToken cancellationToken);
}

/// <summary>MCP tool annotations (hints for clients, e.g. to ask for confirmation before destructive calls).</summary>
public sealed record ToolAnnotations(bool ReadOnly, bool Destructive, bool Idempotent) {

    /// <summary>Only reads state.</summary>
    public static ToolAnnotations Query { get; } = new(ReadOnly: true, Destructive: false, Idempotent: true);

    /// <summary>Changes state without moving hardware or discarding work (e.g. load a sequence into the editor).</summary>
    public static ToolAnnotations Change { get; } = new(ReadOnly: false, Destructive: false, Idempotent: false);

    /// <summary>Edits pins state and can discard work (unsaved sequence edits, progress) without moving hardware.</summary>
    public static ToolAnnotations ChangeDestructive { get; } = new(ReadOnly: false, Destructive: true, Idempotent: false);

    /// <summary>Moves hardware, switches power or can discard work. Clients should confirm with the user.</summary>
    public static ToolAnnotations Hardware { get; } = new(ReadOnly: false, Destructive: true, Idempotent: false);

    /// <summary>Stops things. Safe to repeat; exempt from read-only mode is NOT implied.</summary>
    public static ToolAnnotations Stop { get; } = new(ReadOnly: false, Destructive: false, Idempotent: true);
}

/// <summary>Services available to a tool invocation.</summary>
public sealed class ToolContext {

    public ToolContext(IJobManager jobs, McpServerOptions options) {
        Jobs = jobs;
        Options = options;
    }

    public IJobManager Jobs { get; }

    public McpServerOptions Options { get; }
}

/// <summary>Invalid tool arguments. The message is shown to the model so it can correct the call.</summary>
public sealed class ToolArgumentException : Exception {

    public ToolArgumentException(string message) : base(message) {
    }
}
