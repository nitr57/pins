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

    [TestFixture]
    public class OnStepXDeviceTest {

        private static OnStepXDevice Connect(FakeOnStepXPort port, TimeSpan? proxiskyProbeBudget = null) =>
            OnStepXDevice.Connect(new OnStepXTransport(port), proxiskyProbeBudget ?? TimeSpan.Zero, TimeSpan.Zero);

        [Test]
        public void Connect_Umi17S_IsAProxiskyUmiDevice() {
            var port = FakeOnStepXPort.Umi17S();

            var device = Connect(port);

            Assert.That(device, Is.InstanceOf<ProxiskyUmiDevice>());
            var umi = (ProxiskyUmiDevice)device;
            Assert.That(umi.Model, Is.EqualTo("Proxisky UMi17S"));
            Assert.That(umi.UmiModel, Is.EqualTo("UMi17S"));
            Assert.That(umi.VendorFirmware, Is.EqualTo("1.0.7"));
            Assert.That(umi.HasPowerLossMemory, Is.True);
            Assert.That(umi.HasAntiCollision, Is.True);
            Assert.That(device.Identification, Is.EqualTo(new OnStepXIdentification("On-Step", "10.20a", "Dec  1 2025", "11:55:45")));
            Assert.That(device.IsConnected, Is.True);
        }

        [Test]
        public void Connect_OnStepXWithoutProxiskyCommands_IsTheGenericDevice() {
            // another OnStepX answers :Pbvg# like any unknown command, with a bare "0"
            var port = FakeOnStepXPort.Umi17S().On(":Pbvg#", "0");

            var device = Connect(port);

            Assert.That(device.GetType(), Is.EqualTo(typeof(OnStepXDevice)));
            Assert.That(device.Model, Is.EqualTo("OnStepX"));
            Assert.That(port.Written, Has.None.EqualTo(":Pbc#"));
        }

        [Test]
        public void Connect_ProxiskyAnswersZeroRightAfterConnect_KeepsAsking() {
            var port = FakeOnStepXPort.Umi17S().On(":Pbvg#", "0", "0", "UMi20S|1.0.6#");

            var device = Connect(port, TimeSpan.FromSeconds(10));

            Assert.That(device, Is.InstanceOf<ProxiskyUmiDevice>());
            Assert.That(((ProxiskyUmiDevice)device).UmiModel, Is.EqualTo("UMi20S"));
            Assert.That(port.Written.Count(c => c == ":Pbvg#"), Is.EqualTo(3));
        }

        [Test]
        public void Connect_UnreadableCapability_IsRetriedOnceThenUnknown() {
            var port = FakeOnStepXPort.Umi17S().On(":Pbc#", (string?)null);

            var umi = (ProxiskyUmiDevice)Connect(port);

            Assert.That(umi.HasPowerLossMemory, Is.Null);
            Assert.That(port.Written.Count(c => c == ":Pbc#"), Is.EqualTo(2));
        }

        [Test]
        public void Connect_ClassicOnStep_IsRefusedAndThePortClosed() {
            var port = FakeOnStepXPort.Umi17S().On(":GVN#", "3.16#");

            Assert.That(() => Connect(port), Throws.InstanceOf<OnStepXException>().With.Message.Contains("not OnStepX"));
            Assert.That(port.IsOpen, Is.False);
        }

        [Test]
        public void Connect_NoAnswer_IsRefusedAndThePortClosed() {
            var port = new FakeOnStepXPort().On(FakeOnStepXPort.Ack, (string?)null).On(":GVP#", (string?)null);

            Assert.That(() => Connect(port), Throws.InstanceOf<OnStepXException>());
            Assert.That(port.IsOpen, Is.False);
        }

        [Test]
        public void Handshake_ClearsStartupGarbageWithTwoGvp_AsInIndi() {
            // ACK unanswered twice, then :GVP# answered '0' and the product name, then ACK answered
            var port = FakeOnStepXPort.Umi17S()
                .On(FakeOnStepXPort.Ack, null, null, "P")
                .On(":GVP#", "0", "On-Step#");
            var transport = new OnStepXTransport(port);
            transport.Open();

            Assert.That(OnStepXDevice.Handshake(transport), Is.True);
            Assert.That(port.Written, Is.EqualTo(new[] { FakeOnStepXPort.Ack, FakeOnStepXPort.Ack, ":GVP#", ":GVP#", FakeOnStepXPort.Ack }));
        }

        [Test]
        public void Handshake_FirstGvpNotZero_Fails() {
            var port = new FakeOnStepXPort().On(FakeOnStepXPort.Ack, (string?)null).On(":GVP#", "On-Step#");
            var transport = new OnStepXTransport(port);
            transport.Open();

            Assert.That(OnStepXDevice.Handshake(transport), Is.False);
        }

        [Test]
        public void Reads_Umi17SAtHome() {
            var device = Connect(FakeOnStepXPort.Umi17S());

            Assert.That(device.GetRightAscension(), Is.EqualTo(4 + 15 / 3600.0).Within(1e-9));
            Assert.That(device.GetDeclination(), Is.EqualTo(90.0).Within(1e-9));
            Assert.That(device.GetPierSide(), Is.EqualTo(OnStepXPierSide.West));
            Assert.That(device.GetPulseGuideRate(), Is.EqualTo(1.0));
            Assert.That(device.GetStatus().Park, Is.EqualTo(OnStepXParkState.Unparked));
        }

        [Test]
        public void GetPulseGuideRate_FallsBackToTheIndexInGu() {
            var device = Connect(FakeOnStepXPort.Umi17S().On(":GX90#", "0").On(":GU#", "nNpE160#"));

            Assert.That(device.GetPulseGuideRate(), Is.EqualTo(0.5));
        }

        [Test]
        public void GetStatus_InvalidReply_Throws() {
            var device = Connect(FakeOnStepXPort.Umi17S().On(":GU#", "0"));

            Assert.That(() => device.GetStatus(), Throws.InstanceOf<OnStepXException>());
        }

        [TestCase("04:00:15", 4 + 15 / 3600.0)]
        [TestCase("+90*00:00", 90.0)]
        [TestCase("-05*30:36", -(5 + 30 / 60.0 + 36 / 3600.0))]
        [TestCase("-00*30:00", -0.5)]
        [TestCase("12:30.5", 12 + 30.5 / 60)]
        [TestCase("+45*30", 45.5)]
        [TestCase("+45ß30:00", 45.5)]
        public void TryParseSexagesimal(string text, double expected) {
            Assert.That(OnStepXDevice.TryParseSexagesimal(text, out double value), Is.True);
            Assert.That(value, Is.EqualTo(expected).Within(1e-9));
        }

        [TestCase("")]
        [TestCase("ab:cd")]
        [TestCase("1:2:3:4")]
        public void TryParseSexagesimal_Rejects(string text) {
            Assert.That(OnStepXDevice.TryParseSexagesimal(text, out _), Is.False);
        }

        [Test]
        public void GetRightAscension_BareZero_Throws() {
            // "0" parses as a number; the missing '#' is what makes it the error reply
            var device = Connect(FakeOnStepXPort.Umi17S().On(":GR#", "0"));

            Assert.That(() => device.GetRightAscension(), Throws.InstanceOf<OnStepXException>());
        }

        [TestCase("10.20a", 10)]
        [TestCase("3.16", 3)]
        [TestCase("", -1)]
        [TestCase("x.1", -1)]
        public void Identification_MajorVersion(string version, int major) {
            Assert.That(new OnStepXIdentification("On-Step", version, "", "").MajorVersion, Is.EqualTo(major));
        }
    }
}
