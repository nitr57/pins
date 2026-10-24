// SPDX-License-Identifier: MPL-2.0

using FluentAssertions;
using NUnit.Framework;
using NINA.GuideEngine.Algorithms;
using NINA.GuideEngine.Core;

namespace NINA.GuideEngine.Test.Algorithms;

/// <summary>The direction logic of Dec guide mode Drift: when it picks, keeps, switches and gives up a direction, and the safety valve.</summary>
[TestFixture]
public class DecDirectionPolicyTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 26, 22, 0, 0, TimeSpan.Zero);

    // a drift of `perMin` px/min known to ± `sigmaPerMin`, frame noise 0.1 px (floor 0.2 px/min)
    private static DriftEstimate Drift(double perMin, double sigmaPerMin = 0.05, double noisePx = 0.1) =>
        new(perMin / 60, sigmaPerMin / 60, 300, 150, noisePx);

    private DecDirectionPolicy policy = null!;
    private DateTimeOffset now;

    [SetUp]
    public void SetUp()
    {
        policy = new DecDirectionPolicy();
        now = T0;
    }

    // one frame every 2 s
    private void Frames(int count, DriftEstimate? drift, double offsetPx = 0, bool settling = false)
    {
        for (int i = 0; i < count; i++)
        {
            policy.Update(now, drift, offsetPx, settling, 1.5);
            now = now.AddSeconds(2);
        }
    }

    private List<DecDirectionNote> Notes(DecDirectionNoteKind kind) => policy.TakeNotes().Where(n => n.Kind == kind).ToList();

    // frames of one minute (the hold) and the one that picks the direction
    private const int HoldFrames = 30;

    private void Pick(double perMin) => Frames(HoldFrames + 1, Drift(perMin));

    [Test]
    public void Both_directions_until_a_drift_holds_for_a_minute()
    {
        Frames(30, null);
        policy.Direction.Should().Be(DecGuideDirection.Both);
        policy.Allowed.Should().BeNull();

        Frames(60, Drift(0.15, 0.02));
        policy.Direction.Should().Be(DecGuideDirection.Both, "significant (7.5 σ) but below the floor of 0.2 px/min");
        Frames(60, Drift(0.3, 0.15));
        policy.Direction.Should().Be(DecGuideDirection.Both, "not significant (2 σ)");
        Frames(60, Drift(0.3, 0.11));
        policy.Direction.Should().Be(DecGuideDirection.Both, "significant, but one standard error less is below the floor (0.3 − 0.11 < 0.2)");

        Frames(HoldFrames, Drift(0.6, 0.2));
        policy.Direction.Should().Be(DecGuideDirection.Both, "3 σ, and 0.6 − 0.2 reaches the floor, but only for 58 s");
        Frames(1, Drift(0.6, 0.2));
        policy.Direction.Should().Be(DecGuideDirection.South);
    }

    [Test]
    public void The_minute_starts_over_when_the_drift_stops_holding()
    {
        Frames(20, Drift(0.6));
        Frames(1, Drift(0.1));
        Frames(HoldFrames, Drift(0.6));
        policy.Direction.Should().Be(DecGuideDirection.Both);
        Frames(20, Drift(-0.6));
        policy.Direction.Should().Be(DecGuideDirection.Both, "the other direction starts its own minute");
        Frames(11, Drift(-0.6));
        policy.Direction.Should().Be(DecGuideDirection.North);

        policy.Reset();
        Frames(HoldFrames + 1, Drift(0.6, 0.2, noisePx: 0));
        policy.Direction.Should().Be(DecGuideDirection.South, "without noise the floor is 0: significance alone");
    }

    [TestCase(0.6, DecGuideDirection.South, GuideDirection.South)]
    [TestCase(-0.6, DecGuideDirection.North, GuideDirection.North)]
    public void Picks_the_direction_that_counters_the_drift(double perMin, DecGuideDirection expected, GuideDirection allowed)
    {
        Pick(perMin);
        policy.Direction.Should().Be(expected, "a positive drift grows the offset that South pulses correct");
        policy.Allowed.Should().Be(allowed);
        policy.Switches.Should().Be(1);
        var note = Notes(DecDirectionNoteKind.Switch).Should().ContainSingle().Subject;
        note.Message.Should().Contain($"guiding {expected} only").And.Contain("0.90″/min").And.Contain(perMin > 0 ? "towards North" : "towards South");
    }

    [Test]
    public void Keeps_the_direction_while_the_drift_is_in_its_favour()
    {
        Pick(0.6);
        Frames(300, Drift(0.12, 0.1));
        policy.Direction.Should().Be(DecGuideDirection.South, "weaker than the floor and not significant, but 1.2 σ in favour");
        policy.Switches.Should().Be(1);
    }

    [Test]
    public void Goes_back_to_both_directions_two_minutes_after_the_drift_faded()
    {
        Pick(0.6);
        Frames(60, Drift(0.04));
        policy.Direction.Should().Be(DecGuideDirection.South, "not 2 minutes yet");
        policy.Faded.Should().BeFalse();
        Frames(1, Drift(0.04));
        policy.Direction.Should().Be(DecGuideDirection.Both);
        policy.Allowed.Should().BeNull();
        policy.Faded.Should().BeTrue("the guider doubts the dead band from now on");
        Notes(DecDirectionNoteKind.Switch).Last().Message.Should().StartWith("both directions (the drift has not favoured South for 2 min)");

        Pick(0.6);
        policy.Direction.Should().Be(DecGuideDirection.South, "a drift that holds for a minute is picked again");
    }

    [Test]
    public void A_drift_too_weak_for_one_direction_goes_back_to_both()
    {
        Pick(0.6);
        Frames(61, Drift(0.05, 0.02));
        policy.Direction.Should().Be(DecGuideDirection.Both, "2.5 σ in favour, but even 0.07 px/min is far below the floor");
    }

    [Test]
    public void Switches_straight_to_the_opposite_direction_when_that_drift_holds()
    {
        Pick(0.6);
        Frames(30, Drift(0.0));
        Frames(30, Drift(-0.6));
        policy.Direction.Should().Be(DecGuideDirection.South);
        Frames(1, Drift(-0.6));
        policy.Direction.Should().Be(DecGuideDirection.North, "the opposite drift holds when South has not been favoured for 2 minutes");
        policy.Switches.Should().Be(2);
        Notes(DecDirectionNoteKind.Switch).Last().Message.Should().StartWith("the drift reversed, guiding North only");
    }

    [Test]
    public void A_drift_that_comes_and_goes_changes_the_direction_at_most_every_two_minutes()
    {
        // an hour of drifts that last 2–3 minutes each: +0.6, −0.6, 0, +0.6, … px/min
        var rng = new Random(5);
        double[] runs = [0.6, -0.6, 0];
        var changes = new List<(DateTimeOffset Time, DecGuideDirection From, DecGuideDirection To)>();
        for (int run = 0; now < T0.AddHours(1); run++)
        {
            int frames = rng.Next(60, 91);
            for (int i = 0; i < frames; i++)
            {
                var before = policy.Direction;
                Frames(1, Drift(runs[run % runs.Length], 0.1));
                if (policy.Direction != before)
                {
                    changes.Add((now, before, policy.Direction));
                }
            }
        }

        TestContext.Out.WriteLine(string.Join(", ", changes.Select(c => $"{c.To}@{(c.Time - T0).TotalMinutes:F1}")));
        changes.Should().HaveCountGreaterThan(10);
        var previous = T0;
        foreach (var (time, from, to) in changes)
        {
            // a direction is left once the drift has not favoured it for two minutes, one is picked once a drift has held
            // for a minute: never sooner after the previous change
            (time - previous).Should().BeGreaterThanOrEqualTo(from == DecGuideDirection.Both ? DecDirectionPolicy.HoldFor : DecDirectionPolicy.SwitchAfter,
                $"{from} -> {to} at {(time - T0).TotalMinutes:F1} min");
            previous = time;
        }

        changes.Should().Contain(c => c.From == DecGuideDirection.South && c.To == DecGuideDirection.North, "a drift that reverses and holds");
        changes.Should().Contain(c => c.From != DecGuideDirection.Both && c.To == DecGuideDirection.Both, "a drift that fades");
    }

    [Test]
    public void Observing_in_another_mode_keeps_how_long_the_drift_has_held()
    {
        // guided in Auto, the drift held for a minute: choosing Drift picks the direction on its first frame
        for (int i = 0; i < HoldFrames + 1; i++, now = now.AddSeconds(2))
        {
            policy.Observe(now, Drift(0.6));
        }

        policy.Direction.Should().Be(DecGuideDirection.Both, "observing never picks");
        policy.TakeNotes().Should().BeEmpty();
        policy.Reset(keepHold: true);
        Frames(1, Drift(0.6));
        policy.Direction.Should().Be(DecGuideDirection.South);

        // a full reset (guiding started, a meridian flip) forgets it
        policy.Reset();
        Frames(1, Drift(0.6));
        policy.Direction.Should().Be(DecGuideDirection.Both);
    }

    [Test]
    public void Safety_valve_opens_after_ten_frames_far_on_the_uncorrectable_side_and_closes_when_back()
    {
        Pick(0.6);
        Frames(50, Drift(0.6), offsetPx: 0.1);
        policy.ValveLimitPx.Should().Be(DecDirectionPolicy.ValveMinPx, "3 × 0.1 px RMS is below the minimum");

        Frames(9, Drift(0.6), offsetPx: -1.6);
        policy.ValveOpen.Should().BeFalse("9 frames");
        Frames(1, Drift(0.6), offsetPx: 0.2);
        Frames(9, Drift(0.6), offsetPx: -1.6);
        policy.ValveOpen.Should().BeFalse("the count starts over when the error comes back");
        Frames(1, Drift(0.6), offsetPx: -1.6);
        policy.ValveOpen.Should().BeTrue();
        policy.Allowed.Should().BeNull("both directions while the valve is open");
        policy.Direction.Should().Be(DecGuideDirection.South, "the direction itself stays");
        policy.ValveOpenings.Should().Be(1);
        Notes(DecDirectionNoteKind.ValveOpened).Should().ContainSingle().Which.Message.Should().Contain("North side");

        Frames(5, Drift(0.6), offsetPx: -0.4);
        policy.ValveOpen.Should().BeTrue("not back at the lock position yet");
        Frames(1, Drift(0.6), offsetPx: 0.05);
        policy.ValveOpen.Should().BeFalse();
        policy.Allowed.Should().Be(GuideDirection.South);
        Notes(DecDirectionNoteKind.ValveClosed).Should().ContainSingle();
    }

    [Test]
    public void The_valve_limit_follows_the_rms_of_the_correctable_side_only()
    {
        Frames(HoldFrames + 1, Drift(-0.6), offsetPx: -0.8);
        policy.Direction.Should().Be(DecGuideDirection.North, "North corrects negative offsets: positive ones wait for the drift");
        Frames(100, Drift(-0.6), offsetPx: -0.8);
        policy.ValveLimitPx.Should().BeApproximately(2.4, 0.05);

        // an error creeping away on the uncorrectable side does not raise its own limit
        for (int i = 0; i < 200; i++)
        {
            Frames(1, Drift(-0.6), offsetPx: 0.02 * i);
        }

        policy.ValveLimitPx.Should().BeApproximately(2.4, 0.05);
        policy.ValveOpenings.Should().Be(1, "it crept beyond 2.4 px for 10 frames");
    }

    [Test]
    public void The_valve_waits_while_settling()
    {
        Pick(0.6);
        Frames(30, Drift(0.6), offsetPx: -3, settling: true);
        policy.ValveOpen.Should().BeFalse("the guider guides both ways while settling anyway");
        Frames(10, Drift(0.6), offsetPx: -3);
        policy.ValveOpen.Should().BeTrue();
    }

    [Test]
    public void Reset_starts_over_with_both_directions()
    {
        Pick(0.6);
        Frames(20, Drift(0.6), offsetPx: -3);
        policy.Reset();
        policy.Direction.Should().Be(DecGuideDirection.Both);
        policy.ValveOpen.Should().BeFalse();
        policy.Switches.Should().Be(0);
        policy.ValveOpenings.Should().Be(0);
        policy.Faded.Should().BeFalse();
        policy.RecentRmsPx.Should().Be(0);
    }

    [Test]
    public void A_summary_every_hundred_frames()
    {
        Frames(200, Drift(0.6));
        var summaries = Notes(DecDirectionNoteKind.Summary);
        summaries.Should().HaveCount(2);
        summaries[0].Message.Should().Contain("South only").And.Contain("1 switch,").And.Contain("valve closed");
    }
}
