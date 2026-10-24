// SPDX-License-Identifier: MPL-2.0

using System.Runtime.CompilerServices;
using FluentAssertions;
using Moq;
using NINA.Equipment.Interfaces.Mediator;
using NUnit.Framework;
using NINA.GuideEngine.Algorithms;
using NINA.GuideEngine.Guiding;
using NINA.Equipment.Equipment.MyGuider.Internal;
using Serilog.Events;

namespace NINA.Test.Equipment.MyGuider.Internal;

[TestFixture]
public class PeriodicErrorPluginTests
{
    private const string MountId = "EQ6-R";

    private string dir = string.Empty;
    private string storePath = string.Empty;
    private PeriodicErrorKey key = null!;
    private InternalGuider guider = null!;

    [SetUp]
    public void SetUp()
    {
        dir = Path.Combine(Path.GetTempPath(), "pins-periodic-error-" + Guid.NewGuid().ToString("N"));
        storePath = Path.Combine(dir, "periodic-error.json");
        var (_, service, _) = IncidentSettingsTests.Create();
        var profile = service.Object.ActiveProfile;
        Mock.Get(profile.TelescopeSettings).SetupGet(t => t.Id).Returns(MountId);
        key = new PeriodicErrorKey(profile.Id.ToString(), MountId);
        guider = new InternalGuider(service.Object, new Mock<ITelescopeMediator>().Object, new Mock<IGuideCameraMediator>().Object,
            Path.Combine(dir, "Incidents"), storePath, Path.Combine(dir, "pulse-model.json"));
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, true);
        }
    }

    private static PeriodicErrorModel Model(double periodSeconds) => new()
    {
        PeriodSeconds = periodSeconds,
        Sin = [1.5, 0.2, 0.1],
        Cos = [0.3, 0.1, 0.05],
        Cycles = 6,
        LearnedAt = new DateTimeOffset(2026, 9, 26, 22, 0, 0, TimeSpan.Zero),
    };

    private void Store(params (PeriodicErrorKey Key, PeriodicErrorModel Model)[] entries)
    {
        var store = new PeriodicErrorStore();
        foreach (var (k, m) in entries)
        {
            store.Set(k, m);
        }

        Directory.CreateDirectory(dir);
        File.WriteAllText(storePath, store.ToJson());
    }

    private PeriodicErrorStore Stored() => PeriodicErrorStore.FromJson(File.ReadAllText(storePath));

    private void Discard(double periodSeconds) =>
        guider.OnEngineEvent(null, new PeriodicErrorModelDiscardedEvent(DateTimeOffset.UtcNow, Model(periodSeconds)));

    [Test]
    public void Status_says_whether_the_periodic_error_curve_is_stable()
    {
        var predictive = new PredictiveAlgorithm(periodicError: true);
        InternalGuider.ToDto(predictive, 1.5)!.PeriodicError!.Stable.Should().BeFalse("nothing learned yet");

        predictive.RestorePeriodicError(Model(478.7)).Should().BeTrue();
        predictive.CorrectionApplied(predictive.Result(0.1, DateTimeOffset.UnixEpoch)); // the state is taken per frame
        var restored = InternalGuider.ToDto(predictive, 1.5)!.PeriodicError!;
        restored.Stable.Should().Be(predictive.State.PeriodicError!.Stable).And.BeTrue("a restored curve is stable until it fails the test");
        restored.PeriodSeconds.Should().BeApproximately(478.7, 1e-9);
    }

    [Test]
    public void A_discarded_curve_is_deleted_from_the_store()
    {
        var otherProfile = key with { ProfileId = Guid.NewGuid().ToString() };
        var otherMount = key with { MountName = "AZ-GTi" };
        Store((key, Model(478.7)), (otherProfile, Model(478.7)), (otherMount, Model(478.7)));

        Discard(478.7);

        var stored = Stored();
        stored.Get(key).Should().BeNull("the discarded curve is deleted and the file saved");
        stored.Count.Should().Be(2);
        stored.Get(otherProfile).Should().BeEquivalentTo(Model(478.7), "the other profile's curve is saved again as it was");
        stored.Get(otherMount).Should().BeEquivalentTo(Model(478.7), "the other mount's curve is saved again as it was");
        File.Exists(storePath + ".tmp").Should().BeFalse();
    }

    [Test]
    public void A_curve_stored_since_with_another_period_is_kept()
    {
        Store((key, Model(700)));
        string before = File.ReadAllText(storePath);

        Discard(478.7);

        File.ReadAllText(storePath).Should().Be(before, "the 700 s curve replaced the discarded one");
        Stored().Get(key)!.PeriodSeconds.Should().Be(700);
    }

    [Test]
    public void The_curves_of_other_profiles_and_mounts_are_untouched()
    {
        Store((key with { ProfileId = Guid.NewGuid().ToString() }, Model(478.7)), (key with { MountName = "AZ-GTi" }, Model(478.7)));
        string before = File.ReadAllText(storePath);

        Discard(478.7);

        File.ReadAllText(storePath).Should().Be(before);
    }

    [Test]
    public void An_unreadable_store_only_logs_a_warning()
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(storePath, "{ not json");
        var sink = new LogSink();

        // NINA's Logger sets Serilog's Log.Logger in its static constructor: run it before the swap
        RuntimeHelpers.RunClassConstructor(typeof(NINA.Core.Utility.Logger).TypeHandle);
        var previous = Serilog.Log.Logger;
        Serilog.Log.Logger = new Serilog.LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger();
        try
        {
            FluentActions.Invoking(() => Discard(478.7)).Should().NotThrow();
        }
        finally
        {
            Serilog.Log.Logger = previous;
        }

        File.ReadAllText(storePath).Should().Be("{ not json", "nothing is written over it");
        sink.Events.Where(e => e.Level >= LogEventLevel.Warning)
            .Should().ContainSingle().Which.Level.Should().Be(LogEventLevel.Warning);
        sink.Events.Single(e => e.Level == LogEventLevel.Warning).Properties["message"].ToString()
            .Should().Contain("could not read the periodic error store");
    }

    private sealed class LogSink : Serilog.Core.ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];

        public void Emit(LogEvent logEvent)
        {
            lock (Events)
            {
                Events.Add(logEvent);
            }
        }
    }
}
