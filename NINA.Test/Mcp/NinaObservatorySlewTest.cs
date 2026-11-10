// SPDX-License-Identifier: MPL-2.0

using Moq;
using NINA.Astrometry;
using NINA.Core.Model;
using NINA.Equipment.Equipment.MyGuider;
using NINA.Equipment.Equipment.MyTelescope;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Test.Mcp {

    /// <summary>slew_mount in the MCP adapter: guiding is stopped for the slew and started again unless stop_guiding came.</summary>
    [TestFixture]
    public class NinaObservatorySlewTest {

        private NinaObservatoryHarness harness = null!;
        private int stopCalls;

        [SetUp]
        public void SetUp() {
            harness = new NinaObservatoryHarness();
            stopCalls = 0;

            harness.Telescope.Setup(mediator => mediator.GetInfo()).Returns(new TelescopeInfo { Connected = true, AtPark = false });
            harness.Guider.Setup(mediator => mediator.GetInfo()).Returns(new GuiderInfo { Connected = true });

            // The slew's own stop finds guiding running; later stops find it stopped.
            harness.Guider
                .Setup(mediator => mediator.StopGuiding(It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => Interlocked.Increment(ref stopCalls) == 1);
        }

        private void SlewReturns(bool success, Action? during = null) {
            harness.Telescope
                .Setup(mediator => mediator.SlewToCoordinatesAsync(It.IsAny<Coordinates>(), It.IsAny<CancellationToken>()))
                .Returns(() => {
                    during?.Invoke();

                    return Task.FromResult(success);
                });
        }

        private void StartGuidingDoes(Func<CancellationToken, Task<bool>> start) {
            harness.Guider
                .Setup(mediator => mediator.StartGuiding(It.IsAny<bool>(), It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()))
                .Returns<bool, IProgress<ApplicationStatus>, CancellationToken>((_, _, token) => start(token));
        }

        private void VerifyStarts(Func<Times> times) {
            harness.Guider.Verify(
                mediator => mediator.StartGuiding(It.IsAny<bool>(), It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()),
                times);
        }

        [Test]
        public async Task GuidingThatRan_IsStartedAgainAfterTheSlew() {
            SlewReturns(true);
            StartGuidingDoes(_ => Task.FromResult(true));

            var result = await harness.Observatory.SlewAsync(5.5, 20, CancellationToken.None);

            Assert.That(result.Success, Is.True, result.Message);
            Assert.That(result.Message, Does.EndWith("Guiding was stopped for the slew and started again."));
            VerifyStarts(Times.Once);
        }

        [Test]
        public async Task NoGuiding_NoRestart() {
            harness.Guider.Setup(mediator => mediator.StopGuiding(It.IsAny<CancellationToken>())).ReturnsAsync(false);
            SlewReturns(true);

            var result = await harness.Observatory.SlewAsync(5.5, 20, CancellationToken.None);

            Assert.That(result.Success, Is.True, result.Message);
            Assert.That(result.Message, Does.Not.Contain("Guiding"));
            VerifyStarts(Times.Never);
        }

        [Test]
        public async Task RefusedSlew_StartsGuidingAgain_AsNinasSlewInstruction() {
            SlewReturns(false);
            StartGuidingDoes(_ => Task.FromResult(true));

            var result = await harness.Observatory.SlewAsync(5.5, 20, CancellationToken.None);

            Assert.That(result.Success, Is.False);
            Assert.That(result.Message, Does.StartWith("The mount did not complete the slew").And.EndWith("started again."));
            VerifyStarts(Times.Once);
        }

        [Test]
        public async Task StopGuidingDuringTheSlew_KeepsGuidingStopped() {
            SlewReturns(true, during: () => harness.Observatory.StopGuidingAsync(CancellationToken.None).GetAwaiter().GetResult());

            var result = await harness.Observatory.SlewAsync(5.5, 20, CancellationToken.None);

            Assert.That(result.Success, Is.True, result.Message);
            Assert.That(result.Message, Does.EndWith("stays stopped, because stop_guiding was called meanwhile."));
            VerifyStarts(Times.Never);
        }

        [TestCase(true)]
        [TestCase(false)]
        public async Task StopGuidingDuringTheRestart_StopsGuidingAgain(bool startReportsSuccess) {
            SlewReturns(true);
            StartGuidingDoes(async _ => {
                await harness.Observatory.StopGuidingAsync(CancellationToken.None);

                // PHD2 can be guiding after a retry although its start reports failure.
                return startReportsSuccess;
            });

            var result = await harness.Observatory.SlewAsync(5.5, 20, CancellationToken.None);

            Assert.That(result.Success, Is.True, result.Message);
            Assert.That(result.Message, Does.EndWith("stays stopped, because stop_guiding was called meanwhile."));
            Assert.That(stopCalls, Is.EqualTo(3), "the slew's stop, stop_guiding, and the stop after the restart");
        }

        [Test]
        public async Task CancelDuringTheRestart_FailsWithTheGuidingState() {
            using var job = new CancellationTokenSource();

            SlewReturns(true);
            StartGuidingDoes(token => {
                job.Cancel();
                token.ThrowIfCancellationRequested();

                return Task.FromResult(true);
            });

            var result = await harness.Observatory.SlewAsync(5.5, 20, job.Token);

            Assert.That(result.Success, Is.False, "a failure after the cancel request ends the job as cancelled");
            Assert.That(result.Message, Does.StartWith("Slewed to RA 5.5000 h").And.EndWith("starting it again was cancelled."));
        }

        [Test]
        public async Task CancelThenAStartReportingFailure_IsTheCancel() {
            using var job = new CancellationTokenSource();

            SlewReturns(true);
            StartGuidingDoes(_ => {
                job.Cancel();

                // PHD2's start loop ends on the cancel without throwing.
                return Task.FromResult(false);
            });

            var result = await harness.Observatory.SlewAsync(5.5, 20, job.Token);

            Assert.That(result.Success, Is.False);
            Assert.That(result.Message, Does.EndWith("starting it again was cancelled."));
        }

        [Test]
        public async Task TheGuidersOwnCancellation_IsAFailedRestart_NotAFailedSlew() {
            SlewReturns(true);
            StartGuidingDoes(_ => Task.FromException<bool>(new OperationCanceledException()));

            var result = await harness.Observatory.SlewAsync(5.5, 20, CancellationToken.None);

            Assert.That(result.Success, Is.True, result.Message);
            Assert.That(result.Message, Does.StartWith("Slewed to RA 5.5000 h"));
            Assert.That(result.Message, Does.EndWith("could not be started again (see the pins log); start it with start_guiding."));
        }

        [Test]
        public async Task CancelAfterGuidingStarted_ReportsItRunning() {
            using var job = new CancellationTokenSource();

            SlewReturns(true);
            StartGuidingDoes(_ => {
                job.Cancel();

                return Task.FromResult(true);
            });

            var result = await harness.Observatory.SlewAsync(5.5, 20, job.Token);

            Assert.That(result.Success, Is.True, result.Message);
            Assert.That(result.Message, Does.EndWith("started again."));
        }

        [Test]
        public async Task CancelDuringTheSlew_SaysGuidingWasLeftStopped() {
            using var job = new CancellationTokenSource();

            SlewReturns(true, during: job.Cancel);

            var result = await harness.Observatory.SlewAsync(5.5, 20, job.Token);

            Assert.That(result.Success, Is.False);
            Assert.That(result.Message, Does.StartWith("Slew cancelled.").And.Contain("was not restarted"));
            VerifyStarts(Times.Never);
        }
    }
}
