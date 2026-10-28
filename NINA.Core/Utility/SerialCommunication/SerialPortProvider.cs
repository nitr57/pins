#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Text.RegularExpressions;

namespace NINA.Core.Utility.SerialCommunication {

    public class SerialPortProvider : ISerialPortProvider {
        /**
         * Below are the device IDs that are currently known
         * Arduino Uno R3:                     DeviceID = USB VID:PID=2341:0043
         * Arduino Leonardo:                   DeviceID = USB VID:PID=2341:8036
         * Optec USB/Serial cable:             DeviceID = USB VID:PID=0403:6001
         * Flat Fielder:                       DeviceID = USB VID:PID=0403:6001
         * Pegasus Astro Ultimate Powerbox V2: DeviceID = USB VID:PID=0403:6015
         * 
         * On Linux, serial ports are typically:
         * - /dev/ttyUSB* for USB-to-serial adapters (FTDI, etc.)
         * - /dev/ttyACM* for CDC ACM devices (Arduino, etc.)
         * - /dev/ttyS* for built-in serial ports
         **/

        private readonly Dictionary<string, bool> dtrEnableValue;
        private readonly string ttyClassPath;
        private readonly string serialByIdPath;

        public SerialPortProvider() : this("/sys/class/tty", "/dev/serial/by-id") {
        }

        /// <summary>
        /// <paramref name="ttyClassPath"/>: the sysfs tty class directory, /sys/class/tty on Linux.
        /// <paramref name="serialByIdPath"/>: udev's /dev/serial/by-id, whose names stay the same when a device is plugged
        /// into another USB socket or the ttyUSB numbers change at boot. A test can point both at a fake tree.
        /// </summary>
        public SerialPortProvider(string ttyClassPath, string serialByIdPath) {
            this.ttyClassPath = ttyClassPath;
            this.serialByIdPath = serialByIdPath;
            dtrEnableValue = new Dictionary<string, bool>();
            
            // On Linux, detect Arduino Leonardo devices that need DTR enabled
            // Leonardo has VID:PID = 2341:8036
            try {
                var ports = EnumerateSerialPortsWithDeviceInfo();
                foreach (var (portName, vendorId, productId, _) in ports) {
                    // Arduino Leonardo needs DTR enabled
                    bool needsDtr = (vendorId == "2341" && productId == "8036");
                    if (!dtrEnableValue.ContainsKey(portName)) {
                        dtrEnableValue.Add(portName, needsDtr);
                        Logger.Debug($"Found serial port {portName} (VID:PID={vendorId}:{productId}, DTR={needsDtr})");
                    }
                }
            } catch (Exception ex) {
                Logger.Debug($"Error enumerating serial ports with device info: {ex.Message}");
            }
        }

        private List<(string portName, string vendorId, string productId, string serialNumber)> EnumerateSerialPortsWithDeviceInfo() {
            var result = new List<(string, string, string, string)>();
            
            // On Linux, get device info from /sys/class/tty/*/device
            if (!Directory.Exists(ttyClassPath)) {
                return result;
            }

            foreach (var ttyDir in Directory.GetDirectories(ttyClassPath)) {
                var ttyName = Path.GetFileName(ttyDir);
                
                // Only consider USB serial ports and ACM devices
                if (!ttyName.StartsWith("ttyUSB") && !ttyName.StartsWith("ttyACM")) {
                    continue;
                }
                
                var portName = $"/dev/{ttyName}";
                
                // Try to read USB vendor and product IDs. /sys/class/tty/<tty> is itself a symlink into /sys/devices, and
                // its "device" entry points relative to that physical directory (e.g. ../../../ttyUSB0). .NET joins a
                // relative target to the link's own path and drops the ".." as text, which from /sys/class/tty/<tty> lands
                // outside /sys/devices. So resolve the tty first, then "device" from its physical directory.
                var devicePath = Path.Combine(ResolveLink(ttyDir), "device");
                if (Directory.Exists(devicePath)) {
                    // Navigate up from the link's target to find the USB device
                    var usbDevicePath = FindUsbDeviceParent(ResolveLink(devicePath));
                    if (usbDevicePath != null) {
                        var vendorId = ReadSysfsFile(Path.Combine(usbDevicePath, "idVendor"));
                        var productId = ReadSysfsFile(Path.Combine(usbDevicePath, "idProduct"));
                        var serialNumber = ReadSysfsFile(Path.Combine(usbDevicePath, "serial"));
                        result.Add((portName, vendorId, productId, serialNumber));
                    } else {
                        result.Add((portName, "", "", ""));
                    }
                } else {
                    result.Add((portName, "", "", ""));
                }
            }
            
            return result;
        }

        // tty name (ttyUSB0) -> its /dev/serial/by-id path, for the ports that have one
        private Dictionary<string, string> GetByIdNames() {
            var result = new Dictionary<string, string>();
            try {
                if (!Directory.Exists(serialByIdPath)) {
                    return result;
                }

                foreach (var link in Directory.GetFiles(serialByIdPath).OrderBy(name => name)) {
                    var target = new FileInfo(link).ResolveLinkTarget(returnFinalTarget: true);
                    if (target != null) {
                        result.TryAdd(Path.GetFileName(target.FullName), link);
                    }
                }
            } catch (Exception ex) {
                Logger.Debug($"Error reading {serialByIdPath}: {ex.Message}");
            }

            return result;
        }

        // A /dev/serial/by-id name to the /dev/tty* it points at; any other name unchanged
        private static string ToDevicePath(string portName) {
            try {
                var target = new FileInfo(portName).ResolveLinkTarget(returnFinalTarget: true);
                return target != null ? $"/dev/{Path.GetFileName(target.FullName)}" : portName;
            } catch (Exception) {
                return portName;
            }
        }

        /// <summary>
        /// The serial-number part of a WMI device query as a regex over "+&lt;serial&gt;\0000", the end of an FTDIBUS device
        /// id (FTDIBUS\VID_0403+PID_6001+A8xxxxxx\0000): the LIKE pattern after the PID, e.g. "+A8%" (Alnitak) or
        /// "%FM%" (Pegasus FlatMaster). Null when the query does not filter by serial number.
        /// </summary>
        private static Regex ParseSerialNumberFilter(string deviceQuery) {
            var tail = Regex.Match(deviceQuery, @"PID_[0-9A-Fa-f]{4}([^']*)'");
            if (!tail.Success || tail.Groups[1].Value.Trim('%').Length == 0) {
                return null;
            }

            var pattern = Regex.Escape(tail.Groups[1].Value).Replace("%", ".*").Replace("_", ".");
            return new Regex($"^{pattern}$", RegexOptions.IgnoreCase);
        }

        private static string ResolveLink(string path) {
            try {
                return new DirectoryInfo(path).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? path;
            } catch (IOException) {
                return path;
            }
        }

        private string FindUsbDeviceParent(string devicePath) {
            // Walk up the directory tree to find the USB device
            var currentPath = devicePath;
            for (int i = 0; i < 5; i++) { // Limit depth to prevent infinite loops
                if (File.Exists(Path.Combine(currentPath, "idVendor")) && 
                    File.Exists(Path.Combine(currentPath, "idProduct"))) {
                    return currentPath;
                }
                
                var parentPath = Path.GetDirectoryName(currentPath);
                if (parentPath == currentPath || string.IsNullOrEmpty(parentPath)) {
                    break;
                }
                currentPath = parentPath;
            }
            return null;
        }

        private string ReadSysfsFile(string path) {
            try {
                if (File.Exists(path)) {
                    return File.ReadAllText(path).Trim();
                }
            } catch {
                // Ignore errors
            }
            return string.Empty;
        }

        public ISerialPort GetSerialPort(string portName, int baudRate, Parity parity, int dataBits, StopBits stopBits,
            Handshake handShake, bool dtrEnable, string newLine, int readTimeout, int writeTimeout) {
            if (string.IsNullOrEmpty(portName)) return null;
            dtrEnableValue.TryGetValue(ToDevicePath(portName), out var dtrEnableForLeonardo);
            var dtr = dtrEnable || dtrEnableForLeonardo;
            return new SerialPortWrapper {
                PortName = portName,
                BaudRate = baudRate,
                Parity = parity,
                DataBits = dataBits,
                StopBits = stopBits,
                Handshake = handShake,
                DtrEnable = dtr,
                NewLine = newLine,
                ReadTimeout = readTimeout,
                WriteTimeout = writeTimeout
            };
        }

        public ReadOnlyCollection<string> GetPortNames(string deviceQuery = null, bool addDivider = true, bool addGenericPorts = true) {
            var result = new List<string>();
            try {
                // On Linux, enumerate serial ports from /dev
                var deviceSpecificPorts = new List<string>();
                
                if (deviceQuery != null) {
                    // Parse device query if it contains VID/PID filter
                    // Example: "USB\\VID_0403&PID_6001" -> filter by VID=0403, PID=6001
                    var vidMatch = Regex.Match(deviceQuery, @"VID_([0-9A-Fa-f]{4})");
                    var pidMatch = Regex.Match(deviceQuery, @"PID_([0-9A-Fa-f]{4})");
                    
                    if (vidMatch.Success || pidMatch.Success) {
                        var targetVid = vidMatch.Success ? vidMatch.Groups[1].Value.ToLower() : null;
                        var targetPid = pidMatch.Success ? pidMatch.Groups[1].Value.ToLower() : null;
                        var serialNumberFilter = ParseSerialNumberFilter(deviceQuery);
                        var byIdNames = GetByIdNames();
                        
                        var portsWithInfo = EnumerateSerialPortsWithDeviceInfo();
                        foreach (var (portName, vendorId, productId, serialNumber) in portsWithInfo) {
                            bool matches = true;
                            if (targetVid != null && vendorId.ToLower() != targetVid) matches = false;
                            if (targetPid != null && productId.ToLower() != targetPid) matches = false;
                            // E.g. only FTDI chips whose serial number Alnitak programmed, not any other FTDI adapter
                            if (serialNumberFilter != null && !serialNumberFilter.IsMatch($"+{serialNumber}\\0000")) matches = false;
                            
                            if (matches) {
                                // The by-id name stays valid when the device moves to another USB socket
                                deviceSpecificPorts.Add(byIdNames.TryGetValue(Path.GetFileName(portName), out var byIdName) ? byIdName : portName);
                            }
                        }
                    }
                    
                    result.AddRange(deviceSpecificPorts.OrderBy(s => s));
                }
                
                if (addDivider && deviceSpecificPorts.Any()) { 
                    result.Add("----"); 
                }
                
                if (addGenericPorts) {
                    // The stable by-id names first, then all ports as before, so a profile holding /dev/ttyUSB0 still
                    // finds its entry
                    foreach (var byIdName in GetByIdNames().Values.OrderBy(s => s)) {
                        if (!result.Contains(byIdName)) {
                            result.Add(byIdName);
                        }
                    }

                    var allPorts = SerialPort.GetPortNames().OrderBy(s => s);
                    foreach (var portName in allPorts) {
                        if (!result.Contains(portName)) {
                            result.Add(portName);
                        }
                    }
                }
            } catch (Exception ex) {
                Logger.Error(ex);
                result = SerialPort.GetPortNames().OrderBy(s => s).ToList();
            }
            return new ReadOnlyCollection<string>(result);
        }
    }
}
