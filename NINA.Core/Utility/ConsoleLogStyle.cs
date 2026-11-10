#region "copyright"

/*
    Copyright © 2026 Nico Trost <nico.trost57@gmail.com> and the PI.N.S. contributors

    This file is part of PI 'N' Stars.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Serilog.Events;

namespace NINA.Core.Utility {

    /// <summary>Where the log's console output goes.</summary>
    public enum ConsoleLogTarget {
        /// <summary>A terminal: coloured levels, everything the log level lets through.</summary>
        Terminal,

        /// <summary>A pipe or file: the same lines without colour codes.</summary>
        Redirected,

        /// <summary>
        /// journald (pins.service): the journal adds its own timestamp and keeps the log level as the line's priority
        /// (sd-daemon prefix), so <c>journalctl -p warning</c> finds NINA's warnings and errors. Debug and trace
        /// lines go to the log file only, so a raised log level doesn't write everything twice to the SD card.
        /// </summary>
        Journal,
    }

    public static class ConsoleLogStyle {
        public const string TerminalTemplate = "{Timestamp:yyyy-MM-ddTHH:mm:ss.ffff}|{LegacyLogLevelConsole}|{Message:lj}{NewLine}{Exception}";
        public const string RedirectedTemplate = "{Timestamp:yyyy-MM-ddTHH:mm:ss.ffff}|{LegacyLogLevel}|{Message:lj}{NewLine}{Exception}";
        public const string JournalTemplate = "{SdPriority}{LegacyLogLevel}|{Message:lj}{NewLine}{Exception}";

        /// <param name="journalStream">
        /// The JOURNAL_STREAM variable ("device:inode") systemd sets when stdout or stderr is the journal.
        /// </param>
        /// <param name="outputRedirected">Whether stdout is not a terminal.</param>
        /// <param name="stdoutLink">Where stdout leads (the link of /proc/self/fd/1, e.g. "socket:[12345]"), or null.</param>
        public static ConsoleLogTarget Choose(string journalStream, bool outputRedirected, string stdoutLink) {
            if (!outputRedirected) {
                // also a terminal that inherited JOURNAL_STREAM, e.g. from a user service
                return ConsoleLogTarget.Terminal;
            }

            // Every child of a journald-logged service inherits JOURNAL_STREAM (a shell in GNOME Terminal, an IDE, tmux),
            // so it counts only when stdout is that stream (systemd.exec(5), "$JOURNAL_STREAM")
            return IsJournal(journalStream, stdoutLink) ? ConsoleLogTarget.Journal : ConsoleLogTarget.Redirected;
        }

        private static bool IsJournal(string journalStream, string stdoutLink) {
            if (string.IsNullOrEmpty(journalStream) || string.IsNullOrEmpty(stdoutLink)) {
                return false;
            }

            int colon = journalStream.IndexOf(':');
            string inode = colon >= 0 ? journalStream[(colon + 1)..] : string.Empty;

            return inode.Length > 0 && stdoutLink == $"socket:[{inode}]";
        }

        public static string Template(ConsoleLogTarget target) {
            return target switch {
                ConsoleLogTarget.Journal => JournalTemplate,
                ConsoleLogTarget.Redirected => RedirectedTemplate,
                _ => TerminalTemplate,
            };
        }

        public static LogEventLevel MinimumLevel(ConsoleLogTarget target) {
            return target == ConsoleLogTarget.Journal ? LogEventLevel.Information : LogEventLevel.Verbose;
        }

        /// <summary>
        /// The sd-daemon priority prefix journald reads from the start of a line (sd-daemon.h). Only the first line of an
        /// event carries it: the lines of a stack trace get the stream's default (info), so <c>journalctl -p err</c>
        /// shows an error without its trace, while <c>journalctl -u pins</c> has all of it.
        /// </summary>
        public static string SdPriority(LogEventLevel level) {
            return level switch {
                LogEventLevel.Fatal => "<2>",
                LogEventLevel.Error => "<3>",
                LogEventLevel.Warning => "<4>",
                LogEventLevel.Information => "<6>",
                _ => "<7>",
            };
        }
    }
}
