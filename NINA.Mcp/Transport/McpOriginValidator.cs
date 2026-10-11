// SPDX-License-Identifier: MPL-2.0

using System.Net;
using System.Net.Sockets;

namespace NINA.Mcp.Transport;

/// <summary>
/// DNS rebinding protection for the MCP endpoint. A browser always sends <c>Origin</c> on cross-origin POSTs, so a page
/// on a public site (or one that rebinds its name to the pins address) is rejected unless its host is local by construction.
/// </summary>
/// <remarks>
/// Allowed: no <c>Origin</c> header (non-browser clients), host <c>localhost</c>, a loopback / private / link-local /
/// CGNAT (Tailscale) IP literal, a <c>.local</c> (mDNS) name, the machine name, or an exact entry of
/// <see cref="McpServerOptions.AllowedOrigins"/> (scheme, host and port).
/// </remarks>
public sealed class McpOriginValidator {

    private readonly string machineName;
    private readonly HashSet<string> allowedOrigins;

    public McpOriginValidator(McpServerOptions options) : this(options, Environment.MachineName) {
    }

    public McpOriginValidator(McpServerOptions options, string machineName) {
        this.machineName = machineName.TrimEnd('.');
        allowedOrigins = new HashSet<string>(StringComparer.Ordinal);

        foreach (var origin in options.AllowedOrigins) {
            var normalized = Normalize(origin);

            if (normalized is not null) {
                allowedOrigins.Add(normalized);
            }
        }
    }

    /// <summary>True when a request with this <c>Origin</c> header value (null or empty = absent) may proceed.</summary>
    public bool IsAllowed(string? origin) {
        if (string.IsNullOrEmpty(origin)) {
            return true;
        }

        if (!Uri.TryCreate(origin.Trim(), UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host)) {
            return false;
        }

        if (IsLocalHost(uri)) {
            return true;
        }

        var normalized = Normalize(uri);

        return normalized is not null && allowedOrigins.Contains(normalized);
    }

    private bool IsLocalHost(Uri uri) {
        if (uri.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6) {
            return IPAddress.TryParse(uri.DnsSafeHost, out var address) && IsLocalAddress(address);
        }

        if (uri.HostNameType != UriHostNameType.Dns) {
            return false;
        }

        var host = uri.DnsSafeHost.TrimEnd('.');

        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) {
            return true;
        }

        if (host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)) {
            return true;
        }

        return machineName.Length > 0 && host.Equals(machineName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Loopback, RFC 1918, link-local, CGNAT (IPv4); loopback, unique local, link-local (IPv6).</summary>
    public static bool IsLocalAddress(IPAddress address) {
        if (address.IsIPv4MappedToIPv6) {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily == AddressFamily.InterNetwork) {
            var bytes = address.GetAddressBytes();

            return bytes[0] == 127
                || bytes[0] == 10
                || (bytes[0] == 172 && (bytes[1] & 0xF0) == 16)
                || (bytes[0] == 192 && bytes[1] == 168)
                || (bytes[0] == 169 && bytes[1] == 254)
                || (bytes[0] == 100 && (bytes[1] & 0xC0) == 64);
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6) {
            var bytes = address.GetAddressBytes();

            return IPAddress.IPv6Loopback.Equals(address)
                || (bytes[0] & 0xFE) == 0xFC
                || (bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0x80);
        }

        return false;
    }

    private static string? Normalize(string origin) {
        if (!Uri.TryCreate(origin.Trim(), UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host)) {
            return null;
        }

        return Normalize(uri);
    }

    /// <summary>"scheme://host:port", lower case, explicit (default) port.</summary>
    private static string? Normalize(Uri uri) {
        if (uri.Port < 0) {
            return null;
        }

        return $"{uri.Scheme.ToLowerInvariant()}://{uri.Host.ToLowerInvariant()}:{uri.Port}";
    }
}
