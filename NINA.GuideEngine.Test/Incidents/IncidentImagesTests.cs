// SPDX-License-Identifier: MPL-2.0

using System.Text;
using FluentAssertions;
using NUnit.Framework;
using NINA.GuideEngine.Core;
using NINA.GuideEngine.Guiding;
using NINA.GuideEngine.Imaging;
using NINA.GuideEngine.Incidents;

namespace NINA.GuideEngine.Test.Incidents;

/// <summary>The recorder's image helpers, the trigger table and the FITS writer.</summary>
public class IncidentImagesTests
{
    [TestCase(1936, 1216, 4)]
    [TestCase(1280, 960, 3)]
    [TestCase(640, 480, 1)]
    [TestCase(3096, 2080, 6)]
    public void Context_binning_gives_about_480_pixels(int width, int height, int expected) =>
        IncidentRecorder.ContextBinning(width, height).Should().Be(expected);

    [TestCase(1)]
    [TestCase(3)]
    [TestCase(4)]
    [TestCase(5)]
    public void Binning_is_the_rounded_mean_of_whole_blocks(int binning)
    {
        var frame = new GuideFrame(23, 14);
        var rng = new Random(binning);
        for (int i = 0; i < frame.Pixels.Length; i++)
        {
            frame.Pixels[i] = (ushort)rng.Next(0, 65536);
        }

        var binned = IncidentRecorder.BinMean(frame, binning);

        int bw = 23 / binning, bh = 14 / binning;
        binned.Should().HaveCount(bw * bh);
        for (int by = 0; by < bh; by++)
        {
            for (int bx = 0; bx < bw; bx++)
            {
                long sum = 0;
                for (int y = 0; y < binning; y++)
                {
                    for (int x = 0; x < binning; x++)
                    {
                        sum += frame[bx * binning + x, by * binning + y];
                    }
                }

                binned[by * bw + bx].Should().Be((ushort)Math.Round((double)sum / (binning * binning), MidpointRounding.AwayFromZero));
            }
        }
    }

    [Test]
    public void Crops_follow_the_primary_and_the_secondaries_and_stay_inside_the_frame()
    {
        var frame = new GuideFrame(200, 100);
        for (int i = 0; i < frame.Pixels.Length; i++)
        {
            frame.Pixels[i] = (ushort)i;
        }

        var record = new IncidentFrameRecord
        {
            Star = new GuidePoint(100.4, 50.6),
            Stars =
            [
                new StarInfo(3, 97, 10, 1, 2, false, true, 1, null),
                new StarInfo(100.4, 50.6, 30, 1, 2, true, true, 1, null),
                new StarInfo(150, 20, 10, 1, 2, false, false, 0, "Miss"),
            ],
        };

        var crops = IncidentRecorder.Crops(frame, record, searchRegion: 20);

        crops.Select(c => (c.Star, c.Width, c.X0, c.Y0)).Should().Equal((1, 41, 80, 31), (0, 31, 0, 69), (2, 31, 135, 5));
        crops[0].Pixels[0].Should().Be(frame[80, 31]);
        crops[1].Pixels[^1].Should().Be(frame[30, 99]);
        IncidentRecorder.Crops(frame, record, searchRegion: 60)[0].Width.Should().Be(95, "at most 95 px");
        IncidentRecorder.Crops(frame, record with { Star = null, Stars = [], Lock = new GuidePoint(10, 10) }, 15)
            .Should().ContainSingle().Which.Should().Match<IncidentRecorder.Crop>(c => c.X0 == 0 && c.Y0 == 0, "a lost star's crop shows the lock position");
    }

    [Test]
    public void Alert_codes_map_to_the_trigger_table()
    {
        var expected = new Dictionary<GuideErrorCode, IncidentKind>
        {
            [GuideErrorCode.StarLost] = IncidentKind.StarLost,
            [GuideErrorCode.StarReacquireTimeout] = IncidentKind.StarLost,
            [GuideErrorCode.RunawayDetected] = IncidentKind.Runaway,
            [GuideErrorCode.MountNotResponding] = IncidentKind.MountNotResponding,
            [GuideErrorCode.SettleTimeout] = IncidentKind.SettleTimeout,
            [GuideErrorCode.CameraCaptureFailed] = IncidentKind.CameraFailure,
            [GuideErrorCode.CameraReconnecting] = IncidentKind.CameraFailure,
            [GuideErrorCode.CameraFailed] = IncidentKind.CameraFailure,
            [GuideErrorCode.MountSlewing] = IncidentKind.MountPaused,
            [GuideErrorCode.MountParked] = IncidentKind.MountPaused,
            [GuideErrorCode.MountTrackingOff] = IncidentKind.MountPaused,
            [GuideErrorCode.MountDisconnected] = IncidentKind.MountPaused,
            [GuideErrorCode.CalibrationFailedRaNoMove] = IncidentKind.CalibrationFailed,
            [GuideErrorCode.CalibrationFailedDecNoMove] = IncidentKind.CalibrationFailed,
            [GuideErrorCode.CalibrationFailedBacklash] = IncidentKind.CalibrationFailed,
            [GuideErrorCode.CalibrationFailedStarLost] = IncidentKind.CalibrationFailed,
            [GuideErrorCode.PulseLimitReached] = IncidentKind.PulseLimited,
            [GuideErrorCode.PulseOutputFailed] = IncidentKind.PulseOutputFailed,
            [GuideErrorCode.DecFlipCorrected] = IncidentKind.DecFlipCorrected,
        };

        foreach (var code in Enum.GetValues<GuideErrorCode>())
        {
            IncidentRecorder.KindOf(code).Should().Be(expected.TryGetValue(code, out var k) ? k : null, code.ToString());
        }
    }

    [Test]
    public void Fits_writer_writes_a_primary_hdu_and_extensions_with_keywords()
    {
        var a = new GuideFrame(3, 2, [0, 1, 32767, 32768, 65534, 65535]);
        var b = new GuideFrame(2, 2, [7, 8, 9, 10]);
        using var ms = new MemoryStream();
        FitsWriter.Write(ms, [(a, new Dictionary<string, string> { ["OBJECT"] = FitsWriter.Quote("it's a star") }), (b, new Dictionary<string, string> { ["EXPTIME"] = "2.5" })]);

        var bytes = ms.ToArray();
        (bytes.Length % FitsWriter.BlockSize).Should().Be(0);
        Encoding.ASCII.GetString(bytes, 0, 30).Should().StartWith("SIMPLE  =                    T");
        var hdus = IncidentStoreTests.ReadFits(bytes);
        hdus.Should().HaveCount(2);
        hdus[0].Pixels.Should().Equal(a.Pixels);
        hdus[0].Header["OBJECT"].Should().Be("it''s a star", "quotes are doubled inside FITS strings");
        hdus[1].Header["XTENSION"].Should().Be("IMAGE");
        hdus[1].Header["EXPTIME"].Should().Be("2.5");
        hdus[1].Pixels.Should().Equal(b.Pixels);
        FitsWriter.Quote(new string('x', 100)).Length.Should().Be(68);
        FitsWriter.HduSize(3, 2, 1).Should().Be(2 * FitsWriter.BlockSize);
    }
}
