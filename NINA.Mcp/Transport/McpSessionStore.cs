// SPDX-License-Identifier: MPL-2.0

using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace NINA.Mcp.Transport;

/// <summary>One MCP session, created by <c>initialize</c>.</summary>
public sealed class McpSession {

    private long lastSeenTicks;

    internal McpSession(string id, string protocolVersion, DateTimeOffset now) {
        Id = id;
        ProtocolVersion = protocolVersion;
        CreatedAt = now;
        lastSeenTicks = now.UtcTicks;
    }

    public string Id { get; }

    /// <summary>The protocol version negotiated by <c>initialize</c>.</summary>
    public string ProtocolVersion { get; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset LastSeen => new(Interlocked.Read(ref lastSeenTicks), TimeSpan.Zero);

    internal void Touch(DateTimeOffset now) {
        Interlocked.Exchange(ref lastSeenTicks, now.UtcTicks);
    }
}

/// <summary>
/// In-memory sessions with lazy idle expiry. Expired sessions are dropped when looked up and swept whenever a session is
/// created; beyond <see cref="MaxSessions"/> the least recently used session is evicted.
/// </summary>
public sealed class McpSessionStore {

    public const int MaxSessions = 100;

    private readonly ConcurrentDictionary<string, McpSession> sessions = new(StringComparer.Ordinal);
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan idleTimeout;
    private readonly object createGate = new();

    public McpSessionStore(McpServerOptions options, TimeProvider timeProvider) {
        this.timeProvider = timeProvider;
        idleTimeout = options.SessionIdleTimeout;
    }

    public int Count => sessions.Count;

    public McpSession Create(string protocolVersion) {
        var now = timeProvider.GetUtcNow();

        lock (createGate) {
            Sweep(now);

            while (sessions.Count >= MaxSessions) {
                var oldest = sessions.Values.MinBy(session => session.LastSeen);

                if (oldest is null) {
                    break;
                }

                sessions.TryRemove(oldest.Id, out _);
            }

            while (true) {
                var session = new McpSession(NewId(), protocolVersion, now);

                if (sessions.TryAdd(session.Id, session)) {
                    return session;
                }
            }
        }
    }

    /// <summary>The live session with this id (and marks it used), or null when unknown or expired.</summary>
    public McpSession? Get(string id) {
        if (!sessions.TryGetValue(id, out var session)) {
            return null;
        }

        var now = timeProvider.GetUtcNow();

        if (IsExpired(session, now)) {
            sessions.TryRemove(id, out _);
            return null;
        }

        session.Touch(now);

        return session;
    }

    /// <summary>Ends a session. False when it was unknown or already expired.</summary>
    public bool Remove(string id) {
        if (!sessions.TryRemove(id, out var session)) {
            return false;
        }

        return !IsExpired(session, timeProvider.GetUtcNow());
    }

    private void Sweep(DateTimeOffset now) {
        foreach (var session in sessions.Values) {
            if (IsExpired(session, now)) {
                sessions.TryRemove(session.Id, out _);
            }
        }
    }

    private bool IsExpired(McpSession session, DateTimeOffset now) {
        return now - session.LastSeen > idleTimeout;
    }

    /// <summary>128 random bits as 32 lower-case hex characters (visible ASCII, as the spec requires).</summary>
    private static string NewId() {
        return Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
    }
}
