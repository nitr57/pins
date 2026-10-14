// SPDX-License-Identifier: MPL-2.0

using NINA.Mcp.Jobs;
using NINA.Mcp.Observatory;
using NINA.Mcp.Test.Fakes;
using NUnit.Framework;

namespace NINA.Mcp.Test;

/// <summary>find_targets, get_session_summary, get_system_health and secure_observatory.</summary>
[TestFixture]
public class ToolsSessionAndSafetyTests {

    private static readonly DateTimeOffset Night = new(2026, 10, 11, 20, 0, 0, TimeSpan.Zero);

    private ToolsTestCatalog catalog = null!;

    [SetUp]
    public void SetUp() {
        catalog = new ToolsTestCatalog();
    }

    [TearDown]
    public void TearDown() {
        catalog.Dispose();
    }

    // ----- find_targets -----

    [Test]
    public async Task FindTargets_ByName_DefaultsToNameOrder() {
        var result = await catalog.CallAsync("find_targets", """{"name":" M 42 "}""");

        Assert.That(result.IsError, Is.False, result.Text);

        var search = catalog.Observatory.LastCatalogSearch!;

        Assert.That(search.Name, Is.EqualTo("M 42"));
        Assert.That(search.Order, Is.EqualTo("name"));
        Assert.That(search.Limit, Is.EqualTo(10));

        var item = result.Structured!["objects"]![0]!;

        Assert.That(item["raHours"]!.GetValue<double>(), Is.EqualTo(5.5881));
        Assert.That(item["ra"]!.GetValue<string>(), Does.StartWith("05h35m"));
        Assert.That(item["sizeArcmin"]!.GetValue<double>(), Is.EqualTo(85));
        Assert.That(item["aliases"]!.AsArray(), Has.Count.EqualTo(3));
    }

    [Test]
    public async Task FindTargets_Filters_DefaultToBrightestFirst() {
        await catalog.CallAsync(
            "find_targets",
            """{"types":"brtnb, SNREM,BRTNB","constellation":"cyg","max_magnitude":9,"min_size_arcmin":10,"max_size_arcmin":200,"limit":25}""");

        var search = catalog.Observatory.LastCatalogSearch!;

        Assert.That(search.Types, Is.EqualTo(new[] { "BRTNB", "SNREM" }));
        Assert.That(search.Constellation, Is.EqualTo("CYG"));
        Assert.That(search.MaxMagnitude, Is.EqualTo(9));
        Assert.That(search.MinSizeArcmin, Is.EqualTo(10));
        Assert.That(search.MaxSizeArcmin, Is.EqualTo(200));
        Assert.That(search.Order, Is.EqualTo("magnitude"));
        Assert.That(search.Limit, Is.EqualTo(25));
    }

    [Test]
    public void FindTargets_ValidatesArguments() {
        Assert.That(catalog.ArgumentError("find_targets", "{}"), Does.Contain("at least one filter"));
        Assert.That(catalog.ArgumentError("find_targets", """{"min_size_arcmin":30,"max_size_arcmin":10}"""), Does.Contain("min_size_arcmin"));
        Assert.That(catalog.ArgumentError("find_targets", """{"constellation":"Orion"}"""), Does.Contain("'constellation'"));
        Assert.That(catalog.ArgumentError("find_targets", """{"name":"M 1","order":"distance"}"""), Does.Contain("'order'"));
    }

    [Test]
    public async Task FindTargets_FailureIsAnError() {
        catalog.Observatory.CatalogResult = new CatalogSearchResult(false, "The catalog search failed: database locked", []);

        var result = await catalog.CallAsync("find_targets", """{"name":"M 1"}""");

        Assert.That(result.IsError, Is.True);
        Assert.That(result.Text, Does.Contain("database locked"));
    }

    // ----- get_session_summary -----

    [Test]
    public async Task SessionSummary_GroupsLightsAndFlagsSuspects() {
        AddLight(1, "M 31", "L", 2.0, 400);
        AddLight(2, "M 31", "L", 2.1, 410);
        AddLight(3, "M 31", "L", 2.0, 390);
        AddLight(4, "M 31", "L", 3.2, 380);
        AddLight(5, "M 31", "L", 2.05, 120);
        AddLight(6, "M 31", "Ha", 2.5, 200);
        catalog.Observatory.Images.Add(Image(7, "SNAPSHOT", "M 31", "L", 1.0, 50, Night.AddMinutes(70)));

        var result = await catalog.CallAsync("get_session_summary");

        Assert.That(result.IsError, Is.False, result.Text);

        var summary = result.Structured!;

        Assert.That(summary["frames"]!.GetValue<int>(), Is.EqualTo(6));
        Assert.That(summary["integrationHours"]!.GetValue<double>(), Is.EqualTo(0.5));
        Assert.That(summary["imageTypesInHistory"]!["SNAPSHOT"]!.GetValue<int>(), Is.EqualTo(1));

        var groups = summary["groups"]!.AsArray();
        var luminance = groups.Single(group => group!["filter"]!.GetValue<string>() == "L")!;

        Assert.That(groups, Has.Count.EqualTo(2));
        Assert.That(luminance["frames"]!.GetValue<int>(), Is.EqualTo(5));
        Assert.That(luminance["hfr"]!["median"]!.GetValue<double>(), Is.EqualTo(2.05));
        Assert.That(luminance["hfr"]!["worst"]!.GetValue<double>(), Is.EqualTo(3.2));
        Assert.That(luminance["suspectCount"]!.GetValue<int>(), Is.EqualTo(2));
        Assert.That(
            luminance["suspectFrames"]!.AsArray().Select(frame => frame!["id"]!.GetValue<int>()),
            Is.EqualTo(new[] { 4, 5 }));
        Assert.That(luminance["suspectFrames"]![1]!["reason"]!.GetValue<string>(), Does.Contain("120 stars"));
    }

    [Test]
    public async Task SessionSummary_AllTypesAndSince() {
        AddLight(1, "M 31", "L", 2.0, 400);
        catalog.Observatory.Images.Add(Image(2, "SNAPSHOT", null, null, 2.0, 400, Night.AddMinutes(55)));

        // The fake's status time is 2026-10-11 21:00 UTC: frame 1 is 58 minutes old, frame 2 five minutes.
        var all = await catalog.CallAsync("get_session_summary", """{"image_type":"all","group_by":"target"}""");
        var recent = await catalog.CallAsync("get_session_summary", """{"image_type":"all","since_hours":0.5}""");

        Assert.That(all.Structured!["frames"]!.GetValue<int>(), Is.EqualTo(2));
        Assert.That(all.Structured!["groups"]!.AsArray().Select(group => group!["target"]!.GetValue<string>()), Is.EquivalentTo(new[] { "M 31", "(no target)" }));
        Assert.That(recent.Structured!["frames"]!.GetValue<int>(), Is.EqualTo(1));
    }

    [Test]
    public async Task SessionSummary_EmptyHistory() {
        var result = await catalog.CallAsync("get_session_summary");

        Assert.That(result.Structured!["frames"]!.GetValue<int>(), Is.EqualTo(0));
        Assert.That(result.Structured!["groups"]!.AsArray(), Is.Empty);
        Assert.That(result.Structured!["next"]!.GetValue<string>(), Does.Contain("No frames"));
    }

    // ----- get_system_health -----

    [Test]
    public async Task SystemHealth_ReportsClockOffsetAndPowerWarnings() {
        catalog.Observatory.Health = catalog.Observatory.Health with {
            ClockSynchronized = false,
            Throttling = ThrottleState.Parse("throttled=0x50000")
        };

        var result = await catalog.CallAsync("get_system_health", """{"client_time":"2026-10-13T21:05:00Z"}""");

        Assert.That(result.IsError, Is.False, result.Text);
        Assert.That(result.Structured!["clockOffsetSeconds"]!.GetValue<double>(), Is.EqualTo(-300));

        var warnings = result.Structured!["warnings"]!.AsArray().Select(warning => warning!.GetValue<string>()).ToList();

        Assert.That(warnings.Any(warning => warning.Contains("-300 s off")), Is.True, string.Join(" | ", warnings));
        Assert.That(warnings.Any(warning => warning.Contains("not NTP-synchronized")), Is.True);
        Assert.That(warnings.Any(warning => warning.Contains("Under-voltage occurred since boot")), Is.True);
    }

    [Test]
    public async Task SystemHealth_HealthyBoxHasNoWarnings() {
        var result = await catalog.CallAsync("get_system_health", """{"client_time":"2026-10-13T21:00:10+00:00"}""");

        Assert.That(result.Structured!["clockOffsetSeconds"]!.GetValue<double>(), Is.EqualTo(-10));
        Assert.That(result.Structured!["warnings"]!.AsArray(), Is.Empty);
        Assert.That(result.Structured!["imageDisk"]!["freeGb"]!.GetValue<double>(), Is.EqualTo(120));
    }

    [Test]
    public void SystemHealth_RejectsBadClientTime() {
        Assert.That(catalog.ArgumentError("get_system_health", """{"client_time":"yesterday"}"""), Does.Contain("client_time"));
    }

    [Test]
    public void ThrottleState_DecodesVcgencmdBits() {
        var state = ThrottleState.Parse("throttled=0x50005")!;

        Assert.That(state.UnderVoltageNow, Is.True);
        Assert.That(state.FrequencyCappedNow, Is.False);
        Assert.That(state.ThrottledNow, Is.True);
        Assert.That(state.UnderVoltageSinceBoot, Is.True);
        Assert.That(state.ThrottledSinceBoot, Is.True);
        Assert.That(state.FrequencyCappedSinceBoot, Is.False);
        Assert.That(ThrottleState.Parse("0x0")!.UnderVoltageSinceBoot, Is.False);
        Assert.That(ThrottleState.Parse("error"), Is.Null);
        Assert.That(ThrottleState.Parse(null), Is.Null);
    }

    // ----- secure_observatory -----

    [Test]
    public async Task Secure_StopsParksAndCloses_InOrder() {
        ConnectAll(atPark: false);
        catalog.Observatory.SequenceRunning = true;

        var job = ToolsTestCatalog.Job(await catalog.CallAsync("secure_observatory", """{"warm_camera":true}"""));

        Assert.That(job["state"]!.GetValue<string>(), Is.EqualTo("succeeded"), job.ToJsonString());

        var calls = catalog.Observatory.Calls
            .Where(call => call is not "GetStatus")
            .ToList();

        Assert.That(calls, Is.EqualTo(new[] { "StopSequence", "AbortExposure", "StopGuiding", "Park", "Dome", "FlatDevice", "WarmCamera" }));
        Assert.That(catalog.Observatory.LastDome, Is.EqualTo((DomeAction.CloseShutter, (double?)null)));
        Assert.That(catalog.Observatory.LastFlatDevice, Is.EqualTo((FlatDeviceAction.CloseCover, (int?)null)));
        Assert.That(job["outcome"]!["data"]!.AsArray().Select(step => step!["step"]!.GetValue<string>()), Is.EqualTo(new[] {
            "stop_sequence", "cancel_jobs", "abort_exposure", "stop_guiding", "park_mount", "close_dome", "close_cover", "warm_camera"
        }));
    }

    [Test]
    public async Task Secure_DoesNotCloseTheDomeWhenParkingFails() {
        ConnectAll(atPark: false);
        catalog.Observatory.SetResult("Park", OperationResult.Fail("The mount could not be parked."));

        var job = ToolsTestCatalog.Job(await catalog.CallAsync("secure_observatory"));

        Assert.That(job["state"]!.GetValue<string>(), Is.EqualTo("failed"));
        Assert.That(catalog.Observatory.Calls, Does.Not.Contain("Dome"));
        Assert.That(job["outcome"]!["message"]!.GetValue<string>(), Does.Contain("park_mount").And.Contain("close_dome"));
    }

    [Test]
    public async Task Secure_NeverClosesTheDomeOverAnUnparkedMount() {
        ConnectAll(atPark: false);

        var job = ToolsTestCatalog.Job(await catalog.CallAsync("secure_observatory", """{"park":false}"""));

        Assert.That(job["state"]!.GetValue<string>(), Is.EqualTo("failed"));
        Assert.That(catalog.Observatory.Calls, Does.Not.Contain("Park"));
        Assert.That(catalog.Observatory.Calls, Does.Not.Contain("Dome"));
    }

    [Test]
    public async Task Secure_SkipsDisconnectedDevicesAndOptOuts() {
        ConnectAll(atPark: true);

        var job = ToolsTestCatalog.Job(await catalog.CallAsync("secure_observatory", """{"park":false,"close_cover":false}"""));

        Assert.That(job["state"]!.GetValue<string>(), Is.EqualTo("succeeded"));
        Assert.That(catalog.Observatory.Calls, Does.Not.Contain("Park"));
        Assert.That(catalog.Observatory.Calls, Does.Not.Contain("FlatDevice"));
        Assert.That(catalog.Observatory.Calls, Does.Contain("Dome"));
    }

    [Test]
    public async Task Secure_CancelsRunningJobsFirst() {
        catalog.Observatory.Block("Slew");

        await catalog.CallAsync("slew_mount", """{"ra":1,"dec":1,"wait_seconds":0}""");

        var job = ToolsTestCatalog.Job(await catalog.CallAsync("secure_observatory"));
        var slew = catalog.Jobs.List().Single(entry => entry.Kind == "slew");

        Assert.That(slew.State, Is.EqualTo(JobState.Cancelled));
        Assert.That(job["state"]!.GetValue<string>(), Is.EqualTo("succeeded"));
    }

    // ----- Helpers -----

    private void AddLight(int id, string target, string filter, double hfr, int stars) {
        catalog.Observatory.Images.Add(Image(id, "LIGHT", target, filter, hfr, stars, Night.AddMinutes(id * 2)));
    }

    private static ImageRecord Image(int id, string type, string? target, string? filter, double hfr, int stars, DateTimeOffset time) {
        return new ImageRecord(id, time, type, filter, 300, hfr, stars, 1000, 990, 40, -10, 0.6, target, null, 2.4, 0.45, 12000);
    }

    private void ConnectAll(bool atPark) {
        catalog.Observatory.Devices.Clear();
        catalog.Observatory.Devices.Add(new DeviceStatus(DeviceKind.Camera, true, "Camera", new Dictionary<string, object?>()));
        catalog.Observatory.Devices.Add(new DeviceStatus(DeviceKind.Guider, true, "Guider", new Dictionary<string, object?>()));
        catalog.Observatory.Devices.Add(new DeviceStatus(DeviceKind.Mount, true, "Mount", new Dictionary<string, object?> { ["atPark"] = atPark }));
        catalog.Observatory.Devices.Add(new DeviceStatus(DeviceKind.Dome, true, "Dome", new Dictionary<string, object?> { ["shutter"] = "ShutterOpen" }));
        catalog.Observatory.Devices.Add(new DeviceStatus(DeviceKind.FlatDevice, true, "Panel", new Dictionary<string, object?> { ["coverState"] = "Open" }));
    }
}
