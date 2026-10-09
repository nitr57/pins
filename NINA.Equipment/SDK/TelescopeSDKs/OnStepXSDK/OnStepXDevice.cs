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

using NINA.Core.Utility;
using System;
using System.Globalization;

namespace NINA.Equipment.SDK.TelescopeSDKs.OnStepXSDK {

    public enum OnStepXPierSide {
        Unknown,
        East,
        West,
    }

    /// <summary>The controller's :GVP#, :GVN#, :GVD# and :GVT# replies.</summary>
    public sealed record OnStepXIdentification(string Product, string Version, string Date, string Time) {

        /// <summary>The number before the first '.', or -1. OnStepX counts from 10, classic OnStep went up to 5.</summary>
        public int MajorVersion {
            get {
                int dot = Version.IndexOf('.');
                return dot > 0 && int.TryParse(Version[..dot], NumberStyles.None, CultureInfo.InvariantCulture, out int major) ? major : -1;
            }
        }

        public override string ToString() => $"{Product} {Version} ({Date} {Time})";
    }

    public class OnStepXException : Exception {
        public OnStepXException(string message) : base(message) {
        }
    }

    /// <summary>
    /// An OnStepX controller, with the commands and reply types of INDI's LX200_OnStep. Vendor firmware built on
    /// OnStepX derives from this; <see cref="Connect(OnStepXTransport)"/> picks the class from what the controller
    /// reports.
    /// </summary>
    public partial class OnStepXDevice : IDisposable {

        /// <summary>OnStepX version numbers start at 10.</summary>
        public const int MinMajorVersion = 10;

        protected OnStepXDevice(OnStepXTransport transport, OnStepXIdentification identification) {
            Transport = transport;
            Identification = identification;
        }

        public OnStepXIdentification Identification { get; }

        /// <summary>What the mount is called, for the device list.</summary>
        public virtual string Model => "OnStepX";

        public string PortName => Transport.PortName;

        public bool IsConnected => Transport.IsOpen;

        protected OnStepXTransport Transport { get; }

        /// <summary>
        /// Opens the port, checks that an OnStepX controller answers and returns the class for it: a
        /// <see cref="ProxiskyUmiDevice"/> when the controller reports a Proxisky model, otherwise the generic one.
        /// </summary>
        public static OnStepXDevice Connect(OnStepXTransport transport) =>
            Connect(transport, ProxiskyUmiDevice.ModelProbeBudget, ProxiskyUmiDevice.ModelProbeInterval);

        internal static OnStepXDevice Connect(OnStepXTransport transport, TimeSpan proxiskyProbeBudget, TimeSpan proxiskyProbeInterval) {
            transport.Open();
            try {
                if (!Handshake(transport)) {
                    throw new OnStepXException($"No OnStep controller answers on {transport.PortName}");
                }

                var identification = ReadIdentification(transport);
                if (identification.MajorVersion < MinMajorVersion) {
                    throw new OnStepXException($"{transport.PortName}: firmware {identification} is not OnStepX ({MinMajorVersion}.x or later)");
                }
                Logger.Info($"OnStepX: {identification} on {transport.PortName}");

                string? model = ProxiskyUmiDevice.ProbeModel(transport, proxiskyProbeBudget, proxiskyProbeInterval);
                if (model is null) {
                    return new OnStepXDevice(transport, identification);
                }

                var umi = new ProxiskyUmiDevice(transport, identification, model);
                umi.ReadCapabilities();
                return umi;
            } catch {
                transport.Close();
                throw;
            }
        }

        /// <summary>
        /// INDI's LX200_OnStep::Handshake: the LX200 ACK, and if that goes unanswered :GVP# twice, which clears the
        /// garbage OnStepX can start up with (the first :GVP# is answered with '0', the second with the product name).
        /// </summary>
        internal static bool Handshake(OnStepXTransport transport) {
            if (CheckConnection(transport)) {
                return true;
            }

            if (transport.SendForChar(":GVP#") == '0' && transport.SendForChar(":GVP#") != '0') {
                return CheckConnection(transport);
            }

            return false;
        }

        /// <summary>INDI's check_lx200_connection: ACK (0x06), answered with one character (the UMi says 'P'), two tries.</summary>
        private static bool CheckConnection(OnStepXTransport transport) {
            for (int i = 0; i < 2; i++) {
                if (transport.SendForChar("\u0006") is not null) {
                    return true;
                }
            }
            return false;
        }

        internal static OnStepXIdentification ReadIdentification(OnStepXTransport transport) =>
            new(ReadText(transport, ":GVP#"),
                ReadText(transport, ":GVN#"),
                ReadText(transport, ":GVD#"),
                ReadText(transport, ":GVT#"));

        public OnStepXStatus GetStatus() {
            var reply = Transport.SendForString(":GU#");
            if (reply.Terminated && OnStepXStatus.TryParse(reply.Text, out var status)) {
                return status;
            }
            throw new OnStepXException($"Invalid reply to :GU#: '{reply}'");
        }

        /// <summary>Right ascension in hours.</summary>
        public double GetRightAscension() => ReadSexagesimal(":GR#");

        /// <summary>Declination in degrees.</summary>
        public double GetDeclination() => ReadSexagesimal(":GD#");

        /// <summary>:Gm#, as INDI reads it (E, W, N or ?).</summary>
        public OnStepXPierSide GetPierSide() {
            var reply = Transport.SendForString(":Gm#");
            if (!reply.Terminated || reply.IsEmpty) {
                throw new OnStepXException($"Invalid reply to :Gm#: '{reply}'");
            }
            return reply.Text[0] switch {
                'E' => OnStepXPierSide.East,
                'W' => OnStepXPierSide.West,
                _ => OnStepXPierSide.Unknown,
            };
        }

        /// <summary>
        /// The pulse-guide rate as a multiple of sidereal: :GX90#, or like INDI the rate index in :GU# (0.25x, 0.5x or
        /// 1x) when that fails.
        /// </summary>
        public double GetPulseGuideRate() {
            var reply = Transport.SendForString(":GX90#");
            if (reply.Terminated && double.TryParse(reply.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double rate)) {
                return rate;
            }

            Logger.Debug($"OnStepX: invalid reply to :GX90# '{reply}', reading the guide rate from :GU#");
            return GetStatus().PulseGuideRateIndex switch {
                0 => 0.25,
                1 => 0.5,
                2 => 1.0,
                _ => throw new OnStepXException($"Invalid reply to :GX90#: '{reply}', and no guide rate in :GU#"),
            };
        }

        public void Disconnect() => Transport.Close();

        public void Dispose() {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing) {
            if (disposing) {
                Transport.Dispose();
            }
        }

        protected static string ReadText(OnStepXTransport transport, string command) {
            var reply = transport.SendForString(command);
            if (!reply.Terminated) {
                throw new OnStepXException($"Invalid reply to {command}: '{reply}'");
            }
            return reply.Text;
        }

        private double ReadSexagesimal(string command) {
            var reply = Transport.SendForString(command);
            if (reply.Terminated && TryParseSexagesimal(reply.Text, out double value)) {
                return value;
            }
            throw new OnStepXException($"Invalid reply to {command}: '{reply}'");
        }

        private static readonly char[] SexagesimalSeparators = [':', '*', '\'', '"', ' ', '°', 'ß'];

        /// <summary>
        /// Like INDI's f_scansexa: "HH:MM:SS", "sDD*MM:SS" and the low-precision "HH:MM.T" / "sDD*MM", each field after
        /// the first a 60th of the one before. The LX200 degree sign 0xDF arrives as 'ß'.
        /// </summary>
        internal static bool TryParseSexagesimal(string text, out double value) {
            value = double.NaN;
            string s = text.Trim();
            bool negative = s.StartsWith('-');
            if (s.StartsWith('-') || s.StartsWith('+')) {
                s = s[1..];
            }

            string[] fields = s.Split(SexagesimalSeparators, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length is 0 or > 3) {
                return false;
            }

            double result = 0;
            double unit = 1;
            foreach (string field in fields) {
                if (!double.TryParse(field, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double part)) {
                    return false;
                }
                result += part / unit;
                unit *= 60;
            }

            value = negative ? -result : result;
            return true;
        }
    }
}
