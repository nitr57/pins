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
using NINA.Equipment.Equipment.MyFilterWheel;
using NINA.INDI;
using NINA.INDI.Interfaces;
using NUnit.Framework;
using System.Reflection;

namespace NINA.Test.FilterWheel {

    /// <summary>
    /// A filter change that the lost INDI server connection ends returns from the Position setter, so FilterWheelVM
    /// still resumes the guiding it stopped, and the position then reads as no slot (-2): not -1, which the VM waits
    /// on, and not the slot 0 that a device without properties reads. That holds until the next change, also once
    /// the lost device was disposed.
    /// </summary>
    [TestFixture]
    public class IndiFilterWheelServerLossTest {
        private Mock<IINDIFilterWheel> mockDevice = null!;
        private bool serverConnectionLost;
        private int devicePosition;

        [SetUp]
        public void Init() {
            serverConnectionLost = false;
            devicePosition = 1;
            mockDevice = new Mock<IINDIFilterWheel>();
            mockDevice.SetupGet(d => d.IsMoving).Returns(false);
            mockDevice.SetupGet(d => d.ServerConnectionLost).Returns(() => serverConnectionLost);
            mockDevice.SetupGet(d => d.Position).Returns(() => devicePosition);
            mockDevice.SetupSet(d => d.Position = It.IsAny<int>()).Callback<int>(slot => {
                if (!serverConnectionLost) {
                    devicePosition = slot;
                }
            });
        }

        // As in IndiRotatorTest: IndiFilterWheel only gets its INDI device and connected expectation through
        // Connect(), which needs a live INDI server
        private IndiFilterWheel CreateConnectedSUT() {
            var sut = new IndiFilterWheel(new INDIDeviceInfo { Id = "Test" }, null);

            SetDevice(sut, mockDevice.Object);
            typeof(IndiDevice<IINDIFilterWheel>)
                .GetField("connectedExpectation", BindingFlags.NonPublic | BindingFlags.Instance)!
                .SetValue(sut, true);

            return sut;
        }

        private static void SetDevice(IndiFilterWheel sut, IINDIFilterWheel? device) {
            typeof(IndiDevice<IINDIFilterWheel>)
                .GetField("device", BindingFlags.NonPublic | BindingFlags.Instance)!
                .SetValue(sut, device);
        }

        [Test]
        public void AChangeEndedByTheLostServer_Returns_AndReadsAsNoSlot() {
            var sut = CreateConnectedSUT();
            serverConnectionLost = true;
            devicePosition = 0;

            Assert.That(() => sut.Position = 2, Throws.Nothing, "FilterWheelVM must get to resuming the guiding");
            Assert.That(sut.Position, Is.EqualTo(-2));
        }

        [Test]
        public void AChangeEndedByTheLostServer_StillReadsAsNoSlot_AfterTheDeviceWasDisposed() {
            var sut = CreateConnectedSUT();
            serverConnectionLost = true;
            sut.Position = 2;

            // The Connected poll's TryReconnect disposes a device that lost the server
            SetDevice(sut, null);

            Assert.That(sut.Position, Is.EqualTo(-2), "-1 would keep FilterWheelVM waiting for its 5 minute timeout");
        }

        [Test]
        public void TheNextChange_ReadsTheDevicesPositionAgain() {
            var sut = CreateConnectedSUT();
            serverConnectionLost = true;
            sut.Position = 2;

            serverConnectionLost = false;
            sut.Position = 3;

            Assert.That(sut.Position, Is.EqualTo(3));
        }

        [Test]
        public void AChangeWithTheServerConnected_ReadsTheDevicesPosition() {
            var sut = CreateConnectedSUT();

            sut.Position = 2;

            Assert.That(sut.Position, Is.EqualTo(2));
        }
    }
}
