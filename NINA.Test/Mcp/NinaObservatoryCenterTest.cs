// SPDX-License-Identifier: MPL-2.0

using Moq;
using NINA.Astrometry;
using NINA.Equipment.Equipment.MyCamera;
using NINA.Equipment.Equipment.MyTelescope;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Test.Mcp {

    /// <summary>center_target in the MCP adapter.</summary>
    [TestFixture]
    public class NinaObservatoryCenterTest {

        [TestCase(true, true)]
        [TestCase(false, false)]
        public async Task FailedSlew_SaysWhetherGuidingWasLeftStopped(bool wasGuiding, bool mentioned) {
            var harness = new NinaObservatoryHarness();

            harness.Telescope.Setup(mediator => mediator.GetInfo()).Returns(new TelescopeInfo { Connected = true, AtPark = false });
            harness.Camera.Setup(mediator => mediator.GetInfo()).Returns(new CameraInfo { Connected = true, IsExposing = false });
            harness.Guider.Setup(mediator => mediator.StopGuiding(It.IsAny<CancellationToken>())).ReturnsAsync(wasGuiding);
            harness.Telescope
                .Setup(mediator => mediator.SlewToCoordinatesAsync(It.IsAny<Coordinates>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(false);

            var outcome = await harness.Observatory.CenterAsync(5.5, -5.4, CancellationToken.None);

            Assert.That(outcome.Success, Is.False);
            Assert.That(outcome.Message, Does.StartWith("The mount did not complete the slew"));
            Assert.That(outcome.Message.Contains("Guiding was stopped for centering and was not restarted."), Is.EqualTo(mentioned));
            harness.Guider.Verify(
                mediator => mediator.StartGuiding(It.IsAny<bool>(), It.IsAny<System.IProgress<NINA.Core.Model.ApplicationStatus>>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }
    }
}
