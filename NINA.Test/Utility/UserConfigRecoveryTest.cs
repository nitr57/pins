#region "copyright"

/*
    Copyright © 2026 Nico Trost <nico.trost57@gmail.com> and the PI.N.S. contributors

    This file is part of PI 'N' Stars.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Utility;
using NUnit.Framework;
using System;
using System.IO;

namespace NINA.Test.Utility {

    /// <summary>
    /// The plugins' shared user.config is backed up at start, and an unreadable one (empty or cut off by a power
    /// cut) is moved aside and replaced by that backup, so plugins such as ninaAPI and Touch-N-Stars still load.
    /// </summary>
    [TestFixture]
    public class UserConfigRecoveryTest {
        private const string ValidConfig =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n"
            + "<configuration>\n"
            + "    <configSections>\n"
            + "        <sectionGroup name=\"userSettings\">\n"
            + "            <section name=\"ninaAPI.Properties.Settings\" type=\"System.Configuration.ClientSettingsSection, System.Configuration.ConfigurationManager, Version=10.0.0.0, Culture=neutral, PublicKeyToken=cc7b13ffcd2ddd51\" allowExeDefinition=\"MachineToLocalUser\" requirePermission=\"false\" />\n"
            + "        </sectionGroup>\n"
            + "    </configSections>\n"
            + "    <userSettings>\n"
            + "        <ninaAPI.Properties.Settings>\n"
            + "            <setting name=\"Port\" serializeAs=\"String\">\n"
            + "                <value>1888</value>\n"
            + "            </setting>\n"
            + "        </ninaAPI.Properties.Settings>\n"
            + "    </userSettings>\n"
            + "</configuration>\n";

        private string dir = null!;
        private string path = null!;

        [SetUp]
        public void SetUp() {
            dir = Path.Combine(Path.GetTempPath(), "pins-userconfig-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            path = Path.Combine(dir, "user.config");
        }

        [TearDown]
        public void TearDown() {
            if (Directory.Exists(dir)) {
                Directory.Delete(dir, recursive: true);
            }
        }

        [Test]
        public void IsReadable_AcceptsAUserConfig_AndRejectsAnEmptyOrCutOffOne() {
            File.WriteAllText(path, ValidConfig);
            Assert.That(UserConfigRecovery.IsReadable(path, out _), Is.True);

            File.WriteAllText(path, "");
            Assert.That(UserConfigRecovery.IsReadable(path, out _), Is.False);

            File.WriteAllText(path, ValidConfig[..(ValidConfig.Length / 2)]);
            Assert.That(UserConfigRecovery.IsReadable(path, out var reason), Is.False);
            Assert.That(reason, Is.Not.Empty);
        }

        [Test]
        public void Recover_BacksUpAReadableFile_AndLeavesItAsItIs() {
            File.WriteAllText(path, ValidConfig);

            UserConfigRecovery.Recover(path);

            Assert.That(File.ReadAllText(path), Is.EqualTo(ValidConfig));
            Assert.That(File.ReadAllText(UserConfigRecovery.BackupPath(path)), Is.EqualTo(ValidConfig));
            Assert.That(File.Exists(UserConfigRecovery.BackupPath(path) + ".tmp"), Is.False);
        }

        [Test]
        public void Recover_ReplacesACutOffFile_WithItsBackup_AndKeepsTheBrokenOneAside() {
            File.WriteAllText(path, ValidConfig);
            UserConfigRecovery.Recover(path);
            var cutOff = ValidConfig[..(ValidConfig.Length / 2)];
            File.WriteAllText(path, cutOff);

            UserConfigRecovery.Recover(path);

            Assert.That(File.ReadAllText(path), Is.EqualTo(ValidConfig));
            var aside = Directory.GetFiles(dir, "user.config.unreadable-*");
            Assert.That(aside, Has.Length.EqualTo(1));
            Assert.That(File.ReadAllText(aside[0]), Is.EqualTo(cutOff));
        }

        [Test]
        public void Recover_WithoutABackup_LeavesNoFile_SoThePluginsStartWithTheirDefaults() {
            File.WriteAllText(path, "");

            UserConfigRecovery.Recover(path);

            Assert.That(File.Exists(path), Is.False);
            Assert.That(Directory.GetFiles(dir, "user.config.unreadable-*"), Has.Length.EqualTo(1));
        }

        [Test]
        public void Recover_WithAnUnreadableBackup_LeavesNoFile() {
            File.WriteAllText(path, "");
            File.WriteAllText(UserConfigRecovery.BackupPath(path), "<configuration>");

            UserConfigRecovery.Recover(path);

            Assert.That(File.Exists(path), Is.False);
        }

        [Test]
        public void Recover_DoesNothing_WhenThereIsNoFile() {
            UserConfigRecovery.Recover(path);

            Assert.That(Directory.GetFiles(dir), Is.Empty);
        }
    }
}
