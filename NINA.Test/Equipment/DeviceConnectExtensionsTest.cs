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

            await AssertCancelled(connect);
            device.Verify(x => x.Disconnect(), Times.Never);

            driverConnect.SetResult(true);

            await WaitUntil(() => DisconnectCalls(device) == 1);
        }

        /// <summary>
        /// Verifies that an abandoned attempt which ends without connecting does not trigger a disconnect,
        /// even when the device reports Connected (e.g. because a newer connection exists) - the cleanup must
        /// only act on the abandoned attempt's own result.
        /// </summary>
        [Test]
        public async Task ConnectWithCancellation_WhenAbandonedAttemptFails_DoesNotDisconnect() {
            Mock<IDevice> device = CreateDevice();
            device.SetupGet(x => x.Connected).Returns(true);
            TaskCompletionSource<bool> driverConnect = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            device.Setup(x => x.Connect(It.IsAny<CancellationToken>())).Returns(driverConnect.Task);
            using CancellationTokenSource cts = new CancellationTokenSource();

            Task<bool> connect = device.Object.ConnectWithCancellation(cts.Token);
            cts.Cancel();
            await AssertCancelled(connect);

            driverConnect.SetException(new InvalidOperationException("driver timed out"));

            // A second connect waits for the abandoned attempt's cleanup, so once it returns the cleanup has run.
            device.Setup(x => x.Connect(It.IsAny<CancellationToken>())).ReturnsAsync(false);
            (await device.Object.ConnectWithCancellation(CancellationToken.None).WaitAsync(ConditionTimeout)).Should().BeFalse();
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
            await AssertCancelled(first);

            Task<bool> second = device.Object.ConnectWithCancellation(CancellationToken.None);
            await Task.Delay(200);
            second.IsCompleted.Should().BeFalse();
            Volatile.Read(ref connectCalls).Should().Be(1);

            firstConnect.SetResult(false);

            (await second.WaitAsync(ConditionTimeout)).Should().BeTrue();
            Volatile.Read(ref connectCalls).Should().Be(2);
        }

        /// <summary>
        /// Verifies that the serialization also holds across different wrapper instances of the same physical device.
        /// The VMs wrap devices in a fresh decorator on every attempt (PersistSettingsCameraDecorator, backlash focuser
        /// decorators) and a rescan creates new instances, so the abandoned attempt must be tracked by device Id. The late
        /// cleanup must only disconnect the abandoned wrapper, never the newer connection.
        /// </summary>
        [Test]
        public async Task ConnectWithCancellation_WhenNewWrapperForSameDevice_WaitsAndCleanupLeavesNewConnectionAlone() {
            string id = Guid.NewGuid().ToString();
            Mock<IDevice> firstWrapper = CreateDevice(id);
            Mock<IDevice> secondWrapper = CreateDevice(id);
            TaskCompletionSource<bool> firstConnect = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            firstWrapper.Setup(x => x.Connect(It.IsAny<CancellationToken>())).Returns(firstConnect.Task);
            secondWrapper.Setup(x => x.Connect(It.IsAny<CancellationToken>())).ReturnsAsync(true);
            using CancellationTokenSource cts = new CancellationTokenSource();

            Task<bool> first = firstWrapper.Object.ConnectWithCancellation(cts.Token);
            cts.Cancel();
            await AssertCancelled(first);

            Task<bool> second = secondWrapper.Object.ConnectWithCancellation(CancellationToken.None);
            await Task.Delay(200);
            second.IsCompleted.Should().BeFalse();
            secondWrapper.Verify(x => x.Connect(It.IsAny<CancellationToken>()), Times.Never);

            firstConnect.SetResult(true);

            (await second.WaitAsync(ConditionTimeout)).Should().BeTrue();
            DisconnectCalls(firstWrapper).Should().Be(1, "the late success of the abandoned attempt must be torn down before the next connect starts");
            secondWrapper.Verify(x => x.Disconnect(), Times.Never);
        }

        /// <summary>
        /// Verifies that a driver honouring the token propagates its cancellation, is not disconnected, and does not delay
        /// a following connect.
        /// </summary>
        [Test]
        public async Task ConnectWithCancellation_WhenDriverHonoursToken_PropagatesCancellationWithoutDisconnect() {
            Mock<IDevice> device = CreateDevice();
            device.Setup(x => x.Connect(It.IsAny<CancellationToken>()))
                .Returns((CancellationToken ct) => Task.Delay(Timeout.Infinite, ct).ContinueWith(_ => true, ct, TaskContinuationOptions.None, TaskScheduler.Default));
            using CancellationTokenSource cts = new CancellationTokenSource();

            Task<bool> connect = device.Object.ConnectWithCancellation(cts.Token);
            cts.Cancel();
            await AssertCancelled(connect);

            device.Setup(x => x.Connect(It.IsAny<CancellationToken>())).ReturnsAsync(true);
            (await device.Object.ConnectWithCancellation(CancellationToken.None).WaitAsync(ConditionTimeout)).Should().BeTrue();
            device.Verify(x => x.Disconnect(), Times.Never);
        }

        /// <summary>
        /// Verifies that no real connect is started when the caller already cancelled, so a token-ignoring driver is not
        /// connected only to be torn down again.
        /// </summary>
        [Test]
        public async Task ConnectWithCancellation_WhenTokenAlreadyCancelled_DoesNotStartConnect() {
            Mock<IDevice> device = CreateDevice();
            device.Setup(x => x.Connect(It.IsAny<CancellationToken>())).ReturnsAsync(true);
            using CancellationTokenSource cts = new CancellationTokenSource();
            cts.Cancel();

            await AssertCancelled(device.Object.ConnectWithCancellation(cts.Token));

            device.Verify(x => x.Connect(It.IsAny<CancellationToken>()), Times.Never);
            device.Verify(x => x.Disconnect(), Times.Never);
        }

        // Each test gets its own Id by default: abandoned attempts are tracked per Id in static state.
        private static Mock<IDevice> CreateDevice(string id = null) {
            Mock<IDevice> device = new Mock<IDevice>();
            device.SetupGet(x => x.Id).Returns(id ?? Guid.NewGuid().ToString());
            device.SetupGet(x => x.Name).Returns("TestDevice");
            device.SetupGet(x => x.Connected).Returns(false);
            return device;
        }

        private static async Task AssertCancelled(Task<bool> connect) {
            Func<Task> awaitConnect = () => connect.WaitAsync(ConditionTimeout);
            await awaitConnect.Should().ThrowAsync<OperationCanceledException>();
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
