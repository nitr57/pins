// SPDX-License-Identifier: MPL-2.0

namespace NINA.Mcp.Protocol;

/// <summary>MCP protocol revisions the server speaks, and the <c>initialize</c> version negotiation.</summary>
public static class McpProtocolVersions {

    public const string Latest = "2025-11-25";

    /// <summary>Newest first.</summary>
    public static IReadOnlyList<string> Supported { get; } = [Latest, "2025-06-18", "2025-03-26"];

    public static bool IsSupported(string? version) {
        if (version is null) {
            return false;
        }

        return Supported.Contains(version, StringComparer.Ordinal);
    }

    /// <summary>Echoes the client's version when supported, otherwise answers the newest supported one.</summary>
    public static string Negotiate(string? requested) {
        return IsSupported(requested) ? requested! : Latest;
    }
}
