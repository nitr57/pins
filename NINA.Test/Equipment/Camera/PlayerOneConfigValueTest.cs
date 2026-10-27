#region "copyright"

/*
    Copyright © 2026 Nico Trost <nico.trost57@gmail.com> and the PI.N.S. contributors

    This file is part of PI 'N' Stars.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Equipment.SDK.CameraSDKs.PlayerOneSDK;
using NUnit.Framework;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace NINA.Test.Equipment.Camera {

    /// <summary>
    /// POAConfigValue mirrors the SDK's union { long intValue; double floatValue; POABool boolValue; }.
    /// The C long is 8 bytes on LP64 Linux, so an int written into it has to be sign-extended:
    /// a target temperature of -10 °C must not reach POASetConfig as 4294967286.
    /// </summary>
    [TestFixture]
    public class PlayerOneConfigValueTest {

        [Test]
        public void Layout_MatchesTheCUnion() {
            Assert.That(Marshal.SizeOf<POAConfigValue>(), Is.EqualTo(8));
        }

        [TestCase(-10)]
        [TestCase(-1)]
        [TestCase(0)]
        [TestCase(250)]
        [TestCase(int.MaxValue)]
        [TestCase(int.MinValue)]
        public void IntValue_FillsTheWholeCLong(int written) {
            var value = new POAConfigValue { intValue = written };

            Assert.That(Unsafe.As<POAConfigValue, CLong>(ref value).Value, Is.EqualTo((nint)written));
            Assert.That(value.intValue, Is.EqualTo(written));
        }

        [Test]
        public void IntValue_ReadsALongWrittenByTheSdk() {
            var value = new POAConfigValue();
            Unsafe.As<POAConfigValue, CLong>(ref value) = new CLong(-25);

            Assert.That(value.intValue, Is.EqualTo(-25));
        }
    }
}
