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
using NINA.Equipment.Interfaces;
using NINA.Equipment.SDK.TelescopeSDKs.OnStepXSDK;
using NINA.Profile.Interfaces;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Test.Equipment.OnStepX {

    [TestFixture]
    public class OnStepXTelescopeTest {

        // :GU# of the UMi17S: n = not tracking, N = not slewing, p = not parked
        private const string Idle = "nNpEW260#";
        private const string Tracking = "NpEW260#";
        private const string TrackingAndSlewing = "pEW260#";

        private static (OnStepXTelescope Telescope, FakeOnStepXPort Port) Create(FakeOnStepXPort port, string serialPort = "/dev/fake", bool timeSync = false) {
            var profile = new NINA.Profile.Profile();
            profile.TelescopeSettings.SerialPort = serialPort;
            profile.TelescopeSettings.TimeSync = timeSync;
            profile.ApplicationSettings.DevicePollingInterval = 0.05;
            var profileService = new Mock<IProfileService>();
            profileService.SetupGet(p => p.ActiveProfile).Returns(profile);

            var telescope = new OnStepXTelescope(profileService.Object,
                _ => OnStepXDevice.Connect(new OnStepXTransport(port), TimeSpan.Zero, TimeSpan.Zero));
            return (telescope, port);
        }

        private static async Task<(OnStepXTelescope Telescope, FakeOnStepXPort Port)> Connected(FakeOnStepXPort port, bool timeSync = false) {
            var (telescope, _) = Create(port, timeSync: timeSync);
            Assert.That(await telescope.Connect(CancellationToken.None), Is.True);
            port.Written.Clear();
            return (telescope, port);
        }

        private static IProfileService ProfileWithPort(string serialPort) {
            var profile = new NINA.Profile.Profile();
            profile.TelescopeSettings.SerialPort = serialPort;
            var profileService = new Mock<IProfileService>();
            profileService.SetupGet(p => p.ActiveProfile).Returns(profile);
            return profileService.Object;
        }

        [Test]
        public async Task Discover_NamesTheEntryAfterTheModelAndClosesThePort() {
            var port = FakeOnStepXPort.Umi17S();

            var telescope = await OnStepXTelescope.Discover(ProfileWithPort("/dev/fake-scan"),
                _ => OnStepXDevice.Connect(new OnStepXTransport(port), TimeSpan.Zero, TimeSpan.Zero), _ => false);

            Assert.That(telescope.DisplayName, Is.EqualTo("Proxisky UMi17S (OnStepX)"));
            Assert.That(telescope.Name, Is.EqualTo("OnStepX (serial)"), "the name the profile remembers stays the same");
            Assert.That(telescope.Connected, Is.False);
            Assert.That(port.IsOpen, Is.False);
        }

        [Test]
        public async Task Discover_PortOpenInThisProcess_IsNotTouched() {
            int opened = 0;
            var telescope = await OnStepXTelescope.Discover(ProfileWithPort("/dev/fake-busy"),
                _ => { opened++; throw new IOException("busy"); }, _ => true);

            Assert.That(opened, Is.Zero, "the scan must not open a port this process has open");
            Assert.That(telescope.DisplayName, Is.EqualTo("OnStepX (serial)"));
        }

        [Test]
        public async Task Discover_NothingAnswers_KeepsTheGenericName() {
            var silent = new FakeOnStepXPort { DefaultReply = null };

            var telescope = await OnStepXTelescope.Discover(ProfileWithPort("/dev/fake-silent"),
                _ => OnStepXDevice.Connect(new OnStepXTransport(silent), TimeSpan.Zero, TimeSpan.Zero), _ => false);

            Assert.That(telescope.DisplayName, Is.EqualTo("OnStepX (serial)"));
            Assert.That(silent.IsOpen, Is.False);
        }

        [Test]
        public async Task Discover_WithoutSerialPort_DoesNotScan() {
            int opened = 0;
            var telescope = await OnStepXTelescope.Discover(ProfileWithPort(""),
                _ => { opened++; throw new IOException("no port"); }, _ => false);

            Assert.That(opened, Is.Zero);
            Assert.That(telescope.DisplayName, Is.EqualTo("OnStepX (serial)"));
        }

        [Test]
        public async Task Connect_WithoutSerialPort_Fails() {
            var (telescope, port) = Create(FakeOnStepXPort.Umi17S(), serialPort: "");

            Assert.That(await telescope.Connect(CancellationToken.None), Is.False);
            Assert.That(telescope.Connected, Is.False);
            Assert.That(port.Written, Is.Empty);
        }

        [Test]
        public async Task Connect_Umi17S_ReadsItsState() {
            var (telescope, _) = await Connected(FakeOnStepXPort.Umi17S());

            Assert.That(telescope.Connected, Is.True);
            Assert.That(telescope.DisplayName, Is.EqualTo("Proxisky UMi17S (OnStepX)"));
            Assert.That(telescope.Id, Is.EqualTo(OnStepXTelescope.DeviceId));
            Assert.That(telescope.RightAscension, Is.EqualTo(4 + 15 / 3600.0).Within(1e-9));
            Assert.That(telescope.Declination, Is.EqualTo(90.0).Within(1e-9));
            Assert.That(telescope.Altitude, Is.EqualTo(48 + 40 / 60.0).Within(1e-9));
            Assert.That(telescope.SiteLatitude, Is.EqualTo(48 + 40 / 60.0).Within(1e-9));
            Assert.That(telescope.SiteLongitude, Is.EqualTo(8 + 14 / 60.0).Within(1e-9));
            Assert.That(telescope.SideOfPier, Is.EqualTo(PierSide.pierWest));
            Assert.That(telescope.AlignmentMode, Is.EqualTo(AlignmentMode.GermanPolar));
            Assert.That(telescope.TrackingEnabled, Is.False);
            Assert.That(telescope.TrackingRate.TrackingMode, Is.EqualTo(TrackingMode.Stopped));
            Assert.That(telescope.AtPark, Is.False);
            Assert.That(telescope.Slewing, Is.False);
            Assert.That(telescope.GuideRateRightAscensionArcsecPerSec, Is.EqualTo(15.041).Within(0.001));
            Assert.That(telescope.EquatorialSystem, Is.EqualTo(Epoch.JNOW));
        }

        [Test]
        public async Task Connect_WithTimeSync_SetsTheControllerClock() {
            // the clock commands' replies depend on the time; accept them all
            var port = FakeOnStepXPort.Umi17S();
            port.DefaultReply = "1";
            var (telescope, _) = Create(port, timeSync: true);

            await telescope.Connect(CancellationToken.None);

            Assert.That(port.Written.Any(c => c.StartsWith(":SG")), Is.True);
            Assert.That(port.Written.Any(c => c.StartsWith(":SL")), Is.True);
            Assert.That(port.Written.Any(c => c.StartsWith(":SC")), Is.True);
        }

        [Test]
        public async Task State_IsReadOncePerPoll() {
            var (telescope, port) = await Connected(FakeOnStepXPort.Umi17S());

            _ = telescope.RightAscension;
            _ = telescope.Declination;
            _ = telescope.AtPark;
            _ = telescope.TrackingEnabled;

            Assert.That(port.Written.Count(c => c == ":GU#"), Is.EqualTo(1));
        }

        [Test]
        public async Task SlewToCoordinates_WhileTracking_GoesAndWaitsForTheEnd() {
            var port = FakeOnStepXPort.Umi17S()
                .On(":Sr05:00:00#", "1")
                .On(":Sd+20*00:00#", "1")
                .On(":MS#", "0");
            var (telescope, _) = await Connected(port);
            port.On(":GU#", Tracking, TrackingAndSlewing, TrackingAndSlewing, Tracking);

            bool done = await telescope.SlewToCoordinates(new Coordinates(Angle.ByHours(5), Angle.ByDegree(20), Epoch.JNOW), CancellationToken.None);

            Assert.That(done, Is.True);
            Assert.That(port.Written, Does.Contain(":MS#"));
            Assert.That(port.Written, Has.None.EqualTo(":Te#"));
            Assert.That(port.Written.Count(c => c == ":GU#"), Is.GreaterThanOrEqualTo(3), "polled until the slew ended");
            Assert.That(telescope.TargetCoordinates, Is.Null);
        }

        [Test]
        public async Task SlewToCoordinates_RightAfterTrackingOn_RetriesABelowHorizonRefusalOnce() {
            var port = FakeOnStepXPort.Umi17S()
                .On(":Te#", "1")
                .On(":Sr05:00:00#", "1")
                .On(":Sd+20*00:00#", "1")
                .On(":MS#", "1", "0");
            var (telescope, _) = await Connected(port);
            port.On(":GU#", Idle, Tracking);

            bool done = await telescope.SlewToCoordinates(new Coordinates(Angle.ByHours(5), Angle.ByDegree(20), Epoch.JNOW), CancellationToken.None);

            Assert.That(done, Is.True);
            Assert.That(port.Written, Does.Contain(":Te#"));
            Assert.That(port.Written.Count(c => c == ":MS#"), Is.EqualTo(2));
        }

        [Test]
        public async Task SlewToCoordinates_Refused_ReturnsFalse() {
            var port = FakeOnStepXPort.Umi17S()
                .On(":GU#", Tracking)
                .On(":Sr05:00:00#", "1")
                .On(":Sd+20*00:00#", "1")
                .On(":MS#", "6");
            var (telescope, _) = await Connected(port);

            bool done = await telescope.SlewToCoordinates(new Coordinates(Angle.ByHours(5), Angle.ByDegree(20), Epoch.JNOW), CancellationToken.None);

            Assert.That(done, Is.False);
            Assert.That(port.Written.Count(c => c == ":MS#"), Is.EqualTo(1));
        }

        [Test]
        public async Task SlewToCoordinates_Parked_DoesNothing() {
            var (telescope, port) = await Connected(FakeOnStepXPort.Umi17S().On(":GU#", "nNPEW260#"));

            Assert.That(await telescope.SlewToCoordinates(new Coordinates(Angle.ByHours(5), Angle.ByDegree(20), Epoch.JNOW), CancellationToken.None), Is.False);
            Assert.That(port.Written, Has.None.EqualTo(":MS#"));
        }

        [Test]
        public async Task Park_WaitsUntilParked() {
            var (telescope, port) = await Connected(FakeOnStepXPort.Umi17S());
            port.On(":GU#", Tracking, "nIEW260#", "nIEW260#", "nNPEW260#");

            await telescope.Park(CancellationToken.None);

            Assert.That(port.Written, Does.Contain(":hP#"));
            Assert.That(telescope.AtPark, Is.True);
        }

        [Test]
        public async Task FindHome_EndsWhenTheControllerClearsItsHomingFlag() {
            // OnStepX 10.20a without home sensors: a goto with 'h', then 'h' cleared; after a long slew without 'H'
            // (the UMi17S at 23:28: "nNpET290")
            var (telescope, port) = await Connected(FakeOnStepXPort.Umi17S());
            port.On(":GU#", Tracking, "nphET290#", "nphET290#", "nNpET290#");
            var clock = System.Diagnostics.Stopwatch.StartNew();

            await telescope.FindHome(CancellationToken.None);

            Assert.That(port.Written, Does.Contain(":hC#"));
            Assert.That(port.Written.Count(c => c == ":GU#"), Is.EqualTo(4));
            Assert.That(clock.Elapsed, Is.LessThan(TimeSpan.FromSeconds(2)), "no standstill wait once 'h' is gone");
        }

        [Test]
        public async Task FindHome_KeepsWaitingWhileTheHomingFlagIsSet() {
            // homing with sensors runs as a guide, not a goto: no slew, but 'h' until the controller is done
            var (telescope, port) = await Connected(FakeOnStepXPort.Umi17S());
            port.On(":GU#", Tracking, "nNphEo290#", "nNphEo290#", "nNphEo290#", "nNpHEo290#");

            await telescope.FindHome(CancellationToken.None);

            Assert.That(port.Written.Count(c => c == ":GU#"), Is.EqualTo(5));
            Assert.That(telescope.AtHome, Is.True);
        }

        [Test]
        public async Task Park_Refused_ThrowsTheReasonAtOnce() {
            // TelescopeVM logs "Mount has parked" unless Park throws; OnStepX 10.24c refused :hP# without a park position
            var (telescope, port) = await Connected(FakeOnStepXPort.Umi17S().On(":hP#", "0").On(":GE#", "12#"));
            port.On(":GU#", Tracking);
            var clock = System.Diagnostics.Stopwatch.StartNew();

            Assert.That(async () => await telescope.Park(CancellationToken.None),
                Throws.InvalidOperationException.With.Message.Contains("refused to park: no park position set"));
            Assert.That(clock.Elapsed, Is.LessThan(TimeSpan.FromSeconds(2)));
        }

        [Test]
        public async Task Park_FailedOnTheWay_Throws() {
            var (telescope, port) = await Connected(FakeOnStepXPort.Umi17S());
            port.On(":GU#", Tracking, "nIEW260#", "nNFEW260#");

            Assert.That(async () => await telescope.Park(CancellationToken.None),
                Throws.InvalidOperationException.With.Message.Contains("parking failed"));
        }

        [Test]
        public async Task Setpark_Refused_Throws() {
            var (telescope, _) = await Connected(FakeOnStepXPort.Umi17S().On(":hQ#", "0").On(":GE#", "17#"));

            Assert.That(() => telescope.Setpark(), Throws.InvalidOperationException.With.Message.Contains("standby"));
        }

        [Test]
        public async Task Unpark_Refused_Throws() {
            var (telescope, port) = await Connected(FakeOnStepXPort.Umi17S().On(":hR#", "0").On(":GE#", "11#"));
            port.On(":GU#", "nNPEW260#");

            Assert.That(async () => await telescope.Unpark(CancellationToken.None),
                Throws.InvalidOperationException.With.Message.Contains("refused to unpark: not parked"));
        }

        [Test]
        public async Task FindHome_Refused_Throws() {
            var (telescope, port) = await Connected(FakeOnStepXPort.Umi17S().On(":GE#", "17#"));
            port.On(":GU#", Tracking);

            Assert.That(async () => await telescope.FindHome(CancellationToken.None),
                Throws.InvalidOperationException.With.Message.Contains("refused to find home"));
        }

        [Test]
        public async Task TrackingOn_Refused_DoesNotThrow() {
            // a property setter: the reason goes to a notification and the log
            var (telescope, port) = await Connected(FakeOnStepXPort.Umi17S().On(":Te#", "0").On(":GE#", "17#"));

            Assert.That(() => telescope.TrackingEnabled = true, Throws.Nothing);
            Assert.That(port.Written, Is.EqualTo(new[] { ":Te#", ":GE#" }));
        }

        [Test]
        public async Task GuideRate_FollowsThePulseRateInGu() {
            // :R1# made 0.5x the pulse-guide rate: the digit before the move rate in :GU#
            var (telescope, port) = await Connected(FakeOnStepXPort.Umi17S());
            port.On(":GU#", "nNpEW160#");
            await Task.Delay(300);

            Assert.That(telescope.GuideRateRightAscensionArcsecPerSec, Is.EqualTo(0.5 * 15.0410686).Within(1e-6));
        }

        [Test]
        public async Task Unpark_LostAcknowledgement_TheStatusDecides() {
            var port = FakeOnStepXPort.Umi17S().On(":hR#", (string?)null).On(":GU#", Idle);
            var (telescope, _) = await Connected(port);

            await telescope.Unpark(CancellationToken.None);

            Assert.That(port.Written, Does.Contain(":hR#"));
            Assert.That(telescope.AtPark, Is.False);
        }

        [Test]
        public async Task Sync_NotTracking_IsRefusedWithoutACommand() {
            var (telescope, port) = await Connected(FakeOnStepXPort.Umi17S().On(":GU#", Idle));

            Assert.That(telescope.Sync(new Coordinates(Angle.ByHours(5), Angle.ByDegree(20), Epoch.JNOW)), Is.False);
            Assert.That(port.Written, Has.None.EqualTo(":CM#"));
        }

        [Test]
        public async Task Sync_WhileTracking() {
            var port = FakeOnStepXPort.Umi17S()
                .On(":GU#", Tracking)
                .On(":Sr05:00:00#", "1")
                .On(":Sd+20*00:00#", "1")
                .On(":CM#", "N/A#");
            var (telescope, _) = await Connected(port);

            Assert.That(telescope.Sync(new Coordinates(Angle.ByHours(5), Angle.ByDegree(20), Epoch.JNOW)), Is.True);
        }

        [Test]
        public async Task PulseGuide_SendsTheCommandAndCountsTheAxisBusyForItsDuration() {
            var (telescope, port) = await Connected(FakeOnStepXPort.Umi17S());

            telescope.PulseGuide(GuideDirections.guideNorth, 100);

            Assert.That(port.Written, Does.Contain(":Mgn0100#"));
            Assert.That(telescope.IsPulseGuiding, Is.True);
            await Task.Delay(300);
            Assert.That(telescope.IsPulseGuiding, Is.False);
        }

        [Test]
        public async Task IsPulseGuiding_WaitsUntilTheControllerEndsThePulse() {
            var (telescope, port) = await Connected(FakeOnStepXPort.Umi17S());
            // the controller still reports the pulse ('G') at the first two checks after its estimated end
            port.On(":GU#", "NpGEW260#", "NpGEW260#", "NpEW260#");

            telescope.PulseGuide(GuideDirections.guideNorth, 10);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (telescope.IsPulseGuiding && clock.ElapsedMilliseconds < 2000) {
                await Task.Delay(2);
            }

            Assert.That(telescope.IsPulseGuiding, Is.False);
            Assert.That(port.Written.Count(c => c == ":GU#"), Is.EqualTo(3));
            Assert.That(clock.ElapsedMilliseconds, Is.LessThan(500));
        }

        [Test]
        public async Task IsPulseGuiding_ControllerNeverEndsIt_GivesUpAfterASecond() {
            var (telescope, port) = await Connected(FakeOnStepXPort.Umi17S());
            port.On(":GU#", "NpGEW260#");

            telescope.PulseGuide(GuideDirections.guideNorth, 10);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (telescope.IsPulseGuiding && clock.ElapsedMilliseconds < 3000) {
                await Task.Delay(5);
            }

            Assert.That(clock.Elapsed, Is.GreaterThanOrEqualTo(TimeSpan.FromSeconds(1)).And.LessThan(TimeSpan.FromSeconds(2)));
        }

        [TestCase("nNPEW260#", "parked")]
        [TestCase("pEW260#", "goto")]
        [TestCase("NpEW262#", "limit")]
        public async Task PulseGuide_RefusedByTheController_ThrowsWithoutSending(string status, string reason) {
            var (telescope, port) = await Connected(FakeOnStepXPort.Umi17S());
            port.On(":GU#", status);
            _ = telescope.Slewing;

            Assert.That(() => telescope.PulseGuide(GuideDirections.guideNorth, 100),
                Throws.InvalidOperationException.With.Message.Contains(reason));
            Assert.That(port.Written.Any(c => c.StartsWith(":Mg")), Is.False);
        }

        [Test]
        public async Task PulseGuide_DoesNotReadTheStateFirst() {
            // a stale state would otherwise be read again (seven commands) before every pulse
            var (telescope, port) = await Connected(FakeOnStepXPort.Umi17S());

            telescope.PulseGuide(GuideDirections.guideWest, 100);

            Assert.That(port.Written, Is.EqualTo(new[] { ":Mgw0100#" }));
        }

        [Test]
        public async Task MountPulseOutput_PulsesBothAxesAtOnceOnlyWithOnStepX() {
            var (telescope, _) = await Connected(FakeOnStepXPort.Umi17S());
            var onStepX = new Mock<NINA.Equipment.Interfaces.Mediator.ITelescopeMediator>();
            onStepX.Setup(m => m.GetDevice()).Returns(telescope);
            var other = new Mock<NINA.Equipment.Interfaces.Mediator.ITelescopeMediator>();
            other.Setup(m => m.GetDevice()).Returns(Mock.Of<ITelescope>());

            Assert.That(new NINA.Equipment.Equipment.MyGuider.Internal.MountPulseOutput(onStepX.Object).SupportsSimultaneousPulses, Is.True);
            Assert.That(new NINA.Equipment.Equipment.MyGuider.Internal.MountPulseOutput(other.Object).SupportsSimultaneousPulses, Is.False);
        }

        [Test]
        public async Task PulseGuide_Zero_IsNotSent() {
            var (telescope, port) = await Connected(FakeOnStepXPort.Umi17S());

            telescope.PulseGuide(GuideDirections.guideEast, 0);

            Assert.That(port.Written.Any(c => c.StartsWith(":Mg")), Is.False);
            Assert.That(telescope.IsPulseGuiding, Is.False);
        }

        [Test]
        public async Task MoveAxis_PicksTheNearestRateAndDirection() {
            var (telescope, port) = await Connected(FakeOnStepXPort.Umi17S());

            // 8x sidereal is :R5#; positive primary is east, negative secondary south; 3.5°/s is the slew speed, :R9#
            telescope.MoveAxis(TelescopeAxes.Primary, 8 * 15.041 / 3600);
            telescope.MoveAxis(TelescopeAxes.Secondary, -3.5);
            telescope.MoveAxis(TelescopeAxes.Primary, 0);

            Assert.That(port.Written.Where(c => c != ":GU#" && !c.StartsWith(":G")),
                Is.EqualTo(new[] { ":R5#", ":Me#", ":R9#", ":Ms#", ":Qe#", ":Qw#" }));
        }

        [Test]
        public async Task SelectedMoveRate_StartsAtTheControllersAndIsSetWithR() {
            // the last-but-one character of :GU# "nNpEW260" is the controller's move rate, 6 = 20x
            var (telescope, port) = await Connected(FakeOnStepXPort.Umi17S());

            Assert.That(telescope.SelectedMoveRate, Is.EqualTo(6));
            telescope.SelectMoveRate(2);

            Assert.That(telescope.SelectedMoveRate, Is.EqualTo(2));
            Assert.That(port.Written, Is.EqualTo(new[] { ":R2#" }));
            Assert.That(() => telescope.SelectMoveRate(10), Throws.InstanceOf<ArgumentOutOfRangeException>());
        }

        [Test]
        public async Task MoveAxisDirection_MovesWithoutTouchingTheRate() {
            var (telescope, port) = await Connected(FakeOnStepXPort.Umi17S());

            telescope.MoveAxisDirection(TelescopeAxes.Secondary, 1);
            telescope.MoveAxisDirection(TelescopeAxes.Primary, -1);
            telescope.MoveAxisDirection(TelescopeAxes.Secondary, 0);

            Assert.That(port.Written.Where(c => !c.StartsWith(":G")), Is.EqualTo(new[] { ":Mn#", ":Mw#", ":Qn#", ":Qs#" }));
            Assert.That(telescope.SelectedMoveRate, Is.EqualTo(6));
        }

        [Test]
        public async Task MoveAxisDirection_KeepaliveRepeatsSendNothing() {
            // OnStepX re-reads the pier side on every :Mn#; at home that turned the mount back and forth
            var (telescope, port) = await Connected(FakeOnStepXPort.Umi17S());

            telescope.MoveAxisDirection(TelescopeAxes.Secondary, 1);
            telescope.MoveAxisDirection(TelescopeAxes.Secondary, 1);
            telescope.MoveAxisDirection(TelescopeAxes.Secondary, 1);
            telescope.MoveAxisDirection(TelescopeAxes.Secondary, -1);
            telescope.MoveAxisDirection(TelescopeAxes.Secondary, 0);
            telescope.MoveAxisDirection(TelescopeAxes.Secondary, 1);

            Assert.That(port.Written.Where(c => !c.StartsWith(":G")),
                Is.EqualTo(new[] { ":Mn#", ":Qn#", ":Ms#", ":Qn#", ":Qs#", ":Mn#" }));
        }

        [Test]
        public async Task GetAxisRates_SiderealMultiplesThenHalfAndFullSlewSpeed() {
            var (telescope, _) = await Connected(FakeOnStepXPort.Umi17S());

            var rates = telescope.GetAxisRates(TelescopeAxes.Primary);

            Assert.That(rates, Has.Count.EqualTo(10));
            Assert.That(rates[0].Item1, Is.EqualTo(0.25 * 15.0410686 / 3600).Within(1e-9));
            Assert.That(rates[8].Item1, Is.EqualTo(1.75));
            Assert.That(rates[9].Item1, Is.EqualTo(3.5));
        }

        [Test]
        public async Task TrackingMode_Lunar_SelectsTheRateAndStartsTracking() {
            var (telescope, port) = await Connected(FakeOnStepXPort.Umi17S().On(":Te#", "1"));
            port.On(":GU#", Idle, Tracking);

            telescope.TrackingMode = TrackingMode.Lunar;

            Assert.That(port.Written, Does.Contain(":TL#"));
            Assert.That(port.Written, Does.Contain(":Te#"));
            Assert.That(telescope.TrackingRate.TrackingMode, Is.EqualTo(TrackingMode.Lunar));
        }

        [Test]
        public async Task PortFailure_EndsTheConnection() {
            var (telescope, port) = await Connected(FakeOnStepXPort.Umi17S());
            port.FailWrites = new IOException("device unplugged");

            await Task.Delay(300);
            Assert.That(telescope.RightAscension, Is.NaN);
            await WaitUntil(() => !telescope.Connected);

            Assert.That(telescope.Connected, Is.False);
        }

        [Test]
        public async Task SilentController_EndsTheConnectionAfterRepeatedFailures() {
            var (telescope, port) = await Connected(FakeOnStepXPort.Umi17S());
            port.On(":GU#", (string?)null);

            for (int i = 0; i < 3 && telescope.Connected; i++) {
                await Task.Delay(300);
                _ = telescope.RightAscension;
            }
            await WaitUntil(() => !telescope.Connected);

            Assert.That(telescope.Connected, Is.False);
        }

        [Test]
        public async Task Disconnect_ClosesThePort() {
            var (telescope, port) = await Connected(FakeOnStepXPort.Umi17S());

            telescope.Disconnect();

            Assert.That(telescope.Connected, Is.False);
            Assert.That(port.IsOpen, Is.False);
            Assert.That(telescope.RightAscension, Is.NaN);
        }

        private static async Task WaitUntil(Func<bool> condition) {
            for (int i = 0; i < 100 && !condition(); i++) {
                await Task.Delay(20);
            }
        }
    }
}
