// SPDX-License-Identifier: MPL-2.0

using System.Globalization;
using NINA.GuideEngine.Core;
using NINA.GuideEngine.Guiding;
using NINA.GuideEngine.Imaging;

namespace NINA.GuideEngine.Incidents;

/// <summary>What the guide loop knows about a frame beyond its telemetry record.</summary>
/// <param name="Preprocess">What the preprocessor did (FITS keywords).</param>
/// <param name="DarkLibrary">Name of the dark library in use, null when none.</param>
/// <param name="PixelScale">Guide pixel scale, ″/px.</param>
/// <param name="SearchRegion">Star search region half-size, px.</param>
internal readonly record struct IncidentFrameInfo(PreprocessInfo Preprocess, string? DarkLibrary, double PixelScale, int SearchRegion);

/// <summary>
/// The flight recorder (docs/INCIDENTS.md §1): keeps the last minutes of frames (telemetry, star crops and a binned
/// context image), opens an incident on a trigger, extends it until the guiding recovered (plus a tail) and saves it
/// through the <see cref="IncidentStore"/> on a background task. Fed by the guide loop (frames, alerts, state changes);
/// <see cref="Mark"/> may come from any thread. All state is guarded by one lock; images are prepared outside it.
/// </summary>
internal sealed class IncidentRecorder
{
    /// <summary>Most full-resolution key frames one occurrence keeps in memory (each is a whole frame).</summary>
    internal const int MaxKeyFrames = 8;

    /// <summary>Frames with images around a trigger or recovery of a reopened incident (each side).</summary>
    internal const int RepeatImageFrames = 5;

    /// <summary>
    /// Frames a spike waits before it opens an incident. A runaway, a mount that stopped responding or a lost star show as
    /// a jump of the error first; their alert, when it follows within these frames, gives the incident its kind and the
    /// spike joins it.
    /// </summary>
    internal const int SpikeConfirmFrames = 8;

    /// <summary>Longest history (frames) kept in a long calibration; its middle keeps telemetry only.</summary>
    private const int MaxHistory = 4000;

    // secondary star crops per frame: every secondary of the default 9 guide stars (StarFinderOptions.MaxStars)
    private const int SecondaryCrops = 8;

    // side of a secondary crop, px: PHD2's default search region (15 px) on each side of the star
    private const int SecondaryCropSize = 31;

    // side of the primary crop, px: 2 × the search region + 1, at least a secondary crop; at most 95 px (a 47 px search
    // region) to bound the memory per buffered frame
    private const int PrimaryCropMin = SecondaryCropSize;
    private const int PrimaryCropMax = 95;

    // guide frames before the rolling RMS counts (spike threshold and recovery): fewer give no stable RMS
    private const int MinRmsFrames = 10;

    // long side of the context image, px: enough to see the field, small enough to keep minutes of frames in memory
    private const double ContextLongSidePx = 480.0;

    private readonly object gate = new();
    private readonly IClock clock;
    private readonly Action<GuiderEvent> publish;
    private readonly Func<IncidentContext> contextNow;
    private readonly Func<string?> settingsJson;
    private readonly LinkedList<Entry> history = new();
    private readonly Queue<IncidentFrameRecord> recent = new();
    private readonly List<(GuideFrame Frame, IncidentFrameRecord Record)> keyRing = [];
    private readonly Queue<double> window = new();
    private readonly List<DateTimeOffset> sessionSpikes = [];
    private readonly Stack<ushort[]> pool = new();
    private readonly List<GuiderEvent> outbox = [];
    private readonly HashSet<string> saving = new(StringComparer.Ordinal);
    private readonly EngineFaults faults = new();

    private IncidentSettings settings = new();
    private IncidentStore? store;
    private Func<IncidentTags>? tagsNow;
    private IncidentTags tags = new();
    private DateTimeOffset? calibrationStart;
    private double windowSumSq;
    private int warmupFrames;
    private bool lastAbove;
    private bool lastCoach;
    private Open? open;
    private TriggerBuilder? pendingSpike;
    private Closed? lastClosed;
    private Task saves = Task.CompletedTask;

    public IncidentRecorder(IClock clock, Action<GuiderEvent> publish, Func<IncidentContext> contextNow, Func<string?> settingsJson)
    {
        this.clock = clock;
        this.publish = publish;
        this.contextNow = contextNow;
        this.settingsJson = settingsJson;
    }

    /// <summary>Recording: enabled and a store is set.</summary>
    public bool IsActive
    {
        get
        {
            lock (gate)
            {
                return ActiveCore;
            }
        }
    }

    /// <summary>Id of the incident being recorded, null when none.</summary>
    public string? RecordingId
    {
        get
        {
            lock (gate)
            {
                return open?.Id;
            }
        }
    }

    /// <summary>Completes when the saves queued so far are written.</summary>
    public Task Saves
    {
        get
        {
            lock (gate)
            {
                return saves;
            }
        }
    }

    private bool ActiveCore => settings.Enabled && store is not null;

    /// <summary>Bytes of image data held in memory (history, key frames, open incident); for cost measurements.</summary>
    internal long BufferedBytes
    {
        get
        {
            lock (gate)
            {
                var arrays = new HashSet<object>(ReferenceEqualityComparer.Instance);
                long bytes = 0;
                void Count(Array? a)
                {
                    if (a is not null && arrays.Add(a))
                    {
                        bytes += Buffer.ByteLength(a);
                    }
                }

                foreach (var e in history)
                {
                    Count(e.Context);
                    e.Crops?.ForEach(c => Count(c.Pixels));
                }

                keyRing.ForEach(k => Count(k.Frame.Pixels));
                if (open is { } o)
                {
                    foreach (var f in o.Frames)
                    {
                        Count(f.Context);
                        f.Crops?.ForEach(c => Count(c.Pixels));
                    }

                    foreach (var k in o.Keys.Values)
                    {
                        Count(k.Pixels);
                    }
                }

                return bytes;
            }
        }
    }

    public static bool IsRecordingState(GuiderState s) => s is GuiderState.Calibrating || s.IsGuidingActive();

    /// <summary>The incident kind an alert code triggers, null for codes that never trigger.</summary>
    public static IncidentKind? KindOf(GuideErrorCode code) => code switch
    {
        GuideErrorCode.StarLost or GuideErrorCode.StarReacquireTimeout => IncidentKind.StarLost,
        GuideErrorCode.RunawayDetected => IncidentKind.Runaway,
        GuideErrorCode.MountNotResponding => IncidentKind.MountNotResponding,
        GuideErrorCode.SettleTimeout => IncidentKind.SettleTimeout,
        GuideErrorCode.CameraCaptureFailed or GuideErrorCode.CameraReconnecting or GuideErrorCode.CameraFailed => IncidentKind.CameraFailure,
        GuideErrorCode.MountSlewing or GuideErrorCode.MountParked or GuideErrorCode.MountTrackingOff or GuideErrorCode.MountDisconnected => IncidentKind.MountPaused,
        GuideErrorCode.CalibrationFailedRaNoMove or GuideErrorCode.CalibrationFailedDecNoMove or GuideErrorCode.CalibrationFailedBacklash
            or GuideErrorCode.CalibrationFailedStarLost => IncidentKind.CalibrationFailed,
        GuideErrorCode.PulseLimitReached => IncidentKind.PulseLimited,
        GuideErrorCode.PulseOutputFailed => IncidentKind.PulseOutputFailed,
        GuideErrorCode.DecFlipCorrected => IncidentKind.DecFlipCorrected,
        _ => null,
    };

    /// <summary>New settings. Turning the recorder off saves an open incident (Stopped) and drops all buffers.</summary>
    public void Configure(IncidentSettings value)
    {
        lock (gate)
        {
            settings = value ?? new IncidentSettings();
            if (!ActiveCore)
            {
                Reset();
            }
        }

        Flush();
    }

    /// <summary>Sets the store (null stops recording) and where the tags of new incidents come from.</summary>
    public void SetStore(IncidentStore? value, Func<IncidentTags>? tagsProvider)
    {
        lock (gate)
        {
            if (!ReferenceEquals(value, store))
            {
                Reset();
                lastClosed = null;
            }

            store = value;
            tagsNow = tagsProvider;
            RefreshTags();
        }

        Flush();
    }

    #region loop events

    public void OnStateChanged(GuiderState next, GuiderState previous, bool inFrame)
    {
        lock (gate)
        {
            if (!ActiveCore)
            {
                return;
            }

            if (next == GuiderState.Calibrating && previous != GuiderState.Calibrating)
            {
                // the before-window of a calibration incident is the calibration from its first frame
                calibrationStart = clock.UtcNow;
                ClearHistory();
            }
            else if (next != GuiderState.Calibrating)
            {
                calibrationStart = null;
            }

            if (!IsRecordingState(next) && open is null && pendingSpike is { } spike)
            {
                // guiding ended before the spike was confirmed: record it now
                pendingSpike = null;
                TriggerCore(spike, null);
            }

            if (!IsRecordingState(next) && open is not null)
            {
                if (inFrame)
                {
                    // the frame that ended guiding belongs to the incident: close after it was recorded
                    open.PendingStop = true;
                }
                else
                {
                    Close(IncidentEndReason.Stopped);
                }
            }
        }

        Flush();
    }

    /// <summary>Guiding started: a new session for the spike detection.</summary>
    public void OnGuidingStarted()
    {
        lock (gate)
        {
            window.Clear();
            windowSumSq = 0;
            warmupFrames = 0;
            lastAbove = false;
            sessionSpikes.Clear();
            RefreshTags();
        }
    }

    /// <summary>Guiding resumed after a pause: the first frames may carry the pause's error.</summary>
    public void OnResumed()
    {
        lock (gate)
        {
            warmupFrames = 0;
            lastAbove = false;
        }
    }

    /// <summary>
    /// An alert: starts, joins or reopens an incident when its code triggers one while recording. Returns the incident id,
    /// null when the alert triggered nothing. <paramref name="frame"/> is the frame being processed, null between frames
    /// (the trigger then belongs to the next frame).
    /// </summary>
    public string? OnAlert(GuideErrorCode code, string message, string? detail, GuiderState state, long? frame)
    {
        if (KindOf(code) is not { } kind || !IsRecordingState(state))
        {
            return null;
        }

        string? id;
        lock (gate)
        {
            id = ActiveCore ? TriggerCore(new TriggerBuilder(clock.UtcNow, kind, code, message, detail, frame, null), null) : null;
        }

        Flush();
        return id;
    }

    /// <summary>A manual mark: the last 2 minutes and the next 30 s (or it joins the open incident).</summary>
    public string? Mark(string? note, GuiderState state, out string? error)
    {
        try
        {
            return MarkCore(note, state, out error);
        }
        finally
        {
            Flush();
        }
    }

    private string? MarkCore(string? note, GuiderState state, out string? error)
    {
        lock (gate)
        {
            if (!ActiveCore)
            {
                error = "The incident recorder is off";
                return null;
            }

            if (!IsRecordingState(state))
            {
                error = "Incidents can only be marked while guiding or calibrating";
                return null;
            }

            error = null;
            note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
            if (note is { Length: > 500 })
            {
                note = note[..500];
            }

            return TriggerCore(new TriggerBuilder(clock.UtcNow, IncidentKind.Manual, null, "Marked by the user", note, null, note), note);
        }
    }

    /// <summary>
    /// A frame processed while recording (its state before or after processing is a recording state). Builds the crops
    /// and the context image, keeps them in the history, detects spikes, extends/closes the open incident.
    /// </summary>
    public void OnFrame(IncidentFrameRecord record, GuideFrame frame, IncidentFrameInfo info)
    {
        IncidentSettings s;
        lock (gate)
        {
            if (!ActiveCore)
            {
                return;
            }

            s = settings;
        }

        // images outside the lock (a few ms)
        int binning = ContextBinning(frame.Width, frame.Height);
        var context = BinMean(frame, binning, Rent(frame.Width / binning * (frame.Height / binning)));
        var crops = Crops(frame, record, info.SearchRegion);
        var keywords = Keywords(info);
        var entry = new Entry(record, context, crops, frame.Width, frame.Height, binning, FrameStart(frame, record), keywords);

        lock (gate)
        {
            if (!ActiveCore)
            {
                return;
            }

            var now = record.Time;
            DetectSpike(record, frame, info, s);
            if (pendingSpike is { } spike && open is null && record.Frame - spike.Frame >= SpikeConfirmFrames)
            {
                pendingSpike = null;
                TriggerCore(spike, null);
            }

            if (open is { } o)
            {
                foreach (var t in o.Triggers.Where(t => t.Frame is null))
                {
                    t.Frame = record.Frame;
                    o.KeyWanted.Add(record.Frame);
                }

                AddSlot(o, entry);
                if (o.KeyWanted.Contains(record.Frame))
                {
                    AddKey(o, frame, record.Frame);
                }
            }

            AddHistory(entry, now, s);
            AddRecent(record, now, s);
            keyRing.Add((frame, record));
            if (keyRing.Count > 2)
            {
                keyRing.RemoveAt(0);
            }

            if (open is { } o2)
            {
                UpdateRecovery(o2, record, frame, info, s);
                CheckClose(o2, now, s);
            }
        }

        Flush();
    }

    /// <summary>The loop finished a frame: an incident whose guiding stopped during the frame closes now if its frame was not recorded.</summary>
    public void FrameDone()
    {
        lock (gate)
        {
            if (open is { PendingStop: true })
            {
                Close(IncidentEndReason.Stopped);
            }
        }

        Flush();
    }

    private void Flush()
    {
        GuiderEvent[] events;
        lock (gate)
        {
            if (outbox.Count == 0)
            {
                return;
            }

            events = [.. outbox];
            outbox.Clear();
        }

        foreach (var e in events)
        {
            try
            {
                publish(e);
            }
            catch (Exception ex)
            {
                // subscribers must not break recording
                Fault("IncidentRecorder.Publish", ex);
            }
        }
    }

    #endregion

    #region triggers

    private string TriggerCore(TriggerBuilder t, string? note)
    {
        string id;
        if (open is { } o)
        {
            Join(o, t, note);
            id = o.Id;
        }
        else if (lastClosed is { } c && c.Kind == t.Kind && (t.Time - c.ClosedAt).TotalSeconds <= settings.RepeatSeconds
            && (saving.Contains(c.Id) || store!.Exists(c.Id)))
        {
            Reopen(c, t);
            id = c.Id;
        }
        else
        {
            id = Start(t, note, pendingSpike?.Baseline ?? t.Baseline);
        }

        if (pendingSpike is { } spike && !ReferenceEquals(spike, t) && open is { } started)
        {
            // the spike that preceded this trigger joins its incident
            pendingSpike = null;
            Join(started, spike, null);
        }

        return id;
    }

    /// <param name="baseline">Rolling RMS before a held spike (the frames since then carry its error), else the current one.</param>
    private string Start(TriggerBuilder t, string? note, double? baseline)
    {
        RefreshTags();
        var o = new Open(store!.ReserveId(t.Time, t.Kind), t.Kind)
        {
            Tags = tags,
            Note = note,
            SettingsJson = SafeSettingsJson(),
            CapAt = CapTime(t.Time),
            Baseline = baseline ?? Rms(),
        };
        foreach (var e in history)
        {
            AddSlot(o, e);
        }

        open = o;
        lastClosed = null;
        Join(o, t, note);
        outbox.Add(new IncidentStartedEvent(t.Time, o.Id, t.Kind));
        return o.Id;
    }

    private void Reopen(Closed c, TriggerBuilder t)
    {
        var o = new Open(c.Id, c.Kind)
        {
            Previous = c.Incident,
            Occurrences = c.Incident.Occurrences + 1,
            Tags = c.Incident.Tags,
            Note = c.Incident.Note,
            SettingsJson = c.Incident.SettingsJson,
            CapAt = CapTime(t.Time),
            Baseline = pendingSpike?.Baseline ?? t.Baseline ?? Rms(),
            Reopened = true,
        };

        // the telemetry since the incident closed, images only for the frames just before the trigger
        long lastFrame = c.Incident.Frames.Count > 0 ? c.Incident.Frames[^1].Frame : long.MinValue;
        var withImages = history.Where(e => e.Record.Frame > lastFrame).TakeLast(RepeatImageFrames).ToList();
        long firstWithImages = withImages.Count > 0 ? withImages[0].Record.Frame : long.MaxValue;
        foreach (var r in recent.Where(r => r.Frame > lastFrame && r.Frame < firstWithImages))
        {
            o.Frames.Add(new Slot(r, null, null, 0, 0, 1, r.Time, null));
        }

        foreach (var e in withImages)
        {
            AddSlot(o, e);
            o.Frames[^1].Keep = true;
        }

        open = o;
        lastClosed = null;
        Join(o, t, null);
        outbox.Add(new IncidentStartedEvent(t.Time, o.Id, t.Kind));
    }

    private void Join(Open o, TriggerBuilder t, string? note)
    {
        // the trigger and note markers are built when the incident closes: a trigger between frames gets its frame later
        o.Triggers.Add(t);
        if (note is not null)
        {
            o.Note ??= note;
        }

        if (t.Kind == IncidentKind.Spike)
        {
            sessionSpikes.Add(t.Time);
        }

        if (t.Kind == IncidentKind.Manual)
        {
            var until = t.Time.AddSeconds(settings.PostSeconds);
            o.ManualUntil = o.ManualUntil > until ? o.ManualUntil : until;
        }
        else
        {
            // restart the recovery condition
            o.NeedsRecovery = true;
            o.CalmFrames = 0;
            o.RecoveredAt = null;
        }

        // key frames: the last good frame before the trigger, and the trigger frame (a held spike brings both along)
        var lastGood = t.LastGood ?? keyRing.LastOrDefault(k => k.Record.StarFound && (t.Frame is null || k.Record.Frame < t.Frame));
        if (lastGood.Frame is not null)
        {
            AddKey(o, lastGood.Frame, lastGood.Record.Frame);
        }

        if (t.Frame is { } f && t.TriggerFrame is { } image)
        {
            AddKey(o, image, f);
        }
        else if (t.Frame is { } wanted)
        {
            o.KeyWanted.Add(wanted);
        }

        if (o.Reopened)
        {
            o.ImagesAfterTrigger = RepeatImageFrames;
            KeepLastSlots(o);
        }
    }

    private void DetectSpike(IncidentFrameRecord r, GuideFrame frame, IncidentFrameInfo info, IncidentSettings s)
    {
        if (lastCoach && !r.CoachMeasurement)
        {
            // guiding output was off during the measurement: the first guided frames correct its drift
            warmupFrames = 0;
            lastAbove = false;
        }

        lastCoach = r.CoachMeasurement;
        if (r.State != GuiderState.Guiding || !r.StarFound || r.RaDistanceRaw is not { } ra || r.DecDistanceRaw is not { } dec || r.Settling || r.Dithering
            || r.CoachMeasurement)
        {
            return;
        }

        double error = Math.Sqrt(ra * ra + dec * dec) * info.PixelScale;
        double? rms = Rms();
        double limit = AbsoluteLimit(s);
        bool above = rms is { } v && error > s.SpikeFactor * v && error > limit;
        if (above && !lastAbove && warmupFrames >= s.SpikeWarmupFrames)
        {
            string message = string.Create(CultureInfo.InvariantCulture, $"Spike: total error {error:F2}″, {error / rms!.Value:F1} × the rolling RMS {rms.Value:F2}″");
            string detail = string.Create(CultureInfo.InvariantCulture, $"error {error:F2}″, RMS {rms.Value:F2}″");
            var spike = new TriggerBuilder(r.Time, IncidentKind.Spike, null, message, detail, r.Frame, null)
            {
                LastGood = keyRing.LastOrDefault(k => k.Record.StarFound),
                TriggerFrame = frame,
                Baseline = rms,
            };
            if (open is not null)
            {
                TriggerCore(spike, null);
            }
            else
            {
                pendingSpike ??= spike;
            }
        }

        lastAbove = above;
        warmupFrames++;
        window.Enqueue(error);
        windowSumSq += error * error;
        while (window.Count > Math.Max(1, s.SpikeWindow))
        {
            double old = window.Dequeue();
            windowSumSq -= old * old;
        }
    }

    private void UpdateRecovery(Open o, IncidentFrameRecord r, GuideFrame frame, IncidentFrameInfo info, IncidentSettings s)
    {
        if (!o.NeedsRecovery || o.RecoveredAt is not null)
        {
            return;
        }

        double limit = s.RecoveryRmsFactor * (o.Baseline ?? AbsoluteLimit(s) / s.RecoveryRmsFactor);
        bool calm = r.State == GuiderState.Guiding && r.StarFound && !r.CoachMeasurement && r.RaDistanceRaw is { } ra && r.DecDistanceRaw is { } dec
            && Math.Sqrt(ra * ra + dec * dec) * info.PixelScale < limit;
        o.CalmFrames = calm ? o.CalmFrames + 1 : 0;
        if (o.CalmFrames < Math.Max(1, s.RecoveryFrames))
        {
            return;
        }

        o.RecoveredAt = r.Time;
        o.Markers.Add(new IncidentMarker(r.Time, r.Frame, IncidentMarkerType.Recovered, "Recovered"));
        AddKey(o, frame, r.Frame, recovery: true);
        if (o.Reopened)
        {
            KeepLastSlots(o);
            o.ImagesAfterRecovery = RepeatImageFrames;
        }
    }

    private void CheckClose(Open o, DateTimeOffset now, IncidentSettings s)
    {
        if (o.PendingStop)
        {
            Close(IncidentEndReason.Stopped);
        }
        else if (o.NeedsRecovery && o.RecoveredAt is { } rec && now >= rec.AddSeconds(s.PostSeconds) && now >= o.ManualUntil)
        {
            Close(IncidentEndReason.Recovered);
        }
        else if (!o.NeedsRecovery && now >= o.ManualUntil)
        {
            Close(IncidentEndReason.Manual);
        }
        else if (now >= o.CapAt)
        {
            Close(IncidentEndReason.Cap);
        }
    }

    #endregion

    #region closing and saving

    private void Close(IncidentEndReason reason)
    {
        var o = open!;
        open = null;
        var now = clock.UtcNow;
        var prev = o.Previous;
        if (o.Reopened)
        {
            // images only around the triggers and the recovery
            foreach (var slot in o.Frames.Where(f => !f.Keep))
            {
                slot.DropImages();
            }
        }

        var records = o.Frames.Select(f => f.Record).ToList();
        var frames = (prev?.Frames ?? []).Concat(records).ToList();
        var markers = (prev?.Markers.Where(m => m.Type != IncidentMarkerType.End) ?? []).Concat(o.Markers).Concat(GapMarkers(o)).ToList();
        foreach (var t in o.Triggers)
        {
            markers.Add(new IncidentMarker(t.Time, t.Frame, IncidentMarkerType.Trigger, t.Message));
            if (t.Note is { } note)
            {
                markers.Add(new IncidentMarker(t.Time, t.Frame, IncidentMarkerType.Note, note));
            }
        }

        var start = prev?.Start ?? (o.Frames.Count > 0 ? o.Frames[0].Record.Time : o.Triggers[0].Time);
        var end = frames.Count > 0 && frames[^1].Time > start ? frames[^1].Time : now;
        markers.Add(new IncidentMarker(end, frames.Count > 0 ? frames[^1].Frame : null, IncidentMarkerType.End, reason.ToString()));
        var first = o.Frames.FirstOrDefault(f => f.Context is not null);

        IncidentContext context;
        try
        {
            context = contextNow();
        }
        catch (Exception ex)
        {
            Fault("IncidentRecorder.Context", ex);
            context = new IncidentContext();
        }

        var triggers = (prev?.Triggers ?? []).Concat(o.Triggers.Select(t => t.Build())).ToList();

        // earlier spikes of the session: before the incident's (last) spike, or before its first trigger
        var reference = o.Kind == IncidentKind.Spike ? triggers.Where(t => t.Kind == IncidentKind.Spike).Max(t => t.Time) : triggers[0].Time;
        var incident = new Incident
        {
            Id = o.Id,
            Start = start,
            End = end,
            Kind = o.Kind,
            Triggers = triggers,
            Markers = markers.OrderBy(m => m.Time).ToList(),
            Occurrences = o.Occurrences,
            EndReason = reason,
            Note = o.Note,
            Tags = o.Tags,
            Context = context with { EarlierSpikes = sessionSpikes.Where(t => t < reference).Distinct().ToList() },
            SensorWidth = first?.Width ?? prev?.SensorWidth ?? 0,
            SensorHeight = first?.Height ?? prev?.SensorHeight ?? 0,
            ContextBinning = first?.Binning ?? prev?.ContextBinning ?? 1,
            SettingsJson = o.SettingsJson,
            Frames = frames,
            FrameCount = frames.Count,
        };

        var images = new List<IncidentImage>();
        foreach (var f in o.Frames)
        {
            if (f.Context is { } ctx)
            {
                images.Add(new IncidentImage
                {
                    Frame = f.Record.Frame,
                    Kind = IncidentImageKind.Context,
                    Width = f.Width / f.Binning,
                    Height = f.Height / f.Binning,
                    Binning = f.Binning,
                    Time = f.Start,
                    ExposureMs = f.Record.ExposureMs,
                    Pixels = ctx,
                    Keywords = f.Keywords,
                });
            }

            foreach (var c in f.Crops ?? [])
            {
                images.Add(new IncidentImage
                {
                    Frame = f.Record.Frame,
                    Kind = IncidentImageKind.Crop,
                    Star = c.Star,
                    X0 = c.X0,
                    Y0 = c.Y0,
                    Width = c.Width,
                    Height = c.Height,
                    Time = f.Start,
                    ExposureMs = f.Record.ExposureMs,
                    Pixels = c.Pixels,
                    Keywords = f.Keywords,
                });
            }
        }

        foreach (var (frameNumber, key) in o.Keys.OrderBy(k => k.Key))
        {
            var slot = o.Frames.LastOrDefault(f => f.Record.Frame == frameNumber);
            images.Add(new IncidentImage
            {
                Frame = frameNumber,
                Kind = IncidentImageKind.Key,
                Width = key.Width,
                Height = key.Height,
                Time = slot?.Start ?? key.StartTime,
                ExposureMs = key.ExposureMs,
                Pixels = key.Pixels,
                Keywords = slot?.Keywords,
            });
        }

        lastClosed = new Closed(o.Id, o.Kind, now, incident);
        var target = store!;
        saving.Add(o.Id);
        saves = saves.ContinueWith(_ => SaveCore(target, incident, images), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    private void SaveCore(IncidentStore target, Incident incident, IReadOnlyList<IncidentImage> images)
    {
        bool faulted = false;
        try
        {
            IncidentDiagnosis? diagnosis;
            try
            {
                diagnosis = IncidentDiagnoser.Diagnose(incident);
            }
            catch (Exception ex)
            {
                faulted |= Fault("IncidentDiagnoser", ex);
                diagnosis = null;
            }

            target.Save(incident with { Diagnosis = diagnosis }, images);
        }
        catch (Exception ex)
        {
            // a failed save (disk full, folder deleted meanwhile) must not affect guiding
            faulted |= Fault("IncidentRecorder.Save", ex);
        }
        finally
        {
            lock (gate)
            {
                saving.Remove(incident.Id);
            }
        }

        if (faulted)
        {
            Flush();
        }
    }

    /// <summary>Stretches of frames of this occurrence without images (reopened incidents, long calibrations).</summary>
    private static IEnumerable<IncidentMarker> GapMarkers(Open o)
    {
        int i = 0;
        while (i < o.Frames.Count)
        {
            if (o.Frames[i].Context is not null)
            {
                i++;
                continue;
            }

            int j = i;
            while (j + 1 < o.Frames.Count && o.Frames[j + 1].Context is null)
            {
                j++;
            }

            var a = o.Frames[i].Record;
            var b = o.Frames[j].Record;
            yield return new IncidentMarker(a.Time, a.Frame, IncidentMarkerType.Gap,
                string.Create(CultureInfo.InvariantCulture, $"frames {a.Frame}-{b.Frame} ({(b.Time - a.Time).TotalSeconds:F0} s): telemetry only"));
            i = j + 1;
        }
    }

    #endregion

    #region buffers

    private void AddSlot(Open o, Entry e)
    {
        e.Shared = true;
        var slot = new Slot(e.Record, e.Context, e.Crops, e.Width, e.Height, e.Binning, e.Start, e.Keywords);
        if (o.Reopened)
        {
            if (o.ImagesAfterTrigger > 0)
            {
                o.ImagesAfterTrigger--;
                slot.Keep = true;
            }
            else if (o.ImagesAfterRecovery > 0)
            {
                o.ImagesAfterRecovery--;
                slot.Keep = true;
            }
        }

        o.Frames.Add(slot);
        if (o.Reopened)
        {
            // tentative images: only the last few frames keep theirs until a trigger or the recovery claims them
            for (int k = o.Frames.Count - 1 - RepeatImageFrames; k >= 0 && k >= o.Frames.Count - 1 - 2 * RepeatImageFrames; k--)
            {
                if (!o.Frames[k].Keep)
                {
                    o.Frames[k].DropImages();
                }
            }
        }
    }

    private static void KeepLastSlots(Open o)
    {
        for (int k = Math.Max(0, o.Frames.Count - RepeatImageFrames); k < o.Frames.Count; k++)
        {
            if (o.Frames[k].Context is not null)
            {
                o.Frames[k].Keep = true;
            }
        }
    }

    private void AddKey(Open o, GuideFrame frame, long frameNumber, bool recovery = false)
    {
        o.KeyWanted.Remove(frameNumber);
        if (o.Keys.ContainsKey(frameNumber))
        {
            return;
        }

        // one place stays free for the frame after the recovery
        if (o.Keys.Count < (recovery ? MaxKeyFrames : MaxKeyFrames - 1))
        {
            o.Keys[frameNumber] = frame;
        }
    }

    private void AddHistory(Entry e, DateTimeOffset now, IncidentSettings s)
    {
        history.AddLast(e);
        if (calibrationStart is { } cs)
        {
            // long calibration: keep its first and its last frames with images, the middle as telemetry
            var headEnd = cs.AddSeconds(s.MaxSeconds / 2);
            var tailStart = now.AddSeconds(-s.MaxSeconds / 2);
            foreach (var old in history)
            {
                if (old.Record.Time >= tailStart)
                {
                    break;
                }

                if (old.Record.Time > headEnd && old.Context is not null)
                {
                    Release(old);
                }
            }

            while (history.Count > MaxHistory && history.FirstOrDefault(x => x.Record.Time > headEnd) is { } victim)
            {
                history.Remove(victim);
                Release(victim);
            }

            return;
        }

        var limit = now.AddSeconds(-s.PreSeconds);
        while (history.First is { } first && first.Value.Record.Time < limit)
        {
            history.RemoveFirst();
            Release(first.Value);
        }
    }

    private void AddRecent(IncidentFrameRecord r, DateTimeOffset now, IncidentSettings s)
    {
        recent.Enqueue(r);
        var limit = now.AddSeconds(-s.RepeatSeconds);
        while (recent.Count > 0 && (recent.Peek().Time < limit || recent.Count > MaxHistory))
        {
            recent.Dequeue();
        }
    }

    private void ClearHistory()
    {
        foreach (var e in history)
        {
            Release(e);
        }

        history.Clear();
    }

    private void Reset()
    {
        if (open is not null)
        {
            Close(IncidentEndReason.Stopped);
        }

        ClearHistory();
        recent.Clear();
        keyRing.Clear();
        pendingSpike = null;
        window.Clear();
        windowSumSq = 0;
        warmupFrames = 0;
        lastAbove = false;
        calibrationStart = null;
    }

    private void Release(Entry e)
    {
        if (!e.Shared && e.Context is { } ctx && pool.Count < 4)
        {
            pool.Push(ctx);
        }

        e.Context = null;
        e.Crops = null;
    }

    private ushort[] Rent(int length)
    {
        lock (gate)
        {
            while (pool.Count > 0)
            {
                var a = pool.Pop();
                if (a.Length == length)
                {
                    return a;
                }
            }
        }

        return new ushort[length];
    }

    #endregion

    #region helpers

    private double? Rms() => window.Count >= MinRmsFrames ? Math.Sqrt(Math.Max(0, windowSumSq) / window.Count) : null;

    private double AbsoluteLimit(IncidentSettings s) =>
        tags.ImagingScale is { } scale && scale > 0 ? s.SpikeImagingFraction * scale : s.SpikeFallbackArcsec;

    private DateTimeOffset CapTime(DateTimeOffset trigger) => trigger.AddSeconds(Math.Max(settings.MaxSeconds - settings.PreSeconds, settings.PostSeconds));

    private void RefreshTags()
    {
        try
        {
            tags = tagsNow?.Invoke() ?? new IncidentTags();
        }
        catch (Exception ex)
        {
            // keep the previous tags
            Fault("IncidentRecorder.Tags", ex);
        }
    }

    private string? SafeSettingsJson()
    {
        try
        {
            return settingsJson();
        }
        catch (Exception ex)
        {
            Fault("IncidentRecorder.Settings", ex);
            return null;
        }
    }

    // a swallowed exception, reported as an EngineFaultEvent with the next flush (the caller may hold the gate); false
    // while that source is rate-limited
    private bool Fault(string source, Exception ex)
    {
        if (faults.Report(clock.UtcNow, source, ex) is not { } e)
        {
            return false;
        }

        lock (gate)
        {
            outbox.Add(e);
        }

        return true;
    }

    /// <summary>Context image binning: about <see cref="ContextLongSidePx"/> px on the long side.</summary>
    internal static int ContextBinning(int width, int height) => Math.Max(1, (int)Math.Round(Math.Max(width, height) / ContextLongSidePx, MidpointRounding.AwayFromZero));

    /// <summary>Mean of <paramref name="binning"/> × <paramref name="binning"/> blocks (whole blocks only).</summary>
    internal static unsafe ushort[] BinMean(GuideFrame frame, int binning, ushort[]? target = null)
    {
        int w = frame.Width, h = frame.Height, bw = w / binning, bh = h / binning;
        var dst = target is { } t && t.Length == bw * bh ? t : new ushort[bw * bh];
        if (binning == 1)
        {
            Array.Copy(frame.Pixels, dst, dst.Length);
            return dst;
        }

        var sums = new uint[bw];
        uint n = (uint)(binning * binning);
        fixed (ushort* src = frame.Pixels)
        fixed (ushort* d = dst)
        fixed (uint* sum = sums)
        {
            for (int by = 0; by < bh; by++)
            {
                new Span<uint>(sum, bw).Clear();
                for (int j = 0; j < binning; j++)
                {
                    ushort* p = src + (long)(by * binning + j) * w;
                    if (binning == 4)
                    {
                        for (int bx = 0; bx < bw; bx++, p += 4)
                        {
                            sum[bx] += (uint)(p[0] + p[1] + p[2] + p[3]);
                        }
                    }
                    else
                    {
                        for (int bx = 0; bx < bw; bx++)
                        {
                            uint s = 0;
                            for (int i = 0; i < binning; i++)
                            {
                                s += *p++;
                            }

                            sum[bx] += s;
                        }
                    }
                }

                ushort* row = d + (long)by * bw;
                for (int bx = 0; bx < bw; bx++)
                {
                    row[bx] = (ushort)((sum[bx] + n / 2) / n);
                }
            }
        }

        return dst;
    }

    /// <summary>The primary crop (side clamp(2 × search region + 1, 31, 95)) and up to 8 secondary crops (31 px).</summary>
    internal static List<Crop> Crops(GuideFrame frame, IncidentFrameRecord r, int searchRegion)
    {
        var list = new List<Crop>();
        int primaryIndex = -1;
        for (int i = 0; i < r.Stars.Count; i++)
        {
            if (r.Stars[i].IsPrimary)
            {
                primaryIndex = i;
                break;
            }
        }

        var center = r.Star is { IsValid: true } star ? star
            : primaryIndex >= 0 ? new GuidePoint(r.Stars[primaryIndex].X, r.Stars[primaryIndex].Y)
            : r.Lock is { IsValid: true } lockPos ? lockPos
            : GuidePoint.Invalid;
        if (center.IsValid)
        {
            list.Add(CropAt(frame, center, Math.Clamp(2 * searchRegion + 1, PrimaryCropMin, PrimaryCropMax), Math.Max(0, primaryIndex)));
        }

        int secondaries = 0;
        for (int i = 0; i < r.Stars.Count && secondaries < SecondaryCrops; i++)
        {
            if (i == primaryIndex || !double.IsFinite(r.Stars[i].X) || !double.IsFinite(r.Stars[i].Y))
            {
                continue;
            }

            list.Add(CropAt(frame, new GuidePoint(r.Stars[i].X, r.Stars[i].Y), SecondaryCropSize, i));
            secondaries++;
        }

        return list;
    }

    private static Crop CropAt(GuideFrame frame, GuidePoint center, int side, int star)
    {
        int w = Math.Min(side, frame.Width), h = Math.Min(side, frame.Height);
        int x0 = Math.Clamp((int)Math.Round(center.X) - w / 2, 0, frame.Width - w);
        int y0 = Math.Clamp((int)Math.Round(center.Y) - h / 2, 0, frame.Height - h);
        var pixels = new ushort[w * h];
        for (int y = 0; y < h; y++)
        {
            Array.Copy(frame.Pixels, (y0 + y) * frame.Width + x0, pixels, y * w, w);
        }

        return new Crop(star, x0, y0, w, h, pixels);
    }

    private static DateTimeOffset FrameStart(GuideFrame frame, IncidentFrameRecord r) => frame.StartTime != default ? frame.StartTime : r.Time;

    private static Dictionary<string, string> Keywords(IncidentFrameInfo info) => new()
    {
        ["DARKSUB"] = FitsWriter.Logical(info.Preprocess.DarkSubtracted),
        ["DARKEXP"] = FitsWriter.Number((info.Preprocess.DarkExposureMs ?? 0) / 1000.0),
        ["DARKLIB"] = FitsWriter.Quote(info.Preprocess.DarkSubtracted ? info.DarkLibrary ?? "yes" : "none"),
        ["DEFECTS"] = FitsWriter.Logical(info.Preprocess.DefectMapApplied),
        ["NOISERED"] = FitsWriter.Quote(info.Preprocess.NoiseReduction.ToString()),
        ["SWBIN"] = info.Preprocess.SoftwareBinning.ToString(CultureInfo.InvariantCulture),
    };

    #endregion

    #region types

    internal sealed record Crop(int Star, int X0, int Y0, int Width, int Height, ushort[] Pixels);

    /// <summary>A frame in the history.</summary>
    private sealed class Entry(IncidentFrameRecord record, ushort[]? context, List<Crop>? crops, int width, int height, int binning, DateTimeOffset start,
        IReadOnlyDictionary<string, string> keywords)
    {
        public IncidentFrameRecord Record { get; } = record;

        public ushort[]? Context { get; set; } = context;

        public List<Crop>? Crops { get; set; } = crops;

        public int Width { get; } = width;

        public int Height { get; } = height;

        public int Binning { get; } = binning;

        public DateTimeOffset Start { get; } = start;

        public IReadOnlyDictionary<string, string> Keywords { get; } = keywords;

        /// <summary>An incident holds its images (they must not be reused).</summary>
        public bool Shared { get; set; }
    }

    /// <summary>A frame of an open incident.</summary>
    private sealed class Slot(IncidentFrameRecord record, ushort[]? context, List<Crop>? crops, int width, int height, int binning, DateTimeOffset start,
        IReadOnlyDictionary<string, string>? keywords)
    {
        public IncidentFrameRecord Record { get; } = record;

        public ushort[]? Context { get; private set; } = context;

        public List<Crop>? Crops { get; private set; } = crops;

        public int Width { get; } = width;

        public int Height { get; } = height;

        public int Binning { get; } = binning;

        public DateTimeOffset Start { get; } = start;

        public IReadOnlyDictionary<string, string>? Keywords { get; } = keywords;

        /// <summary>Reopened incidents: the images are kept (around a trigger or the recovery).</summary>
        public bool Keep { get; set; }

        public void DropImages()
        {
            Context = null;
            Crops = null;
        }
    }

    private sealed class TriggerBuilder(DateTimeOffset time, IncidentKind kind, GuideErrorCode? code, string message, string? detail, long? frame, string? note)
    {
        public DateTimeOffset Time { get; } = time;

        public IncidentKind Kind { get; } = kind;

        public string Message { get; } = message;

        /// <summary>Note of a manual mark.</summary>
        public string? Note { get; } = note;

        /// <summary>Null until the next frame when the trigger came between frames.</summary>
        public long? Frame { get; set; } = frame;

        /// <summary>A held spike: the last good frame before it (for the key frames).</summary>
        public (GuideFrame Frame, IncidentFrameRecord Record)? LastGood { get; init; }

        /// <summary>A held spike: its own frame (for the key frames).</summary>
        public GuideFrame? TriggerFrame { get; init; }

        /// <summary>A held spike: the rolling RMS before it.</summary>
        public double? Baseline { get; init; }

        public IncidentTrigger Build() => new(Time, Kind, code, Message, detail, Frame);
    }

    private sealed class Open(string id, IncidentKind kind)
    {
        public string Id { get; } = id;

        public IncidentKind Kind { get; } = kind;

        /// <summary>The stored incident this one continues (reopened), else null.</summary>
        public Incident? Previous { get; init; }

        public int Occurrences { get; init; } = 1;

        public bool Reopened { get; init; }

        public required IncidentTags Tags { get; init; }

        public string? Note { get; set; }

        public string? SettingsJson { get; init; }

        public DateTimeOffset CapAt { get; init; }

        /// <summary>Rolling RMS (″) when the occurrence started: the recovery threshold is a multiple of it.</summary>
        public double? Baseline { get; init; }

        public List<TriggerBuilder> Triggers { get; } = [];

        public List<IncidentMarker> Markers { get; } = [];

        public List<Slot> Frames { get; } = [];

        public Dictionary<long, GuideFrame> Keys { get; } = [];

        /// <summary>Frames whose full image becomes a key frame when they are recorded.</summary>
        public HashSet<long> KeyWanted { get; } = [];

        public bool NeedsRecovery { get; set; }

        public DateTimeOffset ManualUntil { get; set; } = DateTimeOffset.MinValue;

        public int CalmFrames { get; set; }

        public DateTimeOffset? RecoveredAt { get; set; }

        public bool PendingStop { get; set; }

        public int ImagesAfterTrigger { get; set; }

        public int ImagesAfterRecovery { get; set; }
    }

    /// <summary>The last closed incident (telemetry only), for reopening it.</summary>
    private sealed record Closed(string Id, IncidentKind Kind, DateTimeOffset ClosedAt, Incident Incident);

    #endregion
}
