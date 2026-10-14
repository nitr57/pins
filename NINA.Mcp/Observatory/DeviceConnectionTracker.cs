// SPDX-License-Identifier: MPL-2.0

namespace NINA.Mcp.Observatory;

/// <summary>A connection change worth an event, judged by <see cref="DeviceConnectionTracker"/>.</summary>
public sealed record ConnectionChange(EventSeverity Severity, string Message, bool Connected);

/// <summary>
/// Tells requested disconnects from lost connections for one device. The state comes from polling the device info; the
/// VM's Disconnected event (<see cref="DisconnectRequested"/>) marks a disconnect as asked for, and
/// <see cref="ConnectionDropped"/> marks one the VM did by itself because the device went away (the guider VM calls its
/// own Disconnect then, which raises Disconnected as well). Thread-safe: the events come from mediator threads.
/// </summary>
/// <remarks>
/// A VM marks its info disconnected before it raises Disconnected (a driver disconnect can take seconds in between), so a
/// disconnect seen without a request is only judged one poll later. Disconnected events while the device is not
/// connected are ignored: several VMs call Disconnect at the start of every connect.
/// </remarks>
public sealed class DeviceConnectionTracker {

    /// <summary>A connection that ends within this time after a Disconnected event was asked for, not lost.</summary>
    public static readonly TimeSpan ExpectedDisconnectWindow = TimeSpan.FromSeconds(15);

    /// <summary>The VM's own Disconnected event follows a drop at once; within this time it does not count as a request.</summary>
    public static readonly TimeSpan DropEventGrace = TimeSpan.FromSeconds(5);

    private readonly object gate = new();

    private bool wasConnected;
    private DateTime requestedAt = DateTime.MinValue;
    private DateTime droppedAt = DateTime.MinValue;
    private DateTime? pendingLossSince;

    public DeviceConnectionTracker(string device, bool connected) {
        ArgumentException.ThrowIfNullOrEmpty(device);

        Device = device;
        wasConnected = connected;
    }

    public string Device { get; }

    /// <summary>The state of the last poll.</summary>
    public bool WasConnected {
        get {
            lock (gate) {
                return wasConnected;
            }
        }
    }

    /// <summary>
    /// The VM raised Disconnected (a disconnect was asked for). Ignored while the device was not connected at the last
    /// poll (unless its loss is still pending) and right after a drop.
    /// </summary>
    public void DisconnectRequested(DateTime now) {
        lock (gate) {
            // A pending loss was connected a poll ago: a late event (slow driver disconnect) still counts.
            if ((!wasConnected && pendingLossSince is null) || now - droppedAt <= DropEventGrace) {
                return;
            }

            requestedAt = now;
        }
    }

    /// <summary>The device went away by itself (seen by the adapter); the disconnect that follows is a lost connection.</summary>
    public void ConnectionDropped(DateTime now) {
        lock (gate) {
            droppedAt = now;
            requestedAt = DateTime.MinValue;
        }
    }

    /// <summary>Compares <paramref name="connected"/> with the last poll and returns the events to publish, oldest first.</summary>
    public IReadOnlyList<ConnectionChange> Poll(bool connected, DateTime now) {
        var changes = new List<ConnectionChange>();

        lock (gate) {
            if (pendingLossSince is { } since && !connected) {
                pendingLossSince = null;

                changes.Add(requestedAt >= since - ExpectedDisconnectWindow ? Disconnected() : Lost());
            }

            if (connected == wasConnected) {
                return changes;
            }

            wasConnected = connected;

            if (connected) {
                // A drop that recovered before the next poll (INDI drivers reconnect quickly) is still worth knowing about.
                if (pendingLossSince is not null) {
                    changes.Add(new ConnectionChange(EventSeverity.Warning, $"{Device} dropped its connection and reconnected.", true));
                }

                pendingLossSince = null;
                droppedAt = DateTime.MinValue;
                changes.Add(new ConnectionChange(EventSeverity.Info, $"{Device} connected.", true));
            } else if (now - droppedAt <= ExpectedDisconnectWindow) {
                changes.Add(Lost());
            } else if (now - requestedAt <= ExpectedDisconnectWindow) {
                changes.Add(Disconnected());
            } else {
                pendingLossSince = now;
            }
        }

        return changes;
    }

    private ConnectionChange Disconnected() {
        return new ConnectionChange(EventSeverity.Info, $"{Device} disconnected.", false);
    }

    private ConnectionChange Lost() {
        return new ConnectionChange(
            EventSeverity.Error,
            $"{Device} lost its connection (not disconnected by request): check cable, power and driver.",
            false);
    }
}
