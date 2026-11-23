// SPDX-License-Identifier: MPL-2.0

using Moq;
using NINA.Astrometry;
using NINA.Core.Model;
using NINA.Equipment.Equipment.MyCamera;
using NINA.Equipment.Equipment.MyDome;
using NINA.Equipment.Equipment.MyFocuser;
using NINA.Equipment.Equipment.MyGuider;
using NINA.Equipment.Equipment.MyRotator;
using NINA.Equipment.Equipment.MyTelescope;
using NINA.Equipment.Interfaces;
using NINA.Mcp.Adapter;
using NINA.Mcp.Observatory;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Test.Mcp {

    /// <summary>
    /// MCP refuses to move a device that is busy with a motion started elsewhere (Touch-N-Stars, the Advanced API, the
    /// sequence), except the park and the dome close that secure_observatory relies on.
    /// </summary>
    [TestFixture]
    public class NinaObservatoryBusyTest {

        private NinaObservatoryHarness harness = null!;
        private Mock<ITelescope> telescope = null!;

        [SetUp]
        public void SetUp() {
            harness = new NinaObservatoryHarness();
            telescope = new Mock<ITelescope>();

            telescope.SetupGet(device => device.Connected).Returns(true);
            harness.Telescope.Setup(mediator => mediator.GetDevice()).Returns(telescope.Object);
            harness.Telescope.Setup(mediator => mediator.GetInfo()).Returns(new TelescopeInfo { Connected = true, CanFindHome = true });
            harness.Guider.Setup(mediator => mediator.GetInfo()).Returns(new GuiderInfo { Connected = true });
        }

        private void MountSlewsLive() {
            // The cached info still says idle: only the device knows yet.
            telescope.SetupGet(device => device.Slewing).Returns(true);
        }

        [Test]
        public async Task Slew_WhileTheDriverReportsSlewing_IsRefusedWithoutMoving() {
            MountSlewsLive();

            var result = await harness.Observatory.SlewAsync(5.5, 20, CancellationToken.None);

            Assert.That(result.Success, Is.False);
            Assert.That(result.Message, Does.Contain("driver reports slewing").And.Contain("do not stop it unless the user asks"));
            harness.Telescope.Verify(mediator => mediator.SlewToCoordinatesAsync(It.IsAny<Coordinates>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Test]
        public async Task Slew_InTheFirstSecondsOfAParkElsewhere_IsRefused_LaterNot() {
            // NINA raised its flag; the driver does not report the park yet.
            harness.ParkOrHomeFlag.Value = true;
            harness.Telescope
                .Setup(mediator => mediator.SlewToCoordinatesAsync(It.IsAny<Coordinates>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);

            var early = await harness.Observatory.SlewAsync(5.5, 20, CancellationToken.None);

            harness.Clock.Advance(MountParkOrHomeWatch.Window);
            var late = await harness.Observatory.SlewAsync(5.5, 20, CancellationToken.None);

            Assert.That(early.Success, Is.False);
            Assert.That(early.Message, Does.Contain("parking or homing").And.Contain("Retry in about 15 seconds"));
            Assert.That(late.Success, Is.True, "a flag NINA left set does not block for good: " + late.Message);
        }

        [Test]
        public async Task Park_WhileAParkElsewhereIsUnderWay_WaitsForIt_AndDoesNotParkAgain() {
            var info = new TelescopeInfo { Connected = true, CanFindHome = true };
            harness.Telescope.Setup(mediator => mediator.GetInfo()).Returns(info);
            telescope.SetupGet(device => device.AtPark).Returns(() => info.AtPark);
            harness.ParkOrHomeFlag.Value = true;

            var park = harness.Observatory.ParkAsync(CancellationToken.None);

            await Task.Delay(1500);
            Assert.That(park.IsCompleted, Is.False, "waiting for the park under way");

            info.AtPark = true;
            harness.ParkOrHomeFlag.Value = false;
            var result = await park.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.That(result.Success, Is.True, result.Message);
            Assert.That(result.Message, Does.Contain("already under way"));
            harness.Telescope.Verify(mediator => mediator.ParkTelescope(It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()), Times.Never);
            harness.Guider.Verify(mediator => mediator.StopGuiding(It.IsAny<CancellationToken>()), Times.Once, "guiding is stopped also when the park was already under way");
        }

        [Test]
        public async Task Park_WhenAParkElsewhereStartsWhileGuidingStops_WaitsForIt_AndDoesNotParkAgain() {
            var info = new TelescopeInfo { Connected = true, CanFindHome = true };
            harness.Telescope.Setup(mediator => mediator.GetInfo()).Returns(info);
            telescope.SetupGet(device => device.AtPark).Returns(() => info.AtPark);
            harness.Guider
                .Setup(mediator => mediator.StopGuiding(It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => {
                    // Touch-N-Stars parks the mount while the guider stops.
                    harness.ParkOrHomeFlag.Value = true;
                    info.AtPark = true;

                    return true;
                });

            var result = await harness.Observatory.ParkAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

            Assert.That(result.Success, Is.True, result.Message);
            Assert.That(result.Message, Does.Contain("already under way"));
            harness.Telescope.Verify(mediator => mediator.ParkTelescope(It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Test]
        public async Task Park_WhenAParkElsewhereEndsWhileGuidingStops_DoesNotParkAgain() {
            // NINA dropped its flag again when that park ended, and the info from before the guiding stop is stale.
            var info = new TelescopeInfo { Connected = true, CanFindHome = true };
            harness.Telescope.Setup(mediator => mediator.GetInfo()).Returns(info);
            telescope.SetupGet(device => device.AtPark).Returns(() => info.AtPark);
            harness.Guider
                .Setup(mediator => mediator.StopGuiding(It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => {
                    info.AtPark = true;

                    return true;
                });

            var result = await harness.Observatory.ParkAsync(CancellationToken.None);

            Assert.That(result.Success, Is.True, result.Message);
            Assert.That(result.Message, Is.EqualTo("Mount is already parked."));
            harness.Telescope.Verify(mediator => mediator.ParkTelescope(It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Test]
        public async Task Park_WhileTheMountStillMovesAfterTheWindow_KeepsWaiting() {
            // A park the driver does not report as slewing (the INDI simulator): the moving position carries it.
            var info = new TelescopeInfo { Connected = true, CanFindHome = true };
            var ra = 5.0;
            harness.Telescope.Setup(mediator => mediator.GetInfo()).Returns(info);
            telescope.SetupGet(device => device.AtPark).Returns(() => info.AtPark);
            telescope.SetupGet(device => device.RightAscension).Returns(() => ra += 0.01);
            harness.ParkOrHomeFlag.Value = true;

            var park = harness.Observatory.ParkAsync(CancellationToken.None);

            harness.Clock.Advance(MountParkOrHomeWatch.Window);
            await Task.Delay(4500);
            Assert.That(park.IsCompleted, Is.False, "the mount still moves");

            info.AtPark = true;
            var result = await park.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.That(result.Message, Does.Contain("already under way"));
            harness.Telescope.Verify(mediator => mediator.ParkTelescope(It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Test]
        public async Task Park_MountStoppedButParkNotConfirmedYet_KeepsWaiting() {
            // The mount reached its park position; the driver confirms the park a few seconds later.
            var info = new TelescopeInfo { Connected = true, CanFindHome = true };
            var reads = 0;
            harness.Telescope.Setup(mediator => mediator.GetInfo()).Returns(info);
            telescope.SetupGet(device => device.AtPark).Returns(() => info.AtPark);
            telescope.SetupGet(device => device.RightAscension).Returns(() => ++reads < 3 ? 5.0 + reads * 0.1 : 5.3);
            harness.ParkOrHomeFlag.Value = true;
            harness.Clock.Advance(MountParkOrHomeWatch.Window);

            var park = harness.Observatory.ParkAsync(CancellationToken.None);

            await Task.Delay(6000);
            Assert.That(park.IsCompleted, Is.False, "a mount seen moving is waited for longer than a flag on a still one");

            info.AtPark = true;
            var result = await park.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.That(result.Message, Does.Contain("already under way"));
            harness.Telescope.Verify(mediator => mediator.ParkTelescope(It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Test]
        public async Task Park_WhenTheMountStandsStillAfterTheWindow_ParksItself() {
            // NINA left its flag set (a home that did not move) or the park stalled: pins parks.
            harness.ParkOrHomeFlag.Value = true;
            telescope.SetupGet(device => device.RightAscension).Returns(5.0);
            harness.Telescope
                .Setup(mediator => mediator.ParkTelescope(It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);

            var park = harness.Observatory.ParkAsync(CancellationToken.None);

            harness.Clock.Advance(MountParkOrHomeWatch.Window);
            var result = await park.WaitAsync(TimeSpan.FromSeconds(15));

            Assert.That(result.Success, Is.True, result.Message);
            harness.Telescope.Verify(mediator => mediator.ParkTelescope(It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()), Times.Once);
        }

        [Test]
        public async Task Park_RequestedLongAfterAParkElsewhereBegan_StillWaitsWhileTheMountMoves() {
            // secure_observatory 20 s into a park started from Touch-N-Stars: no second park while the first still moves.
            var info = new TelescopeInfo { Connected = true, CanFindHome = true };
            harness.Telescope.Setup(mediator => mediator.GetInfo()).Returns(info);
            telescope.SetupGet(device => device.AtPark).Returns(() => info.AtPark);
            MountSlewsLive();
            harness.ParkOrHomeFlag.Value = true;
            harness.Clock.Advance(TimeSpan.FromSeconds(20));

            var park = harness.Observatory.ParkAsync(CancellationToken.None);

            // Longer than the few seconds a flag on a still mount is waited for: the slewing driver keeps the wait.
            await Task.Delay(4500);
            Assert.That(park.IsCompleted, Is.False, "the park under way is waited for");

            info.AtPark = true;
            var result = await park.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.That(result.Message, Does.Contain("already under way"));
            harness.Telescope.Verify(mediator => mediator.ParkTelescope(It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Test]
        public async Task Park_AfterAHomeOrAFailedParkElsewhere_ParksItself() {
            // The flag drops while the mount is not parked: a home ended, or the park failed.
            harness.ParkOrHomeFlag.Value = true;
            harness.Telescope
                .Setup(mediator => mediator.ParkTelescope(It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);

            var park = harness.Observatory.ParkAsync(CancellationToken.None);

            await Task.Delay(1500);
            harness.ParkOrHomeFlag.Value = false;
            var result = await park.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.That(result.Success, Is.True, result.Message);
            Assert.That(result.Message, Is.EqualTo("Mount parked."));
            harness.Telescope.Verify(mediator => mediator.ParkTelescope(It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()), Times.Once);
        }

        [Test]
        public void StopAll_WithOnlyNinasParkHomeFlag_LeavesTheIdleMountAlone() {
            // Even a fresh flag (a home that did not move leaves it set) is no motion; the abort would stop tracking on
            // several drivers.
            harness.ParkOrHomeFlag.Value = true;

            harness.Observatory.StopSlew();

            harness.Telescope.Verify(mediator => mediator.StopSlew(), Times.Never);
        }

        [Test]
        public void Park_CancelledWhileWaiting_Throws_AndDoesNotPark() {
            harness.ParkOrHomeFlag.Value = true;
            using var job = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

            Assert.That(async () => await harness.Observatory.ParkAsync(job.Token), Throws.InstanceOf<OperationCanceledException>());
            harness.Telescope.Verify(mediator => mediator.ParkTelescope(It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Test]
        public void StopSlew_StopAll_AbortsAMotionStartedElsewhere() {
            MountSlewsLive();

            harness.Observatory.StopSlew();

            harness.Telescope.Verify(mediator => mediator.StopSlew(), Times.Once);
        }

        [Test]
        public async Task PlateSolveSync_WhileSlewing_IsRefused() {
            MountSlewsLive();
            harness.Camera.Setup(mediator => mediator.GetInfo()).Returns(new CameraInfo { Connected = true });

            var result = await harness.Observatory.PlateSolveAsync(true, CancellationToken.None);

            Assert.That(result.Success, Is.False);
            Assert.That(result.Message, Does.StartWith("Not syncing"));
        }

        [Test]
        public async Task Home_WhileSlewing_IsRefused() {
            MountSlewsLive();

            var result = await harness.Observatory.FindHomeAsync(CancellationToken.None);

            Assert.That(result.Success, Is.False);
            harness.Telescope.Verify(mediator => mediator.FindHome(It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [TestCase(true, false, false)]
        [TestCase(false, true, true)]
        // Cached AtHome while a park elsewhere takes the mount away is no "already at home": see Home_WhileSlewing_IsRefused.
        public async Task Home_OnAParkedOrHomedMount_DoesNotCallNinasHome(bool atPark, bool atHome, bool success) {
            // NINA's home would leave its parking/homing flag set until the next park.
            harness.Telescope.Setup(mediator => mediator.GetInfo()).Returns(new TelescopeInfo { Connected = true, CanFindHome = true, AtPark = atPark, AtHome = atHome });

            var result = await harness.Observatory.FindHomeAsync(CancellationToken.None);

            Assert.That(result.Success, Is.EqualTo(success), result.Message);
            harness.Telescope.Verify(mediator => mediator.FindHome(It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Test]
        public async Task Unpark_WhileSlewing_IsRefusedBeforeSayingNotParked() {
            MountSlewsLive();

            var result = await harness.Observatory.UnparkAsync(CancellationToken.None);

            Assert.That(result.Success, Is.False);
            Assert.That(result.Message, Does.Not.Contain("not parked"));
        }

        [Test]
        public async Task Park_WhileSlewing_IsNotRefused() {
            // secure_observatory parks before it closes the dome: a slew still under way must not keep it from parking.
            MountSlewsLive();
            harness.Telescope
                .Setup(mediator => mediator.ParkTelescope(It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);

            var result = await harness.Observatory.ParkAsync(CancellationToken.None);

            Assert.That(result.Success, Is.True, result.Message);
            harness.Telescope.Verify(mediator => mediator.ParkTelescope(It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()), Times.Once);
        }

        [Test]
        public async Task Calibration_WhileSlewing_IsRefused_GuidingWithoutCalibrationIsNot() {
            MountSlewsLive();
            harness.Guider
                .Setup(mediator => mediator.StartGuiding(It.IsAny<bool>(), It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);

            var calibrate = await harness.Observatory.StartGuidingAsync(true, CancellationToken.None);
            var guide = await harness.Observatory.StartGuidingAsync(false, CancellationToken.None);

            Assert.That(calibrate.Success, Is.False);
            Assert.That(calibrate.Message, Does.StartWith("Not calibrating"));
            Assert.That(guide.Success, Is.True, guide.Message);
        }

        [Test]
        public async Task FocuserMove_WhileTheFocuserMoves_IsRefused() {
            harness.Focuser.Setup(mediator => mediator.GetInfo()).Returns(new FocuserInfo { Connected = true, IsMoving = true });

            var result = await harness.Observatory.MoveFocuserAsync(1000, CancellationToken.None);

            Assert.That(result.Success, Is.False);
            harness.Focuser.Verify(mediator => mediator.MoveFocuser(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Test]
        public async Task Home_AtHomeWhileSlewingAway_IsRefused_NotAlreadyHome() {
            MountSlewsLive();
            harness.Telescope.Setup(mediator => mediator.GetInfo()).Returns(new TelescopeInfo { Connected = true, CanFindHome = true, AtHome = true });

            var result = await harness.Observatory.FindHomeAsync(CancellationToken.None);

            Assert.That(result.Success, Is.False);
        }

        [Test]
        public async Task Autofocus_WhileTheFocuserMoves_IsRefused() {
            harness.Camera.Setup(mediator => mediator.GetInfo()).Returns(new CameraInfo { Connected = true });
            harness.Focuser.Setup(mediator => mediator.GetInfo()).Returns(new FocuserInfo { Connected = true, IsMoving = true });

            var result = await harness.Observatory.RunAutofocusAsync(CancellationToken.None);

            Assert.That(result.Success, Is.False);
            Assert.That(result.Message, Does.Contain("focuser is moving"));
        }

        [Test]
        public async Task Autofocus_WhileTheCameraExposes_IsRefused() {
            harness.Camera.Setup(mediator => mediator.GetInfo()).Returns(new CameraInfo { Connected = true, IsExposing = true });
            harness.Focuser.Setup(mediator => mediator.GetInfo()).Returns(new FocuserInfo { Connected = true });

            var result = await harness.Observatory.RunAutofocusAsync(CancellationToken.None);

            Assert.That(result.Success, Is.False);
            Assert.That(result.Message, Does.Contain("already exposing").And.Contain("do not stop it unless the user asks"));
        }

        [Test]
        public async Task Center_WhileSlewing_IsRefusedWithoutMovingOrStoppingGuiding() {
            MountSlewsLive();
            harness.Camera.Setup(mediator => mediator.GetInfo()).Returns(new CameraInfo { Connected = true });

            var result = await harness.Observatory.CenterAsync(5.5, 20, CancellationToken.None);

            Assert.That(result.Success, Is.False);
            Assert.That(result.Message, Does.Contain("driver reports slewing").And.Contain("do not stop it unless the user asks"));
            harness.Telescope.Verify(mediator => mediator.SlewToCoordinatesAsync(It.IsAny<Coordinates>(), It.IsAny<CancellationToken>()), Times.Never);
            harness.Guider.Verify(mediator => mediator.StopGuiding(It.IsAny<CancellationToken>()), Times.Never);
        }

        [Test]
        public async Task RotatorMove_WhileTheRotatorMoves_IsRefused() {
            harness.Rotator.Setup(mediator => mediator.GetInfo()).Returns(new RotatorInfo { Connected = true, IsMoving = true });

            var result = await harness.Observatory.MoveRotatorAsync(90, true, CancellationToken.None);

            Assert.That(result.Success, Is.False);
            harness.Rotator.Verify(mediator => mediator.MoveMechanical(It.IsAny<float>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Test]
        public async Task DomeOpen_WhileTheShutterCloses_IsRefused() {
            var dome = new Mock<IDome>();
            dome.SetupGet(device => device.ShutterStatus).Returns(ShutterState.ShutterClosing);
            harness.Dome.Setup(mediator => mediator.GetDevice()).Returns(dome.Object);
            harness.Dome.Setup(mediator => mediator.GetInfo()).Returns(new DomeInfo { Connected = true, CanSetShutter = true });

            var result = await harness.Observatory.ControlDomeAsync(DomeAction.OpenShutter, null, CancellationToken.None);

            Assert.That(result.Success, Is.False);
            Assert.That(result.Message, Does.Contain("do not move or reopen the dome unless the user asks"));
            harness.Dome.Verify(mediator => mediator.OpenShutter(It.IsAny<CancellationToken>()), Times.Never);
        }

        [Test]
        public async Task DomeClose_WhileTheShutterCloses_IsNotRefused() {
            // secure_observatory closes the dome whatever else is closing it already.
            var info = new DomeInfo { Connected = true, CanSetShutter = true, ShutterStatus = ShutterState.ShutterClosing };
            var dome = new Mock<IDome>();
            dome.SetupGet(device => device.ShutterStatus).Returns(() => info.ShutterStatus);
            harness.Dome.Setup(mediator => mediator.GetDevice()).Returns(dome.Object);
            harness.Dome.Setup(mediator => mediator.GetInfo()).Returns(info);
            harness.Dome
                .Setup(mediator => mediator.CloseShutter(It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => {
                    info.ShutterStatus = ShutterState.ShutterClosed;

                    return true;
                });

            var result = await harness.Observatory.ControlDomeAsync(DomeAction.CloseShutter, null, CancellationToken.None);

            Assert.That(result.Success, Is.True, result.Message);
            harness.Dome.Verify(mediator => mediator.CloseShutter(It.IsAny<CancellationToken>()), Times.Once);
        }

        [Test]
        public async Task DomeStopFollowing_WhileTheShutterCloses_IsNotRefused() {
            var dome = new Mock<IDome>();
            dome.SetupGet(device => device.ShutterStatus).Returns(ShutterState.ShutterClosing);
            harness.Dome.Setup(mediator => mediator.GetDevice()).Returns(dome.Object);
            harness.Dome.Setup(mediator => mediator.GetInfo()).Returns(new DomeInfo { Connected = true, CanSetShutter = true, ShutterStatus = ShutterState.ShutterClosing });
            harness.Dome.Setup(mediator => mediator.DisableFollowing(It.IsAny<CancellationToken>())).ReturnsAsync(true);

            var result = await harness.Observatory.ControlDomeAsync(DomeAction.StopFollowing, null, CancellationToken.None);

            Assert.That(result.Success, Is.True, result.Message);
            harness.Dome.Verify(mediator => mediator.DisableFollowing(It.IsAny<CancellationToken>()), Times.Once);
        }
    }

    /// <summary>NINA's park/home flag counts only for its first seconds: NINA leaves it set after a home that did not move.</summary>
    [TestFixture]
    public class MountParkOrHomeWatchTest {

        [Test]
        public void Busy_OnlyWithinTheWindowAfterTheFlagRose_AndAgainAtTheNextRise() {
            var flag = new ParkingOrHomingFlag();
            var clock = new TestClock();
            var watch = new MountParkOrHomeWatch(flag, () => flag.Value, clock);

            Assert.That(watch.RecentlyStarted, Is.False);

            flag.Value = true;
            clock.Advance(TimeSpan.FromSeconds(10));

            Assert.That(watch.RecentlyStarted, Is.True);

            clock.Advance(TimeSpan.FromSeconds(10));

            Assert.That(watch.RecentlyStarted, Is.False, "a flag NINA left set is stale after the window");

            flag.Value = false;
            flag.Value = true;

            Assert.That(watch.RecentlyStarted, Is.True, "the next park starts a new window");

            flag.Value = false;

            Assert.That(watch.RecentlyStarted, Is.False, "a park that ended is not busy");
        }

        [Test]
        public void FlagAlreadySetAtStart_CountsAsJustStarted() {
            var flag = new ParkingOrHomingFlag { Value = true };
            var clock = new TestClock();
            var watch = new MountParkOrHomeWatch(flag, () => flag.Value, clock);

            Assert.That(watch.RecentlyStarted, Is.True);

            clock.Advance(MountParkOrHomeWatch.Window);

            Assert.That(watch.RecentlyStarted, Is.False);
        }

        [Test]
        public void NoViewModel_IsNeverBusy() {
            var watch = new MountParkOrHomeWatch(null, () => false, TimeProvider.System);

            Assert.That(watch.RecentlyStarted, Is.False);
        }
    }
}
