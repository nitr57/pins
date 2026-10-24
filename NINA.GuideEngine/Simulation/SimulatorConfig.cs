// SPDX-License-Identifier: MPL-2.0

using NINA.GuideEngine.Core;

namespace NINA.GuideEngine.Simulation;

/// <summary>
/// An offset on the sky in arcseconds, measured along great circles: <see cref="Ra"/> is positive
/// towards East (increasing right ascension, already multiplied by cos(dec)), <see cref="Dec"/> is
/// positive towards North.
/// </summary>
public readonly record struct SkyOffset(double Ra, double Dec)
{
    public static SkyOffset Zero => default;

    public double Magnitude => Math.Sqrt(Ra * Ra + Dec * Dec);

    public static SkyOffset operator +(SkyOffset a, SkyOffset b) => new(a.Ra + b.Ra, a.Dec + b.Dec);

    public static SkyOffset operator -(SkyOffset a, SkyOffset b) => new(a.Ra - b.Ra, a.Dec - b.Dec);

    public static SkyOffset operator *(SkyOffset a, double f) => new(a.Ra * f, a.Dec * f);

    public override string ToString() => FormattableString.Invariant($"(RA {Ra:F3}\", Dec {Dec:F3}\")");
}

/// <summary>
/// One periodic error harmonic of the RA drive: <c>A·sin(2π·t/P + φ)</c>, optionally with an amplitude and a phase that
/// change slowly along the gear (a strain-wave gear's curve changes from cycle to cycle).
/// </summary>
/// <param name="AmplitudeArcsec">Semi-amplitude (half of peak-to-peak) in arcsec of RA axis angle, i.e. on the sky at dec 0.</param>
/// <param name="PeriodSec">Period in seconds (worm period for the fundamental).</param>
/// <param name="PhaseRad">Phase at t = 0 in radians.</param>
/// <remarks>
/// The changes are smooth random curves over the same time as the sine (the RA axis angle with
/// <see cref="MountSimConfig.PeriodicErrorFollowsAxis"/>): values drawn evenly within ± the given size at knots
/// <see cref="AmplitudeChangeSec"/> or <see cref="PhaseChangeSec"/> apart, hashed from <see cref="MountSimConfig.Seed"/>,
/// the term's position in the list and the knot, and joined by a smoothstep. So the same gear angle gives the same curve
/// again, and the curve stays within the bounds. Size 0 (the default) leaves the term a plain sine.
/// </remarks>
public sealed record PeriodicErrorTerm(double AmplitudeArcsec, double PeriodSec, double PhaseRad = 0.0)
{
    /// <summary>The amplitude changes by up to ± this fraction (0.1 = ±10 %).</summary>
    public double AmplitudeChange { get; init; }

    /// <summary>Distance of the amplitude's knots in seconds; about the time over which it changes.</summary>
    public double AmplitudeChangeSec { get; init; }

    /// <summary>The phase changes by up to ± this many radians.</summary>
    public double PhaseChangeRad { get; init; }

    /// <summary>Distance of the phase's knots in seconds.</summary>
    public double PhaseChangeSec { get; init; }
}

/// <summary>Random wind gusts: Poisson arrivals, each a half-sine bump in a random direction.</summary>
/// <param name="GustsPerMinute">Mean arrival rate.</param>
/// <param name="AmplitudeArcsec">Peak displacement of a full-strength gust (each gust draws 50..100 %).</param>
/// <param name="DurationSec">Duration of one gust.</param>
public sealed record WindGustConfig(double GustsPerMinute, double AmplitudeArcsec, double DurationSec);

/// <summary>Configuration of the simulated mount and its tracking errors. All angles in arcsec on the sky unless noted.</summary>
public sealed record MountSimConfig
{
    public string Name { get; init; } = "Simulated mount";

    public double DeclinationDeg { get; init; } = 20.0;

    public double RightAscensionHours { get; init; } = 6.0;

    /// <summary>
    /// Initial pier side. The camera orientation is defined for <see cref="PierSide.East"/> (and Unknown);
    /// on <see cref="PierSide.West"/> the image is rotated by 180°.
    /// </summary>
    public PierSide PierSide { get; init; } = PierSide.East;

    /// <summary>RA guide rate as a multiple of sidereal.</summary>
    public double GuideRateRa { get; init; } = 0.5;

    /// <summary>Dec guide rate as a multiple of sidereal.</summary>
    public double GuideRateDec { get; init; } = 0.5;

    public bool SupportsSimultaneousPulses { get; init; }

    /// <summary>RA periodic error harmonics (RA axis angle; the on-sky effect is scaled by cos(dec)).</summary>
    public IReadOnlyList<PeriodicErrorTerm> PeriodicError { get; init; } = [];

    /// <summary>Constant RA drift on the sky, arcsec per minute (positive = pointing drifts East).</summary>
    public double RaDriftArcsecPerMin { get; init; }

    /// <summary>Constant Dec drift (polar misalignment), arcsec per minute (positive = pointing drifts North).</summary>
    public double DecDriftArcsecPerMin { get; init; }

    /// <summary>
    /// Change of the Dec drift per hour (arcsec per minute per hour): the drift of a polar misalignment changes with the
    /// hour angle and can reverse. The drift at t seconds is <see cref="DecDriftArcsecPerMin"/> + this · t / 3600.
    /// </summary>
    public double DecDriftChangePerHour { get; init; }

    /// <summary>Random-walk tracking noise per axis, arcsec per sqrt(second).</summary>
    public double RandomWalkArcsecPerSqrtSec { get; init; }

    public WindGustConfig? Wind { get; init; }

    /// <summary>Dec gear backlash dead band (arcsec) taken up on every Dec reversal.</summary>
    public double DecBacklashArcsec { get; init; }

    /// <summary>The dead band as pulse time at the Dec guide rate (ms): what a backlash measurement should find.</summary>
    public double DecBacklashMs => GuideRateDec > 0 ? DecBacklashArcsec / (GuideRateDec * SimulatedMount.SiderealArcsecPerSec) * 1000.0 : 0;

    /// <summary>The first <c>RaStictionMs</c> of every RA pulse produce no motion.</summary>
    public double RaStictionMs { get; init; }

    /// <summary>The first <c>DecStictionMs</c> of every Dec pulse produce no motion (before any backlash take-up).</summary>
    public double DecStictionMs { get; init; }

    /// <summary>Speed factor of West pulses (asymmetric guide rates: gear mesh, balance); 1 = nominal.</summary>
    public double RaWestEfficiency { get; init; } = 1.0;

    /// <summary>Speed factor of East pulses.</summary>
    public double RaEastEfficiency { get; init; } = 1.0;

    /// <summary>Speed factor of North pulses (motor direction).</summary>
    public double DecNorthEfficiency { get; init; } = 1.0;

    /// <summary>Speed factor of South pulses (motor direction).</summary>
    public double DecSouthEfficiency { get; init; } = 1.0;

    /// <summary>
    /// When true (typical GEM behaviour, matching PHD2's default of not flipping Dec calibration), a North
    /// pulse moves the pointing South on the sky while on <see cref="PierSide.West"/>, so that together
    /// with the 180° image rotation the star moves in the same sensor direction on both pier sides.
    /// </summary>
    public bool DecGuideReversedOnWestPier { get; init; } = true;

    /// <summary>
    /// When true the periodic error follows the RA axis angle, as on a real mount: its phase is the hour angle
    /// (sidereal time − true right ascension, plus 12 h on the West pier side) in tracking seconds, so it jumps with slews
    /// and flips and stands still while tracking is off. False (default): it runs with time, as in the existing scenarios.
    /// </summary>
    public bool PeriodicErrorFollowsAxis { get; init; }

    /// <summary>Local sidereal time at t = 0 in hours; null = <see cref="RightAscensionHours"/> (the target on the meridian).</summary>
    public double? SiderealTimeAtStartHours { get; init; }

    /// <summary>Seed for random walk and wind gusts.</summary>
    public int Seed { get; init; } = 1;
}

/// <summary>A star at a fixed sky position relative to the nominal field centre.</summary>
/// <param name="EastArcsec">Offset East of the field centre (great circle).</param>
/// <param name="NorthArcsec">Offset North of the field centre.</param>
/// <param name="Magnitude">Instrumental magnitude; flux via <see cref="CameraSimConfig.ZeroPointMagnitude"/>.</param>
public sealed record SimStar(double EastArcsec, double NorthArcsec, double Magnitude);

/// <summary>Transparency change (clouds) between two times, with optional linear ramps at both ends.</summary>
public sealed record TransparencyWindow(double StartSec, double EndSec, double Transmission, double RampSec = 0.0);

/// <summary>
/// One layer of slow seeing (<see cref="SkySimConfig.SeeingLayers"/>): all stars move together by a value that is
/// constant within cells of <paramref name="CellSec"/> and independent from cell to cell.
/// </summary>
/// <param name="CellSec">Length of a cell in seconds.</param>
/// <param name="SigmaArcsec">Standard deviation per axis of a cell's displacement, arcsec.</param>
/// <remarks>
/// Common to all stars, a simplification on the safe side for multi-star guiding: motion this slow comes from
/// turbulence on scales of the wind speed times the cell, tens of metres, which in the ground layer the light of all
/// guide stars crosses together and in a layer at 5 km still largely (a 10′ field there is 15 m wide).
/// </remarks>
public sealed record SeeingLayer(double CellSec, double SigmaArcsec);

/// <summary>Star flux forced to zero between two times (star lost). <c>StarIndex</c> null = all stars.</summary>
public sealed record StarOcclusion(double StartSec, double EndSec, int? StarIndex = null);

/// <summary>Configuration of the sky: star field, atmosphere (seeing, transparency).</summary>
public sealed record SkySimConfig
{
    /// <summary>Explicit star list; when null a random field is generated from the fields below.</summary>
    public IReadOnlyList<SimStar>? Stars { get; init; }

    public int RandomStarCount { get; init; } = 25;

    public double BrightestMagnitude { get; init; } = 8.0;

    public double FaintestMagnitude { get; init; } = 12.5;

    public int StarFieldSeed { get; init; } = 42;

    /// <summary>Star FWHM (seeing + optics) in arcsec, Moffat profile.</summary>
    public double FwhmArcsec { get; init; } = 3.5;

    public double MoffatBeta { get; init; } = 3.0;

    /// <summary>
    /// Seeing-induced image motion: standard deviation per axis (arcsec) of the mean star position over a
    /// 1 s exposure. Longer exposures average down as 1/sqrt(T) (for T ≫ coherence time).
    /// </summary>
    public double SeeingJitterArcsec { get; init; } = 0.4;

    /// <summary>Coherence time of the seeing image motion in ms.</summary>
    public double SeeingCoherenceMs { get; init; } = 50.0;

    /// <summary>Fraction of the seeing motion variance common to all stars (1 = fully correlated field).</summary>
    public double SeeingCommonFraction { get; init; } = 0.5;

    public int SeeingSeed { get; init; } = 7;

    /// <summary>
    /// Slow seeing on top of the fast motion above: layers of image motion that is constant within cells of seconds, so
    /// successive frames are correlated (the tilt spectrum falls only as f^(−2/3) at low frequencies; Conan, Rousset and
    /// Madec 1995, JOSA A 12, 1559). Empty (the default): only the fast motion.
    /// </summary>
    public IReadOnlyList<SeeingLayer> SeeingLayers { get; init; } = [];

    public IReadOnlyList<TransparencyWindow> Transparency { get; init; } = [];

    public IReadOnlyList<StarOcclusion> Occlusions { get; init; } = [];
}

/// <summary>Configuration of the simulated guide camera and optics.</summary>
public sealed record CameraSimConfig
{
    public string Name { get; init; } = "Simulator";

    public int SensorWidth { get; init; } = 1936;

    public int SensorHeight { get; init; } = 1216;

    public double PixelSizeUm { get; init; } = 5.86;

    public double FocalLengthMm { get; init; } = 800.0;

    /// <summary>Angle of the +RA (East) direction on the sensor, degrees from +x towards +y, for pier side East.</summary>
    public double CameraAngleDeg { get; init; } = 30.0;

    /// <summary>Mirror image (odd parity): the North direction is flipped relative to East.</summary>
    public bool Mirrored { get; init; }

    /// <summary>Conversion gain in electrons per ADU at gain 0 (see <see cref="GainUnitsPerDecade"/>).</summary>
    public double GainElectronsPerAdu { get; init; } = 1.0;

    /// <summary>Lowest gain setting (native units) reported through <see cref="Core.IGainRange"/>.</summary>
    public int GainMin { get; init; }

    /// <summary>Highest gain setting reported through <see cref="Core.IGainRange"/>.</summary>
    public int GainMax { get; init; } = 100;

    /// <summary>Gain used until a capture request asks for another one.</summary>
    public int DefaultGain { get; init; }

    /// <summary>
    /// Gain units per factor 10 of amplification: electrons per ADU = <see cref="GainElectronsPerAdu"/> /
    /// 10^(gain / GainUnitsPerDecade). With the defaults gain 100 amplifies 10×. Read noise (electrons) does not change.
    /// </summary>
    public double GainUnitsPerDecade { get; init; } = 100.0;

    public double ReadNoiseElectrons { get; init; } = 3.5;

    /// <summary>Dark current per unbinned pixel, electrons per second.</summary>
    public double DarkCurrentElectronsPerSec { get; init; } = 0.05;

    /// <summary>Sky background per unbinned pixel, electrons per second.</summary>
    public double SkyBackgroundElectronsPerSec { get; init; } = 20.0;

    /// <summary>Bias / offset level in ADU.</summary>
    public double BiasAdu { get; init; } = 200.0;

    /// <summary>Saturation level in ADU (clamped to 2^BitsPerPixel − 1).</summary>
    public ushort MaxAdu { get; init; } = 65535;

    public int BitsPerPixel { get; init; } = 16;

    public int MaxBinning { get; init; } = 4;

    /// <summary>Magnitude producing 1 electron per second.</summary>
    public double ZeroPointMagnitude { get; init; } = 20.0;

    public int HotPixelCount { get; init; } = 20;

    public int ColdPixelCount { get; init; } = 5;

    public int DefectSeed { get; init; } = 1234;

    public int NoiseSeed { get; init; } = 99;

    public double DownloadMs { get; init; } = 100.0;

    /// <summary>Probability that an exposure fails (exception after the exposure time).</summary>
    public double ExposureFailureProbability { get; init; }

    /// <summary>Probability that an exposure times out (exception after exposure + <see cref="TimeoutMs"/>).</summary>
    public double ExposureTimeoutProbability { get; init; }

    public double TimeoutMs { get; init; } = 5000.0;

    /// <summary>PSF sub-pixel sampling per axis; 0 = automatic (3 when FWHM &lt; 2.5 px, else 1).</summary>
    public int PsfOversampling { get; init; }

    /// <summary>Maximum number of PSF copies used to integrate motion (seeing, mount) over an exposure.</summary>
    public int MaxMotionSubSamples { get; init; } = 8;

    /// <summary>Unbinned image scale in arcsec/px.</summary>
    public double PixelScale => 206.265 * PixelSizeUm / FocalLengthMm;
}
