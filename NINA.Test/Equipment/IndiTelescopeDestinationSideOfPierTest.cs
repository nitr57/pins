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
using NINA.Astrometry;
using NINA.Core.Enum;
using NINA.Equipment.Equipment.MyTelescope;
using NINA.INDI;
using NINA.INDI.Enums;
using NINA.INDI.Interfaces;
using NINA.Profile.Interfaces;
using NUnit.Framework;
using System.Reflection;

namespace NINA.Test.Equipment {

    /// <summary>
    /// INDI has no property for the pier side a goto ends on. The INDI mount used to answer with its current side, so the
    /// dome (with "use side of pier") was placed for the wrong side on every goto across the meridian; it now answers with
    /// the side a German mount normally takes for the target's hour angle, from the mount's sidereal time.
    /// </summary>
    [TestFixture]
    public class IndiTelescopeDestinationSideOfPierTest {

        private sealed class ConnectedIndiTelescope : IndiTelescope {
            public readonly Mock<IINDITelescope> Fake = new();

            public ConnectedIndiTelescope(PierSide sideOfPier, double siderealTime)
                : base(new INDIDeviceInfo { Id = "Mount", Name = "Mount", Interface = DeviceInterface.TELESCOPE_INTERFACE },
                      new Mock<IProfileService> { DefaultValue = DefaultValue.Mock }.Object) {
                Fake.SetupGet(d => d.Connected).Returns(true);
                Fake.SetupGet(d => d.SiderealTime).Returns(siderealTime);
                Fake.SetupGet(d => d.SideOfPier).Returns(sideOfPier);
                device = Fake.Object;
                typeof(NINA.Equipment.Equipment.IndiDevice<IINDITelescope>).GetField("connectedExpectation", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(this, true);
            }
        }

        private static Coordinates Target(double raHours) => new(Angle.ByHours(raHours), Angle.ByDegree(20), Epoch.JNOW);

        [TestCase(PierSide.pierEast)]
        [TestCase(PierSide.pierWest)]
        public void ATargetEastOfTheMeridian_EndsOnTheWestSide_WhateverTheCurrentSide(PierSide current) {
            var telescope = new ConnectedIndiTelescope(current, siderealTime: 6);

            Assert.That(telescope.DestinationSideOfPier(Target(9)), Is.EqualTo(PierSide.pierWest));
        }

        [TestCase(PierSide.pierEast)]
        [TestCase(PierSide.pierWest)]
        public void ATargetWestOfTheMeridian_EndsOnTheEastSide_WhateverTheCurrentSide(PierSide current) {
            var telescope = new ConnectedIndiTelescope(current, siderealTime: 6);

            Assert.That(telescope.DestinationSideOfPier(Target(3)), Is.EqualTo(PierSide.pierEast));
        }

        [Test]
        public void TheHourAngleComesFromTheMountsSiderealTime() {
            var telescope = new ConnectedIndiTelescope(PierSide.pierWest, siderealTime: 12);

            Assert.That(telescope.DestinationSideOfPier(Target(9)), Is.EqualTo(PierSide.pierEast));
        }
    }
}
