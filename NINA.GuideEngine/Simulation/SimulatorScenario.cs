// SPDX-License-Identifier: MPL-2.0

namespace NINA.GuideEngine.Simulation;

/// <summary>Complete, immutable simulator configuration. Use <c>with</c> to derive variants.</summary>
public sealed record SimulatorScenario
{
    public string Name { get; init; } = "Default";

    public MountSimConfig Mount { get; init; } = new();

    public SkySimConfig Sky { get; init; } = new();

    public CameraSimConfig Camera { get; init; } = new();

    /// <summary>A well-behaved mount: small PE, slight polar misalignment, average seeing.</summary>
    public static SimulatorScenario GoodMount { get; } = new()
    {
        Name = nameof(GoodMount),
        Mount = new MountSimConfig
        {
            PeriodicError = [new PeriodicErrorTerm(2.0, 480.0), new PeriodicErrorTerm(0.5, 240.0, 1.0)],
            DecDriftArcsecPerMin = 0.3,
            RaDriftArcsecPerMin = 0.1,
            RandomWalkArcsecPerSqrtSec = 0.02,
        },
        Sky = new SkySimConfig { SeeingJitterArcsec = 0.3 },
    };

    /// <summary>Large periodic error with harmonics (cheap mount, no PEC).</summary>
    public static SimulatorScenario PoorPeriodicError { get; } = GoodMount with
    {
        Name = nameof(PoorPeriodicError),
        Mount = GoodMount.Mount with
        {
            PeriodicError =
            [
                new PeriodicErrorTerm(15.0, 480.0),
                new PeriodicErrorTerm(5.0, 240.0, 0.7),
                new PeriodicErrorTerm(2.0, 160.0, 2.1),
            ],
        },
    };

    /// <summary>Poor seeing: large image motion and bloated stars.</summary>
    public static SimulatorScenario HighSeeing { get; } = GoodMount with
    {
        Name = nameof(HighSeeing),
        Sky = GoodMount.Sky with { SeeingJitterArcsec = 1.5, FwhmArcsec = 5.5, SeeingCommonFraction = 0.3 },
    };

    /// <summary>Significant Dec backlash plus a Dec drift that forces reversals.</summary>
    public static SimulatorScenario Backlash { get; } = GoodMount with
    {
        Name = nameof(Backlash),
        Mount = GoodMount.Mount with { DecBacklashArcsec = 12.0, DecDriftArcsecPerMin = 1.0 },
    };

    /// <summary>Passing clouds: dimming, then a total loss of the stars for a while.</summary>
    public static SimulatorScenario Clouds { get; } = GoodMount with
    {
        Name = nameof(Clouds),
        Sky = GoodMount.Sky with
        {
            Transparency =
            [
                new TransparencyWindow(60.0, 120.0, 0.3, 10.0),
                new TransparencyWindow(200.0, 240.0, 0.0, 5.0),
                new TransparencyWindow(300.0, 330.0, 0.6, 5.0),
            ],
        },
    };

    /// <summary>Warm, uncooled sensor with many hot and cold pixels.</summary>
    public static SimulatorScenario HotPixelHeavy { get; } = GoodMount with
    {
        Name = nameof(HotPixelHeavy),
        Camera = GoodMount.Camera with { HotPixelCount = 600, ColdPixelCount = 150, DarkCurrentElectronsPerSec = 2.0 },
    };

    /// <summary>
    /// A typical strain-wave (harmonic drive) mount, AM5 or NYX-101 class: ±18″ at 430.8 s (twice per turn of a wave
    /// generator of 861.6 s) with two harmonics, a curve that changes over a few cycles, short components at 2.9 s and
    /// 62 s, guide rate 0.9×, no Dec backlash. Variant B of docs/notes/SHORT-EXPOSURES.md §4.1; the spreads of amplitude
    /// and phase are guesses, since nobody has published them.
    /// </summary>
    public static SimulatorScenario HarmonicDrive { get; } = HarmonicDriveBase(nameof(HarmonicDrive), 430.8,
        amplitudeChange: 0.18, amplitudeCycles: 10, phaseChangeDeg: 10, phaseCycles: 2,
        [
            new PeriodicErrorTerm(18.0, 430.8),
            new PeriodicErrorTerm(2.0, 215.4, 1.3),
            new PeriodicErrorTerm(2.0, 143.6, 2.6),
        ],
        [new PeriodicErrorTerm(0.2, 2.9, 0.4), new PeriodicErrorTerm(0.3, 62.0, 1.9)]);

    /// <summary>
    /// A modern belt-driven strain-wave mount, AM5N, AM7 or Wave class: ±7″ at 287.2 s (the motor pulley) and ±3″ at
    /// 430.8 s, a ratio of 1.5 that only the common 861.6 s holds; amplitude ±10 % over ten cycles. Variant A of §4.1.
    /// </summary>
    public static SimulatorScenario HarmonicDriveModern { get; } = HarmonicDriveBase(nameof(HarmonicDriveModern), 287.2,
        amplitudeChange: 0.10, amplitudeCycles: 10, phaseChangeDeg: 0, phaseCycles: 0,
        [new PeriodicErrorTerm(7.0, 287.2), new PeriodicErrorTerm(3.0, 430.8, 1.1)],
        [new PeriodicErrorTerm(0.2, 2.9, 0.4)]);

    /// <summary>
    /// A rough strain-wave mount, RST-135 or HEM class: ±25″ at 430.8 s with strong harmonics, amplitude ±30 % and phase
    /// ±30° from one cycle to the next, ±1.5″ at 2.4 s (from a forum report only) and ±0.3″ at 9.2 s, 4″ of Dec backlash
    /// (a belt) and wind gusts (no counterweight). Variant C of §4.1.
    /// </summary>
    public static SimulatorScenario HarmonicDriveRough { get; } = HarmonicDriveBase(nameof(HarmonicDriveRough), 430.8,
        amplitudeChange: 0.30, amplitudeCycles: 1, phaseChangeDeg: 30, phaseCycles: 1,
        [
            new PeriodicErrorTerm(25.0, 430.8),
            new PeriodicErrorTerm(8.0, 215.4, 1.3),
            new PeriodicErrorTerm(3.0, 143.6, 2.6),
        ],
        [new PeriodicErrorTerm(1.5, 2.4, 0.4), new PeriodicErrorTerm(0.3, 9.2, 2.2)],
        decBacklashArcsec: 4.0, wind: new WindGustConfig(1.0, 2.0, 3.0));

    /// <summary>
    /// ProxiSky UMi20S with a light load (FSQ106), from its guide logs (docs/notes/UMI-STRAIN-WAVE.md): ±4.5″ at
    /// 430.8 s and ±1″ at 426.6 s (the strain-wave pair, 200 and 202 per axis turn, which beat over a night), ±1.5″ at
    /// 861.6 s, the stage turning 300 times per axis turn (287.2 s) with its sharp harmonics, non-integer lines at 106.4 s
    /// and 62.5 s (RA-axis arcsec); guide rate 1×, no Dec backlash; seeing correlated over 2–3 s; an IMX662 on a 128 mm
    /// guide scope (4.67″/px). Not modelled: Dec pulses whose motion reaches the star partly a frame late, and Dec pulses
    /// acting 0.86 of the calibrated amount (a calibration here would measure the same rate).
    /// </summary>
    public static SimulatorScenario UMi20S { get; } = UmiBase(nameof(UMi20S), declinationDeg: 41.3, decDriftArcsecPerMin: 0.2,
        [
            new PeriodicErrorTerm(1.5, AxisTurnSec / 100, 0.4),
            new PeriodicErrorTerm(4.5, AxisTurnSec / 200, 1.1),
            new PeriodicErrorTerm(1.0, AxisTurnSec / 202, 2.9),
            new PeriodicErrorTerm(0.5, AxisTurnSec / 400, 0.2),
        ],
        stage300: [1.9, 0.8, 0.15, 0.25, 0.35],
        [new PeriodicErrorTerm(1.0, 106.4, 1.7), new PeriodicErrorTerm(0.4, 62.5, 0.6)],
        focalLengthMm: 128, fwhmArcsec: 18, zeroPointMagnitude: 21.0);

    /// <summary>
    /// ProxiSky UMi20S with a heavy load on a long lever (Ocal H-2), from its guide logs: like <see cref="UMi20S"/>, but the
    /// 287.2 s stage as strong as the 430.8 s line (±4″ each) with stronger harmonics and more of the 202 line (more
    /// beating); an IMX662 on a 250 mm guide scope (2.39″/px).
    /// </summary>
    public static SimulatorScenario UMi20SHeavy { get; } = UmiBase(nameof(UMi20SHeavy), declinationDeg: 39.0, decDriftArcsecPerMin: 0.3,
        [
            new PeriodicErrorTerm(1.5, AxisTurnSec / 100, 0.4),
            new PeriodicErrorTerm(4.1, AxisTurnSec / 200, 1.1),
            new PeriodicErrorTerm(1.5, AxisTurnSec / 202, 2.9),
            new PeriodicErrorTerm(0.5, AxisTurnSec / 400, 0.2),
        ],
        stage300: [4.0, 1.0, 0.4, 0.65, 0.4],
        [new PeriodicErrorTerm(0.75, 106.4, 1.7), new PeriodicErrorTerm(0.35, 62.5, 0.6)],
        focalLengthMm: 250, fwhmArcsec: 8);

    /// <summary>
    /// ProxiSky UMi17R (servo in RA only, another gearbox than the 17S) with an SQA85, from its guide logs
    /// (docs/notes/UMI-STRAIN-WAVE.md): ±10.7″ at 856.5 s with its harmonics (±6″ at 428.3 s, ±1″ at 214.1 s), a stage
    /// that turns 100.6 times per axis turn, no whole number, so not geared rigidly to the axis (a belt?); the 300-per-turn
    /// stage at 287.2 s; guide rate 1×, Dec drift 0.7″/min (polar alignment ~2–3′), no Dec backlash; an IMX462 on a 190 mm
    /// guide scope (3.15″/px), guided with 1 s exposures. Not modelled: Dec pulses acting 0.71 of the calibrated amount
    /// and partly a frame late.
    /// </summary>
    public static SimulatorScenario UMi17R { get; } = UmiBase(nameof(UMi17R), declinationDeg: 38.6, decDriftArcsecPerMin: 0.7,
        [
            new PeriodicErrorTerm(10.7, 856.5, 0.4),
            new PeriodicErrorTerm(6.0, 856.5 / 2, 1.6),
            new PeriodicErrorTerm(0.95, 856.5 / 4, 2.3),
        ],
        stage300: [2.5, 0.85, 0.6, 0.5, 0.4],
        [new PeriodicErrorTerm(0.75, 106.8, 1.7), new PeriodicErrorTerm(0.4, 62.5, 0.6), new PeriodicErrorTerm(0.2, 80.5, 2.0)],
        focalLengthMm: 190, fwhmArcsec: 14, zeroPointMagnitude: 20.6, cameraName: "IMX462");

    /// <summary>All presets, for parameterised tests.</summary>
    public static IReadOnlyList<SimulatorScenario> Presets { get; } =
        [GoodMount, PoorPeriodicError, HighSeeing, Backlash, Clouds, HotPixelHeavy, HarmonicDrive, UMi20S, UMi20SHeavy, UMi17R];

    // one turn of the RA axis in tracking seconds (a sidereal day): the mount's stages turn a whole number of times per turn
    private const double AxisTurnSec = 86164.0905;

    // what the UMi presets share: the curve on the RA axis angle; the main lines change ±5 % over ten of their
    // cycles, the 300-per-turn stage (fundamental and harmonics 2–5, phases from its sharp, sawtooth-like profile) and the
    // non-integer lines don't; guide rate 1×; a white seeing part and two parts common to all stars on cells of 3 and 8 s, fitted
    // to the measured seeing under 20 s (σ ≈ 0.45″, lag-1/2/3 autocorrelation ≈ 0.48/0.27/0.14 at 0.87 s per frame); occasional wind; an IMX662 or IMX462 guide camera (HCG read noise), its brightness set
    // so the guide star's SNR is about the logged one
    private static SimulatorScenario UmiBase(
        string name,
        double declinationDeg,
        double decDriftArcsecPerMin,
        IReadOnlyList<PeriodicErrorTerm> mainLines,
        IReadOnlyList<double> stage300,
        IReadOnlyList<PeriodicErrorTerm> otherLines,
        double focalLengthMm,
        double fwhmArcsec,
        double zeroPointMagnitude = 20.0,
        string cameraName = "IMX662") => GoodMount with
    {
        Name = name,
        Mount = GoodMount.Mount with
        {
            DeclinationDeg = declinationDeg,
            PeriodicError =
            [
                .. mainLines.Select(t => t with { AmplitudeChange = 0.05, AmplitudeChangeSec = 10 * t.PeriodSec }),
                .. stage300.Select((a, k) => new PeriodicErrorTerm(a, AxisTurnSec / (300 * (k + 1)), 0.5 + 0.9 * k)),
                .. otherLines,
            ],
            PeriodicErrorFollowsAxis = true,
            GuideRateRa = 1.0,
            GuideRateDec = 1.0,
            DecDriftArcsecPerMin = decDriftArcsecPerMin,
            Wind = new WindGustConfig(0.2, 1.5, 2.0),
        },
        Sky = GoodMount.Sky with
        {
            FwhmArcsec = fwhmArcsec,
            SeeingJitterArcsec = 0.18,
            SeeingLayers = [new SeeingLayer(3.0, 0.3), new SeeingLayer(8.0, 0.4)],
        },
        Camera = GoodMount.Camera with
        {
            Name = cameraName,
            SensorWidth = 1920,
            SensorHeight = 1080,
            PixelSizeUm = 2.9,
            FocalLengthMm = focalLengthMm,
            ReadNoiseElectrons = 1.0,
            ZeroPointMagnitude = zeroPointMagnitude,
        },
    };

    public override string ToString() => Name;

    // what the three strain-wave variants share: GoodMount's sky, camera, drifts and random walk, the curve on the RA axis
    // angle, guide rates 0.9×; the main lines change along the gear (in cycles of the dominant period), the short
    // components don't
    private static SimulatorScenario HarmonicDriveBase(
        string name,
        double dominantPeriodSec,
        double amplitudeChange,
        double amplitudeCycles,
        double phaseChangeDeg,
        double phaseCycles,
        IReadOnlyList<PeriodicErrorTerm> mainLines,
        IReadOnlyList<PeriodicErrorTerm> shortComponents,
        double decBacklashArcsec = 0,
        WindGustConfig? wind = null) => GoodMount with
    {
        Name = name,
        Mount = GoodMount.Mount with
        {
            PeriodicError =
            [
                .. mainLines.Select(t => t with
                {
                    AmplitudeChange = amplitudeChange,
                    AmplitudeChangeSec = amplitudeCycles * dominantPeriodSec,
                    PhaseChangeRad = phaseChangeDeg * Math.PI / 180,
                    PhaseChangeSec = phaseCycles * dominantPeriodSec,
                }),
                .. shortComponents,
            ],
            PeriodicErrorFollowsAxis = true,
            GuideRateRa = 0.9,
            GuideRateDec = 0.9,
            DecBacklashArcsec = decBacklashArcsec,
            Wind = wind,
        },
    };
}
