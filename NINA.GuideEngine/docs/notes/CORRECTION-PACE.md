# Predictive: correction pace — design

Status: implemented 2026-09-28 (branch predictive/correction-pace); the live test in §6 is pending. It supersedes an
earlier proposal to adapt the response automatically, whose review found the statistic wrong and the payoff too small.
Background analysis: [CROSSING-PENALTY.md](CROSSING-PENALTY.md). As built: [ALGORITHMS.md](../ALGORITHMS.md), "Correction
pace".

## 1. What we want

A guider that brings an error down over 2–3 frames instead of cancelling it completely in one. It shouldn't swing
through zero after big corrections, and its pulses should be moderate, not a full correction every frame. It should
be simple: one number per axis that means what it says.

## 2. Why the current behaviour overshoots

**What Predictive does today.** It estimates the error at the next frame and sends all of it: the position estimate
E plus the drift and periodic-error prediction. A sudden excursion beyond 4σ is followed at once, so the whole
excursion is corrected in one frame.

**What the rig showed** (2026-09-27; numbers from the log, see CROSSING-PENALTY.md):
- **After RA's biggest 10 % of corrections**, the error was on the other side of zero two frames later in 73 ± 6 % of
  cases. A well-tuned loop gives 50 %, because seeing makes the next measurement land on either side at random.
- **RA's kicks** (sudden ~3″ excursions) and the frames right after them are 5 % of all frames but carry 27 % of
  RA's squared error.
- **The kicks are only about half persistent**: about half is still there a frame later (6 clean events, so this
  shows a direction, not a size).
- **Part of each correction lands a frame late.** The ASI120 starts every second exposure early, before the
  guider's pulses, so a late share φ of up to about 0.25 lands in the frame after.
- **Pulses move 0.7–1.3× the calibrated amount,** depending on axis and situation.

**Loop gain** L is the fraction of an error removed per frame: L = α·λ·K, with α the pulse effect, λ the pace (§3,
1 today) and K the filter's gain (1 for a followed jump).

**No-ringing condition** (derived in CROSSING-PENALTY.md, re-checked): the error settles without swinging through
zero only while

    L ≤ 1 / (1 + √φ)²        (0.60 at φ = 0.08, 0.44 at φ = 0.25)

- **A jump followed in full** has L = α ≈ 0.7–1.3. That's above the bound, so it rings: a period of about 4.8 frames
  at φ = 0.25, close to the ~4 frames seen on the graph.
- **With pace 0.5** it's L = 0.35–0.65, at or below the bound across the whole range.

## 3. The rule

The correction sent after frame k becomes

    c_k = λ · E_k  +  D·T  +  ΔPE

- **λ, the pace (0 < λ ≤ 1), applies to the position estimate only.** Without new information the remaining error
  shrinks by (1 − λ) each frame. At λ = 0.5 it's 50 % after one frame, 75 % after two and 88 % after three.
- **Drift (D·T) and the periodic-error step (ΔPE) are sent in full.** They are predictable, so slowing them would only
  add a lag. The drift estimate converges to the commanded drift whatever the pace.
- **The filter is unchanged.** It still follows a jump at once in its estimate, because it's told the applied
  correction; only the correction is paced. If a kick returns by itself, the next frames see that, and the rest of it
  is never corrected. So the overshoot is at most λ × the kick.
- **Small errors in the noise** are corrected even more gently (L = α·λ·K with K < 1). That's intended: they're mostly
  seeing.

**Noise-free check** (φ = 0.2, α = 1, kick of size 1 at frame 1; measured error at frames 1–6; Σz² from frame 2 on):

| Case | Today (λ = 1) | λ = 0.6 | λ = 0.5 |
|---|---|---|---|
| Step that stays | +1.00 +0.20 −0.16 −0.07 +0.02 +0.02 (0.07) | +1.00 +0.52 +0.15 +0.02 −0.01 −0.01 (0.29) | +1.00 +0.60 +0.26 +0.10 +0.03 +0.01 (0.44) |
| One-frame spike | +1.00 −0.80 −0.36 +0.09 +0.09 +0.00 (0.79) | +1.00 −0.48 −0.37 −0.13 −0.03 +0.00 (0.39) | +1.00 −0.40 −0.34 −0.16 −0.06 −0.02 (0.31) |
| Kick, half returns | +1.00 −0.30 −0.26 +0.01 +0.05 +0.01 (0.16) | +1.00 +0.02 −0.11 −0.06 −0.02 −0.00 (0.02) | +1.00 +0.10 −0.04 −0.03 −0.02 −0.01 (0.01) |

**What it costs.**
- An error that really stays takes 2–3 frames to remove instead of one.
- On the guide RMS the effect is small either way. On the night analysed, no retuning of the loop gain could have
  changed RMS by more than about 3 %, because only 5.4 % of the error was predictable from its own past.
- What changes is the shape: no ringing, overshoot after kicks at most halved, and pulses about half as long.
- Robustness: the loop stays calm for pulse effects and late shares across the whole range measured, where today's
  loop is at the edge.

**What it doesn't change.**
- Predictive still sends a pulse on most frames. They're smaller, not fewer; pulses under 10 ms are already dropped
  (minimum pulse 20 ms).
- Fewer pulses would need a dead band. Every dead band lets the error creep up to its size: 0.44″ instead of 0.16″ in
  the Predictive simulation with the PHD2 min move. So it's left out; §7.

## 4. Settings and code

- **Setting:** "Correction pace" per axis for Predictive, 0.2–1.0, default 0.5 in the plugin. It's the fraction of
  the remaining (estimated) error corrected per frame. 1.0 is exactly today's behaviour.
- **Engine:**
  - In `PredictiveAlgorithm`, the output becomes λ·E + D·T + ΔPE, where today it's response × (E + D·T + ΔPE).
  - Today's `response` parameter (not exposed in the plugin; nothing else sets it) is replaced by the pace.
  - The engine default stays 1, so the existing simulator results in ALGORITHMS.md stay comparable.
- **Minimum move** stays as it is: none for Predictive.

## 5. Observability

- **Guide log header:** `Correction pace = 0.50`.
- **Guide log, every settings change during a session:** each guide-log header line that changed, as PHD2's
  `INFO: Guiding parameter change, <line>` (e.g. `…, Y guide algorithm = Predictive, Correction pace = 0.50, …`).
  Today only the header carries settings; on 2026-09-27 a Dec algorithm switch had to be inferred from the pulses.
- **Nothing else in the guider.** The statistics below are computed offline from the guide log, which already holds
  every frame's offsets and pulses, with one small script kept in the repo (`tools/guide-log-stats`).

## 6. Test

**Simulator, as a safety net only.** The existing presets and closed-loop suites at λ = 1 must be unchanged. At
λ = 0.5 they report the true RMS, and nothing may get worse by more than 10 %.

**Unit tests.** The output rule itself (λ scales E only; drift and periodic-error step in full), and the parameter
plumbing.

**Live, one night, one target.**
- Blocks of 20 min in the order A B B A: A = λ 1.0 (today), B = λ 0.5.
- Dithers as usual, the same in all blocks. The settings changes are in the guide log.
- Judged by numbers fixed in advance, from the offline script:

| What | Expected with λ 0.5 | Why it's measurable |
|---|---|---|
| RA: share on the other side of zero two frames after the top 10 % of corrections | from ~73 % down to ~50–55 % | about 40 events per block, SE about 5 % per arm |
| RA: share of Σz² in the 4 frames after a jump | clearly lower (27 % tonight) | few events per night; accumulate over nights |
| Pulse length, median and 90th percentile, per axis | about half | trivially measurable |
| Dec: share of pulses that reverse direction | lower | about 400 pulses per block |
| RMS per axis | not worse: upper 95 % bound of the B/A ratio below 1.10 | a guard only; blocks vary 6–20 % by themselves |

- **If B is clearly calmer and RMS isn't worse,** 0.5 stays the plugin default. If not, the default goes back to 1
  until the cause is understood.
- **If RA lingers noticeably** (positive lag-1 autocorrelation growing by more than about 0.15 and RMS up), try λ 0.6
  or 0.7.
- **Dec already under-corrects** (its pulses move about 0.7×), so its effective pace is lower. If Dec's RMS rises,
  Dec gets a higher pace than RA.

## 7. Later, only if the live results call for it

- **A noise-aware dead band,** if fewer pulses matter more than accuracy.
- **Camera timing as a known input**, since the guider can see which frames started early. It would allow a pace
  closer to 1 without ringing.
- **A Dec pulse model** (dead band and scale) measured from dither settles, for Dec's weak pulses. Built 2026-09-29 as the
  pulse model, per axis and without a dead band (there is none): [DEC-PULSE-MODEL.md](DEC-PULSE-MODEL.md).
- **Automatic tuning of the pace,** calming only, from the lag-1 gradient described in the review of
  RESPONSE-ADAPTATION.md.
