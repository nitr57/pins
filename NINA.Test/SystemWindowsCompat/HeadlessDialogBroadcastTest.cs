#region "copyright"

/*
    Copyright © 2026 Nico Trost <nico.trost57@gmail.com> and the PI.N.S. contributors

    This file is part of PI 'N' Stars.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Microsoft.AspNetCore.SignalR;
using Moq;
using NINA.Core.Model;
using NINA.Core.SignalR;
using NINA.Core.Utility;
using NINA.Core.Utility.WindowService;
using NINA.PlateSolving;
using NINA.WPF.Base.ViewModel;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

namespace NINA.Test.SystemWindowsCompat {

    /// <summary>
    /// What the headless WindowService broadcasts to Touch-N-Stars after a dialog opened, and which dialog a button click
    /// from Touch-N-Stars reaches. DialogBroadcaster.Instance is static: the fixture leaves its capturing broadcaster set.
    /// </summary>
    [TestFixture]
    [NonParallelizable]
    public class HeadlessDialogBroadcastTest {
        private readonly List<DialogData> sent = [];

        [SetUp]
        public void SetUp() {
            DialogService.CloseAllDialogs();
            lock (sent) {
                sent.Clear();
            }

            var proxy = new Mock<IClientProxy>();
            proxy.Setup(p => p.SendCoreAsync("ReceiveDialog", It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
                .Callback<string, object[], CancellationToken>((_, args, _) => {
                    lock (sent) {
                        sent.Add((DialogData)args[0]);
                    }
                })
                .Returns(Task.CompletedTask);
            var clients = new Mock<IHubClients>();
            clients.Setup(c => c.All).Returns(proxy.Object);
            var hub = new Mock<IHubContext<DialogHub>>();
            hub.Setup(h => h.Clients).Returns(clients.Object);
            _ = new DialogBroadcaster(hub.Object);
        }

        [TearDown]
        public void TearDown() {
            DialogService.CloseAllDialogs();
        }

        public sealed class ValueVM : BaseINPC {
            private int value = 1;

            public int Value {
                get => value;
                set {
                    this.value = value;
                    RaisePropertyChanged();
                }
            }

            public ICommand OKCommand { get; } = new RelayCommand(_ => { });

            public ICommand CancelCommand { get; } = new RelayCommand(_ => { });
        }

        [Test]
        public async Task AnUpdate_SendsTheCurrentValues_AndTheDialogsButtons() {
            var vm = new ValueVM();
            _ = new WindowService().ShowDialog(vm, "Values");
            await WaitUntilAsync(() => Sent().Count >= 1, "the dialog is broadcast");

            vm.Value = 2;
            await WaitUntilAsync(() => Sent().Count >= 2, "the update is broadcast");

            var update = Sent().Last();
            Assert.That(update.Parameters["Value"], Is.EqualTo("2"), "not the value from when the dialog opened");
            Assert.That(update.AvailableCommands, Is.EqualTo(new[] { "OK", "Cancel" }), "not a bare Cancel");
        }

        [Test]
        public async Task ThePlateSolveHistory_IsBroadcastOncePerChange_AlsoWhenTheStatusIsShownAgain() {
            // Center keeps one status VM for all its runs
            var status = new PlateSolvingStatusVM();
            var first = new WindowService();
            _ = first.ShowDialog(status, "Center");
            await first.Close();
            _ = new WindowService().ShowDialog(status, "Center");
            await Task.Delay(300);
            int before = Sent().Count;

            status.PlateSolveHistory.Add(new PlateSolveResult());
            await Task.Delay(500);

            Assert.That(Sent().Count - before, Is.EqualTo(1));
        }

        [Test]
        public void AClick_ReachesTheNewestDialogOfItsContentType() {
            var clicked = new List<string>();
            int older = DialogService.RegisterDialog(new DialogService.DialogInfo { Title = "older", ContentType = "Type" });
            DialogService.AddButton(older, "OK", "OK", onClick: () => clicked.Add("older"));
            int newer = DialogService.RegisterDialog(new DialogService.DialogInfo { Title = "newer", ContentType = "Type" });
            DialogService.AddButton(newer, "OK", "OK", onClick: () => clicked.Add("newer"));

            Assert.That(new DialogHub().ClickDialogButton("Type", "OK").Result, Is.True);

            Assert.That(clicked, Is.EqualTo(new[] { "newer" }));
        }

        [Test]
        public void AClick_WithoutAMatchingDialogOrButton_ReportsFailure() {
            int id = DialogService.RegisterDialog(new DialogService.DialogInfo { Title = "t", ContentType = "Type" });
            DialogService.AddButton(id, "OK", "OK", onClick: () => { });

            Assert.That(new DialogHub().ClickDialogButton("Other", "OK").Result, Is.False);
            Assert.That(new DialogHub().ClickDialogButton("Type", "Apply").Result, Is.False);
        }

        private List<DialogData> Sent() {
            lock (sent) {
                return [.. sent];
            }
        }

        private static async Task WaitUntilAsync(Func<bool> condition, string because) {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (!condition()) {
                Assert.That(DateTime.UtcNow, Is.LessThan(deadline), $"timed out waiting until {because}");
                await Task.Delay(20);
            }
        }
    }
}
