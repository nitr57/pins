#region "copyright"

/*
    Copyright © 2026 Nico Trost <nico.trost57@gmail.com> and the PI.N.S. contributors

    This file is part of PI 'N' Stars.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Image.ImageData;
using NUnit.Framework;

namespace NINA.Test.Image {

    /// <summary>
    /// Only the plate solver's temporary images skip the fsync after a save: files directly in its working directory,
    /// not its Failed folder or any other path.
    /// </summary>
    [TestFixture]
    public class SaveDurabilityTest {
        private const string Temporary = "/home/pi/.local/share/NINA/PlateSolver";

        [TestCase("/home/pi/.local/share/NINA/PlateSolver/abc123.fits", false)]
        [TestCase("/home/pi/.local/share/NINA/PlateSolver/./abc123.fits", false)]
        [TestCase("/home/pi/.local/share/NINA/PlateSolver/Failed/x.fits", true)]
        [TestCase("/home/pi/.local/share/NINA/PlateSolverOther/abc123.fits", true)]
        [TestCase("/home/pi/.local/share/NINA/abc123.fits", true)]
        [TestCase("/home/pi/Pictures/2026-11-10/M31_LIGHT_001.fits", true)]
        [TestCase("/home/pi/.local/share/NINA/platesolver/abc123.fits", true)]
        [TestCase("", true)]
        public void OnlyFilesDirectlyInThePlateSolverDirectory_SkipTheFlush(string path, bool expected) {
            Assert.That(SaveDurability.NeedsFlushToDisk(path, Temporary), Is.EqualTo(expected));
        }

        [Test]
        public void ATrailingSeparatorOnTheDirectory_DoesNotMatter() {
            Assert.That(SaveDurability.NeedsFlushToDisk(Temporary + "/abc123.fits", Temporary + "/"), Is.False);
        }
    }
}
