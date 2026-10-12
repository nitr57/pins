// SPDX-License-Identifier: MPL-2.0

using NINA.Mcp.Observatory;
using NINA.Mcp.Test.Fakes;
using NINA.Mcp.Tools;
using NUnit.Framework;

namespace NINA.Mcp.Test;

/// <summary>Argument validation shared by every tool (schema-driven) and tool-specific argument handling.</summary>
[TestFixture]
public class ToolsArgumentTests {

    private ToolsTestCatalog catalog = null!;

    [SetUp]
    public void SetUp() {
        catalog = new ToolsTestCatalog();
    }

    [TearDown]
    public void TearDown() {
        catalog.Dispose();
    }

    // ----- Generic validation -----

    [Test]
    public void UnknownArgument_IsRejectedWithAllowedList() {
        var message = catalog.ArgumentError("slew_mount", """{"ra":5,"dec":10,"speed":3}""");

        Assert.That(message, Does.Contain("Unknown argument 'speed'"));
        Assert.That(message, Does.Contain("ra, dec, wait_seconds, force"));
    }

    [Test]
    public void UnknownArgument_OnArgumentlessTool_SaysItTakesNone() {
        var message = catalog.ArgumentError("get_status", """{"verbose":true}""");

        Assert.That(message, Does.Contain("takes no arguments"));
    }

    [Test]
    public void NullArgument_CountsAsAbsentButStillMustBeKnown() {
        Assert.DoesNotThrowAsync(() => catalog.CallAsync("get_recent_images", """{"count":null}"""));

        var message = catalog.ArgumentError("get_status", """{"bogus":null}""");

        Assert.That(message, Does.Contain("'bogus'"));
    }

    [TestCase("slew_mount", """{"dec":10}""", "'ra' is required")]
    [TestCase("slew_mount", """{"ra":5}""", "'dec' is required")]
    [TestCase("capture_image", "{}", "'exposure_seconds' is required")]
    [TestCase("set_tracking", "{}", "'enabled' is required")]
    [TestCase("connect_device", "{}", "'device' is required")]
    [TestCase("move_focuser", "{}", "'position' is required")]
    [TestCase("set_switch", """{"index":0}""", "'value' is required")]
    [TestCase("get_job", "{}", "'job_id' is required")]
    [TestCase("cancel_job", "{}", "'job_id' is required")]
    [TestCase("load_sequence", "{}", "'name' is required")]
    [TestCase("save_sequence", "{}", "'name' is required")]
    [TestCase("cool_camera", "{}", "'target_celsius' is required")]
    public void MissingRequired_IsRejected(string tool, string json, string expected) {
        Assert.That(catalog.ArgumentError(tool, json), Does.Contain(expected));
    }

    [TestCase("get_recent_images", """{"count":0}""", "'count' must be at least 1 and at most 50")]
    [TestCase("get_recent_images", """{"count":51}""", "'count'")]
    [TestCase("get_recent_images", """{"count":2.5}""", "'count' must be an integer")]
    [TestCase("get_recent_images", """{"count":"many"}""", "'count' must be a number")]
    [TestCase("get_sequence", """{"max_depth":0}""", "'max_depth'")]
    [TestCase("get_sequence", """{"max_depth":11}""", "'max_depth'")]
    [TestCase("capture_image", """{"exposure_seconds":0}""", "'exposure_seconds' must be at least 0.0001")]
    [TestCase("capture_image", """{"exposure_seconds":3600.5}""", "'exposure_seconds'")]
    [TestCase("capture_image", """{"exposure_seconds":"NaN"}""", "'exposure_seconds'")]
    [TestCase("capture_image", """{"exposure_seconds":"Infinity"}""", "'exposure_seconds'")]
    [TestCase("capture_image", """{"exposure_seconds":1e400}""", "'exposure_seconds'")]
    [TestCase("capture_image", """{"exposure_seconds":"2,5"}""", "'exposure_seconds' must be a number")]
    [TestCase("capture_image", """{"exposure_seconds":1,"binning":5}""", "'binning'")]
    [TestCase("capture_image", """{"exposure_seconds":1,"binning":0}""", "'binning'")]
    [TestCase("capture_image", """{"exposure_seconds":1,"gain":-1}""", "'gain'")]
    [TestCase("capture_image", """{"exposure_seconds":1,"save":"maybe"}""", "'save' must be a boolean")]
    [TestCase("capture_image", """{"exposure_seconds":1,"filter":""}""", "'filter' must not be empty")]
    [TestCase("cool_camera", """{"target_celsius":-51}""", "'target_celsius'")]
    [TestCase("cool_camera", """{"target_celsius":31}""", "'target_celsius'")]
    [TestCase("cool_camera", """{"target_celsius":-10,"duration_minutes":61}""", "'duration_minutes'")]
    [TestCase("cool_camera", """{"target_celsius":-10,"duration_minutes":-1}""", "'duration_minutes'")]
    [TestCase("warm_camera", """{"duration_minutes":60.1}""", "'duration_minutes'")]
    [TestCase("move_focuser", """{"position":-1}""", "'position' must be at least 0")]
    [TestCase("move_focuser", """{"position":100.5}""", "'position' must be an integer")]
    [TestCase("set_switch", """{"index":-1,"value":1}""", "'index'")]
    [TestCase("set_tracking", """{"enabled":"yes"}""", "'enabled' must be a boolean")]
    [TestCase("slew_mount", """{"ra":24,"dec":0}""", "'ra'")]
    [TestCase("slew_mount", """{"ra":"24:00:00","dec":0}""", "'ra'")]
    [TestCase("slew_mount", """{"ra":5,"dec":-90.01}""", "'dec'")]
    [TestCase("slew_mount", """{"ra":5,"dec":"+91:00:00"}""", "'dec'")]
    [TestCase("slew_mount", """{"ra":5,"dec":0,"force":"sure"}""", "'force' must be a boolean")]
    [TestCase("connect_device", """{"device":"telescope"}""", "'device' must be one of: camera, mount, focuser, filter_wheel")]
    [TestCase("get_job", """{"job_id":"job-1","wait_seconds":121}""", "'wait_seconds'")]
    [TestCase("get_job", """{"job_id":"job-1","wait_seconds":-1}""", "'wait_seconds'")]
    [TestCase("slew_mount", """{"ra":5,"dec":0,"wait_seconds":500}""", "'wait_seconds' must be at least 0 and at most 55")]
    [TestCase("start_guiding", """{"force_calibration":1}""", "'force_calibration' must be a boolean")]
    public void InvalidValue_IsRejected(string tool, string json, string expected) {
        Assert.That(catalog.ArgumentError(tool, json), Does.Contain(expected));
    }

    [Test]
    public void InvalidArguments_DoNotReachTheObservatory() {
        catalog.ArgumentError("slew_mount", """{"ra":30,"dec":0}""");
        catalog.ArgumentError("capture_image", """{"exposure_seconds":-1}""");

        Assert.That(catalog.Observatory.Calls, Is.Empty);
    }

    [Test]
    public void NumbersSentAsStrings_AreAccepted() {
        Assert.DoesNotThrowAsync(() => catalog.CallAsync("get_recent_images", """{"count":"5"}"""));

        Assert.That(catalog.Observatory.LastImageCount, Is.EqualTo(5));
    }

    // ----- Device kinds -----

    [TestCase("camera", DeviceKind.Camera)]
    [TestCase("mount", DeviceKind.Mount)]
    [TestCase("filter_wheel", DeviceKind.FilterWheel)]
    [TestCase("FilterWheel", DeviceKind.FilterWheel)]
    [TestCase("filter wheel", DeviceKind.FilterWheel)]
    [TestCase("Filter-Wheel", DeviceKind.FilterWheel)]
    [TestCase("flat_device", DeviceKind.FlatDevice)]
    [TestCase("safety_monitor", DeviceKind.SafetyMonitor)]
    [TestCase("SWITCH", DeviceKind.Switch)]
    public void DeviceKinds_Parse(string text, DeviceKind expected) {
        Assert.That(DeviceKinds.TryParse(text, out var kind), Is.True);
        Assert.That(kind, Is.EqualTo(expected));
    }

    [TestCase("")]
    [TestCase("telescope")]
    [TestCase("filter")]
    [TestCase(null)]
    public void DeviceKinds_RejectUnknown(string? text) {
        Assert.That(DeviceKinds.TryParse(text, out _), Is.False);
    }

    [Test]
    public void DeviceKinds_RoundTripEveryEnumValue() {
        foreach (var kind in Enum.GetValues<DeviceKind>()) {
            Assert.That(DeviceKinds.TryParse(DeviceKinds.ToName(kind), out var parsed), Is.True);
            Assert.That(parsed, Is.EqualTo(kind));
        }

        Assert.That(DeviceKinds.All, Has.Count.EqualTo(Enum.GetValues<DeviceKind>().Length));
    }

    [Test]
    public async Task ConnectDevice_PassesKindAndClaimsDeviceResource() {
        var result = await catalog.CallAsync("connect_device", """{"device":"Filter Wheel"}""");
        var job = ToolsTestCatalog.Job(result);

        Assert.That(catalog.Observatory.LastDevice, Is.EqualTo(DeviceKind.FilterWheel));
        Assert.That(job["resource"]!.GetValue<string>(), Is.EqualTo("filter_wheel"));
        Assert.That(job["kind"]!.GetValue<string>(), Is.EqualTo("connect"));
    }

    // ----- Tool-specific arguments -----

    [Test]
    public async Task SlewMount_ParsesSexagesimalStrings() {
        await catalog.CallAsync("slew_mount", """{"ra":"05h35m17.3s","dec":"-05°23'28\""}""");

        var (ra, dec) = catalog.Observatory.LastSlew!.Value;

        Assert.That(ra, Is.EqualTo(5 + (35 / 60.0) + (17.3 / 3600)).Within(1e-9));
        Assert.That(dec, Is.EqualTo(-(5 + (23 / 60.0) + (28 / 3600.0))).Within(1e-9));
    }

    [Test]
    public async Task SlewMount_NegativeZeroDeclination() {
        await catalog.CallAsync("slew_mount", """{"ra":"0:00:00","dec":"-00:30:00"}""");

        Assert.That(catalog.Observatory.LastSlew!.Value.Dec, Is.EqualTo(-0.5).Within(1e-12));
    }

    [Test]
    public async Task SlewMount_JobDescriptionShowsFormattedCoordinates() {
        var result = await catalog.CallAsync("slew_mount", """{"ra":5.5,"dec":-0.5}""");
        var job = ToolsTestCatalog.Job(result);

        Assert.That(job["description"]!.GetValue<string>(), Is.EqualTo("Slew to RA 05h30m00.0s Dec -00°30'00\""));
        Assert.That(job["outcome"]!["data"]!["raHours"]!.GetValue<double>(), Is.EqualTo(5.5));
    }

    [Test]
    public async Task CaptureImage_PassesRequest() {
        await catalog.CallAsync("capture_image", """{"exposure_seconds":2.5,"filter":" Ha ","gain":100,"offset":30,"binning":2,"save":true}""");

        Assert.That(catalog.Observatory.LastCapture, Is.EqualTo(new CaptureRequest(2.5, "Ha", 100, 30, 2, true)));
    }

    [Test]
    public async Task CaptureImage_DefaultsKeepCameraSettings() {
        await catalog.CallAsync("capture_image", """{"exposure_seconds":1}""");

        Assert.That(catalog.Observatory.LastCapture, Is.EqualTo(new CaptureRequest(1)));
    }

    [Test]
    public async Task CaptureImage_ImageStatisticsInOutcomeData() {
        var result = await catalog.CallAsync("capture_image", """{"exposure_seconds":1}""");
        var data = ToolsTestCatalog.Job(result)["outcome"]!["data"]!;

        Assert.That(data["hfr"]!.GetValue<double>(), Is.EqualTo(2.5));
        Assert.That(data["stars"]!.GetValue<int>(), Is.EqualTo(321));
    }

    [Test]
    public async Task CaptureImage_FailureCarriesMessage() {
        catalog.Observatory.CaptureResultOverride = new CaptureResult(false, "Camera not connected", null);

        var result = await catalog.CallAsync("capture_image", """{"exposure_seconds":1}""");
        var job = ToolsTestCatalog.Job(result);

        Assert.That(job["state"]!.GetValue<string>(), Is.EqualTo("failed"));
        Assert.That(job["outcome"]!["message"]!.GetValue<string>(), Is.EqualTo("Camera not connected"));
    }

    [Test]
    public async Task CoolCamera_DefaultDurationIsTenMinutes() {
        await catalog.CallAsync("cool_camera", """{"target_celsius":-10}""");

        Assert.That(catalog.Observatory.LastCool, Is.EqualTo((-10.0, TimeSpan.FromMinutes(10))));
    }

    [Test]
    public async Task WarmCamera_PassesDuration() {
        await catalog.CallAsync("warm_camera", """{"duration_minutes":0}""");

        Assert.That(catalog.Observatory.LastWarmDuration, Is.EqualTo(TimeSpan.Zero));
    }

    [TestCase("""{"name":"Ha"}""", "Ha", null)]
    [TestCase("""{"position":3}""", null, 3)]
    [TestCase("""{"position":0}""", null, 0)]
    public async Task ChangeFilter_ExactlyOneSelector(string json, string? name, int? position) {
        await catalog.CallAsync("change_filter", json);

        Assert.That(catalog.Observatory.LastFilter, Is.EqualTo((name, position)));
    }

    [TestCase("{}")]
    [TestCase("""{"name":"Ha","position":1}""")]
    public void ChangeFilter_NeitherOrBoth_IsRejected(string json) {
        Assert.That(catalog.ArgumentError("change_filter", json), Does.Contain("exactly one of 'name' or 'position'"));
    }

    [Test]
    public async Task StartGuiding_ForceCalibrationDefaultsFalse() {
        await catalog.CallAsync("start_guiding");

        Assert.That(catalog.Observatory.LastForceCalibration, Is.False);

        await catalog.CallAsync("start_guiding", """{"force_calibration":true}""");

        Assert.That(catalog.Observatory.LastForceCalibration, Is.True);
    }

    [Test]
    public async Task SetSwitch_PassesIndexAndValue() {
        await catalog.CallAsync("set_switch", """{"index":2,"value":0.75}""");

        Assert.That(catalog.Observatory.LastSwitch, Is.EqualTo((2, 0.75)));
    }

    [Test]
    public async Task SetTracking_ReturnsOperationResult() {
        var ok = await catalog.CallAsync("set_tracking", """{"enabled":true}""");

        Assert.That(ok.IsError, Is.False);
        Assert.That(ok.Structured!["success"]!.GetValue<bool>(), Is.True);
        Assert.That(catalog.Observatory.LastTracking, Is.True);

        catalog.Observatory.SetResult("SetTracking", OperationResult.Fail("Mount is parked."));

        var failed = await catalog.CallAsync("set_tracking", """{"enabled":false}""");

        Assert.That(failed.IsError, Is.True);
        Assert.That(failed.Text, Is.EqualTo("Mount is parked."));
    }

    [TestCase("park_mount", "Park", "park")]
    [TestCase("unpark_mount", "Unpark", "unpark")]
    [TestCase("home_mount", "FindHome", "home")]
    public async Task MountMotion_RunsAsMountJob(string tool, string member, string kind) {
        var result = await catalog.CallAsync(tool);
        var job = ToolsTestCatalog.Job(result);

        Assert.That(catalog.Observatory.Calls, Does.Contain(member));
        Assert.That(job["resource"]!.GetValue<string>(), Is.EqualTo("mount"));
        Assert.That(job["kind"]!.GetValue<string>(), Is.EqualTo(kind));
        Assert.That(job["state"]!.GetValue<string>(), Is.EqualTo("succeeded"));
    }

    [Test]
    public async Task RunAutofocus_ResultInOutcomeData() {
        var result = await catalog.CallAsync("run_autofocus");
        var job = ToolsTestCatalog.Job(result);

        Assert.That(job["resource"]!.GetValue<string>(), Is.EqualTo("camera"));
        Assert.That(job["outcome"]!["data"]!["position"]!.GetValue<int>(), Is.EqualTo(12345));
        Assert.That(job["outcome"]!["data"]!["hfr"]!.GetValue<double>(), Is.EqualTo(2.1));
    }

    [Test]
    public async Task GetRecentImages_DefaultCountIsTen() {
        await catalog.CallAsync("get_recent_images");

        Assert.That(catalog.Observatory.LastImageCount, Is.EqualTo(10));
    }

    [Test]
    public async Task GetSequence_DefaultDepthIsFour() {
        var result = await catalog.CallAsync("get_sequence");

        Assert.That(catalog.Observatory.LastSequenceDepth, Is.EqualTo(4));
        Assert.That(result.Structured!["name"]!.GetValue<string>(), Is.EqualTo("Fake sequence"));
    }

    [Test]
    public async Task GetStatus_UsesSnakeCaseDeviceNamesAndSequenceState() {
        catalog.Observatory.SequenceRunning = true;

        var result = await catalog.CallAsync("get_status");
        var devices = result.Structured!["devices"]!.AsArray();

        Assert.That(devices[0]!["device"]!.GetValue<string>(), Is.EqualTo("camera"));
        Assert.That(devices[0]!["properties"]!["temperature"]!.GetValue<double>(), Is.EqualTo(-10));
        Assert.That(devices[1]!["device"]!.GetValue<string>(), Is.EqualTo("filter_wheel"));
        Assert.That(result.Structured["sequence"]!["running"]!.GetValue<bool>(), Is.True);
        Assert.That(result.Structured["sequence"]!["runningItems"]![0]!.GetValue<string>(), Is.EqualTo("Smart Exposure"));
        Assert.That(result.Structured["runningJobs"]!.AsArray(), Is.Empty);
    }

    [Test]
    public async Task GetStatus_IncludesOnlyRunningJobs() {
        catalog.Observatory.Block("Slew");

        await catalog.CallAsync("slew_mount", """{"ra":1,"dec":2,"wait_seconds":0}""");
        await catalog.CallAsync("set_switch", """{"index":0,"value":1}""");

        var result = await catalog.CallAsync("get_status");
        var running = result.Structured!["runningJobs"]!.AsArray();

        Assert.That(running, Has.Count.EqualTo(1));
        Assert.That(running[0]!["kind"]!.GetValue<string>(), Is.EqualTo("slew"));
        Assert.That(running[0]!["state"]!.GetValue<string>(), Is.EqualTo("running"));

        catalog.Observatory.Release("Slew");
    }
}
