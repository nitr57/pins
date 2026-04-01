#region "copyright"

/*
    Copyright © 2025-2026 Nico Trost <nico.trost57@gmail.com> and the PI.N.S. contributors

    This file is part of PI 'N' Stars.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System;
using System.Collections.Generic;

namespace NINA.INDI.Interfaces
{

    /// <summary>
    /// INDI-layer contract for a generic AUX device that exposes its writable
    /// switch and numeric properties as a flat list of controllable channels.
    /// Used by the Pegasus UPB and similar power/USB hub drivers.
    /// </summary>
    public interface IINDISwitchHub : IINDIDevice
    {

        /// <summary>
        /// Returns all controllable channels discovered after the device connected.
        /// Each <see cref="INDISwitchDescriptor"/> maps to one INDI property element.
        /// </summary>
        IReadOnlyList<INDISwitchDescriptor> GetDescriptors();

        /// <summary>
        /// Reads the current value of a channel.
        /// Returns 0.0/1.0 for boolean switch elements; the raw numeric value otherwise.
        /// </summary>
        double GetValue(INDISwitchDescriptor descriptor);

        /// <summary>Writes a boolean on/off value to an INDI switch element.</summary>
        void SetBoolElement(INDISwitchDescriptor descriptor, bool value);

        /// <summary>Writes a numeric value to an INDI number element.</summary>
        void SetNumberElement(INDISwitchDescriptor descriptor, double value);

        /// <summary>
        /// Raised whenever the INDI server pushes an updated value for any property
        /// (setSwitchVector or setNumberVector).  The argument is the INDI property name.
        /// </summary>
        event System.Action<string> ValuesUpdated;
    }
}