# Predictive: response adaptation — design

Status: **superseded** by [CORRECTION-PACE.md](CORRECTION-PACE.md) (2026-09-28). Its review found the 3-lag statistic is
not the gradient, raising Dec's response unsafe, and the achievable gain on the rig small. Kept for the record.
Original status: proposal for review, 2026-09-28. Not implemented. Once agreed, the built version moves into
[ALGORITHMS.md](../ALGORITHMS.md) ("Predictive").
Related: [ALGORITHMS.md](../ALGORITHMS.md) (Predictive, measurement uncertainty), [NEXTGEN.md](NEXTGEN.md).

## 1. Problem

Live on the EQMod rig (2026-09-27; ASI120MM-S guide camera, 3.11″/px, 2.5 s exposures, about 2.8 s cadence),
Predictive on both axes, Response 1, 19:56–20:26, 616 frames, settling excluded:

| | RA | Dec |
|---|---|---|
| Error autocorrelation, lags 1 / 2 / 3 / 4 (noise ±0.08) | −0.01 / **−0.21** / +0.01 / +0.01 | **+0.23** / +0.08 / +0.01 / −0.07 |
| Correction → later error g₁ / g₂ / g₃ (sum), see §3 | −0.07 / **−0.30** / −0.00 (**−0.37**) | **+0.22** / +0.07 / +0.00 (**+0.29**) |
| Same sum over three 10-minute blocks | −0.09 / −0.56 / −0.43 | +0.61 / +0.29 / +0.12 |
| Filter bank | wander ratio 1–3, gain 0.6–0.8 | wander ratio 30 (top of the grid), gain 0.97 |

- **RA over-corrects.** About 30 % of each correction comes back the other way two frames later. After large
  excursions RA rings with a period of about 4 frames.
- **Dec under-corrects.** About 22 % of each correction is still there in the next frame.
- **Nothing in the guider notices.** The oscillation index counts sign changes from one frame to the next (lag 1), so
  it showed a harmless 0.51–0.54 while RA rang at lag 2. The Predictive bank has no notion of how well its
  corrections work.

The physical causes aren't established. Candidates, none confirmed:
- few-second excursions that return by themselves (the Coach's unguided run on this rig on 2026-09-23 had residual
  lag-1 autocorrelation of +0.51 in RA and +0.35 in Dec, where pure seeing would give about 0);
- guide pulses that move the mount more or less than the calibration pulses did;
- the camera: the ASI120MM-S with indi_asi_ccd starts every second exposure when the previous image arrives, before
  the guider's request and pulses. This was measured with `tools/camera-timing`; the driver doesn't know it, and
  DATE-OBS shows the request time.

The design must therefore not depend on the cause.

## 2. Goal and non-goals

**Goal:** per axis, detect that the corrections systematically overshoot or fall short, and change the loop gain
(Predictive's `response`) until they don't. A loop that already works must not be made worse. Every step must be
visible in the guide log, so that each live night teaches us something.

**Not in this step:**
- identifying the physical cause;
- new terms in the Kalman model (returning excursions, delayed correction effect) — see §8;
- the Classic algorithms;
- fitting the simulator to the rig.

## 3. Theory

**Notation, per axis.** After frame k the algorithm predicts the error at the next frame, p_k (px). It sends
R·p_k, where R is the response. c_k is the correction that actually went out, after limits, minimum pulse and Dec
direction, in px as calibrated; a positive c_k reduces a positive offset. The guider already reports it through
`CorrectionApplied`. z_k is the measured offset at frame k.

**The signal.**

    g = ( Σ_k z_{k+1}·c_k + Σ_k z_{k+2}·c_k + Σ_k z_{k+3}·c_k ) / Σ_k c_k²

g is the share of a correction that is still there (g > 0) or has come back the other way (g < 0) within the next
three frames. The individual terms are g₁, g₂, g₃.

**Why g = 0 is the target.** In a loop whose correction acts fully before the next frame, the minimum-variance
controller leaves only the unpredictable part of the error in the next measurement (Åström 1970; used for loop
performance assessment by Harris 1989). That part is uncorrelated with everything known at frame k, including c_k,
so E[z_{k+j}·c_k] = 0 for every j ≥ 1. A nonzero g therefore means the loop could do better with the same data,
whatever the cause.

**Why its sign gives the direction.** For J = ½·E[z²], the gradient with respect to R is (MIT rule, Åström &
Wittenmark):

    ∂J/∂R ≈ −(1/R) · Σ_j h_j · E[z_{k+j}·c_k]

h_j ≥ 0 is the part of a correction that shows up j frames later. It is unknown; its unweighted sum over j = 1..3
is what g estimates. So g < 0 means less response lowers the error, and g > 0 means more does.

**No seeing bias.** z_{k+j} (j ≥ 1) doesn't contain frame k's measurement noise. c_k does, but that noise is
independent of the later frames. A regression on z_{k+1} − z_k, as used during the diagnosis, is biased by exactly
that; g isn't.

**Chasing seeing also shows up.** If the loop corrects noise at gain K with pulses that move α times the calibrated
amount, then roughly g₁ = Var(x) / (K·(Var(x) + r)) − α (x = true error, r = seeing variance). That turns negative
when seeing dominates at high gain, so the rule also calms a loop that chases seeing.

**Why lags 1–3.** A correction can bounce back or arrive late. On the rig RA's reversal peaked at lag 2. Beyond
3 frames the terms were at the noise level.

**Limits of the theory.**
- The MIT rule ignores that R also changes later corrections through the feedback. It holds when the adaptation is
  much slower than the loop (time-scale separation), hence the slow, block-wise updates in §4.
- "g = 0 is optimal" assumes each correction acts fully within one frame. With the ASI120's early starts, part of
  some corrections arrives a frame later. Then the true optimum has a small nonzero g₁, and the rule is close to
  optimal rather than exactly optimal. The live A/B (§7) measures what that costs.

**Why not identify the cause instead (indirect adaptive control).** The previous attempt learned "how far a
correction really moves the mount" inside the filter bank. In closed-loop simulation it drifted to its grid limit
(1.25) even when the truth was 1.0. In closed loop the corrections are functions of the same errors, so the pulse
effect and the mount's wander can't be told apart without extra excitation. Tuning the one scalar that matters,
directly from a sign (direct adaptive control, cf. iterative feedback tuning, Hjalmarsson et al. 1998), doesn't
need that.

## 4. Algorithm

**Blocks.** The statistics are gathered over blocks of B = 100 guided frames per axis (about 5 minutes at 2.8 s).
Each block is evaluated at one fixed R, and then a new block starts. This avoids the lag of a moving average feeding
back into R.

**What counts.** Only frames k whose follow-ups k+1..k+3 are in the same unbroken guided run. A run breaks at
settling frames, restarts (dither, gap, resume, guiding start or stop), lost stars and pauses. Frames of the Predictive
learning phase (the first 120) don't count; the first block starts after it.

**Robustness.** Before multiplying, z and c are clipped to 4× the block's robust spread (1.4826 × median absolute
deviation). Big excursions still count, which matters because the ringing follows exactly those, but a single gust
can't decide the block.

**No data, no decision.** If the block's corrections are too small to say anything (Σ|c| below a floor, e.g. a very
quiet mount), g is undefined and R stays.

**Significance.** The block is split into 5 sub-blocks of 20 frames, g is computed for each, and
SE = sd(gᵢ)/√5. This is robust to the autocorrelation of the products. R changes only if |g| > 2·SE.

**Update.**

    ln R ← ln R + κ·g,   κ = 0.5,   step limited to ±0.2 (about ±20 %),   R ∈ [0.3, 1.5]

- Near the optimum, g ≈ −β·ln(R/R*). If β ≈ 1, each block halves the distance to R*: from 1.0 to 0.7 in about
  3 blocks (15 min), without overshoot as long as κ·β < 1.
- β is an assumption. The guide log records (R, g) for every block (§5), so the live data measures it.
- 0.3 is the floor because a guider that corrects less than a third of the predicted error reacts too slowly to
  real jumps and drift changes, whatever g says; 1.5 is the existing `MaxResponse`.

**What R scales.** The whole output, as today: the predicted error including drift and periodic error, and also jumps
that are followed at once. The steady lag behind a constant drift is e* = (1 − R)/R × the drift per frame. At
R = 0.7 and 1″/min that's 0.02″, negligible. Scaling the jumps too is intended: RA's ringing follows exactly those.

**Start and keep.**
- R starts at the user's Response (default 1).
- It's kept across dithers and new guiding starts within the session.
- It starts over on a new binning, a new calibration or a reconnect, like the other learned state.
- It isn't persisted across app restarts in this step.

**Interaction with the filter bank.** The bank is told the applied correction, as today, so its model stays
consistent. There are now two adaptive loops: bank selection (120-frame memory) and R (100-frame blocks, gated by
significance). Their time scales are separated, but they can still couple. On Dec, for example, the bank's wander
estimate depends on how the corrections land. The live logs show both (bank takeovers and R changes), so coupling
would be visible.

**Settings.** Per axis for Predictive:
- *Response*: the starting value, 0.3–1.5, default 1;
- *Auto response*: on or off. On in test builds; the default is decided after the live tests.

Off gives today's behaviour exactly.

## 5. Observability

- **Guide log, per axis and block**, updated or not. For example:
  `INFO: Predictive Ra: response 1.00 -> 0.83 (g -0.36 ± 0.09 over 100 frames; lags -0.05/-0.29/-0.02)` or
  `INFO: Predictive Dec: response kept 1.12 (g +0.04 ± 0.08 over 100 frames)`.
- **Guide log, every settings change mid-session** as an INFO line. Today only the header at guiding start carries
  settings. On 2026-09-27 the switch of Dec to Resist Switch had to be inferred from the pulse duty.
- **Guide log header:** `Response = 1.00 (auto)`.
- **Engine state** (`PredictiveState`): response, auto on/off, and the last block's g and SE, for the TNS statistics
  card later.

## 6. Safety net in the simulator

Not for tuning to the rig; only to show that nothing breaks:
- **Existing presets, where Predictive's model is right** (good mount, high seeing, poor periodic error, Dec
  backlash, clouds) and the closed-loop suites: R stays within ±10 % of 1 and the results don't get worse.
- **Two generic plants** whose pulses move 1.3× and 0.7× the calibrated amount: R moves the right way (down, up)
  and settles without oscillating block to block.
- **Unit tests** of the statistic: g on synthetic sequences with known cross-correlation, run breaks, clipping,
  and the significance gate.

## 7. Live test

**Setup.**
- An image with the feature, with auto response switchable per axis in the settings.
- One target, one night, the usual exposure.
- Blocks of 20 minutes in the order A B A B: A = auto on, B = auto off with R = 1.
- Dithers either not at all inside blocks or on the same schedule in both.

**Per block** (a small fixed script on the guide log): RMS per axis, error autocorrelation at lags 1–4, g₁..g₃ and
their sum, the R trajectory, and the number of jumps.

**Expected if the theory holds.**
- With auto on, RA's R falls to about 0.6–0.8 and Dec's rises to about 1.2–1.4 within about 15 minutes.
- RA's g sum and lag-2 autocorrelation drop into the noise.
- Dec's lag-1 autocorrelation shrinks.
- RMS is not worse; RA is better if the ringing mattered.

**What would show it's wrong or not enough.**
- R swings from block to block (adaptation too fast, or coupled to the bank).
- g doesn't approach 0 as R changes (β ≈ 0: the error isn't gain-like, e.g. purely returning excursions). That's the
  signal for §8.
- The A blocks are worse than the B blocks.

The default, and whether §8 is needed, follow from that.

## 8. Later, only if the live results call for it

- **Returning excursions in the Kalman model** (an AR(1) state), so the filter corrects only what persists.
- **Exposure timing as a known input.** The guider knows each frame's request and arrival time, so it can spot the
  ASI120's early frames and tell the filter how much of the last correction such a frame caught.
- **The driver.** Find out why indi_asi_ccd / the ZWO SDK starts every second exposure early, and whether that can
  be switched off.
- **Classic algorithms.** Offer the same g check to them, as a Coach hint.

## 9. Open questions for the review

1. Default on or off once it works?
2. Keep R across app restarts (per profile, like the calibration), or learn it anew each night?
3. Blocks in frames (100) or in time (e.g. 5 min)? Frames keep the statistics comparable; time keeps the reaction
   time independent of the exposure.
4. Should R scale the drift and periodic-error prediction too, or only the part driven by recent errors?

## References

- K. J. Åström, *Introduction to Stochastic Control Theory*, Academic Press, 1970 (minimum-variance control).
- T. J. Harris, "Assessment of control loop performance", *Can. J. Chem. Eng.* 67 (1989) 856–861.
- K. J. Åström, B. Wittenmark, *Adaptive Control*, 2nd ed., Addison-Wesley, 1995 (MIT rule, time-scale separation).
- H. Hjalmarsson, M. Gevers, S. Gunnarsson, O. Lequin, "Iterative feedback tuning: theory and applications",
  *IEEE Control Systems Magazine* 18(4) (1998) 26–41.
