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
using System.Windows;

namespace NINA.Core.MyMessageBox {

    /// <summary>
    /// Represents a message box request to be broadcast via SignalR
    /// </summary>
    public class MyMessageBoxMessage {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Title { get; set; }
        public string Text { get; set; }
        public string Button { get; set; }
        public string DefaultResult { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        public string Result { get; set; }
    }
}
