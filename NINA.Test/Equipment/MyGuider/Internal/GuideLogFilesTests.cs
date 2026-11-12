// SPDX-License-Identifier: MPL-2.0

using FluentAssertions;
using NINA.Equipment.Equipment.MyGuider.Internal;
using NUnit.Framework;

namespace NINA.Test.Equipment.MyGuider.Internal;

[TestFixture]
public class GuideLogFilesTests
{
    private string dir = null!;

    [SetUp]
    public void SetUp()
    {
        dir = Path.Combine(Path.GetTempPath(), "pins-guidelogs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private string Write(string name, string text = "PHD2 version x\n")
    {
        string path = Path.Combine(dir, name);
        File.WriteAllText(path, text);
        return path;
    }

    [Test]
    public void List_ReturnsGuideLogsNewestNightFirst_WithActiveFlagAndZone()
    {
        Write("InternalGuider_GuideLog_2026-10-11.txt");
        string active = Write("InternalGuider_GuideLog_2026-10-12.txt", "PHD2 version x\nmore\n");
        Write("PinsGuider_GuideLog_2026-09-23.txt");
        Write("notes.txt");
        Write("InternalGuider_GuideLog_2026-10-10.log");

        var zone = TimeZoneInfo.CreateCustomTimeZone("Test/Zone", TimeSpan.FromHours(1), "Test", "Test");
        var list = GuideLogFiles.List(dir, active, zone, new DateTime(2026, 11, 10, 0, 0, 0, DateTimeKind.Utc));

        list.Select(f => f.Name).Should().Equal(
            "InternalGuider_GuideLog_2026-10-12.txt", "InternalGuider_GuideLog_2026-10-11.txt", "PinsGuider_GuideLog_2026-09-23.txt");
        list[0].Active.Should().BeTrue();
        list[1].Active.Should().BeFalse();
        list[0].Night.Should().Be("2026-10-12");
        list[0].SizeBytes.Should().Be(new FileInfo(active).Length);
        list[0].TimeZone.Should().Be("Test/Zone");
        list[0].UtcOffsetMinutes.Should().Be(60);
    }

    [Test]
    public void List_MissingDirectory_IsEmpty()
    {
        GuideLogFiles.List(Path.Combine(dir, "nope"), null, TimeZoneInfo.Utc, DateTime.UtcNow).Should().BeEmpty();
    }

    [Test]
    public void Open_ReadsALogThatIsStillBeingWritten()
    {
        string path = Path.Combine(dir, "InternalGuider_GuideLog_2026-10-12.txt");
        using var writer = new StreamWriter(path, append: true) { AutoFlush = true };
        writer.Write("line 1\n");

        using var stream = GuideLogFiles.Open(dir, "InternalGuider_GuideLog_2026-10-12.txt");
        stream.Should().NotBeNull();
        new StreamReader(stream!).ReadToEnd().Should().Be("line 1\n");
    }

    [TestCase("../InternalGuider_GuideLog_2026-10-12.txt")]
    [TestCase("InternalGuider_GuideLog_2026-10-12.txt/../x")]
    [TestCase("/etc/passwd")]
    [TestCase("notes.txt")]
    [TestCase("")]
    public void Open_RejectsNamesThatAreNoGuideLogs(string name)
    {
        Write("notes.txt");
        GuideLogFiles.Open(dir, name).Should().BeNull();
    }

    [Test]
    public void Open_UnknownLog_IsNull()
    {
        GuideLogFiles.Open(dir, "InternalGuider_GuideLog_2026-01-01.txt").Should().BeNull();
    }

    [Test]
    public void List_PerSessionLogStartedAfterMidnight_BelongsToTheNightBefore()
    {
        Write("PinsGuider_GuideLog_2026-09-24_013005.txt");
        Write("PinsGuider_GuideLog_2_2026-09-23_213005.txt");
        Write("PinsGuider_GuideLog_2026-09-24_133005.txt");

        var nights = GuideLogFiles.List(dir, null, TimeZoneInfo.Utc, DateTime.UtcNow).ToDictionary(f => f.Name, f => f.Night);

        nights["PinsGuider_GuideLog_2026-09-24_013005.txt"].Should().Be("2026-09-23");
        nights["PinsGuider_GuideLog_2_2026-09-23_213005.txt"].Should().Be("2026-09-23");
        nights["PinsGuider_GuideLog_2026-09-24_133005.txt"].Should().Be("2026-09-24");
    }

    [Test]
    public void List_OffsetIsTheOneOfTheLogsNight()
    {
        Write("InternalGuider_GuideLog_2026-07-10.txt");
        Write("InternalGuider_GuideLog_2026-12-10.txt");
        Write("InternalGuider_GuideLog_undated.txt");
        var zone = TimeZoneInfo.CreateCustomTimeZone("Test/Dst", TimeSpan.FromHours(1), "Test", "Test", "Test Summer",
        [
            TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(DateTime.MinValue.Date, DateTime.MaxValue.Date, TimeSpan.FromHours(1),
                TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 5, DayOfWeek.Sunday),
                TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 3, 0, 0), 10, 5, DayOfWeek.Sunday)),
        ]);

        // listed in winter
        var offsets = GuideLogFiles.List(dir, null, zone, new DateTime(2026, 12, 20, 12, 0, 0, DateTimeKind.Utc))
            .ToDictionary(f => f.Name, f => f.UtcOffsetMinutes);

        offsets["InternalGuider_GuideLog_2026-07-10.txt"].Should().Be(120);
        offsets["InternalGuider_GuideLog_2026-12-10.txt"].Should().Be(60);
        offsets["InternalGuider_GuideLog_undated.txt"].Should().Be(60);
    }

    [Test]
    public void Open_MissingDirectory_IsNull()
    {
        GuideLogFiles.Open(Path.Combine(dir, "nope"), "InternalGuider_GuideLog_2026-01-01.txt").Should().BeNull();
    }

    [Test]
    public void Open_ErrorOtherThanMissing_IsThrown()
    {
        // a directory where the log should be: not "no such log", an error to report
        Directory.CreateDirectory(Path.Combine(dir, "InternalGuider_GuideLog_2026-01-01.txt"));

        FluentActions.Invoking(() => GuideLogFiles.Open(dir, "InternalGuider_GuideLog_2026-01-01.txt"))
            .Should().Throw<UnauthorizedAccessException>();
    }
}
