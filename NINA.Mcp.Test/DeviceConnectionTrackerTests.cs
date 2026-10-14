// SPDX-License-Identifier: MPL-2.0

using NINA.Mcp.Observatory;
using NUnit.Framework;

namespace NINA.Mcp.Test;

/// <summary>How get_events judges device connection changes (polls plus the VM's Disconnected event).</summary>
[TestFixture]
public class DeviceConnectionTrackerTests {

    private static readonly DateTime Start = new(2026, 10, 13, 22, 0, 0, DateTimeKind.Utc);

    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(3);

    [Test]
    public void Connect_IsInfo() {
        var tracker = new DeviceConnectionTracker("camera", false);

        var changes = tracker.Poll(true, Start);

        Assert.That(changes.Select(change => change.Severity), Is.EqualTo(new[] { EventSeverity.Info }));
        Assert.That(changes[0].Message, Is.EqualTo("camera connected."));
        Assert.That(changes[0].Connected, Is.True);
    }

    [Test]
    public void RequestedDisconnect_IsInfo() {
        var tracker = new DeviceConnectionTracker("mount", true);

        tracker.DisconnectRequested(Start);
        var changes = tracker.Poll(false, Start + Poll);

        Assert.That(changes.Single().Severity, Is.EqualTo(EventSeverity.Info));
        Assert.That(changes.Single().Message, Is.EqualTo("mount disconnected."));
    }

    [Test]
    public void DisconnectWithoutEvent_IsLostOnePollLater() {
        var tracker = new DeviceConnectionTracker("focuser", true);

        Assert.That(tracker.Poll(false, Start), Is.Empty);

        var changes = tracker.Poll(false, Start + Poll);

        Assert.That(changes.Single().Severity, Is.EqualTo(EventSeverity.Error));
        Assert.That(changes.Single().Message, Does.Contain("lost its connection"));
    }

    [Test]
    public void LateDisconnectedEvent_WithinOnePoll_IsStillARequest() {
        var tracker = new DeviceConnectionTracker("camera", true);

        Assert.That(tracker.Poll(false, Start), Is.Empty);

        // The driver disconnect took a while: the VM raises Disconnected after its info already said disconnected.
        tracker.DisconnectRequested(Start + TimeSpan.FromSeconds(2));
        var changes = tracker.Poll(false, Start + Poll);

        Assert.That(changes.Single().Severity, Is.EqualTo(EventSeverity.Info));
    }

    [Test]
    public void SelfDisconnect_OfTheVm_IsLost() {
        var tracker = new DeviceConnectionTracker("guider", true);

        // GuiderVM: the device reports disconnected, the VM calls its own Disconnect, which raises Disconnected.
        tracker.ConnectionDropped(Start);
        tracker.DisconnectRequested(Start);
        var changes = tracker.Poll(false, Start + Poll);

        Assert.That(changes.Single().Severity, Is.EqualTo(EventSeverity.Error));
        Assert.That(changes.Single().Message, Does.Contain("lost its connection"));
    }

    [Test]
    public void DisconnectedEvent_WhileNotConnected_IsIgnored() {
        var tracker = new DeviceConnectionTracker("camera", false);

        // ChooseCamera calls Disconnect at the start of every connect.
        tracker.DisconnectRequested(Start);
        Assert.That(tracker.Poll(true, Start + Poll).Single().Message, Is.EqualTo("camera connected."));

        // A real drop ten seconds after the connect is not mistaken for that request.
        Assert.That(tracker.Poll(false, Start + 4 * Poll), Is.Empty);

        var changes = tracker.Poll(false, Start + 5 * Poll);

        Assert.That(changes.Single().Severity, Is.EqualTo(EventSeverity.Error));
    }

    [Test]
    public void QuickReconnect_IsReported() {
        var tracker = new DeviceConnectionTracker("mount", true);

        Assert.That(tracker.Poll(false, Start), Is.Empty);

        var changes = tracker.Poll(true, Start + Poll);

        Assert.That(changes.Select(change => change.Severity), Is.EqualTo(new[] { EventSeverity.Warning, EventSeverity.Info }));
        Assert.That(changes[0].Message, Does.Contain("dropped its connection and reconnected"));
    }

    [Test]
    public void RequestAfterAReconnectedDrop_IsInfoAgain() {
        var tracker = new DeviceConnectionTracker("guider", true);

        tracker.ConnectionDropped(Start);
        Assert.That(tracker.Poll(false, Start + Poll).Single().Severity, Is.EqualTo(EventSeverity.Error));
        Assert.That(tracker.Poll(true, Start + 2 * Poll).Single().Severity, Is.EqualTo(EventSeverity.Info));

        tracker.DisconnectRequested(Start + 3 * Poll);

        Assert.That(tracker.Poll(false, Start + 3 * Poll).Single().Severity, Is.EqualTo(EventSeverity.Info));
    }

    [Test]
    public void NoChange_NoEvents() {
        var tracker = new DeviceConnectionTracker("dome", true);

        Assert.That(tracker.Poll(true, Start), Is.Empty);
        Assert.That(tracker.WasConnected, Is.True);
    }
}
