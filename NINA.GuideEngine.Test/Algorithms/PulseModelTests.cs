// SPDX-License-Identifier: MPL-2.0

using FluentAssertions;
using NUnit.Framework;
using NINA.GuideEngine.Algorithms;
using NINA.GuideEngine.Core;

namespace NINA.GuideEngine.Test.Algorithms;

/// <summary>
/// The pulse model (docs/notes/DEC-PULSE-MODEL.md) learning from dithers on a synthetic mount whose pulse effects and Dec
/// backlash are known. The mount wanders and drifts, the measurement adds seeing, and a proportional controller corrects
/// every frame, so the pulses after a dither also react to the noise, as in real guiding.
/// </summary>
[TestFixture]
public class PulseModelTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 29, 20, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Calibrated = new(2026, 9, 23, 20, 1, 38, TimeSpan.Zero);

    [TestCase(0.0)]
    [TestCase(0.4)]
    [TestCase(1.0)]
    public void Learns_the_effect_of_both_axes_whatever_the_Dec_backlash(double backlashPx)
    {
        var model = new PulseModel();
        model.UseCalibration(Calibrated, null);
        new Mount { RaEffect = 1.14, DecEffect = 0.78, DecBacklashPx = backlashPx }.Guide(model, dithers: 80);

        var v = model.Values;
        TestContext.Out.WriteLine(Describe(v));
        v.Ra!.Effect.Should().BeApproximately(1.14, 2.5 * v.Ra.EffectSigma);
        v.Dec!.Effect.Should().BeApproximately(0.78, 2.5 * v.Dec.EffectSigma);

        // in use: the effect at the upper end of its uncertainty, so the pulses are never made much longer than they should
        v.RaEffect.Should().BeApproximately(v.Ra.Effect + v.Ra.EffectSigma, PulseModel.EffectStep);
        v.DecEffect.Should().BeApproximately(v.Dec.Effect + v.Dec.EffectSigma, PulseModel.EffectStep);
        v.RaEffect.Should().BeGreaterThan(1.14 - PulseModel.EffectStep);
        v.DecEffect.Should().BeGreaterThan(0.78 - PulseModel.EffectStep);

        // the loss at a reversal is at most the backlash: the gear often turns inside it
        v.Dec.ReversalLossPx!.Value.Should().BeLessThan(backlashPx + 2 * v.Dec.ReversalLossSigmaPx!.Value);
    }

    [Test]
    public void Leaves_a_mount_that_moves_as_calibrated_about_alone()
    {
        var model = new PulseModel();
        model.UseCalibration(Calibrated, null);
        new Mount().Guide(model, dithers: 80);

        var v = model.Values;
        TestContext.Out.WriteLine(Describe(v));
        v.Ra!.Effect.Should().BeApproximately(1.0, 0.05);
        v.Dec!.Effect.Should().BeApproximately(1.0, 0.05);
        v.RaEffect.Should().BeInRange(1.0, 1.06);
        v.DecEffect.Should().BeInRange(1.0, 1.06);
    }

    [Test]
    public void Learns_the_same_while_its_values_size_the_pulses()
    {
        // the guider sizes the pulses with the effect in use: the estimate is relative to the calibrated pulses that went
        // out, so it must not drift with its own use
        var model = new PulseModel();
        model.UseCalibration(Calibrated, null);
        new Mount { RaEffect = 1.14, DecEffect = 0.78, DecBacklashPx = 0.4, UseModel = true }.Guide(model, dithers: 120);

        var v = model.Values;
        TestContext.Out.WriteLine(Describe(v));
        v.Ra!.Effect.Should().BeApproximately(1.14, 2.5 * v.Ra.EffectSigma);
        v.Dec!.Effect.Should().BeApproximately(0.78, 2.5 * v.Dec.EffectSigma);
        v.DecEffect.Should().BeGreaterThan(0.78 - PulseModel.EffectStep);
    }

    [Test]
    public void Stays_neutral_until_it_has_enough_dithers()
    {
        var model = new PulseModel();
        model.UseCalibration(Calibrated, null);
        var mount = new Mount { RaEffect = 1.3, DecEffect = 0.7, DecBacklashPx = 0.5 };
        mount.Guide(model, dithers: 8);
        model.Values.IsNeutral.Should().BeTrue();

        mount.Guide(model, dithers: 30);
        model.Values.IsNeutral.Should().BeFalse();
    }

    [Test]
    public void Learns_nothing_from_interrupted_windows()
    {
        var model = new PulseModel();
        model.UseCalibration(Calibrated, null);
        new Mount { DecEffect = 0.7, InterruptEveryWindow = true }.Guide(model, dithers: 40);

        model.Values.Ra.Should().BeNull();
        model.Values.Dec.Should().BeNull();
        model.State(T0)!.Dec!.Weight.Should().Be(0);
    }

    [Test]
    public void A_dither_in_one_axis_teaches_only_that_axis()
    {
        var model = new PulseModel();
        model.UseCalibration(Calibrated, null);
        new Mount { RaEffect = 1.2, RaOnly = true }.Guide(model, dithers: 40);

        model.State(T0)!.Dec!.Weight.Should().Be(0);
        model.Values.Ra!.Effect.Should().BeApproximately(1.2, 0.06);
    }

    [Test]
    public void Resumes_from_the_stored_state_of_the_same_calibration_only()
    {
        var learned = new PulseModel();
        learned.UseCalibration(Calibrated, null);
        new Mount { RaEffect = 1.14, DecEffect = 0.78, DecBacklashPx = 0.4 }.Guide(learned, dithers: 60);
        var stored = learned.State(T0)!;

        var same = new PulseModel();
        same.UseCalibration(Calibrated.AddMilliseconds(300), stored).Should().BeTrue();
        same.Values.Ra.Should().Be(learned.Values.Ra);
        same.Values.Dec.Should().Be(learned.Values.Dec);
        same.Values.RaEffect.Should().BeApproximately(learned.Values.RaEffect, PulseModel.EffectStep);
        same.Values.DecEffect.Should().BeApproximately(learned.Values.DecEffect, PulseModel.EffectStep);
        same.Values.IsNeutral.Should().BeFalse();

        var other = new PulseModel();
        other.UseCalibration(Calibrated.AddHours(1), stored);
        other.Values.Should().Be(PulseModelValues.Neutral);

        // the calibration in use again: nothing starts over
        same.UseCalibration(Calibrated, stored).Should().BeFalse();
    }

    [Test]
    public void A_new_calibration_starts_over()
    {
        var model = new PulseModel();
        model.UseCalibration(Calibrated, null);
        new Mount { DecEffect = 0.78 }.Guide(model, dithers: 40);
        model.Values.IsNeutral.Should().BeFalse();

        model.UseCalibration(Calibrated.AddDays(1), model.State(T0)).Should().BeTrue();
        model.Values.Should().Be(PulseModelValues.Neutral);
        model.State(T0)!.Dec!.Weight.Should().Be(0);
    }

    [Test]
    public void Store_round_trips_through_json()
    {
        var model = new PulseModel();
        model.UseCalibration(Calibrated, null);
        new Mount { RaEffect = 1.14, DecEffect = 0.78, DecBacklashPx = 0.4 }.Guide(model, dithers: 30);
        var state = model.State(T0)!;
        var key = new PulseModelKey("profile", "EQMod Mount");

        var store = new PulseModelStore();
        store.Set(key, state);
        var back = PulseModelStore.FromJson(store.ToJson()).Get(key)!;

        back.CalibrationTimestamp.Should().Be(state.CalibrationTimestamp);
        back.Dec!.ZX.Should().Equal(state.Dec!.ZX);
        back.Dec.Weight.Should().Be(state.Dec.Weight);
        var resumed = new PulseModel();
        resumed.UseCalibration(Calibrated, back);
        resumed.Values.Ra.Should().Be(model.Values.Ra);
        resumed.Values.Dec.Should().Be(model.Values.Dec);
    }

    [Test]
    public void A_corrupted_stored_state_is_ignored()
    {
        var model = new PulseModel();
        var bad = new PulseModelState
        {
            CalibrationTimestamp = Calibrated,
            Dec = new PulseResponseSums { ZX = [double.NaN, .. new double[15]], ZZ = new double[16], XX = new double[16], Zy = new double[4], Xy = new double[4], Weight = 20 },
            Ra = new PulseResponseSums { ZX = [1, 0], Weight = 20 },
        };

        model.UseCalibration(Calibrated, bad);
        model.Values.Should().Be(PulseModelValues.Neutral);
        model.State(T0)!.Dec!.Weight.Should().Be(0);
        model.State(T0)!.Ra!.Weight.Should().Be(0);
    }

    [Test]
    public void The_effect_in_use_is_bounded()
    {
        var model = new PulseModel();
        model.UseCalibration(Calibrated, null);
        new Mount { RaEffect = 3.0, DecEffect = 0.3 }.Guide(model, dithers: 60);

        model.Values.RaEffect.Should().Be(PulseModel.MaxEffect);
        model.Values.DecEffect.Should().Be(PulseModel.MinEffect);
    }

    private static string Describe(PulseModelValues v) =>
        FormattableString.Invariant(
            $"in use: RA {v.RaEffect:F3}, Dec {v.DecEffect:F3}; estimated: RA {v.Ra?.Effect:F3} ± {v.Ra?.EffectSigma:F3}, Dec {v.Dec?.Effect:F3} ± {v.Dec?.EffectSigma:F3}, reversal loss {v.Dec?.ReversalLossPx:F3} ± {v.Dec?.ReversalLossSigmaPx:F3} px ({v.Dec?.Windows:F1} windows)");

    /// <summary>
    /// Both axes of a mount guided by a proportional controller (gain 0.7) at 2.5 s frames: the star moves by the pulses times
    /// the true effect, the Dec motor turns inside its backlash before the star moves after a reversal, and the mount
    /// wanders and drifts. Every 25 frames a dither moves the lock by 1–4 px in a random direction per axis.
    /// </summary>
    private sealed class Mount
    {
        private const double Rate = 2.419 / 1000; // calibrated px/ms, both axes
        private const double Gain = 0.7;
        private const int FramesPerDither = 25;

        private readonly Random rng = new(11);
        private double ra;
        private double dec;
        private double decPlay;       // motor position inside the backlash: 0 = engaged South, DecBacklashPx = engaged North
        private DateTimeOffset now = T0;

        public double RaEffect { get; init; } = 1.0;

        public double DecEffect { get; init; } = 1.0;

        public double DecBacklashPx { get; init; }

        public double SeeingPx { get; init; } = 0.2;

        public double WanderPx { get; init; } = 0.05;

        public double DriftPxPerFrame { get; init; } = 0.01;

        /// <summary>Size the pulses with the model's values in use, as the guider does.</summary>
        public bool UseModel { get; init; }

        public bool InterruptEveryWindow { get; init; }

        public bool RaOnly { get; init; }

        public void Guide(PulseModel model, int dithers)
        {
            for (int d = 0; d < dithers; d++)
            {
                double dRa = DitherOffset();
                double dDec = RaOnly ? 0 : DitherOffset();
                ra += dRa;
                dec += dDec;
                model.DitherStarted(dRa, dDec);
                for (int f = 0; f < FramesPerDither; f++)
                {
                    Frame(model);
                    if (InterruptEveryWindow && f == PulseModel.WindowFrames - 2)
                    {
                        model.Interrupt(mountMoved: false);
                    }
                }
            }
        }

        private double DitherOffset() => (rng.Next(2) == 0 ? -1 : 1) * (1 + 3 * rng.NextDouble());

        private void Frame(PulseModel model)
        {
            now = now.AddSeconds(2.5);
            double zRa = ra + Gauss() * SeeingPx;
            double zDec = dec + Gauss() * SeeingPx;
            model.FrameMeasured(zRa, zDec, now, out _);
            var v = UseModel ? model.Values : PulseModelValues.Neutral;
            var pulses = new List<PulseCommand>(2);

            // corrections from the measured offsets (seeing included), sized with the rate in use
            double cRa = Gain * zRa;
            int raMs = (int)Math.Round(Math.Abs(cRa) / (Rate * v.RaEffect));
            if (raMs >= 20)
            {
                pulses.Add(new PulseCommand(cRa > 0 ? GuideDirection.West : GuideDirection.East, raMs));
            }

            double cDec = Gain * zDec;
            int decDirection = Math.Sign(cDec);
            int decMs = (int)Math.Round(Math.Abs(cDec) / (Rate * v.DecEffect));
            if (decMs >= 20)
            {
                pulses.Add(new PulseCommand(decDirection > 0 ? GuideDirection.South : GuideDirection.North, decMs));
            }

            model.PulsesSent(pulses, Rate, Rate);
            foreach (var p in pulses)
            {
                double motion = p.DurationMs * Rate;
                switch (p.Direction)
                {
                    case GuideDirection.West:
                        ra -= RaEffect * motion;
                        break;
                    case GuideDirection.East:
                        ra += RaEffect * motion;
                        break;
                    case GuideDirection.South:
                    {
                        double m = DecEffect * motion;
                        double taken = Math.Min(m, decPlay);
                        decPlay -= taken;
                        dec -= m - taken;
                        break;
                    }

                    default:
                    {
                        double m = DecEffect * motion;
                        double taken = Math.Min(m, DecBacklashPx - decPlay);
                        decPlay += taken;
                        dec += m - taken;
                        break;
                    }
                }
            }

            ra += WanderPx * Gauss() + DriftPxPerFrame;
            dec += WanderPx * Gauss() - DriftPxPerFrame;
        }

        private double Gauss()
        {
            double u1 = 1.0 - rng.NextDouble();
            double u2 = rng.NextDouble();
            return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
        }
    }
}
