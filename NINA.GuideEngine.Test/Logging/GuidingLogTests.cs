// SPDX-License-Identifier: MPL-2.0

using FluentAssertions;
using NUnit.Framework;
using NINA.GuideEngine.Algorithms;
using NINA.GuideEngine.Calibration;
using NINA.GuideEngine.Core;
using NINA.GuideEngine.Guiding;
using NINA.GuideEngine.Logging;
using NINA.GuideEngine.Simulation;
using NINA.GuideEngine.Stars;

namespace NINA.GuideEngine.Test.Logging;

public class GuidingLogTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 9, 23, 21, 30, 0, TimeSpan.Zero);

    private static (GuidingLog Log, StringWriter Text, VirtualClock Clock) Create()
    {
        var clock = new VirtualClock(Epoch);
        var text = new StringWriter();
        var log = new GuidingLog(text, clock, new GuidingLogOptions { TimeZone = TimeZoneInfo.Utc });
        return (log, text, clock);
    }

    private static GuideLogHeader Header()
    {
        var cal = new CalibrationData
        {
            XAngle = 30.0 * Math.PI / 180,
            YAngle = 120.0 * Math.PI / 180,
            XRate = 0.0045,
            YRate = 0.0049,
            Declination = 20.0 * Math.PI / 180,
            RaGuideParity = GuideParity.Even,
            DecGuideParity = GuideParity.Odd,
            PixelScale = 1.51,
        };

        return new GuideLogHeader
        {
            EquipmentProfile = "Sim profile",
            Global = new GuideLogGlobalSettings { PixelScale = 1.51, Binning = 1, FocalLengthMm = 800 },
            Guider = new GuideLogGuiderSettings { SearchRegionPx = 15, MassChangeThreshold = 0.5, MultiStar = true, StarListSize = 9 },
            Camera = new GuideLogCameraSettings { Name = "Simulator", Gain = 100, FullWidth = 1936, FullHeight = 1216, PixelSizeUm = 5.86 },
            ExposureMs = 2000,
            Mount = new GuideLogMountSettings
            {
                Name = "Simulated mount",
                Calibration = cal,
                OrthoErrorDeg = 0.4,
                XAlgorithm = GuideLogAlgorithm.Hysteresis(0.1, 0.7, 0.15),
                YAlgorithm = GuideLogAlgorithm.ResistSwitch(0.15, 1.0, true),
                Backlash = new GuideLogBacklash(false, 450),
                RaGuideSpeedArcsecPerSec = 7.5205,
                DecGuideSpeedArcsecPerSec = 7.5205,
                CalibrationTimestamp = "2026-09-23 21:30:40",
            },
            CalibrationSettings = new GuideLogCalibrationSettings(750, 25, false),
            Pointing = new GuideLogPointing { RaHours = 6.0, DecDeg = 20.0, HourAngleHours = -1.25, PierSide = PierSide.East, AltitudeDeg = 45.3, AzimuthDeg = 120.7 },
            LockPosition = new GuidePoint(968.123, 607.456),
            StarPosition = new GuidePoint(968.2, 607.5),
            Hfd = 2.34,
        };
    }

    [Test]
    [SetCulture("de-DE")]
    public void Full_session_matches_phd2_golden_text()
    {
        var (log, text, clock) = Create();
        var header = Header();

        log.EnableLogging();
        clock.Advance(TimeSpan.FromSeconds(10));
        log.StartCalibration(header);
        log.CalibrationStep(new GuideLogCalibrationStep(GuideLogCalibrationDirection.West, 1, 1.234, -0.567, 969.357, 606.889, 1.358));
        log.CalibrationDirectComplete("West", 30.0 * Math.PI / 180, 0.0045, GuideParity.Even);
        log.NotifyStarLost(new GuideLogFrameDropped { StarMass = 12.4, Snr = 2.5, ErrorCode = GuidingLog.ErrorCode(StarFindResult.LowSnr), Status = GuidingLog.StarLostStatus(StarFindResult.LowSnr) });
        log.CalibrationComplete("Simulated mount");

        clock.AdvanceTo(TimeSpan.FromSeconds(60));
        log.GuidingStarted(header);
        log.GuideStep(new GuideLogStep
        {
            FrameNumber = 1, TimeSec = 2.1, CameraDx = 0.512, CameraDy = -0.221, RaRawDistance = 0.3, DecRawDistance = 0.45,
            RaGuideDistance = 0.21, DecGuideDistance = 0.45, RaDurationMs = 47, RaDirection = GuideDirection.East,
            DecDurationMs = 92, DecDirection = GuideDirection.North, StarMass = 12345.2, Snr = 45.67, ErrorCode = 0,
        });
        log.GuideStep(new GuideLogStep
        {
            FrameNumber = 2, TimeSec = 4.2, CameraDx = 0.01, CameraDy = 0.02, RaRawDistance = 0.015, DecRawDistance = -0.012,
            RaDirection = GuideDirection.West, StarMass = 12001, Snr = 44.1,
        });
        log.NotifyStarLost(new GuideLogFrameDropped { FrameNumber = 3, TimeSec = 6.3, StarMass = 11, Snr = 1.2, ErrorCode = 2, Status = "Star lost - low SNR" });
        log.NotifyGuidingDithered(1.5, -2.25, new GuidePoint(969.623, 605.206));
        log.NotifySettlingStateChange(GuidingLog.SettlingStarted);
        log.ServerCommand("PAUSE");
        log.SetGuidingParam("Exposure", "2000 ms");
        clock.AdvanceTo(TimeSpan.FromSeconds(180));
        log.GuidingStopped();
        log.NotifyGuidingDithered(1, 1, new GuidePoint(0, 0)); // ignored: not guiding
        clock.AdvanceTo(TimeSpan.FromSeconds(210));
        log.Close();

        const string settings = """
            Equipment Profile = Sim profile
            Dither = both axes, Dither scale = 1.000, Image noise reduction = none, Guide-frame time lapse = 0, Server disabled
            Pixel scale = 1.51 arc-sec/px, Binning = 1, Focal length = 800 mm
            Search region = 15 px, Star mass tolerance = 50.0%, Multi-star mode, list size = 9
             Camera = Simulator, gain = 100, full size = 1936 x 1216, no dark, no defect map, pixel size = 5.9 um
            Exposure = 2000 ms
            """;
        const string pointing = """
            RA = 6.00 hr, Dec = 20.0 deg, Hour angle = -1.25 hr, Pier side = East, Rotator pos = N/A, Alt = 45.3 deg, Az = 120.7 deg
            Lock position = 968.123, 607.456, Star position = 968.200, 607.500, HFD = 2.34 px
            """;
        string expected = $"""
            PHD2 version InternalGuider [Linux], Log version 2.5. Log enabled at 2026-09-23 21:30:00

            Calibration Begins at 2026-09-23 21:30:10
            {settings}
            Mount = Simulated mount, Calibration Step = 750 ms, Calibration Distance = 25 px, Assume orthogonal axes = no
            RA Guide Speed = 7.5 a-s/s, Dec Guide Speed = 7.5 a-s/s
            {pointing}
            Direction,Step,dx,dy,x,y,Dist
            West,1,1.234,-0.567,969.357,606.889,1.358
            West calibration complete. Angle = 30.0 deg, Rate = 4.500 px/sec, Parity = Even
            INFO: STAR LOST during calibration, Mass= 12, SNR= 2.50, Error= 2, Status=Star lost - low SNR
            Calibration complete, mount = Simulated mount.

            Guiding Begins at 2026-09-23 21:31:00
            {settings}
            Mount = Simulated mount, connected, guiding enabled, xAngle = 30.0, xRate = 4.500, yAngle = 120.0, yRate = 4.900, parity = Even/Odd
            Norm rates RA = 7.2"/s @ dec 0, Dec = 7.4"/s; ortho.err. = 0.4 deg
            X guide algorithm = Hysteresis, Hysteresis = 0.100, Aggression = 0.700, Minimum move = 0.150
            Y guide algorithm = Resist Switch, Minimum move = 0.150 Aggression = 100% FastSwitch = enabled
            Backlash comp = disabled, pulse = 450 ms
            Max RA duration = 2500, Max DEC duration = 2500, DEC guide mode = Auto
            RA Guide Speed = 7.5 a-s/s, Dec Guide Speed = 7.5 a-s/s, Cal Dec = 20.0, Last Cal Issue = None, Timestamp = 2026-09-23 21:30:40
            {pointing}
            Frame,Time,mount,dx,dy,RARawDistance,DECRawDistance,RAGuideDistance,DECGuideDistance,RADuration,RADirection,DECDuration,DECDirection,XStep,YStep,StarMass,SNR,ErrorCode
            1,2.100,"Mount",0.512,-0.221,0.300,0.450,0.210,0.450,47,E,92,N,,,12345,45.67,0
            2,4.200,"Mount",0.010,0.020,0.015,-0.012,0.000,0.000,0,,0,,,,12001,44.10,0
            3,6.300,"DROP",,,,,,,,,,,,,11,1.20,2,"Star lost - low SNR"
            INFO: DITHER by 1.500, -2.250, new lock pos = 969.623, 605.206
            INFO: SETTLING STATE CHANGE, Settling started
            INFO: Server received PAUSE
            INFO: Guiding parameter change, Exposure = 2000 ms
            Guiding Ends at 2026-09-23 21:33:00

            Log Summary: calcnt:1 gcnt:1 gdur:120 gacnt:0
            Log closed at 2026-09-23 21:33:30

            """;

        text.ToString().Should().Be(expected.ReplaceLineEndings("\n"));
    }

    [Test]
    public void Uncalibrated_mount_and_unknown_pointing()
    {
        var (log, text, _) = Create();
        log.EnableLogging();
        log.GuidingStarted(new GuideLogHeader
        {
            EquipmentProfile = "P",
            Global = new GuideLogGlobalSettings { DitherRaOnly = true, NoiseReduction = GuideLogNoiseReduction.Median3x3 },
            Guider = new GuideLogGuiderSettings { MassChangeThreshold = null, MultiStar = false },
            Mount = new GuideLogMountSettings { Name = "M", IsConnected = false, DecGuideMode = DecGuideMode.North },
            Pointing = new GuideLogPointing { RotatorDeg = -10 },
        });

        var lines = text.ToString().Split('\n');
        lines.Should().Contain("Dither = RA only, Dither scale = 1.000, Image noise reduction = 3x3 median, Guide-frame time lapse = 0, Server disabled");
        lines.Should().Contain("Pixel scale = unspecified, Binning = 1, Focal length = unspecified");
        lines.Should().Contain("Search region = 15 px, Star mass tolerance disabled, Single-star mode");
        lines.Should().Contain("Mount = M, not connected, guiding enabled, not calibrated");
        lines.Should().Contain("X guide algorithm = None, ");
        lines.Should().Contain("Max RA duration = 2500, Max DEC duration = 2500, DEC guide mode = North");
        lines.Should().Contain("RA Guide Speed = Unknown, Dec Guide Speed = Unknown, Cal Dec = Unknown, Last Cal Issue = None, Timestamp = ");
        lines.Should().Contain("RA/Dec = Unknown, Hour angle = Unknown, Pier side = Unknown, Rotator pos = 350.0, Alt = Unknown, Az = Unknown");
        lines.Should().Contain("Lock position = 0.000, 0.000, Star position = 0.000, 0.000, HFD = 0.00 px");
        lines.Should().NotContain(l => l.StartsWith(" Camera"));
    }

    [Test]
    public void Nothing_is_written_while_disabled_and_info_needs_guiding()
    {
        var (log, text, _) = Create();
        log.GuideStep(new GuideLogStep());
        log.NotifySettlingStateChange("x");
        text.ToString().Should().BeEmpty();

        log.EnableLogging();
        log.ServerCommand("STOP");
        log.NotifySetLockPosition(new GuidePoint(1, 2));
        log.NotifyManualGuide(GuideDirection.North, 100);
        text.ToString().Split('\n').Should().HaveCount(2, "only the version line and the trailing empty split");

        log.GuidingStarted(new GuideLogHeader());
        log.NotifySetLockPosition(new GuidePoint(1, 2));
        log.NotifyManualGuide(GuideDirection.North, 100);
        log.SetGuidingParam("Aggression", 0.7);
        log.SetGuidingParam("Fast switch", true);
        text.ToString().Should().Contain("INFO: SET LOCK POSITION, new lock pos = 1.000, 2.000\n");
        text.ToString().Should().Contain("INFO: Manual guide (Mount) North 100 ms\n");
        text.ToString().Should().Contain("INFO: Guiding parameter change, Aggression = 0.70\n");
        text.ToString().Should().Contain("INFO: Guiding parameter change, Fast switch = true\n");

        log.DisableLogging();
        text.ToString().Should().EndWith("\nLog disabled at 2026-09-23 21:30:00\n");
    }

    [Test]
    public void Settings_changed_while_guiding_logs_only_the_lines_that_changed()
    {
        var (log, text, _) = Create();
        log.EnableLogging();
        var header = Header();
        log.SettingsChanged(header);
        text.ToString().Should().NotContain("Guiding parameter change", "only while guiding");

        log.GuidingStarted(header);
        int written = text.ToString().Length;
        log.SettingsChanged(header);
        text.ToString().Length.Should().Be(written, "nothing changed");

        var paced = header with
        {
            ExposureMs = 2500,
            Mount = header.Mount! with { YAlgorithm = new GuideLogAlgorithm("Predictive", "Correction pace = 0.50, Minimum move = none") },
        };
        log.SettingsChanged(paced);
        string added = text.ToString()[written..];
        added.Should().Be(
            "INFO: Guiding parameter change, Exposure = 2500 ms\n" +
            "INFO: Guiding parameter change, Y guide algorithm = Predictive, Correction pace = 0.50, Minimum move = none\n");

        log.SettingsChanged(paced);
        text.ToString().Length.Should().Be(written + added.Length, "a change is shown once");
    }

    [Test]
    public void Pulse_model_setting_and_updates_are_logged()
    {
        var (log, text, _) = Create();
        log.EnableLogging();
        var header = Header();
        log.GuidingStarted(header with { Mount = header.Mount! with { PulseModel = true } });
        text.ToString().Should().Contain("Backlash comp = disabled, pulse = 450 ms\nPulse model = on\nMax RA duration");

        var values = new PulseModelValues(1.15, 0.81, new PulseResponseEstimate(1.14, 0.012, null, null, 48.2),
            new PulseResponseEstimate(0.78, 0.031, 0.3, 0.1, 48.2));
        GuideLogBridge.PulseModelText(new PulseModelUpdatedEvent(Epoch, values)).Should().Be(
            "Pulse model: RA pulses ×0.87 (effect 1.14 ± 0.01), Dec pulses ×1.23 (effect 0.78 ± 0.03), from 48 dithers");
        GuideLogBridge.PulseModelText(new PulseModelUpdatedEvent(Epoch, PulseModelValues.Neutral)).Should().Be(
            "Pulse model: RA pulses ×1.00 (learning), Dec pulses ×1.00 (learning), from 0 dithers");
    }

    [Test]
    public void Algorithm_summaries_use_phd2_format()
    {
        GuideLogAlgorithm.Lowpass2(80, 0.2).Summary.Should().Be("Aggressiveness = 80.000, Minimum move = 0.200");
        GuideLogAlgorithm.Lowpass(5, 0.2).Summary.Should().Be("Slope weight = 5.000, Minimum move = 0.200");
        GuideLogAlgorithm.ResistSwitch(0.2, 0.85, false).Summary.Should().Be("Minimum move = 0.200 Aggression = 85% FastSwitch = disabled");
    }

    [Test]
    public void File_name_follows_phd2_convention()
    {
        GuidingLog.FileName(new DateTime(2026, 9, 23, 21, 30, 5)).Should().Be("InternalGuider_GuideLog_2026-09-23_213005.txt");
        GuidingLog.FileName(new DateTime(2026, 1, 2, 3, 4, 5), 2).Should().Be("InternalGuider_GuideLog_2_2026-01-02_030405.txt");
    }

    [Test]
    public void Star_lost_status_strings()
    {
        GuidingLog.StarLostStatus(StarFindResult.MassChange).Should().Be("Star lost - mass changed");
        GuidingLog.StarLostStatus(StarFindResult.Error).Should().Be("No star found");
        GuidingLog.ErrorCode(StarFindResult.TooNearEdge).Should().Be(6);
    }
}
