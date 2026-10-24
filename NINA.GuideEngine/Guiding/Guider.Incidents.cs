// SPDX-License-Identifier: MPL-2.0

using System.Text.Json;
using NINA.GuideEngine.Algorithms;
using NINA.GuideEngine.Core;
using NINA.GuideEngine.Imaging;
using NINA.GuideEngine.Incidents;
using NINA.GuideEngine.MultiStar;

namespace NINA.GuideEngine.Guiding;

/// <summary>
/// Flight recorder integration (docs/INCIDENTS.md): hands every frame processed while calibrating or guiding, the alerts and
/// the state changes to the <see cref="IncidentRecorder"/>, and forwards the store's saved/deleted notifications as events.
/// The frame is recorded after its pulses went out.
/// </summary>
public sealed partial class Guider
{
    private readonly object incidentGate = new();
    private volatile IncidentRecorder? recorder;
    private volatile IncidentStore? incidentStore;

    // what the current frame produced (loop task only)
    private PreprocessInfo framePreprocess;
    private GuiderState frameStartState;
    private DateTimeOffset frameTime;
    private bool inIncidentFrame;
    private bool frameSettling;
    private bool frameDithering;
    private MultiStarFrameResult? frameResult;
    private IReadOnlyList<PulseCommand> framePulses = [];
    private GuideStepEvent? frameStep;
    private StarLostEvent? frameLost;
    private CalibratingEvent? frameCalibration;

    /// <summary>Where incidents are saved, null when the host set none.</summary>
    public IncidentStore? Incidents => incidentStore;

    /// <summary>Id of the incident being recorded right now, null when none.</summary>
    public string? RecordingIncidentId => recorder?.RecordingId;

    /// <summary>
    /// Sets where incidents are saved (null stops recording; an open incident is saved as stopped) and where their tags
    /// come from (called on the loop task when an incident starts and when guiding starts: keep it cheap). Recording also
    /// needs <see cref="IncidentSettings.Enabled"/>.
    /// </summary>
    public void SetIncidentStore(IncidentStore? store, Func<IncidentTags>? tags = null)
    {
        lock (incidentGate)
        {
            var r = recorder ??= new IncidentRecorder(clock, PublishEvent, IncidentContextNow, IncidentSettingsJson);
            if (!ReferenceEquals(incidentStore, store))
            {
                if (incidentStore is { } old)
                {
                    old.Saved -= OnIncidentSaved;
                    old.Deleted -= OnIncidentDeleted;
                }

                if (store is not null)
                {
                    store.Saved += OnIncidentSaved;
                    store.Deleted += OnIncidentDeleted;
                }
            }

            r.Configure(settings.Incidents);
            r.SetStore(store, tags is null ? null : () => WithPixelScale(tags()));
            incidentStore = store;
        }
    }

    /// <summary>
    /// Records the last 2 minutes and the next 30 s as a manual incident with an optional note, or adds the mark to the
    /// incident being recorded. Returns the incident id, or null with <paramref name="error"/> when the recorder is off or
    /// the guider is not guiding or calibrating.
    /// </summary>
    public string? MarkIncident(string? note, out string? error)
    {
        if (recorder is not { } r)
        {
            error = "The incident recorder is off";
            return null;
        }

        return r.Mark(note, state, out error);
    }

    /// <summary>Completes when the incidents closed so far are saved.</summary>
    public Task FlushIncidentsAsync() => recorder?.Saves ?? Task.CompletedTask;

    internal IncidentRecorder? IncidentRecorder => recorder;

    private IncidentTags WithPixelScale(IncidentTags t) => t.PixelScale > 0 ? t : t with { PixelScale = pixelScale };

    private void OnIncidentSaved(object? sender, Incident summary) => PublishEvent(new IncidentSavedEvent(clock.UtcNow, summary));

    private void OnIncidentDeleted(object? sender, string id) => PublishEvent(new IncidentDeletedEvent(clock.UtcNow, id));

    /// <summary>The incident an alert started or joined (see <see cref="IncidentRecorder.KindOf"/>), null when none.</summary>
    private string? IncidentForAlert(GuideErrorCode code, GuideErrorInfo info, string? detail)
    {
        try
        {
            return recorder?.OnAlert(code, info.Title, detail, state, inIncidentFrame ? frameNumber : null);
        }
        catch (Exception ex)
        {
            // the recorder must never break alerting or the guide loop
            ReportFault("IncidentRecorder.OnAlert", ex);
            return null;
        }
    }

    /// <summary>Called by <see cref="Emit"/> for every event: collects the current frame's results and follows the state.</summary>
    private void ObserveIncidentEvent(GuiderEvent e)
    {
        if (recorder is not { } r)
        {
            return;
        }

        try
        {
            switch (e)
            {
                case GuideStepEvent s:
                    frameStep = s;
                    break;
                case StarLostEvent l:
                    frameLost = l;
                    break;
                case CalibratingEvent c:
                    frameCalibration = c;
                    break;
                case AppStateEvent st:
                    r.OnStateChanged(st.State, st.Previous, inIncidentFrame);
                    break;
                case StartGuidingEvent:
                    r.OnGuidingStarted();
                    break;
                case ResumedEvent:
                    r.OnResumed();
                    break;
            }
        }
        catch (Exception ex)
        {
            // the recorder must never break event delivery or the guide loop
            ReportFault("IncidentRecorder.OnEvent", ex);
        }
    }

    private void BeginIncidentFrame()
    {
        inIncidentFrame = true;
        frameStartState = state;
        frameTime = clock.UtcNow;
        frameSettling = settle.IsActive;
        frameDithering = recenter.IsActive || (settle.IsActive && pending is { Kind: PendingKind.Dither });
        frameResult = null;
        framePulses = [];
        frameStep = null;
        frameLost = null;
        frameCalibration = null;
    }

    private void EndIncidentFrame(GuideFrame frame, MountSnapshot snapshot, bool processed)
    {
        try
        {
            if (processed && recorder is { } r && (IncidentRecorder.IsRecordingState(frameStartState) || IncidentRecorder.IsRecordingState(state)) && r.IsActive)
            {
                r.OnFrame(IncidentRecord(frame, snapshot), frame,
                    new IncidentFrameInfo(framePreprocess, Preprocessor.Darks?.Name, pixelScale, tracker.SearchRegion));
            }
        }
        catch (Exception ex)
        {
            // the recorder must never break the guide loop
            ReportFault("IncidentRecorder.OnFrame", ex);
        }
        finally
        {
            inIncidentFrame = false;
            try
            {
                recorder?.FrameDone();
            }
            catch (Exception ex)
            {
                // as above
                ReportFault("IncidentRecorder.FrameDone", ex);
            }
        }
    }

    private IncidentFrameRecord IncidentRecord(GuideFrame frame, MountSnapshot snapshot)
    {
        var r = frameResult;
        var step = frameStep;
        var cal = frameCalibration;
        bool found = r?.StarFound ?? false;
        int raMs = 0, decMs = 0;
        GuideDirection? raDir = null, decDir = null;
        foreach (var p in framePulses)
        {
            if (p.Direction.Axis() == GuideAxis.Ra)
            {
                raMs += p.DurationMs;
                raDir = p.Direction;
            }
            else
            {
                decMs += p.DurationMs;
                decDir = p.Direction;
            }
        }

        static double? Finite(double? v) => v is { } x && double.IsFinite(x) ? x : null;
        return new IncidentFrameRecord
        {
            Frame = frame.FrameNumber,
            Time = step?.Timestamp ?? frameLost?.Timestamp ?? cal?.Timestamp ?? frameTime,
            ExposureMs = frame.ExposureMs > 0 ? frame.ExposureMs : settings.ExposureMs,
            State = state,
            Settling = step?.IsSettling ?? frameSettling,
            Dithering = frameDithering || step is { IsRecenterMove: true },
            CoachMeasurement = step?.CoachMeasurement ?? IsCoachMeasuring,
            StarFound = found,
            PrimaryEstimated = r?.IsEstimated ?? false,
            LostStatus = found || r is null ? null : LostStatus(r),
            Lock = lockPosition.IsValid ? lockPosition : null,
            Star = found && r!.Primary.Position.IsValid ? r.Primary.Position : null,
            Dx = Finite(step?.Dx ?? cal?.Dx),
            Dy = Finite(step?.Dy ?? cal?.Dy),
            RaDistanceRaw = Finite(step?.RaDistanceRaw),
            DecDistanceRaw = Finite(step?.DecDistanceRaw),
            RaDurationMs = raMs,
            RaDirection = raDir,
            DecDurationMs = decMs,
            DecDirection = decDir,
            RaLimited = step?.RaLimited ?? false,
            DecLimited = step?.DecLimited ?? false,
            Snr = Finite(frameLost?.Snr ?? r?.Primary.Snr),
            StarMass = Finite(frameLost?.StarMass ?? r?.Primary.Mass),
            Hfd = Finite(frameLost?.Hfd ?? r?.Primary.Hfd),
            MeasurementSigmaPx = Finite(step?.MeasurementSigmaPx ?? (found ? MeasurementUncertainty.FrameSigmaPx(r!, tracker.FinderOptions.FindMode) : null)),
            Stars = step?.Stars ?? (r is null ? [] : ToStarInfos(r)),
            Mount = ReferenceEquals(snapshot, UnknownMount) ? null : snapshot,
            CalibrationDirection = string.IsNullOrEmpty(cal?.Direction) ? null : cal.Direction,
            CalibrationStep = cal?.Step,
        };
    }

    /// <summary>Why the star was not found: the tracker's find result and its status text, e.g. "MassChange: Star lost - mass changed".</summary>
    private string LostStatus(MultiStarFrameResult r)
    {
        string text = frameLost?.Status ?? r.Status;
        return string.IsNullOrEmpty(text) ? r.ErrorCode.ToString() : $"{r.ErrorCode}: {text}";
    }

    private IncidentContext IncidentContextNow()
    {
        var cal = calibration;
        var s = settings;
        return new IncidentContext
        {
            SearchRegionPx = tracker.SearchRegion,
            MaxRaDurationMs = s.MaxRaDurationMs,
            MaxDecDurationMs = s.MaxDecDurationMs,
            RaRatePxPerMs = cal is { IsValid: true } ? cal.XRate : null,
            DecRatePxPerMs = cal is { HasDecCalibration: true } ? cal.YRate : null,
            RaAngleDeg = cal is { IsValid: true } ? cal.XAngle * 180.0 / Math.PI : null,
            DecAngleDeg = cal is { HasDecCalibration: true } && double.IsFinite(cal.YAngle) ? cal.YAngle * 180.0 / Math.PI : null,
            // only a worm's period (tooth count set or snapped): any other detected period is not the worm
            WormPeriodSeconds = (corrector.RaAlgorithm as PredictiveAlgorithm)?.PeriodicError is { Teeth: not null } pe ? pe.PeriodSeconds : null,
        };
    }

    private string? IncidentSettingsJson() => JsonSerializer.Serialize(settings, IncidentStore.Json);
}
