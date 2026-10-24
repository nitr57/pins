// SPDX-License-Identifier: MPL-2.0

using FluentAssertions;
using NUnit.Framework;
using NINA.GuideEngine.Core;
using NINA.GuideEngine.Guiding;
using NINA.GuideEngine.Incidents;

namespace NINA.GuideEngine.Test.Incidents;

[TestFixture]
public class IncidentDiagnoserTests
{
    // ---- camera ----

    [Test]
    public void Camera_failure_incident_is_camera()
    {
        var b = new IncidentBuilder().Add(30);
        b.Trigger(IncidentKind.CameraFailure, GuideErrorCode.CameraCaptureFailed)
            .Trigger(IncidentKind.CameraFailure, GuideErrorCode.CameraCaptureFailed)
            .Trigger(IncidentKind.CameraFailure, GuideErrorCode.CameraReconnecting);

        var d = Diagnose(b);

        d.Cause.Should().Be(IncidentCause.Camera);
        P(Evidence(d, "cameraFailures"), "count").Should().Be(3);
        d.Message.Should().Be("Likely a camera problem: 3 capture failures.");
    }

    [Test]
    public void Frames_missing_around_the_trigger_are_a_camera_problem()
    {
        var b = new IncidentBuilder().Add(30).Add(1, f => f.TimeSeconds += 24).Add(5);
        b.Trigger(IncidentKind.StarLost, index: 30);

        var d = Diagnose(b);

        d.Cause.Should().Be(IncidentCause.Camera);
        var gap = Evidence(d, "frameGap");
        P(gap, "seconds").Should().BeApproximately(26, 0.1);
        gap.Frame.Should().Be(IncidentBuilder.FrameNumber(30));
    }

    [Test]
    public void Frames_dropped_by_the_recorder_are_not_a_camera_problem()
    {
        var b = new IncidentBuilder().Add(30).Add(1, f => f.TimeSeconds += 24).Add(5);
        b.Marker(IncidentMarkerType.Gap, 29).Trigger(IncidentKind.Manual, index: 30);

        Diagnose(b).Cause.Should().NotBe(IncidentCause.Camera);
    }

    // ---- mount moved ----

    [Test]
    public void Slewing_mount_is_mount_moved()
    {
        var b = new IncidentBuilder().Add(30).Add(3, f => f.Mount = IncidentBuilder.DefaultMount with { IsSlewing = true });
        b.Trigger(IncidentKind.MountPaused, GuideErrorCode.MountSlewing, index: 30);

        var d = Diagnose(b);

        d.Cause.Should().Be(IncidentCause.MountMoved);
        d.Evidence[0].Code.Should().Be("mountSlewing");
        d.Message.Should().Be("Likely the mount moved: it was slewing.");
    }

    [Test]
    public void Pier_side_change_is_mount_moved()
    {
        var b = new IncidentBuilder().Add(20).Add(1, f => f.Lost = true)
            .Add(10, f => f.Mount = IncidentBuilder.DefaultMount with { PierSide = PierSide.East });
        b.Trigger(IncidentKind.StarLost, index: 20);

        var d = Diagnose(b);

        d.Cause.Should().Be(IncidentCause.MountMoved);
        var e = Evidence(d, "pierSideChanged");
        e.Parameters["from"].Should().Be("West");
        e.Parameters["to"].Should().Be("East");
        e.Frame.Should().Be(IncidentBuilder.FrameNumber(21));
    }

    [Test]
    public void Mount_alert_without_snapshots_is_mount_moved()
    {
        var b = new IncidentBuilder().Add(20, f => f.Mount = null);
        b.Trigger(IncidentKind.MountPaused, GuideErrorCode.MountTrackingOff);

        var d = Diagnose(b);

        d.Cause.Should().Be(IncidentCause.MountMoved);
        d.Evidence[0].Code.Should().Be("mountTrackingOff");
    }

    [Test]
    public void Parking_long_after_the_trigger_is_not_the_cause()
    {
        var b = new IncidentBuilder().Add(20).Add(10, f => f.Lost = true).Add(3, f => f.Mount = IncidentBuilder.DefaultMount with { IsParked = true });
        b.Trigger(IncidentKind.StarLost, index: 20);

        Diagnose(b).Cause.Should().NotBe(IncidentCause.MountMoved);
    }

    // ---- calibration mismatch ----

    [Test]
    public void Runaway_is_a_calibration_mismatch_on_its_axis()
    {
        var b = new IncidentBuilder().Add(30);
        b.Trigger(IncidentKind.Runaway, GuideErrorCode.RunawayDetected, detail: "Dec axis");

        var d = Diagnose(b);

        d.Cause.Should().Be(IncidentCause.CalibrationMismatch);
        d.Evidence[0].Code.Should().Be("runaway");
        d.Parameters["axis"].Should().Be("dec");
    }

    [Test]
    public void Dec_flip_correction_is_a_calibration_mismatch()
    {
        var b = new IncidentBuilder().Add(30);
        b.Trigger(IncidentKind.DecFlipCorrected, GuideErrorCode.DecFlipCorrected);

        var d = Diagnose(b);

        d.Cause.Should().Be(IncidentCause.CalibrationMismatch);
        d.Evidence[0].Code.Should().Be("decFlipCorrected");
    }

    [Test]
    public void Error_growing_against_the_corrections_is_a_calibration_mismatch()
    {
        // wrong-sign RA corrections: each West pulse (0.7 × error) pushes the star further out
        double[] errors = [0.8, 1.3, 2.1, 3.4, 5.5];
        var b = new IncidentBuilder().Add(30).Add(errors.Length, (f, i) =>
        {
            f.ShiftX = errors[i];
            f.Ra(GuideDirection.West, (int)(0.7 * errors[i] / 0.005));
        });
        b.Trigger(IncidentKind.Spike);

        var d = Diagnose(b);

        d.Cause.Should().Be(IncidentCause.CalibrationMismatch);
        var e = Evidence(d, "errorGrew");
        P(e, "frames").Should().Be(4);
        P(e, "fromPx").Should().BeApproximately(0.8, 0.2);
        P(e, "toPx").Should().BeApproximately(5.5, 0.2);
        e.Parameters["axis"].Should().Be("ra");
    }

    [Test]
    public void Drift_the_corrections_keep_up_with_is_not_a_calibration_mismatch()
    {
        // a sudden drift of 2 px/frame met by correct 0.5 × error pulses: the error grows ever slower
        double e = 0.2;
        var b = new IncidentBuilder().Add(30).Add(8, (f, _) =>
        {
            f.ShiftX = e;
            f.Ra(GuideDirection.West, (int)(0.5 * e / 0.005));
            e = e - (0.5 * e) + 2;
        });
        b.Trigger(IncidentKind.Spike);

        Diagnose(b).Cause.Should().NotBe(IncidentCause.CalibrationMismatch);
    }

    // ---- mount not moving ----

    [Test]
    public void Mount_not_responding_with_pulses_that_moved_nothing()
    {
        // the step to 5 px would also be a field jump: mount not moving comes first
        var b = new IncidentBuilder().Add(30).Add(4, (f, i) =>
        {
            f.ShiftX = 5;
            if (i < 3)
            {
                f.Ra(GuideDirection.West, 1000);
            }
        });
        b.Trigger(IncidentKind.MountNotResponding, GuideErrorCode.MountNotResponding);

        var d = Diagnose(b);

        d.Cause.Should().Be(IncidentCause.MountNotMoving);
        var e = Evidence(d, "pulsesWithoutMotion");
        P(e, "pulses").Should().Be(3);
        P(e, "expectedPx").Should().BeApproximately(15, 0.1);
        P(e, "movedPx").Should().BeApproximately(0, 0.6);
        d.Message.Should().MatchRegex(@"^Likely the mount is not moving: 3 RA pulses should have moved the star 15 px, it moved [\d.]+ px\.$");
        e.Parameters["axis"].Should().Be("ra");
        e.Frame.Should().Be(IncidentBuilder.FrameNumber(30));
    }

    [Test]
    public void Mount_not_responding_alert_alone_is_mount_not_moving()
    {
        var b = new IncidentBuilder().Add(10);
        b.Trigger(IncidentKind.MountNotResponding, GuideErrorCode.MountNotResponding);

        var d = Diagnose(b);

        d.Cause.Should().Be(IncidentCause.MountNotMoving);
        d.Parameters["axis"].Should().Be("ra");
        d.Parameters["pulses"].Should().BeNull();
    }

    [Test]
    public void Calibration_steps_that_move_nothing_without_a_calibration_are_mount_not_moving()
    {
        // first calibration: no rates and angles yet; the step motion comes from the mount's guide rate
        var b = new IncidentBuilder { Context = new IncidentContext { SearchRegionPx = 15 } }
            .Add(8, (f, i) => f.Calibrate("West", i + 1).Ra(GuideDirection.West, 800));
        b.Trigger(IncidentKind.CalibrationFailed, GuideErrorCode.CalibrationFailedRaNoMove);

        var d = Diagnose(b);

        d.Cause.Should().Be(IncidentCause.MountNotMoving);
        var e = Evidence(d, "pulsesWithoutMotion");
        P(e, "pulses").Should().Be(7);
        P(e, "expectedPx").Should().BeApproximately(7 * 800 * 0.5 * 15.041 * Math.Cos(20 * Math.PI / 180) / 1.5 / 1000, 0.2);
        P(e, "movedPx").Should().BeLessThan(1);
        e.Parameters["axis"].Should().Be("ra");
    }

    [Test]
    public void Calibration_steps_that_move_the_star_are_not_mount_not_moving()
    {
        var b = new IncidentBuilder { Context = new IncidentContext { SearchRegionPx = 15 } }
            .Add(8, (f, i) => f.Calibrate("West", i + 1).Ra(GuideDirection.West, 800).ShiftX = -3.7 * i);
        b.Trigger(IncidentKind.CalibrationFailed, GuideErrorCode.CalibrationFailedStarLost);

        Diagnose(b).Cause.Should().NotBe(IncidentCause.MountNotMoving);
    }

    [Test]
    public void Large_pulses_that_bring_the_star_back_are_not_mount_not_moving()
    {
        // e.g. after the star was found again far from the lock
        double[] errors = [20, 15.5, 11, 6.5, 2, 0.5];
        var b = new IncidentBuilder().Add(errors.Length, (f, i) =>
        {
            f.ShiftX = errors[i];
            f.Ra(GuideDirection.West, errors[i] > 3 ? 1000 : 0);
        }).Add(20);
        b.Trigger(IncidentKind.Manual);

        Diagnose(b).Cause.Should().Be(IncidentCause.Unclear);
    }

    [Test]
    public void Dec_pulses_absorbed_by_backlash_after_reversals_are_not_mount_not_moving()
    {
        var b = new IncidentBuilder().Add(20, f => f.ShiftY = 4).Add(6, (f, i) => f.Dec(i % 2 == 0 ? GuideDirection.North : GuideDirection.South, 1000));
        b.Trigger(IncidentKind.Manual);

        Diagnose(b).Cause.Should().Be(IncidentCause.Unclear);
    }

    [Test]
    public void Small_pulses_are_not_judged()
    {
        var b = new IncidentBuilder().Add(20).Add(8, f => f.Ra(GuideDirection.West, 400));
        b.Trigger(IncidentKind.Manual);

        Diagnose(b).Cause.Should().NotBe(IncidentCause.MountNotMoving);
    }

    // ---- field jump ----

    [Test]
    public void Bump_moving_all_stars_together_is_a_field_jump()
    {
        var b = new IncidentBuilder().Add(30).Add(5, f => (f.ShiftX, f.ShiftY) = (6, -4));
        b.Trigger(IncidentKind.Spike, index: 30);

        var d = Diagnose(b);

        d.Cause.Should().Be(IncidentCause.FieldJump);
        var e = Evidence(d, "fieldJump");
        P(e, "jumpPx").Should().BeApproximately(7.2, 0.4);
        P(e, "jumpArcsec").Should().BeApproximately(10.8, 0.6);
        P(e, "stars").Should().Be(5);
        e.Frame.Should().Be(IncidentBuilder.FrameNumber(30));
        d.Message.Should().StartWith("Likely a field jump");
    }

    [Test]
    public void Jump_beyond_the_search_region_found_again_is_a_field_jump_not_clouds()
    {
        // the star leaves its search box: lost for 2 frames (nothing measured there), then found at the new place
        var b = new IncidentBuilder().Add(30).Add(2, f =>
        {
            f.Lost = true;
            f.LostStatus = "Star lost - low mass";
            f.PrimaryFactor = 0;
        }).Add(10, f => (f.ShiftX, f.ShiftY) = (14, 10));
        b.Trigger(IncidentKind.StarLost, index: 30);

        var d = Diagnose(b);

        d.Cause.Should().Be(IncidentCause.FieldJump);
        P(Evidence(d, "fieldJump"), "jumpPx").Should().BeApproximately(17.2, 0.5);
    }

    [Test]
    public void Jump_with_the_secondaries_measured_only_after_the_corrections_is_a_field_jump()
    {
        // like PHD2 after a large primary move (the simulator's Bump): on the jump frame the secondaries are listed unmeasured
        // at their old places, on the next one not at all; measured again, they are where the corrections pulled the field
        var b = new IncidentBuilder().Add(30)
            .Add(1, f => (f.ShiftX, f.ShiftY, f.SecondariesStale) = (13.3, 6.4, true)).Also(f => f.Ra(GuideDirection.West, 1800).Dec(GuideDirection.South, 900))
            .Add(1, f => (f.ShiftX, f.ShiftY, f.NoSecondaries) = (4.3, 1.9, true)).Also(f => f.Ra(GuideDirection.West, 700).Dec(GuideDirection.South, 300))
            .Add(5, f => (f.ShiftX, f.ShiftY) = (0.8, 0.4));
        b.Trigger(IncidentKind.Spike, index: 30);

        var d = Diagnose(b);

        d.Cause.Should().Be(IncidentCause.FieldJump);
        var e = Evidence(d, "fieldJump");
        P(e, "jumpPx").Should().BeApproximately(14.8, 0.4);
        P(e, "stars").Should().Be(5);
        e.Frame.Should().Be(IncidentBuilder.FrameNumber(30));
    }

    [Test]
    public void Jump_that_loses_the_star_for_long_is_a_field_jump()
    {
        // like the real app's bump during an exposure: the smeared primary is lost (mass changed) while the dropout search
        // measures the secondaries at their new places; the jump filter keeps rejecting the star until the reacquire finds it
        // again at the same offset, where the corrections pull it back
        var b = new IncidentBuilder().Add(30)
            .Add(1, f => (f.ShiftX, f.ShiftY, f.Lost, f.LostStatus, f.PrimaryFactor, f.PrimaryMassFactor, f.FallbackRejected) =
                (13.4, 6.5, true, "MassChange: Star lost - mass changed", 0.7, 0.5, true))
            .Add(4, f => (f.Lost, f.LostStatus) = (true, "Error: Recovering"))
            .Add(1, f => (f.ShiftX, f.ShiftY) = (13.8, 6.6)).Also(f => f.Ra(GuideDirection.West, 2000, limited: true).Dec(GuideDirection.South, 1300))
            .Add(1, f => (f.ShiftX, f.ShiftY, f.SecondariesStale) = (3.8, 0.1, true)).Also(f => f.Ra(GuideDirection.West, 760))
            .Add(20, f => (f.ShiftX, f.ShiftY) = (0, 0));
        b.Trigger(IncidentKind.StarLost, GuideErrorCode.StarLost, index: 30).Trigger(IncidentKind.Spike, index: 35);

        var d = Diagnose(b);

        d.Cause.Should().Be(IncidentCause.FieldJump);
        var e = Evidence(d, "fieldJump");
        P(e, "jumpPx").Should().BeApproximately(15.3, 0.4);
        P(e, "stars").Should().Be(5);
        e.Frame.Should().Be(IncidentBuilder.FrameNumber(30), "the frame where the star was lost");
        d.Message.Should().EndWith("the guide star was lost for 10 s.");
    }

    [Test]
    public void Star_lost_for_long_and_found_after_a_drift_is_not_a_field_jump()
    {
        // clouds: on the loss frame the secondaries are still where they were; the field drifted while nothing was corrected
        var b = new IncidentBuilder().Add(30)
            .Add(1, f => (f.Lost, f.Transparency, f.PrimaryFactor, f.FallbackRejected) = (true, 0.3, 0.3, true))
            .Add(5, (f, i) => (f.Lost, f.Transparency, f.ShiftX, f.ShiftY) = (true, 0.1, 0.7 * (i + 1), 0.35 * (i + 1)))
            .Add(20, f => (f.ShiftX, f.ShiftY) = (4.2, 2.1));
        b.Trigger(IncidentKind.StarLost, GuideErrorCode.StarLost, index: 30);

        Diagnose(b).Cause.Should().NotBe(IncidentCause.FieldJump);
    }

    [Test]
    public void Star_lost_at_once_and_found_where_the_drift_took_it_is_not_a_field_jump()
    {
        // a thick cloud: the stars are gone at once (nothing measured while lost); the corrections before it show a drift of
        // 0.35 px/s, which explains where the star is found again 14 s later
        var b = new IncidentBuilder().Add(30, f => f.Ra(GuideDirection.West, 140))
            .Add(6, f => (f.Lost, f.PrimaryFactor) = (true, 0.05))
            .Add(1, f => (f.ShiftX, f.SecondariesStale) = (4.9, true)).Also(f => f.Ra(GuideDirection.West, 980))
            .Add(20, f => f.ShiftX = 0);
        b.Trigger(IncidentKind.StarLost, GuideErrorCode.StarLost, index: 30);

        Diagnose(b).Cause.Should().NotBe(IncidentCause.FieldJump);
    }

    [Test]
    public void Primary_jumping_alone_while_the_secondaries_were_not_measured_is_not_a_field_jump()
    {
        // the primary jumped to a neighbour star: the corrections moved the field away by the jump
        var b = new IncidentBuilder().Add(30)
            .Add(1, f => (f.PrimaryOffsetX, f.PrimaryOffsetY, f.SecondariesStale) = (13.3, 6.4, true))
            .Also(f => f.Ra(GuideDirection.West, 1800).Dec(GuideDirection.South, 900))
            .Add(1, f => (f.ShiftX, f.ShiftY, f.PrimaryOffsetX, f.PrimaryOffsetY, f.NoSecondaries) = (-9, -4.5, 13.3, 6.4, true))
            .Also(f => f.Ra(GuideDirection.West, 700).Dec(GuideDirection.South, 300))
            .Add(5, f => (f.ShiftX, f.ShiftY, f.PrimaryOffsetX, f.PrimaryOffsetY) = (-12.5, -6, 13.3, 6.4));
        b.Trigger(IncidentKind.Spike, index: 30);

        Diagnose(b).Cause.Should().NotBe(IncidentCause.FieldJump);
    }

    [Test]
    public void Jump_of_a_primary_whose_brightness_changed_is_the_guide_star_only()
    {
        var b = new IncidentBuilder().Add(30)
            .Add(1, f => (f.ShiftX, f.ShiftY, f.SecondariesStale, f.PrimaryFactor, f.PrimaryMassFactor) = (13.3, 6.4, true, 2.5, 3))
            .Also(f => f.Ra(GuideDirection.West, 1800).Dec(GuideDirection.South, 900))
            .Add(1, f => (f.ShiftX, f.ShiftY, f.NoSecondaries) = (4.3, 1.9, true)).Also(f => f.Ra(GuideDirection.West, 700).Dec(GuideDirection.South, 300))
            .Add(5, f => (f.ShiftX, f.ShiftY) = (0.8, 0.4));
        b.Trigger(IncidentKind.Spike, index: 30);

        var d = Diagnose(b);

        d.Cause.Should().Be(IncidentCause.GuideStarOnly);
        d.Evidence.Should().Contain(e => e.Code == "massJump");
    }

    [Test]
    public void Dither_is_not_a_field_jump()
    {
        var b = new IncidentBuilder().Add(30)
            .Add(1, f => (f.Dithering, f.LockX, f.ShiftX) = (true, 6, 6))
            .Add(5, f => f.Settling = true)
            .Add(10);
        b.Trigger(IncidentKind.SettleTimeout, GuideErrorCode.SettleTimeout);

        Diagnose(b).Cause.Should().NotBe(IncidentCause.FieldJump);
    }

    [Test]
    public void Move_explained_by_a_large_pulse_is_not_a_field_jump()
    {
        var b = new IncidentBuilder().Add(30).Add(1, f => f.Ra(GuideDirection.West, 1500)).Add(5, f => f.ShiftX = -7.5);
        b.Trigger(IncidentKind.Manual);

        Diagnose(b).Cause.Should().NotBe(IncidentCause.FieldJump);
    }

    [Test]
    public void Primary_jumping_alone_is_not_a_field_jump()
    {
        var b = new IncidentBuilder().Add(30).Add(1, f => (f.PrimaryOffsetX, f.PrimaryOffsetY) = (5, 3)).Add(5);
        b.Trigger(IncidentKind.Spike, index: 30);

        Diagnose(b).Cause.Should().NotBe(IncidentCause.FieldJump);
    }

    // ---- clouds ----

    [Test]
    public void Stars_fading_together_and_coming_back_are_clouds()
    {
        var b = Clouds(fadeFrames: 10, lostFrames: 30, recover: true);

        var d = Diagnose(b);

        d.Cause.Should().Be(IncidentCause.Clouds);
        var faded = Evidence(d, "starsFaded");
        P(faded, "stars").Should().Be(5);
        P(faded, "dropPercent").Should().BeGreaterThanOrEqualTo(75);
        P(faded, "seconds").Should().BeInRange(8, 24);
        P(Evidence(d, "recoveredAfter"), "seconds").Should().BeInRange(55, 90);
        d.Message.Should().MatchRegex(@"^Likely clouds: all 5 stars faded by \d+ % within [\d.]+ s and came back after [\d.]+ s\.$");
    }

    [Test]
    public void Stars_lost_to_clouds_for_good_are_clouds()
    {
        var b = Clouds(fadeFrames: 10, lostFrames: 30, recover: false);
        b.EndReason = IncidentEndReason.Stopped;

        var d = Diagnose(b);

        d.Cause.Should().Be(IncidentCause.Clouds);
        d.Evidence.Should().NotContain(e => e.Code == "recoveredAfter");
        d.Message.Should().EndWith("and were lost.");
    }

    [Test]
    public void One_faded_secondary_is_not_clouds()
    {
        var b = new IncidentBuilder().Add(30).Add(10, (f, i) => f.SecondaryFactors = [1, 1 - (0.09 * (i + 1)), 1, 1]).Add(20, f => f.SecondaryFactors = [1, 0.1, 1, 1]);
        b.Trigger(IncidentKind.Manual, index: 40);

        var d = Diagnose(b);

        d.Cause.Should().NotBe(IncidentCause.Clouds);
        d.Cause.Should().Be(IncidentCause.Unclear);
    }

    [Test]
    public void Clouds_fading_in_30_s_beat_dew()
    {
        // the stars also swell (HFD +40 %), but the fade is fast
        var b = new IncidentBuilder().Add(40)
            .Add(15, (f, i) => (f.Transparency, f.HfdFactor) = (1 - (0.06 * (i + 1)), 1 + (0.4 * (i + 1) / 15)))
            .Add(15, f => (f.Transparency, f.HfdFactor) = (0.1, 1.4))
            .Add(20);
        b.Trigger(IncidentKind.Spike, index: 56);

        Diagnose(b).Cause.Should().Be(IncidentCause.Clouds);
    }

    // ---- dew ----

    [Test]
    public void Slow_fade_with_growing_stars_is_dew()
    {
        var b = new IncidentBuilder().Add(100, (f, i) => (f.Transparency, f.HfdFactor) = (1 - (0.5 * i / 99), 1 + (0.45 * i / 99)));
        b.Trigger(IncidentKind.Spike);

        var d = Diagnose(b);

        d.Cause.Should().Be(IncidentCause.Dew);
        var e = Evidence(d, "starsFadingSlowly");
        P(e, "stars").Should().Be(5);
        P(e, "dropPercent").Should().BeInRange(35, 60);
        P(e, "minutes").Should().BeInRange(2.9, 3.3);
        P(Evidence(d, "hfdGrew"), "percent").Should().BeInRange(35, 50);
    }

    [Test]
    public void Slow_fade_without_growing_stars_is_not_dew()
    {
        var b = new IncidentBuilder().Add(100, (f, i) => f.Transparency = 1 - (0.5 * i / 99));
        b.Trigger(IncidentKind.Spike);

        Diagnose(b).Cause.Should().Be(IncidentCause.Unclear);
    }

    // ---- guide star only ----

    [Test]
    public void Primary_lost_while_the_secondaries_carry_on_is_the_guide_star_only()
    {
        // the tracker estimated the primary from the secondaries, which stayed as bright as before
        var b = new IncidentBuilder().Add(30).Add(5, f => f.Estimated = true);
        b.Trigger(IncidentKind.StarLost, index: 30);

        var d = Diagnose(b);

        d.Cause.Should().Be(IncidentCause.GuideStarOnly);
        var e = Evidence(d, "primaryOnly");
        P(e, "dropPercent").Should().Be(100);
        P(e, "secondaries").Should().Be(4);
    }

    [Test]
    public void Saturated_primary_lost_is_the_guide_star_only()
    {
        var b = new IncidentBuilder().Add(30).Add(3, f => (f.Lost, f.LostStatus, f.PrimaryFactor) = (true, "Star lost - saturated", 1.5));
        b.Trigger(IncidentKind.StarLost, index: 30);

        var d = Diagnose(b);

        d.Cause.Should().Be(IncidentCause.GuideStarOnly);
        d.Evidence.Should().Contain(e => e.Code == "saturated");
        d.Message.Should().Contain("saturated");
    }

    [Test]
    public void Hot_pixel_on_the_primary_is_a_mass_jump_of_the_guide_star_only()
    {
        var b = new IncidentBuilder().Add(30).Add(1, f => (f.PrimaryFactor, f.PrimaryMassFactor, f.PrimaryOffsetX) = (2.5, 3, 1)).Add(5);
        b.Trigger(IncidentKind.Spike, index: 30);

        var d = Diagnose(b);

        d.Cause.Should().Be(IncidentCause.GuideStarOnly);
        P(Evidence(d, "massJump"), "percent").Should().BeApproximately(200, 20);
        P(Evidence(d, "primaryOnly"), "secondaries").Should().Be(4);
    }

    [Test]
    public void Primary_lost_with_nothing_known_about_the_secondaries_is_not_the_guide_star_only()
    {
        var b = new IncidentBuilder().Add(30).Add(5, f => (f.Lost, f.PrimaryFactor) = (true, 0.05));
        b.Trigger(IncidentKind.StarLost, index: 30);

        var d = Diagnose(b);

        d.Cause.Should().Be(IncidentCause.Unclear);
        Evidence(d, "starLost").Parameters["status"].Should().Be("Star lost - low SNR");
    }

    // ---- drift too fast ----

    [Test]
    public void Drift_outrunning_limited_pulses_is_drift_too_fast()
    {
        var b = new IncidentBuilder { Context = IncidentBuilder.Calibrated with { MaxRaDurationMs = 500 } }
            .Add(30).Add(8, (f, i) => f.Ra(GuideDirection.West, 500, limited: true).ShiftX = 1 + (1.5 * i));
        b.Trigger(IncidentKind.PulseLimited, GuideErrorCode.PulseLimitReached);

        var d = Diagnose(b);

        d.Cause.Should().Be(IncidentCause.DriftTooFast);
        var e = Evidence(d, "driftToEdge");
        P(e, "percentOfRegion").Should().BeApproximately(77, 3);
        P(e, "frames").Should().Be(8);
        e.Parameters["axis"].Should().Be("ra");
        P(Evidence(d, "pulsesLimited"), "frames").Should().Be(8);
    }

    [Test]
    public void Slow_calm_drift_is_not_drift_too_fast()
    {
        // far out, but the guider was not at its limit
        var slow = new IncidentBuilder().Add(60, (f, i) => f.Ra(GuideDirection.West, 100).ShiftX = 0.2 + (0.18 * i));
        slow.Trigger(IncidentKind.Spike);
        Diagnose(slow).Cause.Should().NotBe(IncidentCause.DriftTooFast);

        // at the limit, but not near the edge of the search region
        var near = new IncidentBuilder { Context = IncidentBuilder.Calibrated with { MaxRaDurationMs = 500 } }
            .Add(30).Add(8, (f, i) => f.Ra(GuideDirection.West, 500, limited: true).ShiftX = 1 + (0.6 * i));
        near.Trigger(IncidentKind.PulseLimited, GuideErrorCode.PulseLimitReached);
        Diagnose(near).Cause.Should().NotBe(IncidentCause.DriftTooFast);
    }

    // ---- periodic spike ----

    [Test]
    public void Spike_repeating_with_the_worm_period_is_periodic()
    {
        var b = PeriodicSpikeIncident(480);

        var d = Diagnose(b);

        d.Cause.Should().Be(IncidentCause.PeriodicSpike);
        P(Evidence(d, "spikeRepeats"), "count").Should().Be(2);
        P(Evidence(d, "spikeRepeats"), "periodSeconds").Should().Be(480);
        P(Evidence(d, "spike"), "errorArcsec").Should().BeApproximately(3, 0.3);
    }

    [Test]
    public void Spike_without_a_worm_period_is_unclear()
    {
        var b = PeriodicSpikeIncident(null);

        var d = Diagnose(b);

        d.Cause.Should().Be(IncidentCause.Unclear);
        d.Evidence[0].Code.Should().Be("spike");
        P(d, "errorArcsec").Should().BeApproximately(3, 0.3);
        P(d, "rmsArcsec").Should().BeLessThan(0.5);
        d.Evidence.Should().Contain(e => e.Code == "trigger" && (string?)e.Parameters["kind"] == "Spike");
    }

    [Test]
    public void Spikes_off_the_worm_period_are_not_periodic()
    {
        var b = PeriodicSpikeIncident(480);
        var spike = b.TimeOf(30);
        b.Context = b.Context with { EarlierSpikes = [spike.AddSeconds(-400), spike.AddSeconds(-700)] };

        Diagnose(b).Cause.Should().Be(IncidentCause.Unclear);
    }

    // ---- priority ----

    [Test]
    public void Camera_beats_mount_moved()
    {
        var b = new IncidentBuilder().Add(20).Add(3, f => f.Mount = IncidentBuilder.DefaultMount with { IsSlewing = true });
        b.Trigger(IncidentKind.CameraFailure, GuideErrorCode.CameraFailed, index: 20);

        Diagnose(b).Cause.Should().Be(IncidentCause.Camera);
    }

    [Test]
    public void Mount_moved_beats_field_jump()
    {
        var b = new IncidentBuilder().Add(30).Add(5, f =>
        {
            (f.ShiftX, f.ShiftY) = (6, -4);
            f.Mount = IncidentBuilder.DefaultMount with { IsSlewing = true };
        });
        b.Trigger(IncidentKind.Spike, index: 30);

        Diagnose(b).Cause.Should().Be(IncidentCause.MountMoved);
    }

    [Test]
    public void Runaway_beats_drift_too_fast_and_mount_not_moving()
    {
        var b = new IncidentBuilder().Add(30).Add(8, (f, i) => f.Ra(GuideDirection.West, 2000, limited: true).ShiftX = 1 + (1.5 * i));
        b.Trigger(IncidentKind.Runaway, GuideErrorCode.RunawayDetected, detail: "Ra axis");

        var d = Diagnose(b);

        d.Cause.Should().Be(IncidentCause.CalibrationMismatch);
        d.Parameters["axis"].Should().Be("ra");
    }

    [Test]
    public void Field_jump_beats_periodic_spike()
    {
        var b = PeriodicSpikeIncident(480, spikePx: 6);

        Diagnose(b).Cause.Should().Be(IncidentCause.FieldJump);
    }

    // ---- robustness ----

    [Test]
    public void Empty_incident_is_unclear()
    {
        var d = Diagnose(new IncidentBuilder());

        d.Cause.Should().Be(IncidentCause.Unclear);
        d.Evidence.Should().ContainSingle(e => e.Code == "trigger");
    }

    [Test]
    public void Very_short_incident_is_unclear()
    {
        var b = new IncidentBuilder().Add(1, f => f.Lost = true);
        b.Trigger(IncidentKind.StarLost);

        var d = Diagnose(b);

        d.Cause.Should().Be(IncidentCause.Unclear);
        d.Evidence[0].Code.Should().Be("starLost");
    }

    [Test]
    public void No_calibration_and_no_mount_still_diagnose()
    {
        var b = Clouds(fadeFrames: 10, lostFrames: 20, recover: true);
        b.Context = new IncidentContext();
        b.Mounts(null);

        Diagnose(b).Cause.Should().Be(IncidentCause.Clouds);

        var pulses = new IncidentBuilder { Context = new IncidentContext(), PixelScale = 0 }.Add(10, f => f.Ra(GuideDirection.West, 1500)).Add(5, f => f.ShiftX = 8);
        pulses.Mounts(null);
        pulses.Trigger(IncidentKind.Spike, index: 10);
        Diagnose(pulses).Cause.Should().Be(IncidentCause.Unclear);
    }

    [Test]
    public void Telemetry_only_incident_uses_the_frame_values()
    {
        // no star lists (older or reduced telemetry): the primary's frame values carry the diagnosis
        var b = Clouds(fadeFrames: 10, lostFrames: 20, recover: true);
        b.StarLists = false;

        var d = Diagnose(b);

        d.Cause.Should().Be(IncidentCause.Clouds);
        P(Evidence(d, "starsFaded"), "stars").Should().Be(1);
        d.Message.Should().StartWith("Likely clouds: the guide star faded by");
        Evidence(d, "recoveredAfter").Message.Should().StartWith("The guide star came back");
    }

    [Test]
    public void Missing_lists_and_values_do_not_throw()
    {
        var frames = new IncidentBuilder().Add(20).Build().Frames
            .Select((f, i) => i % 3 == 0 ? f with { Stars = null!, Mount = null, Snr = double.NaN, Lock = null } : f)
            .ToList();
        var incident = new Incident { Id = "x", Frames = frames, Triggers = null!, Markers = null!, Context = null!, Tags = null! };

        var d = IncidentDiagnoser.Diagnose(incident);

        AssertWellFormed(d);
    }

    // ---- helpers ----

    private static IncidentDiagnosis Diagnose(IncidentBuilder b)
    {
        var d = IncidentDiagnoser.Diagnose(b.Build());
        AssertWellFormed(d);
        return d;
    }

    private static void AssertWellFormed(IncidentDiagnosis d)
    {
        d.Message.Should().StartWith("Likely");
        d.Evidence.Should().NotBeEmpty();
        d.Parameters.Should().BeEquivalentTo(d.Evidence[0].Parameters);
        d.Evidence.Should().OnlyContain(e => !string.IsNullOrWhiteSpace(e.Message) && !string.IsNullOrWhiteSpace(e.Code));
    }

    private static IncidentEvidence Evidence(IncidentDiagnosis d, string code) => d.Evidence.Should().ContainSingle(e => e.Code == code).Subject;

    private static double P(IncidentEvidence e, string key) => Convert.ToDouble(e.Parameters[key], System.Globalization.CultureInfo.InvariantCulture);

    private static double P(IncidentDiagnosis d, string key) => Convert.ToDouble(d.Parameters[key], System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>40 calm frames, a fade to 10 % over <paramref name="fadeFrames"/>, the primary lost (trigger), then back.</summary>
    private static IncidentBuilder Clouds(int fadeFrames, int lostFrames, bool recover)
    {
        var b = new IncidentBuilder().Add(40)
            .Add(fadeFrames, (f, i) => f.Transparency = 1 - (0.85 * (i + 1) / fadeFrames))
            .Add(lostFrames, f => (f.Lost, f.Transparency) = (true, 0.1));
        b.Trigger(IncidentKind.StarLost, index: 40 + fadeFrames);
        if (recover)
        {
            b.Add(5, (f, i) => f.Transparency = 0.1 + (0.18 * (i + 1))).Add(20);
        }

        return b;
    }

    /// <summary>30 calm frames, a spike of <paramref name="spikePx"/> px in RA (all stars), 5 frames after.</summary>
    private static IncidentBuilder PeriodicSpikeIncident(double? wormPeriod, double spikePx = 2)
    {
        var b = new IncidentBuilder().Add(30).Add(1, f => f.ShiftX = spikePx).Add(5, f => f.ShiftX = 0);
        b.Trigger(IncidentKind.Spike, index: 30);
        var spike = b.TimeOf(30);
        b.Context = b.Context with
        {
            WormPeriodSeconds = wormPeriod,
            EarlierSpikes = [spike.AddSeconds(-2000), spike.AddSeconds(-480 * 1.02), spike.AddSeconds(-960 * 0.99)],
        };
        return b;
    }
}

/// <summary>One synthetic frame: the star field's position and brightness, what the guider sent and what it flagged.</summary>
internal sealed class FrameSpec
{
    public double TimeSeconds { get; set; }

    public GuiderState State { get; set; } = GuiderState.Guiding;

    /// <summary>Field position relative to the base (the lock), px: the guide error. Carried over to the next frames.</summary>
    public double ShiftX { get; set; }

    public double ShiftY { get; set; }

    /// <summary>Lock position relative to the base, px. Carried over to the next frames.</summary>
    public double LockX { get; set; }

    public double LockY { get; set; }

    /// <summary>Brightness factor of all stars (1 = clear sky).</summary>
    public double Transparency { get; set; } = 1;

    public double HfdFactor { get; set; } = 1;

    /// <summary>Extra SNR factor of the primary (also what the tracker measures in the box of a lost primary).</summary>
    public double PrimaryFactor { get; set; } = 1;

    public double PrimaryMassFactor { get; set; } = 1;

    /// <summary>The primary alone moved (a hot pixel, a neighbour star), px.</summary>
    public double PrimaryOffsetX { get; set; }

    public double PrimaryOffsetY { get; set; }

    public double[]? SecondaryFactors { get; set; }

    /// <summary>The secondaries are listed unmeasured at the previous frame's places (PHD2 after a large primary move).</summary>
    public bool SecondariesStale { get; set; }

    /// <summary>Only the primary is listed.</summary>
    public bool NoSecondaries { get; set; }

    /// <summary>Lost primary: the dropout search measured the secondaries (listed "FallbackRejected") instead of leaving them unmeasured.</summary>
    public bool FallbackRejected { get; set; }

    public bool Lost { get; set; }

    public bool Estimated { get; set; }

    public string? LostStatus { get; set; }

    public int RaMs { get; set; }

    public GuideDirection? RaDirection { get; set; }

    public bool RaLimited { get; set; }

    public int DecMs { get; set; }

    public GuideDirection? DecDirection { get; set; }

    public bool Settling { get; set; }

    public bool Dithering { get; set; }

    public MountSnapshot? Mount { get; set; } = IncidentBuilder.DefaultMount;

    public string? CalibrationDirection { get; set; }

    public int? CalibrationStep { get; set; }

    public FrameSpec Ra(GuideDirection direction, int ms, bool limited = false)
    {
        (RaDirection, RaMs, RaLimited) = (ms > 0 ? direction : (GuideDirection?)null, ms, limited);
        return this;
    }

    public FrameSpec Dec(GuideDirection direction, int ms)
    {
        (DecDirection, DecMs) = (direction, ms);
        return this;
    }

    public FrameSpec Calibrate(string direction, int step)
    {
        (CalibrationDirection, CalibrationStep, State) = (direction, step, GuiderState.Calibrating);
        return this;
    }
}

/// <summary>
/// Builds synthetic incidents: a primary and 4 secondaries whose positions, brightness and HFD follow the frame specs (with a
/// little seeing and SNR noise), telemetry as the recorder writes it (a lost primary keeps its last good values in the star
/// list, the secondaries are not measured then), camera axes = mount axes.
/// </summary>
internal sealed class IncidentBuilder
{
    public static readonly DateTimeOffset T0 = new(2026, 9, 25, 22, 0, 0, TimeSpan.Zero);

    public static readonly MountSnapshot DefaultMount = new()
    {
        IsConnected = true,
        IsTracking = true,
        PierSide = PierSide.West,
        DeclinationDeg = 20,
        RightAscensionHours = 5,
        GuideRateRa = 0.5,
        GuideRateDec = 0.5,
    };

    public static readonly IncidentContext Calibrated = new()
    {
        SearchRegionPx = 15,
        MaxRaDurationMs = 2000,
        MaxDecDurationMs = 2000,
        RaRatePxPerMs = 0.005,
        DecRatePxPerMs = 0.005,
        RaAngleDeg = 0,
        DecAngleDeg = 90,
    };

    private static readonly (double X, double Y, double Snr)[] Field = [(600, 400, 40), (700, 460, 30), (520, 300, 25), (660, 250, 20), (470, 520, 35)];

    private readonly List<FrameSpec> specs = [];
    private readonly List<IncidentTrigger> triggers = [];
    private readonly List<IncidentMarker> markers = [];
    private readonly Random random = new(7);

    public IncidentContext Context { get; set; } = Calibrated;

    public double PixelScale { get; set; } = 1.5;

    public double IntervalSeconds { get; set; } = 2;

    public bool StarLists { get; set; } = true;

    public IncidentEndReason EndReason { get; set; } = IncidentEndReason.Recovered;

    public static long FrameNumber(int index) => 1000 + index;

    public DateTimeOffset TimeOf(int index) => index < 0 ? T0 : T0.AddSeconds(specs[index].TimeSeconds);

    public IncidentBuilder Add(int count, Action<FrameSpec>? configure = null) => Add(count, (f, _) => configure?.Invoke(f));

    public IncidentBuilder Add(int count, Action<FrameSpec, int> configure)
    {
        for (int i = 0; i < count; i++)
        {
            var previous = specs.Count > 0 ? specs[^1] : null;
            var spec = new FrameSpec
            {
                TimeSeconds = previous is null ? 0 : previous.TimeSeconds + IntervalSeconds,
                ShiftX = previous?.ShiftX ?? 0,
                ShiftY = previous?.ShiftY ?? 0,
                LockX = previous?.LockX ?? 0,
                LockY = previous?.LockY ?? 0,
            };
            configure(spec, i);
            specs.Add(spec);
        }

        return this;
    }

    /// <summary>Configures the last frame added once more.</summary>
    public IncidentBuilder Also(Action<FrameSpec> configure)
    {
        configure(specs[^1]);
        return this;
    }

    /// <summary>A trigger at a frame (default: the last one added).</summary>
    public IncidentBuilder Trigger(IncidentKind kind, GuideErrorCode? code = null, string? detail = null, int? index = null)
    {
        int i = index ?? specs.Count - 1;
        triggers.Add(new IncidentTrigger(TimeOf(i), kind, code, kind.ToString(), detail, i < 0 ? null : FrameNumber(i)));
        return this;
    }

    public IncidentBuilder Marker(IncidentMarkerType type, int index)
    {
        markers.Add(new IncidentMarker(TimeOf(index), FrameNumber(index), type, null));
        return this;
    }

    public void Mounts(MountSnapshot? mount)
    {
        foreach (var s in specs)
        {
            s.Mount = mount;
        }
    }

    public Incident Build()
    {
        var frames = specs.Select((s, i) => ToRecord(s, i)).ToList();
        return new Incident
        {
            Id = "test",
            Start = frames.Count > 0 ? frames[0].Time : T0,
            End = frames.Count > 0 ? frames[^1].Time : T0,
            Kind = triggers.Count > 0 ? triggers[0].Kind : IncidentKind.Manual,
            Triggers = triggers,
            Markers = markers,
            EndReason = EndReason,
            Tags = new IncidentTags { PixelScale = PixelScale },
            Context = Context,
            SensorWidth = 1280,
            SensorHeight = 960,
            Frames = frames,
        };
    }

    private double Noise(double amplitude) => ((random.NextDouble() * 2) - 1) * amplitude;

    private IncidentFrameRecord ToRecord(FrameSpec s, int index)
    {
        bool measured = !s.Lost && !s.Estimated;
        bool guiding = s.CalibrationDirection is null;
        double sx = s.ShiftX + Noise(0.1);
        double sy = s.ShiftY + Noise(0.1);
        var (x0, y0, snr0) = Field[0];
        var lockPosition = new GuidePoint(x0 + s.LockX, y0 + s.LockY);
        double px = x0 + sx + s.PrimaryOffsetX;
        double py = y0 + sy + s.PrimaryOffsetY;
        double snr = snr0 * s.Transparency * s.PrimaryFactor * (1 + Noise(0.04));
        double mass = snr0 * 500 * s.Transparency * s.PrimaryMassFactor * (1 + Noise(0.04));
        double hfd = 3 * s.HfdFactor * (1 + Noise(0.03));

        // a lost (or estimated) primary keeps its last good values in the star list
        var stars = new List<StarInfo>
        {
            measured ? new StarInfo(px, py, snr, mass, hfd, true, true, 1, null) : new StarInfo(px, py, snr0, snr0 * 500, 3, true, false, 0, "PrimaryLost"),
        };
        for (int k = 1; k < Field.Length && !s.NoSecondaries; k++)
        {
            var (x, y, snrK) = Field[k];
            double factor = s.Transparency * (s.SecondaryFactors?[k - 1] ?? 1) * (1 + Noise(0.04));
            bool stale = (s.Lost && !s.FallbackRejected) || s.SecondariesStale;
            string? reason = stale ? "NotMeasured" : snrK * factor < 3 ? "Lost" : s.FallbackRejected ? "FallbackRejected" : null;
            var previous = s.SecondariesStale && index > 0 ? specs[index - 1] : s;
            stars.Add(new StarInfo(x + previous.ShiftX + Noise(0.1), y + previous.ShiftY + Noise(0.1), stale ? snrK : snrK * factor, snrK * 500 * factor,
                3 * s.HfdFactor, false, reason is null, reason is null ? 0.25 : 0, reason));
        }

        return new IncidentFrameRecord
        {
            Frame = FrameNumber(index),
            Time = T0.AddSeconds(s.TimeSeconds),
            ExposureMs = 1500,
            State = s.State,
            Settling = s.Settling,
            Dithering = s.Dithering,
            StarFound = !s.Lost,
            PrimaryEstimated = s.Estimated,
            LostStatus = s.Lost ? s.LostStatus ?? "Star lost - low SNR" : null,
            Lock = guiding ? lockPosition : null,
            Star = s.Lost ? null : new GuidePoint(px, py),
            Dx = guiding && !s.Lost ? px - lockPosition.X : null,
            Dy = guiding && !s.Lost ? py - lockPosition.Y : null,
            RaDistanceRaw = guiding && !s.Lost ? px - lockPosition.X : null,
            DecDistanceRaw = guiding && !s.Lost ? py - lockPosition.Y : null,
            RaDurationMs = s.RaMs,
            RaDirection = s.RaDirection,
            RaLimited = s.RaLimited,
            DecDurationMs = s.DecMs,
            DecDirection = s.DecDirection,
            Snr = s.Estimated ? snr0 : snr,
            StarMass = s.Estimated ? snr0 * 500 : mass,
            Hfd = measured ? hfd : null,
            Stars = StarLists ? stars : [],
            Mount = s.Mount,
            CalibrationDirection = s.CalibrationDirection,
            CalibrationStep = s.CalibrationStep,
        };
    }
}
