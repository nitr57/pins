// SPDX-License-Identifier: MPL-2.0

namespace NINA.GuideEngine.Core;

/// <summary>Capture request for a guide frame.</summary>
public sealed record CaptureRequest(double ExposureMs, int Binning = 1, IntRect Subframe = default, int? Gain = null, int? Offset = null);

/// <summary>Guide camera abstraction. Implementations must be safe to call from the guider loop task.</summary>
public interface ICameraSource
{
    string Name { get; }

    bool IsConnected { get; }

    int SensorWidth { get; }

    int SensorHeight { get; }

    /// <summary>Unbinned pixel size in micrometres.</summary>
    double PixelSizeUm { get; }

    int MaxBinning { get; }

    /// <summary>Known saturation ADU (0 = unknown).</summary>
    ushort MaxAdu { get; }

    int BitsPerPixel { get; }

    /// <summary>Exposes and returns the frame. Throws <see cref="GuideCameraException"/> on failure/timeout.</summary>
    Task<GuideFrame> CaptureAsync(CaptureRequest request, CancellationToken ct);

    Task AbortAsync();

    /// <summary>Attempt to reconnect after a failure.</summary>
    Task ReconnectAsync(CancellationToken ct);
}

/// <summary>
/// Optional capability of an <see cref="ICameraSource"/>: the camera's gain range and current gain, for gain
/// selection in UIs and the settings sweep. Values are in the camera's native gain units; null when unknown.
/// </summary>
public interface IGainRange
{
    int? GainMin { get; }

    int? GainMax { get; }

    /// <summary>Gain the camera currently uses (e.g. the driver value or the last requested gain).</summary>
    int? CurrentGain { get; }
}

public sealed class GuideCameraException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Where guide corrections are sent (mount pulse guide, camera ST4, guide port...).</summary>
public interface IPulseOutput
{
    string Name { get; }

    bool IsConnected { get; }

    /// <summary>True when RA and Dec pulses may run concurrently.</summary>
    bool SupportsSimultaneousPulses { get; }

    /// <summary>
    /// Issues a pulse and completes when the pulse has finished. Throws <see cref="PulseRejectedException"/> when the
    /// mount reports that it did not carry the pulse out.
    /// </summary>
    Task PulseAsync(GuideDirection direction, int durationMs, CancellationToken ct);
}

/// <summary>
/// The output sent the pulse, but the mount reported that it did not carry it out (e.g. an INDI driver's Alert). Unlike a
/// failed output it says nothing about the connection: the guider treats the pulse as not sent and keeps guiding.
/// </summary>
public sealed class PulseRejectedException(string message) : Exception(message);

/// <summary>Snapshot of mount state relevant to guiding.</summary>
public sealed record MountSnapshot
{
    public bool IsConnected { get; init; }

    /// <summary>Declination in degrees, null when unknown.</summary>
    public double? DeclinationDeg { get; init; }

    /// <summary>Right ascension in hours, null when unknown.</summary>
    public double? RightAscensionHours { get; init; }

    /// <summary>
    /// Local sidereal time in hours as the mount reports it (its own clock and location, so that it matches its right
    /// ascension), null when unknown.
    /// </summary>
    public double? SiderealTimeHours { get; init; }

    public PierSide PierSide { get; init; } = PierSide.Unknown;

    public bool IsSlewing { get; init; }

    public bool IsParked { get; init; }

    public bool IsHoming { get; init; }

    public bool IsTracking { get; init; } = true;

    /// <summary>Guide rate as a multiple of sidereal (e.g. 0.5), null when unknown.</summary>
    public double? GuideRateRa { get; init; }

    public double? GuideRateDec { get; init; }

    /// <summary>Rotator sky angle in degrees if a rotator is present.</summary>
    public double? RotatorAngleDeg { get; init; }

    public bool IsMoving => IsSlewing || IsHoming;
}

public interface IMountState
{
    MountSnapshot GetSnapshot();
}
