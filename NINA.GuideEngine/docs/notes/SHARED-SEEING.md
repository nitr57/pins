# Predictive: one seeing for both axes — design

Status: **tried and removed** (designed and implemented 2026-09-28, removed from the code 2026-09-29). It failed its
simulator gate (below), and the estimation check in §5 went the other way than expected. This note stays as a record,
so the idea isn't retried without addressing why it failed. The better fix for Dec's odd seeing value is to model
Dec's pulses; see [DEC-PULSE-MODEL.md](DEC-PULSE-MODEL.md).

**Simulator gate (2026-09-29).** True RMS, both axes Predictive, 25 min, the five presets:

| Preset | Each axis alone | Shared | Change |
|---|---|---|---|
| Good mount | 0.157″ | 0.159″ | +1 % |
| High seeing | 0.492″ | 0.592″ | **+20 %** |
| Poor periodic error | 0.588″ | 0.509″ | −13 % |
| Dec backlash | 0.176″ | 0.196″ | **+11 %** |
| Clouds | 0.152″ | 0.163″ | +7 % |

The gate (not worse by more than 10 %) fails on two presets. That's in the simulator, where the seeing really is the
same on both axes.

**Likely reason (inference, untested):** the one-step prediction error is what the correction needs, because the
correction cancels the prediction. The joint likelihood trades some of it for agreement on the seeing, even where each
axis's random-walk-plus-white model is only an approximation (periodic error, backlash and drift changes are absorbed
differently per axis).

The gate was `PredictiveComparisonTests.Shared_seeing_is_not_worse_than_each_axis_alone`, removed with the code.
Related: [CORRECTION-PACE.md](CORRECTION-PACE.md), [CROSSING-PENALTY.md](CROSSING-PENALTY.md), [ALGORITHMS.md](../ALGORITHMS.md)
("Predictive").

## 1. Problem

The statistics card shows each axis's seeing estimate. On the EQMod rig, RA shows about 0.3–0.5″ and Dec about
0.08–0.11″ (2026-09-27/28), which is confusing and physically wrong:

- **Seeing is the same on both axes in expectation.** Atmospheric image motion and centroid noise are isotropic, and
  the rotation from camera to mount axes keeps them isotropic.
- **The rig's own unguided data agree.** The Coach's drift run on 2026-09-23 (155 frames of 1.32 s), fitted per axis
  with the filter's own model (random walk plus white noise), gives a white part of 0.22″ on RA and 0.21″ on Dec. The
  walk is 0.30″ and 0.15″ per frame. So the axes differ in mount motion, not in seeing (one 3-minute run).
- **On 2026-09-27 the Dec value didn't follow the sky.** RA's estimate went from 0.28″ to 0.48″ over the session, while
  Dec's stayed at 0.08–0.10″ as long as Predictive guided Dec. It rose to 0.13–0.20″ once Dec ran on Resist Switch,
  which sends far fewer pulses.

**The cause.** The seeing value isn't measured; it's derived from the model the bank chose. Dec's bank sits at the top
of the wander grid (ratio 30, frame weight 0.97): it explains Dec's jitter as mount motion because Dec's corrections
don't land as the model assumes. Dec pulses move about 0.7× the calibrated amount with a ~30 ms dead band, and on every
second frame part of a correction lands a frame late (the camera's early exposures).

## 2. Theory

**The current choice is already maximum likelihood.** Per axis, a model m (a point on the grid of wander and drift
ratios) predicts innovations ν_k ~ N(0, S_k·r), with S_k in units of the seeing variance r (the filter's P11 plus the
frame's noise factor). Its log-likelihood is

    ℓ(m, r) = −½ Σ_k [ ln(2π S_k r) + ν_k² / (S_k r) ]

Maximising over r gives r̂ = mean(ν²/S), which is exactly how the seeing is estimated today. The profile likelihood is

    ℓ(m) = −½ N [ mean(ln S) + ln r̂ ] + const

For a filter in steady state (S constant) this is −½ N ln(mean ν²). So **choosing the model with the lowest mean squared
prediction error, as the bank does now, is maximum likelihood with a separate seeing per axis.**

**Shared seeing.** One r for both axes: ℓ(m_RA, m_Dec, r) = ℓ_RA + ℓ_Dec. Maximising over r gives the pooled estimate
r̂ = (Σ_RA ν²/S + Σ_Dec ν²/S) / (N_RA + N_Dec), and a cost per frame of

    C = (Σ_RA ln S + Σ_Dec ln S) / N + ln r̂,    N = N_RA + N_Dec

- **How each axis chooses:** the model that lowers C, given the model the other axis is using. That's coordinate
  descent on the joint likelihood; no step can raise C.
- **The fallback:** with only one axis on Predictive, or the setting off, C reduces to today's criterion.
- **What it changes:** a model is judged by how well it predicts and by whether the seeing it implies agrees with the
  other axis. A Dec model that implies 0.1″ while RA's implies 0.4″ now pays for that.

**What stays as it is:**
- the weights and the memory of the scores: 120 frames, noisier frames counting 1/f;
- the exclusions: settling frames and jumps;
- the switching margin: 3 % of the prediction error, which is ln(1/0.97) = 0.0305 in C.

The seeing r used for the jump test, the frame noise factor, and the displayed seeing and mount motion becomes the
pooled estimate of both axes' chosen models.

## 3. Expected effects, and the main risk

- **Seeing:** both axes show one value, which follows the sky.
- **Dec:** its model moves to lower wander ratios and its frame weight drops, so less of each frame's seeing is
  corrected.
- **RA:** little change. Its model already implies a plausible seeing; it may move slightly towards the pooled value.

**The main risk: Dec lingering.** Dec's high frame weight partly compensates its weak pulses. A free bank picks a
higher gain when pulses move less than calibrated, which keeps the RMS near its optimum; the review of the earlier
response-adaptation design found exactly this in a generic model. With a consistent seeing, that compensation shrinks.
Dec's effective loop gain (pulse effect × pace × frame weight) falls from about 0.34 to about 0.2–0.28 per frame, so a
Dec offset lingers longer. Whether less seeing-chasing or more lingering wins isn't known; the live test decides. If
Dec lingers, the physical fix is a Dec pulse model (dead band and scale, measured from dither settles; see
CROSSING-PENALTY.md), not a higher gain.

Per-axis jitter can legitimately differ a little. For example, RA gear noise faster than one frame looks white. Sharing
then gives a compromise. A per-axis factor isn't modelled, since it would need its own identification.

## 4. Implementation

- **Filter:** per-filter running sums Σ w·ν²/S and Σ w·ln S next to the existing score, with the same weights and
  exclusions.
- **PredictiveAlgorithm:**
  - an optional partner: the other axis's Predictive algorithm;
  - both banks' selection (with and without periodic error) uses C with the partner's chosen model once the partner
    has scores (≥ 10 weighted frames), otherwise today's rule;
  - the seeing estimate pools both axes' accumulators.
- **Guider:** after every settings change, it pairs the two axes when both run Predictive and `GuiderSettings.SharedSeeing`
  is on, and unpairs them otherwise.
- **Plugin:** Advanced setting "Shared seeing" (`PredictiveSharedSeeing`), default off after the estimation check in §5.
  The engine default is off too, so the documented simulator results stay comparable.
- **Guide log:** both algorithm lines of the header end in `, Shared seeing = on`. Switching it mid-session appears as a
  parameter change.

## 5. Tests

- **Unit:**
  - with sharing, an axis whose own data imply a much lower seeing moves to a model whose seeing agrees with the other
    axis;
  - without sharing, nothing changes;
  - the pairing rules.
- **Simulator safety net:** the five presets with both axes on Predictive, shared against not shared. The true RMS may
  not get worse by more than 10 %.
- **Estimation check on 2026-09-27:** a replay of the recorded frames and pulses, estimates only, no tuning
  (`NightReplayTests`). It shows each axis's chosen model and seeing with and without sharing.

**Result of the estimation check (2026-09-28)** — the Predictive part of the night, until 20:26:

| | RA frame weight | Dec frame weight | Seeing RA / Dec |
|---|---|---|---|
| Each axis alone (as on the rig) | 0.6–0.8 (wander ratio 1–3) | 0.97 (ratio 30) | 0.28–0.48″ / 0.09–0.25″ |
| Shared | 0.91–0.97 (ratio 10–30) | 0.91–0.92 (ratio 10) | 0.14–0.21″ for both |

- **The axes agree,** and the shared value is close to the white part of the unguided Coach run on 2026-09-23
  (0.21–0.22″). That suggests RA's own estimate was too high.
- **But they agree by pulling RA into trusting every frame,** not by calming Dec. Dec's closed-loop data can only be
  explained with a low seeing, because its corrections don't land as modelled. A shared seeing doesn't remove that
  mismatch; it spreads it to RA.
- **A synthetic case shows the intended direction:** returning excursions on one axis, pulses as calibrated
  (`SharedSeeingPullsAnAxisToTheSkysSeeing`). That axis's own seeing was too high (0.32 px against a true 0.2 px), and
  sharing corrected it and lowered its true error. So sharing corrects an axis whose seeing estimate is off, whichever
  way, but only when the other axis's model is right.
- **Consequence:** the plugin default is off. Sharing is worth a live test only after Dec's model mismatch is addressed
  (the Dec pulse model), or as an A/B with RA's frame weight as an explicit harm check.

## 6. Live test

One night, pace 0.5, Predictive on both axes, 20-minute blocks in the order A B B A: A = shared seeing off, B = on (an
Advanced setting; each change appears in the guide log). Criteria, fixed in advance:

- **Seeing:** in B both axes show the same value.
- **Dec frame weight:** in B, below 0.9, and **RA's frame weight** not above 0.9 (the replay suggests RA's rises).
- **Harm checks:**
  - Dec's lag-1 error autocorrelation rises by no more than 0.15;
  - the upper 95 % bound of Dec's RMS ratio B/A is below 1.10;
  - RA's RMS is unchanged within its block-to-block scatter.

If the harm checks fail (Dec lingers), sharing stays off by default and the Dec pulse model comes first.
