# Coach suggestions: backlash in the guiding regime, camera changes only when significant

Status: implemented 2026-09-29 for a live test (branch `coach/better-suggestions`). Motivation: the Coach session of
2026-09-29 (report `coach-20260929-200718`) suggested two changes that made both alternatives worse than the current
settings:

* **Backlash compensation 730 ms.** The big-pulse backlash test is right about what it measures: after the axis has
  been driven 3 × 1700 ms (≈ 38″ of motor travel) North, the first South pulses lose a lot of motion. The mount's own Dec
  (motor position, in the flight recorder) confirms it: a 2431 ms reversal turned the motor 18.3″ and moved the star 9.1″.
  But guiding reverses after small corrections, and there the slack was much smaller. In trial B every compensated reversal
  overshot by 1.4–2.4″ and Dec ping-ponged; PHD2's adaptive logic cut the pulse 730 → 378 ms in two minutes. A later
  200 ms compensation settled between about 100 and 300 ms. After a long move this mount's take-up is soft: the Dec rate
  recovers over ≈ 40″ of motor travel (58 → 82 → 84 → 101 % in the calibration's South return). So the lost motion depends
  on how far the axis moved before the reversal, and a big-pulse test overstates what compensation needs.
* **Gain 75.** Chosen as the lowest centroid jitter among 9 exposure × gain combinations of 5 frames each. The jitters
  (0.07–0.21 px) show no trend, the centroid noise from SNR/HFD was ≈ 0.02″ against 0.49″ of seeing, and picking the
  smallest of 9 noisy estimates biases low. The gain made no real difference.

## 1. Backlash: measure the slack that guiding meets

Principle: measure in the regime where the result is used. Backlash compensation adds its pulse at a reversal that
follows small guide corrections, so it needs the pulse time lost at such a reversal, not after a long move.

The big-pulse test stays unchanged. It measures the lost motion after a long move, D_large. That value matters for
dithers and large recenters, and it bounds the guiding slack from above. After the Dec pulse-response series (gear engaged
North), a **reversal test** follows. It is a ladder of alternating South/North pulses of P = 0.25, 0.45, 0.65 and
0.85 × D_large (rounded to 50 ms, 100 ms … max Dec pulse):

* Each step starts with one lead-in pulse that only leaves the previous swing: the long North series, or the shorter
  pulses of the step before. A longer pulse would overrun those by the difference and fake a move. Then 2 reversals
  follow, each after a move of the same size P, as in guiding with compensation P.
* Each move is measured like a response pulse: the mean of 3 frames before and after, drift-corrected.
* The first step whose reversals move the star (mean move ≥ 3 σ of the mean) ends the ladder. Its lost time,
  L = P − m/r_dir averaged over the 2 reversals, is D_guide, clamped to 0 … D_large. r_dir is that direction's rate
  measured with the ≥ 500 ms response pulses (its ratio to the calibration, clamped to 0.5–1.5; the calibrated rate when
  missing). That P is the compensation at which a guiding reversal just lands.
* **Hard dead band** (the gear play is D_large whatever the preceding move): no step below it moves the star, and
  D_guide = D_large, as before. **Soft/elastic slack** (the loss grows with the preceding move): an earlier step moves the
  star, and D_guide < D_large.
* **Frames exposed during a pulse are not used**, in any Coach pulse measurement. A frame that arrived less than
  pulse + exposure after a pulse was issued began its exposure during the pulse (the ASI120 starts every second exposure
  when the previous image arrives), so its position lies partly before the move.
* A disrupted reversal pulse (not sent, star lost, mount motion) restarts the test once. After that, or without room
  near the frame edge, D_large is kept.

First version (live 2026-09-29, withdrawn): alternating pulses of P = D_large. Those reversals still came after a 10″
move, so the rig reported 1038 ms (D_large 1425 ms), an overestimate. The early frames after South pulses biased it
further.

Outputs: `BacklashMs`/`BacklashArcsec` (the report, UI headline, history and the `response.decBacklash` finding) are
D_guide. The compensation suggestion (`BacklashPulseMs`) and the Dec guide mode Drift rule (≥ 1 s) use D_guide. The
engine report also keeps `LargeMoveBacklashMs`/`LargeMoveBacklashArcsec`, the onset pulse `ReversalPulseMs` and the
evaluated moves `ReversalMoves`. The finding carries `largeMoveMs`/`largeMoveArcsec`. While the mount response runs, the live status
reports `BacklashState` = `Measuring` and no value until the ladder has finished, so the large-move value never shows
as the result. The compensation stays adaptive
(PHD2's floor/ceiling), so a residual error is corrected while guiding.

Cost: at most 4 steps × 3 pulses × 3 frames, about 1.5 min at 2 s exposures (less when an early step moves), only when
D_large ≥ 100 ms.

## 2. Camera: change only on a significant improvement

The recommended combination (lowest jitter; within 10 % more stars, then the shorter exposure) replaces the current
settings only when:

* the current combination is not feasible (saturated, SNR < 15, no star), or
* the current one has fewer than 3 usable stars and the recommendation has at least 3 (multi-star averaging, a measured
  benefit), or
* its jitter is **significantly** lower: ln(σ²_cur/σ²_rec) > z·√(2/ν_cur + 2/ν_rec). Here ν = 2·(frames − 2) for the
  x and y residuals after the drift fit, and z is the one-sided 95 % normal quantile, Bonferroni-corrected for the number
  of feasible alternatives the best was picked from. With 5 frames and 8 alternatives that means a jitter ratio ≳ 2.8.

Otherwise the current settings are kept (`camera.good`), and trial B gets no camera change. With 5 frames per
combination the camera check can only catch gross problems. That is honest: seeing dominates the jitter and varies
between the combinations, which are measured minutes apart.

## 3. Live test

Run the Coach once with all steps on the rig and check:

1. MountResponse: the report's `LargeMoveBacklashMs` (700–1400 ms on 2026-09-29) and `BacklashMs` (the ladder result;
   expected around 300–450 ms if the mount behaves as the adaptive compensation suggested), `ReversalMoves` per step.
2. Trial B with the suggested compensation: Dec reversals in the guide log land near zero, without the
   overshoot/ping-pong of 2026-09-29. The adaptive pulse (the reversal pulse lengths) should stay near the start value
   instead of falling 20 % per step.
3. Camera: no gain/exposure change unless one combination is clearly better.
