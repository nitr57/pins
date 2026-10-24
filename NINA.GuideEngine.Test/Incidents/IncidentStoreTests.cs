// SPDX-License-Identifier: MPL-2.0

using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using FluentAssertions;
using NUnit.Framework;
using NINA.GuideEngine.Core;
using NINA.GuideEngine.Guiding;
using NINA.GuideEngine.Incidents;

namespace NINA.GuideEngine.Test.Incidents;

[TestFixture]
public class IncidentStoreTests
{
    private const long MB = IncidentStoreOptions.MB;
    private static readonly DateTimeOffset T0 = new(2026, 9, 26, 2, 13, 0, TimeSpan.Zero);

    private string dir = null!;

    [SetUp]
    public void SetUp()
    {
        dir = Path.Combine(Path.GetTempPath(), "pins-incidents-" + Guid.NewGuid().ToString("N"));
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public void Round_trip_keeps_the_record_the_images_and_their_flags()
    {
        var store = new IncidentStore(dir, new IncidentStoreOptions());
        var saved = new List<Incident>();
        store.Saved += (_, s) => saved.Add(s);
        string id = store.ReserveId(T0, IncidentKind.StarLost);
        id.Should().Be("20260926-021300-StarLost");

        var incident = Sample(id, T0, frames: 3) with
        {
            Diagnosis = new IncidentDiagnosis(IncidentCause.Clouds, "Likely clouds", new Dictionary<string, object?> { ["dropPercent"] = 80.0, ["axis"] = "ra" },
                [new IncidentEvidence("starsFaded", "Stars faded", new Dictionary<string, object?> { ["stars"] = 5.0, ["ok"] = true }, 2)]),
        };
        var images = new List<IncidentImage>
        {
            Image(1, IncidentImageKind.Context, 16, 8, binning: 4),
            Image(2, IncidentImageKind.Context, 16, 8, binning: 4),
            Image(2, IncidentImageKind.Crop, 31, 31, star: 0, x0: 40, y0: 12),
            Image(2, IncidentImageKind.Crop, 31, 31, star: 3, x0: 7, y0: 9),
            Image(2, IncidentImageKind.Key, 64, 32),
        };

        var summary = store.Save(incident, images);

        summary.Frames.Should().BeEmpty();
        summary.FrameCount.Should().Be(3);
        summary.SizeBytes.Should().BeCloseTo(Directory.GetFiles(Path.Combine(dir, id)).Sum(f => new FileInfo(f).Length), 64, "all files are counted");
        saved.Should().ContainSingle().Which.Id.Should().Be(id);
        store.List().Should().ContainSingle().Which.Kind.Should().Be(IncidentKind.StarLost);
        store.Usage.Should().Be(new IncidentStoreUsage(summary.SizeBytes, 1, 0, 0));

        var back = store.Get(id)!;
        back.Frames.Should().HaveCount(3);
        back.Frames[0].Should().BeEquivalentTo(incident.Frames[0] with { HasContext = true }, o => o.ComparingByMembers<IncidentFrameRecord>());
        back.Frames.Select(f => (f.HasContext, f.HasKey, f.Crops)).Should().Equal((true, false, 0), (true, true, 2), (false, false, 0));
        back.Triggers.Should().BeEquivalentTo(incident.Triggers);
        back.Markers.Should().BeEquivalentTo(incident.Markers);
        back.Tags.Should().Be(incident.Tags);
        back.Context.EarlierSpikes.Should().Equal(incident.Context.EarlierSpikes);
        back.Diagnosis!.Cause.Should().Be(IncidentCause.Clouds);
        back.Diagnosis.Parameters["dropPercent"].Should().Be(80.0);
        back.Diagnosis.Parameters["axis"].Should().Be("ra");
        back.Diagnosis.Evidence[0].Parameters["ok"].Should().Be(true);

        var json = File.ReadAllText(Path.Combine(dir, id, "incident.json"));
        json.Should().Contain("\"StarLost\"").And.Contain("\"Recovered\"").And.Contain("\"Clouds\"", "enums are stored as strings");

        var context = store.ReadImage(id, IncidentImageKind.Context, 2)!;
        context.Pixels.Should().Equal(images[1].Pixels);
        context.Binning.Should().Be(4);
        store.ReadImage(id, IncidentImageKind.Key, 2)!.Pixels.Should().Equal(images[4].Pixels);
        store.ReadImage(id, IncidentImageKind.Key, 1).Should().BeNull();
        var crops = store.ReadCrops(id, 2);
        crops.Select(c => (c.Star, c.X0, c.Y0)).Should().Equal((0, 40, 12), (3, 7, 9));
        crops[1].Pixels.Should().Equal(images[3].Pixels);
        store.ReadCrops(id, 1).Should().BeEmpty();

        // the FITS files stand alone: HDUs with the documented keywords
        var hdus = ReadFits(File.ReadAllBytes(Path.Combine(dir, id, "crops.fits")));
        hdus.Should().HaveCount(2);
        hdus[1].Header["FRAME"].Should().Be("2");
        hdus[1].Header["KIND"].Should().Be("crop");
        hdus[1].Header["STAR"].Should().Be("3");
        hdus[1].Header["X0"].Should().Be("7");
        hdus[1].Header["EXPTIME"].Should().Be("2");
        hdus[1].Header["NOISERED"].Should().Be("None");
        hdus[1].Header.Should().ContainKey("DATE-OBS");
        hdus[1].Pixels.Should().Equal(images[3].Pixels);
    }

    [Test]
    public void Zip_holds_the_files_the_extras_and_a_readme()
    {
        var store = new IncidentStore(dir, new IncidentStoreOptions());
        string id = store.ReserveId(T0, IncidentKind.Spike);
        store.Save(Sample(id, T0, 2), [Image(1, IncidentImageKind.Context, 8, 8), Image(1, IncidentImageKind.Key, 12, 10)]);

        using var ms = new NonSeekableStream();
        store.WriteZip(id, ms, [("settings.json", "{}"), ("guide-log.txt", "line"), ("../evil.txt", "x")]).Should().BeTrue();

        using var zip = new ZipArchive(new MemoryStream(ms.Written), ZipArchiveMode.Read);
        zip.Entries.Select(e => e.FullName).Should().BeEquivalentTo("incident.json", "context.fits", "key.fits", "settings.json", "guide-log.txt", "evil.txt", "README.txt");
        using (var s = zip.GetEntry("key.fits")!.Open())
        {
            using var copy = new MemoryStream();
            s.CopyTo(copy);
            copy.ToArray().Should().Equal(File.ReadAllBytes(Path.Combine(dir, id, "key.fits")));
        }

        using var reader = new StreamReader(zip.GetEntry("README.txt")!.Open());
        reader.ReadToEnd().Should().Contain(id).And.Contain("context.fits").And.Contain("DATE-OBS");
        store.WriteZip("20000101-000000-Nope", new MemoryStream(), []).Should().BeFalse();
    }

    [Test]
    public void Rotation_deletes_the_oldest_incidents_that_are_not_kept()
    {
        var store = new IncidentStore(dir, new IncidentStoreOptions { BudgetBytes = 3 * MB });
        var deleted = new List<string>();
        store.Deleted += (_, id) => deleted.Add(id);
        var ids = new List<string>();
        for (int i = 0; i < 3; i++)
        {
            string id = store.ReserveId(T0.AddMinutes(i), IncidentKind.StarLost);
            store.Save(Sample(id, T0.AddMinutes(i), 2), [Image(1, IncidentImageKind.Key, 512, 1024)]);
            ids.Add(id);
        }

        store.List().Should().HaveCount(2, "each is ~1 MB and the budget 3 MB");
        deleted.Should().Equal(ids[0]);
        store.SetKept(ids[1], true)!.Kept.Should().BeTrue();

        string next = store.ReserveId(T0.AddMinutes(10), IncidentKind.Runaway);
        store.Save(Sample(next, T0.AddMinutes(10), 2), [Image(1, IncidentImageKind.Key, 512, 1024)]);
        deleted.Should().Equal(ids[0], ids[2]);
        store.List().Select(s => s.Id).Should().Equal(next, ids[1]);
        store.Get(ids[1])!.Kept.Should().BeTrue("the flag survives in the summary");
    }

    [Test]
    public void Only_kept_incidents_left_saves_without_images()
    {
        var store = new IncidentStore(dir, new IncidentStoreOptions { BudgetBytes = 3 * MB });
        for (int i = 0; i < 2; i++)
        {
            string id = store.ReserveId(T0.AddMinutes(i), IncidentKind.StarLost);
            store.Save(Sample(id, T0.AddMinutes(i), 2), [Image(1, IncidentImageKind.Key, 512, 1024)]);
            store.SetKept(id, true);
        }

        string full = store.ReserveId(T0.AddMinutes(5), IncidentKind.StarLost);
        var summary = store.Save(Sample(full, T0.AddMinutes(5), 2), [Image(1, IncidentImageKind.Key, 512, 1024)]);

        summary.FramesOmitted.Should().Be(IncidentFramesOmitted.Budget);
        store.List().Should().HaveCount(3);
        store.ReadImage(full, IncidentImageKind.Key, 1).Should().BeNull();
        store.Get(full)!.Frames.Should().OnlyContain(f => !f.HasKey && !f.HasContext, "the telemetry says there are no images");
        store.DeleteAllNotKept().Should().Be(1);
        store.List().Should().HaveCount(2).And.OnlyContain(s => s.Kept);
    }

    [Test]
    public void Disk_space_guard_leaves_the_images_out_after_rotating()
    {
        long free = 2100 * MB;
        var store = new IncidentStore(dir, new IncidentStoreOptions(), _ => free);
        string a = store.ReserveId(T0, IncidentKind.StarLost);
        store.Save(Sample(a, T0, 2), [Image(1, IncidentImageKind.Key, 512, 1024)]).FramesOmitted.Should().Be(IncidentFramesOmitted.None);

        free = 2048 * MB + 100;
        string b = store.ReserveId(T0.AddMinutes(1), IncidentKind.StarLost);
        var summary = store.Save(Sample(b, T0.AddMinutes(1), 2), [Image(1, IncidentImageKind.Key, 512, 1024)]);
        summary.FramesOmitted.Should().Be(IncidentFramesOmitted.DiskSpace);
        store.ReadImage(b, IncidentImageKind.Key, 1).Should().BeNull();
        store.ReadImage(a, IncidentImageKind.Key, 1).Should().NotBeNull();
    }

    [Test]
    public void Simulator_incidents_have_their_own_budget()
    {
        var store = new IncidentStore(dir, new IncidentStoreOptions { BudgetBytes = 100 * MB, SimulatorBudgetBytes = 2 * MB, SimulatorMaxIncidents = 5 });
        string real = store.ReserveId(T0, IncidentKind.StarLost);
        store.Save(Sample(real, T0, 2), [Image(1, IncidentImageKind.Key, 512, 1024)]);
        var sims = new List<string>();
        for (int i = 1; i <= 3; i++)
        {
            string id = store.ReserveId(T0.AddMinutes(i), IncidentKind.Spike);
            store.Save(Sample(id, T0.AddMinutes(i), 2) with { Tags = new IncidentTags { Simulator = true } }, [Image(1, IncidentImageKind.Key, 512, 1024)]);
            sims.Add(id);
        }

        store.List().Select(s => s.Id).Should().BeEquivalentTo(real, sims[2]);
        var usage = store.Usage;
        usage.Count.Should().Be(1);
        usage.SimulatorCount.Should().Be(1);
        usage.SimulatorBytes.Should().BeLessThan(2 * MB);
    }

    [Test]
    public void Incident_count_limit_rotates_too()
    {
        var store = new IncidentStore(dir, new IncidentStoreOptions { MaxIncidents = 2 });
        for (int i = 0; i < 4; i++)
        {
            string id = store.ReserveId(T0.AddMinutes(i), IncidentKind.StarLost);
            store.Save(Sample(id, T0.AddMinutes(i), 1), []);
        }

        store.List().Select(s => s.Start).Should().Equal(T0.AddMinutes(3), T0.AddMinutes(2));
    }

    [Test]
    public void Saving_again_appends_images_and_keeps_the_kept_flag()
    {
        var store = new IncidentStore(dir, new IncidentStoreOptions());
        string id = store.ReserveId(T0, IncidentKind.StarLost);
        store.Save(Sample(id, T0, 2), [Image(1, IncidentImageKind.Context, 8, 8), Image(2, IncidentImageKind.Key, 8, 8)]);
        store.SetKept(id, true);

        var reopened = Sample(id, T0, 5) with { Occurrences = 2 };
        var summary = store.Save(reopened, [Image(5, IncidentImageKind.Context, 8, 8), Image(5, IncidentImageKind.Key, 8, 8)]);

        summary.Kept.Should().BeTrue();
        summary.Occurrences.Should().Be(2);
        var back = store.Get(id)!;
        back.Frames.Select(f => f.HasContext).Should().Equal(true, false, false, false, true);
        back.Frames.Select(f => f.HasKey).Should().Equal(false, true, false, false, true);
        store.ReadImage(id, IncidentImageKind.Key, 2)!.Pixels.Should().Equal(Image(2, IncidentImageKind.Key, 8, 8).Pixels);
        store.ReadImage(id, IncidentImageKind.Context, 5).Should().NotBeNull();
        ReadFits(File.ReadAllBytes(Path.Combine(dir, id, "key.fits"))).Select(h => h.Header["FRAME"]).Should().Equal("2", "5");
    }

    [Test]
    public void Ids_get_a_suffix_when_taken_and_unsafe_ids_are_refused()
    {
        var store = new IncidentStore(dir, new IncidentStoreOptions());
        store.ReserveId(T0, IncidentKind.Manual).Should().Be("20260926-021300-Manual");
        store.ReserveId(T0, IncidentKind.Manual).Should().Be("20260926-021300-Manual-2");
        store.ReserveId(T0, IncidentKind.Manual).Should().Be("20260926-021300-Manual-3");

        store.Get("../etc").Should().BeNull();
        store.Delete("..").Should().BeFalse();
        store.ReadCrops("a/b", 1).Should().BeEmpty();
        store.SetKept("x y", true).Should().BeNull();
        IncidentStore.IsValidId("20260926-021300-StarLost-2").Should().BeTrue();
        IncidentStore.IsValidId("a_b").Should().BeFalse();
        FluentActions.Invoking(() => store.Save(Sample("../x", T0, 1), [])).Should().Throw<ArgumentException>();
    }

    [Test]
    public void Unreadable_folders_are_skipped()
    {
        var store = new IncidentStore(dir, new IncidentStoreOptions());
        string id = store.ReserveId(T0, IncidentKind.StarLost);
        store.Save(Sample(id, T0, 1), []);
        Directory.CreateDirectory(Path.Combine(dir, "20250101-000000-StarLost"));
        File.WriteAllText(Path.Combine(dir, "20250101-000000-StarLost", "summary.json"), "{ not json");
        Directory.CreateDirectory(Path.Combine(dir, "junk.partial-1"));

        var fresh = new IncidentStore(dir, new IncidentStoreOptions());
        fresh.List().Select(s => s.Id).Should().Equal(id);
        fresh.Get("20250101-000000-StarLost").Should().BeNull();
        fresh.Usage.SimulatorCount.Should().Be(0);
        fresh.Usage.Count.Should().Be(1);
    }

    [Test]
    public void Delete_raises_the_event_and_forgets_the_incident()
    {
        var store = new IncidentStore(dir, new IncidentStoreOptions());
        var deleted = new List<string>();
        store.Deleted += (_, id) => deleted.Add(id);
        string id = store.ReserveId(T0, IncidentKind.StarLost);
        store.Save(Sample(id, T0, 1), [Image(1, IncidentImageKind.Context, 8, 8)]);

        store.Delete(id).Should().BeTrue();
        store.Delete(id).Should().BeFalse();
        deleted.Should().Equal(id);
        store.List().Should().BeEmpty();
        Directory.Exists(Path.Combine(dir, id)).Should().BeFalse();
        store.ReadImage(id, IncidentImageKind.Context, 1).Should().BeNull();
    }

    internal static Incident Sample(string id, DateTimeOffset start, int frames) => new()
    {
        Id = id,
        Start = start,
        End = start.AddSeconds(2 * frames),
        Kind = IncidentKind.StarLost,
        Triggers = [new IncidentTrigger(start.AddSeconds(1), IncidentKind.StarLost, GuideErrorCode.StarLost, "Guide star lost", "LowSnr", 1)],
        Markers =
        [
            new IncidentMarker(start.AddSeconds(1), 1, IncidentMarkerType.Trigger, "Guide star lost"),
            new IncidentMarker(start.AddSeconds(2 * frames), frames, IncidentMarkerType.End, "Recovered"),
        ],
        EndReason = IncidentEndReason.Recovered,
        Tags = new IncidentTags { ProfileId = "p1", ProfileName = "Test", GuideCamera = "Cam", Mount = "Mount", PixelScale = 1.5, ImagingScale = 0.9 },
        Context = new IncidentContext { SearchRegionPx = 15, RaRatePxPerMs = 0.004, EarlierSpikes = [start.AddMinutes(-8)] },
        SensorWidth = 64,
        SensorHeight = 32,
        ContextBinning = 4,
        SettingsJson = "{\"exposureMs\":2000}",
        Frames = Enumerable.Range(1, frames).Select(i => new IncidentFrameRecord
        {
            Frame = i,
            Time = start.AddSeconds(2 * i),
            ExposureMs = 2000,
            State = GuiderState.Guiding,
            StarFound = i != 1,
            LostStatus = i == 1 ? "LowSnr" : null,
            Lock = new GuidePoint(30, 16),
            Star = i == 1 ? null : new GuidePoint(30.5, 16.25),
            Dx = 0.5,
            Dy = 0.25,
            RaDistanceRaw = 0.4,
            DecDistanceRaw = -0.3,
            RaDurationMs = 120,
            RaDirection = GuideDirection.West,
            Snr = 25,
            StarMass = 12000,
            Hfd = 2.5,
            Stars = [new StarInfo(30.5, 16.25, 25, 12000, 2.5, true, true, 1, null), new StarInfo(10, 5, 12, 4000, 2.4, false, false, 0, "Miss")],
            Mount = new MountSnapshot { IsConnected = true, DeclinationDeg = 20, PierSide = PierSide.East, RightAscensionHours = 6 },
        }).ToList(),
    };

    internal static IncidentImage Image(long frame, IncidentImageKind kind, int width, int height, int binning = 1, int star = 0, int x0 = 0, int y0 = 0)
    {
        var pixels = new ushort[width * height];
        for (int i = 0; i < pixels.Length; i++)
        {
            pixels[i] = (ushort)((i * 37 + frame * 1000 + (int)kind * 7 + star) % 65536);
        }

        return new IncidentImage
        {
            Frame = frame,
            Kind = kind,
            Star = star,
            X0 = x0,
            Y0 = y0,
            Width = width,
            Height = height,
            Binning = binning,
            Time = T0.AddSeconds(frame * 2),
            ExposureMs = 2000,
            Pixels = pixels,
            Keywords = new Dictionary<string, string> { ["NOISERED"] = "'None    '" },
        };
    }

    /// <summary>Minimal independent reader of 16-bit multi-HDU FITS files (headers and pixels).</summary>
    internal static List<(Dictionary<string, string> Header, ushort[] Pixels)> ReadFits(byte[] data)
    {
        var list = new List<(Dictionary<string, string>, ushort[])>();
        int offset = 0;
        while (offset + 2880 <= data.Length)
        {
            var header = new Dictionary<string, string>();
            bool end = false;
            while (!end)
            {
                for (int c = 0; c < 36; c++)
                {
                    string card = Encoding.ASCII.GetString(data, offset + c * 80, 80);
                    if (card.StartsWith("END", StringComparison.Ordinal) && card.Trim() == "END")
                    {
                        end = true;
                        break;
                    }

                    if (card.Length > 9 && card[8] == '=')
                    {
                        header[card[..8].Trim()] = card[10..].Trim().Trim('\'').Trim();
                    }
                }

                offset += 2880;
            }

            int w = int.Parse(header["NAXIS1"]), h = int.Parse(header["NAXIS2"]);
            header["BITPIX"].Should().Be("16");
            var pixels = new ushort[w * h];
            for (int i = 0; i < pixels.Length; i++)
            {
                pixels[i] = (ushort)(BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(offset + 2 * i, 2)) + 32768);
            }

            offset += (w * h * 2 + 2879) / 2880 * 2880;
            list.Add((header, pixels));
        }

        return list;
    }

    /// <summary>A write-only stream without seeking, like an HTTP response body.</summary>
    private sealed class NonSeekableStream : Stream
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
