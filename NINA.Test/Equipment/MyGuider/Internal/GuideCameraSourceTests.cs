// SPDX-License-Identifier: MPL-2.0

using FluentAssertions;
using Moq;
using NINA.Core.Model;
using NINA.Equipment.Equipment.MyCamera;
using NINA.Equipment.Equipment.MyGuider.Internal;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Model;
using NINA.GuideEngine.Core;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;
using NINA.INDI;
using NINA.INDI.Enums;
using NINA.Profile.Interfaces;
using NUnit.Framework;

namespace NINA.Test.Equipment.MyGuider.Internal;

/// <summary>The internal guider takes its frames from the guide camera slot.</summary>
[TestFixture]
public class GuideCameraSourceTests
{
    private const int Width = 6;
    private const int Height = 4;

    private Mock<IGuideCameraMediator> mediator = null!;
    private CameraInfo info = null!;
    private CaptureSequence? captured;
    private ushort[] pixels = null!;

    [SetUp]
    public void SetUp()
    {
        info = new CameraInfo { Connected = true, Name = "ToupTek G3M678C", DeviceId = "ToupTek_usb-0547-14ae-4-3", XSize = Width, YSize = Height, BitDepth = 12, PixelSize = 2.0 };
        pixels = Enumerable.Range(0, Width * Height).Select(i => (ushort)(i * 100)).ToArray();
        captured = null;
        mediator = new Mock<IGuideCameraMediator>();
        mediator.Setup(m => m.GetInfo()).Returns(() => info);
        mediator.Setup(m => m.TryRegisterCaptureBlock(It.IsAny<object>())).Returns(true);
        mediator.Setup(m => m.Capture(It.IsAny<CaptureSequence>(), It.IsAny<CancellationToken>(), It.IsAny<IProgress<ApplicationStatus>>()))
            .Callback<CaptureSequence, CancellationToken, IProgress<ApplicationStatus>>((s, _, _) => captured = s)
            .Returns(Task.CompletedTask);
        mediator.Setup(m => m.Download(It.IsAny<CancellationToken>())).ReturnsAsync(() => Exposure(pixels));
    }

    private static IExposureData Exposure(ushort[] data)
    {
        var image = new ImageDataFactoryTestUtility().ImageDataFactory
            .CreateBaseImageData(new ImageArray(data), Width, Height, 16, false, new ImageMetaData());
        var exposure = new Mock<IExposureData>();
        exposure.Setup(e => e.ToImageData(It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>())).ReturnsAsync(image);
        return exposure.Object;
    }

    private GuideCameraSource Acquired()
    {
        var source = new GuideCameraSource(mediator.Object);
        source.Acquire();
        return source;
    }

    [Test]
    public async Task Capture_takes_the_frame_through_the_slot_with_the_requested_exposure_binning_gain_and_offset()
    {
        var source = Acquired();

        var frame = await source.CaptureAsync(new CaptureRequest(2500, Binning: 2, Gain: 120, Offset: 30), CancellationToken.None);

        captured.Should().NotBeNull();
        captured!.ExposureTime.Should().Be(2.5);
        captured.Binning.X.Should().Be(2);
        captured.Binning.Y.Should().Be(2);
        captured.Gain.Should().Be(120);
        captured.Offset.Should().Be(30);
        frame.Width.Should().Be(Width);
        frame.Height.Should().Be(Height);
        frame.Pixels.Should().Equal(pixels);
        frame.Pixels.Should().NotBeSameAs(pixels, "the engine gets its own copy");
        frame.BitsPerPixel.Should().Be(12);
        frame.ExposureMs.Should().Be(2500);
        frame.Binning.Should().Be(2);
    }

    [Test]
    public async Task Capture_without_gain_and_offset_uses_the_guide_cameras_settings()
    {
        var source = Acquired();

        await source.CaptureAsync(new CaptureRequest(1000), CancellationToken.None);

        captured!.Gain.Should().Be(-1);
        captured.Offset.Should().Be(-1);
    }

    [Test]
    public void Acquire_when_something_else_holds_the_guide_camera_fails()
    {
        mediator.Setup(m => m.TryRegisterCaptureBlock(It.IsAny<object>())).Returns(false);
        var source = new GuideCameraSource(mediator.Object);

        source.Invoking(s => s.Acquire()).Should().Throw<GuideCameraException>().WithMessage("*busy*");
    }

    [Test]
    public async Task Capture_needs_the_capture_block()
    {
        var source = new GuideCameraSource(mediator.Object);

        await source.Invoking(s => s.CaptureAsync(new CaptureRequest(1000), CancellationToken.None)).Should().ThrowAsync<GuideCameraException>();
        mediator.Verify(m => m.Capture(It.IsAny<CaptureSequence>(), It.IsAny<CancellationToken>(), It.IsAny<IProgress<ApplicationStatus>>()), Times.Never);
    }

    [Test]
    public void Acquire_and_release_take_and_give_back_the_block_once()
    {
        var source = Acquired();
        source.Acquire();

        source.Release();
        source.Release();

        mediator.Verify(m => m.TryRegisterCaptureBlock(source), Times.Once);
        mediator.Verify(m => m.ReleaseCaptureBlock(source), Times.Once);
    }

    [Test]
    public async Task A_missing_image_is_a_camera_error()
    {
        mediator.Setup(m => m.Download(It.IsAny<CancellationToken>())).ReturnsAsync((IExposureData)null!);
        var source = Acquired();

        await source.Invoking(s => s.CaptureAsync(new CaptureRequest(1000), CancellationToken.None)).Should().ThrowAsync<GuideCameraException>().WithMessage("*no image*");
    }

    [Test]
    public async Task A_download_cancelled_by_the_stop_is_the_stop_not_a_camera_error()
    {
        // an INDI camera answers a cancelled download with no image
        using var stop = new CancellationTokenSource();
        mediator.Setup(m => m.Download(It.IsAny<CancellationToken>())).ReturnsAsync(() =>
        {
            stop.Cancel();
            return (IExposureData)null!;
        });
        var source = Acquired();

        await source.Invoking(s => s.CaptureAsync(new CaptureRequest(1000), stop.Token)).Should().ThrowAsync<OperationCanceledException>();
    }

    [Test]
    public async Task A_disconnected_guide_camera_is_a_camera_error()
    {
        var source = Acquired();
        info.Connected = false;

        await source.Invoking(s => s.CaptureAsync(new CaptureRequest(1000), CancellationToken.None)).Should().ThrowAsync<GuideCameraException>();
        await source.Invoking(s => s.ReconnectAsync(CancellationToken.None)).Should().ThrowAsync<GuideCameraException>("the guider never reconnects the slot itself");
    }

    [Test]
    public async Task Reconnect_carries_on_while_the_guide_camera_is_connected()
    {
        var source = Acquired();

        await source.ReconnectAsync(CancellationToken.None);

        mediator.Verify(m => m.Connect(), Times.Never);
    }

    [Test]
    public async Task Abort_stops_only_the_guiders_own_exposure()
    {
        var source = new GuideCameraSource(mediator.Object);
        await source.AbortAsync();
        mediator.Verify(m => m.AbortExposure(), Times.Never, "without the block the exposure belongs to someone else");

        source.Acquire();
        await source.AbortAsync();
        mediator.Verify(m => m.AbortExposure(), Times.Once);
    }

    [Test]
    public void The_name_keeps_the_plugins_format_so_calibrations_and_darks_still_match()
    {
        // SDK cameras: "Name [Id]", identical models differ by their Id
        mediator.Setup(m => m.GetDevice()).Returns(Mock.Of<ICamera>());
        GuideCameraSource.NameOf(mediator.Object).Should().Be("ToupTek G3M678C [ToupTek_usb-0547-14ae-4-3]");

        // INDI cameras: the device name, which also addresses the camera's ST4 port
        var indi = new IndiCamera(new INDIDeviceInfo { Id = "ToupTek G3M678C", Name = "ToupTek G3M678C", Interface = DeviceInterface.CCD_INTERFACE },
            Mock.Of<IProfileService>(), Mock.Of<IExposureDataFactory>(), Mock.Of<IImageDataFactory>());
        info.DeviceId = indi.Id;
        mediator.Setup(m => m.GetDevice()).Returns(indi);
        GuideCameraSource.NameOf(mediator.Object).Should().Be("ToupTek G3M678C");
        new GuideCameraSource(mediator.Object).IndiDeviceName.Should().Be("ToupTek G3M678C");

        info.Connected = false;
        GuideCameraSource.NameOf(mediator.Object).Should().BeNull();
    }
}
