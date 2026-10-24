// SPDX-License-Identifier: MPL-2.0

using NINA.Mcp.Tools;
using System.Text.Json.Nodes;

namespace NINA.Mcp.Test.Fakes;

/// <summary>Configurable tool double for protocol and transport tests.</summary>
internal sealed class ProtocolFakeTool : McpTool {

    private readonly string name;
    private readonly ToolAnnotations annotations;
    private readonly bool? allowedInReadOnlyMode;
    private readonly Func<ToolArguments, ToolContext, CancellationToken, Task<ToolCallResult>> handler;

    public ProtocolFakeTool(
        string name,
        ToolAnnotations? annotations = null,
        Func<ToolArguments, ToolContext, CancellationToken, Task<ToolCallResult>>? handler = null,
        bool? allowedInReadOnlyMode = null) {
        this.name = name;
        this.annotations = annotations ?? ToolAnnotations.Query;
        this.allowedInReadOnlyMode = allowedInReadOnlyMode;
        this.handler = handler ?? ((_, _, _) => Task.FromResult(ToolCallResult.Success("ok")));

        InputSchema = new JsonObject {
            ["type"] = "object",
            ["properties"] = new JsonObject {
                ["value"] = new JsonObject {
                    ["type"] = "string"
                }
            },
            ["additionalProperties"] = false
        };
    }

    public override string Name => name;

    public override string Title => $"Title of {name}";

    public override string Description => DescriptionOverride?.Invoke() ?? $"Description of {name}.";

    /// <summary>Lets a test make the description getter throw.</summary>
    public Func<string>? DescriptionOverride { get; init; }

    public override JsonObject InputSchema { get; }

    public override ToolAnnotations Annotations => annotations;

    public override bool AllowedInReadOnlyMode => allowedInReadOnlyMode ?? base.AllowedInReadOnlyMode;

    public int CallCount { get; private set; }

    public ToolArguments? LastArguments { get; private set; }

    public ToolContext? LastContext { get; private set; }

    public override Task<ToolCallResult> InvokeAsync(ToolArguments arguments, ToolContext context, CancellationToken cancellationToken) {
        CallCount++;
        LastArguments = arguments;
        LastContext = context;

        return handler(arguments, context, cancellationToken);
    }
}
