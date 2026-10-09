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
using System.Diagnostics;
using System.Threading;

namespace NINA.Equipment.SDK.TelescopeSDKs.OnStepXSDK {

    /// <summary>
    /// A Proxisky UMi mount (UMi17R, UMi17S, UMi20S): OnStepX with the vendor's :P… commands, as INDI's
    /// LX200_Proxisky uses them.
    /// </summary>
    public class ProxiskyUmiDevice : OnStepXDevice {

        /// <summary>
        /// How long :Pbvg# is retried while connecting (INDI LX200_Proxisky PROXISKY_MODEL_PROBE_SECONDS): the mount
        /// answers it with a bare "0" for about a second after connect. On another OnStepX mount it stays "0", so this is
        /// what connecting to one costs.
        /// </summary>
        public static readonly TimeSpan ModelProbeBudget = TimeSpan.FromSeconds(3);

        public static readonly TimeSpan ModelProbeInterval = TimeSpan.FromMilliseconds(250);

        /// <summary>Pause before the one retry of a capability read (INDI PROXISKY_PROBE_RETRY_NSEC).</summary>
        private static readonly TimeSpan CapabilityRetryPause = TimeSpan.FromMilliseconds(50);

        internal ProxiskyUmiDevice(OnStepXTransport transport, OnStepXIdentification identification, string modelReply)
            : base(transport, identification) {
            ModelReply = modelReply;
            // "UMi17S|1.0.7"
            string[] parts = modelReply.Split('|');
            UmiModel = parts[0].Trim();
            VendorFirmware = parts.Length > 1 ? parts[1].Trim() : string.Empty;
        }

        public override string Model => $"Proxisky {UmiModel}";

        /// <summary>The :Pbvg# reply, e.g. "UMi17S|1.0.7".</summary>
        public string ModelReply { get; }

        /// <summary>e.g. "UMi17S".</summary>
        public string UmiModel { get; }

        /// <summary>Proxisky's firmware version on top of OnStepX, e.g. "1.0.7".</summary>
        public string VendorFirmware { get; }

        /// <summary>:Pbc#: the board keeps its position over a power loss; null when unreadable.</summary>
        public bool? HasPowerLossMemory { get; private set; }

        /// <summary>:PbC#: the board has the anti-collision system; null when unreadable.</summary>
        public bool? HasAntiCollision { get; private set; }

        /// <summary>
        /// INDI LX200_Proxisky::probeModel: the :Pbvg# reply, or null when the controller does not report a Proxisky
        /// model within <paramref name="budget"/>. An unterminated reply other than "0" still counts, as in INDI.
        /// </summary>
        internal static string? ProbeModel(OnStepXTransport transport, TimeSpan budget, TimeSpan interval) {
            var clock = Stopwatch.StartNew();
            for (int attempt = 1; ; attempt++) {
                var reply = transport.SendForString(":Pbvg#");
                if (!reply.IsEmpty && reply.Text != "0") {
                    if (!reply.Terminated) {
                        Logger.Warning($"Proxisky: :Pbvg# replied without a terminator, accepting '{reply.Text}'");
                    }
                    Logger.Info($"Proxisky: model and firmware {reply.Text}");
                    return reply.Text;
                }

                // checked after the read, so a slow mount still gets one full attempt
                if (clock.Elapsed >= budget) {
                    Logger.Info($"OnStepX: no Proxisky model reported by :Pbvg# after {attempt} attempts (last reply '{reply}')");
                    return null;
                }

                Thread.Sleep(interval);
            }
        }

        internal void ReadCapabilities() {
            HasPowerLossMemory = ReadVendorBool(":Pbc#");
            HasAntiCollision = ReadVendorBool(":PbC#");
            Logger.Info($"Proxisky: power-loss memory {Describe(HasPowerLossMemory)}, anti-collision {Describe(HasAntiCollision)}");
        }

        /// <summary>
        /// INDI getVendorBool: a terminated "0" or "1". Retried once after a short pause, as INDI does while probing, so
        /// a single lost byte does not hide a feature.
        /// </summary>
        protected bool? ReadVendorBool(string command) {
            for (int attempt = 0; attempt < 2; attempt++) {
                if (attempt > 0) {
                    Thread.Sleep(CapabilityRetryPause);
                }

                var reply = Transport.SendForString(command);
                if (reply.Terminated && reply.Text is "0" or "1") {
                    return reply.Text == "1";
                }
                Logger.Debug($"Proxisky: unexpected reply to {command}: '{reply}'");
            }
            return null;
        }

        private static string Describe(bool? value) => value switch {
            true => "yes",
            false => "no",
            null => "unknown",
        };
    }
}
