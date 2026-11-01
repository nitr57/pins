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
using NINA.INDI.Interfaces;
using NINA.Profile.Interfaces;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Reflection;
using IndiTrackingMode = NINA.INDI.Enums.TrackingMode;
using TrackingMode = NINA.Equipment.Interfaces.TrackingMode;

namespace NINA.Test.Equipment {

    /// <summary>
    /// INDI mounts offer no custom tracking rates: the rates never reached the mount, so a comet tracked at the sidereal
    /// rate without an error, and writing TELESCOPE_TRACK_RATE is not safe across drivers. Custom tracking now fails
    /// visibly instead.
    /// </summary>
    [TestFixture]
    public class IndiTelescopeCustomRateTest {

        private sealed class ConnectedIndiTelescope : IndiTelescope {
            public readonly Mock<IINDITelescope> Fake = new();

            public ConnectedIndiTelescope()
                : base(new INDIDeviceInfo { Id = "Mount", Name = "Mount", Interface = DeviceInterface.TELESCOPE_INTERFACE }, Mock.Of<IProfileService>()) {
                Fake.Setup(d => d.GetSupportedTrackingModes()).Returns(new List<IndiTrackingMode> {
                    IndiTrackingMode.Sidereal, IndiTrackingMode.Lunar, IndiTrackingMode.King, IndiTrackingMode.Stopped,
                });
                device = Fake.Object;
                typeof(NINA.Equipment.Equipment.IndiDevice<IINDITelescope>).GetField("connectedExpectation", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(this, true);
            }

            public IList<TrackingMode> LoadTrackingModes() {
                var modes = (ImmutableList<TrackingMode>)typeof(IndiTelescope)
                    .GetMethod("GetTrackingModes", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .Invoke(this, null)!;
                typeof(IndiTelescope).GetField("trackingModes", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(this, modes);
                return modes;
            }
        }

        [Test]
        public void CustomTrackingIsNotOffered() {
            var telescope = new ConnectedIndiTelescope();

            var modes = telescope.LoadTrackingModes();

            Assert.That(modes, Does.Not.Contain(TrackingMode.Custom));
            Assert.That(telescope.CanSetRightAscensionRate, Is.False);
            Assert.That(telescope.CanSetDeclinationRate, Is.False);
        }

        [Test]
        public void SetCustomTrackingRate_FailsInsteadOfTrackingSidereal() {
            var telescope = new ConnectedIndiTelescope();
            telescope.LoadTrackingModes();

            Assert.That(() => telescope.SetCustomTrackingRate(0.1, 2.0), Throws.TypeOf<NotSupportedException>());
            telescope.Fake.Verify(d => d.SetTrackingMode(It.IsAny<IndiTrackingMode>()), Times.Never);
        }
    }
}
