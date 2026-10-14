// SPDX-License-Identifier: MPL-2.0

using NINA.Mcp.Logs;
using NUnit.Framework;

namespace NINA.Mcp.Test;

/// <summary>Which log file read_log reads: this run's (found through the open file) or the previous run's.</summary>
[TestFixture]
public class PinsLogFilesTests {

    private static readonly DateTime Noon = new(2026, 10, 13, 12, 0, 0, DateTimeKind.Utc);

    // systemd gives pins the same process id on every boot: both runs carry .812-.
    private static readonly PinsLogFile PreviousRun = File("20261012-201500-3.3.0.1.812-.log", Noon.AddDays(-1));
    private static readonly PinsLogFile ThisRunFirstSegment = File("20261013-180000-3.3.0.1.812-.log", Noon.AddHours(1));
    private static readonly PinsLogFile ThisRunSecondSegment = File("20261013-180000-3.3.0.1.812-001.log", Noon.AddHours(5));

    private static readonly PinsLogFile[] Files = [PreviousRun, ThisRunFirstSegment, ThisRunSecondSegment];

    [Test]
    public void CurrentRun_IsTheNewestSegmentOfTheOpenFilesRun() {
        var selection = PinsLogFiles.Select(Files, "/home/pi/.local/share/NINA/Logs/20261013-180000-3.3.0.1.812-001.log", 812, previousRun: false);

        Assert.That(selection.File, Is.EqualTo(ThisRunSecondSegment));
    }

    [Test]
    public void PreviousRun_SkipsEverySegmentOfThisRun_EvenWithTheSameProcessId() {
        var selection = PinsLogFiles.Select(Files, "20261013-180000-3.3.0.1.812-001.log", 812, previousRun: true);

        Assert.That(selection.File, Is.EqualTo(PreviousRun));
    }

    [Test]
    public void WithoutOpenFile_TheProcessIdDecides() {
        var files = new[] { File("20261012-201500-3.3.0.1.700-.log", Noon), File("20261013-180000-3.3.0.1.812-.log", Noon.AddHours(-2)) };

        Assert.That(PinsLogFiles.Select(files, null, 812, previousRun: false).File!.Name, Is.EqualTo("20261013-180000-3.3.0.1.812-.log"));
        Assert.That(PinsLogFiles.Select(files, null, 812, previousRun: true).File!.Name, Is.EqualTo("20261012-201500-3.3.0.1.700-.log"));
    }

    [Test]
    public void OpenFileWithoutProcessId_OnlyThatFileIsThisRun() {
        // The logger renamed its file after a clock correction; the name no longer carries this process id.
        var renamed = File("20261013-190000-3.3.0.1.900-.log", Noon.AddHours(6));
        var files = Files.Append(renamed).ToArray();

        Assert.That(PinsLogFiles.Select(files, renamed.Name, 812, previousRun: false).File, Is.EqualTo(renamed));
    }

    [Test]
    public void EarlierSegmentsOfTheRun_NewestFirst() {
        var third = File("20261013-180000-3.3.0.1.812-002.log", Noon.AddHours(9));
        var files = Files.Append(third).ToArray();

        var current = PinsLogFiles.Select(files, third.Name, 812, previousRun: false);
        var previous = PinsLogFiles.Select(files, third.Name, 812, previousRun: true);

        Assert.That(current.File, Is.EqualTo(third));
        Assert.That(current.EarlierSegments, Is.EqualTo(new[] { ThisRunSecondSegment, ThisRunFirstSegment }));
        Assert.That(previous.File, Is.EqualTo(PreviousRun));
        Assert.That(previous.EarlierSegments, Is.Empty, "the previous run has one file; this run's files share the pid but not the start");
    }

    [Test]
    public void NoMatch_IsNull() {
        Assert.That(PinsLogFiles.Select([PreviousRun], null, 999, previousRun: false).File, Is.Null);
        Assert.That(PinsLogFiles.Select([], null, 812, previousRun: true).File, Is.Null);
    }

    private static PinsLogFile File(string name, DateTime written) {
        return new PinsLogFile("/logs/" + name, written);
    }
}
