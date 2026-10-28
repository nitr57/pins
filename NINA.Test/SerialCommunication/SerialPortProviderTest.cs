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
using System.Linq;

namespace NINA.Test.SerialCommunication {

    /// <summary>
    /// The USB vendor and product IDs of a serial port come from sysfs, where /sys/class/tty/&lt;tty&gt; and its
    /// "device" entry are both symlinks: the provider has to follow them to reach the USB device with
    /// idVendor/idProduct. Runs against a fake sysfs tree laid out like a real one.
    /// </summary>
    [TestFixture]
    [Platform(Include = "Linux", Reason = "Builds a sysfs tree with symlinks")]
    public class SerialPortProviderTest {
        private string root = null!;
        private int usbPorts;

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

            var ports = CreateProvider().GetPortNames("USB\\VID_0403&PID_6001", addDivider: false, addGenericPorts: false);

            Assert.That(ports, Is.EqualTo(new[] { "/dev/ttyUSB0" }));
        }

        [Test]
        public void GetPortNames_ReturnsTheByIdName_WhenThePortHasOne() {
            AddUsbSerialPort("ttyUSB0", "0403", "6001", byId: "usb-FTDI_FT232R_USB_UART_A8001234-if00-port0");

            var ports = CreateProvider().GetPortNames("USB\\VID_0403&PID_6001", addDivider: false, addGenericPorts: false);

            Assert.That(ports, Is.EqualTo(new[] { Path.Combine(root, "dev", "serial", "by-id", "usb-FTDI_FT232R_USB_UART_A8001234-if00-port0") }));
        }

        [Test]
        public void GetPortNames_AppliesTheSerialNumberPrefixOfTheQuery() {
            // Alnitak's query: only FTDI chips whose serial number starts with A8, not any other FTDI adapter
            AddUsbSerialPort("ttyUSB0", "0403", "6001", serialNumber: "FT9XK2LM");
            AddUsbSerialPort("ttyUSB1", "0403", "6001", serialNumber: "A8001234");

            var ports = CreateProvider().GetPortNames(AlnitakQuery, addDivider: false, addGenericPorts: false);

            Assert.That(ports, Is.EqualTo(new[] { "/dev/ttyUSB1" }));
        }

        [Test]
        public void GetPortNames_AppliesASerialNumberPatternInsideTheQuery() {
            // Pegasus FlatMaster's query: serial numbers containing FM
            AddUsbSerialPort("ttyUSB0", "0403", "6015", serialNumber: "PBX12345");
            AddUsbSerialPort("ttyUSB1", "0403", "6015", serialNumber: "PFM00042");

            var ports = CreateProvider().GetPortNames(FlatMasterQuery, addDivider: false, addGenericPorts: false);

            Assert.That(ports, Is.EqualTo(new[] { "/dev/ttyUSB1" }));
        }

        [Test]
        public void GetPortNames_DoesNotMatchADeviceWithoutSerialNumber_WhenTheQueryNeedsOne() {
            AddUsbSerialPort("ttyUSB0", "0403", "6001");

            var ports = CreateProvider().GetPortNames(AlnitakQuery, addDivider: false, addGenericPorts: false);

            Assert.That(ports, Is.Empty);
        }

        [Test]
        public void GetPortNames_ListsTheByIdNamesFirst() {
            AddUsbSerialPort("ttyUSB0", "1a86", "7523", byId: "usb-1a86_USB_Serial-if00-port0");
            AddUsbSerialPort("ttyACM0", "2341", "0043", byId: "usb-Arduino__www.arduino.cc__0043_7563-if00");

            var ports = CreateProvider().GetPortNames();
            var byId = Path.Combine(root, "dev", "serial", "by-id");

            Assert.That(ports.Take(2), Is.EqualTo(new[] {
                Path.Combine(byId, "usb-1a86_USB_Serial-if00-port0"),
                Path.Combine(byId, "usb-Arduino__www.arduino.cc__0043_7563-if00")
            }));
        }

        [Test]
        public void GetSerialPort_EnablesDtr_ForAnArduinoLeonardo() {
            AddUsbSerialPort("ttyACM0", "2341", "8036");
            AddUsbSerialPort("ttyACM1", "2341", "0043");
            var provider = CreateProvider();

            var leonardo = provider.GetSerialPort("/dev/ttyACM0", 9600, Parity.None, 8, StopBits.One, Handshake.None, false, "\n", 1000, 1000);
            var uno = provider.GetSerialPort("/dev/ttyACM1", 9600, Parity.None, 8, StopBits.One, Handshake.None, false, "\n", 1000, 1000);

            Assert.That(leonardo.DtrEnable, Is.True);
            Assert.That(uno.DtrEnable, Is.False);
        }

        [Test]
        public void GetSerialPort_EnablesDtr_ForALeonardoChosenByItsByIdName() {
            AddUsbSerialPort("ttyACM0", "2341", "8036", byId: "usb-Arduino_LLC_Arduino_Leonardo-if00");
            var provider = CreateProvider();

            var leonardo = provider.GetSerialPort(Path.Combine(root, "dev", "serial", "by-id", "usb-Arduino_LLC_Arduino_Leonardo-if00"),
                9600, Parity.None, 8, StopBits.One, Handshake.None, false, "\n", 1000, 1000);

            Assert.That(leonardo.DtrEnable, Is.True);
        }

        // As the flat panel drivers pass them
        private const string AlnitakQuery = @"SELECT * FROM Win32_PnPEntity WHERE DeviceID LIKE 'FTDIBUS\\VID_0403+PID_6001+A8%'";
        private const string FlatMasterQuery = @"SELECT * FROM Win32_PnPEntity WHERE DeviceID LIKE 'FTDIBUS\\VID_0403+PID_6015%FM%'";

        private SerialPortProvider CreateProvider() {
            return new SerialPortProvider(Path.Combine(root, "class", "tty"), Path.Combine(root, "dev", "serial", "by-id"));
        }

        // As on a Pi 5: /sys/class/tty/<tty> is itself a symlink into /sys/devices, and "device" points relative to that
        // physical directory, the way the kernel writes it:
        // - usb-serial: .../3-<n>/3-<n>:1.0/ttyUSB0/tty/ttyUSB0, device -> ../../../ttyUSB0, the port
        // - cdc_acm: .../3-<n>/3-<n>:1.0/tty/ttyACM0, device -> ../../../3-<n>:1.0, the USB interface
        // Either way the USB device 3-<n> above holds idVendor, idProduct and, if the chip has one, serial.
        // <paramref name="byId"/> adds udev's /dev/serial/by-id/<byId> -> ../../<tty>.
        private void AddUsbSerialPort(string tty, string vendorId, string productId, string? serialNumber = null, string? byId = null) {
            var usbDevice = Path.Combine(root, "devices", "platform", "axi", "usb3", $"3-{++usbPorts}");
            var usbInterface = Path.Combine(usbDevice, $"{Path.GetFileName(usbDevice)}:1.0");
            Directory.CreateDirectory(usbInterface);
            File.WriteAllText(Path.Combine(usbDevice, "idVendor"), vendorId + "\n");
            File.WriteAllText(Path.Combine(usbDevice, "idProduct"), productId + "\n");
            if (serialNumber != null) {
                File.WriteAllText(Path.Combine(usbDevice, "serial"), serialNumber + "\n");
            }

            if (byId != null) {
                var dev = Path.Combine(root, "dev");
                var byIdDir = Path.Combine(dev, "serial", "by-id");
                Directory.CreateDirectory(byIdDir);
                File.WriteAllText(Path.Combine(dev, tty), string.Empty);
                File.CreateSymbolicLink(Path.Combine(byIdDir, byId), Path.Combine("..", "..", tty));
            }

            string physicalTty;
            string deviceTarget;

            if (tty.StartsWith("ttyUSB")) {
                physicalTty = Path.Combine(usbInterface, tty, "tty", tty);
                deviceTarget = $"../../../{tty}";
            } else {
                physicalTty = Path.Combine(usbInterface, "tty", tty);
                deviceTarget = $"../../../{Path.GetFileName(usbInterface)}";
            }

            Directory.CreateDirectory(physicalTty);
            Directory.CreateSymbolicLink(Path.Combine(physicalTty, "device"), deviceTarget);

            var classDir = Path.Combine(root, "class", "tty");
            Directory.CreateDirectory(classDir);
            Directory.CreateSymbolicLink(Path.Combine(classDir, tty), Path.GetRelativePath(classDir, physicalTty));
        }
    }
}
