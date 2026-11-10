// SPDX-License-Identifier: MPL-2.0

using Microsoft.Extensions.Logging;
using NINA.Core.Utility;

namespace NINA.Mcp.Adapter;

/// <summary>
/// Forwards what <c>NINA.Mcp</c> writes to <see cref="ILogger"/> from Information on into the pins log: job starts and
/// ends, the calls of tools that are not read-only, warnings and errors. pins clears the ASP.NET Core logging providers
/// and sets their minimum to Warning, so <see cref="McpAdapterServiceCollectionExtensions.AddPinsMcpForNina"/> adds a
/// filter rule that lets Information of these categories through to this provider.
/// </summary>
internal sealed class NinaLoggerProvider : ILoggerProvider {

    public const string CategoryPrefix = "NINA.Mcp";

    public ILogger CreateLogger(string categoryName) {
        return categoryName.StartsWith(CategoryPrefix, StringComparison.Ordinal)
            ? new ForwardingLogger(categoryName)
            : NullLogger.Instance;
    }

    public void Dispose() {
    }

    private sealed class ForwardingLogger : ILogger {

        private readonly string category;

        public ForwardingLogger(string category) {
            this.category = category;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel) {
            return logLevel >= LogLevel.Information && logLevel != LogLevel.None;
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
            if (!IsEnabled(logLevel)) {
                return;
            }

            var message = $"{category}: {formatter(state, exception)}";

            if (logLevel == LogLevel.Information) {
                Logger.Info(exception == null ? message : $"{message} ({exception.Message})");
                return;
            }

            if (logLevel == LogLevel.Warning) {
                Logger.Warning(exception == null ? message : $"{message} ({exception.Message})");
                return;
            }

            if (exception == null) {
                Logger.Error(message);
            } else {
                Logger.Error(message, exception);
            }
        }
    }

    private sealed class NullLogger : ILogger {

        public static readonly NullLogger Instance = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel) {
            return false;
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
        }
    }
}
