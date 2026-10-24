# Predictive: punishing crossings? — research note

Status: research, 2026-09-28. Nothing here is built or agreed. **Correction 2026-09-29:** the Dec pulse loss of §4 (a dead
band of about 31 ms per pulse plus half of the rest) is not confirmed. A fit that instruments the pulses with the dithers
finds no dead time on either axis, and a Dec pulse effect of 0.78 against PINS's calibration. See
[DEC-PULSE-MODEL.md](DEC-PULSE-MODEL.md) §2. The question: could the guide algorithm "punish"
crossings (sign changes) of the RA and Dec error, for example with another dimension in the Kalman filters, so that it
stops over-correcting? This note defines what a crossing can mean, measures the crossings of the 2026-09-27 night,
maps the idea onto estimator and control law, and ranks the sound formulations.
Related: [CORRECTION-PACE.md](CORRECTION-PACE.md) (the design built on it; an earlier response-adaptation proposal
mentioned below was superseded), [ALGORITHMS.md](../ALGORITHMS.md)
(Predictive), [NEXTGEN.md](NEXTGEN.md) (T3: the pulse-effect bank dimension that was tried).

Marks: **[C]** computed from the rig's data (quick Python on `NINA.GuideEngine.Test/Data/night-2026-09-27.csv`,
frames < 650 where both axes ran Predictive, and on the Coach report of 2026-09-23); **[D]** derived here and checked
numerically; **[L]** from the literature (see References); **[J]** judgement.

## 1. Verdict

- **Not as a penalty on crossings.** [D] In a minimum-variance loop the next error is independent of everything known
  when the correction goes out, so it ends up on the other side of zero exactly half the time, after the biggest
  corrections too. One half is the target, not zero. A penalty that lowers the crossing rate pushes towards
  under-correction, which is Dec's problem already. [C] It would also have missed tonight's RA problem: RA crossed
  like noise (0.52), Dec less than noise (0.42).
- **Sound as a statistic.** Crossings *in excess of one half after a correction*, above all after large ones, are a
  robust sign-based cousin of the design's g. [C] After the largest 10 % of RA corrections the error was on the other
  side two frames later in 72 % of the cases (±6 %).
- **Sound as estimator terms that remove the causes.** [C] A closed-loop likelihood fit of the night (§4) finds the
  causes of RA's over-correction in the estimator, not in the gain: the late part of corrections on the camera's
  early-start frames is corrected a second time, excursions that return by themselves are corrected as if they
  stayed, and one-frame spikes are followed at once as jumps. Dec's under-correction is a pulse loss (about 31 ms per
  pulse plus half of the rest), not a crossing problem.
- **As a control law it's a gain.** Every sound control-law form of "don't overshoot" (effort penalty, cautious
  control, a damping bound) reduces to one number per axis, the response. The response-adaptation design already
  adapts that number from the moment version of the same statistic. It subsumes the control-law side of the idea but
  not the estimator side.
- **Smallest sound step:** follow a jump only half at once and confirm on the next frame; feed the per-frame camera
  timing to the filter as a known input; keep the response adaptation, but take the jumps out of it and log the
  crossing statistics next to g. Next, if RA still over-corrects, an excursion (OU) state; for Dec the pulse model
  (NEXTGEN T3), measured from dithers rather than learned in the bank.
- **On average little is at stake.** [C] The best model found here would lower the measured RMS by at most 6 % (RA)
  and 4 % (Dec) tonight. RA's excess is concentrated: the 2 % of frames at and right after jumps hold 18 % of Σz².

## 2. What counts as a crossing

Per axis, z_k is the measured error at frame k and c_k the correction that went out after it (px as calibrated,
c > 0 reduces z > 0), the convention of `CorrectionApplied`.

| Name | Event | Used by |
|---|---|---|
| Plain crossing | sign z_{k+1} ≠ sign z_k | PHD2's oscillation index, 1 − same-side pairs/(n − 1) (`GuidingStatistics`; alerts above 0.6 and below 0.15, the Coach's live hints) |
| Lag-j crossing | sign z_{k+j} ≠ sign z_k | Kedem's higher-order crossings (the same count on filtered series) |
| Correction crossing X_j | sign z_{k+j} ≠ sign c_k: j frames after a correction the error is on the other side | this note |
| X_j after large corrections | the same for the largest \|c_k\| only (top 10 %, top 5 %) | this note |
| Reversal | sign c_k ≠ sign c_{k−1}: the *correction* changes direction | PHD2 Resist Switch (Dec backlash) |

**Theory.**

- [L][D] For a Gaussian stationary series with lag-j autocorrelation ρ_j, P(lag-j crossing) = arccos(ρ_j)/π, and
  E[sign z_k · sign z_{k+j}] = (2/π)·arcsin ρ_j (Sheppard's orthant probability; Kedem 1986). The oscillation index is
  therefore a function of ρ₁ alone. It can't see structure at other lags, nor effects carried by a few large events.
- [D] Minimum variance with each correction acting within one frame: z_{k+j} (j ≥ 1) is the unpredictable part, so it
  is independent of c_k and P(X_j | c_k) = ½ for every c_k, big or small (symmetric noise). Excess over ½ means
  over-correction, a deficit means under-correction. Lowering the crossing rate below ½ buys fewer crossings with
  more error.
- [D] **Sign version of g.** q_j = E[sign z_{k+j} · sign c_k] = (2/π)·arcsin ρ(z_{k+j}, c_k) for Gaussian data, so

      g_j ≈ sin(π·q_j/2) · σ_z/σ_c        (σ robust: 1.4826 × median absolute deviation)

  This is Blomqvist's quadrant test. It is robust to outliers, but on Gaussian data its asymptotic efficiency
  against the moment estimator is only 4/π² ≈ 0.41 (rank versions such as Spearman's: 9/π² ≈ 0.91) [L]. It needs
  about 2.5× the frames for the same power. A |c|-weighted form, Σ c_k·sign z_{k+j} / Σ |c_k|, is robust in z only
  and keeps the weight on the corrections that matter.
- [D] For Predictive in small signal, c_k ≈ K·z_k (§5), so g₁ = ρ₁/K and the oscillation index is arccos(K·g₁)/π:
  it carries the lag-1 part of g and nothing else. [C] This holds on Dec (ρ₁/K = 0.24, g₁ = 0.23) and only loosely on
  RA (−0.01 against −0.07: the jumps at gain 1 and the changing gain break c = K·z).

## 3. The night's crossings [C]

616 guided frames in 5 runs between dithers, settling excluded, Predictive on both axes, response 1. A rate of 0.5
over ~600 pairs has a standard error of ±0.02. SEs of g are from a block bootstrap with 20-frame blocks.

| | RA | Dec |
|---|---|---|
| Autocorrelation, lags 1–4 | −0.01, **−0.21**, +0.01, +0.01 | **+0.24**, +0.09, +0.01, −0.07 |
| g₁, g₂, g₃ ± SE (sum) | −0.07 ±0.04, **−0.30 ±0.06**, −0.00 (−0.38) | **+0.23 ±0.04**, +0.07 ±0.04, +0.00 (+0.30) |
| Plain crossings, lag 1 (Gaussian value from ρ₁) | 0.52 (0.50) | **0.42** (0.43) |
| Lag-2 crossings (Gaussian value from ρ₂) | 0.52 (**0.57**) | 0.46 (0.47) |
| X₁, X₂ over all corrections | 0.52, 0.54 | **0.41**, 0.46 |
| X₁, X₂ after the top 10 % of \|c\| (n ≈ 60, ±0.06) | 0.56, **0.72** | 0.40, 0.53 |
| X₁, X₂ after the top 5 % (n ≈ 32, ±0.09) | 0.61, **0.81** | 0.52, 0.45 |
| Sign version of g₁, g₂ (moment version) | −0.07 (−0.07), **−0.14 (−0.30)** | +0.28 (+0.23), +0.12 (+0.07) |
| Σz² in the jump frames and the 2 after | **18 %** in 2.1 % of the frames (the jump frames alone 10 %) | – (1 jump) |
| RMS reduction a delay-1 minimum-variance controller could reach (AR fit, Harris 1989) | 2–3 % | 3–5 % |

**What it says.**

- **RA's over-correction lives in the big events.** Plain crossings see nothing. The lag-2 crossing rate is 0.52
  where a Gaussian lag-2 correlation of −0.21 would give 0.57, and the sign version of g₂ finds only half of the
  moment version: a few large swings carry the anticorrelation. Half of g₂ comes from 10 frames (1.7 %), mostly after
  jumps. Without the jump windows g₂ is still −0.23, so a small-signal part remains. After large corrections the
  crossing statistic is sharp: 0.72 at lag 2 after the top 10 %, and after |z_k| > 2σ the error is on the other side
  two frames later in 76 % of 33 cases.
- **Dec's under-correction is gain-like.** Sign and moment versions agree (+0.28/+0.23), and the deficit of crossings
  is the same at all amplitudes except the largest 5 % of corrections.
- **A penalty on plain crossings** would have done nothing on RA (0.52 is noise), and one that aims below ½ would have
  rewarded Dec's lingering. A two-sided rule "crossing rate → ½" would have raised Dec's gain (right) and left RA alone
  (wrong: RA's problem is at lag 2 and in the tails).
- **Seeing-driven crossings are not the wrong reason.** A loop that chases seeing corrects noise that isn't there in
  the next frame, and the error reappears on the other side: that is over-correction, and g and the excess of X see it
  correctly. What must not be penalised is the baseline of ½ that every good loop has.
- **PHD2's crossing heuristic, observed.** From frame 651 Dec ran Resist Switch, which vetoes reversals. Pulses went
  out on 10 % of the frames, crossings fell to 0.35, and the errors lingered longer: autocorrelation +0.42, +0.25,
  +0.19, +0.15; g sum +0.48. The RMS can't be compared: the conditions changed (RA went from 0.231 to 0.277 px over the
  same time; the Dec/RA ratio stayed at 0.74–0.76).

## 4. Where the crossings come from: a closed-loop fit [C]

To separate the causes without a simulator, a state-space model per axis was fitted to the guided data by maximum
likelihood: a Kalman filter with the applied corrections as known inputs, 611 innovations per axis, and the pulse
effect α (the share of a calibrated correction that moves the mount) profiled on a grid. The candidate terms are the
proposals of §6; the log-likelihood says which ones the data support. A gain of more than 2 per added parameter is
significant at 5 %; terms without a free parameter need no test. The fit ranks hypotheses. None of the steps
recommended in §8 depends on a number fitted here.

RA, where the camera timing enters as the measured share v_k of each correction that the next frame caught (the
`visible_ra` column, from the PINS log):

| Model | Parameters added | log L | α̂ |
|---|---|---|---|
| Random-walk wander + drift + seeing, correction complete before the next frame (today's structure) | – | 44.8 | 0.80 |
| + camera timing: v_k·c_k lands in z_{k+1}, the rest in z_{k+2} | 0 | 60.9 | 1.20 |
| + returning excursions (OU state: size σ_s, time constant τ) | 2 | 59.6 | 0.90 |
| + both | 2 | **67.7** | **1.10** (95 %: about 0.9–1.35) |
| + α per direction, α on reversals, a dead band per pulse, or a further mechanical lag | 1 each | +0.0 … +0.5 | – |

Dec:

| Model | Parameters added | log L | α̂ |
|---|---|---|---|
| Today's structure | – | 228.6 | 0.70 |
| + camera timing | 0 | 228.0 | 0.50 |
| + OU state | 2 | 236.4 | 0.40 (95 %: up to about 0.55) |
| + OU + dead band per pulse | 3 | **240.6** | **0.49 beyond 0.074 px** (≈ 31 ms at 2.42 px/s) |
| + α per direction, α on reversals, a mechanical lag | 1 each | +0.1 … +0.4 | – |

Noise terms of the best models: RA excursions σ 0.24 px (0.75″) with τ ≈ 8 s (ρ ≈ 0.70 over a 2.8 s frame), the
random-walk wander goes to 0, seeing 0.084 px (0.26″); Dec excursions σ 0.17 px with τ ≈ 10 s (ρ ≈ 0.76), seeing
0.096 px (0.30″) against the bank's collapsed 0.03 px. The unguided Coach run of 2026-09-23 (1 s exposures at
1.0/1.4 s intervals) reproduces the residual autocorrelations quoted in the design (RA +0.51, +0.29, +0.06 after a
cubic trend; they depend on the detrending: +0.43, +0.18, −0.09 after a quintic). On that run the same comparison
favours an OU term on RA (ΔAIC −7.6, τ ≈ 1.7 s), not on Dec (−0.3). The time scale differs between nights and
exposure lengths, so a single OU state is an approximation.

**What it says.**

1. **RA: three estimator causes, all towards over-correction.** (a) On early-start frames, the late part of a
   correction first looks like a new error and is corrected again, so it arrives twice. The timing term alone gains
   16 in log-likelihood without a free parameter. Example: after frame 527, 0.29 px of the −1.16 px correction landed
   a frame late, frame 528 corrected −0.36 px on top, and frame 529 overshot to +0.56 px. (b) Excursions return:
   about 30 % of an excursion is gone by the next frame, but the filter corrects all of it. (c) The pulses may act
   about 10 % more than calibrated, but 1.0 is inside the interval.
2. **Dec: a pulse loss, not a delay, not reversals.** About 31 ms of each pulse does nothing, and about half of the
   rest acts. Reversals cost no more than pulses in the same direction, so backlash, the one physical "crossing cost",
   wasn't tonight's problem. The bank explains the unrealised corrections as wander: wander 30, gain 0.97, collapsed
   seeing, as NEXTGEN T3 found.
3. **The pulse effect is identifiable only together with the right noise model.** α̂ moves from 0.8 to 1.2 on RA and
   from 0.7 to 0.36 on Dec as the timing and excursion terms go in or out. This is the known bias of direct
   closed-loop identification under a wrong noise model (Forssell & Ljung 1999), and it explains why the bank's
   pulse-effect dimension drifted to its grid limit.
4. **How much is on the table.** The best model's one-step prediction error is 6.1 % (RA) and 3.9 % (Dec) below the
   measured RMS. That is what a delay-1 minimum-variance controller with that model could gain tonight; the model-free
   AR fit of §3 gives 2–3 % and 3–5 %. On RA most of it is at the jumps: without the jump frames and the two after,
   the RMS is 0.212 px instead of 0.231. The best model's RA innovations have a lag-2 autocorrelation of −0.06,
   against −0.18 for today's structure.

Caveat [J]: one night on one rig. The parameter values belong to this rig. What should carry over is the structure:
camera timing, returning excursions and the pulse effect matter, and must be estimated together.

## 5. Estimator or control law?

[L] With a known linear model and Gaussian noise, the optimal controller is a Kalman estimate followed by a
deterministic control law (separation and certainty equivalence: Åström 1970; the same split in adaptive optics:
Kulcsár et al. 2006). "Don't overshoot" doesn't appear as an objective of its own, because with the right model the
minimum-variance loop has no systematic overshoot (§2). Systematic excess crossings therefore mean one of:

- **a wrong model** (estimator): what the loop cancels isn't what happens next, e.g. returning motion, late landing
  or a wrong pulse effect;
- **a gain or delay problem** (control law): closed-loop poles negative, complex or close to the unit circle for the
  delay;
- **parameter uncertainty**: then separation fails (the dual effect, Bar-Shalom & Tse 1974), and the optimal law
  becomes cautious. This is the one sound place for a "punishment" in the control law.

[D] **What Predictive is in small signal.** After a full correction the filter's prior is 0, so at response R and
without drift its update is Ê_k = (1−K)(1−R)·Ê_{k−1} + K·z_k with c_k = R·Ê_k. At R = 1 that is proportional control
with the Kalman gain as loop gain, c_k = K₁·z_k, plus a small integral term from the drift state
(c_k = K₁·z_k + T·D_k with D_k = D_{k−1} + K₂·z_k, where K₂·T is at most 0.04 for the drift ratios in use). So the
bank's choice of wander ratio *is* the loop gain (wander 1 → 0.62, 3 → 0.79, 30 → 0.97), and R multiplies it.

The user's idea, mapped: a Kalman state can't punish anything, it can only estimate. A new dimension is sound when it
models a cause of the crossings (§6.1–6.3), and a category error when it encodes the wish not to cross (§6.6). A
selection criterion that scores crossings is a gain change in disguise (§6.7).

## 6. Options

Symptoms: **(a)** RA's lag-2 ringing after kicks, **(b)** Dec lingering, **(c)** Dec's gain 0.97 with collapsed
seeing.

### 6.1 Camera timing as a known input — option (i), a delayed-correction state

- **What.** The filter is told how much of the last correction the new frame could see. The prediction for z_{k+1}
  subtracts v_k·c_k, and (1 − v_k)·c_k is carried to the prediction for z_{k+2}: one pending number per axis, like
  `pendingCorrection` today, fed to both banks. v_k follows from the request, arrival and exposure times. When the
  timing is unknown, v = 1, which is today's behaviour. The periodic-error estimator's open-loop reconstruction
  (`openLoopPx`, which adds each correction at once) should use the same split.
- **Theory.** v_k is a measured input, not a parameter: no identifiability question and no bank dimension. It is the
  standard treatment of a known loop delay in the state; AO controllers carry their 1–2 frames of delay the same way
  (Kulcsár et al. 2006). With it, the design's "g = 0 is optimal" holds again (its §3 limit), up to the late share of
  the correction about to go out, which the filter only learns afterwards.
- **Evidence.** [C] +16 log-likelihood on RA at zero parameters; nothing on Dec.
- **Symptoms.** (a) Removes the second correction of the late part: in the frame-527 example the timing-aware filter
  would have sent about 0.2 px less at frame 528. (b), (c) no effect.
- **Risks.** A wrong v_k. A v_k biased low makes the filter wait for a correction that has already landed, so it
  under-corrects for a frame. Clamp v_k to [0.5, 1] and fall back to 1. The real cure is upstream: stop indi_asi_ccd
  from starting exposures early (design §8).
- **Complexity.** Small in the engine; needs per-frame timing from the camera layer.
- **Live test.** See §8, step 1.

### 6.2 Returning excursions — option (ii), an AR(1)/OU state

- **What.** Error = persistent part ℓ (wander, drift, periodic error) + excursion s with s' = ρ·s + w and
  ρ = e^(−T/τ). The correction cancels ℓ̂ + D̂·T + ρ·ŝ instead of ℓ̂ + ŝ. The bank gets the excursion's variance
  ratio and ρ as a dimension, or they replace part of the wander grid.
- **Theory.** Certainty-equivalent minimum variance for that disturbance, as in AO controllers that model each mode
  as AR(1) or AR(2) (Le Roux et al. 2004; Kulcsár et al. 2006; for vibrations, Petit et al. 2008).
- **Observability and identifiability.** ℓ and s are told apart only by their dynamics: s returns, ℓ doesn't. With
  ρ near 1 the excursion merges with the wander, with ρ near 0 with the seeing. [C] Offline, 600 frames identify it
  clearly (+15 on RA, +8 on Dec). A 120-frame score window holds only about 3 (RA) and 2 (Dec) log-likelihood units
  of that evidence, so the bank would flip between OU and random-walk models; this dimension needs a longer memory.
  A wrong input model (timing, α) also biases ρ.
- **Symptoms.** (a) The small-signal response to a new excursion drops to about ρ ≈ 0.7 on RA, while drift and
  periodic error are still corrected in full. This is the targeted form of what the response adaptation does
  globally. Spikes are partly discounted too. (b), (c) little on its own.
- **Risks.** A ρ that is too low under-corrects persistent wander (lingering). It overlaps with the response
  adaptation, since both lower RA's gain; two adaptive loops on one quantity must not run unpinned at the same time.
- **Complexity.** Medium: 3-state filters in both banks, a new bank dimension, and the jump logic must decide which
  state takes a jump.
- **Live test.** After §8 steps 1–2, with R pinned at 1. Success: RA's g sum within 2 SE of 0 without R's help, and
  the chosen ρ stable over the night. Failure: ρ wanders across the grid, or Dec's g₁ rises.

### 6.3 Pulse effect — option (iii)

- **Evidence.** [C] RA α ≈ 1.1 (0.9–1.35); Dec 0.4–0.5 plus a dead band of about 31 ms per pulse. After the five
  dithers with a Dec step of 1–5 px, the first Dec settling correction moved 22–74 % of its calibrated amount by the
  next frame, and no more by the frame after. Four of the five pulses were over before the next exposure began, so
  this is a loss, not timing. Informative, but each event is noisy against 0.2 px of seeing and excursions.
- **Closed-loop identifiability.** [L] In closed loop the corrections are functions of the same errors. The direct
  prediction-error method is then consistent only with a correct noise model; otherwise the plant estimate is biased,
  the more so the less external excitation there is (Forssell & Ljung 1999). [C] Tonight α̂ moved by ±40 % with the
  noise model, and the bank dimension drifting to its limit is the same effect. External excitation fixes it: dither
  settling steps (known steps of 1–5 px, about 7 per hour, today left out of all learning) and the Coach's pulse tests.
- **Proposal.** Keep it out of the bank. Estimate α, and on Dec the dead band, from dither responses and the Coach as a
  slow calibration check, and apply it in the pulse layer: NEXTGEN T3, a stiction offset added to each pulse rather
  than every pulse scaled by 1/α. If α is uncertain, correct cautiously [D][L] (Åström & Wittenmark 1995;
  Wittenmark 1995):

      c = α̂ · p / (α̂² + Var α)        (p: predicted error; also right for a pulse effect varying pulse to pulse)

- **Symptoms.** (b) and (c) on Dec: the physical fix. With the correction model right, the bank would leave wander 30
  and the seeing estimate would recover. (a) RA: small.
- **Risks.** A wrong α is the worst failure of all options: too low and the loop oscillates (T3 needed a cap of 1.3
  on gain × lengthening against divergence).

### 6.4 Jump handling

- **Today.** A measurement beyond 4σ of the prediction is followed at once (gain about 1).
- [C] **What tonight's RA jumps were.** From the open-loop motion (the measured error plus the corrections landed,
  with the timing split), for the six clean jump events of the night, the share of the jump still present one frame
  later was 0.50, 0.23, 0.59, −0.02, 0.63, 0.57 (mean 0.42), and two frames later 0.33 on average. Frame 410 was a
  step, frames 330 and 526 were spikes that returned within one frame. The jump note at 527 was the guider's own
  overshoot after it followed the 526 spike in full. (The note at 426 is left out: a dither follows it.)
- [D] **Theory.** For a mixture of steps and spikes, the minimum-variance follow is the expected persistence p̄, not
  1: the next-frame error is (p − f)·J for a follow f. Without knowledge of p̄, f = ½ minimises the worst case (J/2
  either way). [C] Tonight's six events give a next-frame mean square of 0.39 J² for f = 1 and 0.06 J² for f = ½; two
  frames later 0.54 J² and 0.12 J². That is six events, so read it as a direction, not as a size. The principled
  versions are a generalised likelihood-ratio test for jumps (Willsky & Jones 1976) or an interacting multiple model
  with a step and a spike hypothesis (Blom & Bar-Shalom 1988); the rule below is the minimal form.
- **Proposal.** Follow half the jump at once (gain ½ instead of 1 in the jump update). The next frame's normal update
  picks up whatever persisted, at the filter's gain. Don't scale the jump follow with the response (§7). Holding the
  drift after a jump isn't needed: [C] K₂·T ≤ 0.04 for the models in use.
- **Symptoms.** (a) Directly: 18 % of RA's Σz² lies at and right after jumps. (b), (c) no effect; Dec had one jump.
- **Risks.** A real step (a cable snag, a bump) is corrected over about three frames instead of one: J/2 is left
  after the first, about 0.15 J after the second (at the filter's gain of about 0.7). On a mount whose jumps are all
  steps that costs; the post-jump statistics in the log show which kind a rig has.
- **Complexity.** Small.
- **Live test.** See §8, step 1.

### 6.5 Control-law penalties

- **Effort penalty** (LQ cost E Σ(x² + λ·c²)). [D] For the one-frame integrator the gain is P/(P + λ) with
  P = (1 + √(1 + 4λ))/2, i.e. a response below 1 and nothing else. With the right model λ = 0 is optimal.
- **Penalty on changes of the correction** (PHD2's Hysteresis blends with the last output): a low-pass on the
  corrections. It adds phase lag to a loop whose ringing comes from lag, so it is the wrong medicine.
- **Cautious control** (§6.3): the principled reason for a gain below the certainty-equivalent one. It needs α and
  its uncertainty.
- **"No crossing" as a (chance) constraint, MPC style.** [D] Requiring P(the next error crosses zero) ≤ ε, with the
  next error ~ N(p − α·c, σ²), gives c ≤ (p − z_{1−ε}·σ)/α. That is a dead band of z_{1−ε}·σ on the estimate, i.e.
  min-move again; ε = ½ recovers minimum variance. ALGORITHMS.md already measured a dead band on the filtered estimate:
  0.44″ against 0.16″. Only a crossing with a physical cost deserves a constraint (a Dec reversal with backlash), and
  then as a pulse model, not as a sign rule (Farina et al. 2016 for chance-constrained MPC).
- **Pole placement and the no-ringing condition.** [D] Let a share φ of each correction land one frame late, and let
  L = α·R·K be the loop gain:

      plant, per frame:   x_{k+1} = x_k − α·[(1−φ)·c_k + φ·c_{k−1}] + w_k
      Predictive:         c_k = R·Ê_k,   Ê_k = a·Ê_{k−1} + K·z_k,   a = (1−K)(1−R)
      closed loop:        z² − (1 + a − L(1−φ))·z + (a + L·φ) = 0
      at R = 1:           z² − (1 − L(1−φ))·z + L·φ = 0;   two real positive poles  ⇔  L ≤ 1/(1 + √φ)²

  Tonight's numbers [C]: φ averaged 0.083 on RA (0.18 on the early-start frames, 45 % of them) and 0.069 on Dec.

  | Case | Loop gain L | No-ringing bound | Poles |
  |---|---|---|---|
  | RA small signal, K 0.62–0.79, φ 0.083 | 0.62–0.79 | 0.60 | complex, radius 0.23–0.26 (period 20 → 6 frames) |
  | RA on an early-start frame, φ 0.18 | 0.62–0.79 | 0.49 | complex, radius 0.33–0.38 |
  | RA jump followed at gain ≈ 1, φ 0.25 | 0.99 | 0.44 | complex, **radius 0.50, period 4.8 frames** |
  | Dec, α 0.4, K 0.97 | 0.39 | 0.63 | real, 0.59 and 0.05 (lingering) |
  | Dec, α 0.4, K 0.97, R 1.5 | 0.58 | 0.63 | real, 0.38 and 0.07 |

  RA's small-signal loop is only slightly underdamped: its oscillation dies within one or two frames and can't make
  the slow swings seen after kicks. A jump followed at gain 1 with a quarter of it landing late rings with a period of
  about 5 frames, close to the observed 4. The swings after the 526 spike decayed more slowly still, because the
  open-loop motion itself bounced (+0.41 px from frame 528 to 529) and the spike's return was followed at gain 1 again.
  The ringing is a jump and timing effect (§6.1, §6.4), not one of the steady gain. Critically damped is not optimal
  either, since minimum variance tolerates slight ringing, so the bound is a sanity line for the log rather than a cap
  on R.

Verdict: every sound control-law penalty is a scalar gain per axis. The response adaptation adapts exactly that from
data, so it subsumes this group.

### 6.6 "No crossing" in the estimator (constrained or pseudo-measurement filtering)

[L][J] Constrained Kalman filtering (Simon 2010) imposes what is known about the *state*, such as physical limits or
conservation laws, by projection, truncation or pseudo-measurements. "The error keeps its sign" is not a property of
the mount but a wish about the controlled future, and a good loop violates it half the time. As a pseudo-measurement
it would pull every estimate towards the previous side. The filter would stop seeing its own overshoots, so the next
correction comes late; its innovations would no longer be white; and the bank's scores and the seeing estimate built
on them would break. It is a category error. The legitimate constrained states here are physical ones: the Dec gear
inside its backlash slack, or a pulse below the minimum doing nothing.

### 6.7 Bank selection with a closed-loop cost

- [D] At R = 1 the model in use has a prior of 0 after each correction, so its innovation *is* the next measured
  error, and its score already is the closed-loop mean square error. The other filters score what they would have
  predicted on data made by another loop. Their own closed-loop cost can't be observed without running them, or
  without the plant model, which is the unknown.
- Penalising candidates "whose closed loop would ring" depends only on their gain (given α and φ). It is a bias
  towards low gain, a response reduction in disguise, and it also moves the seeing and wander estimates, the 4σ jump
  threshold and the frame weighting.
- [J] Keep the prediction-error criterion: with the right model class it is the right one for cancelling the next
  error (on matching the identification criterion to the use of the model, see Gevers 2005). Widen the model class
  instead (§6.1, §6.2). The data-driven closed-loop criterion that does exist, g of the model in use, is the one the
  response adaptation uses.

### 6.8 Other terms — option (iv)

- **Jump hypothesis** (step or spike, §6.4): the only one with a clear case tonight.
- **Backlash as a hybrid Dec state** (position inside the slack): the physically meaningful crossing, of the
  correction's direction rather than of the error. [C] Reversals cost nothing extra tonight (+0.06). Dec guide mode
  Drift and backlash compensation already cover it.
- **Exposure averaging.** The measurement is the mean position over a 2.5 s exposure, not a point; for a random walk,
  first differences of such means have a lag-1 correlation of ¼ (Working 1960). This is a second-order effect for
  the prediction; not recommended now.
- **α per direction:** [C] not supported tonight (+0.4).

### 6.9 Prior art

- **PHD2.** All its heuristics act on the raw offset without a model: Resist Switch (vetoes reversals unless the last
  10 frames make a case; built for Dec backlash), Hysteresis (a penalty on changes of the correction), min-move (a dead
  band, i.e. the chance constraint above), and the oscillation index (lag-1 crossings; PHD2's guidance reads values
  near 0 as under-correction and near 1 as over-correction). They suit proportional control of the raw offset, where
  lag-1 whiteness is most of the story. Predictive replaces them with a model.
- **Adaptive optics.** Optimised modal gains (Gendron & Léna 1994) choose each mode's integrator gain from the
  closed-loop residual spectrum and the known loop transfer function, delay included. That is the direct analogue of
  the response adaptation (per axis, from closed-loop data), but model-based: it reconstructs the pseudo-open-loop
  disturbance with the known actuator gain and delay. LQG controllers (Le Roux et al. 2004; Kulcsár et al. 2006;
  Petit et al. 2008) carry the delay as state and model the disturbance as AR processes, which is §6.1 and §6.2.
  [J] AO's experience matches §4: model-based control pays most for predictable disturbances (vibrations; here late
  landing and jumps), while against turbulence-like noise a well-tuned integrator is close to it.

### 6.10 Ranking

| Rank | Option | (a) RA ringing | (b) Dec lingering | (c) Dec gain 0.97 | Soundness | Main risk | Effort |
|---|---|---|---|---|---|---|---|
| 1 | Jump follow ½, confirm on the next frame, not scaled by R (§6.4) | main part | – | – | minimum variance for a step/spike mixture; minimax ½ | a real bump corrected one frame later | S |
| 2 | Camera timing as a known input (§6.1) | late-part share | – | – | measured input, nothing to identify | wrong timing (clamp, fall back to v = 1) | S engine, M plumbing |
| 3 | Response adaptation, with the §7 changes | small-signal part | yes (R up) | no | MIT rule and minimum variance; subsumes all control-law penalties | coupling with the bank, slow | M (designed) |
| 4 | OU excursion state (§6.2) | small signal, spikes | little | little | minimum variance for an AR(1) disturbance; AO LQG | ρ poorly identified online; overlaps R | M |
| 5 | Dec pulse model from dithers and the Coach (§6.3, T3) | – | yes (physical) | yes | indirect adaptive control with excitation | wrong α makes the loop oscillate | M |
| – | Crossing penalty in filter, bank or constraint; penalty on correction changes; damping cap on R | – | worse | – | not sound | – | – |

## 7. Relation to the response-adaptation design

- **It subsumes the control-law side.** Every sound control-law penalty is a scalar gain per axis (§6.5). The design
  tunes that scalar from g, the moment version of "excess crossings after a correction". A crossing penalty adds
  nothing to it.
- **It doesn't replace the estimator side.** Timing, returning excursions and spikes change *which part* of the error
  is corrected; R can only trade them off globally. On RA, R ≈ 0.7 would also scale the drift and periodic-error
  corrections and the jump follow. With §6.1 and §6.4 in place, the remaining g should be smaller and R should stay
  nearer 1.
- **Suggested changes** [J]:
  1. **Take the jumps out of R.** The design scales followed jumps with R on purpose. With §6.4, the jump logic sets
     the follow and R stays a small-signal gain that a handful of events can't drag: tonight half of g₂ came from
     10 frames. Compute g without the frames k within two frames of a jump, and keep the post-jump statistics
     separately for the jump follow.
  2. **Timing first, or in the same image.** Otherwise R learns to make up for the camera, and is wrong once the
     driver or the camera changes.
  3. **Log the crossing statistics per block, next to g:** X₂ after the top 10 % of corrections, the sign version of
     each g_j, the number of jumps, "return jumps" (an opposite-sign jump within two frames of a followed one) and the
     post-jump share of Σz². Sign and moment versions that agree indicate a gain-like error (Dec tonight,
     +0.28/+0.23), where R is the right tool. A sign version far below the moment version indicates an event-driven
     error (RA tonight, −0.14/−0.30), where R is the wrong tool.
  4. **Keep the 4×MAD clipping.** [C] It keeps RA's signal: g sum −0.38 raw and −0.35 clipped; per 100-frame block
     after the learning phase −0.42, −0.64, −0.50, −0.58, −0.29 raw and within 0.07 of that clipped.
  5. **Use the other axis as a covariate for RMS** in per-axis A/B tests. [C] RMS varies by 11–20 % from block to
     block, more than anything at stake here (≤ 6 %).
- **Dec.** R going up is the right direction, and a crossing penalty would push Dec the wrong way. The physical fix is
  the pulse loss (§6.3).

## 8. Recommendation and live tests

**Is punishing crossings a good idea?** Not as a penalty on sign changes of the error: not in the filter, not in the
bank's selection, not as a constraint. The sound formulations are (1) crossings in excess of ½ after a correction, as
a statistic, which is g in sign form and belongs in the log; (2) the causes of the crossings, as estimator terms; and
(3) the gain, which the response adaptation already adapts two-sidedly towards g = 0.

**Smallest sound step.** Jump follow ½ with confirmation, and the camera timing as a known input, both on RA. Neither
adds a learned parameter; both can be switched per axis. Then the response adaptation with the §7 changes. Then,
only if RA's g stays negative with R pinned, the OU dimension. For Dec, the pulse model from dither and Coach
measurements; meanwhile the response adaptation raises Dec's R.

**Live plan.** One change at a time, per axis, with the other axis as covariate; ABAB blocks of 20 minutes, as in the
design. [C] Per block: SE of g₂ ≈ 0.07; RA had about 12 jump notes per hour, so the jump statistics are pooled over
nights.

| Step | A vs B | Primary endpoints | Success if theory holds | Failure, and what it means |
|---|---|---|---|---|
| 1 | RA: jump follow ½ + timing input off/on; auto response off | RA g₂; X₂ after the top 10 %; return jumps; post-jump share of Σz² | g₂ moves towards 0 by ≥ 2 SE of the pooled A−B difference (about 0.1–0.15); X₂ (top 10 %) from 0.72 towards ≤ 0.6; return jumps disappear; post-jump share clearly below 18 % | g₂ unchanged: timing and jumps aren't the driver, and the excursion term (§6.2) gains weight. RA g₁ rising above +0.1: the half follow is too timid |
| 2 | Auto response on both axes, with step 1 on | R trajectory and g per axis, as in the design | Dec's R rises and its g₁ shrinks; RA's R settles with g within 2 SE of 0 (below 1 is expected: the excursions of §4 argue for it) | R swings from block to block: coupling with the bank. RA's R at 0.6 or below with g still negative: the excursions dominate, go to step 3 |
| 3 | RA: OU dimension, R pinned at 1 | g sum; stability of the chosen ρ; Dec unaffected | g sum within 2 SE of 0; ρ stable over the night | ρ flips between takeovers; lingering on RA |
| Dec | Pulse model from dither and Coach measurements (T3) | Dec g₁; the bank's wander ratio and seeing | g₁ within 2 SE of 0; wander leaves 30; seeing near RA's | reversals or oscillation: α wrong, back off |

RMS guard for every step: over the ABAB pairs, the B/A RMS ratio of the changed axis, divided by that of the unchanged
axis, must not exceed about 1.05 in both pairs. [J] RMS is a guard, not the goal: tonight's bound on the whole gain
is about 6 %.

## References

- K. J. Åström, *Introduction to Stochastic Control Theory*, Academic Press, 1970 (minimum variance, separation).
- K. J. Åström, B. Wittenmark, *Adaptive Control*, 2nd ed., Addison-Wesley, 1995 (cautious control, certainty
  equivalence, MIT rule).
- Y. Bar-Shalom, E. Tse, "Dual effect, certainty equivalence, and separation in stochastic control", *IEEE Trans.
  Automatic Control* 19(5) (1974) 494–500.
- H. A. P. Blom, Y. Bar-Shalom, "The interacting multiple model algorithm for systems with Markovian switching
  coefficients", *IEEE Trans. Automatic Control* 33(8) (1988) 780–783.
- N. Blomqvist, "On a measure of dependence between two random variables", *Ann. Math. Statist.* 21 (1950) 593–600
  (quadrant test; its efficiency 4/π² against the correlation test: H. S. Konijn, *Ann. Math. Statist.* 27 (1956)
  300–323).
- M. Farina, L. Giulioni, R. Scattolini, "Stochastic linear Model Predictive Control with chance constraints – A
  review", *J. Process Control* 44 (2016) 53–67.
- U. Forssell, L. Ljung, "Closed-loop identification revisited", *Automatica* 35(7) (1999) 1215–1241.
- E. Gendron, P. Léna, "Astronomical adaptive optics. I. Modal control optimization", *A&A* 291 (1994) 337–347.
- M. Gevers, "Identification for control: from the early achievements to the revival of experiment design",
  *European Journal of Control* 11 (2005).
- T. J. Harris, "Assessment of control loop performance", *Can. J. Chem. Eng.* 67 (1989) 856–861.
- B. Kedem, "Spectral analysis and discrimination by zero-crossings", *Proc. IEEE* 74(11) (1986) 1477–1493.
- C. Kulcsár, H.-F. Raynaud, C. Petit, J.-M. Conan, P. Viaris de Lesegno, "Optimal control, observers and integrators
  in adaptive optics", *Optics Express* 14(17) (2006) 7464–7476.
- B. Le Roux, J.-M. Conan, C. Kulcsár, H.-F. Raynaud, L. M. Mugnier, T. Fusco, "Optimal control law for classical and
  multiconjugate adaptive optics", *JOSA A* 21(7) (2004) 1261–1276.
- C. Petit, J.-M. Conan, C. Kulcsár, H.-F. Raynaud, T. Fusco, "First laboratory validation of vibration filtering
  with LQG control law for adaptive optics", *Optics Express* 16(1) (2008) 87–97.
- W. F. Sheppard, "On the application of the theory of error to cases of normal distribution and normal
  correlation", *Phil. Trans. R. Soc. A* 192 (1899) 101–167 (orthant probability).
- D. Simon, "Kalman filtering with state constraints: a survey of linear and nonlinear algorithms", *IET Control
  Theory & Applications* 4(8) (2010) 1303–1318.
- A. S. Willsky, H. L. Jones, "A generalized likelihood ratio approach to the detection and estimation of jumps in
  linear systems", *IEEE Trans. Automatic Control* 21(1) (1976) 108–112.
- B. Wittenmark, "Adaptive dual control methods: an overview", 5th IFAC Symposium on Adaptive Systems in Control and
  Signal Processing, *IFAC Proceedings* 28(13) (1995).
- H. Working, "Note on the correlation of first differences of averages in a random chain", *Econometrica* 28(4)
  (1960) 916–918.
- PHD2 documentation, "Analyzing PHD2 Guiding Results" (openphdguiding.org), for the oscillation index.
