#region "copyright"

/*
    Copyright © 2026 Nico Trost <nico.trost57@gmail.com> and the PI.N.S. contributors

    This file is part of PI 'N' Stars.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Core.Enum;
using NINA.Core.Utility.WindowService;
using NINA.WPF.Base.ViewModel.Equipment.Telescope;
using NUnit.Framework;
using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace NINA.Test.SystemWindowsCompat {

    /// <summary>
    /// The location sync prompt on mount connect (TelescopeLocationSyncDirection PROMPT) offers its three choices as
    /// buttons of the headless dialog; before, it only had the fallback Cancel, which syncs nothing.
    /// </summary>
    [TestFixture]
    [NonParallelizable]
    public class LatLongSyncDialogTest {

        [SetUp]
        public void SetUp() {
            DialogService.CloseAllDialogs();
        }

        [TearDown]
        public void TearDown() {
            DialogService.CloseAllDialogs();
        }

        [TestCase("Sync to mount", TelescopeLocationSyncDirection.TOTELESCOPE)]
        [TestCase("Sync from mount", TelescopeLocationSyncDirection.TOAPPLICATION)]
        [TestCase("No sync", TelescopeLocationSyncDirection.NOSYNC)]
        public async Task Button_SetsTheChoice_AndClosesTheDialog(string button, TelescopeLocationSyncDirection expected) {
            var vm = new TelescopeLatLongSyncVM(48.1, 11.6, 520, 0, 0, 0);

            var shown = new WindowService().ShowDialog(vm, "Sync Lat/Long");
            var dialog = DialogService.GetAllDialogs().Single(d => d.ContentType == typeof(TelescopeLatLongSyncVM).FullName);

            Assert.That(dialog.Buttons.Select(b => b.Name), Is.SupersetOf(new[] { "Sync to mount", "Sync from mount", "No sync" }));
            Assert.That(dialog.Buttons.Select(b => b.Name), Does.Not.Contain("Cancel"));

            Assert.That(DialogService.ClickButton(dialog.DialogId, button), Is.True);
            Assert.That(await Task.WhenAny(shown.Task, Task.Delay(TimeSpan.FromSeconds(5))), Is.SameAs(shown.Task), "the dialog closed");
            Assert.That(vm.Mode, Is.EqualTo(expected));
        }
    }
}
