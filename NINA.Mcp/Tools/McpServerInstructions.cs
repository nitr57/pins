// SPDX-License-Identifier: MPL-2.0

namespace NINA.Mcp.Tools;

/// <summary>The <c>instructions</c> text the server returns from <c>initialize</c>. Registered in DI by the tool catalog.</summary>
public sealed record McpServerInstructions(string Text);
