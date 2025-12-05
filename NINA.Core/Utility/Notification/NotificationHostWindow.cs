#region "copyright"

/*
    Copyright © 2016 - 2025 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Microsoft.Win32;
using System;
using System.Windows;

namespace NINA.Core.Utility.Notification {

    public partial class NotificationHostWindow : Window {
        private NotificationManager Manager => DataContext as NotificationManager;

        public NotificationHostWindow() {
        }

        protected void OnClosed(EventArgs e) {
        }

        public void Reposition() {
        }

        public void ShowIfNeeded() {
        }

        public void HideIfPossible() {
        }
    }
}