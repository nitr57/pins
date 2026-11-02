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
using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Test.Equipment {

    /// <summary>
    /// A refused or timed out goto during an INDI meridian flip is retried, as the flip's retry loop intends (a mount
    /// refuses the flip goto while the meridian limit isn't passed yet). SlewToCoordinates throws on those since a
    /// failed slew must not count as done, and the flip used to end at the first refusal.
    /// </summary>
    [TestFixture]
    public class IndiTelescopeMeridianFlipTest {

        private const int Attempts = 21; // MERIDIAN_FLIP_SLEW_RETRY_ATTEMPTS retries after the first attempt

        private sealed class ConnectedIndiTelescope : IndiTelescope {
            public readonly Mock<IINDITelescope> Fake = new();

            public bool DeviceConnected = true;

            public ConnectedIndiTelescope()
                : base(new INDIDeviceInfo { Id = "Mount", Name = "Mount", Interface = DeviceInterface.TELESCOPE_INTERFACE },
                      new Mock<IProfileService> { DefaultValue = DefaultValue.Mock }.Object) {
                Fake.SetupGet(d => d.Connected).Returns(() => DeviceConnected);
                Fake.SetupGet(d => d.Tracking).Returns(true);
                Fake.SetupGet(d => d.AtPark).Returns(false);
                Fake.SetupGet(d => d.SiderealTime).Returns(6d);
                Fake.SetupGet(d => d.RightAscension).Returns(5d);
                Fake.SetupGet(d => d.Declination).Returns(20d);
                Fake.SetupGet(d => d.SideOfPier).Returns(PierSide.pierUnknown);
                device = Fake.Object;
                typeof(NINA.Equipment.Equipment.IndiDevice<IINDITelescope>).GetField("connectedExpectation", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(this, true);
                MeridianFlipSlewRetryWait = TimeSpan.Zero;
            }
        }

        private static readonly Coordinates Target = new Coordinates(Angle.ByHours(7), Angle.ByDegree(20), Epoch.JNOW);

        [Test]
        public async Task ARefusedFlipGoto_IsRetried() {
            var telescope = new ConnectedIndiTelescope();
            telescope.Fake.SetupSequence(d => d.SlewToCoordinatesTaskAsync(It.IsAny<double>(), It.IsAny<double>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("Slew rejected by mount"))
                .Returns(Task.CompletedTask);

            var success = await telescope.MeridianFlip(Target, CancellationToken.None);

            Assert.That(success, Is.True);
            telescope.Fake.Verify(d => d.SlewToCoordinatesTaskAsync(It.IsAny<double>(), It.IsAny<double>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        }

        [Test]
        public async Task AFlipGotoThatAlwaysTimesOut_FailsAfterAllRetries() {
            var telescope = new ConnectedIndiTelescope();
            telescope.Fake.Setup(d => d.SlewToCoordinatesTaskAsync(It.IsAny<double>(), It.IsAny<double>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new TimeoutException("The mount did not report the end of the slew"));

            var success = await telescope.MeridianFlip(Target, CancellationToken.None);

            Assert.That(success, Is.False);
            telescope.Fake.Verify(d => d.SlewToCoordinatesTaskAsync(It.IsAny<double>(), It.IsAny<double>(), It.IsAny<CancellationToken>()), Times.Exactly(Attempts));
        }

        [Test]
        public async Task AFlipGotoFailingWithTheConnection_IsNotRetried() {
            var telescope = new ConnectedIndiTelescope();
            telescope.Fake.Setup(d => d.SlewToCoordinatesTaskAsync(It.IsAny<double>(), It.IsAny<double>(), It.IsAny<CancellationToken>()))
                .Callback(() => telescope.DeviceConnected = false)
                .ThrowsAsync(new InvalidOperationException("Slew failed: the connection to the INDI server was lost"));

            var success = await telescope.MeridianFlip(Target, CancellationToken.None);

            Assert.That(success, Is.False);
            telescope.Fake.Verify(d => d.SlewToCoordinatesTaskAsync(It.IsAny<double>(), It.IsAny<double>(), It.IsAny<CancellationToken>()), Times.Once);
        }
    }
}
