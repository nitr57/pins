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
using NINA.Astrometry;
using NINA.Core.Model;
using NINA.Equipment.Equipment.MyTelescope;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Sequencer.SequenceItem.Telescope;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Test.Sequencer.SequenceItem.Telescope {

    [TestFixture]
    internal class SlewToGuiderCalibrationPositionTest {
        private Mock<ITelescopeMediator> telescopeMediatorMock;
        private Mock<IGuiderMediator> guiderMediatorMock;
        private List<Coordinates> slews;

        [SetUp]
        public void Setup() {
            telescopeMediatorMock = new Mock<ITelescopeMediator>();
            guiderMediatorMock = new Mock<IGuiderMediator>();
            slews = new List<Coordinates>();

            telescopeMediatorMock.Setup(x => x.GetInfo()).Returns(new TelescopeInfo { Connected = true, SiderealTime = 10, RightAscension = 9 });
            telescopeMediatorMock
                .Setup(x => x.SlewToCoordinatesAsync(It.IsAny<Coordinates>(), It.IsAny<CancellationToken>()))
                .Callback<Coordinates, CancellationToken>((c, _) => slews.Add(c))
                .ReturnsAsync(true);
            guiderMediatorMock.Setup(x => x.StopGuiding(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        }

        private SlewToGuiderCalibrationPosition CreateSut() {
            return new SlewToGuiderCalibrationPosition(telescopeMediatorMock.Object, guiderMediatorMock.Object) {
                PointingSide = CalibrationPointingSide.West,
            };
        }

        [Test]
        public async Task Execute_SlewsToTheCurrentEpochPositionAndResumesGuiding() {
            var sut = CreateSut();

            await sut.Execute(new Progress<ApplicationStatus>(), CancellationToken.None);

            Assert.That(slews, Has.Count.EqualTo(2));
            Assert.That(slews[1].Epoch, Is.EqualTo(Epoch.JNOW));
            Assert.That(slews[1].RA, Is.EqualTo(10 - 5.0 / 15.0).Within(1e-9));
            Assert.That(slews[0].Dec, Is.EqualTo(-1).Within(1e-9));
            guiderMediatorMock.Verify(x => x.StartGuiding(false, It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()), Times.Once);
        }

        [Test]
        public void Execute_SlewFails_FailsAndStillResumesGuiding() {
            telescopeMediatorMock.Setup(x => x.SlewToCoordinatesAsync(It.IsAny<Coordinates>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);
            var sut = CreateSut();

            Assert.ThrowsAsync<SequenceEntityFailedException>(() => sut.Execute(new Progress<ApplicationStatus>(), CancellationToken.None));

            guiderMediatorMock.Verify(x => x.StartGuiding(false, It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()), Times.Once);
        }

        [Test]
        public void Execute_Cancelled_DoesNotResumeGuiding() {
            using var cts = new CancellationTokenSource();
            telescopeMediatorMock
                .Setup(x => x.SlewToCoordinatesAsync(It.IsAny<Coordinates>(), It.IsAny<CancellationToken>()))
                .Returns<Coordinates, CancellationToken>((_, _) => {
                    cts.Cancel();
                    return Task.FromCanceled<bool>(cts.Token);
                });
            var sut = CreateSut();

            Assert.CatchAsync<OperationCanceledException>(() => sut.Execute(new Progress<ApplicationStatus>(), cts.Token));

            guiderMediatorMock.Verify(x => x.StartGuiding(It.IsAny<bool>(), It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Test]
        public void Execute_SiderealTimeUnknown_FailsWithoutSlewing() {
            telescopeMediatorMock.Setup(x => x.GetInfo()).Returns(new TelescopeInfo { Connected = true, SiderealTime = double.NaN });
            var sut = CreateSut();

            Assert.ThrowsAsync<SequenceEntityFailedException>(() => sut.Execute(new Progress<ApplicationStatus>(), CancellationToken.None));

            Assert.That(slews, Is.Empty);
            guiderMediatorMock.Verify(x => x.StopGuiding(It.IsAny<CancellationToken>()), Times.Never);
        }

        [TestCase(0, 5, true, true)]
        [TestCase(-89.5, 5, true, false)]
        [TestCase(-89.5, 5, false, true)]
        [TestCase(91, 5, false, false)]
        [TestCase(0, -1, true, false)]
        [TestCase(0, 91, true, false)]
        public void Validate_ChecksDeclinationAndOffset(double dec, double haOffset, bool clearBacklash, bool expected) {
            var sut = CreateSut();
            sut.TargetDec = dec;
            sut.HaOffsetDegrees = haOffset;
            sut.ClearBacklash = clearBacklash;

            Assert.That(sut.Validate(), Is.EqualTo(expected));
            Assert.That(sut.Issues, expected ? Is.Empty : Is.Not.Empty);
        }
    }
}
