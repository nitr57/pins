#region "copyright"

/*
    Copyright © 2026 Nico Trost <nico.trost57@gmail.com> and the PI.N.S. contributors

    This file is part of PI 'N' Stars.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System;
using System.Collections.Generic;

namespace NINA.INDI.Model {

    /// <summary>
    /// Describes how a given INDI mount driver lets a client choose its manual
    /// (MoveAxis) slew rate. INDI drivers are inconsistent here:
    /// <list type="bullet">
    /// <item><see cref="Discrete"/>: a <c>TELESCOPE_SLEW_RATE</c> switch vector with N
    /// named steps (e.g. Guide / Centering / Find / Max, or 1x / 8x / 64x). Counts and
    /// labels vary per driver.</item>
    /// <item><see cref="Continuous"/>: a numeric <c>TELESCOPE_MOTION_RATE</c> in °/s with
    /// a driver-provided min/max/step.</item>
    /// <item><see cref="None"/>: the driver exposes no rate control; motion runs at a
    /// fixed internal rate.</item>
    /// </list>
    /// The frontend should render its rate control from this descriptor instead of
    /// assuming a fixed scale.
    /// </summary>
    public enum SlewRateKind {
        None,
        Discrete,
        Continuous
    }

    /// <summary>
    /// One selectable step of a <see cref="SlewRateKind.Discrete"/> driver, taken
    /// directly from a <c>TELESCOPE_SLEW_RATE</c> switch.
    /// </summary>
    public class SlewRateOption {
        /// <summary>Zero-based position of the switch within the vector. This is the
        /// stable selector a client sends back to choose this rate.</summary>
        public int Index { get; init; }

        /// <summary>INDI switch element name (e.g. <c>SLEW_MAX</c>).</summary>
        public string Name { get; init; } = string.Empty;

        /// <summary>Human-readable label as provided by the driver (e.g. "Max").
        /// Falls back to <see cref="Name"/> when the driver supplies no label.</summary>
        public string Label { get; init; } = string.Empty;

        /// <summary>Whether this step is the driver's currently selected rate.</summary>
        public bool IsSelected { get; init; }

        /// <summary>Best-effort conversion of the label to °/s for callers that need a
        /// physical rate (e.g. PolarAlignment move-timeout math). Null when the label
        /// cannot be parsed.</summary>
        public double? EstimatedRateDps { get; init; }
    }

    /// <summary>
    /// Capability descriptor for a mount's manual slew rate control. Built by
    /// introspecting the connected INDI driver's properties; see
    /// <c>INDITelescope.GetSlewRateCapability</c>.
    /// </summary>
    public class SlewRateCapability {
        public SlewRateKind Kind { get; init; }

        /// <summary>The backing INDI property this descriptor was derived from
        /// (e.g. <c>TELESCOPE_SLEW_RATE</c> or <c>TELESCOPE_MOTION_RATE</c>), or empty
        /// when <see cref="Kind"/> is <see cref="SlewRateKind.None"/>.</summary>
        public string PropertyName { get; init; } = string.Empty;

        /// <summary>Discrete steps, ordered by index. Empty unless
        /// <see cref="Kind"/> is <see cref="SlewRateKind.Discrete"/>.</summary>
        public IReadOnlyList<SlewRateOption> Options { get; init; } = Array.Empty<SlewRateOption>();

        /// <summary>Minimum numeric rate. Meaningful only for
        /// <see cref="SlewRateKind.Continuous"/>.</summary>
        public double Min { get; init; }

        /// <summary>Maximum numeric rate. Meaningful only for
        /// <see cref="SlewRateKind.Continuous"/>.</summary>
        public double Max { get; init; }

        /// <summary>Driver-provided step granularity. Meaningful only for
        /// <see cref="SlewRateKind.Continuous"/>; may be 0 when the driver does not
        /// constrain it.</summary>
        public double Step { get; init; }

        /// <summary>Unit of the continuous range. Always "°/s" for the
        /// <c>TELESCOPE_MOTION_RATE</c> path.</summary>
        public string Unit { get; init; } = string.Empty;

        /// <summary>Driver's current numeric rate. Meaningful only for
        /// <see cref="SlewRateKind.Continuous"/>.</summary>
        public double? CurrentValue { get; init; }

        public static SlewRateCapability None() => new() { Kind = SlewRateKind.None };
    }
}
