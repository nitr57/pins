#region "copyright"

/*
    Copyright © 2026 Nico Trost <nico.trost57@gmail.com> and the PI.N.S. contributors

    This file is part of PI 'N' Stars.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Equipment.SDK.TelescopeSDKs.OnStepXSDK;
using System;

namespace NINA.Test.Equipment.OnStepX {

    /// <summary>Command formats and replies as INDI's LX200_OnStep and lx200driver use them.</summary>
    [TestFixture]
    public class OnStepXCommandsTest {

        private static (OnStepXDevice Device, FakeOnStepXPort Port) Connect(FakeOnStepXPort? port = null) {
            port ??= FakeOnStepXPort.Umi17S();
            var device = OnStepXDevice.Connect(new OnStepXTransport(port), TimeSpan.Zero, TimeSpan.Zero);
            port.Written.Clear();
            return (device, port);
        }

        [TestCase(4 + 15 / 3600.0, "04:00:15")]
        [TestCase(0.0, "00:00:00")]
        [TestCase(23.9999999, "00:00:00")]
        [TestCase(-1.0, "23:00:00")]
        [TestCase(12.5 + 29.6 / 3600, "12:30:30")]
        public void FormatRightAscension(double hours, string expected) {
            Assert.That(OnStepXDevice.FormatRightAscension(hours), Is.EqualTo(expected));
        }

        [TestCase(90.0, "+90*00:00")]
        [TestCase(0.0, "+00*00:00")]
        [TestCase(-(5 + 30 / 60.0 + 36 / 3600.0), "-05*30:36")]
        [TestCase(-0.5, "-00*30:00")]
        [TestCase(45.5, "+45*30:00")]
        public void FormatDeclination(double degrees, string expected) {
            Assert.That(OnStepXDevice.FormatDeclination(degrees), Is.EqualTo(expected));
        }

        [TestCase(48 + 40 / 60.0, "+48:40:0.00")]
        [TestCase(-(33 + 52 / 60.0 + 4.5 / 3600), "-33:52:4.50")]
        [TestCase(-0.5, "-00:30:0.00")]
        public void FormatSiteLatitude(double latitude, string expected) {
            Assert.That(OnStepXDevice.FormatSiteLatitude(latitude), Is.EqualTo(expected));
        }

        [TestCase(360 - (8 + 14 / 60.0), "351:46:0.00")]
        [TestCase(70.5, "070:30:0.00")]
        public void FormatSiteLongitude(double westLongitude, string expected) {
            Assert.That(OnStepXDevice.FormatSiteLongitude(westLongitude), Is.EqualTo(expected));
        }

        // INDI LX200_OnStep::setUTCOffset
        [TestCase(2.0, "-02:00")]
        [TestCase(-5.0, "005:00")]
        [TestCase(0.0, "000:00")]
        [TestCase(5.5, "-05:30")]
        [TestCase(5.75, "-05:45")]
        public void FormatUtcOffset(double offsetHours, string expected) {
            Assert.That(OnStepXDevice.FormatUtcOffset(offsetHours), Is.EqualTo(expected));
        }

        [Test]
        public void Goto_SetsTheTargetAndStarts() {
            var (device, port) = Connect(FakeOnStepXPort.Umi17S()
                .On(":Sr04:00:15#", "1")
                .On(":Sd+45*30:00#", "1")
                .On(":MS#", "0"));

            var error = device.Goto(4 + 15 / 3600.0, 45.5);

            Assert.That(error, Is.EqualTo(OnStepXGotoError.None));
            Assert.That(port.Written, Is.EqualTo(new[] { ":Sr04:00:15#", ":Sd+45*30:00#", ":MS#" }));
        }

        [Test]
        public void Goto_Refused_ReturnsTheReason() {
            var (device, port) = Connect(FakeOnStepXPort.Umi17S()
                .On(":Sr04:00:15#", "1")
                .On(":Sd-80*00:00#", "1")
                .On(":MS#", "1Object below horizon#"));

            Assert.That(device.Goto(4 + 15 / 3600.0, -80), Is.EqualTo(OnStepXGotoError.BelowHorizon));
            Assert.That(port.Unread, Is.Zero);
        }

        [Test]
        public void Goto_TargetRefused_Throws() {
            var (device, port) = Connect(FakeOnStepXPort.Umi17S()
                .On(":Sr04:00:15#", "0"));

            Assert.That(() => device.Goto(4 + 15 / 3600.0, 45.5), Throws.InstanceOf<OnStepXException>());
            Assert.That(port.Written, Has.None.EqualTo(":MS#"));
        }

        [TestCase("N/A#", OnStepXGotoError.None)]
        [TestCase("E6#", OnStepXGotoError.OutsideLimits)]
        public void Sync(string reply, OnStepXGotoError expected) {
            var (device, port) = Connect(FakeOnStepXPort.Umi17S()
                .On(":Sr04:00:15#", "1")
                .On(":Sd+45*30:00#", "1")
                .On(":CM#", reply));

            Assert.That(device.Sync(4 + 15 / 3600.0, 45.5), Is.EqualTo(expected));
            Assert.That(port.Written[^1], Is.EqualTo(":CM#"));
        }

        [Test]
        public void Sync_UnexpectedReply_Throws() {
            var (device, _) = Connect(FakeOnStepXPort.Umi17S()
                .On(":Sr04:00:15#", "1")
                .On(":Sd+45*30:00#", "1")
                .On(":CM#", "0"));

            Assert.That(() => device.Sync(4 + 15 / 3600.0, 45.5), Throws.InstanceOf<OnStepXException>());
        }

        [TestCase(OnStepXDirection.North, 150, ":Mgn0150#")]
        [TestCase(OnStepXDirection.East, 1, ":Mge0001#")]
        [TestCase(OnStepXDirection.West, 20000, ":Mgw9999#")]
        public void PulseGuide(OnStepXDirection direction, int ms, string expected) {
            var (device, port) = Connect();

            device.PulseGuide(direction, ms);

            Assert.That(port.Written, Is.EqualTo(new[] { expected }));
        }

        [Test]
        public void PulseGuide_ZeroIsNeverSent() {
            // :Mg with 0 ms never ends on OnStep
            var (device, port) = Connect();

            device.PulseGuide(OnStepXDirection.North, 0);

            Assert.That(port.Written, Is.Empty);
        }

        [Test]
        public void Move_RateDirectionAndStop() {
            var (device, port) = Connect();

            device.SetMoveRate(5);
            device.StartMove(OnStepXDirection.East);
            device.StopMove(OnStepXDirection.East);
            device.Abort();

            Assert.That(port.Written, Is.EqualTo(new[] { ":R5#", ":Me#", ":Qe#", ":Q#" }));
            Assert.That(() => device.SetMoveRate(10), Throws.InstanceOf<ArgumentOutOfRangeException>());
        }

        [Test]
        public void ParkUnparkHomeTracking_Accepted() {
            var (device, port) = Connect(FakeOnStepXPort.Umi17S()
                .On(":hR#", "1")
                .On(":hQ#", "1")
                .On(":Te#", "1"));

            Assert.That(device.Park(), Is.True);
            Assert.That(device.Unpark(), Is.True);
            device.SetParkPosition();
            device.FindHome();
            device.SetTracking(true);
            device.SetTrackingRate(OnStepXTrackingRate.Lunar);

            // :hC# has no reply, so its error is read with :GE# right after; the others only on a refusal
            Assert.That(port.Written, Is.EqualTo(new[] { ":hP#", ":hR#", ":hQ#", ":hC#", ":GE#", ":Te#", ":TL#" }));
        }

        [Test]
        public void Park_Refused_ThrowsTheControllersReason() {
            var (device, port) = Connect(FakeOnStepXPort.Umi17S().On(":hP#", "0").On(":GE#", "12#"));

            var ex = Assert.Throws<OnStepXCommandRefusedException>(() => device.Park());

            Assert.That(ex!.Error, Is.EqualTo(OnStepXCommandError.NoParkPositionSet));
            Assert.That(ex.Message, Does.Contain("no park position set"));
            Assert.That(port.Written, Is.EqualTo(new[] { ":hP#", ":GE#" }));
        }

        [Test]
        public void FindHome_Refused_ThrowsTheControllersReason() {
            var (device, _) = Connect(FakeOnStepXPort.Umi17S().On(":GE#", "17#"));

            var ex = Assert.Throws<OnStepXCommandRefusedException>(() => device.FindHome());

            Assert.That(ex!.Error, Is.EqualTo(OnStepXCommandError.Standby));
        }

        [Test]
        public void Unpark_AnsweredOneNothingOrZero() {
            var (accepted, _) = Connect(FakeOnStepXPort.Umi17S().On(":hR#", "1"));
            var (silent, _) = Connect(FakeOnStepXPort.Umi17S().On(":hR#", (string?)null));
            var (refused, _) = Connect(FakeOnStepXPort.Umi17S().On(":hR#", "0").On(":GE#", "11#"));

            Assert.That(accepted.Unpark(), Is.True);
            Assert.That(silent.Unpark(), Is.False, "a lost reply leaves the decision to the status");
            Assert.That(() => refused.Unpark(), Throws.InstanceOf<OnStepXCommandRefusedException>().With.Property("Error").EqualTo(OnStepXCommandError.NotParked));
        }

        [Test]
        public void SetTracking_Refused_Throws() {
            var (device, _) = Connect(FakeOnStepXPort.Umi17S().On(":Te#", "0").On(":GE#", "17#"));

            Assert.That(() => device.SetTracking(true), Throws.InstanceOf<OnStepXCommandRefusedException>());
        }

        [Test]
        public void Refusal_WithAnUnreadableReason_StillThrows() {
            var (device, _) = Connect(FakeOnStepXPort.Umi17S().On(":hQ#", "0").On(":GE#", "0"));

            var ex = Assert.Throws<OnStepXCommandRefusedException>(() => device.SetParkPosition());

            Assert.That(ex!.Error, Is.Null);
            Assert.That(ex.Message, Does.Contain("reason unknown"));
        }

        [Test]
        public void GetSite_LongitudeIsPositiveEast() {
            var (device, _) = Connect();

            var (latitude, longitude) = device.GetSite();

            Assert.That(latitude, Is.EqualTo(48 + 40 / 60.0).Within(1e-9));
            Assert.That(longitude, Is.EqualTo(8 + 14 / 60.0).Within(1e-9));
        }

        [Test]
        public void GetSite_FallsBackToLowPrecision() {
            var (device, _) = Connect(FakeOnStepXPort.Umi17S().On(":GtH#", "0").On(":Gt#", "+48*40#"));

            Assert.That(device.GetSite().Latitude, Is.EqualTo(48 + 40 / 60.0).Within(1e-9));
        }

        [Test]
        public void SetSite_AsIndiUpdateLocation() {
            var (device, port) = Connect(FakeOnStepXPort.Umi17S()
                .On(":Sg351:46:0.00#", "1")
                .On(":St+48:40:0.00#", "1"));

            device.SetSite(48 + 40 / 60.0, 8 + 14 / 60.0);

            Assert.That(port.Written, Is.EqualTo(new[] { ":Sg351:46:0.00#", ":St+48:40:0.00#" }));
        }

        [Test]
        public void GetUtcDate_LocalTimePlusOffset() {
            // read from the UMi17S at 20:57 UTC (22:57 CEST)
            var (device, _) = Connect();

            Assert.That(device.GetUtcDate(), Is.EqualTo(new DateTime(2026, 10, 9, 20, 57, 0, DateTimeKind.Utc)));
        }

        [Test]
        public void SetUtcDate_OffsetTimeDate() {
            var (device, port) = Connect(FakeOnStepXPort.Umi17S()
                .On(":SG-02:00#", "1")
                .On(":SL00:30:05#", "1")
                .On(":SC10/10/26#", "1"));

            device.SetUtcDate(new DateTime(2026, 10, 9, 22, 30, 5, DateTimeKind.Utc), TimeSpan.FromHours(2));

            Assert.That(port.Written, Is.EqualTo(new[] { ":SG-02:00#", ":SL00:30:05#", ":SC10/10/26#" }));
        }

        [Test]
        public void ReadsOfTheUmi17S() {
            var (device, _) = Connect();

            Assert.That(device.GetAltitude(), Is.EqualTo(48 + 40 / 60.0).Within(1e-9));
            Assert.That(device.GetAzimuth(), Is.EqualTo(1 / 3600.0).Within(1e-9));
            Assert.That(device.GetSiderealTime(), Is.EqualTo(22 + 43 / 60.0 + 54 / 3600.0).Within(1e-9));
            Assert.That(device.GetSlewSpeed(), Is.EqualTo(3.5));
        }

        [Test]
        public void GetSlewSpeed_Unknown_IsNaN() {
            var (device, _) = Connect(FakeOnStepXPort.Umi17S().On(":GX97#", "0"));

            Assert.That(device.GetSlewSpeed(), Is.NaN);
        }
    }
}
