#region "copyright"

/*
    Copyright © 2025 Nico Trost <nico.trost57@gmail.com> and the PI.N.S. contributors

    This file is part of PI 'N' Stars.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System;
using System.Collections.Generic;

namespace NINA.Core.Model {
    /// <summary>
    /// DTO for sending dialog data via SignalR - supports all dialog types
    /// </summary>
    public class DialogData {
        public string Title { get; set; }
        public string ContentType { get; set; }
        public bool Active { get; set; }
        public string Status { get; set; }
        public SlewAndCenterData SlewAndCenter { get; set; }
        public Dictionary<string, string> Parameters { get; set; }
        public string StatusMessage { get; set; }
        public List<Dictionary<string, string>> Table { get; set; }
        public List<string> TableHeaders { get; set; }
        public List<string> AvailableCommands { get; set; }
    }

    /// <summary>
    /// DTO for slew and center data
    /// </summary>
    public class SlewAndCenterData {
        public bool Active { get; set; }
        public string Status { get; set; }
        public DialogMeasurement CurrentMeasurement { get; set; }
        public List<DialogMeasurement> Measurements { get; set; }
    }

    /// <summary>
    /// DTO for individual dialog measurement (platesolve, etc)
    /// </summary>
    public class DialogMeasurement {
        public string Time { get; set; }
        public bool Success { get; set; }
        public string ErrorDistance { get; set; }
        public string Rotation { get; set; }
    }
}
