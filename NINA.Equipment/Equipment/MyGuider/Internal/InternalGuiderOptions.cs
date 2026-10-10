// SPDX-License-Identifier: MPL-2.0

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Globalization;
using NINA.Equipment.Equipment.MyGuider.Advanced;
using NINA.Profile;
using NINA.Profile.Interfaces;
using NINA.GuideEngine.Algorithms;
using NINA.GuideEngine.Calibration;
using NINA.GuideEngine.Core;
using NINA.GuideEngine.Guiding;
using NINA.GuideEngine.Incidents;
using NINA.GuideEngine.MultiStar;
using NINA.GuideEngine.Simulation;
using NINA.GuideEngine.Stars;
using GuiderSettings = NINA.GuideEngine.Guiding.GuiderSettings;

namespace NINA.Equipment.Equipment.MyGuider.Internal;

/// <summary>Definition of one internal guider setting.</summary>
internal sealed record SettingDef(
    string Name,
    string Label,
    string Group,
    string Type,
    string Default,
    bool Basic = false,
    string? Unit = null,
    double? Min = null,
    double? Max = null,
    string[]? Options = null,
    bool RequiresReconnect = false,
    string Description = "",
    Func<IGuiderSettings, string?>? Phd2Fallback = null,
    string? DependsOn = null,
    string[]? AppliesTo = null);

/// <summary>
/// Internal guider settings stored per profile in the plugin settings. A setting that was never set falls back to the
/// corresponding PHD2 setting of the PINS profile (so an existing PHD2 configuration carries over), then to the
/// built-in default.
/// </summary>
internal sealed class InternalGuiderOptions
{
    /// <summary>
    /// Key of the settings in the profile's plugin settings: the id of the pins-guider plugin the guider came from, so
    /// that its settings carry over.
    /// </summary>
    public static readonly Guid SettingsId = Guid.Parse("3e519099-8e1e-46ee-bed2-f775abd90619");

    public const string GuideCameraSource = "GuideCamera";

    public const string SimulatorSource = "Simulator";

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static readonly string[] RaAlgorithms = ["Hysteresis", "Lowpass2", "Lowpass", "None", "Predictive"];
    private static readonly string[] DecAlgorithms = ["ResistSwitch", "Hysteresis", "Lowpass2", "Lowpass", "None", "Predictive"];

    private const string AlgorithmDescription =
        "Predictive (experimental) learns the seeing and the mount's own motion from the guide frames and corrects the error " +
        "it predicts for the next frame, so aggression, hysteresis and min move don't apply to it.";

    // the algorithms that read each parameter (see Algorithm()); the settings sheet hides a parameter for the others
    private static readonly string[] RaAggressionAlgorithms = ["Hysteresis"];
    private static readonly string[] DecAggressionAlgorithms = ["ResistSwitch", "Hysteresis"];
    private static readonly string[] HysteresisAlgorithms = ["Hysteresis"];
    private static readonly string[] Lowpass2Algorithms = ["Lowpass2"];
    private static readonly string[] RaMinMoveAlgorithms = ["Hysteresis", "Lowpass2", "Lowpass"];
    private static readonly string[] DecMinMoveAlgorithms = ["ResistSwitch", "Hysteresis", "Lowpass2", "Lowpass"];
    private static readonly string[] PredictiveAlgorithms = ["Predictive"];

    private const string CorrectionPaceDescription = "Share of the remaining error the Predictive algorithm corrects per frame: " +
        "0.5 takes an offset out over 2-3 frames without swinging past zero, 1 corrects all of it at once. " +
        "Drift and periodic error are always corrected in full.";

    // settings of the built-in simulator: shown only while it is the guide source
    private static readonly string[] SimulatorOnly = [SimulatorSource];
    private static readonly string[] SimulatorScenarios = SimulatorScenario.Presets.Select(p => p.Name).ToArray();
    private static readonly string[] SimulatorFaults = Enum.GetNames<SimulatorFault>();

    public static readonly IReadOnlyList<SettingDef> Definitions =
    [
        // Camera
        new("GuideSource", "Guide frames from", "Camera", "enum", GuideCameraSource, Basic: true, Options: [GuideCameraSource, SimulatorSource],
            RequiresReconnect: true,
            Description: "GuideCamera: the camera in the guide camera slot of the equipment. Simulator: the built-in simulator, for testing without hardware."),
        new("ExposureSeconds", "Exposure", "Camera", "double", "2", Basic: true, Unit: "s", Min: 0.01, Max: 30,
            Phd2Fallback: s => s.PHD2ExposureMs is { } ms && ms > 0 ? (ms / 1000.0).ToString(Inv) : null),
        new("Gain", "Gain", "Camera", "int", "-1", Basic: true, Min: -1, Max: 10000,
            Description: "-1 uses the gain of the guide camera's settings. (PHD2's gain is a percentage, so it is not carried over.)"),
        new("Offset", "Offset", "Camera", "int", "-1", Min: -1, Max: 10000, Description: "-1 uses the offset of the guide camera's settings."),
        new("Binning", "Binning", "Camera", "int", "1", Basic: true, Min: 1, Max: 4, Phd2Fallback: s => s.PHD2CameraBinning?.ToString(Inv)),
        new("FocalLengthMm", "Guide focal length", "Camera", "double", "0", Basic: true, Unit: "mm", Min: 0, Max: 10000,
            Description: "Focal length of the guide scope (or main scope with an OAG). Needed for arcsec values and calibration step.",
            Phd2Fallback: s => s.PHD2FocalLength is { } f && f > 0 ? f.ToString(Inv) : null),
        new("PixelSizeUm", "Pixel size override", "Camera", "double", "0", Unit: "µm", Min: 0, Max: 50, Description: "0 uses the camera driver's pixel size."),
        new("NoiseReduction", "Noise reduction", "Camera", "enum", "None", Options: ["None", "Mean2x2", "Median3x3"],
            Phd2Fallback: s => s.PHD2NoiseReductionMethod switch { 1 => "Mean2x2", 2 => "Median3x3", 0 => "None", _ => null }),
        new("UseDarkLibrary", "Use dark library", "Camera", "bool", "true", Basic: true,
            Description: "Subtract darks: the native library if built, otherwise PHD2's dark library for this camera size."),
        new("PulseOutput", "Guide output", "Camera", "enum", "Mount", Basic: true, Options: ["Mount", "CameraST4"], RequiresReconnect: true,
            Description: "Mount: pulse guiding through the connected mount driver. CameraST4: through the guide camera's ST4 port (INDI cameras)."),

        // Stars
        new("MultiStar", "Multi-star guiding", "Stars", "bool", "true", Basic: true, Phd2Fallback: s => Bool(s.PHD2UseMultipleStars)),
        new("MaxStars", "Max guide stars", "Stars", "int", "9", Basic: true, Min: 1, Max: 12),
        new("MinSnr", "Min star SNR", "Stars", "double", "6", Basic: true, Min: 3, Max: 100, Phd2Fallback: s => s.PHD2AfMinStarSnr?.ToString(Inv)),
        new("SearchRegion", "Search region", "Stars", "int", "15", Unit: "px", Min: 7, Max: 50, Phd2Fallback: s => s.PHD2SearchRegion?.ToString(Inv)),
        new("MinHfd", "Min star HFD", "Stars", "double", "1.5", Unit: "px", Min: 0.1, Max: 10, Phd2Fallback: s => s.PHD2MinStarHFD?.ToString(Inv)),
        new("MaxHfd", "Max star HFD", "Stars", "double", "20", Unit: "px", Min: 1, Max: 50, Phd2Fallback: s => s.PHD2MaxStarHFD?.ToString(Inv)),
        new("MassChangeEnabled", "Star mass change detection", "Stars", "bool", "true", Phd2Fallback: s => Bool(s.PHD2MassChangeThresholdEnabled)),
        new("MassChangeThreshold", "Mass change threshold", "Stars", "double", "0.5", Min: 0.1, Max: 1,
            Phd2Fallback: s => s.PHD2MassChangeThreshold is { } v ? (v > 1 ? v / 100.0 : v).ToString(Inv) : null),
        new("PrimaryDropoutFallback", "Keep guiding on secondaries", "Stars", "bool", "true",
            Description: "When the primary star drops out for a frame, estimate its position from ≥3 agreeing secondary stars."),
        new("SaturationAdu", "Saturation ADU", "Stars", "int", "0", Min: 0, Max: 65535, Description: "0 = detect saturation from the star profile."),

        // Algorithms
        new("RaAlgorithm", "RA algorithm", "Algorithms", "enum", "Hysteresis", Basic: true, Options: RaAlgorithms, Description: AlgorithmDescription,
            Phd2Fallback: s => AlgorithmName(s.PHD2GuideAlgorithmRA, RaAlgorithms)),
        new("RaAggression", "RA aggression", "Algorithms", "double", "0.7", Basic: true, Min: 0, Max: 1, Description: "Share of the error corrected per frame (0..1).",
            Phd2Fallback: s => s.PHD2RAAggressiveness?.ToString(Inv), DependsOn: "RaAlgorithm", AppliesTo: RaAggressionAlgorithms),
        new("RaHysteresis", "RA hysteresis", "Algorithms", "double", "0.1", Min: 0, Max: 0.5, Phd2Fallback: s => s.PHD2RAHysteresis?.ToString(Inv),
            DependsOn: "RaAlgorithm", AppliesTo: HysteresisAlgorithms),
        new("RaLowpass2Aggressiveness", "RA Lowpass2 aggressiveness", "Algorithms", "double", "80", Unit: "%", Min: 0, Max: 120,
            Phd2Fallback: s => s.PHD2RALowpass2Aggressiveness?.ToString(Inv), DependsOn: "RaAlgorithm", AppliesTo: Lowpass2Algorithms),
        new("RaMinMove", "RA min move", "Algorithms", "double", "0", Unit: "px", Min: 0, Max: 5, Description: "0 = automatic from the image scale.",
            Phd2Fallback: s => s.PHD2RAMinMove?.ToString(Inv), DependsOn: "RaAlgorithm", AppliesTo: RaMinMoveAlgorithms),
        new("RaCorrectionPace", "RA correction pace", "Algorithms", "double", "0.5", Min: 0.2, Max: 1, DependsOn: "RaAlgorithm",
            AppliesTo: PredictiveAlgorithms, Description: CorrectionPaceDescription),
        new("PeriodicErrorPrediction", "Periodic error prediction", "Algorithms", "bool", "true", DependsOn: "RaAlgorithm", AppliesTo: PredictiveAlgorithms,
            Description: "Learns the periodic error of the RA worm and corrects it before it shows. The learned curve is kept per profile and mount."),
        new("WormTeeth", "RA worm wheel teeth", "Algorithms", "int", "0", Min: 0, Max: 2000, DependsOn: "RaAlgorithm", AppliesTo: PredictiveAlgorithms,
            Description: "Teeth of the RA worm wheel (e.g. 180 on an EQ6-R, 135 on an HEQ5); gives the exact worm period. 0 = detect the period from the guide frames."),
        new("ForgetPeriodicError", "Forget periodic error", "Algorithms", "action", "", DependsOn: "RaAlgorithm", AppliesTo: PredictiveAlgorithms,
            Description: "Forgets the periodic error learned for this mount, e.g. after the worm was adjusted; it is then learned anew."),
        new("DecAlgorithm", "Dec algorithm", "Algorithms", "enum", "ResistSwitch", Basic: true, Options: DecAlgorithms, Description: AlgorithmDescription,
            Phd2Fallback: s => AlgorithmName(s.PHD2GuideAlgorithmDec, DecAlgorithms)),
        new("DecAggression", "Dec aggression", "Algorithms", "double", "1.0", Basic: true, Min: 0, Max: 1, Phd2Fallback: s => s.PHD2DecAggressiveness?.ToString(Inv),
            DependsOn: "DecAlgorithm", AppliesTo: DecAggressionAlgorithms),
        new("DecHysteresis", "Dec hysteresis", "Algorithms", "double", "0.1", Min: 0, Max: 0.5, Phd2Fallback: s => s.PHD2DecHysteresis?.ToString(Inv),
            DependsOn: "DecAlgorithm", AppliesTo: HysteresisAlgorithms),
        new("DecLowpass2Aggressiveness", "Dec Lowpass2 aggressiveness", "Algorithms", "double", "80", Unit: "%", Min: 0, Max: 120,
            Phd2Fallback: s => s.PHD2DecLowpass2Aggressiveness?.ToString(Inv), DependsOn: "DecAlgorithm", AppliesTo: Lowpass2Algorithms),
        new("DecFastSwitch", "Dec fast switch", "Algorithms", "bool", "true", Phd2Fallback: s => Bool(s.PHD2DecFastSwitch),
            DependsOn: "DecAlgorithm", AppliesTo: ["ResistSwitch"]),
        new("DecMinMove", "Dec min move", "Algorithms", "double", "0", Unit: "px", Min: 0, Max: 5, Description: "0 = automatic from the image scale.",
            Phd2Fallback: s => s.PHD2DecMinMove?.ToString(Inv), DependsOn: "DecAlgorithm", AppliesTo: DecMinMoveAlgorithms),
        new("DecCorrectionPace", "Dec correction pace", "Algorithms", "double", "0.5", Min: 0.2, Max: 1, DependsOn: "DecAlgorithm",
            AppliesTo: PredictiveAlgorithms, Description: CorrectionPaceDescription),
        new("DecGuideMode", "Dec guide mode", "Algorithms", "enum", "Auto", Basic: true, Options: ["Auto", "North", "South", "Off", "Drift"],
            Description: "Auto guides Dec both ways, North or South one way only, Off not at all. Drift follows the drift: it measures the Dec " +
                "drift while guiding and guides only against it, so the Dec gears never reverse and backlash stops mattering; it switches " +
                "when the drift reverses and guides both ways until the drift is known, while settling and to bring back a large error.",
            Phd2Fallback: s => s.PHD2DecGuideMode switch { "Auto" or "North" or "South" or "Off" => s.PHD2DecGuideMode, "None" => "Off", _ => null }),
        new("MaxRaDurationMs", "Max RA pulse", "Algorithms", "int", "2500", Unit: "ms", Min: 50, Max: 8000, Phd2Fallback: s => s.PHD2MaxRADuration?.ToString(Inv)),
        new("MaxDecDurationMs", "Max Dec pulse", "Algorithms", "int", "2500", Unit: "ms", Min: 50, Max: 8000, Phd2Fallback: s => s.PHD2MaxDecDuration?.ToString(Inv)),
        new("MinPulseMs", "Min pulse", "Algorithms", "int", "20", Unit: "ms", Min: 0, Max: 50,
            Description: "Shorter guide pulses are rounded to 0 or to this length: some mounts mishandle very short pulses. 0 = any length."),
        new("SimultaneousPulses", "Simultaneous RA and Dec pulses", "Algorithms", "bool", "false",
            Description: "Sends a frame's RA and Dec pulses at the same time instead of one after the other, which shortens the guide cycle. " +
                "Only with mounts that time both axes separately (pins' native OnStepX driver); other mounts keep pulsing one after the other."),
        new("BacklashCompensation", "Dec backlash compensation", "Algorithms", "bool", "false"),
        new("BacklashPulseMs", "Dec backlash pulse", "Algorithms", "int", "20", Unit: "ms", Min: 20, Max: 8000),
        new("PulseModel", "Pulse model", "Algorithms", "bool", "true",
            Description: "Learns from the dithers how far a guide pulse really moves the star on each axis, and sizes the pulses by " +
                "it, so a calibration that is off no longer makes the guiding over- or under-correct. It starts as calibrated, " +
                "changes only what the dithers clearly show, and keeps what it learned per profile and mount until the next calibration."),

        // Calibration
        new("CalibrationStepMs", "Calibration step", "Calibration", "int", "0", Unit: "ms", Min: 0, Max: 10000, Description: "0 = automatic from optics, guide rate and declination."),
        new("ReuseCalibration", "Reuse calibration", "Calibration", "bool", "true", Description: "Keep the calibration across sessions and adjust it for declination and pier side.",
            Phd2Fallback: s => Bool(s.PHD2AutoRestoreCalibration)),
        new("AssumeOrthogonal", "Assume Dec orthogonal", "Calibration", "bool", "false", Phd2Fallback: s => Bool(s.PHD2AssumeDecOrthogonal)),
        new("DecCompensation", "Declination compensation", "Calibration", "bool", "true", Phd2Fallback: s => Bool(s.PHD2UseDecCompensation)),
        new("DecFlipRequired", "Reverse Dec after meridian flip", "Calibration", "bool", "false", Phd2Fallback: s => Bool(s.PHD2ReverseDecOnFlip),
            Description: "Per-mount setting; corrected automatically by the post-flip Dec self-check."),
        new("VerifyDecAfterFlip", "Post-flip Dec self-check", "Calibration", "bool", "true"),

        // Dither
        new("DitherMode", "Dither mode", "Dither", "enum", "Random", Options: ["Random", "Spiral"], Phd2Fallback: s => s.PHD2DitherMode is "Random" or "Spiral" ? s.PHD2DitherMode : null),
        new("DitherScale", "Dither scale", "Dither", "double", "1", Min: 0.1, Max: 100, Phd2Fallback: s => s.PHD2DitherScale?.ToString(Inv)),
        new("FastRecenter", "Fast recenter after dither", "Dither", "bool", "true", Phd2Fallback: s => Bool(s.PHD2FastRecenter)),

        // Safety
        new("ReacquireTimeoutSec", "Lost star timeout", "Safety", "int", "60", Unit: "s", Min: 10, Max: 600),
        new("MaxPulseDutyPercent", "Max correction duty", "Safety", "int", "50", Unit: "%", Min: 10, Max: 100,
            Description: "Guiding stops when an axis is pulsed more than this share of a minute (runaway protection)."),
        new("PauseWhenSlewing", "Pause while mount slews", "Safety", "bool", "true",
            Description: "Pause corrections while the mount reports slewing/parked. Turn off only if the mount driver reports 'slewing' permanently."),
        new("PauseWhenTrackingOff", "Pause when tracking is off", "Safety", "bool", "true",
            Description: "Pause corrections while the mount reports tracking off. Turn off if the mount driver cannot report its tracking state."),
        new("SaveGuideLog", "Write guide log", "Safety", "bool", "true", Description: "PHD2-compatible guide log in ~/.local/share/NINA/InternalGuider/Logs."),

        // Incidents (flight recorder)
        new("IncidentRecorder", "Record incidents", "Incidents", "bool", "true",
            Description: "Keeps the last minutes of guide frames in memory and saves them around star losses, runaways, mount and camera problems and spikes, for a replay and a download."),
        new("IncidentBudgetMb", "Incident disk budget", "Incidents", "int", "1000", Unit: "MB", Min: 100, Max: 20000,
            Description: "Disk space for recorded incidents; the oldest ones that are not kept are deleted first. Simulator incidents have their own 200 MB."),
        new("SimulatorScenario", "Simulator scenario", "Incidents", "enum", "GoodMount", Options: SimulatorScenarios, RequiresReconnect: true,
            DependsOn: "GuideSource", AppliesTo: SimulatorOnly, Description: "Mount, sky and camera the built-in simulator simulates."),
        new("SimulateFault", "Simulate a fault", "Incidents", "action", "", Options: SimulatorFaults, DependsOn: "GuideSource", AppliesTo: SimulatorOnly,
            Description: "Injects a fault into the running simulator: clouds (110 s), a bump of the mount, a mount that stops responding (60 s), " +
            "3 failed exposures, or Dec corrections going the wrong way (120 s)."),
    ];

    private readonly IProfileService profileService;
    private readonly Guid pluginId;
    private readonly object gate = new();

    public InternalGuiderOptions(IProfileService profileService, Guid pluginId)
    {
        this.profileService = profileService;
        this.pluginId = pluginId;
    }

    public static SettingDef? Find(string name) => Definitions.FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase));

    private PluginOptionsAccessor Accessor => new(profileService, pluginId);

    public string GetString(string name)
    {
        lock (gate)
        {
            return GetStringCore(name);
        }
    }

    private string GetStringCore(string name)
    {
        var def = Find(name) ?? throw new ArgumentException($"unknown setting {name}", nameof(name));
        string? stored = null;
        try
        {
            stored = Accessor.GetValueString(def.Name, string.Empty);
        }
        catch
        {
            // profile not ready
        }

        if (!string.IsNullOrEmpty(stored))
        {
            return stored;
        }

        if (def.Name == "GuideSource" && IsLegacySimulator())
        {
            return SimulatorSource;
        }

        try
        {
            var fb = def.Phd2Fallback?.Invoke(profileService.ActiveProfile.GuiderSettings);
            if (!string.IsNullOrEmpty(fb) && Validate(def, fb, out var normalized, out _))
            {
                return normalized;
            }
        }
        catch
        {
            // ignore fallback errors
        }

        return def.Default;
    }

    public double GetDouble(string name) => double.TryParse(GetString(name), NumberStyles.Float, Inv, out var v) ? v : double.Parse(Find(name)!.Default, Inv);

    public int GetInt(string name) => int.TryParse(GetString(name), NumberStyles.Integer, Inv, out var v) ? v : int.Parse(Find(name)!.Default, Inv);

    public bool GetBool(string name) => bool.TryParse(GetString(name), out var v) ? v : bool.Parse(Find(name)!.Default);

    public bool TrySet(string name, string value, out string error)
    {
        var def = Find(name);
        if (def is null)
        {
            error = $"Unknown setting '{name}'";
            return false;
        }

        if (!Validate(def, value, out var normalized, out error))
        {
            return false;
        }

        if (def.Type == "action")
        {
            // nothing to store: the guider runs the action
            return true;
        }

        lock (gate)
        {
            Accessor.SetValueString(def.Name, normalized);
        }

        return true;
    }

    private static bool Validate(SettingDef def, string value, out string normalized, out string error)
    {
        value = (value ?? string.Empty).Trim();
        normalized = value;
        error = string.Empty;
        switch (def.Type)
        {
            case "double":
                if (!double.TryParse(value.Replace(',', '.'), NumberStyles.Float, Inv, out var d) || double.IsNaN(d))
                {
                    error = $"{def.Label}: '{value}' is not a number";
                    return false;
                }

                if ((def.Min is { } min && d < min) || (def.Max is { } max && d > max))
                {
                    error = $"{def.Label} must be between {def.Min} and {def.Max}";
                    return false;
                }

                normalized = d.ToString(Inv);
                return true;
            case "int":
                if (!int.TryParse(value, NumberStyles.Integer, Inv, out var i))
                {
                    error = $"{def.Label}: '{value}' is not an integer";
                    return false;
                }

                if ((def.Min is { } imin && i < imin) || (def.Max is { } imax && i > imax))
                {
                    error = $"{def.Label} must be between {def.Min} and {def.Max}";
                    return false;
                }

                normalized = i.ToString(Inv);
                return true;
            case "bool":
                if (!bool.TryParse(value, out var b))
                {
                    if (value is "1" or "0")
                    {
                        b = value == "1";
                    }
                    else
                    {
                        error = $"{def.Label}: '{value}' is not true/false";
                        return false;
                    }
                }

                normalized = b ? "true" : "false";
                return true;
            case "enum":
            case "action" when def.Options is not null:
                var match = def.Options?.FirstOrDefault(o => string.Equals(o, value, StringComparison.OrdinalIgnoreCase));
                if (match is null)
                {
                    error = $"{def.Label} must be one of {string.Join(", ", def.Options ?? [])}";
                    return false;
                }

                normalized = match;
                return true;
            default:
                return true;
        }
    }

    public IReadOnlyList<AdvancedGuiderSetting> Describe() => Definitions.Select(d => new AdvancedGuiderSetting
    {
        Name = d.Name,
        Label = d.Label,
        Group = d.Group,
        Description = d.Description,
        Basic = d.Basic,
        Type = d.Type,
        Value = GetString(d.Name),
        DefaultValue = d.Default,
        Min = d.Min,
        Max = d.Max,
        Unit = d.Unit,
        Options = d.Options?.ToList(),
        RequiresReconnect = d.RequiresReconnect,
        DependsOn = d.DependsOn ?? string.Empty,
        AppliesTo = d.AppliesTo?.ToList(),
    }).ToList();

    /// <summary>Names of the settings shown in the basic view.</summary>
    public static IReadOnlyCollection<string> BasicSettings { get; } = Definitions.Where(d => d.Basic).Select(d => d.Name).ToHashSet();

    public bool IsSimulator => GetString("GuideSource") == SimulatorSource;

    /// <summary>The plugin chose the simulator with its guide camera driver setting (GuideCameraDriver=simulator).</summary>
    private bool IsLegacySimulator()
    {
        try
        {
            return string.Equals(Accessor.GetValueString("GuideCameraDriver", string.Empty), "simulator", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            // profile not ready
            return false;
        }
    }

    public bool UseCameraSt4 => GetString("PulseOutput") == "CameraST4";

    public bool SaveGuideLog => GetBool("SaveGuideLog");

    public bool UseDarkLibrary => GetBool("UseDarkLibrary");

    public bool ReuseCalibration => GetBool("ReuseCalibration");

    public double FocalLengthMm => GetDouble("FocalLengthMm");

    public bool IncidentRecorder => GetBool("IncidentRecorder");

    /// <summary>Disk budget of the incidents recorded with real equipment.</summary>
    public long IncidentBudgetBytes => GetInt("IncidentBudgetMb") * IncidentStoreOptions.MB;

    /// <summary>The simulator preset chosen in the settings (GoodMount when unknown).</summary>
    public SimulatorScenario SimulatorScenario =>
        SimulatorScenario.Presets.FirstOrDefault(p => string.Equals(p.Name, GetString("SimulatorScenario"), StringComparison.OrdinalIgnoreCase))
        ?? SimulatorScenario.GoodMount;

    /// <summary>Builds the engine settings. Unset min-move values use PHD2's smart default from the image scale.</summary>
    public GuiderSettings ToEngineSettings()
    {
        double minMoveRa = GetDouble("RaMinMove");
        double minMoveDec = GetDouble("DecMinMove");
        int stepMs = GetInt("CalibrationStepMs");
        int maxStars = GetInt("MaxStars");
        bool multiStar = GetBool("MultiStar");

        return new GuiderSettings
        {
            ExposureMs = GetDouble("ExposureSeconds") * 1000.0,
            Binning = GetInt("Binning"),
            Gain = GetInt("Gain") is var g && g >= 0 ? g : null,
            Offset = GetInt("Offset") is var o && o >= 0 ? o : null,
            FocalLengthMm = GetDouble("FocalLengthMm"),
            PixelSizeUm = GetDouble("PixelSizeUm"),
            NoiseReduction = GetString("NoiseReduction") switch
            {
                "Mean2x2" => GuideEngine.Imaging.NoiseReduction.Mean2x2,
                "Median3x3" => GuideEngine.Imaging.NoiseReduction.Median3x3,
                _ => GuideEngine.Imaging.NoiseReduction.None,
            },
            StarFinder = new StarFinderOptions
            {
                SearchRegion = GetInt("SearchRegion"),
                MinHfd = GetDouble("MinHfd"),
                MaxHfd = GetDouble("MaxHfd"),
                MinSnr = GetDouble("MinSnr"),
                SaturationAdu = (ushort)Math.Clamp(GetInt("SaturationAdu"), 0, 65535),
                MaxStars = multiStar ? maxStars : 1,
            },
            MultiStar = new MultiStarOptions
            {
                MultiStarEnabled = multiStar && maxStars > 1,
                MassChangeThresholdEnabled = GetBool("MassChangeEnabled"),
                MassChangeThreshold = GetDouble("MassChangeThreshold"),
                PrimaryDropoutFallback = GetBool("PrimaryDropoutFallback"),
            },
            Calibration = new CalibrationSettings
            {
                StepMs = stepMs > 0 ? stepMs : CalibrationStepCalculator.DefaultCalibrationDurationMs,
                AssumeOrthogonal = GetBool("AssumeOrthogonal"),
            },
            AutoCalibrationStep = stepMs <= 0,
            RaAlgorithm = Algorithm(GetString("RaAlgorithm"), GuideAxis.Ra, minMoveRa),
            DecAlgorithm = Algorithm(GetString("DecAlgorithm"), GuideAxis.Dec, minMoveDec),
            DecGuideMode = Enum.TryParse<DecGuideMode>(GetString("DecGuideMode"), out var mode) ? mode : DecGuideMode.Auto,
            MaxRaDurationMs = GetInt("MaxRaDurationMs"),
            MaxDecDurationMs = GetInt("MaxDecDurationMs"),
            MinPulseMs = GetInt("MinPulseMs"),
            SimultaneousPulses = GetBool("SimultaneousPulses"),
            Backlash = new BacklashSettings { Enabled = GetBool("BacklashCompensation"), PulseMs = GetInt("BacklashPulseMs") },
            PulseModel = GetBool("PulseModel"),
            DitherMode = GetString("DitherMode") == "Spiral" ? DitherMode.Spiral : DitherMode.Random,
            DitherScale = GetDouble("DitherScale"),
            DecFlipRequired = GetBool("DecFlipRequired"),
            VerifyDecAfterFlip = GetBool("VerifyDecAfterFlip"),
            DecCompensation = GetBool("DecCompensation"),
            FastRecenter = GetBool("FastRecenter"),
            Safety = new SafetySettings
            {
                ReacquireTimeoutSec = GetInt("ReacquireTimeoutSec"),
                MaxPulseDuty = GetInt("MaxPulseDutyPercent") / 100.0,
            },
            Incidents = new IncidentSettings { Enabled = GetBool("IncidentRecorder") },
        };
    }

    private AlgorithmSettings Algorithm(string name, GuideAxis axis, double minMove)
    {
        if (name == "Predictive")
        {
            // no PHD2 min move: a dead band only adds error to its filtered estimate; RA also learns the periodic error
            return axis == GuideAxis.Ra
                ? new AlgorithmSettings(GuideAlgorithmKind.Predictive, new Dictionary<string, double>
                {
                    ["pace"] = GetDouble("RaCorrectionPace"),
                    ["periodicError"] = GetBool("PeriodicErrorPrediction") ? 1 : 0,
                    ["wormTeeth"] = GetInt("WormTeeth"),
                })
                : new AlgorithmSettings(GuideAlgorithmKind.Predictive, new Dictionary<string, double>
                {
                    ["pace"] = GetDouble("DecCorrectionPace"),
                });
        }

        string prefix = axis == GuideAxis.Ra ? "Ra" : "Dec";
        var p = new Dictionary<string, double>();
        if (minMove > 0)
        {
            p["minMove"] = minMove;
        }

        switch (name)
        {
            case "Hysteresis":
                p["aggression"] = GetDouble(prefix + "Aggression");
                p["hysteresis"] = GetDouble(prefix + "Hysteresis");
                return new AlgorithmSettings(GuideAlgorithmKind.Hysteresis, p);
            case "Lowpass2":
                p["aggressiveness"] = GetDouble(prefix + "Lowpass2Aggressiveness");
                return new AlgorithmSettings(GuideAlgorithmKind.Lowpass2, p);
            case "Lowpass":
                return new AlgorithmSettings(GuideAlgorithmKind.Lowpass, p);
            case "ResistSwitch":
                p["aggression"] = GetDouble(prefix + "Aggression");
                p["fastSwitch"] = GetBool("DecFastSwitch") ? 1 : 0;
                return new AlgorithmSettings(GuideAlgorithmKind.ResistSwitch, p);
            default:
                // "None" has no parameters (PHD2's Identity ignores a min move)
                return new AlgorithmSettings(GuideAlgorithmKind.Identity);
        }
    }

    private static string? Bool(bool? b) => b is null ? null : b.Value ? "true" : "false";

    private static string? AlgorithmName(string? phd2Name, string[] allowed)
    {
        if (string.IsNullOrWhiteSpace(phd2Name))
        {
            return null;
        }

        var n = phd2Name.Replace(" ", string.Empty).Replace("-", string.Empty);
        return allowed.FirstOrDefault(a => string.Equals(a, n, StringComparison.OrdinalIgnoreCase));
    }
}
