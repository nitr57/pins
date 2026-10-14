// SPDX-License-Identifier: MPL-2.0

using NINA.Mcp.Jobs;
using NINA.Mcp.Logs;
using NINA.Mcp.Observatory;
using NINA.Mcp.Test.Fakes;
using NINA.Mcp.Tools;
using NINA.Mcp.Tools.Catalog;
using NUnit.Framework;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace NINA.Mcp.Test;

/// <summary>Plate solving, guider diagnostics, log, planning and the rotator, flat device and dome tools.</summary>
[TestFixture]
public class ToolsDiagnosticsTests {

    private ToolsTestCatalog catalog = null!;
    private string? tempFile;

    [SetUp]
    public void SetUp() {
        catalog = new ToolsTestCatalog();
    }

    [TearDown]
    public void TearDown() {
        catalog.Dispose();

        if (tempFile is not null && File.Exists(tempFile)) {
            File.Delete(tempFile);
        }
    }

    // ----- Plate solving -----

    [Test]
    public async Task PlateSolve_RunsAsCameraJob_WithSolutionInOutcome() {
        var job = ToolsTestCatalog.Job(await catalog.CallAsync("plate_solve", """{"sync":true}"""));

        Assert.That(job["kind"]!.GetValue<string>(), Is.EqualTo("plate_solve"));
        Assert.That(job["resource"]!.GetValue<string>(), Is.EqualTo("camera"));
        Assert.That(job["state"]!.GetValue<string>(), Is.EqualTo("succeeded"));
        Assert.That(job["outcome"]!["data"]!["raHours"]!.GetValue<double>(), Is.EqualTo(5.5883));
        Assert.That(job["outcome"]!["data"]!["positionAngleDegrees"]!.GetValue<double>(), Is.EqualTo(92.5));
        Assert.That(catalog.Observatory.LastPlateSolveSync, Is.True);
    }

    [Test]
    public async Task PlateSolve_DefaultsToNoSync() {
        await catalog.CallAsync("plate_solve");

        Assert.That(catalog.Observatory.LastPlateSolveSync, Is.False);
    }

    [Test]
    public async Task PlateSolve_FailureKeepsSolutionData() {
        catalog.Observatory.SetResult("PlateSolve", OperationResult.Fail("Solved, but the mount rejected the sync."));

        var job = ToolsTestCatalog.Job(await catalog.CallAsync("plate_solve", """{"sync":true}"""));

        Assert.That(job["state"]!.GetValue<string>(), Is.EqualTo("failed"));
        Assert.That(job["outcome"]!["message"]!.GetValue<string>(), Does.Contain("rejected the sync"));
        Assert.That(job["outcome"]!["data"]!["decDegrees"]!.GetValue<double>(), Is.EqualTo(-5.391));
    }

    [Test]
    public async Task CenterTarget_ParsesSexagesimalAndHoldsMount() {
        var job = ToolsTestCatalog.Job(await catalog.CallAsync("center_target", """{"ra":"05h35m17.3s","dec":"-05:23:28"}"""));

        Assert.That(job["kind"]!.GetValue<string>(), Is.EqualTo("center"));
        Assert.That(job["resource"]!.GetValue<string>(), Is.EqualTo("mount"));
        Assert.That(catalog.Observatory.LastCenter!.Value.Ra, Is.EqualTo(5 + 35 / 60.0 + 17.3 / 3600).Within(1e-9));
        Assert.That(catalog.Observatory.LastCenter!.Value.Dec, Is.EqualTo(-(5 + 23 / 60.0 + 28 / 3600.0)).Within(1e-9));
    }

    [Test]
    public async Task CenterTarget_RefusedWhileCaptureRuns_AndCaptureRefusedWhileCentering() {
        catalog.Observatory.Block("Capture");

        await catalog.CallAsync("capture_image", """{"exposure_seconds":30,"wait_seconds":0}""");

        var refused = Assert.ThrowsAsync<JobConflictException>(() => catalog.CallAsync("center_target", """{"ra":1,"dec":1}"""));

        Assert.That(refused!.Resource, Is.EqualTo("camera"));

        catalog.Observatory.Release("Capture");
        await WaitIdleAsync();

        catalog.Observatory.Block("Center");

        await catalog.CallAsync("center_target", """{"ra":1,"dec":1,"wait_seconds":0}""");
        await catalog.Observatory.WaitForCallAsync("Center");

        // The job holds the mount and reserves camera, guider and filter wheel until it ends.
        var capture = Assert.ThrowsAsync<JobConflictException>(() => catalog.CallAsync("capture_image", """{"exposure_seconds":1}"""));
        var solve = Assert.ThrowsAsync<JobConflictException>(() => catalog.CallAsync("plate_solve"));
        var focuser = Assert.ThrowsAsync<JobConflictException>(() => catalog.CallAsync("move_focuser", """{"position":100}"""));
        var filter = Assert.ThrowsAsync<JobConflictException>(() => catalog.CallAsync("change_filter", """{"name":"L"}"""));
        var guiding = Assert.ThrowsAsync<JobConflictException>(() => catalog.CallAsync("start_guiding"));
        var slew = Assert.ThrowsAsync<JobConflictException>(() => catalog.CallAsync("slew_mount", """{"ra":2,"dec":2}"""));

        Assert.That(capture!.Resource, Is.EqualTo("camera"));
        Assert.That(solve!.Resource, Is.EqualTo("camera"));
        Assert.That(focuser!.Resource, Is.EqualTo("camera"));
        Assert.That(filter!.Resource, Is.EqualTo("filter_wheel"));
        Assert.That(guiding!.Resource, Is.EqualTo("guider"));
        Assert.That(slew!.Resource, Is.EqualTo("mount"));

        catalog.Observatory.Release("Center");
        await WaitIdleAsync();

        Assert.That((await catalog.CallAsync("move_focuser", """{"position":100}""")).IsError, Is.False, "the reservation ends with the job");
    }

    [Test]
    public async Task CenterTarget_Cancelled_KeepsTheFilterWheelUntilTheSolverReturns() {
        // NINA's centering solver restores the original filter on a fresh token after a cancel.
        catalog.Observatory.Block("Center", ignoreCancellation: true);

        var job = ToolsTestCatalog.Job(await catalog.CallAsync("center_target", """{"ra":1,"dec":1,"wait_seconds":0}"""));

        await catalog.Observatory.WaitForCallAsync("Center");

        catalog.Jobs.Cancel(job["id"]!.GetValue<string>());

        Assert.That(() => catalog.Jobs.Reserve(["camera", "guider"], "test").Dispose(), Throws.Nothing, "released on the cancel request");

        var filter = Assert.ThrowsAsync<JobConflictException>(() => catalog.CallAsync("change_filter", """{"name":"L"}"""));

        Assert.That(filter!.Resource, Is.EqualTo("filter_wheel"));

        catalog.Observatory.Release("Center");
        await WaitIdleAsync();

        Assert.That(() => catalog.Jobs.Reserve(["filter_wheel"], "test").Dispose(), Throws.Nothing, "released when the work returned");
    }

    // ----- Guider diagnostics -----

    [Test]
    public async Task GuiderReport_PassesCountsAndReturnsBags() {
        var result = await catalog.CallAsync("get_guider_report", """{"steps":50,"alerts":5}""");

        Assert.That(result.IsError, Is.False, result.Text);
        Assert.That(catalog.Observatory.LastGuiderReport, Is.EqualTo((50, 5)));
        Assert.That(result.Structured!["advanced"]!.GetValue<bool>(), Is.True);
        Assert.That(result.Structured!["status"]!["windowStats"]!["rmsTotalArcsec"]!.GetValue<double>(), Is.EqualTo(0.62));
        Assert.That(result.Structured!["alerts"]!.AsArray(), Has.Count.EqualTo(1));
    }

    [Test]
    public async Task GuiderReport_Defaults() {
        await catalog.CallAsync("get_guider_report");

        Assert.That(catalog.Observatory.LastGuiderReport, Is.EqualTo((0, 20)));
    }

    [Test]
    public async Task CoachAndIncidents_UnavailableIsAnErrorResult() {
        catalog.Observatory.CoachData = GuiderData.Unavailable("The Guiding Coach needs the pins internal guider.");

        var result = await catalog.CallAsync("get_guiding_coach_report", """{"history":1,"include_samples":true}""");

        Assert.That(result.IsError, Is.True);
        Assert.That(result.Text, Does.Contain("internal guider"));
        Assert.That(catalog.Observatory.LastCoach, Is.EqualTo((1, true)));
    }

    [Test]
    public async Task GuideIncident_PassesIdAndFrames() {
        var result = await catalog.CallAsync("get_guide_incident", """{"id":"20261013-213012-StarLost","frames":10}""");

        Assert.That(result.IsError, Is.False, result.Text);
        Assert.That(result.Structured!["data"]!["id"]!.GetValue<string>(), Is.EqualTo("20261013-213012-StarLost"));
        Assert.That(catalog.Observatory.LastIncident, Is.EqualTo(("20261013-213012-StarLost", 10)));
    }

    [Test]
    public void GuideIncident_RejectsPathLikeIds() {
        Assert.That(catalog.ArgumentError("get_guide_incident", """{"id":"../etc/passwd"}"""), Does.Contain("'id'"));
    }

    [Test]
    public async Task ListGuideIncidents_DefaultCount() {
        await catalog.CallAsync("list_guide_incidents");

        Assert.That(catalog.Observatory.LastIncidentCount, Is.EqualTo(20));
    }

    // ----- Property bags -----

    [Test]
    public void PropertyBags_ConvertsDtosToJsonFriendlyValues() {
        var dto = new SampleDto {
            State = "Guiding",
            Rms = double.NaN,
            Snr = 12.5f,
            Kind = DateTimeKind.Utc,
            Timestamp = new DateTime(2026, 10, 13, 20, 0, 0, DateTimeKind.Unspecified),
            Duration = TimeSpan.FromSeconds(90),
            Samples = [1, 2, 3],
            Parameters = new Dictionary<string, object?> { ["Exposure"] = 2.0 },
            Child = new SampleDto { State = "Child" }
        };

        var bag = PropertyBags.FromObject(dto, exclude: ["samples"]);

        Assert.That(bag["state"], Is.EqualTo("Guiding"));
        Assert.That(bag.ContainsKey("rms"), Is.True);
        Assert.That(bag["rms"], Is.Null);
        Assert.That(bag["snr"], Is.EqualTo(12.5));
        Assert.That(bag["kind"], Is.EqualTo("Utc"));
        Assert.That(bag["timestamp"], Is.EqualTo("2026-10-13T20:00:00.0000000Z"));
        Assert.That(bag["duration"], Is.EqualTo(90.0));
        Assert.That(bag.ContainsKey("samples"), Is.False);
        Assert.That(((Dictionary<string, object?>)bag["parameters"]!)["Exposure"], Is.EqualTo(2.0), "dictionary keys are kept as they are");
        Assert.That(((Dictionary<string, object?>)bag["child"]!)["state"], Is.EqualTo("Child"));
    }

    [Test]
    public void PropertyBags_CutsListsAndDepth() {
        var dto = new SampleDto { Samples = Enumerable.Range(0, 10).ToList() };

        var bag = PropertyBags.FromObject(dto, maxItems: 3);
        var shallow = PropertyBags.FromObject(new SampleDto { Child = new SampleDto { Child = new SampleDto() } }, maxDepth: 2);

        Assert.That((List<object?>)bag["samples"]!, Has.Count.EqualTo(3));
        Assert.That(((Dictionary<string, object?>)shallow["child"]!)["child"], Is.Null);
        Assert.That(PropertyBags.FromObject(null), Is.Empty);
        Assert.That(PropertyBags.FromObject(5)["value"], Is.EqualTo(5));
    }

    // ----- Log reader -----

    [Test]
    public void LogReader_ParsesStructuredAndPlainLines() {
        Assert.That(PinsLogReader.TryParseLine("2026-10-13T22:59:01.1234|WARNING|Center.cs|DoCenter|142|Plate solve failed | retrying", out var entry), Is.True);
        Assert.That(entry.Level, Is.EqualTo(PinsLogLevel.Warning));
        Assert.That(entry.Source, Is.EqualTo("Center.cs"));
        Assert.That(entry.Member, Is.EqualTo("DoCenter"));
        Assert.That(entry.Line, Is.EqualTo(142));
        Assert.That(entry.Message, Is.EqualTo("Plate solve failed | retrying"));

        Assert.That(PinsLogReader.TryParseLine("2026-10-13T22:59:01.1234|INFO|plain message", out var plain), Is.True);
        Assert.That(plain.Source, Is.Null);
        Assert.That(plain.Message, Is.EqualTo("plain message"));

        // Logger.Error(Exception) writes no message field; the exception follows on the next lines.
        Assert.That(PinsLogReader.TryParseLine("2026-10-13T22:59:01.1234|ERROR|Center.cs|DoCenter|142", out var bare), Is.True);
        Assert.That(bare.Source, Is.EqualTo("Center.cs"));
        Assert.That(bare.Line, Is.EqualTo(142));
        Assert.That(bare.Message, Is.Empty);

        Assert.That(PinsLogReader.TryParseLine("DATE|LEVEL|SOURCE|MEMBER|LINE|MESSAGE", out _), Is.False);
        Assert.That(PinsLogReader.TryParseLine("   at NINA.Foo.Bar()", out _), Is.False);
        Assert.That(PinsLogReader.TryParseLine("2026-10-13T22:59:01.1234|NOISE|x|y|1|z", out _), Is.False);
    }

    [Test]
    public void LogReader_FiltersByLevelAndText_AttachesExceptions() {
        var log = new StringBuilder()
            .AppendLine("----------------------------------------------------------------------")
            .AppendLine("DATE|LEVEL|SOURCE|MEMBER|LINE|MESSAGE")
            .AppendLine("2026-10-13T21:00:00.0000|INFO|Seq.cs|Run|1|Sequence started")
            .AppendLine("2026-10-13T21:05:00.0000|ERROR|Center.cs|DoCenter|2|Platesolve failed")
            .AppendLine("System.Exception: no stars")
            .AppendLine("   at NINA.PlateSolving.ImageSolver.Solve()")
            .AppendLine("2026-10-13T21:06:00.0000|WARNING|Seq.cs|Run|3|Item skipped")
            .ToString();

        var errors = Read(log, new PinsLogQuery(10, PinsLogLevel.Error));
        var plate = Read(log, new PinsLogQuery(10, PinsLogLevel.Trace, Contains: "imagesolver"));
        var all = Read(log, new PinsLogQuery(10, PinsLogLevel.Trace));

        Assert.That(errors.Entries, Has.Count.EqualTo(1));
        Assert.That(errors.Entries[0].Detail, Is.EqualTo("System.Exception: no stars\n   at NINA.PlateSolving.ImageSolver.Solve()"));
        Assert.That(plate.Entries.Single().Message, Is.EqualTo("Platesolve failed"), "text matches the exception detail too");
        Assert.That(all.Entries.Select(entry => entry.Message), Is.EqualTo(new[] { "Sequence started", "Platesolve failed", "Item skipped" }));
        Assert.That(all.ReachedStart, Is.True);
        Assert.That(all.Entries[0].Detail, Is.Null);
    }

    [Test]
    public void LogReader_LongStackTraceKeepsItsTop() {
        var log = new StringBuilder("2026-10-13T21:05:00.0000|ERROR|Center.cs|DoCenter|2\n")
            .Append("System.InvalidOperationException: the solver crashed\n");

        for (var i = 0; i < 200; i++) {
            log.Append(CultureInfo.InvariantCulture, $"   at Frame{i}()\n");
        }

        var entry = Read(log.ToString(), new PinsLogQuery(10, PinsLogLevel.Error)).Entries.Single();

        Assert.That(entry.Detail, Does.StartWith("System.InvalidOperationException: the solver crashed\n   at Frame0()"));
        Assert.That(entry.Detail, Does.Not.Contain("Frame199"));
    }

    [Test]
    public void LogReader_StopsAtSinceAndAtMaxEntries() {
        var log = new StringBuilder();

        for (var minute = 0; minute < 60; minute++) {
            log.Append(CultureInfo.InvariantCulture, $"2026-10-13T21:{minute:00}:00.0000|INFO|A.cs|M|1|entry {minute}\n");
        }

        var since = Read(log.ToString(), new PinsLogQuery(100, PinsLogLevel.Info, Since: new DateTime(2026, 10, 13, 21, 50, 0)));
        var newest = Read(log.ToString(), new PinsLogQuery(5, PinsLogLevel.Info));

        Assert.That(since.Entries.Select(entry => entry.Message).First(), Is.EqualTo("entry 50"));
        Assert.That(since.Entries, Has.Count.EqualTo(10));
        Assert.That(since.ReachedSince, Is.True);
        Assert.That(since.ReachedStart, Is.False);
        Assert.That(newest.Entries.Select(entry => entry.Message), Is.EqualTo(new[] { "entry 55", "entry 56", "entry 57", "entry 58", "entry 59" }));
    }

    [Test]
    public void LogReader_HandlesChunkBoundariesMultibyteTextAndMissingFinalNewline() {
        var log = new StringBuilder();

        // ~300 KB: several 64 KB chunks, with multi-byte characters so that chunk edges fall inside lines.
        for (var i = 0; i < 3000; i++) {
            log.Append(CultureInfo.InvariantCulture, $"2026-10-13T21:00:00.0000|INFO|A.cs|M|1|entry {i} – focus at -10 °C {new string('x', i % 50)}\r\n");
        }

        log.Append("2026-10-13T22:00:00.0000|ERROR|A.cs|M|1|last line without newline");

        var result = Read(log.ToString(), new PinsLogQuery(5000, PinsLogLevel.Trace));

        Assert.That(result.Entries, Has.Count.EqualTo(3001));
        Assert.That(result.Entries[1234].Message, Does.StartWith("entry 1234 – focus at -10 °C"));
        Assert.That(result.Entries[^1].Message, Is.EqualTo("last line without newline"));
        Assert.That(result.Entries.All(entry => !entry.Message.EndsWith('\r')), Is.True);
    }

    [Test]
    public void LogReader_RespectsScanBudget() {
        var log = new StringBuilder();

        for (var i = 0; i < 5000; i++) {
            log.Append(CultureInfo.InvariantCulture, $"2026-10-13T21:00:00.0000|INFO|A.cs|M|1|entry {i} {new string('y', 80)}\n");
        }

        var result = Read(log.ToString(), new PinsLogQuery(10, PinsLogLevel.Error, MaxScanBytes: 64 * 1024));

        Assert.That(result.Entries, Is.Empty);
        Assert.That(result.ReachedStart, Is.False);
        Assert.That(result.ScannedBytes, Is.LessThanOrEqualTo(64 * 1024));
    }

    // ----- read_log -----

    [Test]
    public async Task ReadLog_DefaultsToWarnings_FromTheCurrentRun() {
        UseLogFile(
            "2026-10-13T21:00:00.0000|INFO|A.cs|M|1|started\n"
            + "2026-10-13T21:01:00.0000|WARNING|A.cs|M|2|guide star lost\n"
            + "2026-10-13T21:02:00.0000|ERROR|B.cs|N|3|sequence aborted\n");

        var result = await catalog.CallAsync("read_log");

        Assert.That(result.IsError, Is.False, result.Text);
        Assert.That(catalog.Observatory.LastLogPreviousRun, Is.False);

        var entries = result.Structured!["entries"]!.AsArray();

        Assert.That(entries.Select(entry => entry!["message"]!.GetValue<string>()), Is.EqualTo(new[] { "guide star lost", "sequence aborted" }));
        Assert.That(entries[1]!["level"]!.GetValue<string>(), Is.EqualTo("error"));
        Assert.That(result.Structured!["coverage"]!["wholeFile"]!.GetValue<bool>(), Is.True);
    }

    [Test]
    public async Task ReadLog_LevelTextAndPreviousRun() {
        UseLogFile(
            "2026-10-13T21:00:00.0000|DEBUG|A.cs|M|1|camera download 1.2 s\n"
            + "2026-10-13T21:00:01.0000|INFO|A.cs|M|1|mount slewing\n");

        var result = await catalog.CallAsync("read_log", """{"min_level":"debug","contains":"CAMERA","previous_run":true,"lines":3}""");

        Assert.That(result.Structured!["count"]!.GetValue<int>(), Is.EqualTo(1));
        Assert.That(catalog.Observatory.LastLogPreviousRun, Is.True);
    }

    [Test]
    public async Task ReadLog_ContinuesIntoEarlierSegmentsOfTheRun() {
        UseLogFile("2026-10-13T21:02:00.0000|ERROR|B.cs|N|3|sequence aborted\n");

        var newest = tempFile!;
        var older = Path.Combine(Path.GetTempPath(), $"pins-mcp-test-{Guid.NewGuid():N}.log");

        File.WriteAllText(older, "2026-10-13T20:00:00.0000|WARNING|A.cs|M|2|guide star lost\n");

        try {
            catalog.Observatory.LogSource = new LogSource(newest, Path.GetFileName(newest), [older]);

            var all = await catalog.CallAsync("read_log");
            var newestOnly = await catalog.CallAsync("read_log", """{"lines":1}""");

            Assert.That(all.Structured!["entries"]!.AsArray().Select(entry => entry!["message"]!.GetValue<string>()), Is.EqualTo(new[] { "guide star lost", "sequence aborted" }));
            Assert.That(all.Structured!["earlierSegmentsRead"]![0]!.GetValue<string>(), Is.EqualTo(Path.GetFileName(older)));
            Assert.That(newestOnly.Structured!["count"]!.GetValue<int>(), Is.EqualTo(1));
            Assert.That(newestOnly.Structured!["earlierSegmentsRead"], Is.Null, "enough entries in the newest segment");
        } finally {
            File.Delete(older);
        }
    }

    [Test]
    public async Task ReadLog_ScansOneAtATime() {
        UseLogFile("2026-10-13T21:01:00.0000|WARNING|A.cs|M|2|guide star lost\n");

        await ReadLogTool.ScanGate.WaitAsync();

        Task<ToolCallResult> waiting;

        try {
            // A scan is running (the gate is held): the next one waits for it instead of reading in parallel.
            waiting = catalog.CallAsync("read_log");

            await Task.Delay(200);

            Assert.That(waiting.IsCompleted, Is.False);
        } finally {
            ReadLogTool.ScanGate.Release();
        }

        var result = await waiting.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.That(result.Structured!["count"]!.GetValue<int>(), Is.EqualTo(1));
        Assert.That(ReadLogTool.ScanGate.CurrentCount, Is.EqualTo(1), "the gate is released after the scan");
    }

    [Test]
    public async Task ReadLog_NoFileIsAnError() {
        var result = await catalog.CallAsync("read_log");

        Assert.That(result.IsError, Is.True);
        Assert.That(result.Text, Does.Contain("No log file"));
    }

    [Test]
    public void ReadLog_RejectsUnknownLevel() {
        Assert.That(catalog.ArgumentError("read_log", """{"min_level":"verbose"}"""), Does.Contain("min_level"));
    }

    // ----- plan_targets -----

    [Test]
    public async Task PlanTargets_SummarizesTheNight() {
        var result = await catalog.CallAsync(
            "plan_targets",
            """{"targets":[{"name":"M 31","ra":"00h42m44s","dec":41.27}],"min_altitude":30}""");

        Assert.That(result.IsError, Is.False, result.Text);

        var request = catalog.Observatory.LastNightSkyRequest!;

        Assert.That(request.Night, Is.Null);
        Assert.That(request.Step, Is.EqualTo(TimeSpan.FromMinutes(5)));
        Assert.That(request.Targets.Single().RaHours, Is.EqualTo(0 + 42 / 60.0 + 44 / 3600.0).Within(1e-9));

        var plan = result.Structured!;

        AssertTime(plan["sun"]!["astronomicalDusk"], 18, 46);
        AssertTime(plan["sun"]!["astronomicalDawn"], 5, 14);
        Assert.That(plan["sun"]!["darkHours"]!.GetValue<double>(), Is.EqualTo(10.46).Within(0.1));
        Assert.That(plan["moon"]!["illuminationPercent"]!.GetValue<double>(), Is.EqualTo(42));
        AssertTime(plan["moon"]!["rise"], 3, 0);
        AssertTime(plan["moon"]!["set"], 15, 0);

        var target = plan["targets"]![0]!;

        Assert.That(target["name"]!.GetValue<string>(), Is.EqualTo("M 31"));
        AssertTime(target["transit"]!["time"], 0, 0);
        Assert.That(target["transit"]!["altitude"]!.GetValue<double>(), Is.EqualTo(70));
        Assert.That(target["transit"]!["inDark"]!.GetValue<bool>(), Is.True);
        AssertTime(target["risesAboveLimit"], 16, 42);
        AssertTime(target["setsBelowLimit"], 7, 18);
        Assert.That(target["usableDarkHours"]!.GetValue<double>(), Is.EqualTo(10.46).Within(0.1));
        AssertTime(target["bestWindow"]!["from"], 18, 46);
        Assert.That(target["bestWindow"]!["minMoonSeparation"]!.GetValue<double>(), Is.EqualTo(90));
        Assert.That(target["now"]!["aboveMinAltitude"]!.GetValue<bool>(), Is.True);
        Assert.That(plan["hourly"], Is.Null);
    }

    [Test]
    public async Task PlanTargets_CustomHorizonRaisesTheLimit_AndHourlyTable() {
        // A horizon of 75° (above the target's 70° transit) until sample 150 (h = 12.5, 00:30), then 0°: the target only
        // counts from when the horizon drops away.
        var horizon = Enumerable.Range(0, ToolsNightSky.Samples).Select(i => (double?)(i < 150 ? 75 : 0)).ToList();

        catalog.Observatory.NightSkyFactory = request => ToolsNightSky.Create(request.Targets, horizon);

        var result = await catalog.CallAsync(
            "plan_targets",
            """{"targets":[{"name":"T","ra":1,"dec":1}],"date":"2026-10-13","hourly":true,"darkness":"nautical"}""");

        Assert.That(result.IsError, Is.False, result.Text);
        Assert.That(catalog.Observatory.LastNightSkyRequest!.Night, Is.EqualTo(new DateOnly(2026, 10, 13)));

        var plan = result.Structured!;
        var target = plan["targets"]![0]!;

        AssertTime(target["bestWindow"]!["from"], 0, 30);
        Assert.That(plan["sun"]!["darkness"]!.GetValue<string>(), Is.EqualTo("nautical"));
        Assert.That(plan["site"]!["customHorizon"]!.GetValue<bool>(), Is.True);

        var hourly = plan["hourly"]!.AsArray();

        Assert.That(hourly.Count, Is.GreaterThan(8));
        Assert.That(hourly.All(row => row!["sun"]!.GetValue<double>() < -6), Is.True);
        Assert.That(hourly[0]!["targets"]!.AsArray(), Has.Count.EqualTo(1));
    }

    [Test]
    public async Task PlanTargets_NeverHighEnough_IsNoted() {
        var result = await catalog.CallAsync("plan_targets", """{"targets":[{"name":"Low","ra":1,"dec":-60}],"min_altitude":80}""");

        var target = result.Structured!["targets"]![0]!;

        Assert.That(target["bestWindow"], Is.Null);
        Assert.That(target["usableDarkHours"]!.GetValue<double>(), Is.EqualTo(0));
        Assert.That(target["notes"]![0]!.GetValue<string>(), Does.Contain("Never above 80°"));
    }

    [Test]
    public async Task PlanTargets_RiseAndSetBoundTheBestWindow() {
        // Transit at 16:00 (h = 4): above 30° from the start of the night until 23:18 (h = 11.3), and again from 08:42 the
        // next morning (h = 20.7), after the dark. The first crossings of the whole night would put "sets" (23:18) before
        // "rises" (08:42).
        catalog.Observatory.NightSkyFactory = request => {
            var sky = ToolsNightSky.Create(request.Targets);
            var altitude = Enumerable.Range(0, ToolsNightSky.Samples).Select(i => 30 * Math.Cos(2 * Math.PI * (i * 5 / 60.0 - 4) / 24) + 40).ToList();

            return sky with { Targets = sky.Targets.Select(target => target with { Altitude = altitude }).ToList() };
        };

        var result = await catalog.CallAsync("plan_targets", """{"targets":[{"name":"T","ra":1,"dec":1}],"min_altitude":30}""");
        var target = result.Structured!["targets"]![0]!;

        Assert.That(target["risesAboveLimit"], Is.Null, "already above the limit when the night starts");
        AssertTime(target["setsBelowLimit"], 23, 18);
        AssertTime(target["bestWindow"]!["from"], 18, 46);
        AssertTime(target["bestWindow"]!["to"], 23, 15);
    }

    [Test]
    public async Task PlanTargets_AllDarkNight_Is24Hours() {
        // Polar night with a circumpolar target: every one of the 289 samples is dark and usable.
        catalog.Observatory.NightSkyFactory = request => {
            var sky = ToolsNightSky.Create(request.Targets);

            return sky with {
                SunAltitude = Enumerable.Repeat(-30.0, ToolsNightSky.Samples).ToList(),
                Targets = sky.Targets.Select(target => target with { Altitude = Enumerable.Repeat(60.0, ToolsNightSky.Samples).ToList() }).ToList()
            };
        };

        var result = await catalog.CallAsync("plan_targets", """{"targets":[{"name":"T","ra":1,"dec":89}]}""");
        var target = result.Structured!["targets"]![0]!;

        Assert.That(result.Structured!["sun"]!["darkHours"]!.GetValue<double>(), Is.EqualTo(24));
        Assert.That(target["usableDarkHours"]!.GetValue<double>(), Is.EqualTo(24));
        Assert.That(target["bestWindow"]!["hours"]!.GetValue<double>(), Is.EqualTo(24));
    }

    [Test]
    public async Task PlanTargets_PassesTheObservatorysNotesOn() {
        catalog.Observatory.NightSkyFactory = request => ToolsNightSky.Create(request.Targets) with {
            Notes = ["The pins box's time zone (UTC+00:00) is about 10 h away from the site's solar time."]
        };

        var result = await catalog.CallAsync("plan_targets", """{"targets":[{"name":"T","ra":1,"dec":1}]}""");

        Assert.That(result.Structured!["notes"]!.AsArray().Select(note => note!.GetValue<string>()), Has.Some.Contains("10 h away"));
    }

    [Test]
    public async Task PlanTargets_ObservatoryFailureAndInconsistentDataAreErrors() {
        catalog.Observatory.NightSkyFactory = _ => NightSky.Fail("The site location is not set.", DateTimeOffset.UnixEpoch);

        var failed = await catalog.CallAsync("plan_targets", """{"targets":[{"name":"T","ra":1,"dec":1}]}""");

        catalog.Observatory.NightSkyFactory = request => ToolsNightSky.Create([]);

        var inconsistent = await catalog.CallAsync("plan_targets", """{"targets":[{"name":"T","ra":1,"dec":1}]}""");

        Assert.That(failed.IsError, Is.True);
        Assert.That(failed.Text, Does.Contain("site location"));
        Assert.That(inconsistent.IsError, Is.True);
    }

    [Test]
    public void PlanTargets_ValidatesArguments() {
        var eleven = string.Join(",", Enumerable.Range(0, 11).Select(i => $$"""{"name":"T{{i}}","ra":1,"dec":1}"""));

        Assert.That(catalog.ArgumentError("plan_targets", """{"targets":[]}"""), Does.Contain("targets"));
        Assert.That(catalog.ArgumentError("plan_targets", $$"""{"targets":[{{eleven}}]}"""), Does.Contain("targets"));
        Assert.That(catalog.ArgumentError("plan_targets", """{"targets":[{"name":"T","ra":25,"dec":1}]}"""), Does.Contain("targets[0].ra"));
        Assert.That(catalog.ArgumentError("plan_targets", """{"targets":[{"name":"T","ra":1,"dec":1}],"date":"2026-13-45"}"""), Does.Contain("'date'"));
        Assert.That(catalog.ArgumentError("plan_targets", """{"targets":[{"name":"T","ra":1,"dec":1}],"date":"tonight"}"""), Does.Contain("'date'"));
    }

    // ----- Rotator, flat device, dome -----

    [Test]
    public async Task MoveRotator_PassesPositionAndMode() {
        var job = ToolsTestCatalog.Job(await catalog.CallAsync("move_rotator", """{"position":181.5,"mechanical":true}"""));

        Assert.That(job["resource"]!.GetValue<string>(), Is.EqualTo("rotator"));
        Assert.That(catalog.Observatory.LastRotator, Is.EqualTo((181.5, true)));
        Assert.That(catalog.ArgumentError("move_rotator", """{"position":360}"""), Does.Contain("'position'"));
    }

    [Test]
    public async Task RotatorAndCaptureExcludeEachOther() {
        catalog.Observatory.Block("MoveRotator");

        await catalog.CallAsync("move_rotator", """{"position":10,"wait_seconds":0}""");

        var capture = Assert.ThrowsAsync<JobConflictException>(() => catalog.CallAsync("capture_image", """{"exposure_seconds":1}"""));

        Assert.That(capture!.Resource, Is.EqualTo("rotator"));

        catalog.Observatory.Release("MoveRotator");
        await WaitIdleAsync();

        catalog.Observatory.Block("Capture");

        await catalog.CallAsync("capture_image", """{"exposure_seconds":30,"wait_seconds":0}""");

        var rotator = Assert.ThrowsAsync<JobConflictException>(() => catalog.CallAsync("move_rotator", """{"position":10}"""));
        var flat = Assert.ThrowsAsync<JobConflictException>(() => catalog.CallAsync("control_flat_device", """{"action":"close_cover"}"""));

        Assert.That(rotator!.Resource, Is.EqualTo("camera"));
        Assert.That(flat!.Resource, Is.EqualTo("camera"));

        catalog.Observatory.Release("Capture");
    }

    [TestCase("open_cover", FlatDeviceAction.OpenCover)]
    [TestCase("close_cover", FlatDeviceAction.CloseCover)]
    [TestCase("Light On", FlatDeviceAction.LightOn)]
    [TestCase("light_off", FlatDeviceAction.LightOff)]
    public async Task FlatDevice_MapsActions(string action, FlatDeviceAction expected) {
        var result = await catalog.CallAsync("control_flat_device", $$"""{"action":"{{action}}"}""");

        Assert.That(result.IsError, Is.False, result.Text);
        Assert.That(catalog.Observatory.LastFlatDevice, Is.EqualTo((expected, (int?)null)));
    }

    [Test]
    public async Task FlatDevice_BrightnessOnlyWithSetBrightness() {
        await catalog.CallAsync("control_flat_device", """{"action":"set_brightness","brightness":120}""");

        Assert.That(catalog.Observatory.LastFlatDevice, Is.EqualTo((FlatDeviceAction.SetBrightness, (int?)120)));
        Assert.That(catalog.ArgumentError("control_flat_device", """{"action":"set_brightness"}"""), Does.Contain("required"));
        Assert.That(catalog.ArgumentError("control_flat_device", """{"action":"light_on","brightness":5}"""), Does.Contain("only applies"));
    }

    [TestCase("open_shutter", DomeAction.OpenShutter)]
    [TestCase("close_shutter", DomeAction.CloseShutter)]
    [TestCase("park", DomeAction.Park)]
    [TestCase("find_home", DomeAction.FindHome)]
    [TestCase("sync_to_mount", DomeAction.SyncToMount)]
    [TestCase("start_following", DomeAction.StartFollowing)]
    [TestCase("stop_following", DomeAction.StopFollowing)]
    public async Task Dome_MapsActions(string action, DomeAction expected) {
        var job = ToolsTestCatalog.Job(await catalog.CallAsync("control_dome", $$"""{"action":"{{action}}"}"""));

        Assert.That(job["resource"]!.GetValue<string>(), Is.EqualTo("dome"));
        Assert.That(catalog.Observatory.LastDome, Is.EqualTo((expected, (double?)null)));
    }

    [Test]
    public async Task Dome_AzimuthOnlyWithSlew() {
        await catalog.CallAsync("control_dome", """{"action":"slew_to_azimuth","azimuth":271}""");

        Assert.That(catalog.Observatory.LastDome, Is.EqualTo((DomeAction.SlewToAzimuth, (double?)271)));
        Assert.That(catalog.ArgumentError("control_dome", """{"action":"slew_to_azimuth"}"""), Does.Contain("required"));
        Assert.That(catalog.ArgumentError("control_dome", """{"action":"park","azimuth":10}"""), Does.Contain("only applies"));
    }

    [Test]
    public async Task Dome_CloseIsNotBlockedByCameraOrMountJobs() {
        catalog.Observatory.Block("Capture");
        catalog.Observatory.Block("Slew");

        await catalog.CallAsync("capture_image", """{"exposure_seconds":30,"wait_seconds":0}""");
        catalog.Observatory.Release("Capture");
        await WaitIdleAsync();
        await catalog.CallAsync("slew_mount", """{"ra":1,"dec":1,"wait_seconds":0}""");

        var close = await catalog.CallAsync("control_dome", """{"action":"close_shutter"}""");

        Assert.That(ToolsTestCatalog.Job(close)["state"]!.GetValue<string>(), Is.EqualTo("succeeded"));

        catalog.Observatory.Release("Slew");
    }

    // ----- Helpers -----

    private sealed class SampleDto {

        public string? State { get; set; }

        public double Rms { get; set; }

        public float Snr { get; set; }

        public DateTimeKind Kind { get; set; }

        public DateTime Timestamp { get; set; }

        public TimeSpan Duration { get; set; }

        public List<int>? Samples { get; set; }

        public Dictionary<string, object?>? Parameters { get; set; }

        public SampleDto? Child { get; set; }
    }

    private static PinsLogReadResult Read(string text, PinsLogQuery query) {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));

        return PinsLogReader.ReadTail(stream, query);
    }

    private void UseLogFile(string content) {
        tempFile = Path.Combine(Path.GetTempPath(), $"pins-mcp-test-{Guid.NewGuid():N}.log");
        File.WriteAllText(tempFile, content);
        catalog.Observatory.LogSource = new LogSource(tempFile, Path.GetFileName(tempFile));
    }

    private async Task WaitIdleAsync() {
        for (var i = 0; i < 200 && catalog.Jobs.List().Any(job => job.State == JobState.Running); i++) {
            await Task.Delay(10);
        }
    }

    /// <summary>Asserts a "yyyy-MM-ddTHH:mm+02:00" time within 5 minutes of the given local clock time.</summary>
    private static void AssertTime(JsonNode? node, int hour, int minute) {
        Assert.That(node, Is.Not.Null);

        var time = DateTimeOffset.Parse(node!.GetValue<string>(), CultureInfo.InvariantCulture);
        var minutes = time.Hour * 60 + time.Minute;
        var expected = hour * 60 + minute;
        var difference = Math.Abs(minutes - expected);

        Assert.That(Math.Min(difference, 1440 - difference), Is.LessThanOrEqualTo(5), node.GetValue<string>());
        Assert.That(time.Offset, Is.EqualTo(TimeSpan.FromHours(2)));
    }
}
