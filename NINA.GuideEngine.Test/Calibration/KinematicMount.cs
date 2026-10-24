// SPDX-License-Identifier: MPL-2.0

using NINA.GuideEngine.Calibration;
using NINA.GuideEngine.Core;

namespace NINA.GuideEngine.Test.Calibration;

/// <summary>
/// Simple kinematic star/mount model without images: the star moves by pulse × rate along per-axis camera vectors.
/// East pulses move the star along <see cref="EastAngleDeg"/>, North pulses along <see cref="NorthAngleDeg"/>.
/// </summary>
internal sealed class KinematicMount
{
    private Random rng = new(1);
    private GuideDirection lastDecDirection = GuideDirection.South;
    private double decBacklashRemaining;
    private double raHours = 5.0;
    private double decCoordDeg;
    private bool coordsInitialised;

    public double EastAngleDeg { get; set; } = 30.0;

    public double NorthAngleDeg { get; set; } = 120.0;

    /// <summary>RA guide rate, × sidereal.</summary>
    public double GuideRateRa { get; set; } = 0.5;

    public double GuideRateDec { get; set; } = 0.5;

    public bool ReportGuideRates { get; set; } = true;

    public bool ReportCoordinates { get; set; } = true;

    public double PixelScale { get; set; } = 2.0;

    public double DeclinationDeg { get; set; }

    /// <summary>Dec backlash, ms of pulse absorbed after a direction reversal.</summary>
    public double DecBacklashMs { get; set; }

    public double NoisePx { get; set; }

    public int Seed
    {
        set => rng = new Random(value);
    }

    public double WestEfficiency { get; set; } = 1.0;

    public double EastEfficiency { get; set; } = 1.0;

    public double DecEfficiency { get; set; } = 1.0;

    /// <summary>West pulse increases RA coordinate (reversed RA).</summary>
    public bool OddRaParity { get; set; }

    /// <summary>North pulse decreases Dec coordinate.</summary>
    public bool OddDecParity { get; set; }

    public PierSide PierSide { get; set; } = PierSide.East;

    public double? RotatorAngleDeg { get; set; }

    public GuidePoint Position { get; set; } = new(500, 400);

    public List<PulseCommand> Pulses { get; } = [];

    public double RaRatePxPerMs => GuideRateRa * 15.0 * Math.Cos(DeclinationDeg * Math.PI / 180.0) / PixelScale / 1000.0;

    public double DecRatePxPerMs => GuideRateDec * 15.0 / PixelScale / 1000.0;

    public double EastAngle => EastAngleDeg * Math.PI / 180.0;

    public double NorthAngle => NorthAngleDeg * Math.PI / 180.0;

    public void Apply(PulseCommand p)
    {
        EnsureCoords();
        Pulses.Add(p);
        switch (p.Direction)
        {
            case GuideDirection.East:
                Move(EastAngle, p.DurationMs * RaRatePxPerMs * EastEfficiency);
                raHours += (OddRaParity ? -1 : 1) * GuideRateRa * p.DurationMs / 1000.0 / 3600.0 * EastEfficiency;
                break;
            case GuideDirection.West:
                Move(EastAngle, -p.DurationMs * RaRatePxPerMs * WestEfficiency);
                raHours -= (OddRaParity ? -1 : 1) * GuideRateRa * p.DurationMs / 1000.0 / 3600.0 * WestEfficiency;
                break;
            default:
            {
                if (p.Direction != lastDecDirection)
                {
                    decBacklashRemaining = DecBacklashMs;
                    lastDecDirection = p.Direction;
                }

                double effective = Math.Max(0.0, p.DurationMs - decBacklashRemaining) * DecEfficiency;
                decBacklashRemaining = Math.Max(0.0, decBacklashRemaining - p.DurationMs);
                double sign = p.Direction == GuideDirection.North ? 1.0 : -1.0;
                Move(NorthAngle, sign * effective * DecRatePxPerMs);
                decCoordDeg += sign * (OddDecParity ? -1 : 1) * GuideRateDec * 15.0 * effective / 1000.0 / 3600.0;
                break;
            }
        }
    }

    public GuidePoint Observe() =>
        NoisePx > 0 ? new GuidePoint(Position.X + Gauss() * NoisePx, Position.Y + Gauss() * NoisePx) : Position;

    public MountSnapshot Snapshot()
    {
        EnsureCoords();
        return new MountSnapshot
        {
            IsConnected = true,
            DeclinationDeg = ReportCoordinates ? decCoordDeg : null,
            RightAscensionHours = ReportCoordinates ? raHours : null,
            PierSide = PierSide,
            GuideRateRa = ReportGuideRates ? GuideRateRa : null,
            GuideRateDec = ReportGuideRates ? GuideRateDec : null,
            RotatorAngleDeg = RotatorAngleDeg,
        };
    }

    public double Gauss()
    {
        double u1 = 1.0 - rng.NextDouble();
        double u2 = rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }

    private void EnsureCoords()
    {
        if (!coordsInitialised)
        {
            decCoordDeg = DeclinationDeg;
            coordsInitialised = true;
        }
    }

    private void Move(double angle, double dist) =>
        Position = new GuidePoint(Position.X + dist * Math.Cos(angle), Position.Y + dist * Math.Sin(angle));
}

/// <summary>Drives a <see cref="CalibrationProcess"/> against a <see cref="KinematicMount"/>.</summary>
internal static class CalibrationRunner
{
    public static CalibrationContext Context(KinematicMount m, CalibrationData? previous = null, int binning = 1)
    {
        // choose optics that yield the model's pixel scale: scale = 206.265 * px * bin / fl
        double pixel = 3.8;
        double fl = 206.265 * pixel * binning / m.PixelScale;
        return new CalibrationContext
        {
            Optics = new GuideOptics(fl, pixel, binning),
            PreviousCalibration = previous,
            Clock = new FixedClock(),
        };
    }

    public static (CalibrationProcess Process, List<CalibrationUpdate> Updates) Run(KinematicMount m,
        CalibrationSettings? settings = null, CalibrationContext? context = null, Func<int, bool>? lostFrame = null,
        int maxFrames = 2000)
    {
        var p = new CalibrationProcess(settings ?? new CalibrationSettings(), context ?? Context(m));
        var updates = new List<CalibrationUpdate> { p.Begin(m.Observe(), m.Snapshot()) };
        if (updates[0].IsFailed)
        {
            return (p, updates);
        }

        for (int frame = 0; frame < maxFrames; frame++)
        {
            GuidePoint pos = lostFrame?.Invoke(frame) == true ? GuidePoint.Invalid : m.Observe();
            CalibrationUpdate u = p.Update(pos, m.Snapshot());
            updates.Add(u);
            foreach (PulseCommand pulse in u.Pulses)
            {
                m.Apply(pulse);
            }

            if (u.IsFailed || u.Result is not null)
            {
                return (p, updates);
            }
        }

        throw new InvalidOperationException("calibration did not terminate");
    }

    public static double AngleDiffDeg(double a, double b) => Math.Abs(MountTransform.Degrees(MountTransform.NormAngle(a - b)));
}

internal sealed class FixedClock : IClock
{
    public DateTimeOffset UtcNow { get; set; } = new(2026, 9, 23, 22, 0, 0, TimeSpan.Zero);

    public long ElapsedMs => 0;

    public Task Delay(TimeSpan delay, CancellationToken ct) => Task.CompletedTask;
}
