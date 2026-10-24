// SPDX-License-Identifier: MPL-2.0

#nullable enable

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NINA.Core.Model;
using NINA.Core.Model.Equipment;
using NINA.Core.Utility;
using NINA.Equipment.Equipment.MyCamera;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Model;
using NINA.GuideEngine.Core;

namespace NINA.Equipment.Equipment.MyGuider.Internal;

/// <summary>
/// The internal guider's camera: the guide camera slot. The slot chooses, connects and configures the camera (and keeps
/// one physical camera out of both slots); this takes the guide frames through it. While the guider is connected it holds
/// the slot's capture block (<see cref="Acquire"/>), so no other capture (e.g. through the API) runs between two guide
/// frames.
/// </summary>
internal sealed class GuideCameraSource(IGuideCameraMediator camera) : ICameraSource, IGainRange
{
    /// <summary>Added to the exposure before a frame times out: download (USB 2 guide cameras, busy Pi) and image decoding.</summary>
    internal static readonly TimeSpan TimeoutMargin = TimeSpan.FromSeconds(30);

    private readonly object blockGate = new();
    private bool holdsBlock;

    private CameraInfo Info => camera.GetInfo();

    /// <summary>
    /// The guide camera's name as calibrations and dark libraries are stored under it: the INDI device name, or
    /// "Name [Id]" for other cameras (identical models differ by their Id). Null when the guide camera is not connected.
    /// </summary>
    public static string? NameOf(IGuideCameraMediator camera)
    {
        var info = camera.GetInfo();
        if (info is not { Connected: true } || string.IsNullOrEmpty(info.DeviceId))
        {
            return null;
        }

        return camera.GetDevice() is IndiCamera ? info.DeviceId : $"{info.Name} [{info.DeviceId}]";
    }

    public string Name => NameOf(camera) ?? "Guide camera";

    /// <summary>The INDI device name when the guide camera is an INDI camera (its ST4 port is addressed by it), otherwise null.</summary>
    public string? IndiDeviceName => Info.Connected && camera.GetDevice() is IndiCamera indi ? indi.Id : null;

    public bool IsConnected => Info.Connected;

    public int SensorWidth => Info.XSize;

    public int SensorHeight => Info.YSize;

    public double PixelSizeUm => Info.PixelSize is var p && double.IsFinite(p) && p > 0 ? p : 0;

    public int MaxBinning => Info.BinningModes is { Count: > 0 } modes ? Math.Max(1, modes.Max(m => (int)m.X)) : 1;

    public ushort MaxAdu => 0;

    public int BitsPerPixel => Info.BitDepth > 0 ? Math.Clamp(Info.BitDepth, 8, 16) : 16;

    public int? GainMin => Info is { CanSetGain: true } i && i.GainMax > i.GainMin ? i.GainMin : null;

    public int? GainMax => Info is { CanSetGain: true } i && i.GainMax > i.GainMin ? i.GainMax : null;

    public int? CurrentGain => Info is { CanGetGain: true } i ? i.Gain : null;

    /// <summary>Takes the slot's capture block for the guider. Throws when something else is using the guide camera.</summary>
    public void Acquire()
    {
        lock (blockGate)
        {
            if (holdsBlock)
            {
                return;
            }

            if (!camera.TryRegisterCaptureBlock(this))
            {
                throw new GuideCameraException("The guide camera is busy with another exposure; try again when it has finished.");
            }

            holdsBlock = true;
        }
    }

    public void Release()
    {
        lock (blockGate)
        {
            if (holdsBlock)
            {
                camera.ReleaseCaptureBlock(this);
                holdsBlock = false;
            }
        }
    }

    public async Task<GuideFrame> CaptureAsync(CaptureRequest request, CancellationToken ct)
    {
        if (!Info.Connected)
        {
            throw new GuideCameraException("The guide camera is not connected.");
        }

        if (!holdsBlock)
        {
            throw new GuideCameraException("The guide camera is not reserved for the guider.");
        }

        double seconds = Math.Max(request.ExposureMs / 1000.0, 0.001);
        short bin = (short)Math.Max(1, request.Binning);
        var sequence = new CaptureSequence(seconds, CaptureSequence.ImageTypes.SNAPSHOT, null, new BinningMode(bin, bin), 1)
        {
            // -1: the gain/offset of the guide camera's settings
            Gain = request.Gain ?? -1,
            Offset = request.Offset ?? -1,
        };

        var started = DateTimeOffset.UtcNow;
        var timeout = TimeSpan.FromSeconds(seconds) + TimeoutMargin;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await camera.Capture(sequence, timeoutCts.Token, NoProgress.Instance).ConfigureAwait(false);
            var exposure = await camera.Download(timeoutCts.Token).ConfigureAwait(false);
            if (exposure is null)
            {
                // an INDI camera answers a cancelled download with no image: that is the stop or the timeout, not a camera fault
                timeoutCts.Token.ThrowIfCancellationRequested();
                throw new GuideCameraException("The guide camera returned no image.");
            }

            var image = await exposure.ToImageData(NoProgress.Instance, timeoutCts.Token).ConfigureAwait(false);
            var pixels = image.Data.FlatArray ?? throw new GuideCameraException("The guide camera returned no pixel data.");
            // a copy: the engine works on the frame while the image data may still be in use elsewhere
            return new GuideFrame(image.Properties.Width, image.Properties.Height, (ushort[])pixels.Clone())
            {
                BitsPerPixel = BitsPerPixel,
                ExposureMs = request.ExposureMs,
                Binning = bin,
                StartTime = started,
            };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            await AbortAsync().ConfigureAwait(false);
            throw new GuideCameraException($"The guide exposure timed out after {timeout.TotalSeconds:F0} s.");
        }
        catch (OperationCanceledException)
        {
            await AbortAsync().ConfigureAwait(false);
            throw;
        }
        catch (Exception ex) when (ex is not GuideCameraException)
        {
            await AbortAsync().ConfigureAwait(false);
            throw new GuideCameraException($"The guide exposure failed: {ex.Message}", ex);
        }
    }

    public Task AbortAsync()
    {
        // only the guider's own exposure: without the block the exposure belongs to someone else
        if (holdsBlock)
        {
            try
            {
                camera.AbortExposure();
            }
            catch (Exception ex)
            {
                Logger.Debug($"InternalGuider: aborting the guide exposure failed: {ex.Message}");
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// The engine reconnects after failed frames. The guide camera belongs to its slot: the guider never reconnects it,
    /// it carries on while the slot is connected and fails otherwise.
    /// </summary>
    public Task ReconnectAsync(CancellationToken ct)
    {
        if (!Info.Connected)
        {
            throw new GuideCameraException("The guide camera is disconnected; connect it again to continue guiding.");
        }

        return Task.CompletedTask;
    }

    private sealed class NoProgress : IProgress<ApplicationStatus>
    {
        public static readonly NoProgress Instance = new();

        public void Report(ApplicationStatus value)
        {
        }
    }
}
