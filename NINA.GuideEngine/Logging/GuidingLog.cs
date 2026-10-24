// SPDX-License-Identifier: MPL-2.0 AND BSD-3-Clause
// Copyright (c) 2012-2013 Bret McKee
// Copyright (c) 2006-2010 Craig Stark.
// All rights reserved.
// Ported from PHD2 src/guidinglog.cpp (a6c02722); header summary formats from src/myframe.cpp,
// src/guider_multistar.cpp, src/camera.cpp, src/mount.cpp, src/scope.cpp and src/guide_algorithm_*.cpp.

using System.Globalization;
using NINA.GuideEngine.Calibration;
using NINA.GuideEngine.Core;
using NINA.GuideEngine.Stars;

namespace NINA.GuideEngine.Logging;

/// <summary>Guide algorithm name and settings summary as printed in the guide log header.</summary>
/// <param name="Name">PHD2 algorithm name (Hysteresis, Lowpass, Lowpass2, Resist Switch, Predictive PEC, ZFilter, None).</param>
/// <param name="Summary">Settings summary without trailing newline.</param>
public sealed record GuideLogAlgorithm(string Name, string Summary)
{
    public static GuideLogAlgorithm None { get; } = new("None", "");

    public static GuideLogAlgorithm Hysteresis(double hysteresis, double aggression, double minMove) =>
        new("Hysteresis", Inv($"Hysteresis = {hysteresis:F3}, Aggression = {aggression:F3}, Minimum move = {minMove:F3}"));

    /// <param name="aggression">Aggression as a fraction (1.0 = 100 %).</param>
    public static GuideLogAlgorithm ResistSwitch(double minMove, double aggression, bool fastSwitch) =>
        new("Resist Switch", Inv($"Minimum move = {minMove:F3} Aggression = {aggression * 100.0:F0}% FastSwitch = {(fastSwitch ? "enabled" : "disabled")}"));

    public static GuideLogAlgorithm Lowpass2(double aggressiveness, double minMove) =>
        new("Lowpass2", Inv($"Aggressiveness = {aggressiveness:F3}, Minimum move = {minMove:F3}"));

    public static GuideLogAlgorithm Lowpass(double slopeWeight, double minMove) =>
        new("Lowpass", Inv($"Slope weight = {slopeWeight:F3}, Minimum move = {minMove:F3}"));

    private static string Inv(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
}

/// <summary>Options for <see cref="GuidingLog"/>.</summary>
public sealed record GuidingLogOptions
{
    /// <summary>Application/version text written after "PHD2 version " in the first line.</summary>
    public string AppVersion { get; init; } = "InternalGuider";

    /// <summary>OS name written in brackets in the first line.</summary>
    public string OsName { get; init; } = "Linux";

    /// <summary>Time zone for the wall-clock timestamps (PHD2 logs local time). Default: local time zone.</summary>
    public TimeZoneInfo? TimeZone { get; init; }

    /// <summary>Flush the writer after every entry, like PHD2 does.</summary>
    public bool AutoFlush { get; init; } = true;
}

/// <summary>
/// PHD2-compatible guide log writer (port of PHD2 GuidingLog). Produces the PHD2 guide log text
/// format (log version 2.5) so tools like PHD2LogViewer can analyse sessions. Writes to a
/// host-supplied <see cref="TextWriter"/>; all numbers use the invariant culture and lines end in
/// "\n". Thread-safe.
/// </summary>
/// <remarks>
/// Deviation from PHD2: no file handling (the host owns the writer, see <see cref="FileName"/> for the
/// naming convention), no AO/step-guider rows, and the summary block is always written on close.
/// </remarks>
public sealed class GuidingLog
{
    /// <summary>Guide log format version (PHD2 GUIDELOG_VERSION).</summary>
    public const string LogVersion = "2.5";

    /// <summary>File name prefix (PHD2 uses "PHD2_GuideLog_").</summary>
    public const string FilePrefix = "InternalGuider_GuideLog_";

    public const string ColumnHeader =
        "Frame,Time,mount,dx,dy,RARawDistance,DECRawDistance,RAGuideDistance,DECGuideDistance,"
        + "RADuration,RADirection,DECDuration,DECDirection,XStep,YStep,StarMass,SNR,ErrorCode";

    public const string CalibrationColumnHeader = "Direction,Step,dx,dy,x,y,Dist";

    /// <summary>Settling messages PHD2 uses with <see cref="NotifySettlingStateChange"/>.</summary>
    public const string SettlingStarted = "Settling started";

    public const string SettlingComplete = "Settling complete";

    public const string SettlingFailed = "Settling failed";

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private readonly object gate = new();
    private readonly TextWriter writer;
    private readonly IClock clock;
    private readonly GuidingLogOptions options;
    private readonly TimeZoneInfo timeZone;
    private bool enabled;
    private bool isGuiding;
    private bool isCalibrating;
    private DateTimeOffset guidingStarted;
    private int calibrationCount;
    private int guideCount;
    private double guideDurationSec;

    // the settings lines of the last guiding header, to log what changes while guiding
    private IReadOnlyList<string> loggedSettings = [];

    public GuidingLog(TextWriter writer, IClock clock, GuidingLogOptions? options = null)
    {
        this.writer = writer;
        this.clock = clock;
        this.options = options ?? new GuidingLogOptions();
        timeZone = this.options.TimeZone ?? TimeZoneInfo.Local;
    }

    public bool IsEnabled
    {
        get
        {
            lock (gate) return enabled;
        }
    }

    /// <summary>PHD2 guide log file name for a log started at <paramref name="localTime"/>, e.g. InternalGuider_GuideLog_2026-09-23_213000.txt.</summary>
    public static string FileName(DateTime localTime, int instanceNumber = 1)
    {
        string qualifier = instanceNumber > 1 ? instanceNumber.ToString(Inv) + "_" : "";
        return FilePrefix + qualifier + localTime.ToString("yyyy-MM-dd_HHmmss", Inv) + ".txt";
    }

    /// <summary>PHD2 star error code for a star find result.</summary>
    public static int ErrorCode(StarFindResult result) => (int)result;

    /// <summary>PHD2 status text for a lost star (StarStatusStr).</summary>
    public static string StarLostStatus(StarFindResult result) => result switch
    {
        StarFindResult.LowSnr => "Star lost - low SNR",
        StarFindResult.LowMass => "Star lost - low mass",
        StarFindResult.LowHfd => "Star lost - low HFD",
        StarFindResult.TooNearEdge => "Star too near edge",
        StarFindResult.MassChange => "Star lost - mass changed",
        _ => "No star found",
    };

    /// <summary>Starts logging: writes the version line (PHD2 EnableLogging).</summary>
    public void EnableLogging()
    {
        lock (gate)
        {
            if (enabled) return;
            Write($"PHD2 version {options.AppVersion} [{options.OsName}], Log version {LogVersion}. Log enabled at {Now()}\n");
            enabled = true;
            Flush();
        }
    }

    /// <summary>Stops logging (PHD2 DisableLogging).</summary>
    public void DisableLogging()
    {
        lock (gate)
        {
            if (!enabled) return;
            Write("\n");
            Write($"Log disabled at {Now()}\n");
            Flush();
            enabled = false;
        }
    }

    /// <summary>Writes the summary and the closing line (PHD2 CloseGuideLog).</summary>
    public void Close()
    {
        lock (gate)
        {
            if (enabled)
            {
                Write("\n");
                Write(string.Format(Inv, "Log Summary: calcnt:{0} gcnt:{1} gdur:{2:F0} gacnt:{3}\n", calibrationCount, guideCount, guideDurationSec, 0));
                Write($"Log closed at {Now()}\n");
                Flush();
            }

            enabled = false;
        }
    }

    /// <summary>Starts a calibration section (PHD2 StartCalibration). Uses <see cref="GuideLogHeader.CalibrationSettings"/>.</summary>
    public void StartCalibration(GuideLogHeader header)
    {
        lock (gate)
        {
            isGuiding = true;
            isCalibrating = true;
            if (!enabled) return;

            Write("\n");
            Write($"Calibration Begins at {Now()}\n");
            Write($"Equipment Profile = {header.EquipmentProfile}\n");
            Write(GlobalSettingsText(header.Global));
            Write(GuiderSettingsText(header.Guider));
            Write(CameraText(header));

            var mount = header.Mount ?? new GuideLogMountSettings();
            Write("Mount = " + mount.Name);
            if (header.CalibrationSettings is { } cal)
            {
                Write(", " + string.Format(
                    Inv,
                    "Calibration Step = {0} ms, Calibration Distance = {1} px, Assume orthogonal axes = {2}\n",
                    cal.StepMs,
                    cal.DistancePx,
                    cal.AssumeOrthogonal ? "yes" : "no") + GuideSpeedSummary(mount));
            }

            Write("\n");
            Write(PointingInfo(header.Pointing));
            Write("\n");
            WriteLockPosition(header);
            Write(CalibrationColumnHeader + "\n");
            Flush();
        }
    }

    /// <summary>Alias of <see cref="StartCalibration"/>.</summary>
    public void CalibrationBegin(GuideLogHeader header) => StartCalibration(header);

    public void CalibrationStep(GuideLogCalibrationStep step)
    {
        lock (gate)
        {
            if (!enabled) return;
            Write(string.Format(Inv, "{0},{1},{2:F3},{3:F3},{4:F3},{5:F3},{6:F3}\n", step.Direction, step.Step, step.Dx, step.Dy, step.X, step.Y, step.Distance));
            Flush();
        }
    }

    /// <summary>One axis finished calibrating (PHD2 CalibrationDirectComplete).</summary>
    /// <param name="direction">"West" or "North".</param>
    /// <param name="angleRad">Axis angle in radians.</param>
    /// <param name="ratePxPerMs">Rate in pixels per millisecond (logged as px/sec).</param>
    /// <param name="parity">Guide parity.</param>
    public void CalibrationDirectComplete(string direction, double angleRad, double ratePxPerMs, GuideParity parity)
    {
        lock (gate)
        {
            if (!enabled) return;
            Write(string.Format(
                Inv,
                "{0} calibration complete. Angle = {1:F1} deg, Rate = {2:F3} px/sec, Parity = {3}\n",
                direction,
                angleRad * 180.0 / Math.PI,
                ratePxPerMs * 1000.0,
                ParityStr(parity)));
            Flush();
        }
    }

    public void CalibrationComplete(string mountName)
    {
        lock (gate)
        {
            isGuiding = false;
            isCalibrating = false;
            if (!enabled) return;
            calibrationCount++;
            Write($"Calibration complete, mount = {mountName}.\n");
            Flush();
        }
    }

    public void CalibrationFailed(string message)
    {
        lock (gate)
        {
            isGuiding = false;
            isCalibrating = false;
            if (!enabled) return;
            Write(message + "\n");
            Flush();
        }
    }

    /// <summary>Star lost during calibration (PHD2 CalibrationFrameDropped).</summary>
    public void CalibrationFrameDropped(GuideLogFrameDropped info)
    {
        lock (gate)
        {
            if (!enabled) return;
            Write(string.Format(Inv, "INFO: STAR LOST during calibration, Mass= {0:F0}, SNR= {1:F2}, Error= {2}, Status={3}\n", info.StarMass, info.Snr, info.ErrorCode, info.Status));
            Flush();
        }
    }

    /// <summary>Starts a guiding section and writes the full header (PHD2 GuidingStarted).</summary>
    public void GuidingStarted(GuideLogHeader header)
    {
        lock (gate)
        {
            isGuiding = true;
            isCalibrating = false;
            guidingStarted = clock.UtcNow;
            if (!enabled) return;
            Write("\n");
            Write($"Guiding Begins at {Format(guidingStarted)}\n");
            WriteGuidingHeader(header);
            Flush();
        }
    }

    /// <summary>Alias of <see cref="GuidingStarted"/>.</summary>
    public void StartGuiding(GuideLogHeader header) => GuidingStarted(header);

    /// <summary>Seconds since <see cref="GuidingStarted"/> on the log's clock (PHD2 TimeSinceGuidingStarted).</summary>
    public double TimeSinceGuidingStarted
    {
        get
        {
            lock (gate) return (clock.UtcNow - guidingStarted).TotalSeconds;
        }
    }

    public void GuidingStopped()
    {
        lock (gate)
        {
            bool wasGuiding = isGuiding;
            isGuiding = false;
            if (!enabled) return;
            guideCount++;
            if (wasGuiding) guideDurationSec += (clock.UtcNow - guidingStarted).TotalSeconds;
            Write($"Guiding Ends at {Now()}\n");
            Flush();
        }
    }

    /// <summary>Alias of <see cref="GuidingStopped"/>.</summary>
    public void StopGuiding() => GuidingStopped();

    public void GuideStep(GuideLogStep step)
    {
        lock (gate)
        {
            if (!enabled) return;
            Write(string.Format(
                Inv,
                "{0},{1:F3},\"{2}\",{3:F3},{4:F3},{5:F3},{6:F3},{7:F3},{8:F3},",
                step.FrameNumber,
                step.TimeSec,
                "Mount",
                step.CameraDx,
                step.CameraDy,
                step.RaRawDistance,
                step.DecRawDistance,
                step.RaGuideDistance,
                step.DecGuideDistance));
            Write(string.Format(
                Inv,
                "{0},{1},{2},{3},,,",
                step.RaDurationMs,
                step.RaDurationMs > 0 ? DirectionChar(step.RaDirection) : "",
                step.DecDurationMs,
                step.DecDurationMs > 0 ? DirectionChar(step.DecDirection) : ""));
            Write(string.Format(Inv, "{0:F0},{1:F2},{2}\n", step.StarMass, step.Snr, step.ErrorCode));
            Flush();
        }
    }

    /// <summary>A guide frame without a usable star (PHD2 FrameDropped, the "DROP" row).</summary>
    public void FrameDropped(GuideLogFrameDropped info)
    {
        lock (gate)
        {
            if (!enabled) return;
            Write(string.Format(Inv, "{0},{1:F3},\"DROP\",,,,,,,,,,,,,{2:F0},{3:F2},{4},\"{5}\"\n", info.FrameNumber, info.TimeSec, info.StarMass, info.Snr, info.ErrorCode, info.Status));
            Flush();
        }
    }

    /// <summary>Star lost: a DROP row while guiding, an INFO line while calibrating (PHD2 Guider::UpdateGuideState).</summary>
    public void NotifyStarLost(GuideLogFrameDropped info)
    {
        bool calibrating;
        lock (gate) calibrating = isCalibrating;
        if (calibrating) CalibrationFrameDropped(info);
        else FrameDropped(info);
    }

    public void NotifyGuidingDithered(double dx, double dy, GuidePoint newLockPosition)
    {
        lock (gate)
        {
            if (!enabled || !isGuiding) return;
            Write(string.Format(Inv, "INFO: DITHER by {0:F3}, {1:F3}, new lock pos = {2:F3}, {3:F3}\n", dx, dy, newLockPosition.X, newLockPosition.Y));
            Flush();
        }
    }

    /// <summary>Settling state change; PHD2 messages are <see cref="SettlingStarted"/>, <see cref="SettlingComplete"/>, <see cref="SettlingFailed"/>.</summary>
    public void NotifySettlingStateChange(string message)
    {
        lock (gate)
        {
            if (!enabled) return;
            Write($"INFO: SETTLING STATE CHANGE, {message}\n");
            Flush();
        }
    }

    public void NotifySetLockPosition(GuidePoint lockPosition)
    {
        lock (gate)
        {
            if (!enabled || !isGuiding) return;
            Write(string.Format(Inv, "INFO: SET LOCK POSITION, new lock pos = {0:F3}, {1:F3}\n", lockPosition.X, lockPosition.Y));
            Flush();
        }
    }

    /// <summary>Free-form INFO line while guiding (PHD2 Log Viewer lists it with the guide steps).</summary>
    public void Info(string message)
    {
        lock (gate)
        {
            if (!enabled || !isGuiding) return;
            Write($"INFO: {message}\n");
            Flush();
        }
    }

    /// <summary>A command received from a client (PHD2 logs e.g. "PAUSE", "RESUME", "STOP").</summary>
    public void ServerCommand(string command)
    {
        lock (gate)
        {
            if (!enabled || !isGuiding) return;
            Write($"INFO: Server received {command}\n");
            Flush();
        }
    }

    public void NotifyManualGuide(GuideDirection direction, int durationMs)
    {
        lock (gate)
        {
            if (!enabled || !isGuiding) return;
            Write(string.Format(Inv, "INFO: Manual guide (Mount) {0} {1} ms\n", direction, durationMs));
            Flush();
        }
    }

    public void SetGuidingParam(string name, double value) => SetGuidingParam(name, value.ToString("F2", Inv));

    public void SetGuidingParam(string name, int value) => SetGuidingParam(name, value.ToString(Inv));

    public void SetGuidingParam(string name, bool value) => SetGuidingParam(name, value ? "true" : "false");

    public void SetGuidingParam(string name, string value)
    {
        lock (gate)
        {
            if (!enabled || !isGuiding) return;
            Write($"INFO: Guiding parameter change, {name} = {value}\n");
            Flush();
        }
    }

    /// <summary>
    /// Settings applied while guiding: every line of the guiding header's settings part that differs from what the log
    /// showed last is written as a PHD2 parameter change ("INFO: Guiding parameter change, X guide algorithm = …").
    /// </summary>
    public void SettingsChanged(GuideLogHeader header)
    {
        lock (gate)
        {
            if (!enabled || !isGuiding || isCalibrating) return;
            var lines = SettingsLines(GuidingSettingsText(header));
            var shown = new HashSet<string>(loggedSettings);
            bool any = false;
            foreach (string line in lines)
            {
                if (!shown.Contains(line))
                {
                    Write($"INFO: Guiding parameter change, {line}\n");
                    any = true;
                }
            }

            loggedSettings = lines;
            if (any) Flush();
        }
    }

    private static string DirectionChar(GuideDirection? d) => d switch
    {
        GuideDirection.North => "N",
        GuideDirection.South => "S",
        GuideDirection.East => "E",
        GuideDirection.West => "W",
        _ => "-",
    };

    private static string ParityStr(GuideParity p) => p switch
    {
        GuideParity.Even => "Even",
        GuideParity.Odd => "Odd",
        _ => "N/A",
    };

    private static string PierSideStr(PierSide p) => p switch
    {
        PierSide.East => "East",
        PierSide.West => "West",
        _ => "Unknown",
    };

    private static string Num(double? v, string format) => v is { } x ? x.ToString(format, Inv) : "Unknown";

    private static string GuideSpeedSummary(GuideLogMountSettings m) =>
        m.RaGuideSpeedArcsecPerSec is { } ra && m.DecGuideSpeedArcsecPerSec is { } dec
            ? string.Format(Inv, "RA Guide Speed = {0:F1} a-s/s, Dec Guide Speed = {1:F1} a-s/s", ra, dec)
            : "RA Guide Speed = Unknown, Dec Guide Speed = Unknown";

    private static string PointingInfo(GuideLogPointing p)
    {
        string rotator = p.RotatorDeg is { } r ? Norm360(r).ToString("F1", Inv) : "N/A";
        string s;
        if (p.RaHours is { } ra && p.DecDeg is { } dec)
        {
            s = string.Format(
                Inv,
                "RA = {0:F2} hr, Dec = {1:F1} deg, Hour angle = {2} hr, Pier side = {3}, Rotator pos = {4}, ",
                ra,
                dec,
                Num(p.HourAngleHours, "F2"),
                PierSideStr(p.PierSide),
                rotator);
            s += p.AltitudeDeg is { } alt && p.AzimuthDeg is { } az
                ? string.Format(Inv, "Alt = {0:F1} deg, Az = {1:F1} deg", alt, az)
                : "Alt = Unknown, Az = Unknown";
        }
        else
        {
            s = $"RA/Dec = Unknown, Hour angle = Unknown, Pier side = Unknown, Rotator pos = {rotator}, Alt = Unknown, Az = Unknown";
        }

        return s;
    }

    private static double Norm360(double v)
    {
        v %= 360.0;
        return v < 0 ? v + 360.0 : v;
    }

    private string Format(DateTimeOffset t) =>
        TimeZoneInfo.ConvertTime(t, timeZone).ToString("yyyy-MM-dd HH:mm:ss", Inv);

    private string Now() => Format(clock.UtcNow);

    private void Write(string s) => writer.Write(s);

    private void Flush()
    {
        if (options.AutoFlush) writer.Flush();
    }

    private void WriteGuidingHeader(GuideLogHeader h)
    {
        string settings = GuidingSettingsText(h);
        loggedSettings = SettingsLines(settings);
        Write(settings);
        Write(PointingInfo(h.Pointing));
        Write("\n");
        WriteLockPosition(h);
        Write(ColumnHeader + "\n");
    }

    // the settings part of the guiding header (everything before the pointing line)
    private static string GuidingSettingsText(GuideLogHeader h) =>
        $"Equipment Profile = {h.EquipmentProfile}\n" + GlobalSettingsText(h.Global) + GuiderSettingsText(h.Guider) + CameraText(h)
        + (h.Mount != null ? MountSettingsText(h.Mount) : "");

    private static List<string> SettingsLines(string text) =>
        [.. text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0)];

    private static string GlobalSettingsText(GuideLogGlobalSettings g)
    {
        string scale = g.PixelScale is { } ps && ps != 1.0 ? ps.ToString("F2", Inv) + " arc-sec/px" : "unspecified";
        string fl = g.FocalLengthMm is { } f && f != 0 ? f.ToString(Inv) + " mm" : "unspecified";
        string nr = g.NoiseReduction switch
        {
            GuideLogNoiseReduction.None => "none",
            GuideLogNoiseReduction.Mean2x2 => "2x2 mean",
            _ => "3x3 median",
        };
        return string.Format(
            Inv,
            "Dither = {0}, Dither scale = {1:F3}, Image noise reduction = {2}, Guide-frame time lapse = {3}, Server {4}\n",
            g.DitherRaOnly ? "RA only" : "both axes",
            g.DitherScale,
            nr,
            g.TimeLapseMs,
            g.ServerEnabled ? "enabled" : "disabled")
            + string.Format(Inv, "Pixel scale = {0}, Binning = {1}, Focal length = {2}\n", scale, g.Binning, fl);
    }

    private static string GuiderSettingsText(GuideLogGuiderSettings g)
    {
        string s = string.Format(Inv, "Search region = {0} px, Star mass tolerance ", g.SearchRegionPx);
        s += g.MassChangeThreshold is { } t ? string.Format(Inv, "= {0:F1}%", t * 100.0) : "disabled";

        // PHD2 quirk kept for format fidelity: the multi-star variant ends in "\n " (next line starts with a space).
        s += g.MultiStar ? string.Format(Inv, ", Multi-star mode, list size = {0}\n ", g.StarListSize) : ", Single-star mode\n";
        return s;
    }

    private static string CameraText(GuideLogHeader h)
    {
        if (h.Camera is not { } c) return "";
        string gain = c.Gain is { } g ? string.Format(Inv, ", gain = {0}", g) : "";
        string dark = c.DarkExposureMs is { } d && d != 0 ? string.Format(Inv, "have dark, dark dur = {0}", d) : "no dark";
        string px = c.PixelSizeUm is { } p ? p.ToString("F1", Inv) + " um" : "unspecified";
        return string.Format(
            Inv,
            "Camera = {0}{1}, full size = {2} x {3}, {4}, {5}, pixel size = {6}\n",
            c.Name,
            gain,
            c.FullWidth,
            c.FullHeight,
            dark,
            c.DefectMapInUse ? "defect map in use" : "no defect map",
            px)
            + string.Format(Inv, "Exposure = {0} ms\n", h.ExposureMs);
    }

    private static string MountSettingsText(GuideLogMountSettings m)
    {
        string s = $"Mount = {m.Name},{(m.IsConnected ? "" : " not")} connected, guiding {(m.GuidingEnabled ? "enabled" : "disabled")}, ";
        if (m.Calibration is { IsValid: true } cal)
        {
            double xRatePx = cal.XRate * 1000.0, yRatePx = cal.YRate * 1000.0;
            s += string.Format(
                Inv,
                "xAngle = {0:F1}, xRate = {1:F3}, yAngle = {2:F1}, yRate = {3:F3}, parity = {4}/{5}\n",
                cal.XAngle * 180.0 / Math.PI,
                xRatePx,
                cal.YAngle * 180.0 / Math.PI,
                yRatePx,
                ParityStr(cal.RaGuideParity),
                ParityStr(cal.DecGuideParity));

            double scale = cal.PixelScale > 0 ? cal.PixelScale : 1.0;
            double xRateAsD0 = cal.Declination is { } decl ? cal.XRate * 1000.0 / Math.Cos(decl) * scale : cal.XRate * 1000.0 * scale;
            string ortho = m.OrthoErrorDeg is { } oe ? oe.ToString("F1", Inv) + " deg" : "unknown";
            s += string.Format(Inv, "Norm rates RA = {0:F1}\"/s @ dec 0, Dec = {1:F1}\"/s; ortho.err. = {2}\n", xRateAsD0, yRatePx * scale, ortho);
        }
        else
        {
            s += "not calibrated\n";
        }

        s += $"X guide algorithm = {m.XAlgorithm.Name}, {m.XAlgorithm.Summary}\n";
        s += $"Y guide algorithm = {m.YAlgorithm.Name}, {m.YAlgorithm.Summary}\n";
        if (m.Backlash is { } bl)
            s += string.Format(Inv, "Backlash comp = {0}, pulse = {1} ms{2}\n", bl.Enabled ? "enabled" : "disabled", bl.PulseMs, bl.PlayModel ? ", model = play" : "");
        if (m.PulseModel is { } pulseModel)
            s += $"Pulse model = {(pulseModel ? "on" : "off")}\n";

        // Scope::GetSettingsSummary
        s += string.Format(Inv, "Max RA duration = {0}, Max DEC duration = {1}, DEC guide mode = {2}\n", m.MaxRaDurationMs, m.MaxDecDurationMs, DecGuideModeStr(m.DecGuideMode));
        s += GuideSpeedSummary(m) + ", ";
        string calDec = m.Calibration?.Declination is { } cd ? (cd * 180.0 / Math.PI).ToString("F1", Inv) : "Unknown";
        s += $"Cal Dec = {calDec}, Last Cal Issue = {m.LastCalibrationIssue}, Timestamp = {m.CalibrationTimestamp}\n";
        return s;
    }

    private static string DecGuideModeStr(DecGuideMode m) => m switch
    {
        DecGuideMode.Off => "Off",
        DecGuideMode.Auto => "Auto",
        DecGuideMode.North => "North",
        DecGuideMode.South => "South",
        DecGuideMode.Drift => "Drift",
        _ => "Invalid",
    };

    private void WriteLockPosition(GuideLogHeader h)
    {
        Write(string.Format(
            Inv,
            "Lock position = {0:F3}, {1:F3}, Star position = {2:F3}, {3:F3}, HFD = {4:F2} px\n",
            h.LockPosition.X,
            h.LockPosition.Y,
            h.StarPosition.X,
            h.StarPosition.Y,
            h.Hfd));
    }
}
