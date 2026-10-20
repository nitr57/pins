// SPDX-License-Identifier: MPL-2.0

using FluentAssertions;
using Moq;
using NINA.Equipment.Equipment.MyGuider.Internal;
using NINA.Equipment.Interfaces.Mediator;
using NINA.GuideEngine.Algorithms;
using NINA.GuideEngine.Core;
using NINA.GuideEngine.Guiding;
using NUnit.Framework;

namespace NINA.Test.Equipment.MyGuider.Internal;

[TestFixture]
public class PlayModelPluginTests
{
    private const string MountId = "EQ6-R";

    private static readonly DateTimeOffset T0 = new(2026, 10, 19, 22, 0, 0, TimeSpan.Zero);

    private string dir = string.Empty;
    private string storePath = string.Empty;
    private PulseModelKey key = null!;
    private InternalGuider guider = null!;

    [SetUp]
    public void SetUp()
    {
        dir = Path.Combine(Path.GetTempPath(), "pins-play-model-" + Guid.NewGuid().ToString("N"));
        storePath = Path.Combine(dir, "backlash-play-model.json");
        var (_, service, _) = IncidentSettingsTests.Create();
        var profile = service.Object.ActiveProfile;
        Mock.Get(profile.TelescopeSettings).SetupGet(t => t.Id).Returns(MountId);
        key = new PulseModelKey(profile.Id.ToString(), MountId);
        guider = new InternalGuider(service.Object, new Mock<ITelescopeMediator>().Object, new Mock<IGuideCameraMediator>().Object,
            Path.Combine(dir, "Incidents"), Path.Combine(dir, "periodic-error.json"), Path.Combine(dir, "pulse-model.json"));
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, true);
        }
    }

    private static BacklashPlayModelLearnedEvent Learned(DateTimeOffset at, double windows)
    {
        var state = new PlayModelState { LearnedAt = at, LogPosterior = [0, -1, -2], LearnedWindows = windows };
        var values = new PlayModelValues(1200, 600, 600, GuideDirection.North, true, "hard", 0.2, 3, 0, windows, 0, false);
        return new BacklashPlayModelLearnedEvent(at, state, values, InUse: true);
    }

    private double? StoredWindows() =>
        File.Exists(storePath) ? PlayModelStore.FromJson(File.ReadAllText(storePath)).Get(key)?.LearnedWindows : null;

    [Test]
    public void Learned_play_models_are_stored_at_most_once_per_interval()
    {
        guider.OnEngineEvent(null, Learned(T0, 1));
        StoredWindows().Should().Be(1, "the first learned model is stored at once");

        guider.OnEngineEvent(null, Learned(T0.AddSeconds(10), 2));
        guider.OnEngineEvent(null, Learned(T0.AddSeconds(30), 3));
        StoredWindows().Should().Be(1, "within the interval the file is not rewritten");

        guider.OnEngineEvent(null, Learned(T0 + InternalGuider.PlayModelSaveInterval, 4));
        StoredWindows().Should().Be(4, "the latest model is stored once the interval has passed");
    }

    [Test]
    public void A_state_change_stores_the_pending_play_model()
    {
        guider.OnEngineEvent(null, Learned(T0, 1));
        guider.OnEngineEvent(null, Learned(T0.AddSeconds(20), 2));
        StoredWindows().Should().Be(1);

        guider.OnEngineEvent(null, new AppStateEvent(T0.AddSeconds(25), GuiderState.Stopped, GuiderState.Guiding));
        StoredWindows().Should().Be(2, "stopping keeps what was learned since the last store");
    }

    [Test]
    public void A_state_change_without_a_pending_model_does_not_delay_the_next_store()
    {
        guider.OnEngineEvent(null, Learned(T0, 1));
        guider.OnEngineEvent(null, new AppStateEvent(T0.AddSeconds(50), GuiderState.Guiding, GuiderState.Calibrating));

        guider.OnEngineEvent(null, Learned(T0 + InternalGuider.PlayModelSaveInterval, 2));
        StoredWindows().Should().Be(2);
    }
}
