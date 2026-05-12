#region "copyright"

/*
    Copyright © 2025-2026 Nico Trost <nico.trost57@gmail.com> and the PI.N.S. contributors

    This file is part of PI 'N' Stars.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.INDI.Enums;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.INDI.Interfaces {
    public interface IINDIDevice {
        string Id { get; }
        string Name { get; }
        string DisplayName { get; }
        string Category { get; }
        bool Connected { get; set; }
        string Description { get; }
        string DriverInfo { get; }
        string DriverVersion { get; }

        Task<bool> Connect(CancellationToken ct);
        void Disconnect();
        void Dispose();

        void ConfigureConnectionProperties(string connectionMode, bool autoSearch, string address, string port, int baudRate);
        void ConfigurePreConnectDelay(TimeSpan delay);

        /// <summary>Returns the value of a switch element in a named INDI switch property, or null if not available.</summary>
        bool? GetSwitchPropertyValue(string propertyName, string elementName);

        /// <summary>Returns the overall state of a named INDI light property, or null if not available.</summary>
        PropertyState? GetLightPropertyState(string propertyName);

        IList<string> SupportedActions { get; }
        string Action(string actionName, string actionParameters);
        void CommandBlind(string command, bool raw = false);
        bool CommandBool(string command, bool raw = false);
        string CommandString(string command, bool raw = false);
    }
}
