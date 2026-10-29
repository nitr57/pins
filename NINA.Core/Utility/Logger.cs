#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Core.Enum;
using System;
using System.IO;
using System.Runtime.CompilerServices;
using Serilog;
using Serilog.Core;
using Serilog.Sinks.File;
using System.Text;
using Serilog.Events;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace NINA.Core.Utility {

    public static class Logger {
        private static LoggingLevelSwitch levelSwitch;

        // On RTC-less hosts (e.g. Raspberry Pi) the system clock can still be wrong when this runs,
        // and only gets corrected once a client connects later. Detect that step via the monotonic
        // clock and rename the log file(s) so the name matches the corrected date.
        private static readonly TimeSpan ClockJumpThreshold = TimeSpan.FromHours(1);
        private static readonly TimeSpan ClockWatchdogInterval = TimeSpan.FromSeconds(5);
        private static DateTime startupUtc;
        private static long startupTicks;
        private static string logDir;
        private static string logFileNamePrefix;
        private static Timer clockWatchdog;

        static Logger() {
            startupUtc = DateTime.UtcNow;
            startupTicks = Environment.TickCount64;

            var logDate = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            logDir = Path.Combine(CoreUtil.APPLICATIONTEMPPATH, "Logs");
            var processId = Environment.ProcessId;
            logFileNamePrefix = $"{logDate}-{CoreUtil.Version}.{processId}-";
            var logFilePath = Path.Combine(logDir, $"{logFileNamePrefix}.log");

            levelSwitch = new LoggingLevelSwitch();
            levelSwitch.MinimumLevel = LogEventLevel.Information;

            if (!Directory.Exists(logDir)) {
                Directory.CreateDirectory(logDir);
            } else {
                CoreUtil.DirectoryCleanup(logDir, TimeSpan.FromDays(-90));
            }

            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.ControlledBy(levelSwitch)
                .Enrich.With<LegacyLogLevelMappingEnricher>()
                .WriteTo.Console(
                    theme: Serilog.Sinks.SystemConsole.Themes.AnsiConsoleTheme.Literate,
                    outputTemplate: "{Timestamp:yyyy-MM-ddTHH:mm:ss.ffff}|{LegacyLogLevelConsole}|{Message:lj}{NewLine}{Exception}")
                .WriteTo.File(logFilePath,
                    rollOnFileSizeLimit: true,
                    rollingInterval: RollingInterval.Month,
                    outputTemplate: "{Timestamp:yyyy-MM-ddTHH:mm:ss.ffff}|{LegacyLogLevel}|{Message:lj}{NewLine}{Exception}",
                    shared: false,
                    buffered: false,
                    hooks: new HeaderWriter(GenerateHeader),
                    flushToDiskInterval: TimeSpan.FromSeconds(1),
                    retainedFileCountLimit: null)
                .CreateLogger();

            clockWatchdog = new Timer(CheckForClockCorrection, null, ClockWatchdogInterval, ClockWatchdogInterval);
        }

        private static void CheckForClockCorrection(object state) {
            var elapsedWall = DateTime.UtcNow - startupUtc;
            var elapsedMonotonic = TimeSpan.FromMilliseconds(Environment.TickCount64 - startupTicks);
            if ((elapsedWall - elapsedMonotonic).Duration() < ClockJumpThreshold) {
                return;
            }

            clockWatchdog.Change(Timeout.Infinite, Timeout.Infinite);
            try {
                var correctedLogFileNamePrefix = $"{DateTime.Now:yyyyMMdd-HHmmss}-{CoreUtil.Version}.{Environment.ProcessId}-";
                foreach (var oldFilePath in Directory.GetFiles(logDir, $"{logFileNamePrefix}*.log")) {
                    var newFilePath = Path.Combine(logDir, Path.GetFileName(oldFilePath).Replace(logFileNamePrefix, correctedLogFileNamePrefix));
                    File.Move(oldFilePath, newFilePath);
                }
            } catch {
                // Best effort - if the rename fails the file simply keeps its original (stale) name.
            } finally {
                clockWatchdog.Dispose();
            }
        }

        private static string GenerateHeader() {
            /* Initial log of App Version, OS Info, INDI Version, .NET Version */
            var sb = new StringBuilder();
            sb.AppendLine(PadBoth("", 70, '-'));
            sb.AppendLine(PadBoth(CoreUtil.Title, 70, '-'));
            sb.AppendLine(PadBoth(string.Format("Version {0}", CoreUtil.Version), 70, '-'));
            sb.AppendLine(PadBoth(DateTime.Now.ToString("s"), 70, '-'));
            sb.AppendLine(PadBoth("", 70, '-'));
            try {
                sb.AppendLine(PadBoth("{0}", 70, '-', RuntimeInformation.OSDescription));
                sb.AppendLine(PadBoth("OS Architecture {0}", 70, '-', RuntimeInformation.OSArchitecture.ToString()));
                sb.AppendLine(PadBoth("Process Architecture {0}", 70, '-', RuntimeInformation.ProcessArchitecture.ToString()));
                sb.AppendLine(PadBoth("{0}", 70, '-', RuntimeInformation.FrameworkDescription));
                sb.AppendLine(PadBoth("", 70, '-'));
                sb.AppendLine(PadBoth("Processor Count {0}", 70, '-', Environment.ProcessorCount.ToString()));
            } catch {
                sb.AppendLine(PadBoth("Unable to determine OS information", 70, '-'));
            }

            try {
                sb.AppendLine(PadBoth("Total Physical Memory {0} GB", 70, '-', Math.Round(GetTotalPhysicalMemory() / 1024d / 1024d / 1024d, 2).ToString()));
            } catch {
                sb.AppendLine(PadBoth("Unable to determine Physical Memory", 70, '-'));
            }

            sb.AppendLine(PadBoth("", 70, '-'));
            sb.Append("DATE|LEVEL|SOURCE|MEMBER|LINE|MESSAGE");
            return sb.ToString();
        }

        private static long GetTotalPhysicalMemory() {
            var lines = System.IO.File.ReadAllLines("/proc/meminfo");
            var memTotalLine = lines.FirstOrDefault(l => l.StartsWith("MemTotal:"));
            if (memTotalLine != null) {
                var parts = memTotalLine.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (long.TryParse(parts[1], out long kb)) {
                    return kb * 1024;
                }
            }

            return 0; // Return 0 if the information could not be retrieved
        }

        private static string PadBoth(string msg, int length, char paddingChar, params string[] msgParams) {
            var source = string.Format(msg, msgParams);
            int spaces = length - source.Length;
            int padLeft = spaces / 2 + source.Length;
            return source.PadLeft(padLeft, paddingChar).PadRight(length, paddingChar);
        }

        public static void SetLogLevel(LogLevelEnum logLevel) {
            levelSwitch.MinimumLevel = logLevel switch {
                LogLevelEnum.TRACE => LogEventLevel.Verbose,
                LogLevelEnum.DEBUG => LogEventLevel.Debug,
                LogLevelEnum.INFO => LogEventLevel.Information,
                LogLevelEnum.WARNING => LogEventLevel.Warning,
                LogLevelEnum.ERROR => LogEventLevel.Error,
                _ => LogEventLevel.Information,
            };
        }

        public static bool IsEnabled(LogLevelEnum level) {
            var serilogLevel = level switch {
                LogLevelEnum.TRACE => LogEventLevel.Verbose,
                LogLevelEnum.DEBUG => LogEventLevel.Debug,
                LogLevelEnum.INFO => LogEventLevel.Information,
                LogLevelEnum.WARNING => LogEventLevel.Warning,
                LogLevelEnum.ERROR => LogEventLevel.Error,
                _ => LogEventLevel.Information,
            };

            return Log.IsEnabled(serilogLevel);
        }

        public static void CloseAndFlush() {
            Log.CloseAndFlush();
        }

        private const string ErrorTemplate = "{source}|{member}|{line}";
        private const string MessageTemplate = "{source}|{member}|{line}|{message}";

        public static void Error(
                Exception ex,
                [CallerMemberName] string memberName = "",
                [CallerFilePath] string sourceFilePath = "",
                [CallerLineNumber] int lineNumber = 0) {
            Log.Error(ex, ErrorTemplate, Path.GetFileName(sourceFilePath), memberName, lineNumber);
        }

        public static void Error(
                string customMsg,
                Exception ex,
                [CallerMemberName] string memberName = "",
                [CallerFilePath] string sourceFilePath = "",
                [CallerLineNumber] int lineNumber = 0) {
            Log.Error(ex, MessageTemplate, Path.GetFileName(sourceFilePath), memberName, lineNumber, customMsg);
        }

        public static void Error(string message,
                [CallerMemberName] string memberName = "",
                [CallerFilePath] string sourceFilePath = "",
                [CallerLineNumber] int lineNumber = 0) {
            Log.Error(MessageTemplate, Path.GetFileName(sourceFilePath), memberName, lineNumber, message);
        }

        public static void Warning(string message,
                [CallerMemberName] string memberName = "",
                [CallerFilePath] string sourceFilePath = "",
                [CallerLineNumber] int lineNumber = 0) {
            Log.Warning(MessageTemplate, Path.GetFileName(sourceFilePath), memberName, lineNumber, message);
        }

        public static void Info(string message,
                [CallerMemberName] string memberName = "",
                [CallerFilePath] string sourceFilePath = "",
                [CallerLineNumber] int lineNumber = 0) {
            Log.Information(MessageTemplate, Path.GetFileName(sourceFilePath), memberName, lineNumber, message);
        }

        public static void Debug(string message,
                [CallerMemberName] string memberName = "",
                [CallerFilePath] string sourceFilePath = "",
                [CallerLineNumber] int lineNumber = 0) {
            Log.Debug(MessageTemplate, Path.GetFileName(sourceFilePath), memberName, lineNumber, message);
        }

        public static void Trace(string message,
                [CallerMemberName] string memberName = "",
                [CallerFilePath] string sourceFilePath = "",
                [CallerLineNumber] int lineNumber = 0) {
            Log.Verbose(MessageTemplate, Path.GetFileName(sourceFilePath), memberName, lineNumber, message);
        }

        private class HeaderWriter : FileLifecycleHooks {

            // Factory method to generate the file header
            private readonly Func<string> headerFactory;

            public HeaderWriter(Func<string> headerFactory) {
                this.headerFactory = headerFactory;
            }

            public override Stream OnFileOpened(Stream underlyingStream, Encoding encoding) {
                using (var writer = new StreamWriter(underlyingStream, encoding, 1024, true)) {
                    var header = this.headerFactory();

                    writer.WriteLine(header);
                    writer.Flush();
                    underlyingStream.Flush();
                }

                return base.OnFileOpened(underlyingStream, encoding);
            }
        }

        private class LegacyLogLevelMappingEnricher : ILogEventEnricher {
            private static readonly string LEGACYLOGLEVELPROPERTY = "LegacyLogLevel";
            private static readonly string COLOREDLEGACYLOGLEVELPROPERTY = "LegacyLogLevelConsole";
            private static readonly string TRACE = LogLevelEnum.TRACE.ToString();
            private static readonly string DEBUG = LogLevelEnum.DEBUG.ToString();
            private static readonly string INFO = LogLevelEnum.INFO.ToString();
            private static readonly string WARNING = LogLevelEnum.WARNING.ToString();
            private static readonly string ERROR = LogLevelEnum.ERROR.ToString();
            private static readonly string FATAL = "FATAL";
            private static readonly string UNKNOWN = "UNKNOWN";

            public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory) {

                var legacyLogLevel = logEvent.Level switch {
                    LogEventLevel.Verbose => TRACE,
                    LogEventLevel.Debug => DEBUG,
                    LogEventLevel.Information => INFO,
                    LogEventLevel.Warning => WARNING,
                    LogEventLevel.Error => ERROR,
                    LogEventLevel.Fatal => FATAL,
                    _ => UNKNOWN
                };
                logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty(LEGACYLOGLEVELPROPERTY, legacyLogLevel));

                var coloredLevel = logEvent.Level switch {
                    LogEventLevel.Warning => "\x1b[33mWARNING\x1b[0m",
                    LogEventLevel.Error => "\x1b[31mERROR\x1b[0m",
                    LogEventLevel.Fatal => "\x1b[31mFATAL\x1b[0m",
                    _ => legacyLogLevel
                };
                logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty(COLOREDLEGACYLOGLEVELPROPERTY, coloredLevel));
            }
        }
    }
}
