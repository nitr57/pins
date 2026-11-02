#region "copyright"

/*
    Copyright © 2026 Nico Trost <nico.trost57@gmail.com> and the PI.N.S. contributors

    This file is part of PI 'N' Stars.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Moq;
using NINA.Equipment.Equipment.MyTelescope;
using NINA.INDI;
using NINA.INDI.Enums;
using NINA.Profile.Interfaces;
using NUnit.Framework;
using System.Reflection;

namespace NINA.Test.Equipment {

    /// <summary>
    /// Before INDITelescope converted INDI's 0..360° east longitude, a location sync "to application" wrote a site west
    /// of Greenwich into the profile as e.g. 355. Connecting an INDI mount puts such a value back into -180..180, so a
    /// sync "to telescope" no longer reports a failure at every connect.
    /// </summary>
    [TestFixture]
    public class IndiTelescopeProfileLongitudeTest {

        private static (IndiTelescope Telescope, Mock<IProfileService> Profile) Create(double longitude) {
            var profile = new Mock<IProfileService> { DefaultValue = DefaultValue.Mock };
            profile.SetupGet(p => p.ActiveProfile.AstrometrySettings.Longitude).Returns(longitude);
            var telescope = new IndiTelescope(new INDIDeviceInfo { Id = "Mount", Name = "Mount", Interface = DeviceInterface.TELESCOPE_INTERFACE }, profile.Object);
            return (telescope, profile);
        }

        private static void Normalize(IndiTelescope telescope) {
            typeof(IndiTelescope).GetMethod("NormalizeProfileLongitude", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(telescope, null);
        }

        [Test]
        public void ALongitudeInIndisForm_IsPutBackIntoTheProfilesRange() {
            var (telescope, profile) = Create(355);

            Normalize(telescope);

            profile.Verify(p => p.ChangeLongitude(It.Is<double>(l => System.Math.Abs(l - -5) < 1e-9)), Times.Once);
        }

        [TestCase(-5)]
        [TestCase(180)]
        [TestCase(-180)]
        [TestCase(0)]
        public void ALongitudeInTheProfilesRange_IsLeftAlone(double longitude) {
            var (telescope, profile) = Create(longitude);

            Normalize(telescope);

            profile.Verify(p => p.ChangeLongitude(It.IsAny<double>()), Times.Never);
        }
    }
}
