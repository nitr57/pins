# Pulse model: what a pulse really moves — design

Status: designed and implemented 2026-09-29 (setting `PulseModel`, plugin default on); the live test is open. It replaces
the shared-seeing idea ([SHARED-SEEING.md](SHARED-SEEING.md)), which tried to fix Dec's odd seeing value in the
estimator, and starts from the Dec pulse loss that [CROSSING-PENALTY.md](CROSSING-PENALTY.md) §6.3 and
[NEXTGEN.md](NEXTGEN.md) T3 found. Related: [ALGORITHMS.md](../ALGORITHMS.md#pulse-model).

In short:
- **Built:** the effect of a pulse per axis, learned from dithers.
- **Not found:** the dead time that earlier notes reported. It was an artifact of the method.
- **Found but not compensated:** Dec's backlash, because dithers can't measure it reliably (§5).

## 1. Problem

Every guide algorithm turns a correction in pixels into a pulse with the calibrated rate, and Predictive's filters
assume the pulse moved exactly that. On the EQMod rig Dec behaves as if it didn't:

- **The Dec bank sits at the top of the wander grid.** Wander 30, frame weight 0.97.
- **The seeing it implies is too small.** About 0.1″, against about 0.4″ on RA.
- **Dec errors linger.** The lag-1 autocorrelation is +0.23.

**What 2026-09-27 looked like** (Predictive, Dec guide mode Auto, 616 guided frames):

- 89 % of the frames sent a Dec pulse; the median is 48 ms (0.12 px) and 90 % are under 110 ms.
- 40 % of the pulses reversed the Dec direction.
- A run of pulses in one direction added up to 0.23 px (median); 57 % of the runs were shorter than 0.3 px.

Earlier notes described this as a dead band of about 30 ms per pulse plus a loss. Both came from fits that use the
closed-loop pulses themselves. §2 shows what survives a cleaner method.

## 2. Evidence: the response to dithers

**Method.** A dither shifts the lock position by a random offset d. The pulses that follow move the star to the new lock,
but they also react to wander and seeing, so regressing the star's motion on them is biased. That is the classic problem
of closed-loop identification (Forssell & Ljung 1999). The offset d itself is exogenous, so it is a valid instrument
(two-stage least squares).

For each dither, over the next m frames (m = 8, about 20 s at 2.5 s exposures), the model is:

    y = z₀ − z_m = g · Σ sᵢ·tᵢ·R  −  b · Σ (signed reversals)  +  c · Δt  +  noise

- z is the axis offset; sᵢ, tᵢ are the direction and length of each pulse as sent; R is the calibrated rate.
- g is the effect: the share of a calibrated pulse that moves the star.
- b is the backlash taken up at every reversal of the pulse direction.
- c is the drift.
- **Instruments:** d; whether the first settling pulse reverses the last pulse before the dither (known before the
  dither, so exogenous); and Δt.

**Data.** Guide logs of the same mount (EQMod, 3.11″/px, Dec 56.8°):

- **PHD2:** 5 nights, 2026-09-18 to 09-24, about 320 dithers, PHD2's own calibration.
- **PINS:** 3 nights, 2026-09-25 to 09-27, about 113 dithers, with PINS's calibration of 2026-09-23 (step 1600 ms).

**RA is the control.** It has no motor reversal at a guide rate below sidereal, so it must show no backlash.

| m = 8 | Effect g | Backlash b |
|---|---|---|
| RA, PHD2 | 1.06 ± 0.02 | −0.28 ± 0.21 px |
| RA, PINS | **1.14 ± 0.01** | −0.01 ± 0.12 px |
| Dec, PHD2 | 1.01 ± 0.03 | **0.75 ± 0.22 px** |
| Dec, PINS | **0.78 ± 0.01** | **0.40 ± 0.12 px** |

1. **No dead time.**
   - Plain least squares on the same windows finds 80–160 ms "dead time" on RA (PINS) and 240–340 ms on both axes
     (PHD2), whose motor never stops for RA pulses. That's the closed-loop bias.
   - With instruments the dead time is −16 ± 127 ms (RA) and 60–90 ± 70 ms (Dec): nothing.
   - The earlier "31 ms dead band" was the same artifact.
2. **PINS's stored calibration is off on both axes, in opposite directions.**
   - **RA:** pulses act 1.14× the calibrated amount, the same for small and large dithers. In sky units that is the
     nominal 7.5″/s; the calibration says 6.6″/s. So RA over-corrects by 14 %.
   - **Dec:** pulses act 0.78×. In sky units that is 5.9″/s; PINS's calibration says 7.5″/s, and PHD2's calibration of
     the same mount said 6.1″/s. So Dec under-corrects by 22 %.
   - PHD2's pulses act as PHD2 calibrated them (1.06 and 1.01). So a pulse effect isn't a property of guiding; a
     calibration can simply be off. Why this one is off isn't known.
3. **Dec has backlash, RA doesn't.** The PINS nights show about 0.4 px (1.2″); PHD2's nights, earlier in September,
   0.5–0.8 px. It matches the rig note of about 0.5 px. These are lower bounds (§5). So on 09-27 most of Dec's pulse
   runs never got through the slack: the gear rattled, the star stayed. The filter took the corrections that didn't
   happen for mount motion (§1).
4. **Dec keeps moving after a pulse.** g grows with the window, from 0.68 at 2 frames to 0.81 at 10 (PINS; PHD2 0.85 →
   1.05); RA doesn't. The slow part isn't modelled here (§6).
5. **A logging bug skewed RA's first estimate.** The guide log records the requested length of a recenter pulse, but
   what goes out is clamped to the maximum pulse. 54 RA pulses of the three PINS nights were logged above 2500 ms. Fixed
   with this change: the log records the pulses as sent.

## 3. The model

Per pulse on axis a, of length t in direction s, the star moves Δ = s · g_a · R_a · t, where g_a is the effect of the
axis relative to its calibrated rate R_a.

- **Where it sits.** The model is algorithm-independent: it sits in the pulse layer and serves every guide algorithm.
- **Backlash is estimated but not modelled for use** (§5): it keeps the effect estimate unbiased and nothing more.

## 4. Compensation

- **Pulse length.** A correction of c px becomes c / (g_a·R_a) ms, for algorithm, deduced and recenter moves alike.
- **What the algorithm is told.** The motion it can expect: sent length · g_a · R_a. With a correct model that is the
  correction it asked for, so the filters see their corrections land. Predictive's bank can then choose its wander from
  the mount rather than from lost pulses.
- **Caution.** A wrong model is the worst failure: T3 diverged with an effect that was too low.
  - The effect in use is ĝ + σ, the upper end of its uncertainty, which lengthens less.
  - It is clamped to 0.67–1.5 (pulses at most 1.5× longer or shorter) and changes in steps of 0.02.
  - It starts at 1 and acts only once at least 10 windows are behind it and σ ≤ 0.1.

  An effect that is too high leaves a little error for the next frame, which the loop removes; one that is too low
  overshoots on every pulse.

## 5. Learning

**Window.** A dither opens one window per axis it moved.
- It takes the first frame measured after the dither and the next 8 frames, with the pulses sent after them, as sent
  (the recenter clamp included).
- A frame without a star, a rejected jump, a failed pulse, a pause, a moved lock position, a Coach measurement, a new
  dither or the end of guiding drops the window.

**Estimator.** Exactly identified two-stage least squares, from running sums forgotten by 2 % per window (a memory of
about 50 dithers), with standard errors from the residuals.
- **RA** fits g and a drift.
- **Dec** also fits the loss at a reversal of the pulse direction (instrumented by whether the first pulse reverses the
  last one before the dither) and a loss at the window's start in the dither's direction (instrumented by sign d).
  Without them the backlash leaks into the effect: with 1 px of backlash in a synthetic closed loop, the Dec effect came
  out 0.70 for a true 0.78, and 0.72 ± 0.07 with them.

**Why the backlash isn't compensated.** The loss at a reversal is only a lower bound of the backlash. While the guiding
reverses on noise, the gear often sits inside the backlash, and the next pulse then loses part of it whatever its
direction. In a synthetic closed loop with a physical backlash of 0.4 px (seeing 0.1–0.2 px, drift 0–0.03 px per
frame, 200 dithers), the estimate came out between −0.07 and 0.22 px. With compensation feeding back it climbs only
slowly. On the rig's logs it was 0.40 ± 0.12 px on PINS's nights and 0.75 ± 0.22 px on PHD2's (Resist Switch reverses
less). That is too uncertain to size a take-up pulse safely.

For mounts with backlash and a drift, Dec guide mode Drift (no reversals) is the tool.

**Runs with the setting off, too.** The model learns while the setting is off, so switching it on uses what is known.
Each completed window emits the state for the host to store. Each change of the values in use (while on) writes one
guide-log line with the estimates.

**Memory across nights.** The state is stored per profile and mount, like the periodic error, and holds only with the
calibration it was learned with; a new calibration starts over.

**The three PINS nights, replayed through these rules** (a check of the rules, not a tuning; an earlier variant of the
estimator without the start term): Dec's effect was in use after 24 dithers of the first night (0.81) and held at
0.79–0.82.

## 6. Expected effects, risks, limits

**Expected on the rig once learned.** With the pace at 0.5, both axes then get about the pace set.
- RA pulses ×0.87: less over-correction, the direction of the ringing seen on 2026-09-27.
- Dec pulses about ×1.2.

**Simulator** (`ClosedLoopTests.Pulse_model_learns_what_the_pulses_move`: both axes Predictive, 5 px dithers every 2
minutes, 2″ of Dec backlash, 1″/min Dec drift, one hour):

| Calibration | True RMS without | With | Learned (truth) |
|---|---|---|---|
| RA 1.14×, Dec 0.78× off (like the rig) | 0.212″ | 0.205″ | RA 1.12, Dec 0.77 (1.14, 0.78) |
| Right | 0.225″ | 0.205″ | RA 0.98, Dec 1.08 ± 0.10 (1, 1) |

Predictive's filters absorb much of a clean calibration error by themselves, so the simulator gain is small. On the rig
the Dec trouble also comes from its backlash, which the model leaves alone.

**Risks.**
- A mount whose response changes during the night is followed only slowly (about 50 dithers).
- A dither-free session learns nothing, and the model stays as it was.

**Not modelled.**
- Dec backlash (above).
- The slow Dec response (§2.4): a correction's last 5–10 % arrives over the next 20 s.

## 7. Tests

- **Unit** (`PulseModelTests`, a synthetic closed loop with wander, seeing, drift and a physical backlash):
  - it recovers both effects with 0, 0.4 and 1 px of backlash, the effect in use never below the truth;
  - it leaves a mount that moves as calibrated about alone, and learns the same while its own values size the pulses;
  - plus: the gates, dropped windows, one-axis dithers, the calibration rules, the store and a corrupted store.
- **Simulator** (`ClosedLoopTests`):
  - the table above, asserting the learned effects and a true RMS no worse than 1.03× without;
  - a recenter pulse is logged as sent.

## 8. Live test

- **Night 1: on (the plugin default), learning.** Check that the guide-log lines appear after about 10 dithers and
  settle (RA effect about 1.1, Dec about 0.8).
- **Night 2: blocks A B B A of 20 minutes** (A = off, B = on; the setting is Advanced, and each change appears in the
  guide log). Criteria, fixed in advance:
  - **Dec seeing:** in B, Dec's seeing estimate moves towards RA's.
  - **Dec lingering:** in B, Dec's lag-1 error autocorrelation drops.
  - **RA ringing:** in B, RA's lag-2 error autocorrelation moves towards 0.
  - **Harm check:** the upper 95 % bound of the RMS ratio B/A is below 1.10 on each axis.
- **If Dec still lingers or rattles.** Try Dec guide mode Drift with the model on: it removes the reversals that the
  backlash eats.
