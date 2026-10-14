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
    public void Log_IdsOfARunStartAboveEarlierRuns() {
        var earlier = ObservatoryEventLog.InitialId(new DateTimeOffset(2026, 10, 13, 20, 0, 0, TimeSpan.Zero));
        var later = ObservatoryEventLog.InitialId(new DateTimeOffset(2026, 10, 13, 20, 1, 0, TimeSpan.Zero));

        Assert.That(later - earlier, Is.EqualTo(240), "four ids per second");
        Assert.That(ObservatoryEventLog.InitialId(new DateTimeOffset(2042, 1, 1, 0, 0, 0, TimeSpan.Zero)), Is.LessThan(int.MaxValue));
        Assert.That(ObservatoryEventLog.InitialId(new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero)), Is.EqualTo(0));
    }

    [Test]
    public void Log_ReadPagesWithCursor() {
        var log = new ObservatoryEventLog();
        var start = log.LatestId;

        for (var i = 1; i <= 5; i++) {
            log.Publish("device", EventSeverity.Info, $"event {i}");
        }

        var first = log.Read(start, 2);
        var fromZero = log.Read(0, 10);
        var second = log.Read(first.NextSinceId, 10);

        Assert.That(first.Events.Select(entry => entry.Message), Is.EqualTo(new[] { "event 1", "event 2" }));
        Assert.That(first.More, Is.True);
        Assert.That(first.Missed, Is.False);
        Assert.That(fromZero.Events, Has.Count.EqualTo(5));
        Assert.That(fromZero.Missed, Is.False, "since_id 0 is the start of this run");
        Assert.That(first.NextSinceId, Is.EqualTo(start + 2));
        Assert.That(second.Events.Select(entry => entry.Id), Is.EqualTo(new[] { start + 3, start + 4, start + 5 }));
        Assert.That(second.More, Is.False);
        Assert.That(second.NextSinceId, Is.EqualTo(start + 5));
        Assert.That(second.LatestId, Is.EqualTo(start + 5));
    }

    [Test]
    public void Log_FilterMovesCursorPastSkippedEvents() {
        var log = new ObservatoryEventLog();
        var start = log.LatestId;

        log.Publish("device", EventSeverity.Info, "connected");
        log.Publish("safety", EventSeverity.Critical, "unsafe");
        log.Publish("device", EventSeverity.Info, "connected again");

        var page = log.Read(start, 10, entry => entry.Severity >= EventSeverity.Warning);

        Assert.That(page.Events.Single().Message, Is.EqualTo("unsafe"));
        Assert.That(page.NextSinceId, Is.EqualTo(start + 3));
    }

    [Test]
    public void Log_DropsOldestBeyondCapacity_AndReportsMissed() {
        var log = new ObservatoryEventLog();
        var start = log.LatestId;

        for (var i = 0; i < ObservatoryEventLog.Capacity + 10; i++) {
            log.Publish("image", EventSeverity.Info, $"saved {i}");
        }

        var page = log.Read(start + 5, 1);

        Assert.That(page.Missed, Is.True);
        Assert.That(page.Events.Single().Id, Is.EqualTo(start + 11));
        Assert.That(log.Read(page.LatestId, 1).Missed, Is.False);
        Assert.That(log.Recent(3).Events.Select(entry => entry.Id), Is.EqualTo(new[] { start + 2008, start + 2009, start + 2010 }));
    }

    [Test]
    public void Log_AFloodOfInfoEvents_KeepsTheCriticalOne() {
        var log = new ObservatoryEventLog();
        var start = log.LatestId;

        log.Publish("device", EventSeverity.Warning, "camera dropped its connection and reconnected.");
        var unsafeEvent = log.Publish("safety", EventSeverity.Critical, "The safety monitor reports UNSAFE.");

        for (var i = 0; i < ObservatoryEventLog.Capacity + 100; i++) {
            log.Publish("image", EventSeverity.Info, $"saved {i}");
        }

        var important = log.Read(start, 10, entry => entry.Severity >= EventSeverity.Warning);

        Assert.That(important.Events.Select(entry => entry.Id), Is.EqualTo(new[] { start + 1, unsafeEvent.Id }));
        Assert.That(important.Missed, Is.True, "info events after the cursor were dropped");
        Assert.That(log.Recent(ObservatoryEventLog.Capacity).Events, Has.Count.EqualTo(ObservatoryEventLog.Capacity));

        // A cursor past every dropped event misses nothing.
        Assert.That(log.Read(log.LatestId - 5, 10).Missed, Is.False);
    }

    [Test]
    public void Log_CriticalEventsAreDroppedLast_OldestFirst() {
        var log = new ObservatoryEventLog();
        var start = log.LatestId;

        for (var i = 0; i < ObservatoryEventLog.Capacity + 1; i++) {
            log.Publish("safety", EventSeverity.Critical, $"unsafe {i}");
        }

        Assert.That(log.Read(start, 1).Events.Single().Id, Is.EqualTo(start + 2));
    }

    [Test]
    public void Log_CursorFromAnEarlierRunIsMissed() {
        var log = new ObservatoryEventLog();
        var start = log.LatestId;

        log.Publish("device", EventSeverity.Info, "camera connected.");
        log.Publish("safety", EventSeverity.Critical, "unsafe");

        var earlierRun = log.Read(start - 1000, 10);
        var clockWentBack = log.Read(start + 500, 10);

        Assert.That(earlierRun.Events.Select(entry => entry.Id), Is.EqualTo(new[] { start + 1, start + 2 }));
        Assert.That(earlierRun.Missed, Is.True);
        Assert.That(clockWentBack.Events, Has.Count.EqualTo(2));
        Assert.That(clockWentBack.Missed, Is.True);
        Assert.That(clockWentBack.NextSinceId, Is.EqualTo(start + 2));
    }

    [Test]
    public async Task Log_WaitReturnsWhenAMatchingEventArrives() {
        var log = new ObservatoryEventLog();
        var start = log.LatestId;

        var waiting = log.WaitAsync(start, 10, entry => entry.Kind == "safety", TimeSpan.FromSeconds(10), CancellationToken.None);

        log.Publish("device", EventSeverity.Info, "not this one");

        await Task.Delay(50);
        Assert.That(waiting.IsCompleted, Is.False, "a filtered-out event must not end the wait");

        log.Publish("safety", EventSeverity.Critical, "unsafe");

        var page = await waiting.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.That(page.Events.Single().Message, Is.EqualTo("unsafe"));
        Assert.That(page.NextSinceId, Is.EqualTo(start + 2));
    }

    [Test]
    public async Task Log_WaitTimesOutEmpty() {
        var log = new ObservatoryEventLog();
        var old = log.Publish("device", EventSeverity.Info, "old");

        var page = await log.WaitAsync(old.Id, 10, null, TimeSpan.FromMilliseconds(100), CancellationToken.None);

        Assert.That(page.Events, Is.Empty);
        Assert.That(page.NextSinceId, Is.EqualTo(old.Id));
    }

    // ----- get_events -----

    [Test]
    public async Task GetEvents_WithoutCursorReturnsNewest() {
        catalog.Events.Publish("device", EventSeverity.Warning, "camera disconnected.", new Dictionary<string, object?> { ["device"] = "camera" });
        var last = catalog.Events.Publish("safety", EventSeverity.Critical, "The safety monitor reports UNSAFE.");

        var result = await catalog.CallAsync("get_events");

        Assert.That(result.IsError, Is.False, result.Text);

        var events = result.Structured!["events"]!.AsArray();

        Assert.That(events, Has.Count.EqualTo(2));
        Assert.That(events[0]!["kind"]!.GetValue<string>(), Is.EqualTo("device"));
        Assert.That(events[0]!["data"]!["device"]!.GetValue<string>(), Is.EqualTo("camera"));
        Assert.That(events[1]!["severity"]!.GetValue<string>(), Is.EqualTo("critical"));
        Assert.That(result.Structured!["nextSinceId"]!.GetValue<long>(), Is.EqualTo(last.Id));
    }

    [Test]
    public async Task GetEvents_FiltersByKindAndSeverity() {
        var start = catalog.Events.LatestId;

        catalog.Events.Publish("device", EventSeverity.Info, "camera connected.");
        catalog.Events.Publish("image", EventSeverity.Error, "Saving an image failed: the disk is full.");
        catalog.Events.Publish("guider", EventSeverity.Warning, "Guide star lost.");

        var result = await catalog.CallAsync("get_events", $$"""{"since_id":{{start}},"kinds":"image, Guider","min_severity":"error"}""");

        var events = result.Structured!["events"]!.AsArray();

        Assert.That(events.Select(entry => entry!["message"]!.GetValue<string>()), Is.EqualTo(new[] { "Saving an image failed: the disk is full." }));
    }

    [Test]
    public async Task GetEvents_LongPollsWithCursor() {
        var old = catalog.Events.Publish("device", EventSeverity.Info, "old");

        var call = catalog.CallAsync("get_events", $$"""{"since_id":{{old.Id}},"wait_seconds":10}""");

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
