#region "copyright"

/*
    Copyright © 2025 Nico Trost <nico.trost57@gmail.com> and the PI.N.S. contributors

    This file is part of PI 'N' Stars.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace NINA.Core.SignalR {
    public interface IMyMessageBoxBroadcaster {
        Task<MessageBoxResult> ShowMessageBoxAsync(string messageBoxText, string caption, MessageBoxButton button, MessageBoxResult defaultResult, System.TimeSpan timeout = default);

        /// <summary>
        /// As the overload without a token, and ended by <paramref name="token"/> with <paramref name="defaultResult"/>.
        /// <see cref="Timeout.InfiniteTimeSpan"/> waits for an answer without a time limit.
        /// </summary>
        Task<MessageBoxResult> ShowMessageBoxAsync(string messageBoxText, string caption, MessageBoxButton button, MessageBoxResult defaultResult, System.TimeSpan timeout, CancellationToken token);
        Task HandleMessageBoxResponseAsync(string messageBoxId, string result);
    }
}
