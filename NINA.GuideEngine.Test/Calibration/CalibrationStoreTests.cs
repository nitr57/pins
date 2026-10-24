// SPDX-License-Identifier: MPL-2.0

using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;
using NINA.GuideEngine.Calibration;
using NINA.GuideEngine.Core;

namespace NINA.GuideEngine.Test.Calibration;

[TestFixture]
public class CalibrationStoreTests
{
    private static readonly CalibrationKey Key = new("profile-1", "ZWO ASI120MM Mini", "EQ6-R", 1, 240);

    private static CalibrationData Cal() => new()
    {
        XAngle = 0.5,
        YAngle = 2.07,
        XRate = 0.0041,
        YRate = 0.0044,
        Declination = 0.2,
        PierSide = PierSide.West,
        RotatorAngleDeg = null,
        Binning = 1,
        RaGuideParity = GuideParity.Even,
        DecGuideParity = GuideParity.Odd,
        GuideRateRa = 0.5,
        GuideRateDec = 0.5,
        PixelScale = 3.22,
        PixelSizeUm = 3.75,
        FocalLengthMm = 240,
        Timestamp = new DateTimeOffset(2026, 9, 23, 21, 30, 0, TimeSpan.FromHours(2)),
        RaStepCount = 9,
        DecStepCount = 8,
        RaSteps = [new GuidePoint(0, 0), new GuidePoint(2.5, 1.2)],
        DecSteps = [new GuidePoint(0, 0), new GuidePoint(-1, 2.6)],
        CalibrationStepMs = 750,
        CalibrationDistancePx = 25,
        LastIssue = CalibrationIssueType.Rates,
    };

    private static GuideOptics Optics => new(240, 3.75, 1);

    [Test]
    public void JsonRoundTrip()
    {
        var store = new CalibrationStore();
        store.Set(Key, Cal());
        store.Set(Key with { Binning = 2 }, Cal() with { Binning = 2, XRate = 0.0082 });

        string json = store.ToJson();
        json.Should().Contain("\"pierSide\": \"West\"");

        CalibrationStore back = CalibrationStore.FromJson(json);
        back.Count.Should().Be(2);
        back.TryGet(Key, out CalibrationData? c).Should().BeTrue();
        c.Should().BeEquivalentTo(Cal());
        back.TryGet(Key with { Binning = 2 }, out CalibrationData? c2).Should().BeTrue();
        c2!.XRate.Should().Be(0.0082);
    }

    [Test]
    public void StreamRoundTrip()
    {
        var store = new CalibrationStore();
        store.Set(Key, Cal() with { YRate = double.NaN });
        using var ms = new MemoryStream();
        store.Save(ms);
        ms.Position = 0;
        CalibrationStore back = CalibrationStore.Load(ms);
        back.TryGet(Key, out CalibrationData? c).Should().BeTrue();
        double.IsNaN(c!.YRate).Should().BeTrue();
        c.HasDecCalibration.Should().BeFalse();
    }

    [Test]
    public void SingleCalibrationSerialisation()
    {
        string json = CalibrationStore.SerializeCalibration(Cal());
        CalibrationStore.DeserializeCalibration(json).Should().BeEquivalentTo(Cal());
    }

    [Test]
    public void KeyIsNormalised()
    {
        var store = new CalibrationStore();
        store.Set(Key, Cal());
        store.TryGet(Key with { FocalLengthMm = 240.0001 }, out _).Should().BeTrue();
        store.TryGet(Key with { MountName = "Other" }, out _).Should().BeFalse();
        store.TryGet(Key with { CameraName = "Other" }, out _).Should().BeFalse();
        store.TryGet(Key with { ProfileId = "p2" }, out _).Should().BeFalse();
        store.TryGet(Key with { FocalLengthMm = 250 }, out _).Should().BeFalse();
    }

    [Test]
    public void LookupChecksOptics()
    {
        var store = new CalibrationStore();
        store.Set(Key, Cal());
        store.Lookup(Key, Optics).Validity.Should().Be(CalibrationValidity.Valid);
        store.Lookup(Key, Optics).Usable.Should().NotBeNull();
        store.Lookup(Key, Optics with { PixelSizeUm = 2.9 }).Validity.Should().Be(CalibrationValidity.PixelSizeChanged);
        store.Lookup(Key, Optics with { PixelSizeUm = 2.9 }).Usable.Should().BeNull();
        store.Lookup(Key, Optics with { FocalLengthMm = 250 }).Validity.Should().Be(CalibrationValidity.FocalLengthChanged);
        store.Lookup(Key, Optics with { Binning = 2 }).Validity.Should().Be(CalibrationValidity.BinningChanged);
        store.Lookup(Key with { Binning = 2 }, Optics).Validity.Should().Be(CalibrationValidity.NotFound);

        store.Set(Key, Cal() with { XRate = 0 });
        store.Lookup(Key, Optics).Validity.Should().Be(CalibrationValidity.Invalid);
    }

    [Test]
    public void RemoveAndClear()
    {
        var store = new CalibrationStore();
        store.Set(Key, Cal());
        store.Set(Key with { ProfileId = "p2" }, Cal());
        store.RemoveProfile("profile-1").Should().Be(1);
        store.Count.Should().Be(1);
        store.Remove(Key with { ProfileId = "p2" }).Should().BeTrue();
        store.Count.Should().Be(0);
        store.Set(Key, Cal());
        store.Clear();
        store.Entries.Should().BeEmpty();
    }

    [Test]
    public void EmptyAndFutureVersions()
    {
        CalibrationStore.FromJson("{}").Count.Should().Be(0);
        FluentActions.Invoking(() => CalibrationStore.FromJson("{\"version\": 99, \"entries\": []}"))
            .Should().Throw<JsonException>();
        FluentActions.Invoking(() => CalibrationStore.FromJson("not json")).Should().Throw<JsonException>();
    }
}
