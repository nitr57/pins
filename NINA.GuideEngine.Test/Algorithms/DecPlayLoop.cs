// SPDX-License-Identifier: MPL-2.0

using NINA.GuideEngine.Algorithms;
using NINA.GuideEngine.Core;

namespace NINA.GuideEngine.Test.Algorithms;

/// <summary>
/// A Dec-only closed loop around <see cref="AxisCorrector"/> with a mount whose gear has a soft play: the motor drives the
/// output through several elastic stages of different play (a physical take-up independent of the play model's code),
/// the sky drifts and wanders, and the measurement adds seeing. Defaults follow the OnStepX guide log of 2026-10-12:
/// Dec rate 3.09 px/s, 1.3 s frames, seeing σ ≈ 0.17 px, almost no drift, and a play the 900 ms compensation never
/// crossed (most of it 1–3 s of motor time).
/// </summary>
internal sealed class DecPlayLoop
{
    public double YRatePxPerMs { get; init; } = 0.00309;

    public double FrameSec { get; init; } = 1.3;

    public double SeeingSigmaPx { get; init; } = 0.17;

    /// <summary>Dec drift of the sky (px/s); positive grows the offset positive (corrected by South).</summary>
    public double DriftPxPerSec { get; init; } = 0.0002;

    /// <summary>Random walk of the sky per frame (px).</summary>
    public double WanderPxPerFrame { get; init; } = 0.01;

    /// <summary>
    /// A slow wander of the star (px σ) that follows an AR(1) process with <see cref="SlowWanderPhi"/> from frame to frame;
    /// the Dec offsets of the 2026-10-12 log had σ 0.21 px and a lag-1 autocorrelation of 0.84 (about 8 s). 0 = none.
    /// </summary>
    public double SlowWanderPx { get; init; }

    public double SlowWanderPhi { get; init; } = 0.84;

    /// <summary>Play of the gear stages (motor ms) and their share of the output.</summary>
    public (double PlayMs, double Share)[] Stages { get; init; } = [(150, 0.1), (900, 0.2), (1500, 0.3), (2200, 0.25), (3000, 0.15)];

    /// <summary>Each stage's play varies by this factor around its value, redrawn every <see cref="PlayChangeFrames"/> frames.</summary>
    public double PlayVariation { get; init; } = 0.0;

    public int PlayChangeFrames { get; init; } = 300;

    public int Seed { get; init; } = 1;

    /// <summary>A dither every this many frames (0 = none): the lock position jumps by up to ±<see cref="DitherPx"/> in Dec.</summary>
    public int DitherEveryFrames { get; init; }

    public double DitherPx { get; init; } = 3;

    /// <summary>
    /// A dither is moved back as Guider does by default (<see cref="NINA.GuideEngine.Guiding.GuiderSettings.FastRecenter"/>):
    /// the algorithms are told of it, and the next frame makes one open-loop recovery move of the dither, which the gate
    /// does not hold and which drops an open take-up window. False: the dither is guided out with guide steps.
    /// </summary>
    public bool FastRecenter { get; set; }

    /// <summary>A dither has settled once the true offset stayed below this (px) for <see cref="SettleFrames"/> frames.</summary>
    public double SettlePx { get; init; } = 0.3;

    public int SettleFrames { get; init; } = 3;

    /// <summary>
    /// The measured offset snaps to multiples of this (px); 0 = it does not. The Sky Simulator renders a star of HFD ≤ 3 px
    /// from 5 × 5 samples per pixel placed relative to the star and rounded to whole pixels, so its image, and the
    /// centroid, moves in steps of 0.2 px.
    /// </summary>
    public double MeasurementQuantumPx { get; init; }

    /// <summary>
    /// Dec reversals (a pulse against the previous Dec pulse sent) of at least this many ms are not carried out; null =
    /// all are. In the PHD2 guide logs of 2026-09/10 on an OnStep mount over INDI, compensated reversals ended at once
    /// and did not move the star.
    /// </summary>
    public int? DropReversalsFromMs { get; init; }

    /// <summary>
    /// The mount reports a reversal it did not carry out (an INDI Alert): the guider then treats the frame's pulses as not
    /// sent, as Guider does for a <see cref="PulseRejectedException"/>.
    /// </summary>
    public bool ReportDropped { get; init; }

    /// <summary>
    /// The Dec rate the corrector sizes its pulses with, as a share of the mount's (<see cref="YRatePxPerMs"/>): the pulse
    /// model's Dec effect in use (0.67 lengthens the pulses 1.5 ×, its limit). 1 = the calibrated rate.
    /// </summary>
    public double CorrectorRateFactor { get; init; } = 1.0;

    public sealed record Result(double TrueRmsPx, double MeasuredRmsPx, int Reversals, double DecPulseSec, int Frames, int Held, double Hours)
    {
        /// <summary>Mean frames from a dither until it settled (NaN without dithers).</summary>
        public double SettleFramesMean { get; init; } = double.NaN;

        /// <summary>RMS of the true offset outside the settling after dithers (px).</summary>
        public double GuidingRmsPx { get; init; } = double.NaN;

        /// <summary>RMS of the measured offset outside the settling (px): what a guide log shows.</summary>
        public double MeasuredGuidingRmsPx { get; init; } = double.NaN;

        /// <summary>Dec pulses the mount did not carry out (<see cref="DropReversalsFromMs"/>).</summary>
        public int Dropped { get; init; }

        /// <summary>Dec pulses of the corrector's max duration (2500 ms in <see cref="Corrector"/>).</summary>
        public int LimitPulses { get; init; }

        public double ReversalsPerHour => Reversals / Hours;

        public double PulseSecPerHour => DecPulseSec / Hours;

        public override string ToString() =>
            $"true RMS {TrueRmsPx:F3} px, guiding {GuidingRmsPx:F3} px, settle {SettleFramesMean:F1} frames, measured {MeasuredRmsPx:F3} px, {ReversalsPerHour:F0} reversals/h, {PulseSecPerHour:F0} s pulses/h, held {Held}";
    }

    /// <summary>Runs <paramref name="frames"/> guide frames with the corrector and returns what the sky saw.</summary>
    public Result Run(AxisCorrector corrector, int frames, Action<AxisCorrector, GuideCorrection>? onStep = null)
    {
        var rng = new Random(Seed);
        int n = Stages.Length;
        var play = Stages.Select(s => s.PlayMs).ToArray();
        var gap = new double[n]; // motor position relative to the stage's centre, ±play/2
        double sky = 0;           // where the sky put the star (px)
        double mount = 0;         // what the gear moved it back (px)
        var t0 = new DateTimeOffset(2026, 10, 12, 21, 31, 0, TimeSpan.Zero);
        double sumTrue = 0, sumMeasured = 0;
        int reversals = 0, held = 0, dropped = 0, limitPulses = 0;
        double pulseMs = 0;
        GuideDirection? last = null;
        double lockOffset = 0;
        double recenterPx = 0;
        double slow = 0;
        int sinceDither = -1, calm = 0, settledCount = 0;
        double settleSum = 0, guidingSum = 0, measuredGuidingSum = 0;
        int guidingFrames = 0;

        for (int f = 0; f < frames; f++)
        {
            if (PlayVariation > 0 && f % PlayChangeFrames == 0)
            {
                for (int i = 0; i < n; i++)
                {
                    play[i] = Stages[i].PlayMs * (1 + PlayVariation * (2 * rng.NextDouble() - 1));
                    gap[i] = Math.Clamp(gap[i], -play[i] / 2, play[i] / 2);
                }
            }

            sky += DriftPxPerSec * FrameSec + WanderPxPerFrame * Gauss(rng);
            double slowBefore = slow;
            slow = SlowWanderPhi * slow + SlowWanderPx * Math.Sqrt(1 - SlowWanderPhi * SlowWanderPhi) * Gauss(rng);
            sky += slow - slowBefore;
            var now = t0.AddSeconds(f * FrameSec);
            if (DitherEveryFrames > 0 && f > 0 && f % DitherEveryFrames == 0)
            {
                double before = lockOffset;
                lockOffset = DitherPx * (2 * rng.NextDouble() - 1);
                sinceDither = 0;
                calm = 0;
                if (FastRecenter)
                {
                    // Guider.DitherCore: the lock moved by the mount delta, the offset it creates is its negative
                    corrector.GuidingDithered(0, lockOffset - before, now);
                    recenterPx = before - lockOffset;
                }
            }

            double truth = sky - mount - lockOffset;
            bool guidingFrame = sinceDither < 0;
            if (sinceDither >= 0)
            {
                calm = Math.Abs(truth) < SettlePx ? calm + 1 : 0;
                if (calm >= SettleFrames)
                {
                    settleSum += sinceDither - SettleFrames + 1;
                    settledCount++;
                    sinceDither = -1;
                }
                else
                {
                    sinceDither++;
                }
            }
            else
            {
                guidingSum += truth * truth;
                guidingFrames++;
            }

            double measured = truth + SeeingSigmaPx * Gauss(rng);
            if (MeasurementQuantumPx > 0)
            {
                // the grid is fixed to the sensor; the lock position sits somewhere on it
                const double gridPhase = 0.07;
                measured = MeasurementQuantumPx * Math.Round((measured + gridPhase) / MeasurementQuantumPx) - gridPhase;
            }

            sumTrue += truth * truth;
            sumMeasured += measured * measured;
            if (guidingFrame)
            {
                measuredGuidingSum += measured * measured;
            }

            GuideCorrection corr;
            bool recenterMove = recenterPx != 0;
            if (recenterMove)
            {
                // Guider with FastRecenter: one open-loop recovery move of the dither (it stays below 0.7 × the search
                // region), past the algorithms and the gate, the Dec pulse clamped to the max pulse
                corr = corrector.Move(0, recenterPx, 1, YRatePxPerMs * CorrectorRateFactor, MoveOptions.RecoveryMove, now)!;
                corrector.DirectMoveApplied(0, recenterPx);
                recenterPx = 0;
            }
            else
            {
                corr = corrector.GuideStep(0, measured, 1, YRatePxPerMs * CorrectorRateFactor, now);
            }

            onStep?.Invoke(corrector, corr);
            if (corr.DecHeldByBacklash)
            {
                held++;
            }

            bool reported = false;
            foreach (var computed in corr.Pulses.Where(p => p.Direction.Axis() == GuideAxis.Dec))
            {
                var p = recenterMove ? computed with { DurationMs = Math.Min(computed.DurationMs, corrector.Limiter.MaxDecDurationMs) } : computed;
                pulseMs += p.DurationMs;
                if (p.DurationMs >= corrector.Limiter.MaxDecDurationMs)
                {
                    limitPulses++;
                }

                bool reversal = last is { } l && l != p.Direction;
                if (reversal)
                {
                    reversals++;
                }

                last = p.Direction;
                if (reversal && p.DurationMs >= DropReversalsFromMs)
                {
                    dropped++;
                    reported |= ReportDropped;
                    continue;
                }

                double motor = (p.Direction == GuideDirection.South ? 1 : -1) * p.DurationMs;
                double moved = 0;
                for (int i = 0; i < n; i++)
                {
                    double before = gap[i];
                    gap[i] = Math.Clamp(gap[i] + motor, -play[i] / 2, play[i] / 2);
                    moved += Stages[i].Share * (motor - (gap[i] - before));
                }

                mount += moved * YRatePxPerMs;
            }

            if (reported)
            {
                // Guider.PlayModelPulsesNotSent
                if (corrector.Backlash is PlayModelBacklash playModel)
                {
                    playModel.ResetState();
                }
            }
            else
            {
                corrector.CorrectionsApplied(0, corr.DecCorrectionPx);
            }
        }

        double hours = frames * FrameSec / 3600;
        return new Result(Math.Sqrt(sumTrue / frames), Math.Sqrt(sumMeasured / frames), reversals, pulseMs / 1000, frames, held, hours)
        {
            SettleFramesMean = settledCount > 0 ? settleSum / settledCount : double.NaN,
            GuidingRmsPx = guidingFrames > 0 ? Math.Sqrt(guidingSum / guidingFrames) : double.NaN,
            MeasuredGuidingRmsPx = guidingFrames > 0 ? Math.Sqrt(measuredGuidingSum / guidingFrames) : double.NaN,
            Dropped = dropped,
            LimitPulses = limitPulses,
        };
    }

    /// <summary>The take-up after a reversal from a fully engaged gear, as a calibration's Dec return would measure it.</summary>
    public IReadOnlyList<NINA.GuideEngine.Calibration.DecTakeUpPoint> CalibrationTakeUp(int stepMs, int steps)
    {
        var points = new List<NINA.GuideEngine.Calibration.DecTakeUpPoint>();
        for (int k = 1; k <= steps; k++)
        {
            double u = k * stepMs;
            double moved = Stages.Sum(s => s.Share * Math.Max(0, u - s.PlayMs));
            points.Add(new(u, moved * YRatePxPerMs));
        }

        return points;
    }

    public static AxisCorrector Corrector(IBacklashCompensation? backlash, GuideAlgorithmKind kind = GuideAlgorithmKind.Hysteresis, double minMove = 0.05)
    {
        var limiter = new PulseLimiter();
        limiter.SetMaxDecDuration(2500);
        if (backlash is PlayModelBacklash play)
        {
            play.Limiter = limiter;
        }

        var ra = GuideAlgorithmFactory.Create(GuideAlgorithmKind.Hysteresis, GuideAxis.Ra, minMove);
        var dec = GuideAlgorithmFactory.Create(kind, GuideAxis.Dec, minMove);
        return new AxisCorrector(ra, dec, limiter, backlash ?? new BacklashCompensation(limiter: limiter));
    }

    private static double Gauss(Random rng)
    {
        double u1 = 1.0 - rng.NextDouble();
        double u2 = rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }
}
