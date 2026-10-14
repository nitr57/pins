// SPDX-License-Identifier: MPL-2.0

using Moq;
using NINA.Equipment.Interfaces;
using NINA.Mcp.Observatory;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Test.Mcp {

    /// <summary>control_dome in the MCP adapter: what a job cancellation (cancel_job, stop_all, secure_observatory) does to the dome.</summary>
    [TestFixture]
    public class NinaObservatoryDomeTest {

        [Test]
        public async Task CloseShutter_CancellingTheJob_NeverReachesTheClose() {
            var harness = new NinaObservatoryHarness();
            var info = harness.ConnectDome(ShutterState.ShutterOpen);
            var closing = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationToken closeToken = default;

            harness.Dome
                .Setup(mediator => mediator.CloseShutter(It.IsAny<CancellationToken>()))
                .Returns<CancellationToken>(token => {
                    closeToken = token;
                    return closing.Task;
                });

            using var job = new CancellationTokenSource();
            var result = harness.Observatory.ControlDomeAsync(DomeAction.CloseShutter, null, job.Token);

            job.Cancel();

            Assert.That(async () => await result, Throws.InstanceOf<OperationCanceledException>());
            Assert.That(closeToken.CanBeCanceled, Is.True, "the close runs on a timeout token of its own");
            Assert.That(closeToken.IsCancellationRequested, Is.False);
            harness.Dome.Verify(mediator => mediator.GetDevice(), Times.Never, "no stop may be sent to a closing dome");

            info.ShutterStatus = ShutterState.ShutterClosed;
            closing.SetResult(true);

            await Task.Delay(50);

            Assert.That(closeToken.IsCancellationRequested, Is.False);
        }

        [Test]
        public async Task CloseShutter_ReportsClosedWhenTheDomeIsClosed() {
            var harness = new NinaObservatoryHarness();
            harness.ConnectDome(ShutterState.ShutterClosed);
            harness.Dome.Setup(mediator => mediator.CloseShutter(It.IsAny<CancellationToken>())).ReturnsAsync(true);

            var result = await harness.Observatory.ControlDomeAsync(DomeAction.CloseShutter, null, CancellationToken.None);

            Assert.That(result.Success, Is.True, result.Message);
            Assert.That(result.Message, Is.EqualTo("Shutter closed."));
        }
    }
}
