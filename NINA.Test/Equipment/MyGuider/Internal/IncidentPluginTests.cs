// SPDX-License-Identifier: MPL-2.0

using System.IO.Compression;
using System.Text.Json.Nodes;
using FluentAssertions;
using Moq;
using NINA.Equipment.Equipment.MyGuider.Advanced;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Profile;
using NINA.Profile.Interfaces;
using NUnit.Framework;
using NINA.GuideEngine.Core;
using NINA.GuideEngine.Guiding;
using NINA.GuideEngine.Incidents;
using NINA.GuideEngine.Simulation;
using NINA.Equipment.Equipment.MyGuider.Internal;

namespace NINA.Test.Equipment.MyGuider.Internal;

[TestFixture]
public class IncidentSettingsTests
{
    private delegate bool TryGetString(Guid id, string key, out string value);

    internal static (InternalGuiderOptions Options, Mock<IProfileService> Service, Dictionary<string, string> Store) Create()
    {
        var profile = new Mock<IProfile>();
        var store = new Dictionary<string, string>();
        var plugin = new Mock<IPluginSettings>();
        plugin.Setup(p => p.SetValue(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>()))
            .Callback<Guid, string, string>((_, k, v) => store[k] = v);
        plugin.Setup(p => p.TryGetValue(It.IsAny<Guid>(), It.IsAny<string>(), out It.Ref<string>.IsAny))
            .Returns(new TryGetString((Guid _, string k, out string v) => store.TryGetValue(k, out v!)));
        var astrometry = new Mock<IAstrometrySettings>();
        astrometry.SetupGet(a => a.Latitude).Returns(48.12345);
        astrometry.SetupGet(a => a.Longitude).Returns(11.56789);
        profile.SetupGet(p => p.PluginSettings).Returns(plugin.Object);
        profile.SetupGet(p => p.GuiderSettings).Returns(new Mock<IGuiderSettings>().Object);
        profile.SetupGet(p => p.AstrometrySettings).Returns(astrometry.Object);
        profile.SetupGet(p => p.CameraSettings).Returns(new Mock<ICameraSettings>().Object);
        profile.SetupGet(p => p.TelescopeSettings).Returns(new Mock<ITelescopeSettings>().Object);
        profile.SetupGet(p => p.Id).Returns(Guid.Parse("11111111-2222-3333-4444-555555555555"));
        profile.SetupGet(p => p.Name).Returns("Rig A");
        var service = new Mock<IProfileService>();
        service.SetupGet(s => s.ActiveProfile).Returns(profile.Object);
        return (new InternalGuiderOptions(service.Object, InternalGuiderOptions.SettingsId), service, store);
    }

    [Test]
    public void Incident_settings_are_described_in_their_group()
    {
        var d = Create().Options.Describe().Where(x => x.Group == "Incidents").ToDictionary(x => x.Name);
        d.Keys.Should().Equal("IncidentRecorder", "IncidentBudgetMb", "SimulatorScenario", "SimulateFault");
        d["IncidentRecorder"].Should().Match<AdvancedGuiderSetting>(x => x.Type == "bool" && x.DefaultValue == "true" && x.Value == "true");
        d["IncidentBudgetMb"].Should().Match<AdvancedGuiderSetting>(x => x.Type == "int" && x.DefaultValue == "1000" && x.Min == 100 && x.Max == 20000 && x.Unit == "MB");
        d["SimulatorScenario"].Type.Should().Be("enum");
        d["SimulatorScenario"].DefaultValue.Should().Be("GoodMount");
        d["SimulatorScenario"].Options.Should().Equal(SimulatorScenario.Presets.Select(p => p.Name));
        d["SimulatorScenario"].RequiresReconnect.Should().BeTrue();
        d["SimulateFault"].Type.Should().Be("action");
        d["SimulateFault"].Options.Should().Equal("Clouds", "Bump", "MountStopsResponding", "CameraFailure", "Runaway");
        foreach (var name in new[] { "SimulatorScenario", "SimulateFault" })
        {
            d[name].DependsOn.Should().Be("GuideSource");
            d[name].AppliesTo.Should().Equal("Simulator");
        }

        d["IncidentRecorder"].DependsOn.Should().BeEmpty();
    }

    [Test]
    public void Incident_settings_reach_the_engine_and_the_store()
    {
        var (o, _, store) = Create();
        o.ToEngineSettings().Incidents.Enabled.Should().BeTrue();
        o.IncidentBudgetBytes.Should().Be(1000 * IncidentStoreOptions.MB);
        o.SimulatorScenario.Should().BeSameAs(SimulatorScenario.GoodMount);

        o.TrySet("IncidentRecorder", "false", out _).Should().BeTrue();
        o.TrySet("IncidentBudgetMb", "2500", out _).Should().BeTrue();
        o.TrySet("IncidentBudgetMb", "50", out var tooSmall).Should().BeFalse();
        tooSmall.Should().Contain("between");
        o.TrySet("SimulatorScenario", "clouds", out _).Should().BeTrue();

        o.ToEngineSettings().Incidents.Enabled.Should().BeFalse();
        o.IncidentBudgetBytes.Should().Be(2500 * IncidentStoreOptions.MB);
        o.SimulatorScenario.Should().BeSameAs(SimulatorScenario.Clouds);
        store["SimulatorScenario"].Should().Be("Clouds");
    }

    [Test]
    public void An_action_with_options_accepts_only_its_options_and_stores_nothing()
    {
        var (o, _, store) = Create();
        o.TrySet("SimulateFault", "bump", out _).Should().BeTrue();
        o.TrySet("SimulateFault", "Earthquake", out var error).Should().BeFalse();
        error.Should().Contain("Clouds");
        store.Should().NotContainKey("SimulateFault");
        o.GetString("SimulateFault").Should().BeEmpty();
    }

    [Test]
    public void Faults_need_the_connected_simulator()
    {
        using var t = new GuiderFixture();
        t.Guider.TrySetSetting("SimulateFault", "Clouds", out var error).Should().BeFalse();
        error.Should().Contain("simulator");
    }
}

[TestFixture]
public class IncidentMappingTests
{
    [Test]
    public void Summary_follows_the_naming_conventions()
    {
        var i = IncidentSamples.Incident("20260926-021345-StarLost") with
        {
            FramesOmitted = IncidentFramesOmitted.DiskSpace,
            Diagnosis = new IncidentDiagnosis(IncidentCause.FieldJump, "Likely a field jump", new Dictionary<string, object?> { ["jumpPx"] = 4.5 }, []),
            Occurrences = 2,
            Kept = true,
        };

        var s = IncidentMapping.ToSummary(i);

        s.Id.Should().Be("20260926-021345-StarLost");
        s.Kind.Should().Be("StarLost");
        s.Kinds.Should().Equal("StarLost", "Spike");
        s.EndReason.Should().Be("recovered");
        s.FramesOmitted.Should().Be("diskSpace");
        s.Cause.Should().Be("fieldJump");
        s.Occurrences.Should().Be(2);
        s.Ongoing.Should().BeTrue();
        s.Kept.Should().BeTrue();
        s.FrameCount.Should().Be(2);
        s.Start.Kind.Should().Be(DateTimeKind.Utc);
        s.Tags.Should().BeEquivalentTo(new AdvancedIncidentTags { ProfileId = "p", ProfileName = "Rig A", GuideCamera = "ASI", Mount = "EQ6", PixelScale = 2, ImagingScale = 1.1 });

        IncidentMapping.ToSummary(i with { FramesOmitted = IncidentFramesOmitted.None, Diagnosis = null, Frames = [], FrameCount = 7 }).Should()
            .Match<AdvancedIncidentSummary>(x => x.FramesOmitted == null && x.Cause == null && x.FrameCount == 7);
        IncidentMapping.FramesOmitted(IncidentFramesOmitted.Budget).Should().Be("budget");
        IncidentMapping.EndReason(IncidentEndReason.Cap).Should().Be("cap");
        Enum.GetValues<IncidentCause>().Select(IncidentMapping.Cause).Should().Contain(["clouds", "dew", "guideStarOnly", "driftTooFast", "mountNotMoving",
            "calibrationMismatch", "mountMoved", "camera", "periodicSpike", "unclear"]);
    }

    [Test]
    public void Incident_maps_triggers_markers_diagnosis_and_frames()
    {
        var i = IncidentSamples.Incident("x") with
        {
            Diagnosis = new IncidentDiagnosis(IncidentCause.Clouds, "Likely clouds", new Dictionary<string, object?> { ["dropPercent"] = 80.0 },
                [new IncidentEvidence("starsFaded", "3 stars faded", new Dictionary<string, object?> { ["stars"] = 3.0, ["seconds"] = 12.0 }, 11)]),
        };

        var d = IncidentMapping.ToDto(i);

        d.SearchRegionPx.Should().Be(17);
        d.SensorWidth.Should().Be(1936);
        d.ContextBinning.Should().Be(4);
        d.Triggers[0].Should().BeEquivalentTo(new AdvancedIncidentTrigger
        {
            Time = i.Triggers[0].Time.UtcDateTime, Kind = "StarLost", Code = 100, CodeName = "StarLost", Message = "Guide star lost", Detail = "LowSnr", Frame = 11,
        });
        d.Triggers[1].Code.Should().BeNull();
        d.Triggers[1].CodeName.Should().BeNull();
        d.Markers.Select(m => m.Type).Should().Equal("trigger", "gap", "recovered", "end");
        d.Diagnosis!.Cause.Should().Be("clouds");
        d.Diagnosis.Parameters["dropPercent"].Should().Be(80.0);
        d.Diagnosis.Evidence.Should().ContainSingle().Which.Should().Match<AdvancedIncidentEvidence>(e => e.Code == "starsFaded" && e.Frame == 11 && (double)e.Parameters["stars"]! == 3.0);

        var found = d.Frames[0];
        found.State.Should().Be("Guiding");
        found.RaArcsec.Should().BeApproximately(0.6, 1e-9, "raw px × the guide pixel scale");
        found.DecArcsec.Should().BeApproximately(-0.8, 1e-9);
        found.TotalArcsec.Should().BeApproximately(1.0, 1e-9);
        found.RaDirection.Should().Be("West");
        found.DecDirection.Should().BeEmpty();
        found.LockX.Should().Be(100);
        found.StarX.Should().Be(100.3);
        found.MountTracking.Should().BeTrue();
        found.PierSide.Should().Be("West");
        found.DeclinationDeg.Should().Be(20);
        found.Stars.Should().HaveCount(2);
        found.Stars[1].RejectReason.Should().Be("Miss");
        found.HasContext.Should().BeTrue();
        found.Crops.Should().Be(2);

        var lost = d.Frames[1];
        lost.StarFound.Should().BeFalse();
        lost.LostStatus.Should().Be("LowSnr");
        lost.StarX.Should().BeNull();
        lost.TotalArcsec.Should().BeNull();
        lost.MountTracking.Should().BeNull("the mount was not connected");
        lost.PierSide.Should().BeNull();
        lost.CalibrationDirection.Should().Be("West");
        lost.CalibrationStep.Should().Be(3);
    }

    [Test]
    public void Images_carry_their_geometry_and_16_bit_pixels()
    {
        var key = IncidentMapping.ToDto(new IncidentImage { Frame = 5, Kind = IncidentImageKind.Key, Width = 3, Height = 2, X0 = 9, Y0 = 9, Pixels = [1, 2, 3, 4, 5, 6] });
        key.Should().BeEquivalentTo(new AdvancedIncidentImage { Frame = 5, Kind = "key", Width = 3, Height = 2, Binning = 1, BitDepth = 16, X0 = 0, Y0 = 0, Pixels = [1, 2, 3, 4, 5, 6] });
        var crop = IncidentMapping.ToDto(new IncidentImage { Frame = 5, Kind = IncidentImageKind.Crop, Star = 2, Width = 1, Height = 1, X0 = 40, Y0 = 12, Pixels = [7] });
        crop.Should().Match<AdvancedIncidentImage>(c => c.Kind == "crop" && c.Star == 2 && c.X0 == 40 && c.Y0 == 12);
        IncidentMapping.ToDto(new IncidentImage { Kind = IncidentImageKind.Context, Width = 1, Height = 1, Binning = 4, Pixels = [1] }).Should()
            .Match<AdvancedIncidentImage>(c => c.Kind == "context" && c.Binning == 4);
        IncidentMapping.ImageKind("Context").Should().Be(IncidentImageKind.Context);
        IncidentMapping.ImageKind("key").Should().Be(IncidentImageKind.Key);
        IncidentMapping.ImageKind("crop").Should().BeNull();
        IncidentMapping.ImageKind(null).Should().BeNull();
    }

    [Test]
    public void Started_summary_is_recording()
    {
        var s = IncidentMapping.Started(new IncidentStartedEvent(new DateTimeOffset(2026, 9, 26, 2, 13, 45, TimeSpan.Zero), "20260926-021345-Manual", IncidentKind.Manual),
            new IncidentTags { Simulator = true });
        s.Should().Match<AdvancedIncidentSummary>(x => x.Id == "20260926-021345-Manual" && x.Kind == "Manual" && x.EndReason == "recording" && x.Tags.Simulator);
    }
}

[TestFixture]
public class IncidentArchiveTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    private string dir = string.Empty;

    [SetUp]
    public void SetUp() => dir = Path.Combine(Path.GetTempPath(), "pins-archive-" + Guid.NewGuid().ToString("N"));

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, true);
        }
    }

    [Test]
    public void Log_excerpts_cover_the_window_and_mask_the_site_and_home()
    {
        var guideLogs = Directory.CreateDirectory(Path.Combine(dir, "guide")).FullName;
        var pinsLogs = Directory.CreateDirectory(Path.Combine(dir, "pins")).FullName;
        var guide = new List<string>
        {
            "PHD2 version 2.6.13, Log version 2.5. Log enabled at 2026-09-25 20:00:00",
            "",
            "Guiding Begins at 2026-09-26 02:00:00",
            "Dither = both axes, Dither scale = 1.000",
            "Frame,Time,mount,dx,dy,RARawDistance,DECRawDistance,RAGuideDistance,DECGuideDistance,RADuration,RADirection,DECDuration,DECDirection,XStep,YStep,StarMass,SNR,ErrorCode",
        };
        for (int k = 1; k <= 600; k++)
        {
            guide.Add($"{k},{k * 2}.000,\"Mount\",0.1,0.2,0.1,0.2,0.1,0.2,100,W,0,,,,1000,20.00,0");
        }

        guide.Add("INFO: SETTLING STATE CHANGE, Settling complete");
        guide.Add("Guiding Ends at 2026-09-26 02:20:00");
        File.WriteAllLines(Path.Combine(guideLogs, "InternalGuider_GuideLog_2026-09-25.txt"), guide);

        File.WriteAllLines(Path.Combine(pinsLogs, "20260926-013000-3.2.0.9001.123-.log"),
        [
            "2026-09-26T02:05:00.0000|INFO|Too early",
            "2026-09-26T02:13:30.1234|INFO|Site Latitude: 48.12345, Longitude: 11.56789 (48° 7' 24.4\" N, 011:34:04 E)",
            "2026-09-26T02:13:00.0000|ERROR|Failed to read /home/tester/.local/share/NINA/Profiles/x.profile",
            "System.IO.IOException: at 48,1234 in /home/tester/src",
            "2026-09-26T02:17:30.0000|INFO|Too late",
        ]);
        File.WriteAllText(Path.Combine(pinsLogs, "20260926-030000-3.2.0.9001.124-.log"), "2026-09-26T03:00:00.0000|INFO|Started later\n");
        foreach (var f in Directory.GetFiles(pinsLogs))
        {
            File.SetLastWriteTimeUtc(f, new DateTime(2026, 9, 26, 3, 30, 0, DateTimeKind.Utc));
        }

        var incident = IncidentSamples.Incident("20260926-021400-StarLost") with
        {
            Start = new DateTimeOffset(2026, 9, 26, 2, 14, 0, TimeSpan.Zero),
            End = new DateTimeOffset(2026, 9, 26, 2, 16, 0, TimeSpan.Zero),
        };

        var extras = IncidentArchive.Extras(incident, "{\"x\":1}", guideLogs, pinsLogs, 48.12345, 11.56789, "/home/tester", Utc).ToDictionary(e => e.Name, e => e.Content);

        extras.Keys.Should().Equal("settings.json", "guide-log.txt", "pins-log.txt");
        var guideLines = extras["guide-log.txt"].Split('\n', StringSplitOptions.RemoveEmptyEntries);
        guideLines[0].Should().Be("Guiding Begins at 2026-09-26 02:00:00", "the section header comes along");
        guideLines.Should().Contain(l => l.StartsWith("Frame,Time", StringComparison.Ordinal));
        var rows = guideLines.Where(l => char.IsDigit(l[0])).Select(l => int.Parse(l.Split(',')[0])).ToList();
        rows.First().Should().Be(390, "02:13:00 = 780 s after the start");
        rows.Last().Should().Be(510, "02:17:00");
        guideLines.Should().NotContain(l => l.StartsWith("Guiding Ends", StringComparison.Ordinal));

        string pins = extras["pins-log.txt"];
        pins.Should().NotContain("Too early").And.NotContain("Too late").And.NotContain("Started later");
        pins.Should().Contain("Site Latitude: <latitude>, Longitude: <longitude> (<latitude> N, <longitude> E)");
        pins.Should().Contain("Failed to read ~/.local/share/NINA/Profiles/x.profile");
        pins.Should().Contain("System.IO.IOException: at <latitude> in ~/src", "continuation lines belong to their line");
        pins.Should().NotContain("48.12").And.NotContain("11.56").And.NotContain("/home/tester");
    }

    [Test]
    public void Masking_handles_common_formats_without_touching_other_numbers()
    {
        string text = "lat 48.1235 lat2 -48.123 dms 48°07'24\" dm 48 07.4' long 11.5679 and 11,57 hfr 148.12 exposure 2.48 frame 11560";
        string masked = IncidentArchive.Mask(text, 48.12345, 11.56789, null);
        masked.Should().Be("lat <latitude> lat2 <latitude> dms <latitude> dm <latitude> long <longitude> and <longitude> hfr 148.12 exposure 2.48 frame 11560");
        IncidentArchive.Mask("near 0.1234 and 0.12", 0.12345, null, null).Should().Be("near <latitude> and 0.12", "3 decimals near 0°");
        IncidentArchive.Mask("/home/me/x and /home/mean", null, null, "/home/me").Should().Be("~/x and ~an");
    }

    [Test]
    public void Settings_json_has_the_engine_settings_and_the_plugin_settings()
    {
        var json = JsonNode.Parse(IncidentArchive.SettingsJson("{\"ExposureMs\":2000}",
            [new AdvancedGuiderSetting { Name = "Gain", Type = "int", Value = "100" }, new AdvancedGuiderSetting { Name = "SimulateFault", Type = "action", Value = "" }]))!;
        json["engineSettingsAtIncident"]!["ExposureMs"]!.GetValue<int>().Should().Be(2000);
        json["pluginSettingsNow"]!["Gain"]!.GetValue<string>().Should().Be("100");
        json["pluginSettingsNow"]!.AsObject().ContainsKey("SimulateFault").Should().BeFalse();
    }
}

[TestFixture]
public class InternalGuiderIncidentTests
{
    [Test]
    public void Incidents_are_readable_and_manageable_without_a_connection()
    {
        using var t = new GuiderFixture();
        var store = new IncidentStore(t.Directory, new IncidentStoreOptions());
        string a = store.ReserveId(new DateTimeOffset(2026, 9, 26, 2, 13, 45, TimeSpan.Zero), IncidentKind.StarLost);
        store.Save(IncidentSamples.Incident(a), IncidentSamples.Images());
        string b = store.ReserveId(new DateTimeOffset(2026, 9, 26, 3, 0, 0, TimeSpan.Zero), IncidentKind.Spike);
        store.Save(IncidentSamples.Incident(b) with { Start = new DateTimeOffset(2026, 9, 26, 3, 0, 0, TimeSpan.Zero), Kind = IncidentKind.Spike }, []);

        var list = t.Guider.GetIncidents();
        list.Incidents.Select(i => i.Id).Should().Equal(b, a);
        list.Incidents.Should().OnlyContain(i => i.GetType() == typeof(AdvancedIncidentSummary), "the contract promises plain summaries");
        list.Enabled.Should().BeTrue();
        list.RecordingId.Should().BeNull();
        list.UsedBytes.Should().BeGreaterThan(0);
        list.BudgetBytes.Should().Be(1000 * IncidentStoreOptions.MB);
        list.MaxIncidents.Should().Be(50);
        list.SimulatorBudgetBytes.Should().Be(200 * IncidentStoreOptions.MB);

        var incident = t.Guider.GetIncident(a)!;
        incident.Frames.Should().HaveCount(2);
        incident.Frames[0].HasContext.Should().BeTrue();
        var context = t.Guider.GetIncidentImage(a, "context", 11);
        context.Should().Match<AdvancedIncidentImage>(c => c.Kind == "context" && c.Width == 4 && c.Height == 2 && c.Binning == 4 && c.BitDepth == 16 && c.X0 == 0);
        context!.Pixels.Should().HaveCount(8);
        t.Guider.GetIncidentImage(a, "key", 11).Should().NotBeNull();
        t.Guider.GetIncidentImage(a, "key", 12).Should().BeNull();
        t.Guider.GetIncidentImage(a, "whatever", 11).Should().BeNull();
        t.Guider.GetIncidentCrops(a, 11).Select(c => c.Star).Should().Equal(0, 1);

        t.Guider.SetIncidentKept(a, true).Should().BeTrue();
        t.Guider.GetIncidents().Incidents.Single(i => i.Id == a).Kept.Should().BeTrue();
        t.Guider.DeleteAllIncidents().Should().Be(1);
        t.Events.Should().ContainSingle(e => e.Type == "incident").Which.Payload.Should().BeEquivalentTo(new AdvancedIncidentEvent { Action = "deleted", Id = b });
        t.Guider.DeleteIncident(a).Should().BeTrue();
        t.Guider.GetIncidents().Incidents.Should().BeEmpty();
    }

    [Test]
    public void Unknown_ids_never_throw()
    {
        using var t = new GuiderFixture();
        foreach (var id in new[] { "20260101-000000-StarLost", "../../etc", "", null! })
        {
            t.Guider.GetIncident(id).Should().BeNull();
            t.Guider.GetIncidentImage(id, "context", 1).Should().BeNull();
            t.Guider.GetIncidentCrops(id, 1).Should().BeEmpty();
            t.Guider.SetIncidentKept(id, true).Should().BeFalse();
            t.Guider.DeleteIncident(id).Should().BeFalse();
        }

        t.Guider.DeleteAllIncidents().Should().Be(0);
        t.Guider.MarkIncident("note", out var error).Should().BeNull();
        error.Should().Contain("not connected");
    }

    [Test]
    public async Task Archive_is_a_zip_and_unknown_ids_write_nothing()
    {
        using var t = new GuiderFixture();
        var store = new IncidentStore(t.Directory, new IncidentStoreOptions());
        string id = store.ReserveId(new DateTimeOffset(2026, 9, 26, 2, 13, 45, TimeSpan.Zero), IncidentKind.StarLost);
        store.Save(IncidentSamples.Incident(id), IncidentSamples.Images());

        var nothing = new WriteOnlyStream();
        (await t.Guider.WriteIncidentArchive("20260101-000000-StarLost", nothing, CancellationToken.None)).Should().BeFalse();
        nothing.Written.Should().BeEmpty();

        var output = new WriteOnlyStream();
        (await t.Guider.WriteIncidentArchive(id, output, CancellationToken.None)).Should().BeTrue();
        using var zip = new ZipArchive(new MemoryStream(output.Written), ZipArchiveMode.Read);
        zip.Entries.Select(e => e.FullName).Should().Contain(["incident.json", "context.fits", "crops.fits", "key.fits", "settings.json", "guide-log.txt", "pins-log.txt",
            "README.txt"]);
        using var settings = new StreamReader(zip.GetEntry("settings.json")!.Open());
        JsonNode.Parse(settings.ReadToEnd())!["pluginSettingsNow"]!["IncidentRecorder"]!.GetValue<string>().Should().Be("true");
    }

    /// <summary>A write-only, non-seekable stream like an HTTP response.</summary>
    private sealed class WriteOnlyStream : Stream
    {
        private readonly MemoryStream inner = new();

        public byte[] Written => inner.ToArray();

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
    }
}

/// <summary>A disconnected internal guider with its incidents in a temp folder, recording its events.</summary>
internal sealed class GuiderFixture : IDisposable
{
    public GuiderFixture()
    {
        Directory = Path.Combine(Path.GetTempPath(), "pins-guider-incidents-" + Guid.NewGuid().ToString("N"));
        var (_, service, _) = IncidentSettingsTests.Create();
        // every store in the temp folder: the defaults are the user's InternalGuider folder
        Guider = new InternalGuider(service.Object, new Mock<ITelescopeMediator>().Object, new Mock<IGuideCameraMediator>().Object, Directory,
            Path.Combine(Directory, "periodic-error.json"), Path.Combine(Directory, "pulse-model.json"));
        Guider.AdvancedGuiderEvent += (_, e) => Events.Add(e);
    }

    public string Directory { get; }

    public InternalGuider Guider { get; }

    public List<AdvancedGuiderEventArgs> Events { get; } = [];

    public void Dispose()
    {
        if (System.IO.Directory.Exists(Directory))
        {
            System.IO.Directory.Delete(Directory, true);
        }
    }
}

internal static class IncidentSamples
{
    public static Incident Incident(string id)
    {
        var t0 = new DateTimeOffset(2026, 9, 26, 2, 13, 45, TimeSpan.Zero);
        return new Incident
        {
            Id = id,
            Start = t0,
            End = t0.AddSeconds(4),
            Kind = IncidentKind.StarLost,
            Triggers =
            [
                new IncidentTrigger(t0.AddSeconds(2), IncidentKind.StarLost, GuideErrorCode.StarLost, "Guide star lost", "LowSnr", 11),
                new IncidentTrigger(t0.AddSeconds(3), IncidentKind.Spike, null, "Spike: 3″", null, 12),
            ],
            Markers =
            [
                new IncidentMarker(t0.AddSeconds(2), 11, IncidentMarkerType.Trigger, "Guide star lost"),
                new IncidentMarker(t0.AddSeconds(2.5), 12, IncidentMarkerType.Gap, "frames 12-12: telemetry only"),
                new IncidentMarker(t0.AddSeconds(3), 12, IncidentMarkerType.Recovered, "Recovered"),
                new IncidentMarker(t0.AddSeconds(4), 12, IncidentMarkerType.End, "Recovered"),
            ],
            EndReason = IncidentEndReason.Recovered,
            Tags = new IncidentTags { ProfileId = "p", ProfileName = "Rig A", GuideCamera = "ASI", Mount = "EQ6", PixelScale = 2, ImagingScale = 1.1 },
            Context = new IncidentContext { SearchRegionPx = 17 },
            SensorWidth = 1936,
            SensorHeight = 1216,
            ContextBinning = 4,
            SettingsJson = "{\"exposureMs\":2000}",
            FrameCount = 2,
            Frames =
            [
                new IncidentFrameRecord
                {
                    Frame = 11,
                    Time = t0.AddSeconds(2),
                    ExposureMs = 2000,
                    State = GuiderState.Guiding,
                    StarFound = true,
                    Lock = new GuidePoint(100, 50),
                    Star = new GuidePoint(100.3, 49.6),
                    RaDistanceRaw = 0.3,
                    DecDistanceRaw = -0.4,
                    RaDurationMs = 120,
                    RaDirection = GuideDirection.West,
                    DecDurationMs = 0,
                    DecDirection = GuideDirection.North,
                    Snr = 30,
                    Stars = [new StarInfo(100.3, 49.6, 30, 9000, 2.2, true, true, 1, null), new StarInfo(20, 30, 10, 3000, 2.1, false, false, 0, "Miss")],
                    Mount = new MountSnapshot { IsConnected = true, PierSide = PierSide.West, DeclinationDeg = 20, RightAscensionHours = 5 },
                    HasContext = true,
                    Crops = 2,
                },
                new IncidentFrameRecord
                {
                    Frame = 12,
                    Time = t0.AddSeconds(4),
                    ExposureMs = 2000,
                    State = GuiderState.Calibrating,
                    StarFound = false,
                    LostStatus = "LowSnr",
                    Mount = new MountSnapshot { IsConnected = false },
                    CalibrationDirection = "West",
                    CalibrationStep = 3,
                },
            ],
        };
    }

    public static List<IncidentImage> Images() =>
    [
        new() { Frame = 11, Kind = IncidentImageKind.Context, Width = 4, Height = 2, Binning = 4, Pixels = [1, 2, 3, 4, 5, 6, 7, 8] },
        new() { Frame = 11, Kind = IncidentImageKind.Key, Width = 2, Height = 2, Pixels = [9, 9, 9, 9] },
        new() { Frame = 11, Kind = IncidentImageKind.Crop, Star = 0, X0 = 85, Y0 = 35, Width = 2, Height = 1, Pixels = [5, 6] },
        new() { Frame = 11, Kind = IncidentImageKind.Crop, Star = 1, X0 = 5, Y0 = 15, Width = 2, Height = 1, Pixels = [7, 8] },
    ];
}
