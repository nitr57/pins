// SPDX-License-Identifier: MPL-2.0

using Moq;
using NINA.Core.Model;
using NINA.Equipment.Equipment.MySwitch;
using NINA.Equipment.Interfaces;
using NINA.Mcp.Observatory;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Test.Mcp {

    /// <summary>set_switch in the MCP adapter: switches named by their stable identity, written by identity.</summary>
    [TestFixture]
    public class NinaObservatorySwitchTest {

        private NinaObservatoryHarness harness = null!;

        [SetUp]
        public void SetUp() {
            harness = new NinaObservatoryHarness();
        }

        private static Mock<IWritableSwitch> Writable(short id, string name, double value) {
            var writable = new Mock<IWritableSwitch>();

            writable.SetupGet(s => s.Id).Returns(id);
            writable.SetupGet(s => s.Name).Returns(name);
            writable.SetupGet(s => s.Value).Returns(value);
            writable.SetupGet(s => s.Minimum).Returns(0);
            writable.SetupGet(s => s.Maximum).Returns(1);
            writable.SetupGet(s => s.StepSize).Returns(1);

            return writable;
        }

        private static SwitchInfo Info(string deviceId, params IWritableSwitch[] switches) {
            return new SwitchInfo {
                Connected = true,
                DeviceId = deviceId,
                WritableSwitches = new ReadOnlyCollection<IWritableSwitch>(switches.ToList()),
                ReadonlySwitches = new ReadOnlyCollection<ISwitch>(new List<ISwitch>())
            };
        }

        private void VerifyNoWrite() {
            harness.Switch.Verify(
                mediator => mediator.SetSwitchValueByIdentity(It.IsAny<string>(), It.IsAny<short>(), It.IsAny<double>(), It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()),
                Times.Never);
#pragma warning disable CS0618 // The index write the adapter only uses without a device id.
            harness.Switch.Verify(
                mediator => mediator.SetSwitchValue(It.IsAny<short>(), It.IsAny<double>(), It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()),
                Times.Never);
#pragma warning restore CS0618
        }

        private void VerifyIdentityWrite(string deviceId, short channelId, Func<Times> times) {
            harness.Switch.Verify(
                mediator => mediator.SetSwitchValueByIdentity(deviceId, channelId, 1, It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()),
                times);
        }

        [Test]
        public async Task ByIdentity_WritesByIdentity_AndReadsTheValueFromTheLiveList() {
            var before = Writable(7, "Dew heater", 0);

            // The list is rebuilt during the write: the value is read from the new object, not the one resolved before.
            var after = Writable(7, "Dew heater", 1);

            harness.Switch.SetupSequence(mediator => mediator.GetInfo())
                .Returns(Info("Powerbox", Writable(3, "Camera", 1).Object, before.Object))
                .Returns(Info("Powerbox", after.Object));

            var result = await harness.Observatory.SetSwitchAsync(SwitchTarget.ForIdentity("Powerbox", 7), 1, CancellationToken.None);

            Assert.That(result.Success, Is.True, result.Message);
            Assert.That(result.Message, Is.EqualTo("Switch Dew heater set to 1."));
            VerifyIdentityWrite("Powerbox", 7, Times.Once);
        }

        [Test]
        public async Task ByIndexAndName_AlsoWritesByIdentity() {
            harness.Switch.Setup(mediator => mediator.GetInfo()).Returns(Info("Powerbox", Writable(3, "Camera", 1).Object, Writable(7, "Dew heater", 1).Object));

            var result = await harness.Observatory.SetSwitchAsync(SwitchTarget.ForIndex(1, "Dew heater"), 1, CancellationToken.None);

            Assert.That(result.Success, Is.True, result.Message);
            VerifyIdentityWrite("Powerbox", 7, Times.Once);
        }

        [Test]
        public async Task ByIndex_WithAnotherName_IsRefusedWithoutWriting() {
            harness.Switch.Setup(mediator => mediator.GetInfo()).Returns(Info("Powerbox", Writable(3, "Camera", 1).Object, Writable(7, "Mount", 1).Object));

            var result = await harness.Observatory.SetSwitchAsync(SwitchTarget.ForIndex(1, "Dew heater"), 1, CancellationToken.None);

            Assert.That(result.Success, Is.False);
            Assert.That(result.Message, Does.Contain("is now \"Mount\""));
            VerifyNoWrite();
        }

        [Test]
        public async Task HubChannel_UsesItsOwnDeviceAndChannelId() {
            var channel = Writable(1234, "Outlet 1", 1);
            var identity = channel.As<ICompositeSwitchIdentity>();

            identity.SetupGet(s => s.DeviceId).Returns("PINS Powerbox");
            identity.SetupGet(s => s.ChannelId).Returns(2);

            harness.Switch.Setup(mediator => mediator.GetInfo()).Returns(Info("NINA.Switch.Hub", channel.Object));

            var byHubId = await harness.Observatory.SetSwitchAsync(SwitchTarget.ForIdentity("NINA.Switch.Hub", 1234), 1, CancellationToken.None);

            Assert.That(byHubId.Success, Is.False, "a hub channel is named by its own identity, as the sequencer records it");
            VerifyNoWrite();

            var result = await harness.Observatory.SetSwitchAsync(SwitchTarget.ForIdentity("PINS Powerbox", 2), 1, CancellationToken.None);

            Assert.That(result.Success, Is.True, result.Message);
            VerifyIdentityWrite("PINS Powerbox", 2, Times.Once);
        }
    }
}
