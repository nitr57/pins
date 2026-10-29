#region "copyright"

/*
    Copyright © 2026 Nico Trost <nico.trost57@gmail.com> and the PI.N.S. contributors

    This file is part of PI 'N' Stars.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Properties;
using NUnit.Framework;
using System;
using System.IO;

namespace NINA.Test.Utility {

    /// <summary>
    /// The global app settings (Settings.xml) are saved atomically with a backup of the previous file, and loading falls
    /// back to that backup, so a power cut during a save can't silently reset them to the defaults.
    /// </summary>
    [TestFixture]
    public class AppSettingsFileTest {
        private string dir = null!;
        private string path = null!;

        [SetUp]
        public void SetUp() {
            dir = Path.Combine(Path.GetTempPath(), "pins-settings-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            path = Path.Combine(dir, "Settings.xml");
        }

        [TearDown]
        public void TearDown() {
            if (Directory.Exists(dir)) {
                Directory.Delete(dir, recursive: true);
            }
        }

        [Test]
        public void SaveTo_ThenLoadFrom_KeepsTheValues_AndLeavesNoTemporaryFile() {
            new Settings { SaveQueueSize = 7 }.SaveTo(path);

            Assert.That(Settings.LoadFrom(path).SaveQueueSize, Is.EqualTo(7));
            Assert.That(File.Exists(path + ".tmp"), Is.False);
        }

        [Test]
        public void SaveTo_KeepsThePreviousFileAsBackup() {
            new Settings { SaveQueueSize = 7 }.SaveTo(path);

            new Settings { SaveQueueSize = 9 }.SaveTo(path);

            Assert.That(Settings.LoadFrom(path).SaveQueueSize, Is.EqualTo(9));
            Assert.That(Settings.LoadFrom(path + ".bkp").SaveQueueSize, Is.EqualTo(7));
        }

        [Test]
        public void LoadFrom_FallsBackToTheBackup_WhenTheFileIsTruncated() {
            new Settings { SaveQueueSize = 7 }.SaveTo(path);
            new Settings { SaveQueueSize = 9 }.SaveTo(path);

            // what an interrupted in-place write left behind
            var text = File.ReadAllText(path);
            File.WriteAllText(path, text.Substring(0, text.Length / 2));

            Assert.That(Settings.LoadFrom(path).SaveQueueSize, Is.EqualTo(7));
        }

        [Test]
        public void LoadFrom_UsesTheDefaults_WhenNeitherFileCanBeRead() {
            File.WriteAllText(path, "<Settings");

            Assert.That(Settings.LoadFrom(path).SaveQueueSize, Is.EqualTo(new Settings().SaveQueueSize));
        }

        [Test]
        public void LoadFrom_UsesTheDefaults_WithoutAFile() {
            Assert.That(Settings.LoadFrom(path).SaveQueueSize, Is.EqualTo(new Settings().SaveQueueSize));
        }
    }
}
