#region "copyright"

/*
    Copyright © 2025 Nico Trost <nico.trost57@gmail.com> and the PI.N.S. contributors

    This file is part of PI 'N' Stars.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System.Threading.Tasks;
using System.Windows;

namespace NINA.Core.SignalR {
    public interface IMyMessageBoxBroadcaster {
        Task<MessageBoxResult> ShowMessageBoxAsync(string messageBoxText, string caption, MessageBoxButton button, MessageBoxResult defaultResult, System.TimeSpan timeout = default);
        Task HandleMessageBoxResponseAsync(string messageBoxId, string result);
    }
}
