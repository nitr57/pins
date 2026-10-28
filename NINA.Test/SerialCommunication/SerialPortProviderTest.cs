#region "copyright"

/*
    Copyright © 2026 Nico Trost <nico.trost57@gmail.com> and the PI.N.S. contributors

    This file is part of PI 'N' Stars.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Core.Utility.SerialCommunication;
using NUnit.Framework;
using System;
using System.IO;
using System.IO.Ports;

namespace NINA.Test.SerialCommunication {

    /// <summary>
    /// The USB vendor and product IDs of a serial port come from sysfs, where /sys/class/tty/&lt;tty&gt;/device is a
    /// symlink into /sys/devices: the provider has to follow it to reach the USB device with idVendor/idProduct.
    /// Runs against a fake sysfs tree laid out like a real one.
    /// </summary>
    [TestFixture]
    [Platform(Include = "Linux", Reason = "Builds a sysfs tree with symlinks")]
    public class SerialPortProviderTest {
        private string root = null!;

        [SetUp]
        public void SetUp() {
            root = Path.Combine(Path.GetTempPath(), $"pins-sysfs-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
        }

        [TearDown]
        public void TearDown() {
            Directory.Delete(root, recursive: true);
        }

        [Test]
        public void GetPortNames_FindsAPortByItsUsbVendorAndProductId() {
            AddUsbSerialPort("ttyUSB0", "0403", "6001");
            AddUsbSerialPort("ttyUSB1", "1a86", "7523");

            var ports = new SerialPortProvider(Path.Combine(root, "class", "tty"))
                .GetPortNames("USB\\VID_0403&PID_6001", addDivider: false, addGenericPorts: false);

            Assert.That(ports, Is.EqualTo(new[] { "/dev/ttyUSB0" }));
        }

        [Test]
        public void GetSerialPort_EnablesDtr_ForAnArduinoLeonardo() {
            AddUsbSerialPort("ttyACM0", "2341", "8036");
            AddUsbSerialPort("ttyACM1", "2341", "0043");
            var provider = new SerialPortProvider(Path.Combine(root, "class", "tty"));

            var leonardo = provider.GetSerialPort("/dev/ttyACM0", 9600, Parity.None, 8, StopBits.One, Handshake.None, false, "\n", 1000, 1000);
            var uno = provider.GetSerialPort("/dev/ttyACM1", 9600, Parity.None, 8, StopBits.One, Handshake.None, false, "\n", 1000, 1000);

            Assert.That(leonardo.DtrEnable, Is.True);
            Assert.That(uno.DtrEnable, Is.False);
        }

        // As on a Pi: /sys/class/tty/<tty>/device -> /sys/devices/.../usb1/1-1.<n>/1-1.<n>:1.0, the USB interface,
        // whose parent 1-1.<n> is the USB device that holds idVendor and idProduct
        private void AddUsbSerialPort(string tty, string vendorId, string productId) {
            var usbDevice = Path.Combine(root, "devices", "platform", "scb", "usb1", $"1-1.{tty[^1]}{tty[3]}");
            var usbInterface = Path.Combine(usbDevice, $"{Path.GetFileName(usbDevice)}:1.0");
            Directory.CreateDirectory(Path.Combine(usbInterface, tty));
            File.WriteAllText(Path.Combine(usbDevice, "idVendor"), vendorId + "\n");
            File.WriteAllText(Path.Combine(usbDevice, "idProduct"), productId + "\n");

            var classDir = Path.Combine(root, "class", "tty", tty);
            Directory.CreateDirectory(classDir);
            Directory.CreateSymbolicLink(Path.Combine(classDir, "device"), Path.GetRelativePath(classDir, usbInterface));
        }
    }
}
