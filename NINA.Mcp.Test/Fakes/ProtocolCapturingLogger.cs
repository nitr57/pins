// SPDX-License-Identifier: MPL-2.0

using Microsoft.Extensions.Logging;

namespace NINA.Mcp.Test.Fakes;

/// <summary>Records log entries so tests can assert that failures are logged.</summary>
internal sealed class ProtocolCapturingLogger<T> : ILogger<T> {

    private readonly List<(LogLevel Level, string Message, Exception? Exception)> entries = [];

    public IReadOnlyList<(LogLevel Level, string Message, Exception? Exception)> Entries {
        get {
            lock (entries) {
                return entries.ToList();
            }
        }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull {
        return null;
    }

    public bool IsEnabled(LogLevel logLevel) {
        return true;
    }

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
        lock (entries) {
            entries.Add((logLevel, formatter(state, exception), exception));
        }
    }
}
