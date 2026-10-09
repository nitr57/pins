#region "copyright"

/*
    Copyright © 2026 Nico Trost <nico.trost57@gmail.com> and the PI.N.S. contributors

    This file is part of PI 'N' Stars.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

#nullable enable

using System;
using System.IO.Ports;
using System.Text;

namespace NINA.Equipment.SDK.TelescopeSDKs.OnStepXSDK {

    /// <summary>USB serial connection to an OnStepX controller: 8N1, no handshake.</summary>
    public sealed class OnStepXSerialPort : IOnStepXPort {
        public const int DefaultBaudRate = 9600;

        private readonly SerialPort port;

        public OnStepXSerialPort(string portName, int baudRate = DefaultBaudRate) {
            port = new SerialPort(portName, baudRate, Parity.None, 8, StopBits.One) {
                Handshake = Handshake.None,
                Encoding = Encoding.ASCII,
                WriteTimeout = 1000,
                // Linux raises DTR and RTS together when the port opens. An ESP32 board's auto-reset circuit resets
                // the controller while RTS is set and DTR is not, which clearing DTR before RTS (the .NET default of
                // both off) passes through. Keep both set and never touch them again, like INDI's tty_connect.
                DtrEnable = true,
                RtsEnable = true,
            };
        }

        public string PortName => port.PortName;

        public bool IsOpen => port.IsOpen;

        public void Open() => port.Open();

        public void Close() => port.Close();

        public void Write(string text) => port.Write(text);

        public int ReadByte(TimeSpan timeout) {
            port.ReadTimeout = Math.Max(1, (int)Math.Ceiling(timeout.TotalMilliseconds));
            try {
                return port.ReadByte();
            } catch (TimeoutException) {
                return -1;
            }
        }

        public void DiscardInBuffer() => port.DiscardInBuffer();

        public void Dispose() => port.Dispose();
    }
}
