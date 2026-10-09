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
using System.Diagnostics;

namespace NINA.Test.Equipment.OnStepX {

    /// <summary>
    /// Against a real OnStepX mount, read-only: nothing here moves it. Runs only when selected, with the port in
    /// ONSTEPX_PORT, e.g. ONSTEPX_PORT=/dev/ttyUSB0 dotnet test --filter "FullyQualifiedName~OnStepXHardwareTest".
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
    }
}
