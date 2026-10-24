// SPDX-License-Identifier: MPL-2.0

using System.Security.Cryptography;
using System.Text;

namespace NINA.Mcp.Transport;

/// <summary>Optional shared-secret check of the <c>Authorization: Bearer</c> header.</summary>
public sealed class McpBearerToken {

    private const string Scheme = "Bearer ";

    private readonly byte[]? expectedHash;

    public McpBearerToken(McpServerOptions options) {
        if (!string.IsNullOrEmpty(options.BearerToken)) {
            expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(options.BearerToken));
        }
    }

    public bool IsRequired => expectedHash is not null;

    /// <summary>True when no token is configured or the header carries it. Constant time in the token content.</summary>
    public bool IsAuthorized(string? authorizationHeader) {
        if (expectedHash is null) {
            return true;
        }

        if (authorizationHeader is null || !authorizationHeader.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase)) {
            return false;
        }

        var presented = authorizationHeader[Scheme.Length..].Trim();

        // Hashing first makes both sides the same length, so the comparison does not leak the token length either.
        var presentedHash = SHA256.HashData(Encoding.UTF8.GetBytes(presented));

        return CryptographicOperations.FixedTimeEquals(presentedHash, expectedHash);
    }
}
