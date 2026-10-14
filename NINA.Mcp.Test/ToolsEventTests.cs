// SPDX-License-Identifier: MPL-2.0

using NINA.Mcp.Observatory;
using NINA.Mcp.Test.Fakes;
using NUnit.Framework;

namespace NINA.Mcp.Test;

/// <summary>The event log and get_events.</summary>
[TestFixture]
public class ToolsEventTests {

    private ToolsTestCatalog catalog = null!;

    [SetUp]
    public void SetUp() {
        catalog = new ToolsTestCatalog();
    }

    [TearDown]
    public void TearDown() {
        catalog.Dispose();
    }

    // ----- Log -----

    [Test]
    public void Log_ReadPagesWithCursor() {
        var log = new ObservatoryEventLog();

        for (var i = 1; i <= 5; i++) {
            log.Publish("device", EventSeverity.Info, $"event {i}");
        }

        var first = log.Read(0, 2);
        var second = log.Read(first.NextSinceId, 10);

        Assert.That(first.Events.Select(entry => entry.Message), Is.EqualTo(new[] { "event 1", "event 2" }));
        Assert.That(first.More, Is.True);
        Assert.That(first.NextSinceId, Is.EqualTo(2));
        Assert.That(second.Events.Select(entry => entry.Id), Is.EqualTo(new long[] { 3, 4, 5 }));
        Assert.That(second.More, Is.False);
        Assert.That(second.NextSinceId, Is.EqualTo(5));
        Assert.That(second.LatestId, Is.EqualTo(5));
    }

    [Test]
    public void Log_FilterMovesCursorPastSkippedEvents() {
        var log = new ObservatoryEventLog();

        log.Publish("device", EventSeverity.Info, "connected");
        log.Publish("safety", EventSeverity.Critical, "unsafe");
        log.Publish("device", EventSeverity.Info, "connected again");

        var page = log.Read(0, 10, entry => entry.Severity >= EventSeverity.Warning);

        Assert.That(page.Events.Single().Message, Is.EqualTo("unsafe"));
        Assert.That(page.NextSinceId, Is.EqualTo(3));
    }

    [Test]
    public void Log_DropsOldestBeyondCapacity_AndReportsMissed() {
        var log = new ObservatoryEventLog();

        for (var i = 0; i < ObservatoryEventLog.Capacity + 10; i++) {
            log.Publish("image", EventSeverity.Info, $"saved {i}");
        }

        var page = log.Read(5, 1);

        Assert.That(page.Missed, Is.True);
        Assert.That(page.Events.Single().Id, Is.EqualTo(11));
        Assert.That(log.Read(page.LatestId, 1).Missed, Is.False);
        Assert.That(log.Recent(3).Events.Select(entry => entry.Id), Is.EqualTo(new long[] { 2008, 2009, 2010 }));
    }

    [Test]
    public void Log_CursorFromBeforeARestartReadsFromTheStart() {
        var log = new ObservatoryEventLog();

        log.Publish("device", EventSeverity.Info, "camera connected.");
        log.Publish("safety", EventSeverity.Critical, "unsafe");

        var page = log.Read(500, 10);

        Assert.That(page.Events.Select(entry => entry.Id), Is.EqualTo(new long[] { 1, 2 }));
        Assert.That(page.Missed, Is.True);
        Assert.That(page.NextSinceId, Is.EqualTo(2));
    }

    [Test]
    public async Task Log_WaitReturnsWhenAMatchingEventArrives() {
        var log = new ObservatoryEventLog();

        var waiting = log.WaitAsync(0, 10, entry => entry.Kind == "safety", TimeSpan.FromSeconds(10), CancellationToken.None);

        log.Publish("device", EventSeverity.Info, "not this one");

        await Task.Delay(50);
        Assert.That(waiting.IsCompleted, Is.False, "a filtered-out event must not end the wait");

        log.Publish("safety", EventSeverity.Critical, "unsafe");

        var page = await waiting.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.That(page.Events.Single().Message, Is.EqualTo("unsafe"));
        Assert.That(page.NextSinceId, Is.EqualTo(2));
    }

    [Test]
    public async Task Log_WaitTimesOutEmpty() {
        var log = new ObservatoryEventLog();

        log.Publish("device", EventSeverity.Info, "old");

        var page = await log.WaitAsync(1, 10, null, TimeSpan.FromMilliseconds(100), CancellationToken.None);

        Assert.That(page.Events, Is.Empty);
        Assert.That(page.NextSinceId, Is.EqualTo(1));
    }

    // ----- get_events -----

    [Test]
    public async Task GetEvents_WithoutCursorReturnsNewest() {
        catalog.Events.Publish("device", EventSeverity.Warning, "camera disconnected.", new Dictionary<string, object?> { ["device"] = "camera" });
        catalog.Events.Publish("safety", EventSeverity.Critical, "The safety monitor reports UNSAFE.");

        var result = await catalog.CallAsync("get_events");

        Assert.That(result.IsError, Is.False, result.Text);

        var events = result.Structured!["events"]!.AsArray();

        Assert.That(events, Has.Count.EqualTo(2));
        Assert.That(events[0]!["kind"]!.GetValue<string>(), Is.EqualTo("device"));
        Assert.That(events[0]!["data"]!["device"]!.GetValue<string>(), Is.EqualTo("camera"));
        Assert.That(events[1]!["severity"]!.GetValue<string>(), Is.EqualTo("critical"));
        Assert.That(result.Structured!["nextSinceId"]!.GetValue<long>(), Is.EqualTo(2));
    }

    [Test]
    public async Task GetEvents_FiltersByKindAndSeverity() {
        catalog.Events.Publish("device", EventSeverity.Info, "camera connected.");
        catalog.Events.Publish("image", EventSeverity.Error, "Saving an image failed: the disk is full.");
        catalog.Events.Publish("guider", EventSeverity.Warning, "Guide star lost.");

        var result = await catalog.CallAsync("get_events", """{"since_id":0,"kinds":"image, guider","min_severity":"error"}""");

        var events = result.Structured!["events"]!.AsArray();

        Assert.That(events.Select(entry => entry!["message"]!.GetValue<string>()), Is.EqualTo(new[] { "Saving an image failed: the disk is full." }));
    }

    [Test]
    public async Task GetEvents_LongPollsWithCursor() {
        catalog.Events.Publish("device", EventSeverity.Info, "old");

        var call = catalog.CallAsync("get_events", """{"since_id":1,"wait_seconds":10}""");

        await Task.Delay(50);
        catalog.Events.Publish("mount", EventSeverity.Info, "Mount parked.");

        var result = await call.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.That(result.Structured!["events"]![0]!["message"]!.GetValue<string>(), Is.EqualTo("Mount parked."));
    }

    [Test]
    public void GetEvents_WaitNeedsCursor() {
        Assert.That(catalog.ArgumentError("get_events", """{"wait_seconds":5}"""), Does.Contain("since_id"));
        Assert.That(catalog.ArgumentError("get_events", """{"kinds":"Device!"}"""), Does.Contain("'kinds'"));
    }
}
