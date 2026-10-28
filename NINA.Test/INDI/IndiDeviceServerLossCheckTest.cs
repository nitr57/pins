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
using NINA.Equipment.Equipment;
using NINA.INDI;
using NINA.INDI.Interfaces;
using NUnit.Framework;
using System;
using System.Reflection;

namespace NINA.Test.INDI {

    /// <summary>
    /// IndiDevice.ThrowIfServerConnectionLost, which IndiTelescope calls after waits that a lost INDI server ends early
    /// (setting the pier side, a slew without async support): it fails the operation when the device lost the server,
    /// and also when the Connected poll's TryReconnect has disposed that device during the wait.
    /// </summary>
    [TestFixture]
    public class IndiDeviceServerLossCheckTest {

        private sealed class TestIndiDevice : IndiDevice<IINDIDevice> {

            public TestIndiDevice() : base(new INDIDeviceInfo { Id = "Test" }) {
            }

            protected override string ConnectionLostMessage => "lost";

            protected override IINDIDevice GetInstance() => throw new NotSupportedException();

            public void Check() => ThrowIfServerConnectionLost("Slew");
        }

        private static TestIndiDevice CreateSUT(IINDIDevice? device) {
            var sut = new TestIndiDevice();
            typeof(IndiDevice<IINDIDevice>)
                .GetField("device", BindingFlags.NonPublic | BindingFlags.Instance)!
                .SetValue(sut, device);
            return sut;
        }

        [Test]
        public void Throws_WhenTheDeviceLostTheServer() {
            var device = new Mock<IINDIDevice>();
            device.SetupGet(d => d.ServerConnectionLost).Returns(true);

            Assert.That(() => CreateSUT(device.Object).Check(),
                Throws.InvalidOperationException.With.Message.Contains("connection to the INDI server was lost"));
        }

        [Test]
        public void Throws_WhenTheDeviceWasDisposedDuringTheWait() {
            Assert.That(() => CreateSUT(null).Check(),
                Throws.InvalidOperationException.With.Message.Contains("disconnected"),
                "Slewing reads false without a device, which must not count as a finished slew");
        }

        [Test]
        public void DoesNotThrow_WhileTheServerConnectionHolds() {
            var device = new Mock<IINDIDevice>();
            device.SetupGet(d => d.ServerConnectionLost).Returns(false);

            Assert.That(() => CreateSUT(device.Object).Check(), Throws.Nothing);
        }
    }
}
