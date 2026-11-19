#region "copyright"

/*
    Copyright © 2026 Nico Trost <nico.trost57@gmail.com> and the PI.N.S. contributors

    This file is part of PI 'N' Stars.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.INDI;
using NUnit.Framework;
using System;
using System.IO;

namespace NINA.Test.INDI {

    /// <summary>
    /// The driver lookup before a "start": it finds a driver where indiserver's execlp would, so a driver that is not
    /// installed is reported instead of being started in vain.
    /// </summary>
    [TestFixture]
    public class INDIDriverInstalledTest {
        private string dir = null!;

        [SetUp]
        public void SetUp() {
            dir = Path.Combine(Path.GetTempPath(), "pins-indi-driver-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
        }

        [TearDown]
        public void TearDown() {
            Directory.Delete(dir, recursive: true);
        }

        [Test]
        public void ExecutableOnThePath_IsInstalled() {
            Executable("indi_fake_ccd");

            Assert.That(INDIClient.IsDriverInstalled("indi_fake_ccd", "/nonexistent:" + dir), Is.True);
        }

        [Test]
        public void MissingDriver_IsNotInstalled() {
            Assert.That(INDIClient.IsDriverInstalled("indi_fake_ccd", dir), Is.False);
        }

        [Test]
        public void FileWithoutExecuteBit_IsNotInstalled() {
            if (OperatingSystem.IsWindows()) {
                Assert.Ignore("execute bits are a Unix thing");
            }

            File.WriteAllText(Path.Combine(dir, "indi_fake_ccd"), "");
            File.SetUnixFileMode(Path.Combine(dir, "indi_fake_ccd"), UnixFileMode.UserRead | UnixFileMode.UserWrite);

            Assert.That(INDIClient.IsDriverInstalled("indi_fake_ccd", dir), Is.False);
        }

        [Test]
        public void OnlyTheFirstWord_IsLookedUp() {
            Executable("indi_fake_ccd");

            Assert.That(INDIClient.IsDriverInstalled("  indi_fake_ccd -v", dir), Is.True);
        }

        [Test]
        public void NameWithAPath_IsCheckedAsItIs() {
            string driver = Executable("indi_fake_ccd");

            Assert.That(INDIClient.IsDriverInstalled(driver, "/nonexistent"), Is.True);
            Assert.That(INDIClient.IsDriverInstalled(Path.Combine(dir, "indi_other_ccd"), dir), Is.False);
        }

        [Test]
        public void WithoutAPath_BinAndUsrBinAreSearched() {
            if (OperatingSystem.IsWindows()) {
                Assert.Ignore("execlp's default search path is a Unix thing");
            }

            Assert.That(INDIClient.IsDriverInstalled("sh", null), Is.True, "/bin/sh or /usr/bin/sh");
            Assert.That(INDIClient.IsDriverInstalled("indi_surely_not_installed_ccd", null), Is.False);
        }

        [Test]
        public void DriverOfAChainedServer_IsNotChecked() {
            Assert.That(INDIClient.IsDriverInstalled("Telescope Simulator@remotehost:7624", dir), Is.True);
        }

        private string Executable(string name) {
            string file = Path.Combine(dir, name);
            File.WriteAllText(file, "#!/bin/sh\n");

            if (!OperatingSystem.IsWindows()) {
                File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            return file;
        }
    }
}
