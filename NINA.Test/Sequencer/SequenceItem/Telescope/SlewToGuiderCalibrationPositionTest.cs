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
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Profile.Interfaces;
using NINA.Sequencer.SequenceItem.Telescope;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Test.Sequencer.SequenceItem.Telescope {

    [TestFixture]
    internal class SlewToGuiderCalibrationPositionTest {
        private Mock<ITelescopeMediator> telescopeMediatorMock;
        private Mock<IGuiderMediator> guiderMediatorMock;
        private Mock<IGuider> guiderMock;
        private Mock<IProfileService> profileServiceMock;
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
            guiderMediatorMock.Setup(x => x.StartGuiding(It.IsAny<bool>(), It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
            guiderMock = new Mock<IGuider>();
            guiderMock.Setup(x => x.State).Returns("Guiding");
            guiderMediatorMock.Setup(x => x.GetDevice()).Returns(guiderMock.Object);
            profileServiceMock = new Mock<IProfileService>();
        }

        private SlewToGuiderCalibrationPosition CreateSut() {
            return new SlewToGuiderCalibrationPosition(telescopeMediatorMock.Object, guiderMediatorMock.Object, profileServiceMock.Object) {
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
            telescopeMediatorMock.Setup(x => x.GetInfo()).Returns(new TelescopeInfo { Connected = true, SiderealTime = double.NaN, SiteLongitude = double.NaN });
            var sut = CreateSut();

            Assert.ThrowsAsync<SequenceEntityFailedException>(() => sut.Execute(new Progress<ApplicationStatus>(), CancellationToken.None));

            Assert.That(slews, Is.Empty);
            guiderMediatorMock.Verify(x => x.StopGuiding(It.IsAny<CancellationToken>()), Times.Never);
        }

        [Test]
        public async Task Execute_SiderealTimeOutOfRange_UsesTheSiteLongitude() {
            // INDI reports -1 for an unknown sidereal time
            telescopeMediatorMock.Setup(x => x.GetInfo()).Returns(new TelescopeInfo { Connected = true, SiderealTime = -1, SiteLongitude = 10, RightAscension = 9 });
            var sut = CreateSut();
            double expected = AstroUtil.EuclidianModulus(AstroUtil.GetLocalSiderealTimeNow(10) - 5.0 / 15.0, 24.0);

            await sut.Execute(new Progress<ApplicationStatus>(), CancellationToken.None);

            Assert.That(slews[1].RA, Is.EqualTo(expected).Within(0.01));
        }

        [Test]
        public void Execute_MountNotConnected_FailsWithoutSlewing() {
            telescopeMediatorMock.Setup(x => x.GetInfo()).Returns(new TelescopeInfo { Connected = false, SiderealTime = -1 });
            var sut = CreateSut();

            Assert.ThrowsAsync<SequenceEntityFailedException>(() => sut.Execute(new Progress<ApplicationStatus>(), CancellationToken.None));

            Assert.That(slews, Is.Empty);
        }

        [Test]
        public async Task Execute_GuiderOnlyLooping_DoesNotStartGuiding() {
            guiderMock.Setup(x => x.State).Returns("Looping");
            var sut = CreateSut();

            await sut.Execute(new Progress<ApplicationStatus>(), CancellationToken.None);

            guiderMediatorMock.Verify(x => x.StartGuiding(It.IsAny<bool>(), It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [TestCase("idle")]
        [TestCase("looping")]
        public async Task Execute_SkyGuardNotGuiding_DoesNotStartGuiding(string state) {
            // SkyGuard's StopGuiding succeeds also when it was not guiding
            guiderMock.Setup(x => x.State).Returns(state);
            var sut = CreateSut();

            await sut.Execute(new Progress<ApplicationStatus>(), CancellationToken.None);

            guiderMediatorMock.Verify(x => x.StartGuiding(It.IsAny<bool>(), It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Test]
        public async Task Execute_GuiderWithOtherStateNames_ResumesGuiding() {
            // MetaGuide's state while guiding
            guiderMock.Setup(x => x.State).Returns("Locked and Guiding");
            var sut = CreateSut();

            await sut.Execute(new Progress<ApplicationStatus>(), CancellationToken.None);

            guiderMediatorMock.Verify(x => x.StartGuiding(false, It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()), Times.Once);
        }

        [Test]
        public void Execute_GuidingNotRestarted_Fails() {
            guiderMediatorMock.Setup(x => x.StartGuiding(It.IsAny<bool>(), It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);
            var sut = CreateSut();

            var ex = Assert.ThrowsAsync<SequenceEntityFailedException>(() => sut.Execute(new Progress<ApplicationStatus>(), CancellationToken.None));

            Assert.That(ex.Message, Does.Contain("Guiding could not be restarted"));
        }

        [Test]
        public void Execute_SlewFailsAndGuidingNotRestarted_ReportsTheSlew() {
            telescopeMediatorMock.Setup(x => x.SlewToCoordinatesAsync(It.IsAny<Coordinates>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);
            guiderMediatorMock.Setup(x => x.StartGuiding(It.IsAny<bool>(), It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);
            var sut = CreateSut();

            var ex = Assert.ThrowsAsync<SequenceEntityFailedException>(() => sut.Execute(new Progress<ApplicationStatus>(), CancellationToken.None));

            Assert.That(ex.Message, Does.Contain("slew"));
        }

        [Test]
        public void Execute_PositionBelowTheMinimumAltitude_FailsWithoutSlewing() {
            // at latitude 50° Dec -40° culminates at 0°
            telescopeMediatorMock.Setup(x => x.GetInfo()).Returns(new TelescopeInfo { Connected = true, SiderealTime = 10, SiteLatitude = 50 });
            var sut = CreateSut();
            sut.TargetDec = -40;

            Assert.That(sut.Validate(), Is.False);
            Assert.ThrowsAsync<SequenceEntityFailedException>(() => sut.Execute(new Progress<ApplicationStatus>(), CancellationToken.None));
            Assert.That(slews, Is.Empty);
        }

        // a horizon of 80° east of the meridian (azimuth below 180°) and none west of it
        private static CustomHorizon EastBlockedHorizon() {
            return CustomHorizon.FromReader_Standard(new StringReader("0 80\n179 80\n181 0\n360 0"));
        }

        [Test]
        public async Task Stay_OnlyTheOtherSideBlocked_ValidatesAndSlewsOnTheCurrentSide() {
            // hour angle +1 h: the mount points west
            telescopeMediatorMock.Setup(x => x.GetInfo()).Returns(new TelescopeInfo { Connected = true, SiderealTime = 10, RightAscension = 9, SiteLatitude = 50 });
            profileServiceMock.Setup(x => x.ActiveProfile.AstrometrySettings.Horizon).Returns(EastBlockedHorizon());
            var sut = CreateSut();
            sut.PointingSide = CalibrationPointingSide.Stay;

            Assert.That(sut.Validate(), Is.True);

            await sut.Execute(new Progress<ApplicationStatus>(), CancellationToken.None);

            Assert.That(slews[^1].RA, Is.EqualTo(10 - 5.0 / 15.0).Within(1e-9));
        }

        [Test]
        public void Stay_CurrentSideBlocked_FailsWithoutSlewing() {
            // hour angle -1 h: the mount points east, behind the horizon
            telescopeMediatorMock.Setup(x => x.GetInfo()).Returns(new TelescopeInfo { Connected = true, SiderealTime = 10, RightAscension = 11, SiteLatitude = 50 });
            profileServiceMock.Setup(x => x.ActiveProfile.AstrometrySettings.Horizon).Returns(EastBlockedHorizon());
            var sut = CreateSut();
            sut.PointingSide = CalibrationPointingSide.Stay;

            Assert.That(sut.Validate(), Is.True);
            Assert.ThrowsAsync<SequenceEntityFailedException>(() => sut.Execute(new Progress<ApplicationStatus>(), CancellationToken.None));
            Assert.That(slews, Is.Empty);
        }

        [Test]
        public void East_BehindTheHorizon_DoesNotValidate() {
            telescopeMediatorMock.Setup(x => x.GetInfo()).Returns(new TelescopeInfo { Connected = true, SiderealTime = 10, SiteLatitude = 50 });
            profileServiceMock.Setup(x => x.ActiveProfile.AstrometrySettings.Horizon).Returns(EastBlockedHorizon());
            var sut = CreateSut();
            sut.PointingSide = CalibrationPointingSide.East;

            Assert.That(sut.Validate(), Is.False);
            Assert.That(sut.Issues, Has.Some.Contains("east"));
        }

        [Test]
        public void Validate_NoTelescopeInfo_ReportsNotConnected() {
            telescopeMediatorMock.Setup(x => x.GetInfo()).Returns((TelescopeInfo)null!);
            var sut = CreateSut();

            Assert.That(sut.Validate(), Is.False);
        }

        [TestCase(0, 5, true, true)]
        [TestCase(-89.5, 5, true, false)]
        [TestCase(-89.5, 5, false, true)]
        [TestCase(91, 5, false, false)]
        [TestCase(0, -1, true, false)]
        [TestCase(0, 91, true, false)]
        public void Validate_ChecksDeclinationAndOffset(double dec, double haOffset, bool clearBacklash, bool expected) {
            // an unknown site: only the ranges are checked, not the altitude
            telescopeMediatorMock.Setup(x => x.GetInfo()).Returns(new TelescopeInfo { Connected = true, SiderealTime = 10, SiteLatitude = double.NaN });
            var sut = CreateSut();
            sut.TargetDec = dec;
            sut.HaOffsetDegrees = haOffset;
            sut.ClearBacklash = clearBacklash;

            Assert.That(sut.Validate(), Is.EqualTo(expected));
            Assert.That(sut.Issues, expected ? Is.Empty : Is.Not.Empty);
        }
    }
}
