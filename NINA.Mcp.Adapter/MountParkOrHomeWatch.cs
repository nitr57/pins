// SPDX-License-Identifier: MPL-2.0

using System.ComponentModel;

namespace NINA.Mcp.Adapter;

/// <summary>
/// Whether NINA has just started a park or home of the mount (TelescopeVM's <c>IsParkingOrHoming</c>), from Touch-N-Stars,
/// the Advanced API or the sequence. The flag turns true at once, long before the driver reports slewing, but NINA resets it
/// only when a park or a home that actually moves ends: a home on a parked or homed mount, or without a connected mount,
/// leaves it true until the next park or home that moves. So it only counts for <see cref="Window"/> after it turned true;
/// after that, a motion still under way shows as the driver's slewing state or a changing position.
/// </summary>
public sealed class MountParkOrHomeWatch {

    public static readonly TimeSpan Window = TimeSpan.FromSeconds(15);

    private readonly Func<bool> isParkingOrHoming;
    private readonly TimeProvider timeProvider;
    private long startedTicks = long.MinValue;

    /// <param name="source">Raises PropertyChanged for <c>IsParkingOrHoming</c>; null when there is no such view model.</param>
    /// <param name="isParkingOrHoming">Reads the flag.</param>
    /// <param name="timeProvider">The clock.</param>
    public MountParkOrHomeWatch(INotifyPropertyChanged? source, Func<bool> isParkingOrHoming, TimeProvider timeProvider) {
        this.isParkingOrHoming = isParkingOrHoming ?? throw new ArgumentNullException(nameof(isParkingOrHoming));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

        if (source is not null) {
            source.PropertyChanged += OnPropertyChanged;
        }

        // Already set when MCP starts (it is created on the first request): a park may be under way.
        if (ReadFlag()) {
            MarkStarted();
        }
    }

    /// <summary>NINA's flag as it is, also when it was left set: only for a park or home that is seen still moving.</summary>
    public bool Flag => ReadFlag();

    /// <summary>NINA parks or homes the mount and started less than <see cref="Window"/> ago.</summary>
    public bool RecentlyStarted {
        get {
            var started = Interlocked.Read(ref startedTicks);

            return started != long.MinValue
                && ReadFlag()
                && timeProvider.GetUtcNow().UtcTicks - started < Window.Ticks;
        }
    }

    private void OnPropertyChanged(object? sender, PropertyChangedEventArgs e) {
        if (e.PropertyName == "IsParkingOrHoming" && ReadFlag()) {
            MarkStarted();
        }
    }

    private void MarkStarted() {
        Interlocked.Exchange(ref startedTicks, timeProvider.GetUtcNow().UtcTicks);
    }

    private bool ReadFlag() {
        try {
            return isParkingOrHoming();
        } catch {
            return false;
        }
    }
}
