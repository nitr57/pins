#region "copyright"

/*
    Copyright © 2026 Nico Trost <nico.trost57@gmail.com> and the PI.N.S. contributors

    This file is part of PI 'N' Stars.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Core.Utility;
using NUnit.Framework;
using Serilog.Events;

namespace NINA.Test.Utility {

    /// <summary>
    /// The log's console output: coloured in a terminal, plain when redirected, and for journald (pins.service) with
    /// the sd-daemon priority, no own timestamp and nothing below Information, which the log file has.
    /// </summary>
    [TestFixture]
    public class ConsoleLogStyleTest {

        [TestCase(null, false, null, ConsoleLogTarget.Terminal)]
        [TestCase("8:12345", false, "socket:[12345]", ConsoleLogTarget.Terminal)]
        [TestCase(null, true, "pipe:[777]", ConsoleLogTarget.Redirected)]
        [TestCase("", true, "socket:[12345]", ConsoleLogTarget.Redirected)]
        [TestCase("8:12345", true, "socket:[12345]", ConsoleLogTarget.Journal)]
        public void Choose_TheTargetFromTheEnvironment(string? journalStream, bool redirected, string? stdoutLink, ConsoleLogTarget expected) {
            Assert.That(ConsoleLogStyle.Choose(journalStream!, redirected, stdoutLink!), Is.EqualTo(expected));
        }

        [TestCase("pipe:[99999]")]
        [TestCase("/home/pi/nina.log")]
        [TestCase("socket:[123456]")]
        [TestCase(null)]
        public void AnInheritedJournalStream_IsNotTheJournal_WhenStdoutLeadsElsewhere(string? stdoutLink) {
            // e.g. `./NINA | tee x.log` in a terminal that inherited JOURNAL_STREAM from a user service
            Assert.That(ConsoleLogStyle.Choose("8:12345", true, stdoutLink!), Is.EqualTo(ConsoleLogTarget.Redirected));
        }

        [Test]
        public void OnlyTheTerminal_GetsColouredLevels() {
            Assert.That(ConsoleLogStyle.Template(ConsoleLogTarget.Terminal), Does.Contain("{LegacyLogLevelConsole}"));
            Assert.That(ConsoleLogStyle.Template(ConsoleLogTarget.Redirected), Does.Not.Contain("LegacyLogLevelConsole"));
            Assert.That(ConsoleLogStyle.Template(ConsoleLogTarget.Journal), Does.Not.Contain("LegacyLogLevelConsole"));
        }

        [Test]
        public void TheJournal_GetsThePriorityFirst_AndNoTimestamp() {
            var template = ConsoleLogStyle.Template(ConsoleLogTarget.Journal);

            Assert.That(template, Does.StartWith("{SdPriority}"));
            Assert.That(template, Does.Not.Contain("Timestamp"));
        }

        [TestCase(ConsoleLogTarget.Terminal, LogEventLevel.Verbose)]
        [TestCase(ConsoleLogTarget.Redirected, LogEventLevel.Verbose)]
        [TestCase(ConsoleLogTarget.Journal, LogEventLevel.Information)]
        public void TheJournal_LeavesDebugAndTraceToTheLogFile(ConsoleLogTarget target, LogEventLevel expected) {
            Assert.That(ConsoleLogStyle.MinimumLevel(target), Is.EqualTo(expected));
        }

        [TestCase(LogEventLevel.Fatal, "<2>")]
        [TestCase(LogEventLevel.Error, "<3>")]
        [TestCase(LogEventLevel.Warning, "<4>")]
        [TestCase(LogEventLevel.Information, "<6>")]
        [TestCase(LogEventLevel.Debug, "<7>")]
        [TestCase(LogEventLevel.Verbose, "<7>")]
        public void SdPriority_FollowsSdDaemon(LogEventLevel level, string expected) {
            Assert.That(ConsoleLogStyle.SdPriority(level), Is.EqualTo(expected));
        }
    }
}
