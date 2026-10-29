// SPDX-License-Identifier: MPL-2.0

using System.Globalization;
using NINA.GuideEngine.Algorithms;
using NINA.GuideEngine.Core;
using NINA.GuideEngine.Guiding;

namespace NINA.GuideEngine.Coach;

/// <summary>Plugin setting names used in <see cref="CoachSettingChange.Name"/> (AdvancedGuiderSetting.Name).</summary>
public static class CoachSettingNames
{
    public const string ExposureSeconds = "ExposureSeconds";
    public const string Gain = "Gain";
    public const string Binning = "Binning";
    public const string MultiStar = "MultiStar";
    public const string RaAggression = "RaAggression";
    public const string RaHysteresis = "RaHysteresis";
    public const string RaMinMove = "RaMinMove";
    public const string DecAggression = "DecAggression";
    public const string DecMinMove = "DecMinMove";
    public const string DecGuideMode = "DecGuideMode";
    public const string DecAlgorithm = "DecAlgorithm";
    public const string BacklashCompensation = "BacklashCompensation";
    public const string BacklashPulseMs = "BacklashPulseMs";
    public const string BacklashModel = "BacklashModel";
    public const string MaxRaDurationMs = "MaxRaDurationMs";
    public const string MaxDecDurationMs = "MaxDecDurationMs";

    public static IReadOnlyList<string> All { get; } =
    [
        ExposureSeconds, Gain, Binning, MultiStar, RaAggression, RaHysteresis, RaMinMove, DecAggression, DecMinMove, DecGuideMode, DecAlgorithm,
        BacklashCompensation, BacklashPulseMs, BacklashModel, MaxRaDurationMs, MaxDecDurationMs,
    ];
}

/// <summary>
/// Maps the plugin setting names of coach recommendations to and from <see cref="GuiderSettings"/>, following the plugin's
/// conventions (min-move 0 = automatic, gain -1 = driver default). Used to apply trial settings temporarily and to read
/// current values; persisting applied changes is the host's job.
/// </summary>
public static class CoachSettingsMap
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Invariant-culture text of a number as used in setting values.</summary>
    public static string Format(double value) => Math.Round(value, 4).ToString("0.####", Inv);

    public static string Format(int value) => value.ToString(Inv);

    public static string Format(bool value) => value ? "true" : "false";

    /// <summary>Current value of a plugin setting in <paramref name="s"/>, null for unknown names.</summary>
    public static string? GetValue(GuiderSettings s, string name)
    {
        ArgumentNullException.ThrowIfNull(s);
        return name switch
        {
            CoachSettingNames.ExposureSeconds => Format(s.ExposureMs / 1000.0),
            CoachSettingNames.Gain => Format(s.Gain ?? -1),
            CoachSettingNames.Binning => Format(s.Binning),
            CoachSettingNames.MultiStar => Format(s.MultiStar.MultiStarEnabled),
            CoachSettingNames.RaAggression => FormatParam(s.RaAlgorithm, GuideAxis.Ra, "aggression"),
            CoachSettingNames.RaHysteresis => FormatParam(s.RaAlgorithm, GuideAxis.Ra, "hysteresis"),
            CoachSettingNames.RaMinMove => Format(ExplicitMinMove(s.RaAlgorithm)),
            CoachSettingNames.DecAggression => FormatParam(s.DecAlgorithm, GuideAxis.Dec, "aggression"),
            CoachSettingNames.DecMinMove => Format(ExplicitMinMove(s.DecAlgorithm)),
            CoachSettingNames.DecGuideMode => s.DecGuideMode.ToString(),
            CoachSettingNames.DecAlgorithm => AlgorithmName(s.DecAlgorithm.Kind),
            CoachSettingNames.BacklashCompensation => Format(s.Backlash.Enabled),
            CoachSettingNames.BacklashPulseMs => Format(s.Backlash.PulseMs),
            CoachSettingNames.BacklashModel => s.Backlash.Model.ToString(),
            CoachSettingNames.MaxRaDurationMs => Format(s.MaxRaDurationMs),
            CoachSettingNames.MaxDecDurationMs => Format(s.MaxDecDurationMs),
            _ => null,
        };
    }

    /// <summary>Numeric value of an algorithm parameter (explicit, else the algorithm's default); null when the algorithm has none.</summary>
    public static double? GetParameter(AlgorithmSettings a, GuideAxis axis, string param)
    {
        if (a.Parameters is { } p && p.TryGetValue(param, out var v))
        {
            return v;
        }

        try
        {
            var alg = GuideAlgorithmFactory.Create(a.Kind, axis);
            return alg.TryGetParam(param, out var d) ? d : null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Applies changes; throws <see cref="ArgumentException"/> for unknown names or invalid values.</summary>
    public static GuiderSettings Apply(GuiderSettings s, IEnumerable<CoachSettingChange> changes)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(changes);
        foreach (var c in changes)
        {
            s = ApplyOne(s, c.Name, c.Value);
        }

        return s;
    }

    /// <summary>Applies changes; false with an error for unknown names or invalid values.</summary>
    public static bool TryApply(GuiderSettings s, IEnumerable<CoachSettingChange> changes, out GuiderSettings result, out string? error)
    {
        try
        {
            result = Apply(s, changes);
            error = null;
            return true;
        }
        catch (ArgumentException ex)
        {
            result = s;
            error = ex.Message;
            return false;
        }
    }

    private static GuiderSettings ApplyOne(GuiderSettings s, string name, string value)
    {
        switch (name)
        {
            case CoachSettingNames.ExposureSeconds:
                return s with { ExposureMs = Positive(name, value) * 1000.0 };
            case CoachSettingNames.Gain:
                int gain = Int(name, value);
                return s with { Gain = gain >= 0 ? gain : null };
            case CoachSettingNames.Binning:
                return s with { Binning = Math.Max(1, Int(name, value)) };
            case CoachSettingNames.MultiStar:
                return s with { MultiStar = s.MultiStar with { MultiStarEnabled = Bool(name, value) } };
            case CoachSettingNames.RaAggression:
                return s with { RaAlgorithm = WithParam(s.RaAlgorithm, "aggression", Double(name, value)) };
            case CoachSettingNames.RaHysteresis:
                return s with { RaAlgorithm = WithParam(s.RaAlgorithm, "hysteresis", Double(name, value)) };
            case CoachSettingNames.RaMinMove:
                return s with { RaAlgorithm = WithMinMove(s.RaAlgorithm, Double(name, value)) };
            case CoachSettingNames.DecAggression:
                return s with { DecAlgorithm = WithParam(s.DecAlgorithm, "aggression", Double(name, value)) };
            case CoachSettingNames.DecMinMove:
                return s with { DecAlgorithm = WithMinMove(s.DecAlgorithm, Double(name, value)) };
            case CoachSettingNames.DecGuideMode:
                if (!Enum.TryParse<DecGuideMode>(value, true, out var mode) || !Enum.IsDefined(mode))
                {
                    throw new ArgumentException($"{name}: invalid value '{value}'");
                }

                return s with { DecGuideMode = mode };
            case CoachSettingNames.DecAlgorithm:
                return s with { DecAlgorithm = WithKind(s.DecAlgorithm, ParseAlgorithm(name, value)) };
            case CoachSettingNames.BacklashCompensation:
                return s with { Backlash = s.Backlash with { Enabled = Bool(name, value) } };
            case CoachSettingNames.BacklashPulseMs:
                return s with { Backlash = s.Backlash with { PulseMs = Math.Max(0, Int(name, value)) } };
            case CoachSettingNames.BacklashModel:
                if (!Enum.TryParse<BacklashModel>(value, true, out var model) || !Enum.IsDefined(model))
                {
                    throw new ArgumentException($"{name}: invalid value '{value}'");
                }

                return s with { Backlash = s.Backlash with { Model = model } };
            case CoachSettingNames.MaxRaDurationMs:
                return s with { MaxRaDurationMs = Math.Max(1, Int(name, value)) };
            case CoachSettingNames.MaxDecDurationMs:
                return s with { MaxDecDurationMs = Math.Max(1, Int(name, value)) };
            default:
                throw new ArgumentException($"unknown setting '{name}'");
        }
    }

    private static string? FormatParam(AlgorithmSettings a, GuideAxis axis, string param) =>
        GetParameter(a, axis, param) is { } v ? Format(v) : null;

    private static double ExplicitMinMove(AlgorithmSettings a) =>
        a.Parameters is { } p && p.TryGetValue("minMove", out var v) && v > 0 ? v : 0.0;

    private static AlgorithmSettings WithParam(AlgorithmSettings a, string param, double value)
    {
        var p = a.Parameters is null ? new Dictionary<string, double>() : new Dictionary<string, double>(a.Parameters);
        p[param] = value;
        return a with { Parameters = p };
    }

    private static AlgorithmSettings WithMinMove(AlgorithmSettings a, double value)
    {
        var p = a.Parameters is null ? new Dictionary<string, double>() : new Dictionary<string, double>(a.Parameters);
        if (value > 0)
        {
            p["minMove"] = value;
        }
        else
        {
            // plugin convention: 0 = automatic (smart default from the image scale)
            p.Remove("minMove");
        }

        return a with { Parameters = p };
    }

    private static AlgorithmSettings WithKind(AlgorithmSettings a, GuideAlgorithmKind kind)
    {
        if (a.Kind == kind)
        {
            return a;
        }

        // keep the parameters the new algorithm understands (min-move, aggression)
        var p = new Dictionary<string, double>();
        if (a.Parameters is { } old)
        {
            foreach (var key in new[] { "minMove", "aggression" })
            {
                if (old.TryGetValue(key, out var v))
                {
                    p[key] = v;
                }
            }
        }

        return new AlgorithmSettings(kind, p);
    }

    private static GuideAlgorithmKind ParseAlgorithm(string name, string value) => value switch
    {
        "ResistSwitch" => GuideAlgorithmKind.ResistSwitch,
        "Hysteresis" => GuideAlgorithmKind.Hysteresis,
        "Lowpass2" => GuideAlgorithmKind.Lowpass2,
        "Lowpass" => GuideAlgorithmKind.Lowpass,
        "None" or "Identity" => GuideAlgorithmKind.Identity,
        // what GetValue reports for it, so that a session can put a Predictive axis back
        "Predictive" => GuideAlgorithmKind.Predictive,
        _ => throw new ArgumentException($"{name}: invalid value '{value}'"),
    };

    private static string AlgorithmName(GuideAlgorithmKind kind) => kind switch
    {
        GuideAlgorithmKind.Identity or GuideAlgorithmKind.None => "None",
        _ => kind.ToString(),
    };

    private static double Double(string name, string value) =>
        double.TryParse(value, NumberStyles.Float, Inv, out var d) && double.IsFinite(d) ? d : throw new ArgumentException($"{name}: invalid number '{value}'");

    private static double Positive(string name, string value)
    {
        double d = Double(name, value);
        return d > 0 ? d : throw new ArgumentException($"{name}: must be positive");
    }

    private static int Int(string name, string value) =>
        int.TryParse(value, NumberStyles.Integer, Inv, out var i) ? i : throw new ArgumentException($"{name}: invalid integer '{value}'");

    private static bool Bool(string name, string value) =>
        bool.TryParse(value, out var b) ? b : throw new ArgumentException($"{name}: invalid boolean '{value}'");
}
