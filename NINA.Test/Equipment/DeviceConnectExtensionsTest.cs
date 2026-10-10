#region "copyright"

/*
    Copyright © 2026 Nico Trost <nico.trost57@gmail.com> and the PI.N.S. contributors

    This file is part of PI 'N' Stars.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using FluentAssertions;
using Moq;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Utility;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Test.Equipment {

    [TestFixture]
    public class DeviceConnectExtensionsTest {
        private static readonly TimeSpan ConditionTimeout = TimeSpan.FromSeconds(5);

        /// <summary>
        /// Verifies that a connect that completes normally returns the driver's result unchanged.
        /// </summary>
        [TestCase(true)]
        [TestCase(false)]
        public async Task ConnectWithCancellation_WhenConnectCompletes_ReturnsDriverResult(bool driverResult) {
            Mock<IDevice> device = CreateDevice();
            device.Setup(x => x.Connect(It.IsAny<CancellationToken>())).ReturnsAsync(driverResult);

            bool result = await device.Object.ConnectWithCancellation(CancellationToken.None);

            result.Should().Be(driverResult);
            device.Verify(x => x.Disconnect(), Times.Never);
        }

        /// <summary>
        /// Verifies that cancelling returns immediately even when the driver ignores the token (issue #33),
        /// and that a driver which connects late is disconnected again instead of being left half-adopted.
        /// </summary>
        [Test]
        public async Task ConnectWithCancellation_WhenDriverIgnoresToken_ThrowsImmediatelyAndDisconnectsLateSuccess() {
            Mock<IDevice> device = CreateDevice();
            TaskCompletionSource<bool> driverConnect = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            device.Setup(x => x.Connect(It.IsAny<CancellationToken>())).Returns(driverConnect.Task);
            using CancellationTokenSource cts = new CancellationTokenSource();

            Task<bool> connect = device.Object.ConnectWithCancellation(cts.Token);
            cts.Cancel();

            Func<Task> awaitConnect = () => connect;
            await awaitConnect.Should().ThrowAsync<OperationCanceledException>();
            device.Verify(x => x.Disconnect(), Times.Never);

            driverConnect.SetResult(true);

            await WaitUntil(() => DisconnectCalls(device) == 1);
        }

        /// <summary>
        /// Verifies that an abandoned attempt which ends without connecting does not trigger a disconnect.
        /// </summary>
        [Test]
        public async Task ConnectWithCancellation_WhenAbandonedAttemptFails_DoesNotDisconnect() {
            Mock<IDevice> device = CreateDevice();
            TaskCompletionSource<bool> driverConnect = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            device.Setup(x => x.Connect(It.IsAny<CancellationToken>())).Returns(driverConnect.Task);
            using CancellationTokenSource cts = new CancellationTokenSource();

            Task<bool> connect = device.Object.ConnectWithCancellation(cts.Token);
            cts.Cancel();
            Func<Task> awaitConnect = () => connect;
            await awaitConnect.Should().ThrowAsync<OperationCanceledException>();

            driverConnect.SetException(new InvalidOperationException("driver timed out"));

            // A second connect waits for the abandoned attempt's cleanup, so once it returns the cleanup has run.
            device.Setup(x => x.Connect(It.IsAny<CancellationToken>())).ReturnsAsync(false);
            (await device.Object.ConnectWithCancellation(CancellationToken.None)).Should().BeFalse();
            device.Verify(x => x.Disconnect(), Times.Never);
        }

        /// <summary>
        /// Verifies that a new connect on the same device does not start while an abandoned attempt is still running,
        /// so two connects never race on one driver.
        /// </summary>
        [Test]
        public async Task ConnectWithCancellation_WhenPreviousAttemptStillRunning_WaitsBeforeConnectingAgain() {
            Mock<IDevice> device = CreateDevice();
            TaskCompletionSource<bool> firstConnect = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            int connectCalls = 0;
            device.Setup(x => x.Connect(It.IsAny<CancellationToken>())).Returns(() => {
                return Interlocked.Increment(ref connectCalls) == 1 ? firstConnect.Task : Task.FromResult(true);
            });
            using CancellationTokenSource cts = new CancellationTokenSource();

            Task<bool> first = device.Object.ConnectWithCancellation(cts.Token);
            cts.Cancel();
            Func<Task> awaitFirst = () => first;
            await awaitFirst.Should().ThrowAsync<OperationCanceledException>();

            Task<bool> second = device.Object.ConnectWithCancellation(CancellationToken.None);
            await Task.Delay(200);
            second.IsCompleted.Should().BeFalse();
            Volatile.Read(ref connectCalls).Should().Be(1);

            firstConnect.SetResult(false);

            (await second.WaitAsync(ConditionTimeout)).Should().BeTrue();
            Volatile.Read(ref connectCalls).Should().Be(2);
        }

        /// <summary>
        /// Verifies that a driver honouring the token propagates its cancellation without being treated as abandoned.
        /// </summary>
        [Test]
        public async Task ConnectWithCancellation_WhenDriverHonoursToken_PropagatesCancellationWithoutCleanup() {
            Mock<IDevice> device = CreateDevice();
            device.Setup(x => x.Connect(It.IsAny<CancellationToken>()))
                .Returns((CancellationToken ct) => Task.FromCanceled<bool>(ct));
            using CancellationTokenSource cts = new CancellationTokenSource();
            cts.Cancel();

            Func<Task> connect = () => device.Object.ConnectWithCancellation(cts.Token);

            await connect.Should().ThrowAsync<OperationCanceledException>();
            device.Verify(x => x.Disconnect(), Times.Never);
        }

        private static Mock<IDevice> CreateDevice() {
            Mock<IDevice> device = new Mock<IDevice>();
            device.SetupGet(x => x.Name).Returns("TestDevice");
            device.SetupGet(x => x.Connected).Returns(false);
            return device;
        }

        private static int DisconnectCalls(Mock<IDevice> device) {
            int calls = 0;
            foreach (var invocation in device.Invocations) {
                if (invocation.Method.Name == nameof(IDevice.Disconnect)) {
                    calls++;
                }
            }
            return calls;
        }

        private static async Task WaitUntil(Func<bool> condition) {
            Stopwatch stopwatch = Stopwatch.StartNew();
            while (!condition()) {
                if (stopwatch.Elapsed > ConditionTimeout) {
                    Assert.Fail("Condition was not met within the timeout");
                }
                await Task.Delay(20);
            }
        }
    }
}
