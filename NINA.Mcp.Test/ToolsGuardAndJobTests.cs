// SPDX-License-Identifier: MPL-2.0

using NINA.Mcp.Jobs;
using NINA.Mcp.Observatory;
using NINA.Mcp.Test.Fakes;
using NINA.Mcp.Tools;
using NUnit.Framework;
using System.Globalization;
using System.Text.Json;

namespace NINA.Mcp.Test;

/// <summary>Sequence-running guard, the shared job payload, conflicts and the stop tools.</summary>
[TestFixture]
public class ToolsGuardAndJobTests {

    /// <summary>Minimal valid arguments for every tool that the guard affects.</summary>
    private static readonly object[] GuardedCalls = [
        new object[] { "connect_device", """{"device":"camera"}""" },
        new object[] { "disconnect_device", """{"device":"mount"}""" },
        new object[] { "slew_mount", """{"ra":5,"dec":5}""" },
        new object[] { "park_mount", "{}" },
        new object[] { "unpark_mount", "{}" },
        new object[] { "home_mount", "{}" },
        new object[] { "set_tracking", """{"enabled":false}""" },
        new object[] { "capture_image", """{"exposure_seconds":1}""" },
        new object[] { "cool_camera", """{"target_celsius":-10}""" },
        new object[] { "warm_camera", "{}" },
        new object[] { "move_focuser", """{"position":100}""" },
        new object[] { "run_autofocus", "{}" },
        new object[] { "change_filter", """{"name":"L"}""" },
        new object[] { "start_guiding", "{}" },
        new object[] { "stop_guiding", "{}" },
        new object[] { "set_switch", """{"index":0,"value":1}""" },
        new object[] { "add_target", """{"name":"M 42","ra":5.5,"dec":-5.4,"exposures":[{"exposure_seconds":60,"count":10}]}""" },
        new object[] { "save_sequence", """{"name":"x"}""" }
    ];

    private static readonly object[] StrictCalls = [
        new object[] { "load_sequence", """{"name":"Night"}""" },
        new object[] { "clear_targets", "{}" },
        new object[] { "start_sequence", "{}" },
        new object[] { "reset_sequence", "{}" }
    ];

    private static readonly object[] UnguardedCalls = [
        new object[] { "get_status", "{}" },
        new object[] { "get_recent_images", "{}" },
        new object[] { "get_sequence", "{}" },
        new object[] { "list_saved_sequences", "{}" },
        new object[] { "list_target_templates", "{}" },
        new object[] { "list_jobs", "{}" },
        new object[] { "stop_all", "{}" },
        new object[] { "stop_sequence", "{}" }
    ];

    private ToolsTestCatalog catalog = null!;

    [SetUp]
    public void SetUp() {
        catalog = new ToolsTestCatalog();
        catalog.Observatory.SavedSequences.Add(new SavedSequenceInfo("Night", "Night.json", DateTimeOffset.UnixEpoch));
    }

    [TearDown]
    public void TearDown() {
        catalog.Dispose();
    }

    // ----- Guard -----

    [TestCaseSource(nameof(GuardedCalls))]
    public async Task Guard_RefusesWhileSequenceRuns(string tool, string json) {
        catalog.Observatory.SequenceRunning = true;

        var result = await catalog.CallAsync(tool, json);

        Assert.That(result.IsError, Is.True);
        Assert.That(result.Text, Does.Contain("advanced sequence is running"));
        Assert.That(result.Text, Does.Contain("force: true"));
        Assert.That(result.Text, Does.Contain(tool));
        Assert.That(catalog.Observatory.Calls.Where(call => call != "GetStatus"), Is.Empty);
        Assert.That(catalog.Jobs.List(), Is.Empty);
    }

    [TestCaseSource(nameof(GuardedCalls))]
    public async Task Guard_ForceOverrides(string tool, string json) {
        catalog.Observatory.SequenceRunning = true;
        catalog.Observatory.Templates.Add("t");

        var forced = json.Insert(1, json.Length > 2 ? "\"force\":true," : "\"force\":true");

        var result = await catalog.CallAsync(tool, forced);

        Assert.That(result.IsError, Is.False, result.Text);
        Assert.That(catalog.Observatory.Calls.Count(call => call != "GetStatus"), Is.GreaterThan(0));
    }

    [TestCaseSource(nameof(GuardedCalls))]
    public async Task Guard_ForceFalse_StillRefuses(string tool, string json) {
        catalog.Observatory.SequenceRunning = true;

        var withForce = json.Insert(1, json.Length > 2 ? "\"force\":false," : "\"force\":false");

        var result = await catalog.CallAsync(tool, withForce);

        Assert.That(result.IsError, Is.True);
    }

    [TestCaseSource(nameof(GuardedCalls))]
    public async Task Guard_AllowsWhenSequenceIdle(string tool, string json) {
        var result = await catalog.CallAsync(tool, json);

        Assert.That(result.IsError, Is.False, result.Text);
    }

    [TestCaseSource(nameof(StrictCalls))]
    public async Task StrictGuard_RefusesWhileRunning_AndHasNoForce(string tool, string json) {
        catalog.Observatory.SequenceRunning = true;

        var result = await catalog.CallAsync(tool, json);

        Assert.That(result.IsError, Is.True);
        Assert.That(result.Text, Does.Contain("stop_sequence"));
        Assert.That(catalog.ArgumentError(tool, json.Insert(1, json.Length > 2 ? "\"force\":true," : "\"force\":true")), Does.Contain("Unknown argument 'force'"));
    }

    [TestCaseSource(nameof(StrictCalls))]
    public async Task StrictGuard_AllowsWhenIdle(string tool, string json) {
        var result = await catalog.CallAsync(tool, json);

        Assert.That(result.IsError, Is.False, result.Text);
    }

    [TestCaseSource(nameof(UnguardedCalls))]
    public async Task Unguarded_WorkWhileSequenceRuns(string tool, string json) {
        catalog.Observatory.SequenceRunning = true;

        var result = await catalog.CallAsync(tool, json);

        Assert.That(result.IsError, Is.False, result.Text);
    }

    [Test]
    public async Task Unguarded_JobQueriesAndCancelWorkWhileSequenceRuns() {
        var started = await catalog.CallAsync("slew_mount", """{"ra":1,"dec":1}""");
        var id = ToolsTestCatalog.Job(started)["id"]!.GetValue<string>();

        catalog.Observatory.SequenceRunning = true;

        Assert.That((await catalog.CallAsync("get_job", $$"""{"job_id":"{{id}}"}""")).IsError, Is.False);
        Assert.That((await catalog.CallAsync("cancel_job", $$"""{"job_id":"{{id}}"}""")).IsError, Is.False);
    }

    [Test]
    public void Guard_ValidatesArgumentsBeforeCheckingSequence() {
        catalog.Observatory.SequenceRunning = true;

        Assert.That(catalog.ArgumentError("slew_mount", """{"ra":99,"dec":0}"""), Does.Contain("'ra'"));
    }

    // ----- Job payload -----

    [Test]
    public async Task Job_FinishedWithinWait_HasFinalStateAndOutcome() {
        var result = await catalog.CallAsync("slew_mount", """{"ra":5,"dec":5}""");
        var job = ToolsTestCatalog.Job(result);

        Assert.That(job["id"]!.GetValue<string>(), Does.StartWith("job-"));
        Assert.That(job["kind"]!.GetValue<string>(), Is.EqualTo("slew"));
        Assert.That(job["resource"]!.GetValue<string>(), Is.EqualTo("mount"));
        Assert.That(job["state"]!.GetValue<string>(), Is.EqualTo("succeeded"));
        Assert.That(job.ContainsKey("startedAt"), Is.True);
        Assert.That(job.ContainsKey("finishedAt"), Is.True);
        Assert.That(job["outcome"]!["success"]!.GetValue<bool>(), Is.True);
        Assert.That(job["outcome"]!["message"]!.GetValue<string>(), Is.EqualTo("Slew done"));
        Assert.That(result.Structured!["next"]!.GetValue<string>(), Is.EqualTo("Finished successfully."));
        Assert.That(result.Text, Is.EqualTo(result.Structured.ToJsonString(Protocol.McpJson.Options)));
    }

    [Test]
    public async Task Job_StillRunning_TellsModelToPoll() {
        catalog.Observatory.Block("Slew");

        var result = await catalog.CallAsync("slew_mount", """{"ra":5,"dec":5,"wait_seconds":0}""");
        var job = ToolsTestCatalog.Job(result);
        var id = job["id"]!.GetValue<string>();

        Assert.That(job["state"]!.GetValue<string>(), Is.EqualTo("running"));
        Assert.That(job.ContainsKey("outcome"), Is.False);
        Assert.That(job.ContainsKey("finishedAt"), Is.False);
        Assert.That(result.Structured!["next"]!.GetValue<string>(), Does.Contain("get_job").And.Contain(id));

        catalog.Observatory.Release("Slew");

        var polled = await catalog.CallAsync("get_job", $$"""{"job_id":"{{id}}","wait_seconds":10}""");

        Assert.That(ToolsTestCatalog.Job(polled)["state"]!.GetValue<string>(), Is.EqualTo("succeeded"));
    }

    [Test]
    public async Task Job_ShortWait_ReturnsRunningAfterWait() {
        catalog.Observatory.Block("CoolCamera");

        var result = await catalog.CallAsync("cool_camera", """{"target_celsius":-10,"wait_seconds":0.05}""");

        Assert.That(ToolsTestCatalog.Job(result)["state"]!.GetValue<string>(), Is.EqualTo("running"));

        catalog.Observatory.Release("CoolCamera");
    }

    [Test]
    public async Task Job_OperationFailure_IsFailedJobNotToolError() {
        catalog.Observatory.SetResult("Slew", OperationResult.Fail("Mount is parked."));

        var result = await catalog.CallAsync("slew_mount", """{"ra":5,"dec":5}""");
        var job = ToolsTestCatalog.Job(result);

        Assert.That(job["state"]!.GetValue<string>(), Is.EqualTo("failed"));
        Assert.That(job["outcome"]!["success"]!.GetValue<bool>(), Is.False);
        Assert.That(job["outcome"]!["message"]!.GetValue<string>(), Is.EqualTo("Mount is parked."));
        Assert.That(result.Structured!["next"]!.GetValue<string>(), Does.Contain("failed"));
    }

    [Test]
    public async Task Job_Exception_IsFailedJob() {
        catalog.Observatory.Throw("MoveFocuser", new InvalidOperationException("Focuser exploded"));

        var result = await catalog.CallAsync("move_focuser", """{"position":5}""");
        var job = ToolsTestCatalog.Job(result);

        Assert.That(job["state"]!.GetValue<string>(), Is.EqualTo("failed"));
        Assert.That(job["outcome"]!["message"]!.GetValue<string>(), Is.EqualTo("Focuser exploded"));
    }

    [Test]
    public async Task Job_RunsOnItsOwnToken_NotTheRequests() {
        catalog.Observatory.Block("Slew");

        using var request = new CancellationTokenSource();

        await catalog.CallAsync("slew_mount", """{"ra":5,"dec":5,"wait_seconds":0}""", request.Token);
        await catalog.Observatory.WaitForCallAsync("Slew");

        request.Cancel();

        Assert.That(catalog.Observatory.LastToken.IsCancellationRequested, Is.False);
        Assert.That(catalog.Jobs.List().Single().State, Is.EqualTo(JobState.Running));

        catalog.Observatory.Release("Slew");
    }

    [Test]
    public async Task Job_RequestAbortedDuringWait_ReturnsRunningJob() {
        catalog.Observatory.Block("Slew");

        using var request = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        var result = await catalog.CallAsync("slew_mount", """{"ra":5,"dec":5,"wait_seconds":60}""", request.Token);

        Assert.That(ToolsTestCatalog.Job(result)["state"]!.GetValue<string>(), Is.EqualTo("running"));

        catalog.Observatory.Release("Slew");
    }

    [Test]
    public async Task Job_SameResource_Conflicts() {
        catalog.Observatory.Block("Slew");

        await catalog.CallAsync("slew_mount", """{"ra":5,"dec":5,"wait_seconds":0}""");

        var exception = Assert.ThrowsAsync<JobConflictException>(() => catalog.CallAsync("park_mount", """{"wait_seconds":0}"""));

        Assert.That(exception!.Resource, Is.EqualTo("mount"));
        Assert.That(exception.Message, Does.Contain("cancel_job"));
        Assert.That(catalog.Observatory.Calls, Does.Not.Contain("Park"));

        catalog.Observatory.Release("Slew");
    }

    [Test]
    public async Task Job_DifferentResources_RunConcurrently() {
        catalog.Observatory.Block("Slew");
        catalog.Observatory.Block("CoolCamera");

        await catalog.CallAsync("slew_mount", """{"ra":5,"dec":5,"wait_seconds":0}""");
        await catalog.CallAsync("cool_camera", """{"target_celsius":-10,"wait_seconds":0}""");

        var capture = await catalog.CallAsync("capture_image", """{"exposure_seconds":1}""");

        Assert.That(ToolsTestCatalog.Job(capture)["state"]!.GetValue<string>(), Is.EqualTo("succeeded"));
        Assert.That(catalog.Jobs.List().Count(job => job.State == JobState.Running), Is.EqualTo(2));

        catalog.Observatory.Release("Slew");
        catalog.Observatory.Release("CoolCamera");
    }

    [Test]
    public async Task Job_FocuserMoveRefusedWhileCameraBusy() {
        catalog.Observatory.Block("Capture");

        await catalog.CallAsync("capture_image", """{"exposure_seconds":30,"wait_seconds":0}""");

        var exception = Assert.ThrowsAsync<JobConflictException>(() => catalog.CallAsync("move_focuser", """{"position":10}"""));

        Assert.That(exception!.Resource, Is.EqualTo("camera"));

        catalog.Observatory.Release("Capture");
    }

    [Test]
    public async Task Job_AutofocusRefusedWhileFocuserBusy() {
        catalog.Observatory.Block("MoveFocuser");

        await catalog.CallAsync("move_focuser", """{"position":10,"wait_seconds":0}""");

        var exception = Assert.ThrowsAsync<JobConflictException>(() => catalog.CallAsync("run_autofocus"));

        Assert.That(exception!.Resource, Is.EqualTo("focuser"));

        catalog.Observatory.Release("MoveFocuser");
    }

    [Test]
    public async Task Job_CameraDisconnectRefusedWhileCooling() {
        catalog.Observatory.Block("CoolCamera");

        await catalog.CallAsync("cool_camera", """{"target_celsius":-10,"wait_seconds":0}""");

        var exception = Assert.ThrowsAsync<JobConflictException>(() => catalog.CallAsync("disconnect_device", """{"device":"camera"}"""));
        var mount = await catalog.CallAsync("disconnect_device", """{"device":"mount"}""");

        Assert.That(exception!.Resource, Is.EqualTo("camera_cooler"));
        Assert.That(mount.IsError, Is.False);

        catalog.Observatory.Release("CoolCamera");
    }

    [Test]
    public async Task Job_CaptureWithFilterRefusedWhileFilterWheelBusy() {
        catalog.Observatory.Block("ChangeFilter");

        await catalog.CallAsync("change_filter", """{"name":"Ha","wait_seconds":0}""");

        Assert.ThrowsAsync<JobConflictException>(() => catalog.CallAsync("capture_image", """{"exposure_seconds":1,"filter":"OIII"}"""));

        var withoutFilter = await catalog.CallAsync("capture_image", """{"exposure_seconds":1}""");

        Assert.That(withoutFilter.IsError, Is.False);

        catalog.Observatory.Release("ChangeFilter");
    }

    [Test]
    public async Task Job_DescriptionsAreCultureInvariant() {
        var previous = CultureInfo.CurrentCulture;

        try {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");

            var capture = await catalog.CallAsync("capture_image", """{"exposure_seconds":2.5}""");
            var cool = await catalog.CallAsync("cool_camera", """{"target_celsius":-12.5,"duration_minutes":7.5}""");
            var error = catalog.ArgumentError("get_recent_images", """{"count":0.5}""");
            var range = catalog.ArgumentError("capture_image", """{"exposure_seconds":3600.5}""");

            Assert.That(ToolsTestCatalog.Job(capture)["description"]!.GetValue<string>(), Is.EqualTo("Capture 2.5 s"));
            Assert.That(ToolsTestCatalog.Job(cool)["description"]!.GetValue<string>(), Is.EqualTo("Cool camera to -12.5 °C over 7.5 min"));
            Assert.That(error, Does.Contain("integer"));
            Assert.That(range, Does.Contain("(got 3600.5)"));
        } finally {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Test]
    public void JobPayload_ReadWait_DefaultsAndBounds() {
        Assert.That(JobPayload.ReadWait(ToolArguments.Empty, 30), Is.EqualTo(TimeSpan.FromSeconds(30)));
        Assert.That(JobPayload.ReadWait(Args("""{"wait_seconds":0}"""), 30), Is.EqualTo(TimeSpan.Zero));
        Assert.That(JobPayload.ReadWait(Args("""{"wait_seconds":120}"""), 30), Is.EqualTo(TimeSpan.FromSeconds(120)));
        Assert.Throws<ToolArgumentException>(() => JobPayload.ReadWait(Args("""{"wait_seconds":120.5}"""), 30));
        Assert.Throws<ToolArgumentException>(() => JobPayload.ReadWait(Args("""{"wait_seconds":-0.1}"""), 30));
    }

    // ----- get_job / list_jobs / cancel_job -----

    [Test]
    public async Task GetJob_UnknownId_IsError() {
        var result = await catalog.CallAsync("get_job", """{"job_id":"job-000000"}""");

        Assert.That(result.IsError, Is.True);
        Assert.That(result.Text, Does.Contain("Unknown job 'job-000000'"));
    }

    [Test]
    public async Task ListJobs_RunningFirst() {
        await catalog.CallAsync("park_mount");

        catalog.Observatory.Block("Slew");
        await catalog.CallAsync("slew_mount", """{"ra":5,"dec":5,"wait_seconds":0}""");

        var result = await catalog.CallAsync("list_jobs");
        var jobs = result.Structured!["jobs"]!.AsArray();

        Assert.That(result.Structured["running"]!.GetValue<int>(), Is.EqualTo(1));
        Assert.That(jobs, Has.Count.EqualTo(2));
        Assert.That(jobs[0]!["kind"]!.GetValue<string>(), Is.EqualTo("slew"));
        Assert.That(jobs[1]!["kind"]!.GetValue<string>(), Is.EqualTo("park"));

        catalog.Observatory.Release("Slew");
    }

    [Test]
    public async Task CancelJob_CancelsRunningJob() {
        catalog.Observatory.Block("RunAutofocus");

        var started = await catalog.CallAsync("run_autofocus", """{"wait_seconds":0}""");
        var id = ToolsTestCatalog.Job(started)["id"]!.GetValue<string>();

        await catalog.Observatory.WaitForCallAsync("RunAutofocus");

        var result = await catalog.CallAsync("cancel_job", $$"""{"job_id":"{{id}}"}""");
        var job = ToolsTestCatalog.Job(result);

        Assert.That(job["state"]!.GetValue<string>(), Is.EqualTo("cancelled"));
        Assert.That(result.Structured!["next"]!.GetValue<string>(), Does.Contain("cancelled"));
    }

    [Test]
    public async Task CancelJob_FinishedJob_ReturnsItUnchanged() {
        var started = await catalog.CallAsync("park_mount");
        var id = ToolsTestCatalog.Job(started)["id"]!.GetValue<string>();

        var result = await catalog.CallAsync("cancel_job", $$"""{"job_id":"{{id}}"}""");

        Assert.That(ToolsTestCatalog.Job(result)["state"]!.GetValue<string>(), Is.EqualTo("succeeded"));
    }

    [Test]
    public async Task CancelJob_UnknownId_IsError() {
        var result = await catalog.CallAsync("cancel_job", """{"job_id":"job-zzzzzz"}""");

        Assert.That(result.IsError, Is.True);
    }

    // ----- stop_all -----

    [Test]
    public async Task StopAll_RunsStepsInOrder() {
        catalog.Observatory.Block("Slew");
        await catalog.CallAsync("slew_mount", """{"ra":5,"dec":5,"wait_seconds":0}""");
        await catalog.Observatory.WaitForCallAsync("Slew");

        var result = await catalog.CallAsync("stop_all");

        Assert.That(result.IsError, Is.False);

        var stopCalls = catalog.Observatory.Calls
            .Where(call => call is "StopSequence" or "AbortExposure" or "StopSlew" or "StopFocuser")
            .ToList();

        Assert.That(stopCalls, Is.EqualTo(new[] { "StopSequence", "AbortExposure", "StopSlew", "StopFocuser" }));

        var steps = result.Structured!["steps"]!.AsArray().Select(step => step!["step"]!.GetValue<string>());

        Assert.That(steps, Is.EqualTo(new[] { "stop_sequence", "cancel_jobs", "abort_exposure", "stop_slew", "stop_focuser" }));

        var cancelStep = result.Structured["steps"]![1]!;

        Assert.That(cancelStep["message"]!.GetValue<string>(), Does.Contain("1 running job"));

        var job = await catalog.Jobs.WaitAsync(catalog.Jobs.List().Single().Id, TimeSpan.FromSeconds(10), CancellationToken.None);

        Assert.That(job!.State, Is.EqualTo(JobState.Cancelled));
    }

    [Test]
    public async Task StopAll_ContinuesWhenAStepFails() {
        catalog.Observatory.Throw("StopSequence", new InvalidOperationException("sequencer broken"));
        catalog.Observatory.Throw("AbortExposure", new InvalidOperationException("camera gone"));

        var result = await catalog.CallAsync("stop_all");
        var steps = result.Structured!["steps"]!.AsArray();

        Assert.That(result.IsError, Is.False);
        Assert.That(steps[0]!["success"]!.GetValue<bool>(), Is.False);
        Assert.That(steps[0]!["message"]!.GetValue<string>(), Does.Contain("sequencer broken"));
        Assert.That(steps[2]!["success"]!.GetValue<bool>(), Is.False);
        Assert.That(steps[3]!["success"]!.GetValue<bool>(), Is.True);
        Assert.That(catalog.Observatory.Calls, Does.Contain("StopSlew").And.Contain("StopFocuser"));
    }

    [Test]
    public async Task StopAll_ReportsFailedStopSequenceResult() {
        catalog.Observatory.SetResult("StopSequence", OperationResult.Fail("No sequence running."));

        var result = await catalog.CallAsync("stop_all");
        var step = result.Structured!["steps"]![0]!;

        Assert.That(step["success"]!.GetValue<bool>(), Is.False);
        Assert.That(step["message"]!.GetValue<string>(), Is.EqualTo("No sequence running."));
    }

    [Test]
    public async Task StopAll_DoesNotQueryStatus() {
        catalog.Observatory.Throw("GetStatus", new InvalidOperationException("must not be needed"));

        var result = await catalog.CallAsync("stop_all");

        Assert.That(result.IsError, Is.False);
        Assert.That(catalog.Observatory.Calls, Does.Not.Contain("GetStatus"));
    }

    [Test]
    public async Task StopSequence_ReturnsOperationResult() {
        var result = await catalog.CallAsync("stop_sequence");

        Assert.That(result.IsError, Is.False);
        Assert.That(catalog.Observatory.Calls, Is.EqualTo(new[] { "StopSequence" }));
    }

    [Test]
    public async Task StopGuiding_ClaimsNoResource() {
        catalog.Observatory.Block("StartGuiding");

        await catalog.CallAsync("start_guiding", """{"wait_seconds":0}""");

        var result = await catalog.CallAsync("stop_guiding");
        var job = ToolsTestCatalog.Job(result);

        Assert.That(job.ContainsKey("resource"), Is.False);
        Assert.That(job["state"]!.GetValue<string>(), Is.EqualTo("succeeded"));

        catalog.Observatory.Release("StartGuiding");
    }

    private static ToolArguments Args(string json) {
        return new ToolArguments(JsonDocument.Parse(json).RootElement.Clone());
    }
}
