// SPDX-License-Identifier: MPL-2.0

using System.Diagnostics;
using System.Reflection;
using FluentAssertions;
using Moq;
using NINA.Core.Enum;
using NINA.Equipment.Equipment.MyGuider.Internal;
using NINA.Equipment.Equipment.MyTelescope;
using NINA.Equipment.Interfaces.Mediator;
using NINA.GuideEngine.Core;
using NINA.INDI;
using NINA.INDI.Enums;
using NINA.INDI.Interfaces;
using NINA.Profile.Interfaces;
using NUnit.Framework;

namespace NINA.Test.Equipment.MyGuider.Internal;

/// <summary>
/// The guider waits for the end of every pulse before the next pulse or exposure, so an INDI mount's end of pulse
/// must reach it at once: <see cref="IndiTelescope.IsPulseGuiding"/> is read without the property cache, and
/// <see cref="MountPulseOutput"/> polls it every few ms.
/// </summary>
[TestFixture]
public class MountPulseOutputTests
{
    [Test]
    public void IndiTelescope_reports_the_end_of_a_pulse_without_the_property_cache()
    {
        var telescope = new ConnectedIndiTelescope();
        telescope.Pulsing = true;
        telescope.IsPulseGuiding.Should().BeTrue();

        telescope.Pulsing = false;

        telescope.IsPulseGuiding.Should().BeFalse("a cached value would still say guiding for up to 100 ms");
    }

    [Test]
    public async Task On_an_indi_mount_the_pulse_counts_as_done_right_after_the_driver_reports_its_end()
    {
        const int pulseMs = 100;
        const int driverLateMs = 40;
        var telescope = new ConnectedIndiTelescope();
        var mediator = new Mock<ITelescopeMediator>();
        mediator.Setup(m => m.GetInfo()).Returns(new TelescopeInfo { Connected = true });
        mediator.Setup(m => m.GetDevice()).Returns(telescope);
        var clock = Stopwatch.StartNew();
        long endedAtMs = -1;
        mediator.Setup(m => m.PulseGuide(It.IsAny<GuideDirections>(), It.IsAny<int>())).Callback(() =>
        {
            telescope.Pulsing = true;
            // the driver reports the end of the pulse a little after it
            _ = Task.Delay(pulseMs + driverLateMs).ContinueWith(_ =>
            {
                Interlocked.Exchange(ref endedAtMs, clock.ElapsedMilliseconds);
                telescope.Pulsing = false;
            });
        });

        await new MountPulseOutput(mediator.Object).PulseAsync(GuideDirection.North, pulseMs, CancellationToken.None);
        long doneAtMs = clock.ElapsedMilliseconds;

        long endedAt = Interlocked.Read(ref endedAtMs);
        endedAt.Should().BeGreaterThanOrEqualTo(0, "it waits for the driver");
        // measured from the moment the driver reported the end, so the test's own timer jitter does not count; the old
        // property cache with a 25 ms poll noticed it up to 125 ms late, a 5 ms poll within a timer tick or two
        (doneAtMs - endedAt).Should().BeLessThan(60, "the guider notices the end of the pulse within a few ms");
    }

    [TestCase(null, false)]
    [TestCase("the driver rejected the 300 ms pulse on TELESCOPE_TIMED_GUIDE_NS", true)]
    public async Task A_pulse_the_indi_driver_rejected_throws_once_it_is_over(string? rejection, bool throws)
    {
        var telescope = new ConnectedIndiTelescope { Rejection = rejection };
        var mediator = new Mock<ITelescopeMediator>();
        mediator.Setup(m => m.GetInfo()).Returns(new TelescopeInfo { Connected = true });
        mediator.Setup(m => m.GetDevice()).Returns(telescope);

        Func<Task> pulse = () => new MountPulseOutput(mediator.Object).PulseAsync(GuideDirection.South, 20, CancellationToken.None);

        if (throws)
        {
            (await pulse.Should().ThrowAsync<PulseRejectedException>()).WithMessage(rejection);
        }
        else
        {
            await pulse.Should().NotThrowAsync();
        }
    }

    /// <summary>An INDI mount without a server: its device is a fake whose pulse flag the test sets.</summary>
    private sealed class ConnectedIndiTelescope : IndiTelescope
    {
        private readonly Mock<IINDITelescope> fake = new();

        public ConnectedIndiTelescope()
            : base(new INDIDeviceInfo { Id = "Mount", Name = "Mount", Interface = DeviceInterface.TELESCOPE_INTERFACE }, Mock.Of<IProfileService>())
        {
            fake.SetupGet(d => d.IsPulseGuiding).Returns(() => Pulsing);
            fake.Setup(d => d.TakeRejectedPulse(It.IsAny<GuideDirections>())).Returns(() => Rejection);
            device = fake.Object;
            // connectedExpectation is set only by a real Connect; properties read as defaults without it
            typeof(NINA.Equipment.Equipment.IndiDevice<IINDITelescope>).GetField("connectedExpectation", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(this, true);
        }

        public volatile bool Pulsing;

        public volatile string? Rejection;
    }
}
