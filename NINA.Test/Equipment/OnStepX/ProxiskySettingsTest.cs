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
using System.Linq;

namespace NINA.Test.Equipment.OnStepX {

    /// <summary>The Proxisky vendor settings, with the commands and replies of INDI's LX200_Proxisky.</summary>
    [TestFixture]
    public class ProxiskySettingsTest {

        /// <summary>
        /// A UMi20S 1.0.6 as INDI's LX200_Proxisky measured it: LED, ACS and power-loss memory fitted, RA limits but no
        /// Dec limits or Dec second home (both answered with a bare "0", the firmware's unknown command).
        /// </summary>
        private static FakeOnStepXPort Umi20S() => FakeOnStepXPort.Umi17S()
            .On(":Pbvg#", "UMi20S|1.0.6#")
            .On(":PsLga#", "1#")
            .On(":PsLgb#", "1#")
            .On(":Pscg#", "0#")
            .On(":PsAg#", "1#")
            .On(":PsShg#", "0#")
            .On(":Psag#", "0#")
            .On(":Pnbg#", "95|90#")
            .On(":PsSrg#", "1#")
            .On(":PsCg#", "1#")
            .On(":Pncg#", "1000|2000#")
            .On(":Pnccg#", "3|0#")
            .On(":Pndgo#", "0#")
            .On(":Pndgv#", "10#")
            .On(":Pnag0#", "300|200|200|50|0|0|0#")
            .On(":Pnag1#", "200|200|200|35|0|0|0#");

        private static FakeOnStepXPort Umi17X() => Umi20S()
            .On(":Pbvg#", "UMi17X|1.0.7#")
            .On(":PsDgb#", "0#")
            .On(":PsSdG#", "120|130#")
            .On(":PsSdg#", "1#");

        private static (ProxiskyUmiDevice Device, FakeOnStepXPort Port) Connect(FakeOnStepXPort port) {
            var device = (ProxiskyUmiDevice)OnStepXDevice.Connect(new OnStepXTransport(port), TimeSpan.Zero, TimeSpan.Zero);
            port.Written.Clear();
            return (device, port);
        }

        [Test]
        public void ReadSettings_Umi20S_ReadsWhatTheMountHas() {
            var (device, _) = Connect(Umi20S());

            var s = device.ReadSettings();

            Assert.That(s.StatusLed, Is.True);
            Assert.That(s.PowerLossMemory, Is.False);
            Assert.That(s.AsiairHoming, Is.True);
            Assert.That(s.SupervisedHome, Is.False);
            Assert.That(s.DecSecondHome, Is.Null);
            Assert.That(s.AutoTracking, Is.False);
            Assert.That((s.RaLimits, s.RaLimitLeft, s.RaLimitRight), Is.EqualTo((true, 95, 90)));
            Assert.That((s.DecLimits, s.DecLimitCcw, s.DecLimitCw), Is.EqualTo(((bool?)null, (int?)null, (int?)null)));
            Assert.That((s.AntiCollision, s.AcsThresholdRa, s.AcsThresholdDec), Is.EqualTo((true, 1000, 2000)));
            Assert.That((s.AcsCollisionsRa, s.AcsCollisionsDec), Is.EqualTo((3, 0)));
            Assert.That((s.SupervisedGoto, s.SupervisedGotoTolerance), Is.EqualTo((false, 10)));
            Assert.That(s.RaPid, Is.EqualTo(new ProxiskyPidGains(300, 200, 200, 50)));
            Assert.That(s.DecPid, Is.EqualTo(new ProxiskyPidGains(200, 200, 200, 35)));
            Assert.That((s.CanSetDecLimits, s.CanSetDecSecondHome, s.CanSetPid), Is.EqualTo((false, false, true)));
            Assert.That(s.RestartPending, Is.Empty);
        }

        [Test]
        public void ReadSettings_Umi17X_HasDecLimitsAndMayChangeThem() {
            var (device, _) = Connect(Umi17X());

            var s = device.ReadSettings();

            Assert.That((s.DecLimits, s.DecLimitCcw, s.DecLimitCw), Is.EqualTo((true, 120, 130)));
            Assert.That(s.DecSecondHome, Is.False);
            Assert.That((s.CanSetDecLimits, s.CanSetDecSecondHome, s.CanSetPid), Is.EqualTo((true, true, true)));
        }

        [Test]
        public void ReadSettings_Umi17S_ShowsThePidGainsReadOnly() {
            var (device, _) = Connect(Umi20S().On(":Pbvg#", "UMi17S|1.0.7#"));

            var s = device.ReadSettings();

            Assert.That(s.RaPid, Is.Not.Null);
            Assert.That(s.CanSetPid, Is.False);
        }

        [Test]
        public void ReadSettings_NoLedOrAntiCollisionFitted_DoesNotAskForTheirState() {
            var (device, port) = Connect(Umi20S().On(":PsLga#", "0#").On(":PbC#", "0#"));

            var s = device.ReadSettings();

            Assert.That(s.StatusLed, Is.Null);
            Assert.That((s.AntiCollision, s.AcsThresholdRa, s.AcsCollisionsRa), Is.EqualTo(((bool?)null, (int?)null, (int?)null)));
            Assert.That(port.Written, Has.None.EqualTo(":PsLgb#").And.None.EqualTo(":PsCg#").And.None.EqualTo(":Pncg#").And.None.EqualTo(":Pnccg#"));
        }

        [Test]
        public void ReadSettings_ToleranceOutsideTheRange_IsNull() {
            // a bare "0" is the vendor's "unsupported" for single numbers too
            var (device, _) = Connect(Umi20S().On(":Pndgv#", "0#"));

            Assert.That(device.ReadSettings().SupervisedGotoTolerance, Is.Null);
        }

        [Test]
        public void SetSwitch_SendsTheVendorCommand() {
            var (device, port) = Connect(Umi20S().On(":PsAs0#", "1#").On(":Pndss1#", "1#"));

            device.SetSwitch(ProxiskySwitch.AsiairHoming, false);
            device.SetSwitch(ProxiskySwitch.SupervisedGoto, true);

            Assert.That(port.Written, Is.EqualTo(new[] { ":PsAs0#", ":Pndss1#" }));
        }

        [Test]
        public void SetSwitch_Refused_Throws() {
            // a UMi20S answers every supervised home change with "0#"
            var (device, _) = Connect(Umi20S().On(":PsShs1#", "0#"));

            Assert.That(() => device.SetSwitch(ProxiskySwitch.SupervisedHome, true),
                Throws.InstanceOf<OnStepXCommandRefusedException>().With.Property("Command").EqualTo(":PsShs1#"));
        }

        [Test]
        public void SetSwitch_NoAcknowledgement_Throws() {
            var (device, _) = Connect(Umi20S().On(":PsAs1#", (string?)null));

            Assert.That(() => device.SetSwitch(ProxiskySwitch.AsiairHoming, true),
                Throws.InstanceOf<OnStepXException>().With.Message.Contains("No acknowledgement"));
        }

        [Test]
        public void SetSwitch_StatusLed_ShowsTheWrittenValueUntilThePowerCycle() {
            // the mount keeps reading back the LED it runs
            var (device, _) = Connect(Umi20S().On(":PsLs0#", "1#").On(":PsLs1#", "1#"));

            device.SetSwitch(ProxiskySwitch.StatusLed, false);
            var pending = device.ReadSettings();
            device.SetSwitch(ProxiskySwitch.StatusLed, true);
            var back = device.ReadSettings();

            Assert.That(pending.StatusLed, Is.False);
            Assert.That(pending.RestartPending, Is.EqualTo(new[] { "StatusLed" }));
            Assert.That(back.StatusLed, Is.True);
            Assert.That(back.RestartPending, Is.Empty);
        }

        [Test]
        public void SetSwitch_DecSettingsOnAUmi20S_AreRefusedWithoutACommand() {
            var (device, port) = Connect(Umi20S());

            Assert.That(() => device.SetSwitch(ProxiskySwitch.DecLimits, true), Throws.InstanceOf<OnStepXException>().With.Message.Contains("UMi17X"));
            Assert.That(() => device.SetSwitch(ProxiskySwitch.DecSecondHome, true), Throws.InstanceOf<OnStepXException>().With.Message.Contains("UMi17X"));
            Assert.That(() => device.SetDecLimits(120, 120), Throws.InstanceOf<OnStepXException>().With.Message.Contains("UMi17X"));
            Assert.That(port.Written, Is.Empty);
        }

        [Test]
        public void SetDecLimits_Umi17X_SendsBothSides() {
            var (device, port) = Connect(Umi17X().On(":PsSdl120#", "1#").On(":PsSdr200#", "1#"));

            device.SetDecLimits(120, 200);

            Assert.That(port.Written, Is.EqualTo(new[] { ":PsSdl120#", ":PsSdr200#" }));
        }

        [Test]
        public void SetRaLimits_OneSideRefused_SendsBothAndNamesTheRefusedOne() {
            var (device, port) = Connect(Umi20S().On(":Pnbsl95#", "0#").On(":Pnbsr90#", "1#"));

            Assert.That(() => device.SetRaLimits(95, 90),
                Throws.InstanceOf<OnStepXCommandRefusedException>().With.Property("Reason").Contains("left"));
            Assert.That(port.Written, Is.EqualTo(new[] { ":Pnbsl95#", ":Pnbsr90#" }));
        }

        [TestCase(0, 90)]
        [TestCase(90, 181)]
        public void SetRaLimits_OutOfRange_ThrowsWithoutACommand(int left, int right) {
            var (device, port) = Connect(Umi20S());

            Assert.That(() => device.SetRaLimits(left, right), Throws.InstanceOf<ArgumentOutOfRangeException>());
            Assert.That(port.Written, Is.Empty);
        }

        [Test]
        public void SetAcsThresholds_DecBelowItsOwnMinimum_Throws() {
            // RA takes 10, Dec only 100
            var (device, port) = Connect(Umi20S().On(":Pncsr10#", "1#"));

            Assert.That(() => device.SetAcsThresholds(10, 99), Throws.InstanceOf<ArgumentOutOfRangeException>());
            Assert.That(port.Written, Is.Empty);
        }

        [Test]
        public void SetAcsThresholds_ClearCounters_AndTolerance_SendTheVendorCommands() {
            var (device, port) = Connect(Umi20S().On(":Pncsr500#", "1#").On(":Pncsd800#", "1#").On(":Pnccc#", "1#").On(":Pndsv12#", "1#"));

            device.SetAcsThresholds(500, 800);
            device.ClearCollisionCounters();
            device.SetSupervisedGotoTolerance(12);

            Assert.That(port.Written, Is.EqualTo(new[] { ":Pncsr500#", ":Pncsd800#", ":Pnccc#", ":Pndsv12#" }));
        }

        [Test]
        public void SetPidGains_WritesTheFourGainsThenCommitsAndWaitsForTheAxis() {
            // the running gains, then all zero while the axis restarts, then the stored gains
            var port = Umi20S()
                .On(":Pnag0#", "300|200|200|50|0|0|0#", "0|0|0|0|0|0|0#", "300|200|200|50|0|0|0#")
                .On(":Pnas0Ap310#", "1#").On(":Pnas0Ai210#", "1#").On(":Pnas0Sp220#", "1#").On(":Pnas0Si60#", "1#").On(":Pnas0r#", "1#");
            var (device, _) = Connect(port);

            device.SetPidGains(ProxiskyAxis.Ra, new ProxiskyPidGains(310, 210, 220, 60));
            var s = device.ReadSettings();

            // the first eight commands are the write; ReadSettings follows
            Assert.That(port.Written.Take(8), Is.EqualTo(new[] {
                ":Pnag0#", ":Pnas0Ap310#", ":Pnas0Ai210#", ":Pnas0Sp220#", ":Pnas0Si60#", ":Pnas0r#", ":Pnag0#", ":Pnag0#",
            }));
            Assert.That(s.RaPid, Is.EqualTo(new ProxiskyPidGains(310, 210, 220, 60)));
            Assert.That(s.DecPid, Is.EqualTo(new ProxiskyPidGains(200, 200, 200, 35)));
            Assert.That(s.RestartPending, Is.EqualTo(new[] { "RaPid" }));
        }

        [Test]
        public void SetPidGains_ReadbackStaysZero_ThrowsButKeepsTheGainsPending() {
            // the commit was accepted, so the mount has the gains although the axis does not report them in time
            var port = Umi20S()
                .On(":Pnag0#", "300|200|200|50|0|0|0#", "0|0|0|0|0|0|0#")
                .On(":Pnas0Ap310#", "1#").On(":Pnas0Ai210#", "1#").On(":Pnas0Sp220#", "1#").On(":Pnas0Si60#", "1#").On(":Pnas0r#", "1#");
            var (device, _) = Connect(port);

            Assert.That(() => device.SetPidGains(ProxiskyAxis.Ra, new ProxiskyPidGains(310, 210, 220, 60)),
                Throws.InstanceOf<OnStepXException>().With.Message.Contains("power cycle"));
            port.On(":Pnag0#", "300|200|200|50|0|0|0#");
            var s = device.ReadSettings();

            Assert.That(s.RaPid, Is.EqualTo(new ProxiskyPidGains(310, 210, 220, 60)));
            Assert.That(s.RestartPending, Is.EqualTo(new[] { "RaPid" }));
        }

        [Test]
        public void SetSwitch_AcknowledgementWithoutTerminator_Counts() {
            // INDI's sendVendorSetter looks at the first character only
            var (device, port) = Connect(Umi20S().On(":PsAs1#", "1"));

            device.SetSwitch(ProxiskySwitch.AsiairHoming, true);

            Assert.That(port.Written, Is.EqualTo(new[] { ":PsAs1#" }));
        }

        [Test]
        public void SetPidGains_RefusedGain_DoesNotCommit() {
            var (device, port) = Connect(Umi20S().On(":Pnas1Ap300#", "1#").On(":Pnas1Ai300#", "0#"));

            Assert.That(() => device.SetPidGains(ProxiskyAxis.Dec, new ProxiskyPidGains(300, 300, 300, 40)), Throws.InstanceOf<OnStepXCommandRefusedException>());
            Assert.That(port.Written, Has.None.EqualTo(":Pnas1r#"));
            Assert.That(device.ReadSettings().RestartPending, Is.Empty);
        }

        [TestCase(10, 200, 200, 50)]
        [TestCase(300, 2000, 200, 50)]
        [TestCase(300, 200, 200, 81)]
        public void SetPidGains_OutOfRange_ThrowsWithoutACommand(int angleKp, int angleKi, int speedKp, int speedKi) {
            var (device, port) = Connect(Umi20S());

            Assert.That(() => device.SetPidGains(ProxiskyAxis.Ra, new ProxiskyPidGains(angleKp, angleKi, speedKp, speedKi)),
                Throws.InstanceOf<ArgumentOutOfRangeException>());
            Assert.That(port.Written, Is.Empty);
        }

        [Test]
        public void SetPidGains_Umi17S_IsReadOnly() {
            var (device, port) = Connect(Umi20S().On(":Pbvg#", "UMi17S|1.0.7#"));

            Assert.That(() => device.SetPidGains(ProxiskyAxis.Ra, new ProxiskyPidGains(300, 200, 200, 50)),
                Throws.InstanceOf<OnStepXException>().With.Message.Contains("UMi20S"));
            Assert.That(port.Written, Is.Empty);
        }
    }
}
