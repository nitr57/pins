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

namespace NINA.Equipment.SDK.TelescopeSDKs.OnStepXSDK {

    /// <summary>
    /// The byte stream to an OnStepX controller. Kept this small so the protocol can be tested against a fake port.
    /// </summary>
    public interface IOnStepXPort : IDisposable {
        string PortName { get; }

        bool IsOpen { get; }

        void Open();

        void Close();

        void Write(string text);

        /// <summary>The next byte, or -1 when none arrived within <paramref name="timeout"/>.</summary>
        int ReadByte(TimeSpan timeout);

        void DiscardInBuffer();
    }
}
