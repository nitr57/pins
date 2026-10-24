#region "copyright"

/*
    Copyright © 2025-2026 Nico Trost <nico.trost57@gmail.com> and the PI.N.S. contributors

    This file is part of PI 'N' Stars.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Core.Utility;
using NINA.INDI.Enums;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml.Linq;

namespace NINA.INDI.Protocol {
    public static class INDIProtocolParser {
        /// <summary>
        /// Tolerant parse for INDI "numberValue" text. The spec permits both plain decimal
        /// numbers and sexagesimal notation ("sdd:mm:ss.s" / "sdd:mm.m", used for coordinates
        /// like RA/Dec), and an empty value is legal too. A single malformed element must not
        /// throw and drop the entire vector (the caller logs/catches upstream), so this falls
        /// back to 0 and logs rather than propagating a FormatException.
        /// </summary>
        public static double ParseNumberValue(string raw) {
            if (string.IsNullOrWhiteSpace(raw)) return 0;
            raw = raw.Trim();

            if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var simple)) {
                return simple;
            }

            var negative = raw.StartsWith("-");
            var parts = (negative ? raw[1..] : raw).Split(':');
            if (parts.Length is 2 or 3
                && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var whole)
                && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var minutes)) {
                var seconds = 0.0;
                if (parts.Length == 3) {
                    double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out seconds);
                }
                var value = whole + minutes / 60.0 + seconds / 3600.0;
                return negative ? -value : value;
            }

            Logger.Warning($"INDI: could not parse number value '{raw}', defaulting to 0");
            return 0;
        }
        public static PropertyState ParseState(string state) {
            return state.ToLower() switch {
                "idle" => PropertyState.Idle,
                "ok" => PropertyState.Ok,
                "busy" => PropertyState.Busy,
                "alert" => PropertyState.Alert,
                _ => PropertyState.Idle
            };
        }

        public static PropertyPermission ParsePermission(string perm) {
            return perm.ToLower() switch {
                "ro" => PropertyPermission.ReadOnly,
                "wo" => PropertyPermission.WriteOnly,
                "rw" => PropertyPermission.ReadWrite,
                _ => PropertyPermission.ReadOnly
            };
        }

        public static SwitchRule ParseRule(string rule) {
            return rule.ToLower() switch {
                "oneofmany" => SwitchRule.OneOfMany,
                "atmostone" => SwitchRule.AtMostOne,
                "anyofmany" => SwitchRule.AnyOfMany,
                _ => SwitchRule.OneOfMany
            };
        }

        public static INDINumberProperty ParseDefNumberVector(XElement element) {
            var prop = new INDINumberProperty {
                DeviceName = element.Attribute("device")?.Value ?? string.Empty,
                Name = element.Attribute("name")?.Value ?? string.Empty,
                Label = element.Attribute("label")?.Value ?? string.Empty,
                Group = element.Attribute("group")?.Value ?? string.Empty,
                State = ParseState(element.Attribute("state")?.Value ?? "Idle"),
                Permission = ParsePermission(element.Attribute("perm")?.Value ?? "ro"),
                Timestamp = element.Attribute("timestamp")?.Value ?? string.Empty
            };

            foreach (var defNum in element.Elements("defNumber")) {
                prop.Numbers.Add(new INDINumber {
                    Name = defNum.Attribute("name")?.Value ?? string.Empty,
                    Label = defNum.Attribute("label")?.Value ?? string.Empty,
                    Format = defNum.Attribute("format")?.Value ?? "%g",
                    Min = ParseNumberValue(defNum.Attribute("min")?.Value),
                    Max = ParseNumberValue(defNum.Attribute("max")?.Value),
                    Step = ParseNumberValue(defNum.Attribute("step")?.Value),
                    Value = ParseNumberValue(defNum.Value)
                });
            }

            return prop;
        }

        public static INDISwitchProperty ParseDefSwitchVector(XElement element) {
            var prop = new INDISwitchProperty {
                DeviceName = element.Attribute("device")?.Value ?? string.Empty,
                Name = element.Attribute("name")?.Value ?? string.Empty,
                Label = element.Attribute("label")?.Value ?? string.Empty,
                Group = element.Attribute("group")?.Value ?? string.Empty,
                State = ParseState(element.Attribute("state")?.Value ?? "Idle"),
                Permission = ParsePermission(element.Attribute("perm")?.Value ?? "ro"),
                Rule = ParseRule(element.Attribute("rule")?.Value ?? "OneOfMany"),
                Timestamp = element.Attribute("timestamp")?.Value ?? string.Empty
            };

            foreach (var defSwitch in element.Elements("defSwitch")) {
                var cleanValue = defSwitch.Value.Replace("\r", "").Replace("\n", "").Trim();
                prop.Switches.Add(new INDISwitch {
                    Name = defSwitch.Attribute("name")?.Value ?? string.Empty,
                    Label = defSwitch.Attribute("label")?.Value ?? string.Empty,
                    Value = cleanValue.ToLower() == "on"
                });
            }

            return prop;
        }

        public static INDITextProperty ParseDefTextVector(XElement element) {
            var prop = new INDITextProperty {
                DeviceName = element.Attribute("device")?.Value ?? string.Empty,
                Name = element.Attribute("name")?.Value ?? string.Empty,
                Label = element.Attribute("label")?.Value ?? string.Empty,
                Group = element.Attribute("group")?.Value ?? string.Empty,
                State = ParseState(element.Attribute("state")?.Value ?? "Idle"),
                Permission = ParsePermission(element.Attribute("perm")?.Value ?? "ro"),
                Timestamp = element.Attribute("timestamp")?.Value ?? string.Empty
            };

            foreach (var defText in element.Elements("defText")) {
                prop.Texts.Add(new INDIText {
                    Name = defText.Attribute("name")?.Value ?? string.Empty,
                    Label = defText.Attribute("label")?.Value ?? string.Empty,
                    Value = defText.Value.Replace("\r", "").Replace("\n", "").Trim()
                });
            }

            return prop;
        }

        public static INDIBlobProperty ParseDefBlobVector(XElement element) {
            var prop = new INDIBlobProperty {
                DeviceName = element.Attribute("device")?.Value ?? string.Empty,
                Name = element.Attribute("name")?.Value ?? string.Empty,
                Label = element.Attribute("label")?.Value ?? string.Empty,
                Group = element.Attribute("group")?.Value ?? string.Empty,
                State = ParseState(element.Attribute("state")?.Value ?? "Idle"),
                Permission = ParsePermission(element.Attribute("perm")?.Value ?? "ro"),
                Timestamp = element.Attribute("timestamp")?.Value ?? string.Empty
            };

            foreach (var defBlob in element.Elements("defBLOB")) {
                prop.Blobs.Add(new INDIBlob {
                    Name = defBlob.Attribute("name")?.Value ?? string.Empty,
                    Label = defBlob.Attribute("label")?.Value ?? string.Empty,
                    Format = string.Empty,
                    Data = []
                });
            }

            return prop;
        }

        public static void UpdateNumberProperty(INDINumberProperty prop, XElement element) {
            prop.State = ParseState(element.Attribute("state")?.Value ?? "Idle");
            prop.Timestamp = element.Attribute("timestamp")?.Value ?? string.Empty;

            foreach (var oneNum in element.Elements("oneNumber")) {
                var name = oneNum.Attribute("name")?.Value ?? string.Empty;
                var number = prop.Numbers.FirstOrDefault(n => n.Name == name);
                if (number != null) {
                    // Drivers can change an element's range at runtime via IUUpdateMinMax(),
                    // which attaches min/max/step attributes to the oneNumber elements
                    // (e.g. toupbase widening the gain range after connect). Honor them.
                    var min = oneNum.Attribute("min");
                    if (min != null) number.Min = ParseNumberValue(min.Value);
                    var max = oneNum.Attribute("max");
                    if (max != null) number.Max = ParseNumberValue(max.Value);
                    var step = oneNum.Attribute("step");
                    if (step != null) number.Step = ParseNumberValue(step.Value);

                    number.Value = ParseNumberValue(oneNum.Value);
                }
            }
        }

        public static void UpdateSwitchProperty(INDISwitchProperty prop, XElement element) {
            prop.State = ParseState(element.Attribute("state")?.Value ?? "Idle");
            prop.Timestamp = element.Attribute("timestamp")?.Value ?? string.Empty;

            foreach (var oneSwitch in element.Elements("oneSwitch")) {
                var name = oneSwitch.Attribute("name")?.Value ?? string.Empty;
                var sw = prop.Switches.FirstOrDefault(s => s.Name == name);
                if (sw != null) {
                    var cleanValue = oneSwitch.Value.Replace("\r", "").Replace("\n", "").Trim();
                    sw.Value = cleanValue.ToLower() == "on";
                }
            }
        }

        public static void UpdateTextProperty(INDITextProperty prop, XElement element) {
            prop.State = ParseState(element.Attribute("state")?.Value ?? "Idle");
            prop.Timestamp = element.Attribute("timestamp")?.Value ?? string.Empty;

            foreach (var oneText in element.Elements("oneText")) {
                var name = oneText.Attribute("name")?.Value ?? string.Empty;
                var text = prop.Texts.FirstOrDefault(t => t.Name == name);
                if (text != null) {
                    text.Value = oneText.Value.Replace("\r", "").Replace("\n", "").Trim();
                }
            }
        }

        public static INDILightProperty ParseDefLightVector(XElement element) {
            var prop = new INDILightProperty {
                DeviceName = element.Attribute("device")?.Value ?? string.Empty,
                Name = element.Attribute("name")?.Value ?? string.Empty,
                Label = element.Attribute("label")?.Value ?? string.Empty,
                Group = element.Attribute("group")?.Value ?? string.Empty,
                State = ParseState(element.Attribute("state")?.Value ?? "Idle"),
                Permission = PropertyPermission.ReadOnly,
                Timestamp = element.Attribute("timestamp")?.Value ?? string.Empty
            };

            foreach (var defLight in element.Elements("defLight")) {
                prop.Lights.Add(new INDILight {
                    Name = defLight.Attribute("name")?.Value ?? string.Empty,
                    Label = defLight.Attribute("label")?.Value ?? string.Empty,
                    State = ParseState(defLight.Value.Replace("\r", "").Replace("\n", "").Trim())
                });
            }

            return prop;
        }

        public static void UpdateLightProperty(INDILightProperty prop, XElement element) {
            prop.State = ParseState(element.Attribute("state")?.Value ?? "Idle");
            prop.Timestamp = element.Attribute("timestamp")?.Value ?? string.Empty;

            foreach (var oneLight in element.Elements("oneLight")) {
                var name = oneLight.Attribute("name")?.Value ?? string.Empty;
                var light = prop.Lights.FirstOrDefault(l => l.Name == name);
                if (light != null) {
                    light.State = ParseState(oneLight.Value.Replace("\r", "").Replace("\n", "").Trim());
                }
            }
        }

        /// <summary>
        /// One decoded <c>oneBLOB</c> of a <c>setBLOBVector</c>. <see cref="Data"/> is null when the
        /// element carried no payload (the blob then keeps its previous data).
        /// </summary>
        public sealed record DecodedBlob(string Name, string Format, byte[] Data);

        public static void UpdateBlobProperty(INDIBlobProperty prop, XElement element) {
            ApplyBlobUpdate(prop, element, DecodeBlobs(element));
        }

        /// <summary>
        /// Decodes the payloads of a <c>setBLOBVector</c> without touching any property, so a caller
        /// can do this expensive part (hundreds of milliseconds for a camera image on a Pi) outside
        /// its locks and apply the result with <see cref="ApplyBlobUpdate"/>.
        /// </summary>
        public static List<DecodedBlob> DecodeBlobs(XElement element) {
            var decoded = new List<DecodedBlob>();
            foreach (var oneBlob in element.Elements("oneBLOB")) {
                var name = oneBlob.Attribute("name")?.Value ?? string.Empty;
                var format = oneBlob.Attribute("format")?.Value ?? string.Empty;

                // Decode base64 BLOB data. Convert.FromBase64String ignores embedded
                // whitespace, so the payload (libindi wraps it in 72-char lines) is passed
                // through as-is — stripping the line breaks first would copy the
                // multi-megabyte string twice per frame.
                var base64Data = oneBlob.Value;
                if (string.IsNullOrWhiteSpace(base64Data)) {
                    decoded.Add(new DecodedBlob(name, format, null));
                    continue;
                }

                try {
                    var data = Convert.FromBase64String(base64Data);

                    // A ".z" format suffix means the driver zlib-compressed the payload
                    // (libindi does this when the device's CCD_COMPRESSION is enabled —
                    // reachable via the INDI control panel). Inflate here so downstream
                    // consumers always see the raw payload and its real format.
                    if (format.EndsWith(".z", StringComparison.OrdinalIgnoreCase)) {
                        data = InflateZlib(data);
                        format = format[..^2];
                    }

                    decoded.Add(new DecodedBlob(name, format, data));
                } catch (Exception ex) {
                    Logger.Warning($"INDI: failed to decode BLOB '{name}' (format '{format}'): {ex.Message}");
                    decoded.Add(new DecodedBlob(name, format, []));
                }
            }
            return decoded;
        }

        /// <summary>Applies the state and timestamp of a <c>setBLOBVector</c> and its blobs decoded by <see cref="DecodeBlobs"/>.</summary>
        public static void ApplyBlobUpdate(INDIBlobProperty prop, XElement element, IReadOnlyList<DecodedBlob> decoded) {
            prop.State = ParseState(element.Attribute("state")?.Value ?? "Idle");
            prop.Timestamp = element.Attribute("timestamp")?.Value ?? string.Empty;

            foreach (var d in decoded) {
                var blob = prop.Blobs.FirstOrDefault(b => b.Name == d.Name);
                if (blob == null) {
                    blob = new INDIBlob { Name = d.Name };
                    prop.Blobs.Add(blob);
                }

                blob.Format = d.Format;
                if (d.Data != null) {
                    blob.Data = d.Data;
                }
            }
        }

        private static byte[] InflateZlib(byte[] compressed) {
            using var input = new MemoryStream(compressed);
            using var zlib = new ZLibStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            zlib.CopyTo(output);
            return output.ToArray();
        }
    }
}
