// SPDX-License-Identifier: MPL-2.0

using System.Globalization;

namespace NINA.GuideEngine.Coach;

/// <summary>Creates findings with English fallback messages (UIs render localised texts from the code and parameters).</summary>
public static class CoachFindings
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Creates a finding. <paramref name="qualifier"/> makes the id unique for repeated codes (id = code:qualifier).</summary>
    public static CoachFinding Create(string code, string step, string severity, DateTime timestamp, Dictionary<string, object?>? parameters = null,
        double? impactArcsec = null, IReadOnlyList<CoachSettingChange>? changes = null, string? qualifier = null, DateTime? expiresAt = null)
    {
        parameters ??= new Dictionary<string, object?>();
        return new CoachFinding
        {
            Id = qualifier is null ? code : $"{code}:{qualifier}",
            Code = code,
            Step = step,
            Severity = severity,
            Parameters = parameters,
            ImpactArcsec = impactArcsec is { } i && double.IsFinite(i) ? Math.Round(Math.Max(0, i), 3) : null,
            Changes = changes ?? [],
            Message = MessageFor(code, parameters),
            Timestamp = timestamp,
            ExpiresAt = expiresAt,
        };
    }

    /// <summary>English fallback text of a code.</summary>
    public static string MessageFor(string code, IReadOnlyDictionary<string, object?> p)
    {
        string V(string key, string format = "0.##") => p.TryGetValue(key, out var v) ? v switch
        {
            null => "unknown",
            double d => d.ToString(format, Inv),
            float f => f.ToString(format, Inv),
            int i => i.ToString(Inv),
            bool b => b ? "yes" : "no",
            _ => Convert.ToString(v, Inv) ?? string.Empty,
        } : "?";

        return code switch
        {
            CoachCodes.Busy => "Another Guiding Coach session is running or the guider is busy.",
            CoachCodes.NotConnected => "The guide camera is not connected.",
            CoachCodes.NoStar => "No usable guide star was found.",
            CoachCodes.NoCalibration => "This step needs a calibration; calibrating was not allowed.",
            CoachCodes.CalibrationFailed => "Calibration failed; steps that need a calibration were skipped.",
            CoachCodes.StarLost => "The guide star was lost during the measurement.",
            CoachCodes.NoPulseOutput => "No guide output (mount pulse guiding) is connected.",
            CoachCodes.Interrupted => $"The session was interrupted ({V("reason")}).",
            CoachCodes.CameraError => "The guide camera failed during the camera check.",
            CoachCodes.Internal => "Internal error in the Guiding Coach.",
            CoachCodes.CameraRecommendation =>
                $"Best camera settings: {V("exposureSeconds")} s at gain {V("gain")} (centroid jitter {V("jitterArcsec")}″, SNR {V("snr", "0")}, {V("stars")} stars).",
            CoachCodes.CameraGood => $"The current camera settings are as good as any measured, within the noise (SNR {V("snr", "0")}, jitter {V("jitterArcsec")}″).",
            CoachCodes.CameraNoFeasible => $"No exposure/gain combination gave an unsaturated star with SNR ≥ 15 ({V("reason")}).",
            CoachCodes.CameraSnrLow => $"Star SNR is low ({V("snr", "0")}): centroid noise adds about {V("noiseArcsec")}″ RMS. Use a longer exposure, higher gain, darks or focus.",
            CoachCodes.CameraSaturated => $"The current settings ({V("exposureSeconds")} s, gain {V("gain")}) saturate the guide star; lower gain or exposure.",
            CoachCodes.CameraDefocused => $"Guide stars are large (HFD {V("hfdPx", "0.0")} px, {V("hfdArcsec", "0.0")}″): focus the guide camera.",
            CoachCodes.CameraFewStars => $"Only {V("stars")} usable stars: multi-star guiding cannot average the seeing.",
            CoachCodes.CameraNoDarks => "No dark library: hot pixels can be mistaken for stars. Build a dark library.",
            CoachCodes.DriftSeeing => $"Seeing (high-frequency star motion) is {V("rmsArcsec")}″ RMS ({V("level")}); guiding cannot beat this floor.",
            CoachCodes.DriftMinMove => $"Suggested min-move from the seeing: RA {V("raPx")} px, Dec {V("decPx")} px.",
            CoachCodes.DriftPeriodicError when p.TryGetValue("learned", out var learned) && learned is true =>
                $"RA periodic error ±{V("amplitudeArcsec")}″ with a period of {V("periodSeconds", "0.0")} s{(p.TryGetValue("wormTeeth", out var teeth) && teeth is not null ? $" ({V("wormTeeth")} teeth)" : "")}, learned by Predictive guiding{(p.TryGetValue("predicting", out var predicting) && predicting is true ? ", which corrects it in advance" : "")}; max rate {V("maxRateArcsecPerSec", "0.###")}″/s.",
            CoachCodes.DriftPeriodicError => p.TryGetValue("periodSeconds", out var period) && period is not null
                ? $"RA periodic error ±{V("amplitudeArcsec")}″ with a period of {V("periodSeconds", "0")} s, max rate {V("maxRateArcsecPerSec", "0.###")}″/s."
                : $"RA periodic error about ±{V("amplitudeArcsec")}″ (period longer than the measurement), max rate {V("maxRateArcsecPerSec", "0.###")}″/s.",
            CoachCodes.DriftExposureLimit => $"Longest exposure before the RA drift exceeds the seeing: {V("seconds", "0.0")} s (current exposure {V("currentSeconds", "0.0")} s).",
            CoachCodes.DriftPolarAlignment => $"Polar alignment error about {V("arcmin", "0.0")}′ (Dec drift {V("driftArcsecPerMin")}″/min).",
            CoachCodes.DriftWind => $"{V("gustPercent", "0.#")} % of the frames show sudden jumps (wind, vibration).",
            CoachCodes.DriftDecGuideMode => $"Dec drifts steadily ({V("driftArcsecPerMin")}″/min) and backlash is large ({V("backlashMs", "0")} ms): guide Dec in one direction only, following the drift (Dec guide mode {V("mode")}).",
            CoachCodes.ResponseDecBacklash when p.TryGetValue("playModel", out var playModel) && playModel is true =>
                (p.TryGetValue("playReason", out var reason) && reason is "soft"
                    ? $"Dec backlash varies with the preceding move (about {V("ms", "0")} ms after small corrections, {V("largeMoveMs", "0")} ms after a long move)"
                    : $"Dec backlash is large (about {V("ms", "0")} ms at guiding reversals, {V("largeMoveMs", "0")} ms after a long move)")
                + ": the play model tracks the gear inside its play and compensates what is left.",
            CoachCodes.ResponseDecBacklash when p.TryGetValue("playModelNeedsPredictive", out var needsPredictive) && needsPredictive is true =>
                $"Dec backlash at guiding reversals about {V("ms", "0")} ms ({V("arcsec", "0.0")}″); after a long move {V("largeMoveMs", "0")} ms. The play model would fit this backlash but needs the Predictive Dec algorithm.",
            CoachCodes.ResponseDecBacklash => $"Dec backlash at guiding reversals about {V("ms", "0")} ms ({V("arcsec", "0.0")}″); after a long move {V("largeMoveMs", "0")} ms.",
            CoachCodes.ResponseMinPulse => $"{V("axis")} pulses shorter than {V("ms", "0")} ms hardly move the mount (stiction).",
            CoachCodes.ResponseAsymmetry => $"{V("axis")} moves asymmetrically (ratio {V("ratio")}).",
            CoachCodes.ResponseRateMismatch => $"{V("axis")} moves at {V("ratio")}× the calibrated rate; recalibrate.",
            CoachCodes.ResponseGood => "The mount responds cleanly to guide pulses.",
            CoachCodes.TrialsWinner => $"Trial {V("id")} guided best: {V("rmsArcsec")}″ vs {V("baselineRmsArcsec")}″ ({V("improvementPercent", "0")} % better).",
            CoachCodes.TrialsNoImprovement => $"The current settings guided best ({V("rmsArcsec")}″).",
            CoachCodes.TrialsNothingToTry => $"No setting worth a trial; the current settings guided at {V("rmsArcsec")}″.",
            CoachCodes.TrialsConditionsChanged => $"Conditions changed during the trials (baseline {V("changePercent", "0")} % different); the comparison is unreliable.",
            CoachCodes.ReportSeeingLimited => $"Seeing ({V("seeingArcsec")}″) dominates the guided RMS ({V("guidedArcsec")}″): the settings are fine.",
            CoachCodes.ReportMountLimited => $"The mount contributes {V("mountArcsec")}″ of the guided {V("guidedArcsec")}″: see the mount findings.",
            CoachCodes.ReportNoImagingScale => "Set the imaging camera pixel size and telescope focal length for a grade relative to the image scale.",
            CoachCodes.HintRaOscillation => $"RA over-corrects (oscillation index {V("index")}): lower RA aggression.",
            CoachCodes.HintRaSluggish => $"RA corrects too little (oscillation index {V("index")}, RA RMS {V("rmsRaArcsec")}″): raise RA aggression.",
            CoachCodes.HintPulseLimited => $"{V("percent", "0")} % of the {V("axis")} pulses hit the max pulse duration.",
            CoachCodes.HintLowSnr => $"The guide star SNR dropped to {V("snr", "0")} (clouds, dew?).",
            CoachCodes.HintDecDrift => $"Dec corrections are one-sided (drift {V("driftArcsecPerMin")}″/min, polar alignment about {V("arcmin", "0.0")}′).",
            CoachCodes.HintSeeingBound => $"Guiding RMS ({V("rmsArcsec")}″) is close to the seeing floor: nothing to tune.",
            _ => code,
        };
    }
}
