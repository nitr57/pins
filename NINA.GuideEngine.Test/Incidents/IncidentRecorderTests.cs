// SPDX-License-Identifier: MPL-2.0

using FluentAssertions;
using NUnit.Framework;
using NINA.GuideEngine.Core;
using NINA.GuideEngine.Guiding;
using NINA.GuideEngine.Incidents;
using NINA.GuideEngine.Simulation;
using NINA.GuideEngine.Test.TestSupport;

namespace NINA.GuideEngine.Test.Incidents;

/// <summary>
/// The flight recorder in the closed-loop simulator (docs/INCIDENTS.md §6): one incident per injected fault with the right
/// triggers, windows, end reasons and likely causes (the diagnoser's rules are tested there).
/// </summary>
[TestFixture]
[NonParallelizable]
public class IncidentRecorderTests
{
    internal static readonly SettleParams Settle = new(1.5, 10, 120);

    [Test]
    public async Task Clouds_record_one_star_lost_incident_until_recovered()
    {
        using var h = new Harness(SimulatorScenario.GoodMount);
        h.AtGuideStep(40, () => h.Sim.InjectFault(SimulatorFault.Clouds));
        _ = h.Guider.StartGuidingAsync(Settle);
        await h.RunFor(TimeSpan.FromMinutes(12));

        var incident = (await h.Incidents()).Should().ContainSingle().Subject;
        h.Describe(incident);
        incident.Kind.Should().Be(IncidentKind.StarLost);
        incident.Triggers[0].Code.Should().Be(GuideErrorCode.StarLost);
        incident.Diagnosis!.Cause.Should().Be(IncidentCause.Clouds);
        incident.Triggers.Should().OnlyContain(t => t.Kind == IncidentKind.StarLost || t.Kind == IncidentKind.Spike);
        incident.EndReason.Should().Be(IncidentEndReason.Recovered);
        incident.Occurrences.Should().Be(1);
        incident.Tags.Simulator.Should().BeTrue();
        incident.Tags.PixelScale.Should().BeApproximately(h.Guider.PixelScale, 1e-9, "filled in by the guider");

        // window: 2 minutes before the trigger, until 30 s after the recovery
        var trigger = incident.Triggers[0].Time;
        (trigger - incident.Start).TotalSeconds.Should().BeInRange(116, 122.5);
        var recovered = incident.Markers.Single(m => m.Type == IncidentMarkerType.Recovered);
        (incident.End - recovered.Time).TotalSeconds.Should().BeInRange(30, 33);
        incident.Markers.Select(m => m.Type).Should().Contain([IncidentMarkerType.Trigger, IncidentMarkerType.Recovered, IncidentMarkerType.End]);
        incident.Markers.Should().NotContain(m => m.Type == IncidentMarkerType.Gap);

        // every frame with telemetry and images; the lost frames say why
        var frames = incident.Frames;
        frames.Select(f => f.Frame).Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
        (frames[^1].Frame - frames[0].Frame).Should().Be(frames.Count - 1, "no frame is missing");
        frames.Should().OnlyContain(f => f.HasContext && f.Crops > 0);
        frames.Should().Contain(f => !f.StarFound && f.LostStatus != null && (f.State == GuiderState.LostLock || f.State == GuiderState.Reacquiring));
        frames.Where(f => f.StarFound && f.RaDistanceRaw != null).Should().NotBeEmpty()
            .And.OnlyContain(f => f.Stars.Count > 1 && f.Mount != null && f.Snr > 0 && f.MeasurementSigmaPx > 0);
        frames.Where(f => !f.StarFound).Should().OnlyContain(f => f.MeasurementSigmaPx == null);
        frames.Count(f => f.HasKey).Should().BeInRange(3, IncidentRecorder.MaxKeyFrames, "the last good frame, the trigger frame and the recovery");
        frames.Single(f => f.Frame == incident.Triggers[0].Frame).HasKey.Should().BeTrue();

        incident.SensorWidth.Should().Be(1936);
        incident.ContextBinning.Should().Be(4);
        var context = h.Store.ReadImage(incident.Id, IncidentImageKind.Context, frames[0].Frame)!;
        (context.Width, context.Height, context.Binning).Should().Be((484, 304, 4));
        var crops = h.Store.ReadCrops(incident.Id, frames[0].Frame);
        crops[0].Width.Should().Be(31, "2 × 15 + 1");
        incident.Context.SearchRegionPx.Should().Be(15);
        incident.Context.RaRatePxPerMs.Should().BeGreaterThan(0);
        incident.SettingsJson.Should().Contain("\"exposureMs\":2000");

        // events: the alert names the incident, started and saved were raised
        h.Events.OfType<AlertEvent>().Where(a => a.Code == GuideErrorCode.StarLost).Should().OnlyContain(a => a.IncidentId == incident.Id);
        h.Events.OfType<AlertEvent>().Where(a => a.IncidentId != null).Should().OnlyContain(a => a.IncidentId == incident.Id);
        h.Events.OfType<IncidentStartedEvent>().Should().ContainSingle(e => e.Id == incident.Id && e.Kind == IncidentKind.StarLost);
        h.Events.OfType<IncidentSavedEvent>().Should().Contain(e => e.Summary.Id == incident.Id && e.Summary.Frames.Count == 0 && e.Summary.FrameCount == frames.Count);
        h.Guider.RecordingIncidentId.Should().BeNull();
    }

    [Test]
    [Category("Slow")]
    public async Task Repeated_clouds_reopen_one_ongoing_incident()
    {
        using var h = new Harness(SimulatorScenario.GoodMount);
        h.AtGuideStep(40, () => h.Sim.InjectFault(SimulatorFault.Clouds));
        int after = -1;
        h.OnEvent = e =>
        {
            if (e is IncidentSavedEvent && after < 0)
            {
                after = 0;
            }
            else if (e is GuideStepEvent && after >= 0 && ++after == 45)
            {
                h.Sim.InjectFault(SimulatorFault.Clouds);
            }
        };
        _ = h.Guider.StartGuidingAsync(Settle);
        await h.RunFor(TimeSpan.FromMinutes(18));

        var incident = (await h.Incidents()).Should().ContainSingle().Subject;
        h.Describe(incident);
        incident.Occurrences.Should().Be(2);
        incident.Ongoing.Should().BeTrue();
        incident.Diagnosis!.Cause.Should().Be(IncidentCause.Clouds);
        incident.EndReason.Should().Be(IncidentEndReason.Recovered);
        incident.Triggers.Count(t => t.Code == GuideErrorCode.StarLost).Should().Be(2);
        incident.Markers.Count(m => m.Type == IncidentMarkerType.Recovered).Should().Be(2);
        incident.Markers.Should().ContainSingle(m => m.Type == IncidentMarkerType.End);
        incident.Markers.Should().Contain(m => m.Type == IncidentMarkerType.Gap);
        var gap = incident.Markers.First(m => m.Type == IncidentMarkerType.Gap);

        var frames = incident.Frames;
        (frames[^1].Frame - frames[0].Frame).Should().Be(frames.Count - 1, "the telemetry of the gap is complete");
        frames.Single(f => f.Frame == gap.Frame).HasContext.Should().BeFalse();
        long second = incident.Triggers.Last(t => t.Code == GuideErrorCode.StarLost).Frame!.Value;
        frames.Where(f => f.Frame >= second - IncidentRecorder.RepeatImageFrames && f.Frame <= second + 2).Should().HaveCount(IncidentRecorder.RepeatImageFrames + 3)
            .And.OnlyContain(f => f.HasContext, "images around the repeat");
        frames.Count(f => !f.HasContext).Should().BeGreaterThan(20);
        h.Events.OfType<IncidentSavedEvent>().Count(e => e.Summary.Id == incident.Id).Should().Be(2, "saved again after the repeat");
    }

    [Test]
    public async Task Bump_records_a_spike()
    {
        using var h = new Harness(SimulatorScenario.GoodMount);
        h.AtGuideStep(40, () => h.Sim.InjectFault(SimulatorFault.Bump));
        _ = h.Guider.StartGuidingAsync(Settle);
        await h.RunFor(TimeSpan.FromMinutes(8));

        var incident = (await h.Incidents()).Should().ContainSingle().Subject;
        h.Describe(incident);
        incident.Kind.Should().Be(IncidentKind.Spike);
        incident.Triggers[0].Code.Should().BeNull();
        incident.Diagnosis!.Cause.Should().Be(IncidentCause.FieldJump);
        incident.Triggers[0].Message.Should().StartWith("Spike");
        incident.EndReason.Should().Be(IncidentEndReason.Recovered);
        var spike = incident.Frames.Single(f => f.Frame == incident.Triggers[0].Frame);
        (Math.Sqrt(spike.RaDistanceRaw!.Value * spike.RaDistanceRaw.Value + spike.DecDistanceRaw!.Value * spike.DecDistanceRaw.Value)).Should().BeGreaterThan(8);
        spike.HasKey.Should().BeTrue();
        incident.Frames.Single(f => f.Frame == spike.Frame - 1).HasKey.Should().BeTrue("the last good frame before it");
    }

    [Test]
    public async Task Bump_beyond_the_search_region_records_a_lost_star_from_a_field_jump()
    {
        // 33 px: the primary and the secondaries leave their search boxes; the reacquire finds the star again at the new place
        using var h = new Harness(SimulatorScenario.GoodMount);
        double pixel = h.Sim.Camera.Config.PixelScale;
        h.AtGuideStep(40, () => h.Sim.Mount.Bump(new SkyOffset(32 * pixel, 10 * pixel)));
        _ = h.Guider.StartGuidingAsync(Settle);
        await h.RunFor(TimeSpan.FromMinutes(8));

        var incident = (await h.Incidents()).Should().ContainSingle().Subject;
        h.Describe(incident);
        incident.Kind.Should().Be(IncidentKind.StarLost);
        incident.Diagnosis!.Cause.Should().Be(IncidentCause.FieldJump, "not clouds: the stars left their boxes, they did not fade");
        incident.Diagnosis.Evidence[0].Frame.Should().Be(incident.Triggers[0].Frame, "the frame where the star was lost");
        incident.EndReason.Should().Be(IncidentEndReason.Recovered);
    }

    [Test]
    [Category("Slow")]
    public async Task Calm_hour_records_nothing()
    {
        using var h = new Harness(SimulatorScenario.GoodMount);
        _ = h.Guider.StartGuidingAsync(Settle);
        await h.RunFor(TimeSpan.FromMinutes(63));

        (await h.Incidents()).Should().BeEmpty();
        h.Events.OfType<GuideStepEvent>().Count().Should().BeGreaterThan(1700);
        h.Events.OfType<AlertEvent>().Should().NotContain(a => a.IncidentId != null);
        h.Events.OfType<IncidentStartedEvent>().Should().BeEmpty();
    }

    [Test]
    public async Task Mount_that_stops_responding_records_one_incident()
    {
        using var h = new Harness(SimulatorScenario.GoodMount);
        h.AtGuideStep(40, () => h.Sim.InjectFault(SimulatorFault.MountStopsResponding));
        _ = h.Guider.StartGuidingAsync(Settle);
        await h.RunFor(TimeSpan.FromMinutes(6));

        var incident = (await h.Incidents()).Should().ContainSingle().Subject;
        h.Describe(incident);
        incident.Kind.Should().Be(IncidentKind.MountNotResponding);
        incident.Diagnosis!.Cause.Should().Be(IncidentCause.MountNotMoving);
        incident.Triggers.Select(t => t.Kind).Should().Contain(IncidentKind.Spike, "the jump came first and joined");
        incident.EndReason.Should().Be(IncidentEndReason.Stopped);
        h.Guider.State.Should().Be(GuiderState.Failed);
        incident.Frames[^1].State.Should().Be(GuiderState.Failed, "the frame that ended guiding is part of it");
    }

    [Test]
    public async Task Runaway_records_one_incident()
    {
        using var h = new Harness(SimulatorScenario.GoodMount);
        h.AtGuideStep(40, () => h.Sim.InjectFault(SimulatorFault.Runaway));
        _ = h.Guider.StartGuidingAsync(Settle);
        await h.RunFor(TimeSpan.FromMinutes(6));

        var incident = (await h.Incidents()).Should().ContainSingle().Subject;
        h.Describe(incident);
        incident.Kind.Should().Be(IncidentKind.Runaway);
        incident.Diagnosis!.Cause.Should().Be(IncidentCause.CalibrationMismatch);
        incident.Triggers[0].Code.Should().Be(GuideErrorCode.RunawayDetected);
        incident.EndReason.Should().Be(IncidentEndReason.Stopped);
    }

    [Test]
    public async Task Camera_failures_record_one_incident_until_recovered()
    {
        using var h = new Harness(SimulatorScenario.GoodMount);
        h.AtGuideStep(40, () => h.Sim.InjectFault(SimulatorFault.CameraFailure));
        _ = h.Guider.StartGuidingAsync(Settle);
        await h.RunFor(TimeSpan.FromMinutes(6));

        var incident = (await h.Incidents()).Should().ContainSingle().Subject;
        h.Describe(incident);
        incident.Kind.Should().Be(IncidentKind.CameraFailure);
        incident.Diagnosis!.Cause.Should().Be(IncidentCause.Camera);
        incident.Triggers.Select(t => t.Code).Should().Equal(GuideErrorCode.CameraCaptureFailed, GuideErrorCode.CameraCaptureFailed, GuideErrorCode.CameraReconnecting);
        incident.Triggers.Should().OnlyContain(t => t.Frame != null, "a trigger between frames belongs to the next frame");
        incident.EndReason.Should().Be(IncidentEndReason.Recovered);
        h.Guider.State.Should().Be(GuiderState.Stopped);
    }

    [Test]
    public async Task Calibration_failure_records_the_calibration()
    {
        using var h = new Harness(SimulatorScenario.GoodMount);
        h.Sim.Mount.NotResponding = true;
        _ = h.Guider.StartGuidingAsync(Settle);
        await h.RunFor(TimeSpan.FromMinutes(8));

        var incident = (await h.Incidents()).Should().ContainSingle().Subject;
        h.Describe(incident);
        incident.Kind.Should().Be(IncidentKind.CalibrationFailed);
        incident.Diagnosis!.Cause.Should().Be(IncidentCause.MountNotMoving);
        incident.Triggers[0].Code.Should().Be(GuideErrorCode.CalibrationFailedRaNoMove);
        incident.EndReason.Should().Be(IncidentEndReason.Stopped);
        var calibrating = h.Events.OfType<CalibratingEvent>().ToList();
        incident.Frames.Should().Contain(f => f.CalibrationDirection == "West" && f.CalibrationStep > 0 && f.RaDurationMs > 0);
        incident.Frames[0].Time.Should().BeOnOrBefore(calibrating[0].Timestamp, "from the calibration's first frame");
        (calibrating[0].Timestamp - incident.Frames[0].Time).TotalSeconds.Should().BeLessThan(5);
        if ((incident.End - incident.Start).TotalSeconds > 300)
        {
            incident.Markers.Should().Contain(m => m.Type == IncidentMarkerType.Gap, "a long calibration keeps its start and its end with images");
        }
    }

    [Test]
    public async Task Slew_pauses_and_recovers()
    {
        using var h = new Harness(SimulatorScenario.GoodMount);
        h.AtGuideStep(40, () => h.Sim.Mount.StartSlew(new SkyOffset(0, 0), TimeSpan.FromSeconds(10)));
        _ = h.Guider.StartGuidingAsync(Settle);
        await h.RunFor(TimeSpan.FromMinutes(6));

        var incident = (await h.Incidents()).Should().ContainSingle().Subject;
        h.Describe(incident);
        incident.Kind.Should().Be(IncidentKind.MountPaused);
        incident.Diagnosis!.Cause.Should().Be(IncidentCause.MountMoved);
        incident.Triggers[0].Code.Should().Be(GuideErrorCode.MountSlewing);
        incident.EndReason.Should().Be(IncidentEndReason.Recovered);
        incident.Frames.Should().Contain(f => f.State == GuiderState.Paused && f.Mount!.IsSlewing);
    }

    [Test]
    public async Task Manual_mark_records_two_minutes_before_and_30_seconds_after()
    {
        using var h = new Harness(SimulatorScenario.GoodMount);
        h.Guider.MarkIncident("too early", out var early).Should().BeNull();
        early.Should().Contain("guiding");
        string? id = null;
        DateTimeOffset markedAt = default;
        h.AtGuideStep(80, () =>
        {
            markedAt = h.Clock.UtcNow;
            id = h.Guider.MarkIncident("  wind gust? ", out var error);
            error.Should().BeNull();
            h.Guider.RecordingIncidentId.Should().Be(id);
        });
        _ = h.Guider.StartGuidingAsync(Settle);
        await h.RunFor(TimeSpan.FromMinutes(8));

        var incident = (await h.Incidents()).Should().ContainSingle().Subject;
        h.Describe(incident);
        incident.Id.Should().Be(id).And.EndWith("-Manual");
        incident.Kind.Should().Be(IncidentKind.Manual);
        incident.EndReason.Should().Be(IncidentEndReason.Manual);
        incident.Note.Should().Be("wind gust?");
        incident.Markers.Should().Contain(m => m.Type == IncidentMarkerType.Note && m.Text == "wind gust?");
        (markedAt - incident.Start).TotalSeconds.Should().BeInRange(116, 122.5);
        (incident.End - markedAt).TotalSeconds.Should().BeInRange(30, 32.5);
        incident.Triggers[0].Frame.Should().NotBeNull();
    }

    [Test]
    public async Task Stopping_guiding_ends_an_incident_as_stopped()
    {
        using var h = new Harness(SimulatorScenario.GoodMount);
        h.AtGuideStep(40, () => h.Guider.MarkIncident(null, out _));
        h.AtGuideStep(45, () => h.Guider.StopGuiding());
        _ = h.Guider.StartGuidingAsync(Settle);
        await h.RunFor(TimeSpan.FromMinutes(6));

        var incident = (await h.Incidents()).Should().ContainSingle().Subject;
        incident.EndReason.Should().Be(IncidentEndReason.Stopped);
        incident.Frames.Should().OnlyContain(f => IncidentRecorder.IsRecordingState(f.State), "looping frames are not recorded");
    }

    [Test]
    public async Task A_failing_tag_provider_is_reported_and_recording_goes_on()
    {
        using var h = new Harness(SimulatorScenario.GoodMount);
        h.Guider.SetIncidentStore(h.Store, () => throw new InvalidOperationException("no profile"));
        h.AtGuideStep(40, () => h.Guider.MarkIncident(null, out _));
        _ = h.Guider.StartGuidingAsync(Settle);
        await h.RunFor(TimeSpan.FromMinutes(6));

        (await h.Incidents()).Should().ContainSingle("the recorder keeps the previous tags");
        h.Events.OfType<EngineFaultEvent>().Should().NotBeEmpty()
            .And.OnlyContain(f => f.Source == "IncidentRecorder.Tags" && f.Message == "InvalidOperationException: no profile");
    }

    [Test]
    public async Task Turned_off_records_and_buffers_nothing()
    {
        using var h = new Harness(SimulatorScenario.GoodMount, s => s with { Incidents = s.Incidents with { Enabled = false } });
        h.AtGuideStep(40, () =>
        {
            h.Sim.InjectFault(SimulatorFault.Clouds);
            h.Guider.MarkIncident(null, out var error).Should().BeNull();
            error.Should().Contain("off");
            h.Guider.IncidentRecorder!.BufferedBytes.Should().Be(0);
        });
        _ = h.Guider.StartGuidingAsync(Settle);
        await h.RunFor(TimeSpan.FromMinutes(8));

        (await h.Incidents()).Should().BeEmpty();
        h.Events.OfType<AlertEvent>().Should().Contain(a => a.Code == GuideErrorCode.StarLost).And.OnlyContain(a => a.IncidentId == null);
    }

    /// <summary>A simulator, a guider with a store in a temp folder and an event recorder.</summary>
    internal sealed class Harness : IDisposable
    {
        private readonly object gate = new();
        private readonly List<GuiderEvent> events = [];
        private readonly List<(int Step, Action Action)> atSteps = [];
        private int guideSteps;
        private bool settled;

        public Harness(SimulatorScenario scenario, Func<GuiderSettings, GuiderSettings>? configure = null)
        {
            Clock = new VirtualClock();
            Sim = new Simulator(scenario, Clock);
            Directory = Path.Combine(Path.GetTempPath(), "pins-incidents-" + Guid.NewGuid().ToString("N"));
            Store = new IncidentStore(Directory, new IncidentStoreOptions { SimulatorBudgetBytes = 100_000 * IncidentStoreOptions.MB, SimulatorMaxIncidents = 100 },
                _ => long.MaxValue);
            var settings = new GuiderSettings { FocalLengthMm = scenario.Camera.FocalLengthMm, ExposureMs = 2000 };
            settings = configure?.Invoke(settings) ?? settings;
            Guider = new Guider(Sim.Camera, Sim.Mount, Sim.Mount, Clock, settings, ditherSeed: 5)
            {
                AutoStartLoop = false,
            };
            Guider.SetIncidentStore(Store, () => new IncidentTags { ProfileName = "Test", GuideCamera = "Simulator", Mount = "Simulator", Simulator = true });
            Guider.EventRaised += (_, e) =>
            {
                // a frame event carries the whole image: keeping every one would hold gigabytes in a long run
                if (e is not FrameReadyEvent)
                {
                    lock (gate)
                    {
                        events.Add(e);
                    }
                }

                if (e is SettleDoneEvent)
                {
                    settled = true;
                }

                if (e is GuideStepEvent { IsSettling: false } && settled)
                {
                    guideSteps++;
                    foreach (var (_, action) in atSteps.Where(a => a.Step == guideSteps).ToList())
                    {
                        action();
                    }
                }

                OnEvent?.Invoke(e);
            };
        }

        public VirtualClock Clock { get; }

        public Simulator Sim { get; }

        public Guider Guider { get; }

        public IncidentStore Store { get; }

        public string Directory { get; }

        public Action<GuiderEvent>? OnEvent { get; set; }

        public IncidentFrameRecord? LastGuidingRecord { get; private set; }

        public IReadOnlyList<GuiderEvent> Events
        {
            get
            {
                lock (gate)
                {
                    return events.ToList();
                }
            }
        }

        /// <summary>Runs <paramref name="action"/> on the loop at the n-th guide step after the first settling.</summary>
        public void AtGuideStep(int step, Action action) => atSteps.Add((step, action));

        public async Task RunFor(TimeSpan duration)
        {
            Guider.StartLooping(Clock.CancelAt(Clock.Elapsed + duration));
            await Guider.WaitForLoopAsync().Within(duration);
            var last = Events.OfType<GuideStepEvent>().LastOrDefault();
            if (last is not null)
            {
                LastGuidingRecord = new IncidentFrameRecord
                {
                    Frame = last.Frame,
                    Time = last.Timestamp,
                    ExposureMs = 1000,
                    State = GuiderState.Guiding,
                    StarFound = true,
                    Lock = last.LockPosition,
                    Star = last.StarPosition,
                    RaDistanceRaw = last.RaDistanceRaw,
                    DecDistanceRaw = last.DecDistanceRaw,
                    Stars = last.Stars,
                };
            }
        }

        /// <summary>Stored incidents with their frames, newest first, once all saves finished.</summary>
        public async Task<IReadOnlyList<Incident>> Incidents()
        {
            await Guider.FlushIncidentsAsync();
            return Store.List().Select(s => Store.Get(s.Id)!).ToList();
        }

        public double Seconds(DateTimeOffset t) => (t - Clock.Epoch).TotalSeconds;

        public void Describe(Incident i)
        {
            TestContext.Out.WriteLine($"{i.Id}: {i.Kind}, {Seconds(i.Start):F0}-{Seconds(i.End):F0} s, {i.FrameCount} frames, end {i.EndReason}, occurrences {i.Occurrences}, {i.SizeBytes / 1e6:F1} MB");
            foreach (var t in i.Triggers)
            {
                TestContext.Out.WriteLine($"  trigger {Seconds(t.Time):F0} s frame {t.Frame}: {t.Kind} {t.Code} {t.Message} {t.Detail}");
            }

            foreach (var m in i.Markers)
            {
                TestContext.Out.WriteLine($"  marker {Seconds(m.Time):F0} s frame {m.Frame}: {m.Type} {m.Text}");
            }

            if (i.Diagnosis is { } d)
            {
                TestContext.Out.WriteLine($"  DIAGNOSIS {d.Cause}: {d.Message}");
                foreach (var e in d.Evidence)
                {
                    TestContext.Out.WriteLine($"    evidence {e.Code} frame {e.Frame}: {e.Message}");
                }
            }
        }

        public void Dispose()
        {
            try
            {
                if (System.IO.Directory.Exists(Directory))
                {
                    System.IO.Directory.Delete(Directory, recursive: true);
                }
            }
            catch (IOException)
            {
                // a save may still be finishing
            }
        }
    }
}
