// SPDX-License-Identifier: MPL-2.0

namespace NINA.Mcp;

/// <summary>
/// Configuration of the MCP endpoint. Read from environment variables by <see cref="FromEnvironment"/> so that it can be
/// changed with a systemd drop-in, without a profile setting or a command line option (pins' command line parser
/// rejects unknown options).
/// </summary>
public sealed class McpServerOptions {

    /// <summary>PINS_MCP_ENABLED: "0", "false", "no" or "off" disables the endpoint. Default: enabled.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Route of the Streamable HTTP endpoint on the pins web host. Default "/mcp".</summary>
    public string Path { get; init; } = "/mcp";

    /// <summary>PINS_MCP_TOKEN: when set, every request needs "Authorization: Bearer &lt;token&gt;".</summary>
    public string? BearerToken { get; init; }

    /// <summary>
    /// PINS_MCP_ALLOWED_ORIGINS: comma separated extra origins (e.g. "https://my.host") allowed besides the built-in
    /// local ones. Requests without an Origin header (non-browser clients such as Claude Code) are always allowed.
    /// </summary>
    public IReadOnlyList<string> AllowedOrigins { get; init; } = [];

    /// <summary>PINS_MCP_READONLY: "1"/"true" lists and allows only tools whose annotations are read-only.</summary>
    public bool ReadOnly { get; init; }

    /// <summary>Version reported in <c>serverInfo</c>. Set by the host (pins version).</summary>
    public string ServerVersion { get; init; } = "0.0.0";

    /// <summary>Idle time after which an MCP session is forgotten. Default 1 hour.</summary>
    public TimeSpan SessionIdleTimeout { get; init; } = TimeSpan.FromHours(1);

    /// <summary>Maximum request body size in bytes. Default 1 MiB.</summary>
    public long MaxRequestBodyBytes { get; init; } = 1024 * 1024;

    /// <summary>Reads the PINS_MCP_* variables. <paramref name="getVariable"/> is injectable for tests.</summary>
    public static McpServerOptions FromEnvironment(string serverVersion, Func<string, string?>? getVariable = null) {
        getVariable ??= Environment.GetEnvironmentVariable;

        var token = getVariable("PINS_MCP_TOKEN");
        var origins = getVariable("PINS_MCP_ALLOWED_ORIGINS");

        return new McpServerOptions {
            Enabled = ParseBool(getVariable("PINS_MCP_ENABLED"), defaultValue: true),
            BearerToken = string.IsNullOrWhiteSpace(token) ? null : token.Trim(),
            AllowedOrigins = string.IsNullOrWhiteSpace(origins)
                ? []
                : origins.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            ReadOnly = ParseBool(getVariable("PINS_MCP_READONLY"), defaultValue: false),
            ServerVersion = serverVersion
        };
    }

    private static bool ParseBool(string? value, bool defaultValue) {
        if (string.IsNullOrWhiteSpace(value)) {
            return defaultValue;
        }

        return value.Trim().ToLowerInvariant() switch {
            "1" or "true" or "yes" or "on" => true,
            "0" or "false" or "no" or "off" => false,
            _ => defaultValue
        };
    }
}
