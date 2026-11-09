// SPDX-License-Identifier: MPL-2.0

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NINA.Core.Enum;
using NINA.Core.Utility;
using NINA.Equipment.Equipment.MyCamera;
using NINA.Equipment.Equipment.MyTelescope;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.GuideEngine.Calibration;
using NINA.GuideEngine.Core;
using NinaPierSide = NINA.Core.Enum.PierSide;
using PierSide = NINA.GuideEngine.Core.PierSide;

namespace NINA.Equipment.Equipment.MyGuider.Internal;

/// <summary>A rotator's mechanical angle (degrees) and whether its driver reports it moving.</summary>
internal readonly record struct RotatorReading(double MechanicalDeg, bool IsMoving);

/// <summary>Mount state from NINA's telescope mediator.</summary>
/// <param name="clock">Time for the computed sidereal time and the log window (tests pass a fixed one).</param>
/// <param name="rotator">The rotator's state, null without a connected rotator or a readable angle.</param>
/// <param name="rotatorSettle">
/// How long after its angle last changed the rotator still counts as turning. NINA polls the rotator, so a driver that
/// reports the end of a move late, or not at all, would otherwise look stopped between two polls.
/// </param>
internal sealed class NinaMountState(ITelescopeMediator telescope, Func<bool>? pauseWhenSlewing = null, Func<bool>? pauseWhenTrackingOff = null,
    bool mountOptional = false, IClock? clock = null, Func<RotatorReading?>? rotator = null, TimeSpan? rotatorSettle = null) : IMountState
{
    public const double SiderealArcsecPerSec = 15.041;

    /// <summary>The largest change of the rotator angle that does not count as a move (degrees).</summary>
    public const double RotatorChangeDeg = 0.05;

    /// <summary>
    /// An angle change seen across a longer gap between two reads does not count as turning: no guide frames were taken
    /// (e.g. while NINA centered and rotated with guiding stopped), so the move is over unless the driver says otherwise.
    /// Well above the frame spacing of long guide exposures, which see every change across a gap of several seconds.
    /// </summary>
    public static readonly TimeSpan RotatorReadGapLimit = TimeSpan.FromSeconds(60);

    /// <summary>Most slewing/tracking changes logged per <see cref="LogWindow"/>: enough to show a pattern, without flooding the log.</summary>
    private const int MaxLoggedChanges = 30;

    /// <summary>Window of <see cref="MaxLoggedChanges"/>: a driver that flaps all night still shows up again every few minutes.</summary>
    private static readonly TimeSpan LogWindow = TimeSpan.FromMinutes(10);

    private readonly IClock clock = clock ?? SystemClock.Instance;
    private readonly TimeSpan rotatorSettle = rotatorSettle ?? TimeSpan.FromSeconds(4);
    private readonly object rotatorGate = new();

    // the angle at the last change that counted (so a slow turn adds up), when it changed, and when the angle was last read
    private double? rotatorAnchorAngle;
    private DateTimeOffset rotatorChangedAt = DateTimeOffset.MinValue;
    private DateTimeOffset? rotatorReadAt;
    private bool? lastSlewing;
    private bool? lastTracking;
    private DateTimeOffset logWindowStart = DateTimeOffset.MinValue;
    private int loggedChanges;

    public MountSnapshot GetSnapshot()
    {
        var info = telescope.GetInfo();
        if (info is { Connected: true })
        {
            LogStateChange(info);
        }

        var (rotatorAngle, rotatorMoving, rotatorTurning) = ReadRotator();
        if (info is null || !info.Connected)
        {
            // ST4 guiding works without a mount connection; pier side/declination are then unknown
            return mountOptional
                ? new MountSnapshot
                {
                    IsConnected = true,
                    IsTracking = true,
                    PierSide = PierSide.Unknown,
                    RotatorAngleDeg = rotatorAngle,
                    RotatorMoving = rotatorMoving,
                    RotatorTurning = rotatorTurning,
                }
                : new MountSnapshot
                {
                    IsConnected = false,
                    RotatorAngleDeg = rotatorAngle,
                    RotatorMoving = rotatorMoving,
                    RotatorTurning = rotatorTurning,
                };
        }

        static double? Rate(double arcsecPerSec) => double.IsNaN(arcsecPerSec) || arcsecPerSec <= 0 ? null : arcsecPerSec / SiderealArcsecPerSec;

        return new MountSnapshot
        {
            IsConnected = true,
            DeclinationDeg = double.IsNaN(info.Declination) ? null : info.Declination,
            RightAscensionHours = double.IsNaN(info.RightAscension) ? null : info.RightAscension,
            SiderealTimeHours = SiderealTime(info),
            PierSide = info.SideOfPier switch
            {
                NinaPierSide.pierEast => PierSide.East,
                NinaPierSide.pierWest => PierSide.West,
                _ => PierSide.Unknown,
            },
            IsSlewing = info.Slewing && (pauseWhenSlewing?.Invoke() ?? true),
            IsParked = info.AtPark,
            IsHoming = false,
            IsTracking = info.TrackingEnabled || !(pauseWhenTrackingOff?.Invoke() ?? true),
            GuideRateRa = Rate(info.GuideRateRightAscensionArcsecPerSec),
            GuideRateDec = Rate(info.GuideRateDeclinationArcsecPerSec),
            RotatorAngleDeg = rotatorAngle,
            RotatorMoving = rotatorMoving,
            RotatorTurning = rotatorTurning,
        };
    }

    // the angle, whether the driver reports the rotator moving, and whether the angle changed within the settle time
    private (double? Angle, bool Moving, bool Turning) ReadRotator()
    {
        if (rotator?.Invoke() is not { } r)
        {
            return (null, false, false);
        }

        var now = clock.UtcNow;
        lock (rotatorGate)
        {
            if (rotatorAnchorAngle is not { } anchor)
            {
                rotatorAnchorAngle = r.MechanicalDeg;
            }
            else
            {
                double change = MountTransform.Norm(r.MechanicalDeg - anchor, -180.0, 180.0);
                if (Math.Abs(change) > RotatorChangeDeg)
                {
                    rotatorAnchorAngle = r.MechanicalDeg;

                    // seen between two guide frames it may still be turning, also with long guide exposures; after a long
                    // gap (no guide frames, e.g. while NINA centers and rotates with guiding stopped) the move is over
                    // unless the driver says so
                    var gapLimit = rotatorSettle > RotatorReadGapLimit ? rotatorSettle : RotatorReadGapLimit;
                    if (rotatorReadAt is { } readAt && now - readAt <= gapLimit)
                    {
                        rotatorChangedAt = now;
                    }
                }
            }

            rotatorReadAt = now;
            return (r.MechanicalDeg, r.IsMoving, now - rotatorChangedAt < rotatorSettle);
        }
    }

    // the mount's own sidereal time (its RA follows its clock); the computer's for a driver that doesn't report one (INDI: -1)
    private double? SiderealTime(NINA.Equipment.Equipment.MyTelescope.TelescopeInfo info) =>
        info.SiderealTime is >= 0 and < 24 ? info.SiderealTime
        : double.IsNaN(info.SiteLongitude) ? null : LocalMeanSiderealTime(clock.UtcNow.UtcDateTime, info.SiteLongitude);

    /// <summary>
    /// Local mean sidereal time in hours from UTC and the east longitude: USNO's approximate GMST
    /// (https://aa.usno.navy.mil/faq/GAST), within 0.1 s of the IAU 2006 GMST this century. The worm phase only needs it
    /// consistent from night to night; UTC for UT1 adds under a second.
    /// </summary>
    internal static double LocalMeanSiderealTime(DateTime utc, double longitudeDeg)
    {
        double days = (utc - new DateTime(2000, 1, 1, 12, 0, 0, DateTimeKind.Utc)).TotalDays;
        double hours = (18.697374558 + 24.06570982441908 * days + longitudeDeg / 15.0) % 24.0;
        return hours < 0 ? hours + 24.0 : hours;
    }

    /// <summary>
    /// Logs every change of the driver's slewing/tracking flags (at most <see cref="MaxLoggedChanges"/> per
    /// <see cref="LogWindow"/>): drivers that report a slew around guide pulses pause guiding, and the log shows what the
    /// driver reported.
    /// </summary>
    private void LogStateChange(NINA.Equipment.Equipment.MyTelescope.TelescopeInfo info)
    {
        if (lastSlewing == info.Slewing && lastTracking == info.TrackingEnabled)
        {
            return;
        }

        bool first = lastSlewing is null;
        lastSlewing = info.Slewing;
        lastTracking = info.TrackingEnabled;
        if (first)
        {
            return;
        }

        var now = clock.UtcNow;
        if (now - logWindowStart > LogWindow)
        {
            logWindowStart = now;
            loggedChanges = 0;
        }

        if (++loggedChanges > MaxLoggedChanges)
        {
            if (loggedChanges == MaxLoggedChanges + 1)
            {
                Logger.Info($"InternalGuider: further mount slewing/tracking changes are not logged for {LogWindow.TotalMinutes:F0} minutes");
            }

            return;
        }

        Logger.Info(FormattableString.Invariant(
            $"InternalGuider: mount reports slewing={info.Slewing} tracking={info.TrackingEnabled} pulseGuiding={info.IsPulseGuiding} parked={info.AtPark} RA={info.RightAscension:F5}h Dec={info.Declination:F4}°"));
    }
}

/// <summary>
/// Pulse guiding through the connected NINA mount (any driver type). NINA's PulseGuide returns immediately for INDI
/// mounts, so completion is awaited here: the nominal duration, then the driver's IsPulseGuiding flag. A pulse an INDI
/// driver rejected throws <see cref="PulseRejectedException"/> once it is over.
/// </summary>
internal sealed class MountPulseOutput(ITelescopeMediator telescope) : IPulseOutput
{
    /// <summary>
    /// Shortest wait for the driver's pulse-guiding flag after the nominal duration (otherwise half the pulse): drivers
    /// report the end of a pulse late, and a bounded wait keeps a stuck flag from stalling the guide loop.
    /// </summary>
    private const int MinCompletionWaitMs = 1000;

    /// <summary>How often the flag is read while waiting: short against the pulses, so the next frame starts soon after.</summary>
    private static readonly TimeSpan BusyPollInterval = TimeSpan.FromMilliseconds(25);

    /// <summary>
    /// The poll for an INDI or native OnStepX mount, whose flag is pins' in-memory pulse tracker: reading it costs
    /// nothing. An ASCOM or Alpaca mount keeps <see cref="BusyPollInterval"/>, as each read can be a call to the driver
    /// or an HTTP request.
    /// </summary>
    internal static readonly TimeSpan IndiBusyPollInterval = TimeSpan.FromMilliseconds(5);

    public string Name => "Mount";

    public bool IsConnected => telescope.GetInfo()?.Connected == true;

    /// <summary>
    /// Only pins' native OnStepX driver: OnStepX times each axis' pulse separately (Guide.cpp) and the driver waits for
    /// both to end. INDI and ASCOM drivers vary, so they keep one pulse after the other.
    /// </summary>
    public bool SupportsSimultaneousPulses => telescope.GetDevice() is OnStepXTelescope;

    public async Task PulseAsync(GuideDirection direction, int durationMs, CancellationToken ct)
    {
        if (durationMs <= 0)
        {
            return;
        }

        var info = telescope.GetInfo();
        if (info is null || !info.Connected)
        {
            throw new InvalidOperationException("mount not connected");
        }

        if (info.AtPark)
        {
            throw new InvalidOperationException("mount is parked");
        }

        if (!info.CanPulseGuide)
        {
            // any driver type: NINA's PulseGuide only warns and sends nothing, and the pulse counted as sent
            throw new InvalidOperationException("the mount cannot pulse guide");
        }

        var dir = direction switch
        {
            GuideDirection.North => GuideDirections.guideNorth,
            GuideDirection.South => GuideDirections.guideSouth,
            GuideDirection.East => GuideDirections.guideEast,
            _ => GuideDirections.guideWest,
        };

        var started = DateTime.UtcNow;
        if (telescope.GetDevice() is IndiTelescope indiMount)
        {
            // a driver error reaches the guider (IndiTelescope.PulseGuide only logged it and notified for every pulse)
            await Task.Run(() => indiMount.PulseGuideForGuider(dir, durationMs), ct).ConfigureAwait(false);
        }
        else
        {
            await Task.Run(() => telescope.PulseGuide(dir, durationMs), ct).ConfigureAwait(false);
        }

        // wait for the pulse to complete: nominal duration, then the driver's busy flag (bounded)
        var remaining = TimeSpan.FromMilliseconds(durationMs) - (DateTime.UtcNow - started);
        if (remaining > TimeSpan.Zero)
        {
            await Task.Delay(remaining, ct).ConfigureAwait(false);
        }

        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(Math.Max(MinCompletionWaitMs, durationMs / 2));
        var device = telescope.GetDevice() as ITelescope;
        var pollInterval = device is IndiTelescope or OnStepXTelescope ? IndiBusyPollInterval : BusyPollInterval;
        while (device is not null && DateTime.UtcNow < deadline)
        {
            bool busy;
            try
            {
                busy = device.IsPulseGuiding;
            }
            catch
            {
                break;
            }

            if (!busy)
            {
                break;
            }

            await Task.Delay(pollInterval, ct).ConfigureAwait(false);
        }

        if (device is IndiTelescope indi && indi.TakeRejectedPulse(dir) is { } reason)
        {
            throw new PulseRejectedException(reason);
        }
    }
}

/// <summary>
/// Pulse guiding through the guide camera's ST4 port (INDI TELESCOPE_TIMED_GUIDE_* on the camera device), awaited like
/// <see cref="MountPulseOutput"/>: the nominal duration, then the driver's end of the pulse (bounded). A pulse the camera
/// rejected (Alert) throws an output failure, which the guider counts towards its limit: a port that cannot guide stops
/// guiding instead of "correcting" into nothing.
/// </summary>
internal sealed class CameraSt4PulseOutput(Func<IndiCamera?> guideCamera) : IPulseOutput
{
    /// <summary>Shortest wait for the end of a pulse after its nominal duration (otherwise half the pulse).</summary>
    private const int MinCompletionWaitMs = 1000;

    public string Name => "Camera ST4";

    public bool IsConnected => guideCamera() is not null;

    public bool SupportsSimultaneousPulses => false;

    public async Task PulseAsync(GuideDirection direction, int durationMs, CancellationToken ct)
    {
        if (durationMs <= 0)
        {
            return;
        }

        var camera = guideCamera() ?? throw new InvalidOperationException("guide camera not connected");
        var dir = direction switch
        {
            GuideDirection.North => GuideDirections.guideNorth,
            GuideDirection.South => GuideDirections.guideSouth,
            GuideDirection.East => GuideDirections.guideEast,
            _ => GuideDirections.guideWest,
        };

        var started = DateTime.UtcNow;
        await Task.Run(() => camera.St4PulseGuide(dir, durationMs), ct).ConfigureAwait(false);

        var remaining = TimeSpan.FromMilliseconds(durationMs) - (DateTime.UtcNow - started);
        if (remaining > TimeSpan.Zero)
        {
            await Task.Delay(remaining, ct).ConfigureAwait(false);
        }

        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(Math.Max(MinCompletionWaitMs, durationMs / 2));
        while (camera.IsSt4PulseGuiding && DateTime.UtcNow < deadline)
        {
            await Task.Delay(MountPulseOutput.IndiBusyPollInterval, ct).ConfigureAwait(false);
        }

        if (camera.TakeRejectedSt4Pulse(dir) is { } reason)
        {
            throw new InvalidOperationException($"ST4 pulse failed: {reason}");
        }

        Logger.Trace($"InternalGuider: ST4 {direction} {durationMs} ms");
    }
}
