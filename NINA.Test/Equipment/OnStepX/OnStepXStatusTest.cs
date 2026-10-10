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

namespace NINA.Test.Equipment.OnStepX {

    [TestFixture]
    public class OnStepXStatusTest {

        private static OnStepXStatus Parse(string reply) {
            Assert.That(OnStepXStatus.TryParse(reply, out var status), Is.True, reply);
            return status!;
        }

        [Test]
        public void TryParse_Umi17SAtHome() {
            var status = Parse("nNpEW260");

            Assert.That(status.Tracking, Is.False);
            Assert.That(status.Slewing, Is.False);
            Assert.That(status.Park, Is.EqualTo(OnStepXParkState.Unparked));
            Assert.That(status.MountType, Is.EqualTo(OnStepXMountType.GermanEquatorial));
            Assert.That(status.PulseGuideRateIndex, Is.EqualTo(2));
            Assert.That(status.MoveRateIndex, Is.EqualTo(6));
            Assert.That(status.Error, Is.EqualTo(OnStepXError.None));
            Assert.That(status.Raw, Is.EqualTo("nNpEW260"));
        }

        // INDI LX200_OnStep::ReadScopeStatus: n+N idle, n alone slewing, N alone tracking, neither slewing while tracking
        [TestCase("nNpE260", false, false)]
        [TestCase("npE260", false, true)]
        [TestCase("NpE260", true, false)]
        [TestCase("pE260", true, true)]
        public void TryParse_TrackingAndSlewing(string reply, bool tracking, bool slewing) {
            var status = Parse(reply);

            Assert.That(status.Tracking, Is.EqualTo(tracking));
            Assert.That(status.Slewing, Is.EqualTo(slewing));
        }

        [TestCase("nNpE260", OnStepXParkState.Unparked)]
        [TestCase("nIE260", OnStepXParkState.Parking)]
        [TestCase("nNPE260", OnStepXParkState.Parked)]
        [TestCase("nNFE260", OnStepXParkState.ParkFailed)]
        public void TryParse_ParkState(string reply, OnStepXParkState park) {
            var status = Parse(reply);

            Assert.That(status.Park, Is.EqualTo(park));
        }

        [TestCase("NpEW260", OnStepXTrackingRate.Sidereal)]
        [TestCase("Np(EW260", OnStepXTrackingRate.Lunar)]
        [TestCase("NpOEW260", OnStepXTrackingRate.Solar)]
        [TestCase("NpkEW260", OnStepXTrackingRate.King)]
        public void TryParse_TrackingRate(string reply, OnStepXTrackingRate rate) {
            Assert.That(Parse(reply).TrackingRate, Is.EqualTo(rate));
        }

        [TestCase("NpEW260", OnStepXCompensation.None)]
        [TestCase("NprEW260", OnStepXCompensation.RefractionDual)]
        [TestCase("NprsEW260", OnStepXCompensation.Refraction)]
        [TestCase("NptEW260", OnStepXCompensation.ModelDual)]
        [TestCase("NptsEW260", OnStepXCompensation.Model)]
        public void TryParse_Compensation(string reply, OnStepXCompensation compensation) {
            Assert.That(Parse(reply).Compensation, Is.EqualTo(compensation));
        }

        [TestCase("nNpE260", OnStepXMountType.GermanEquatorial)]
        [TestCase("nNpK260", OnStepXMountType.Fork)]
        [TestCase("nNpk260", OnStepXMountType.Unknown)]
        [TestCase("nNpA260", OnStepXMountType.AltAz)]
        [TestCase("nNpL260", OnStepXMountType.AltAlt)]
        [TestCase("nNp260", OnStepXMountType.Unknown)]
        public void TryParse_MountType(string reply, OnStepXMountType mountType) {
            var status = Parse(reply);

            Assert.That(status.MountType, Is.EqualTo(mountType));
        }

        [Test]
        public void TryParse_HomeFlags() {
            var parkedAtHome = Parse("nNPHE260");
            var waiting = Parse("NpwE260");

            Assert.That(parkedAtHome.AtHome, Is.True);
            Assert.That(parkedAtHome.WaitingAtHome, Is.False);
            Assert.That(waiting.AtHome, Is.False);
            Assert.That(waiting.WaitingAtHome, Is.True);
        }

        [Test]
        public void TryParse_HomingAndPulseGuideFlags() {
            var homing = Parse("nphET290");
            var guiding = Parse("NpGEW260");

            Assert.That(homing.Homing, Is.True);
            Assert.That(homing.Slewing, Is.True);
            Assert.That(homing.AtHome, Is.False);
            Assert.That(homing.PulseGuiding, Is.False);
            Assert.That(guiding.PulseGuiding, Is.True);
            Assert.That(guiding.Homing, Is.False);
        }

        [TestCase("nNpET290", OnStepXPierSide.East)]
        [TestCase("nNpEW260", OnStepXPierSide.West)]
        [TestCase("nNphEo290", OnStepXPierSide.Unknown)]
        public void TryParse_PierSide(string reply, OnStepXPierSide pierSide) {
            Assert.That(Parse(reply).PierSide, Is.EqualTo(pierSide));
        }

        [Test]
        public void TryParse_WaitingAtHomeIsNotThePierSide() {
            // lowercase 'w' is waiting at home, uppercase 'W' the west pier side
            Assert.That(Parse("NpwET260").PierSide, Is.EqualTo(OnStepXPierSide.East));
        }

        [Test]
        public void TryParse_ManualMoveIsNotAGoto() {
            var manual = Parse("NpgET290");
            var pulse = Parse("NpGET260");

            Assert.That(manual.ManualMove, Is.True);
            Assert.That(manual.Slewing, Is.False, "'N' stays: a manual move is no goto");
            Assert.That(pulse.ManualMove, Is.False);
        }

        [Test]
        public void TryParse_TheTrailingDigitsAreNotReadAsFlags() {
            // error 15 is sent as '0' + 15 = '?', error 11 as ';'; a park state hidden in them must not count
            Assert.That(OnStepXStatus.TryParse("nN2P?", out _), Is.False);
            var status = Parse("nNpE26?");

            Assert.That(status.Error, Is.EqualTo(OnStepXError.NvInitFailed));
        }

        [TestCase("nNpE261", OnStepXError.MotorFault)]
        [TestCase("nNpE26:", OnStepXError.GotoSyncFailed)]
        [TestCase("nNpE26<", OnStepXError.AltitudeMax)]
        public void TryParse_ErrorCode(string reply, OnStepXError error) {
            var status = Parse(reply);

            Assert.That(status.Error, Is.EqualTo(error));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("0")]
        [TestCase("nNE260")]
        public void TryParse_RejectsAReplyWithoutParkState(string? reply) {
            Assert.That(OnStepXStatus.TryParse(reply, out var status), Is.False);
            Assert.That(status, Is.Null);
        }

        [Test]
        public void TryParse_RateIndexThatIsNotADigit_IsMinusOne() {
            var status = Parse("nNpEx?0");

            Assert.That(status.PulseGuideRateIndex, Is.EqualTo(-1));
            Assert.That(status.MoveRateIndex, Is.EqualTo(-1));
        }
    }
}
