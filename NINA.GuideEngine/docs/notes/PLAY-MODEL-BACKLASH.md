# Dec backlash as a play model

Status: implemented 2026-10-13 (branch `feat/guider-play-backlash`), Dec backlash model `PlayModel` (default `Fixed`).
Code: `PlayModelBacklash`, `ReversalGate`, `IBacklashCompensation`; tests `PlayModelBacklashTests`, `ReversalGateTests`,
`PlayModelBacklashLoopTests` (closed loop, `DecPlayLoop`).

## 1. Why: the guide log of 2026-10-12

OnStepX, 200 mm, 2.99″/px, Dec rate 3.09 px/s, Predictive on both axes, 1 s exposures, 21:31–03:56 with PHD2's
backlash compensation (port in `BacklashCompensation`) at 900 ms, adjusted down to 521 ms in the last hour.

| Regime | Dec RMS | Reversals/h | Dec pulse time/h |
|---|---|---|---|
| compensation off (20:51) | 0.52″ | 205 | 48 s |
| 900 ms (21:31–03:56) | 0.61″ | 202 | 328 s |
| 2000 / 2330 ms | 0.73–1.67″ | 225–351 | 117–136 s |
| last hour (≈ 1000 → 521 ms) | 0.97″ | 209 | 340 s |

- **The compensation pulses did not move the star.** After a compensated reversal (900–1000 ms, or 521 ms) the star
  moved about 0.05 px in the first frame and ±0.1 px after ten frames, with a median of 1.5 s of motor time sent in the
  new direction.
- **The reversals were noise.** Before a reversal the offset was 0.0–0.2 px (≤ 0.6″), within the seeing; 43 % of the
  compensated reversals were followed by another one within three frames.
- **The gear never engaged.** Between reversals about 1.6 s of motor time went out, less than the play; the Dec effect
  (star motion over commanded motion) was 0.02 right after a reversal and 0.15–0.25 after seconds of travel. The gear
  wandered inside its play, and Dec was good only because there was almost no Dec drift (North 172 s, South 167 s of
  pulses per hour).
- **The play is soft, large and depends on the history.** The calibration at 20:13 shows it: the backlash clearing
  moved 0.7 → 2.3 → 4.9 px per step, the South return after the North leg 2.5 / 1.5 / 2.5 px where the North steps moved
  4.9 px. After long moves 1000–1200 ms were lost. Same as `COACH-SUGGESTIONS.md` (2026-09-29).

- **The Dec error wandered, uncorrected.** Between dithers the Dec offset had σ 0.21 px with a lag-1 autocorrelation of
  0.84 (0.45 after ten frames; RA 0.20 and 0.02): a slow motion of about 8 s that the guiding could have corrected, but
  its pulses ended in the play. The frame-to-frame noise was only 0.08 px.
- **The play changed with the target.** Replaying the log into the model (`PlayModelGuideLogReplay`): after the
  calibration at 20:13 (20:15–20:51) pulses of 2 s moved the star a lot (Dec RMS 1.67″ with 2000 ms of compensation)
  and the model learns about 2 s of play, while from 21:31 (pier East, Dec +57.6°) even 1.5 s did not move it and the
  model only learns that the play is wider than every travel made. A gear that the load keeps on one flank on one target
  and lets float on another fits both.

A fixed pulse at every reversal can't work on such a mount: after noise reversals it is too short to put the gear
against a flank, after long moves it is arbitrary, and it multiplies the pulse time without moving the star. The guider
needs to know **where the gear is inside its play**, and **when a pulse that has to cross the play is worth it**.

## 2. Model

**Play state.** The motion of the star for a motor travel u (ms at the guide rate) is a Prandtl-Ishlinskii superposition
of play operators, y = Σ wᵢ·F_rᵢ[u], Σ wᵢ = 1 (Brokate & Sprekels 1996, §2.3). Every pulse the corrector computes moves
every operator; a pulse the guider does not send resets the position to unknown. A narrow operator follows the motor at
once, a wide one after crossing its play: a gear that wobbled inside its play loses little at the next reversal, one that
was driven far loses everything, and spread weights make the take-up soft. This is the dead-band reconstruction of
`DecDriftEstimator`, with the widths superposed instead of one chosen.

**Hypotheses.** Neither size nor shape is known. A grid: no play (prior 0.1), and every shape × size, the shapes hard
(one width), medium (three widths over a factor 2) and soft (seven over a factor 20), each scaled so that its full
reversal (the weighted mean width) is the size, 62.5 ms … 8 s in steps of 2^¼. A calibration adds its measured shape
with 60 % of the prior (`SeedFromCalibration`, below). Each hypothesis keeps its own gear position. Prior over the size:
log-normal around the "Dec backlash pulse" setting, σ = 0.9 (0.5 around a calibrated size).

**Calibration.** The South return and nudges after the North leg start from a gear engaged North: their pulse time and
Dec motion since the reversal (`CalibrationData.DecTakeUp`) are the take-up curve. The weights are fitted on the simplex
by projected gradient (Duchi et al. 2008). The calibration's last move leaves the gear engaged South.

**Learning.** A pulse that has to take up play opens a window (`WindowFrames` = 6). Every frame j of it measures the
motion since the start, and the residuals rⱼ have the covariance of white noise with the start's common to all,
σ_w²(I + 11ᵀ), plus the wander since the start, a random walk q·min(i, j); log L = −½ rᵀC⁻¹r (Cholesky). σ_w² and q
come from the variances of the open-loop changes over one and two frames (V₁ = 2σ_w² + q, V₂ = 2σ_w² + 2q). With
white noise alone (σ²(I + 11ᵀ)) the replay of the real log learned nothing consistent: its Dec offset wanders over many
frames, and taken as independent frames every window was far too sure. The posterior is tempered towards
the prior by 0.95 per window (a memory of about 20 take-ups), so a play that changes over the night is followed. The
frames in between are what tell a soft take-up from a hard one; with the end point alone, a play crossed in one window
fitted every shape alike and a play never crossed sent the posterior to the largest soft size.

**Compensation.** A pulse of T ms that should move the star needs uₖ = Fₖ⁻¹(T) of motor time under hypothesis k (the
piecewise linear inverse, Kuhnen 2003); the 0.3 quantile of uₖ − T over the posterior is added, at most up to the max
Dec pulse. An undershoot leaves error for the next frame, which the model knows still has play to take up; an overshoot
costs another reversal with the whole play. The correction reported to the algorithm is the motion the model expects
(posterior mean), not "duration minus compensation".

**After a slew.** A slew leaves the gear on an unknown side of its play, and the load and with it the play change with
the target. When guiding starts with a kept calibration after the mount moved more than `RemeasureAfterSlewDeg`
(default 5°) or changed pier side since the play was last measured (a calibration or this measurement; always at the
first start of a session), `DecPlayProbe` measures it again before guiding starts, in the Calibrating state: North
steps of about 1.5 px until the star follows three in a row (PHD2's backlash clearing), then South steps back to the
start. The return is the take-up from a gear engaged North and seeds the model as a calibration's does; the gear is
then engaged South and guiding locks on the star's original position. If the star does not follow, guiding starts with
the gear's position unknown. Guide log line "Dec play measured after the slew: …".

**Gate.** A pulse whose direction still has play to take up (posterior mean ≥ 60 ms) is sent only when the offset needs
it: a one-sided CUSUM per side on z = offset/σ, S⁺ = max(0, S⁺ + z − k), k = 0.5, h = 7 (Page 1954), or |z| ≥ 4. σ is
the frame-to-frame noise of the offset minus the corrections expected. Held, the pulse would only have moved the gear
inside its play; the algorithm is told that nothing went out. Pulses towards the side the gear rests on pass as usual.

## 3. What was tried and dropped

| Variant | Result (closed loop, 1 h, Predictive unless noted) |
|---|---|
| Gate only on reversals, compensate every pulse that still has play | floating gear: compensated noise pulses feed the seeing into the mount, true RMS 0.11 px vs 0.05 off |
| Learn only from windows started with the gear certainly engaged | never learned on the logged mount (the gear never engages); no play learned as 0.9 s |
| One shape (soft) × size | a hard play pushed to the largest size (3.4 s for 2 s), 0.23 px RMS |
| Gate commits to a direction once allowed | the pulses that complete a take-up chase the seeing again: Hysteresis 0.07 → 0.14 px |
| Gate by expected effect (pass a pulse when ≥ 50 % of it would move the star) | hard 600 ms: Hysteresis 0.10 → 0.17 px |
| No gate (play state and compensation only) | thousands of compensated noise reversals, up to 2000 s of pulses/h |
| k = 0.75, h = 5 | worse on the logged mount and with drift than k = 0.5, h = 7, slightly better on hard 600 ms |
| Caution quantile 0.15 / 0.5 | 0.15 ping-pongs on a varying play (470 reversals/h), 0.5 no better than 0.3 |
| Learning noise from the offset's own RMS (to cover the wander) | the deliberate motion of a take-up counts as noise: hard plays learned 20–30 % off |

## 4. Results

`PlayModelBacklashLoopTests.PrintComparison`: `DecPlayLoop`, a Dec-only loop around `AxisCorrector` with an elastic
multi-stage gear written independently of the model (default: the logged mount, stages of 150–3000 ms, seeing σ
0.17 px, 1.3 s frames; "logged wander": the log's Dec, 0.06 px white noise and a slow wander of 0.2 px σ with a lag-1
autocorrelation of 0.84), dithers of up to ±3 px every 5 minutes, 1 h, mean of 5 seeds. RMS of the true offset (px):
total, outside the settling after dithers ("guiding"), frames to settle (< 0.3 px for 3 frames), reversals and pulse
seconds per hour. Fixed = PHD2's compensation at 900 ms.

**Predictive Dec algorithm**

| Mount | off: RMS / guiding / settle | fixed | play model | reversals/h off → play | pulse s/h off → play |
|---|---|---|---|---|---|
| logged wander | 0.252 / 0.189 / 3.4 | 0.246 / 0.181 / 3.1 | 0.220 / 0.173 / 2.4 | 663 → 208 | 151 → 233 |
| logged wander, hard 600 ms | 0.256 / 0.215 / 2.1 | 0.249 / 0.207 / 2.2 | 0.219 / 0.177 / 1.3 | 705 → 256 | 161 → 199 |
| logged wander, no backlash | 0.177 / 0.122 / 1.0 | 0.191 / 0.141 / 1.0 | 0.179 / 0.125 / 1.0 | 1354 → 1234 | 76 → 68 |
| logged OnStepX, no dither | 0.064 / 0.064 / – | 0.060 / 0.060 | 0.076 / 0.076 | 1256 → 27 | 105 → 19 |
| logged OnStepX | 0.179 / 0.067 / 3.0 | 0.179 / 0.068 / 3.0 | 0.161 / 0.069 / 2.8 | 1274 → 24 | 129 → 29 |
| logged, play ±50 % | 0.229 / 0.067 / 3.1 | 0.230 / 0.067 / 3.2 | 0.205 / 0.076 / 3.1 | 1288 → 30 | 128 → 36 |
| drift 0.006 px/s | 0.187 / 0.095 / 3.0 | 0.189 / 0.099 / 2.8 | 0.169 / 0.092 / 2.4 | 1230 → 29 | 144 → 37 |
| hard 600 ms | 0.206 / 0.148 / 2.2 | 0.206 / 0.148 / 2.2 | 0.170 / 0.104 / 2.0 | 1084 → 61 | 161 → 47 |
| hard 2000 ms, drift | 0.261 / 0.185 / 4.0 | 0.255 / 0.173 / 4.7 | 0.178 / 0.109 / 2.0 | 909 → 60 | 189 → 132 |
| no backlash | 0.139 / 0.050 / 1.1 | 0.159 / 0.092 / 1.3 | 0.142 / 0.058 / 1.3 | 1289 → 1159 | 18 → 19 |

With the log's wander the play model sends as much Dec pulse time as the fixed pulse, but now it moves the star: the
wander is corrected instead of the gear being pushed around inside its play.

**PHD2's algorithms** (Hysteresis, ResistSwitch): dithers settle faster with the play model (Hysteresis 4.3 → 2.8
frames on the logged mount; with the log's wander total RMS 0.278 off, 0.253 fixed, 0.219 play model), but the guiding
between dithers is worse on a hard play and with drift (Hysteresis hard 600 ms: 0.117 px off, 0.175 play model). They don't learn what a pulse really did (`IGuideAlgorithm.CorrectionApplied`): a held
or swallowed correction is still part of their state. So the play model is used with the Predictive Dec algorithm only
(`Guider.UsesPlayModel`); with the others the fixed compensation stays in use.

## 5. Limits

- The simulator is not the mount. The log fits a play wider than every travel the guiding made, but it does not rule out
  a mount that drops pulses (§6); the live test decides (guide log line "Backlash play model: …" per take-up, and the
  `PulseRejected` warning or the NINA log's notes on pulses an INDI driver ended early).
- A pulse the mount drops without saying so looks like play to the model, and it learns one: in `DecPlayLoop` with a hard
  150 ms play and silently dropped reversals of 300 ms or more, it learns 2.8 s and guides worse than without
  compensation (§6). Only a reported drop is safe. Where
  the play depends on the target, a balance that keeps the Dec gear loaded on one side helps any compensation.
- The play is symmetric (one size for both directions). A Dec axis that loses more one way would need per-direction
  widths.
- Without dithers or real offsets, the play model on the logged mount guides about 0.01 px worse than no compensation
  (it does sometimes take up the play on a false alarm of the gate); the total over a night with dithers is better.
- What is learned is stored per profile and mount (`backlash-play-model.json`); a calibration replaces the shape.
- The Guiding Coach suggests the play model only where a fixed pulse clearly fails (`response.decBacklash`,
  `GuidingCoach.BacklashSuggestion`): with the Predictive Dec algorithm, in Dec guide mode Auto, without a suggested
  Drift mode, and when the play is soft (the large-move value at least 1.5 × the reversal value) or large (1 s or more).
  The measured reversal value becomes the "Dec backlash pulse", the prior of the size (at most 8 s, the largest size);
  a large play has no 3 s limit, since each pulse adds at most up to the max Dec pulse and the model keeps what is
  left. A small hard play keeps the fixed pulse, although the simulator favours the play model there too (hard 600 ms
  above): until the live test, the coach stays with what was proven on the mount; the setting can be chosen by hand.
  The coach runs no trial of play model against fixed pulse: the model learns from take-ups (a memory of about 20) and gains
  mostly around dithers, without which it guides slightly worse (no dither above), so a few minutes of trial would
  judge it unfairly.

## 6. PHD2 guide logs of 2026-09-26 to 10-06

Seven nights of PHD2 2.6.15 (pins build) on the same rig over INDI (LX200 OnStep), Dec guided by Hysteresis or
ResistSwitch with PHD2's adaptive backlash compensation, 0.5 s exposures in all but two sessions. The play model was
not in use; the logs were analysed after its implementation to see whether it covers them.

- **The compensation ran away every night.** At reversals with offsets of 0.15–0.3 px (seeing), PHD2's adaptive logic
  judged the following frames an undershoot and grew the pulse from 20–50 ms to 1.5–3 s over 2–3 hours (4.3 s at most),
  raising "Dec Max Duration" to 3000 ms on the way. The internal guider's port caps it at 1.5 × the setting unless a
  ceiling is set.
- **The compensation part of a reversal did not move the star.** After reversals of 0.3–3 s the star moved 0.15–0.3 px
  of 1–9 px commanded (most of it the offset's own return), and nothing followed over the next 20 frames. Pulses of the
  same length in the direction of the pulse before moved it fully (0.44 of 0.43 px).
- **PHD2 did not wait for it.** After a continuing pulse the next frame came the pulse length later (500 ms → +0.5 s,
  1250 ms → +1.9 s); after a reversal, the compensation part was missing from the interval (a 3 s reversal was over
  after 0.9 s, the bare exposure). PHD2's INDI mount waits until the timed-guide property is no longer Busy, so the
  driver reported those pulses as ended at once. Whether it rejected them (Alert) or ended them (Ok) the log does not
  say; reversals under 200 ms were waited for on both pier sides.
- **What the play model would learn from them.** `PlayModelGuideLogReplay` with the logged pulses as sent: 0–600 ms on
  the sessions before the compensation had grown, 3.5–8 s on those after (the largest size of its grid). Two sessions
  instead drove the gate's noise estimate to 0.42 and 0.54 px (real: about 0.07); a correction the mount did not make is
  one large frame-to-frame change, and the clipped mean took each one in. Frames after a correction of more than 4 σ
  are now left out of it (0.11 and 0.24 px; the closed-loop results of §4 are unchanged within ±0.003 px).
- **Consequences.** The internal guider waits the full length of every pulse and then the driver's flag, so the timing
  is not the issue; a dropped pulse is. An INDI Alert on a guide pulse now reaches the guider as
  `PulseRejectedException`: the pulse counts as not sent and the play model forgets the gear's position. In
  `DecPlayLoop` with a hard 150 ms play and reversals of 300 ms or more rejected (`DropReversalsFromMs`,
  `ReportDropped`), it then learns 146–157 ms and guides at 1.06 × the RMS without compensation; dropped silently it
  learns 2.8 s at 1.9 ×. A driver that ends a pulse early with Ok is only noted in the NINA log, as some drivers end it
  as soon as the mount, which times it, has accepted it. Whether the OnStep INDI driver rejects or ends such pulses, and
  whether the native OnStepX driver drops any, is open and decides whether this covers the mount.

## 7. The gate and a wandering Dec (2026-10-18)

A Sky Simulator session (OnStep profile, pier West with a hard 3″ play, 410 ms at 7.3″/s) guided Dec at 0.64″ RMS
against RA's 0.43″, although the profile gives Dec less drift and wander than RA. The play model had learned the play
right (357–412 ms). The cost was the gate: the profile's Dec random walk of 0.25″/√s is 0.12 px a frame at its 2.1 s
frames, twice the seeing, and the gate took z against the whole frame-to-frame noise, wander included. It held back the
star's own motion until it had gone far (478 corrections held in 41 minutes, lag-1 autocorrelation of the Dec offset
0.63).

The gate now takes z against σ² − (½ − `WanderShare`) q: the frame-to-frame noise without most of the estimated wander q.
Without wander, or with a negative one as an over-correcting loop makes, this is σ² as before. Closed loop
(`DecPlayLoop`, Predictive, play model, mean of 5 seeds, RMS of the true offset in px; the session's case also on the
simulator's 0.2 px centroid grid, `MeasurementQuantumPx`):

| Scenario | share ½ (before) | share 0.1 | share 0 |
|---|---|---|---|
| Sky Simulator session | 0.187 | 0.164 | 0.158 |
| Sky Simulator session, 0.2 px grid | 0.197 | 0.179 | 0.173 |
| logged wander + dither | 0.219 | 0.195 | 0.188 |
| logged wander, hard 600 | 0.220 | 0.205 | 0.199 |
| hard 2000, drift + dither | 0.179 | 0.174 | 0.174 |
| hard 600 + dither | 0.170 | 0.171 | 0.174 |
| drift 0.006 + dither | 0.167 | 0.167 | 0.171 |
| OnStepX + dither | 0.159 | 0.157 | 0.159 |
| no backlash + dither | 0.151 | 0.150 | 0.151 |

0.1 keeps most of the gain and none of the losses that 0 shows with a hard play or a drift, where the white/wander split
of a few dozen frames is too uncertain to trust in full. The wander is followed, so the reversals rise where it is large
(Sky Simulator 140 → 246 an hour, logged wander 211 → 687, still fewer than the fixed pulse's); elsewhere they barely
change. The "no backlash" row is 0.151 rather than §4's 0.142 for both, as the play model changed in between (every
reversal now opens a take-up window).

With a fixed pulse of exactly the right size, Hysteresis and ResistSwitch did better on the session's case than the play
model with the old gate (0.157 and 0.165 against 0.192 without the grid); with the new gate the play model reaches
0.158–0.166 without having been told the size. The Predictive correction pace mattered little: 0.7 against 0.5 gained
about 3 %.

## 8. A limit above the measured size (OnStepX, 2026-10-18)

The first night with the play model on the OnStepX (Dec soft, about 4 s from the calibration's return; the fit cannot go
above the largest of `BaseWidthsMs`, 4000 ms, although the return lost about 6 s) guided Dec at 0.53″ RMS over 64
minutes. The learned full reversal swung between 1.1 and 8 s: it rose in every run of pulses one way after which the star
stayed where it was (frames 1119–1139: 3.2 s of North pulses, 10 px expected, the offset stayed at −0.2 px; after Dec
dithers up to 8 s, the top of the grid), and fell in the alternating corrections that followed. The gear is never fully
engaged while guiding: at the dithers, RA moved 0.85–1.24 × its calibrated distance and Dec 0.13–0.61 ×, also after a
pulse the same way.

The high values cost the most: grouped by the size the model held, outside settling,

| Learned full reversal | Frames | Dec RMS | Reversals that overshot by > 0.3 px |
|---|---|---|---|
| < 3 s | 1659 | 0.40–0.48″ | 13 % |
| 3–4.5 s | 1831 | 0.41″ | 11 % |
| > 4.5 s | 606 | 0.91″ | 53 % |

with 25 reversal pulses at the 2500 ms limit above 4.5 s and none below. With a measured size (a calibration's or the
probe's after a slew) the sizes above the grid size nearest to `MaxGrowthOverMeasured` = 1.2 × it (1.09–1.30 × it, as the grid steps
are 19 %; exactly 1.2 × could leave no size above the measured one) now keep no weight whatever the evidence; smaller sizes stay free, as an undershoot is cheap, and an unmeasured play is not limited. A
restored model takes the measured size from its stored calibrated shape (its mean width), so the limit and the prior
survive a restart; before, the prior was built around the "Dec backlash pulse" setting. Replaying the night's Dec
offsets and pulses through the model (open loop, so the pulses are the night's): without the limit 10/50/90 % of the
frames held 1.3/3.1/5.4 s and 14 % more than 4.5 s, with it 1.9/3.2/4.5 s, at most 4.76 s. Whether the overshoots go with
it is for the next night to show.

After the meridian flip the probe measured the play again (North 1980 ms, South 7179 ms, the model at 3.7 s) and the
size stayed at 3.1–4.3 s, but 16 reversals in 6 minutes went out at the 2500 ms limit for offsets of 0.04–0.5 px. The
gate's CUSUM took a Dec dither of 3.6 px (z ≈ 34) in full: the sum reached 33–55 σ and, worn down by k = 0.5 a frame,
stayed above h = 7 for about 100 frames, letting every reversal that way through; and the star's way back, one way
frame after frame, entered the noise estimate as wander, which took the noise the gate uses down to `MinNoisePx`. The
sums are now capped at h, and a change from or to an offset of more than `LargeOffsetSigmas` = 12 σ stays out of the
noise estimate. At 4–8 σ the closed-loop "no backlash + dither" case lost 5 % (0.165 against 0.157 px); at 12 σ it is
unchanged (0.157).

### A/B after the fixes (21:31–22:20, same session, pier East)

With the play model (28 min) Dec was 0.47″ RMS outside settling at 267 reversals an hour, 4 % of them overshooting and
2 pulses at the 2500 ms limit an hour; with the compensation off (20 min) 0.62″ at 495 reversals, the Dec frame-to-frame
noise equal (0.28 and 0.30″), every 5-minute block worse. The pulse model was still neutral (learning after a new
calibration). A simulation scenario with 40 % of the output following small corrections at once, as the closed-loop
regressions of the log suggested, guided best without compensation, the opposite of the A/B: those regressions are
biased. Fitted to the A/B over a grid of hard, medium and soft plays, the closest was a hard play of 2–3 s with seeing
0.09 px and little wander (`PlayModelBacklashLoopTests.OnStepX20261018`, in the scenario list as "OnStepX 2026-10-18").
There the play model without a calibration's seed guided 15 % better than seeded (0.134 against 0.157 px measured): the
seed's shape mixes the two grid widths around the play. Not changed on one night of one mount; the same holds for
leaving the pulse model's Dec effect neutral under the play model (better RMS, longer settling in the scenario).

## 9. Reversals where no play is expected (2026-10-19)

Since every reversal opens a take-up window (§7), a mount without play guided 7 % worse with the play model than without
(`PlayModelBacklashLoopTests.AMountWithoutBacklashLosesNothing`, 0.157 against 0.147 px; it allows 5 %). The
compensation was not the cost, 3–11 pulses an hour carried any: the gate was. At 0.17 px of seeing and 0.0031 px/ms a
window cannot tell no play from about 100 ms, so about 500–700 windows an hour kept the posterior's mean play swinging
around `GateMinSlackMs` (60–130 ms, the most probable hypothesis still no play), and the gate held back 1300–1400 pulses
an hour that a gear without play would have carried out. Before, the model learned about 5 windows, settled at 10–40 ms
and stopped opening windows, and the gate held only during those first minutes (about 230 pulses).

Such a window, opened at a reversal while the mean play that way is below `GateMinSlackMs`, now weighs
`ProbeWindowWeight` = 0.1 of a window: its likelihood is tempered by 0.1 and the older evidence forgotten by
`Forgetting`^0.1. A play that appears still shows in many of them (`APlayThatAppearsAfterNoPlayWasLearnedIsLearned`);
the seeing of a mount without play hardly moves the model. In the test case 0.25 gave 0.156 px, 0.1 gave 0.151 and 0.05
gave 0.150. Tried and dropped: learning such a window only when its best hypothesis beats the posterior by 3 σ (0.162 px:
the windows that pass are the seeing's outliers, which teach a play), leaving out windows that start or run through an
offset of more than 12 σ (0.156 px, and it lost the appearing play in the noiseless unit test), and gating by the
median play instead of the mean (0.190 px: the gate's holds are what keeps the reversals of the seeing out).

In `PrintComparison` only the cases without play changed (play model, RMS / reversals an hour):

| Algorithm | Scenario | off | before | now |
|---|---|---|---|---|
| Predictive | no backlash + dither | 0.139 / 1289 | 0.151 / 536 | 0.144 / 990 |
| Predictive | logged wander, no backlash | 0.177 / 1354 | 0.182 / 1281 | 0.179 / 1286 |
| Hysteresis | no backlash + dither | 0.180 / 1416 | 0.198 / 1341 | 0.183 / 1364 |
| Hysteresis | logged wander, no backlash | 0.184 / 937 | 0.189 / 931 | 0.185 / 938 |
| ResistSwitch | no backlash + dither | 0.219 / 1284 | 0.228 / 1270 | 0.222 / 1278 |
| ResistSwitch | logged wander, no backlash | 0.189 / 698 | 0.194 / 698 | 0.191 / 699 |

Every case with a play is unchanged to the third digit; Hysteresis on "OnStepX varying + dither" has the same RMS at 76
reversals an hour instead of 47.

## 10. A wrong Dec-flip setting after the flip (OnStepX, 2026-10-18/19)

On 2026-10-18 at 21:08 the flip West → East was handled right (`DecFlipRequired` true, the measurement after the slew found
the play). Ten minutes later, after a dither, the play model swung Dec between 2.5 s pulses North and South for offsets of
±0.3 px (the take-up limit cycle fixed in §8). That tripped the runaway detector's pulse duty (more than half of 60 s),
and a Dec runaway within 15 minutes of a flip was taken as a wrong Dec-flip setting: Dec was inverted, the now wrong
setting saved, and guiding ran away at once. On 2026-10-19, back on pier West, the East calibration was flipped with that
setting: the measurement after the slew reported "North pulses moved the star South", guiding started anyway and Dec ran
away by 270 px. After the recalibration Predictive Dec kept what it had learned from those frames (wander 30, gain 0.97,
seeing 2 px instead of 0.1 px) and corrected the seeing on every frame.

Now:

- `DecPlayProbe.DecReversed` marks that failure. With a flipped calibration and the post-flip self-check on, the guider
  inverts Dec (saving the setting, alert `DecFlipCorrected`) and measures again; the second measurement confirms the
  direction, and the self-check does not invert back later. Otherwise, or when it is still reversed, the mount is
  calibrated again (alert `CalibrationInvalidated`) instead of guiding with a Dec that pushes away.
- `RunawayDetector.Cause` tells a growing error from the pulse duty; only a growing Dec error inverts Dec after a flip.
  A duty runaway still stops guiding.
- A completed calibration resets the Dec algorithm, and a runaway resets the algorithm of its axis.
- The play depends on the pier side here. After the flip of 21:05 (Dec inverted by the self-check, the measurement had
  failed) the model kept the "none" it had learned on pier West: in eight minutes, 21 reversals and Dec RMS rising to
  1.1″, it learned nothing, since the windows of reversals where it expects no play weigh 0.1 (§9). The same 551 frames
  replayed into a fresh model (`PlayModelGuideLogReplay`) give 4595 ms (Soft); the West session of 20:11 gives 24 ms
  (None). When the measurement after a pier-side change does not seed the model, `PlayModelBacklash.ForgetLearned` now
  takes it back to its prior (a calibration's shape and size stay).

Tests: `ClosedLoopTests.Play_probe_after_the_flip_inverts_a_wrong_dec_flip_setting`,
`Play_probe_calibrates_again_when_dec_moves_the_wrong_way_and_may_not_be_inverted`,
`Pulse_duty_runaway_after_a_flip_does_not_invert_dec`, `Play_learned_on_the_other_pier_side_is_forgotten_when_the_probe_fails`;
`PlayModelBacklashTests.ForgettingWhatWasLearnedGoesBackToThePriorAndKeepsTheMeasuredShape`.

## 11. Plays wider than 4 s (OnStepX pier East, 2026-10-19)

The probe after the slew of 21:45 returned through 8354 ms South, yet the model started at 3711 ms and then stayed at
4757 ms for most of the session, with 2.5 s compensations at ±0.3 px that overshot by up to 3 px. A calibration's or a
probe's fit is a mix of `BaseWidthsMs`, so the widest operator (4000 ms) bounded the size it could measure: hard or soft
plays of 4–8 s all came out as a 4000 ms size (3711 ms as the posterior's mean), and the limit above a measured size (§8)
kept the learned size at 4757 ms. The 19:56 calibration on pier West had hit the same bound.

`BaseWidthsMs` now goes on to 6000 and 8000 ms (`SizesMs` already reached 8000). A width the longest return pulse did
not get past moves nothing in the points and only stands in for a lower rate: with it a probe at half the calibrated
rate measured a 1.1 s play as 1.6 s (`DecPlayProbeTests.MeasuresThePlayThroughSeeing(0.5)`). The fit therefore takes the
widths up to 4000 ms, as before, and the wider ones only when the return got past them. States stored with ten weights
are restored with zeros for the new widths. Tests: `PlayModelBacklashTests.AReturnThroughAPlayWiderThanFourSecondsMeasuresItsSize`,
`AStateStoredWithTheFirstTenWidthsIsRestored`.

## 12. Review of PR 208 (2026-10-20)

- **The dither in the simulation.** `DecPlayLoop` guided a dither out with guide steps; `Guider` by default
  (`FastRecenter`) tells the algorithms of the dither and moves it back with an open-loop recovery move, which the gate
  does not hold and which drops an open take-up window. `DecPlayLoop.FastRecenter` does that now
  (`PlayModelBacklashLoopTests.PrintFastRecenter`, Predictive, 5 seeds; RMS / guiding / settle frames / reversals an hour,
  guided out → recentered):

  | Scenario | off | play model, guided out | play model, recentered |
  |---|---|---|---|
  | OnStepX + dither | 0.179 / 0.067 / 3.0 / 1274 | 0.155 / 0.069 / 2.6 / 30 | 0.153 / 0.070 / 1.9 / 31 |
  | hard 600 + dither | 0.206 / 0.148 / 2.2 / 1084 | 0.174 / 0.112 / 2.2 / 74 | 0.172 / 0.111 / 1.5 / 74 |
  | hard 2000, drift + dither | 0.261 / 0.185 / 4.0 / 909 | 0.178 / 0.107 / 1.8 / 80 | 0.178 / 0.108 / 1.6 / 83 |
  | no backlash + dither | 0.139 / 0.050 / 1.1 / 1289 | 0.144 / 0.061 / 1.5 / 990 | 0.142 / 0.062 / 0.9 / 1050 |
  | logged wander + dither | 0.252 / 0.189 / 3.4 / 663 | 0.199 / 0.148 / 1.7 / 398 | 0.199 / 0.148 / 2.0 / 399 |
  | OnStepX 2026-10-18 | 0.347 / 0.173 / 5.1 / 622 | 0.270 / 0.098 / 1.5 / 176 | 0.255 / 0.084 / 1.4 / 152 |

  Without compensation and with the fixed pulse the recenter changes the results by about their seed noise. The results
  of §4, §7 and §9 hold for the default path: the play model settles as fast or faster after a recenter, and guides
  the same between dithers.
- **The gate while the gear's position is unknown.** After a stop, a Dec guide mode change or a Dec pulse not sent the
  model assumes the gear in the middle of its play, and the first pulse either way got half of it without the gate, as
  switching the compensation on did before (947 ms for a 0.15 px offset on the OnStepX of 2026-10-18). The gate now
  holds such pulses as well. Giving no compensation while the gate has no noise estimate yet (its first five frames)
  was tried and dropped: "hard 600 + dither" guided 3 % worse (0.121 against 0.117 px), and a stop without calibration
  or probe afterwards is rare.
- **Compensation off.** The model kept telling Predictive the motion it expected (`EffectiveMs`) while the compensation
  was off, unlike the fixed compensation switched off. The algorithm is now told the pulse as sent; the model still
  tracks the gear, so switching it on keeps the position. Where the "without compensation" side of an A/B (2026-10-18)
  switched the play model off rather than choosing the fixed compensation, Predictive was told the expected motion
  there too, and the simulation (`Corrector(null)`, the pulse as sent) did not compare the same thing.
- **Pulses not sent per axis.** A pulse the mount rejected made the whole frame count as not sent: sent one after the
  other, the Dec pulse after a rejected RA pulse was not sent at all, and the play model forgot the gear. Now only the
  rejected axis counts as not sent (`PulseRejectedTests.A_rejected_RA_pulse_does_not_keep_the_Dec_pulse_of_the_frame_from_being_sent`).
