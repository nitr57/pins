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
using NINA.Equipment.SDK.TelescopeSDKs.OnStepXSDK;
using NINA.Profile.Interfaces;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Test.Equipment.OnStepX {

    /// <summary>
    /// Against a real OnStepX mount. Only the pulse test moves it (a few arcseconds north and back south). Runs only when
    /// selected, with the port in ONSTEPX_PORT, e.g.
    /// ONSTEPX_PORT=/dev/ttyUSB0 dotnet test --filter "FullyQualifiedName~OnStepXHardwareTest".
    /// </summary>
    [TestFixture]
    [Explicit("needs an OnStepX mount on ONSTEPX_PORT")]
    [Category("Hardware")]
    [NonParallelizable]
    public class OnStepXHardwareTest {

        private static string PortName() {
            string? port = Environment.GetEnvironmentVariable("ONSTEPX_PORT");
            if (string.IsNullOrEmpty(port)) {
                Assert.Ignore("ONSTEPX_PORT is not set");
                return string.Empty;
            }
            return port;
        }

        [Test]
        public void Connect_ReadsIdentificationAndState() {
            using var device = OnStepXDevice.Connect(new OnStepXTransport(new OnStepXSerialPort(PortName())));

            TestContext.Out.WriteLine($"{device.Model}: {device.Identification}");
            if (device is ProxiskyUmiDevice umi) {
                TestContext.Out.WriteLine($"Proxisky {umi.ModelReply}, power-loss memory {umi.HasPowerLossMemory}, anti-collision {umi.HasAntiCollision}");
            }
            var status = device.GetStatus();
            TestContext.Out.WriteLine($":GU# {status.Raw}: tracking {status.Tracking}, slewing {status.Slewing}, park {status.Park}, home {status.AtHome}, {status.MountType}, error {status.Error}");
            TestContext.Out.WriteLine($"RA {device.GetRightAscension():F5} h, Dec {device.GetDeclination():F4}°, pier {device.GetPierSide()}, guide rate {device.GetPulseGuideRate():F2}x");

            Assert.That(device.Identification.MajorVersion, Is.GreaterThanOrEqualTo(OnStepXDevice.MinMajorVersion));
        }

        /// <summary>
        /// Opening or closing the port must not reset an ESP32 controller (DTR/RTS): after a reset it stays silent for
        /// about 8 s, so the ACK right after reopening would go unanswered.
        /// </summary>
        [Test]
        public void Reopening_DoesNotRestartTheController() {
            string portName = PortName();
            using (var first = new OnStepXTransport(new OnStepXSerialPort(portName))) {
                first.Open();
                Assert.That(first.SendForChar("\u0006"), Is.Not.Null, "no answer after the first open: not connected, or opening restarted the controller");
            }

            using var second = new OnStepXTransport(new OnStepXSerialPort(portName));
            var clock = Stopwatch.StartNew();
            second.Open();
            char? ack = second.SendForChar("\u0006");
            TestContext.Out.WriteLine($"ACK after reopening: '{ack}' in {clock.ElapsedMilliseconds} ms");

            Assert.That(ack, Is.Not.Null, "no answer right after reopening: the controller restarted");
        }

        /// <summary>
        /// Guide pulses through the driver: <see cref="OnStepXTelescope.IsPulseGuiding"/> must stay true until the
        /// controller has ended the pulse ('G' gone from :GU#). Moves the mount about 4.5" north and back.
        /// </summary>
        [Test]
        public async Task PulseGuide_IsPulseGuidingCoversThePulse() {
            var profile = new NINA.Profile.Profile();
            profile.TelescopeSettings.SerialPort = PortName();
            profile.TelescopeSettings.TimeSync = false;
            var profileService = new Mock<IProfileService>();
            profileService.SetupGet(p => p.ActiveProfile).Returns(profile);
            using var telescope = new OnStepXTelescope(profileService.Object);
            Assert.That(await telescope.Connect(CancellationToken.None), Is.True);

            foreach (var direction in new[] { GuideDirections.guideNorth, GuideDirections.guideSouth }) {
                var clock = Stopwatch.StartNew();
                telescope.PulseGuide(direction, 300);
                while (telescope.IsPulseGuiding && clock.ElapsedMilliseconds < 3000) {
                    await Task.Delay(5);
                }
                TestContext.Out.WriteLine($"{direction} 300 ms: done after {clock.ElapsedMilliseconds} ms");
                Assert.That(clock.ElapsedMilliseconds, Is.InRange(300, 1500));
                await Task.Delay(300);
            }
            telescope.Disconnect();
        }

        private static async Task<OnStepXTelescope> ConnectTelescope() {
            var profile = new NINA.Profile.Profile();
            profile.TelescopeSettings.SerialPort = PortName();
            profile.TelescopeSettings.TimeSync = false;
            profile.ApplicationSettings.DevicePollingInterval = 0.25;
            var profileService = new Mock<IProfileService>();
            profileService.SetupGet(p => p.ActiveProfile).Returns(profile);
            var telescope = new OnStepXTelescope(profileService.Object);
            Assert.That(await telescope.Connect(CancellationToken.None), Is.True);
            return telescope;
        }

        /// <summary>
        /// A goto stopped on the way (:Q#) must fail, a goto run to the end must arrive within the tolerance. Slews to
        /// an hour west of the meridian at Dec +50° and back home.
        /// </summary>
        [Test]
        public async Task Goto_StoppedOnTheWay_ThrowsAndAFullGotoArrives() {
            using var telescope = await ConnectTelescope();
            try {
                Assert.That(telescope.SiderealTime, Is.Not.NaN);
                var target = new Coordinates(Angle.ByHours(AstroUtil.EuclidianModulus(telescope.SiderealTime - 1, 24)), Angle.ByDegree(50), Epoch.JNOW);
                TestContext.Out.WriteLine($"target {target}, LST {telescope.SiderealTime:F4} h");

                var slew = telescope.SlewToCoordinates(target, CancellationToken.None);
                await Task.Delay(TimeSpan.FromSeconds(3));
                Assert.That(slew.IsCompleted, Is.False, "the goto ended before the stop");
                var clock = Stopwatch.StartNew();
                telescope.StopSlew();
                var stopped = Assert.ThrowsAsync<InvalidOperationException>(async () => await slew);
                TestContext.Out.WriteLine($"stopped goto after {clock.ElapsedMilliseconds} ms: {stopped?.Message}");
                Assert.That(stopped?.Message, Does.Contain("from the target"));

                Assert.That(await telescope.SlewToCoordinates(target, CancellationToken.None), Is.True);
                var reached = telescope.Coordinates;
                TestContext.Out.WriteLine($"full goto reached {reached}, {(reached - target).Distance.ArcSeconds:F0}\" from the target");
                Assert.That((reached - target).Distance.Degree, Is.LessThan(1));
            } finally {
                telescope.StopSlew();
                telescope.TrackingEnabled = false;
                await telescope.FindHome(CancellationToken.None);
                telescope.Disconnect();
            }
        }

        /// <summary>
        /// A park stopped on the way (:Q#) returns the mount to unparked: Park must fail at once. Slews away from the park
        /// position first, so the park has a way to go, and homes afterwards.
        /// </summary>
        [Test]
        public async Task Park_AbortedOnTheWay_FailsAtOnce() {
            using var telescope = await ConnectTelescope();
            try {
                if (telescope.AtPark) {
                    await telescope.Unpark(CancellationToken.None);
                }
                Assert.That(telescope.SiderealTime, Is.Not.NaN);
                var away = new Coordinates(Angle.ByHours(AstroUtil.EuclidianModulus(telescope.SiderealTime - 1, 24)), Angle.ByDegree(50), Epoch.JNOW);
                Assert.That(await telescope.SlewToCoordinates(away, CancellationToken.None), Is.True);

                var park = telescope.Park(CancellationToken.None);
                await Task.Delay(TimeSpan.FromSeconds(3));
                if (park.IsFaulted && park.Exception?.InnerException is InvalidOperationException refused) {
                    Assert.Ignore($"the mount did not start parking: {refused.Message}");
                }
                Assert.That(park.IsCompleted, Is.False, "the park ended before the stop");

                var clock = Stopwatch.StartNew();
                telescope.StopSlew();
                var aborted = Assert.ThrowsAsync<InvalidOperationException>(async () => await park);
                TestContext.Out.WriteLine($"aborted park after {clock.ElapsedMilliseconds} ms: {aborted?.Message}");
                Assert.That(aborted?.Message, Does.Contain("park was aborted"));
                Assert.That(clock.Elapsed, Is.LessThan(TimeSpan.FromSeconds(20)));
                Assert.That(telescope.AtPark, Is.False);
            } finally {
                telescope.StopSlew();
                if (telescope.AtPark) {
                    await telescope.Unpark(CancellationToken.None);
                }
                telescope.TrackingEnabled = false;
                await telescope.FindHome(CancellationToken.None);
                telescope.Disconnect();
            }
        }
    }
}
