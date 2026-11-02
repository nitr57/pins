#region "copyright"

/*
    Copyright © 2025-2026 Nico Trost <nico.trost57@gmail.com> and the PI.N.S. contributors

    This file is part of PI 'N' Stars.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Moq;
using NINA.Equipment.Equipment;
using NINA.Equipment.Equipment.MyRotator;
using NINA.INDI;
using NINA.INDI.Interfaces;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Test.Rotator {

    [TestFixture]
    public class IndiRotatorTest {
        private Mock<IINDIRotator> mockDevice;
        private float mechanicalPosition;

        [SetUp]
        public void Init() {
            mechanicalPosition = 90f;
            mockDevice = new Mock<IINDIRotator>();
            mockDevice.SetupGet(d => d.MechanicalPosition).Returns(() => mechanicalPosition);
            mockDevice.SetupGet(d => d.Position).Returns(() => mechanicalPosition);
            mockDevice.Setup(d => d.MoveAsync(It.IsAny<float>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
        }

        // IndiRotator only wires up its INDI `device` and its connected-expectation flag through
        // Connect(), which requires a live INDI server. Reach into the private base-class state
        // instead so the Move*/MoveAbsolute* logic can be tested against a mocked IINDIRotator.
        private IndiRotator CreateConnectedSUT() {
            var sut = new IndiRotator(new INDIDeviceInfo { Id = "Test" }, null);

            typeof(IndiDevice<IINDIRotator>)
                .GetField("device", BindingFlags.NonPublic | BindingFlags.Instance)
                .SetValue(sut, mockDevice.Object);

            typeof(IndiDevice<IINDIRotator>)
                .GetField("connectedExpectation", BindingFlags.NonPublic | BindingFlags.Instance)
                .SetValue(sut, true);

            return sut;
        }

        [Test]
        public async Task MoveAbsolute_WritesRequestedSkyAngle_NotTheDelta() {
            // Regression test: current mechanical position 90°, request sky angle 100° while
            // unsynced (offset 0) must command the device to 100°, not to the relative delta 10°.
            var sut = CreateConnectedSUT();

            var result = await sut.MoveAbsolute(100f, CancellationToken.None);

            ClassicAssert.IsTrue(result);
            mockDevice.Verify(d => d.MoveAsync(100f, It.IsAny<CancellationToken>()), Times.Once);
        }

        [Test]
        public async Task MoveAbsolute_IllegalNegativeRequest_IsNormalizedNotSentAsNegative() {
            // Regression test: current mechanical position 90°, request sky angle 45° while
            // unsynced must command the device to 45°, not to -45° (90 - 45).
            var sut = CreateConnectedSUT();

            var result = await sut.MoveAbsolute(45f, CancellationToken.None);

            ClassicAssert.IsTrue(result);
            mockDevice.Verify(d => d.MoveAsync(45f, It.IsAny<CancellationToken>()), Times.Once);
        }

        [Test]
        public async Task MoveAbsoluteMechanical_WritesRequestedMechanicalPosition_NotTheDelta() {
            var sut = CreateConnectedSUT();

            var result = await sut.MoveAbsoluteMechanical(10f, CancellationToken.None);

            ClassicAssert.IsTrue(result);
            mockDevice.Verify(d => d.MoveAsync(10f, It.IsAny<CancellationToken>()), Times.Once);
        }

        [Test]
        public async Task Move_RelativeDelta_IsAddedToMechanicalPositionBeforeSend() {
            // Move() is the one INDI truly-relative-sounding entry point on IRotator, but the
            // backend only exposes an absolute property, so the wrapper must convert.
            var sut = CreateConnectedSUT();

            var result = await sut.Move(20f, CancellationToken.None);

            ClassicAssert.IsTrue(result);
            mockDevice.Verify(d => d.MoveAsync(110f, It.IsAny<CancellationToken>()), Times.Once);
        }

        [Test]
        public async Task MoveAbsolute_UsesOffsetFromSync_ToConvertSkyAngleToMechanical() {
            var sut = CreateConnectedSUT();
            // Sync: mechanical 90° == sky 120° => offset = 30°
            sut.Sync(120f);

            var result = await sut.MoveAbsolute(150f, CancellationToken.None);

            ClassicAssert.IsTrue(result);
            mockDevice.Verify(d => d.MoveAsync(120f, It.IsAny<CancellationToken>()), Times.Once);
        }

        // An unreadable position gave a NaN sync offset, so the sky angle stayed NaN until the next sync
        [Test]
        public async Task Sync_RefusesAnUnreadablePosition() {
            mechanicalPosition = float.NaN;
            var sut = CreateConnectedSUT();

            Assert.That(() => sut.Sync(120f), Throws.InvalidOperationException);
            Assert.That(sut.Synced, Is.False);

            // the offset is still 0: sky angle 150° is mechanical 150°
            await sut.MoveAbsolute(150f, CancellationToken.None);
            mockDevice.Verify(d => d.MoveAsync(150f, It.IsAny<CancellationToken>()), Times.Once);
        }

        // ... and NaN as the target of a relative move
        [Test]
        public void Move_RefusesAnUnreadablePosition() {
            mechanicalPosition = float.NaN;
            var sut = CreateConnectedSUT();

            Assert.That(async () => await sut.Move(20f, CancellationToken.None), Throws.InvalidOperationException);
            mockDevice.Verify(d => d.MoveAsync(It.IsAny<float>(), It.IsAny<CancellationToken>()), Times.Never);
        }
    }
}
