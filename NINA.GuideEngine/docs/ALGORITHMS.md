# Algorithms

How the guiding algorithms that are not PHD2 ports work, as built, and the simulator results that justify their design.
The PHD2 ports (star finding, multi-star, calibration, the classic guide algorithms, backlash compensation) are
described in [ARCHITECTURE.md](../ARCHITECTURE.md#engine-design).

## Predictive

Model-based guide algorithm (`PredictiveAlgorithm`, `GuideAlgorithmKind.Predictive`, opt-in per axis): a Kalman filter
per axis whose state is the position error and the drift rate, with the corrections that actually went out (after
clamps, Dec guide mode and backlash compensation, reported by the guider through `IGuideAlgorithm.CorrectionApplied`)
as known inputs. The correction cancels the error predicted for the next measurement. How much a measurement is trusted
follows the night instead of aggression and hysteresis settings.

**Model.** Per frame interval T: position e' = e + d·T − c + w_e (c = the applied correction), drift d' = d + w_d,
measurement z = e + v, with Var(v) = r (seeing and centroid noise), Var(w_e) = q_e and Var(w_d·T) = q_d.

**Filter bank.** The noise ratios are learned with a bank of 50 filters (multiple-model estimation): one filter per pair
of q_e/r and q_d/r on a grid, all fed the same measurements and applied corrections. Each scores how well it predicts
the next measurement (exponentially weighted mean squared innovation, a memory of 240 s and at least 120 frames, see
*Memory in time*); the best one drives the
correction. Minimum prediction error is what the correction needs, since it cancels the prediction, and it is
scale-free: the gains depend on the ratios only, so the filters work in units of r. r itself (the seeing) is estimated
from the chosen filter's normalised innovations and used to tell a real jump (bump, gust; beyond 4 σ) from noise.
Solving the autocovariances of the open-loop position's second differences for seeing, wander and drift change (method
of moments) instead was too noisy: the wander came out 4× too high on seeing-limited data, so the filter chased seeing.

**No min-move.** The guider's smart default min-move is made for the PHD2 filters, which see the raw offset. On the
filtered estimate it only makes the error creep up to its size before each correction: on the simulated good mount
0.44″ with it, 0.16″ without. Predictive has no dead band unless one is set with its `minMove` parameter; the plugin
never passes the PHD2 min-move to it, and the Coach's aggression/min-move advice and the live oscillation hints apply
to the Classic algorithms only.

**Learning.** The algorithm publishes a state per axis (`PredictiveState`): *Learning* until the scores have been
taught for 120 frames spanning 240 s (the score memory; the first takeover can come after 10), then *Adapted*. Frames while the guider settles (after a dither or
the start of guiding) still guide, but don't enter the scores, the seeing estimate or the learning progress, and the
drift is held (a Schmidt update with no gain for it): the large settling corrections would show a small
calibration-rate error or Dec backlash as seeing, wander and drift. Settings changes rebuild an axis's algorithm only
when its own settings changed; a Predictive axis also keeps its model when only its parameters change (correction
pace, min move, e.g. in a Coach trial). A new binning starts it over (its estimates are in pixels), and so does a reconnect.

**Memory in time.** The score memory (with the seeing estimate) and the learning phase last 240 s, the median of the
usual centroid σ 12 minutes, each at least its frame count: N = max(N_min, T / Δt) frames at the measured frame interval
Δt, forgetting by λ = 1 − 1/N per frame. What they average over takes time (a change of seeing, a cloud passage, part
of a periodic-error cycle); telling the filters apart takes frames, hence the floor (at 8 s, 240 s are only 30 frames).
240 s and 12 minutes are the 120 and 360 frames of 2 s, where everything above was tuned, so frame intervals of 2 s and
longer run exactly as before (checked bit for bit on simulated nights at 2 and 2.5 s); at 0.5 s frames (Δt ≈ 0.85 s)
the memory is 282 frames instead of 120. The learning phase counts frames and seconds both, so it stays over when a
shorter exposure lengthens the memory afterwards. The median's buffer holds at most 4000 frames (12 minutes down to
0.18 s per frame). Design: [notes/SHORT-EXPOSURES.md](notes/SHORT-EXPOSURES.md).

**Fit, not a phase.** The recent prediction error of the model in use (20-frame memory) against its long-run one (the
score memory) is noted in the debug log when it leaves 0.35 … 2.5 (a change of the seeing σ by about 1.6×). It is no phase of
the algorithm: in steady simulated skies the ratio stays within 0.49 … 1.78, but a large periodic error or Dec backlash
swing it beyond the bounds too (with dithers every 3 min 10 % and 26 % of the time). Takeovers are no sign of a change
either: in steady simulated skies the bank still switches between near-equal models (3–4 % apart) a few times per 20
minutes.

**Diagnostics.** Takeovers (old → new model, both prediction errors, gains), jumps, position restarts, the end of the
learning, fit changes and a summary every 50 frames come as `AlgorithmNoteEvent`s; the plugin writes them to the PINS
log at Debug level, and the guide log gets takeovers, jumps, the end of the learning and resets as
`INFO: Predictive Ra: …` lines.

*Simulator results* (true RMS, 25 min incl. calibration, `PredictiveComparisonTests`; Classic = PHD2 defaults): good
mount 0.359″ → 0.155″, high seeing 0.902″ → 0.566″, poor periodic error 1.396″ → 0.596″, Dec backlash 0.364″ → 0.178″,
clouds 0.440″ → 0.148″. Against Classic with min-move lowered by hand (the better of 0.1 px and 0): 0.225″, 0.794″ and
1.129″ for the first three, so the gain is not only the dead band. The whole closed-loop suite (dither, lost star,
runaway, not responding, slews, meridian flip, guide log) also runs with Predictive on both axes
(`PredictiveClosedLoopTests`).

Not built: seeding from the Coach, dark guiding while the star is lost.

## Correction pace

The correction Predictive sends after a frame is `c = λ·E + D·T + ΔPE`:
- the drift until the next frame (D·T) and the periodic error's change (ΔPE, when predicted) go out in full;
- of the estimated position error E, only the share λ goes out. λ is the *correction pace*, parameter `pace`, 0.1–1,
  engine default 1.

Without new information an offset shrinks by (1 − λ) per frame: at 0.5 it's half gone after one frame and three
quarters after two.

The filter itself is unchanged. It's told the correction that went out, so its estimate stays consistent, and it still
follows a jump at once in its estimate; only the correction is spread over frames. If a kick returns by itself, the
next frames see that, and the rest of it is never corrected. The dead band (`minMove`, none by default) applies to the
whole predicted error.

**Why.** Suppose part of each correction lands a frame late, a share φ. That happens, for example, with a guide camera
that starts every second exposure before the guider's request, as the ASI120MM-S with indi_asi_ccd does
(`tools/camera-timing`). The loop gain is L = α·λ·K, where α is the part of a pulse the mount carries out and K the
filter's gain (1 for a followed jump). Such a loop settles without swinging through zero only while

    L ≤ 1 / (1 + √φ)²

which is 0.60 at φ = 0.08 and 0.44 at φ = 0.25 (derivation in [notes/CROSSING-PENALTY.md](notes/CROSSING-PENALTY.md)).
A jump followed at λ = 1 exceeds that and rings with a period of about 4–5 frames. That's what the EQMod rig showed on
2026-09-27:
- two frames after the largest 10 % of RA corrections, the error was on the other side of zero 73 ± 6 % of the time,
  against 50 % in a well-tuned loop;
- the frames right after RA's 3″ kicks, 5 % of all frames, held 27 % of RA's squared error.

At λ = 0.5 the loop gain stays at or below the bound for pulse effects of 0.7–1.3× and late shares up to 0.25.

**Cost.** An offset that stays takes 2–3 frames to remove instead of one. On that night, no retuning of the loop gain
could have changed the guide RMS by more than about 3 %, because only 5.4 % of the error was predictable from its own
past. So the pace changes the shape rather than the RMS: no ringing, overshoot after kicks at most halved, and pulses
about half as long.

Noise-free illustration (φ = 0.2, α = 1, a kick of size 1; measured error in the frames after it):

| Case | λ = 1 | λ = 0.5 |
|---|---|---|
| Offset that stays | 1.00, 0.20, −0.16, −0.07 | 1.00, 0.60, 0.26, 0.10 |
| One-frame spike | 1.00, −0.80, −0.36, +0.09 | 1.00, −0.40, −0.34, −0.16 |
| Kick, half returns | 1.00, −0.30, −0.26, +0.01 | 1.00, +0.10, −0.04, −0.03 |

The plugin sets 0.5 per axis by default (`RaCorrectionPace`, `DecCorrectionPace`); 1 is the behaviour before. The
simulator results under "Predictive" were made at λ = 1. The live confirmation and its pre-registered criteria are in
[notes/CORRECTION-PACE.md](notes/CORRECTION-PACE.md).

## Pulse model

Every guide algorithm turns a correction into a pulse with the calibrated rate. The pulse model learns from the dithers
how far a calibrated pulse really moves the star on each axis, its *effect* g, and sizes the pulses with the rate times
g (setting `PulseModel`; engine default off, plugin default on). A calibration that is off then no longer makes the
guiding over- or under-correct. The algorithms are told the motion they can expect, so Predictive's filters see their
corrections land.

**Learning.** A dither is an exogenous step: the offset d it creates is a valid instrument for the pulses that follow.
Those pulses also react to wander and seeing, which biases a plain regression on them. Per axis, over the first frame
after a dither and the next 8, by two-stage least squares:

    z₀ − z₈ = g·Σ s·t·R − b·(signed reversals) − a·sign d + c·Δt

- **Instruments:** d; whether the first pulse reverses the last one before the dither; sign d; Δt.
- **Axes:** on Dec, b and a keep the backlash out of the effect. RA fits g and c only: its motor doesn't reverse at a
  guide rate below sidereal.
- **Memory:**
  - the sums forget 2 % per window, a memory of about 50 dithers;
  - they're stored per profile and mount together with the calibration they belong to; a new calibration starts over;
  - the model learns with the setting off too, so switching it on uses what is known.

**In use.** Cautious throughout:
- the effect is taken at the upper end of its uncertainty (ĝ + σ), which lengthens less;
- it stays within 0.67–1.5 and changes in steps of 0.02;
- it applies only after 10 windows and with σ ≤ 0.1.

Each change writes a guide-log line, e.g.
`INFO: Pulse model: RA pulses ×0.87 (effect 1.14 ± 0.01), Dec pulses ×1.25 (effect 0.78 ± 0.03), from 48 dithers`.

**Backlash isn't compensated.** On Dec the loss at a reversal is estimated, but only as a lower bound. While the
guiding reverses on noise, the gear often turns inside the backlash, and a reversal then loses less than all of it. In
the simulator a 0.4 px backlash came out between −0.07 and 0.22 px, which is too weak to drive a compensation safely.
Dec guide mode Drift avoids the reversals instead.

**Evidence** (the EQMod rig; details in [notes/DEC-PULSE-MODEL.md](notes/DEC-PULSE-MODEL.md)):
- With PINS's calibration of 2026-09-23, RA pulses acted 1.14 ± 0.01 and Dec pulses 0.78 ± 0.01 of the calibrated
  amount. PHD2's pulses on the same mount acted 1.06 and 1.01 of PHD2's own calibration.
- There is no dead time per pulse. The earlier "dead band" was the bias of a plain regression, which finds one on RA
  as well.

**Simulator** (`ClosedLoopTests.Pulse_model_learns_what_the_pulses_move`: both axes Predictive, a dither every 2 minutes,
2″ of Dec backlash):
- **Calibration off like the rig's:** it learned 1.12 and 0.77 (truth 1.14 and 0.78); true RMS 0.212″ → 0.205″.
- **Calibration right:** it stayed about neutral; true RMS 0.225″ → 0.205″.

Predictive's filters absorb much of a clean calibration error by themselves. What the model does on the rig, where
Dec's trouble also comes from its backlash, is for the live test.

## Measurement uncertainty

Per-frame uncertainty of the measured guide-star position (`MeasurementUncertainty`): how far the centroid is expected
to be off the star image's true position because of the noise in the frame, σ per axis in px. The seeing's image motion
is not part of it (the star image really moved). Used by Predictive and the Coach's trial judge; no setting.

**Model.**

- **One star:** σ = HFD / SNR · (0.42 + 10 / SNR). The first term is the centroid precision of a Gaussian PSF,
  σ_psf / SNR with σ_psf = HFD / (2√(2 ln 2)) (a Gaussian's HFD is its FWHM). The second comes from PHD2's centroid,
  which only uses pixels above background + 3σ: on a faint star the pixels near that threshold drop in and out from frame
  to frame, and the noise grows faster than 1/SNR. Its factor 10 is fitted (`MeasurementUncertaintyTests`); the camera
  noise model is PHD2's SNR (gain, background noise). The true centroid error is 0.83–1.10× the model on the simulator's
  stars (magnitude 8–13.5, transparency 0.1–1, SNR 7–250) and 0.7–1.9× on synthetic Gaussian and Moffat stars (0.9–1.6×
  for 80 % of them); a plain c · HFD / SNR is off by up to 4×. σ is capped at 5 px; a peak-mode position has the pixel
  quantisation (1/√12 px) as a floor.
- **Several stars:** the refined offset is the weighted mean of the primary's offset (weight 1) and the used
  secondaries' displacements (weight: SNR ratio), so σ = √(Σ w²σ²) / Σ w. A primary estimated from secondaries is the
  median of their estimates: √(π/2) · √(Σ σ²) / n from three stars on, the mean's √(Σ σ²) / n below.
- A star without an SNR or HFD makes the frame's σ unknown (null); an unknown frame counts as a usual one.

**Predictive.** The measurement variance of a frame is r · f with f = 1 + (σ² − b) / r, clamped to 1 … 100, where r is
the learned seeing, σ_u the median σ of the last 12 minutes and at least 360 frames (*Memory in time*), and b = max(1.5² σ_u², σ_u² + r / 4): the frame-to-frame
scatter of σ, and a change too small to matter next to the seeing. f enters the Kalman update of every filter of both
banks, the jump test and the periodic-error fit; the scores and the seeing estimate weight the frame 1/f (and forget as
much as 1/f of a frame). A usual frame has f = 1 and is treated exactly as without the uncertainty. Noise over more than
half of that window becomes the usual level (after 181 frames of it at 2 s), and its frames get f = 1. r was learned
with the old usual σ², so at that frame it takes over the difference to the new one (when that lies beyond the old
level's band); a lower usual level r learns by itself, trusting its frames less meanwhile, not more.

**What the simulator decided.**

- *Additive, not multiplicative.* Scaling the whole variance by (σ/σ_u)² discounted a bright star's frames up to 9×
  under clouds that raised their variance by at most 30 %; under those clouds 4 % worse than without the weighting
  (8 seeds).
- *A median, not a weighted mean of σ².* A cloudy frame's weight 1/f shrinks only like r/σ², so it still adds about r to
  a weighted σ² sum, hundreds of times a clear frame's σ²: a few clouds lift such a "usual" σ well above the clear one
  and hide moderate noise afterwards.
- *The band.* With multi-star, about one steady frame in eight guides on the primary alone (PHD2 keeps the average only
  when it is the smaller offset) with 1.5–2.2× the usual σ; without a band 20–40 % of steady frames had f > 1. On a
  bright star σ² ≪ r, and such tiny factors (f ≈ 1.001) only perturb the chaotic closed loop: without the band the
  comparison test's HighSeeing Predictive-RA run went from 0.819″ to 0.911″ and failed. With the r / 4 term bright-star
  runs are unchanged frame for frame.
- *360 frames, not 120* (at 2 s; 12 minutes at shorter frame intervals). A median of 120 frames flips to a cloud after a minute of it; with 4-minute passages it gained
  10 % under the clouds against 17 % with 360 (4 seeds, total 0.97× against 0.93×).
- *r takes over a new usual level at once.* Left to learn it (memory 120 frames, from innovations normalised by the old
  level), r kept the old value while f dropped from about 9 to 1 in one frame, so the frames were trusted far too much:
  σ 0.03 → 0.3 px for good at 0.1 px seeing (8 seeds, the 120 frames after the switch), true RMS 0.198 px against
  0.119 px without the weighting and 50 jumps flagged by mistake (none without); with the take-over 0.117 px and none
  (`PredictiveAlgorithmTests.ANewUsualNoiseLevelIsTakenOverWithoutATransient`).

**Coach.** A trial's *cloud noise* is the σ² of its frames beyond 1.5² × the trial's median σ², averaged over all its
frames. An alternative must beat the baseline by 2 standard errors also with each trial's cloud noise taken out of its
RMS, so a cloud over the baseline makes no winner (equal settings, a cloud over a third of A: 3 false winners in 200
instead of 166), and a truly 30 % better setting still wins 146 / 200 (185 against a clear baseline). Differences of the
usual σ between trials (another exposure or gain) count as before, and the RMS shown is the measured one.

**Diagnostics.** `GuideStepEvent` carries σ and each axis's factor, the flight recorder keeps σ per frame. The PINS log
(Debug) notes the start of a stretch of frames trusted less (f ≥ 2; at most once per 50 frames) with σ, the usual σ and
the factor, its end, and the count in the summaries; nothing goes to the guide log. The Statistics card's frame weight
(the Kalman gain) drops for those frames. Not built: the UI does not say *why* the frame weight dropped, the Classic
algorithms don't use σ, and the model is checked on simulated and synthetic stars only.

*Simulator results* (closed loop, Predictive both axes, faint field magnitude 12–13.5 so the guide star's centroid noise
is about the seeing's; 6 paired seeds, 40 min, from minute 6 on; `MeasurementNoiseBenchmarks.Benchmark`):

| Sky | True RMS with ÷ without frame weighting: all, under clouds, clear | Seeing learned, with / without | Model in use (log₁₀ wander ratio), with / without |
|---|---|---|---|
| Clear | 1.000 ± 0.025 | 0.071 / 0.074 px | −1.00 / −0.98 |
| Thin clouds, 30–60 s at 20–50 % every 2.5 min | 0.947 ± 0.031, **0.877 ± 0.047** (better in 6/6), 0.996 ± 0.032 | 0.079 / 0.093 px | −1.01 / −1.22 |
| Thin clouds, 4 min at 35 % every 10 min | **0.934 ± 0.012** (6/6), **0.854 ± 0.028** (6/6), 1.027 ± 0.023 | 0.074 / 0.082 px | −1.05 / −1.07 |

Without the weighting the clouds inflate the learned seeing and, with the short passages, push the bank towards models
that trust every frame less, clear ones included; with it both stay at their clear-sky values. With the short passages
11 % of the frames were trusted less (f ≥ 2), with the long ones 14 %; in clear sky 10–15 % of the frames have f a little
above 1 (mostly the primary-only frames), 1 % f ≥ 2.

Bright stars (magnitude 8–12.5): under 20–50 % clouds their σ grows from 0.008 to about 0.014 px against 0.087 px of
seeing, at most about the band's edge, so those frames stay usual or nearly so (f ≤ about 1.05; the multiplicative form
made them 4 % worse, see above). Only frames near a total loss count less: in the `Clouds` preset (with an opaque cloud)
Predictive on both axes went from 0.169″ to 0.152″, on RA only from 0.207″ to 0.214″ (one seed,
`PredictiveComparisonTests`). The steady presets (good mount, high seeing, poor periodic error, backlash) are unchanged
to the last digit.

## Periodic-error prediction

A separate estimator on the RA axis (`PeriodicErrorEstimator`, inside a Predictive RA axis, on by default there) fits
the periodic error of the RA drive to the reconstructed *open-loop* RA motion: the measured error plus all corrections
that went out, dithers and recenters accounted for, settling frames left out. Its prediction enters the filter bank as a
known input like the corrections, so the bank models only the rest (seeing, wander, drift) and stops reading the
changing periodic-error slope as drift changes. Dec has none while tracking.

**Model.** Worm fundamental + 2 harmonics with a level and a drift, fitted by a Kalman filter over [level, drift,
3 × sin/cos] with a memory of a few worm cycles. Amplitudes in RA-axis arcseconds (independent of the declination; on
the sky and on the guide camera × cos dec). The level's and the drift's process noise are capped at 0.01 and 1e-10 of
the seeing variance per frame: taken from the filter bank as they are, they include the periodic error itself until it
is predicted (the bank reads it as wander and drift), and a level that follows the curve leaves nothing for the
harmonics, so a known tooth count would never become significant.

**Period.** Setting *Worm teeth* (0 = detect; period = sidereal day / teeth exactly, since one worm turn moves the gear
by one tooth). Detection: harmonic-sum periodogram (phasor products, ≤ 1500 samples) of the open-loop motion on a grid
of 1/(6 · span) over 180–1200 s, refined by a golden-section search; Cramér–Rao σ from the effective samples (lag-1
autocorrelation). That σ is about 2× too small on the reconstructed motion, whose estimates can stay 0.3 teeth off for an
hour (135 teeth: 134.4–134.8 for 90 min), so the period snaps to a tooth count only with σ < 0.12 teeth *and* the last 3
refinements (5 min apart) within 0.2 of the same integer. Simulator: a 180-tooth worm snaps after 68 min, the
`PoorPeriodicError` preset's 480 s (179.5 teeth, estimates 179.4–179.7) never. Only a period set by the tooth count or
snapped to one counts as the worm (for the status and the incidents); any other is just a period.

**Worm position.** Every mechanical period is locked to the RA axis angle, and while tracking that follows the hour
angle: worm phase = 2π · teeth · (HA + 12 h on the other pier side) / 24 h, with HA = the mount's sidereal time − its
native RA (NINA's telescope info passes both unconverted; the plugin falls back to USNO's approximate GMST, within
0.01 s of ERFA's IAU 2006 GMST, with the mount's site longitude). A stored model therefore predicts at once after a
start, slew or restart; the fit keeps correcting phase and amplitude live (a 6′ sync offset is 18° of phase on a
180-tooth worm). A meridian flip shifts the phase by teeth × 180°: with the tooth count known the prediction goes on,
otherwise period and amplitudes are kept, the phase is estimated anew and the prediction pauses until it is reliable
(about half a cycle). Without mount information (camera ST4 output) the phase is time-based within the session (like
PHD2's PPEC) and nothing is stored.

**Stability test.** A curve counts only while it is stable like a mechanical periodic error, not a wobble of the mount
that comes and goes. The *stability run* (below) is cut into parts of a worm cycle of data (2–6), each with its own least
squares of level, drift and the harmonics: every part's fundamental above 2.5 standard errors, any two parts' amplitudes
within ×2 (or 3 SE of the difference), their phases within 45° (or 3 SE). The noise at each harmonic is a power law
fitted to the log periodogram of the residual over f/3 … 3f (red noise; Vaughan 2005, A&A 431, 391), or where higher the
residual's periodogram seen through a part's spectral window (Fejér kernel): a wobble or a transient at a nearby period
leaks into a part's fit although the whole run resolves it. A detected period needs 5 parts (5 cycles, 40 min for 480 s):
a wobble can look as steady as a worm for a few of its cycles (in the simulator one of 12 min, coherent for 15–30 min,
passed 3 parts of 0.8 cycle in 6–7 of 10 runs, 5 parts of a cycle in 1–2). The curve of a known tooth count needs 2
parts and is not applied before. A held period is checked every 5 min and dropped after two failed checks in a row, and
so is a known tooth count's curve withdrawn (never the tooth count itself). A curve is stored only once it passed the
test on the session's data: a restored one is applied at once, but not stored again before.

**Stability run.** The data since the last slew, plate-solve sync or meridian flip, over any number of guiding runs:
NINA stops and starts guiding for every autofocus or filter change (every 20–35 min), and with one guiding run per test
a detected period (five turns in one run) would never come, nor would the curve of a set tooth count with runs under two
turns. A run ends when the RA axis angle moves by more than 10 s beyond the time that passed (a slew, or a sync, which
shifts the curve's phase against the hour angle while the live fit catches up) or the pier side changes; without mount
information at every start of guiding, since a slew can't be told. Each guiding run keeps its own level and drift, taken
out together with the curve before the parts are fitted (the detrending alone takes a share of the curve with it from a
run of a few cycles). Parts are equal shares of the time the data cover (frames over 60 s apart are a gap, no part of a
cycle). A part across a pause covers the cycle's phases with a hole and has a several times larger uncertainty, so a cut
moves to a nearby start of a guiding run while every part keeps 0.9 cycle, and a held period's check takes fewer,
longer parts rather than parts across a pause. Simulator (estimator on its own, 180 teeth, 4 seeds): a 1.5–8″ worm is
found after 44–52 min in runs of 12–30 min with 2–4 min pauses, after 41 min in one run; a 12 min wobble alone
(coherent for 15 or 30 min, 5 h, 10 seeds) is taken in 1–2 nights of 10, and dropped again.

**Keep searching.** The 5 strongest periodogram peaks of the last hour are tried in turn, each refined on the stability
run before the test. The grid's ends are no peaks, and a candidate outside 180–1200 s, whose highest power on the run
lies at the edge of its window, or within half a resolution (1/(2 · span)) of one tried already is skipped. While a
detected period is held the full range is searched again every 10 min; a stable period with ≥ 1.5× its harmonic power
takes over. A refinement whose highest power lies at the edge of its window has no peak near the held period and is not
taken.

**Stored curves.** Stored in `periodic-error.json` keyed by profile + mount (teeth or period, amplitudes, phase as worm
angle, date, cycles), written when the prediction switches on and every 200 frames while it helps, restored on connect
and into every new RA Predictive algorithm. A restored model is checked first (`PeriodicErrorEstimator.Invalid`): all
numbers finite, 1–2000 teeth or a period of 180–1200 s, three harmonics, no coefficient beyond ±300″; the guider discards
any other (a note in the debug log, and `PeriodicErrorModelDiscardedEvent` so that the host deletes its copy), so a
corrupted file never drives pulses. A detected period that is replaced by a stronger one takes its stored curve along
(`PeriodicErrorModelDiscardedEvent`; the guider passes it on when the stored period is within 2 %), and so does one
dropped because its significant parts disagree. One dropped because the night can't confirm it (a part not significant,
the rest agreeing) keeps its stored curve until another period is found. A known tooth count (set, snapped, or restored
with the curve) is handled like a set one: its curve's stable flag is taken back, never the period, the tooth count or
the stored curve. *Forget periodic error* in the settings, or a new tooth count, forgets it.

**Gate.** The curve is applied only while the fundamental's amplitude is above 3 σ of its uncertainty and the bank
predicts measurably better with it than a reference bank without it on the same frames (prediction error below 0.95× to
switch on, above 0.99× to switch off); it fades in over 20 frames and out over 10.

**Too small to predict** (phase `Negligible`): the least-squares curve of the last hour of the stability run, each
harmonic less the noise's share, changes by less than 0.15 of the seeing σ between two frames (leaves above 0.225). A
guider that follows the star within a frame then loses < 1 % RMS without it. It only labels a curve that is not applied;
the gate, which measures the gain, decides: the `HighSeeing` preset's ±2″ curve is "too small" by this measure while
predicting it cuts the RA RMS by 30 % (a slow guider, low gain in poor seeing, trails even a small curve by several
frames). It holds for the curve at the period whether it is stable or not: noise only adds to the least-squares curve,
so an unstable one that is too small bounds the periodic error there. The seeing is the filter bank's without the
per-frame noise factor, so that one cloud does not make a curve "too small". The Coach reports the amplitude only of a
stable one (`PeriodicErrorState.Stable`).

**Cost.** The search and the checks are spread over the frames, one step per frame: the periodogram of the last hour,
then per candidate its peak on the recent data, its peak on the stability run and its stability test; the refinement,
the check and the search of a held period likewise. Evenly spaced frequencies by turning a phasor per sample instead of
a sin/cos pair per frequency. The heaviest step is a stability test on a whole night (0.4 M sample × frequency
evaluations; a test asserts < 1 M per frame); on a replayed 2.8 h night in detect mode the slowest frame takes 8–10 ms on
one x86 core. Frames closer than 1.2 s are averaged into one sample, so the 6000 samples always hold more than 5 turns
of the longest period (1200 s); the search looks at the last 1500 samples or 50 min, whichever is longer.

**Shown** in the Predictive line (±x″, period and teeth, learning n % / predicting / too small / off), and in the Coach
report instead of its own 3-minute fit, which cannot see worm periods. Debug log: detection, the stability verdicts,
gate changes with both prediction errors, phase re-locks, a summary every 50 frames; the guide log gets detection, gate
changes and re-locks. A wrong calibration rate scales the reconstructed motion (and so the fitted amplitude); the gate
and the live fit limit the damage.

*On a real night* (EQMod mount, 180-tooth worm of 478.7 s, 3.11″/px, 2 s frames; open-loop RA rebuilt from the guide
log in `PeriodicErrorTests`): a weak worm (0.44″ on the sky) under an irregular ~12 min wobble of 0.5–2″ and ±4 px of red
wander. In detect mode no period is accepted; with 180 teeth the curve is not stable and too small to predict for most of
the night, which is what the owner needs to know. A stored 180-tooth curve restored there is taken back and kept.

*Simulator results* (closed loop, true RA RMS; the worm locked to the axis angle, 15/5/2″, 20° dec):

| Case | Result |
|---|---|
| 180-tooth worm, detection, min 45–75 (detected after five turns) | 0.67″ without → 0.23″ with |
| Worm teeth set to 180 | predicting after two worm turns (stability test), 0.22″ in min 20–28 |
| Stored curve at another target, min 3–15 | 0.31″ learning → 0.20″ restored |
| Stored curve half a cycle off | gated off, then corrected by the fit (0.40″ vs 0.53″ learning) |
| Meridian flip, 135 / 180 teeth set | keeps predicting: 0.18″ / 0.22″ |
| Meridian flip, teeth unknown | phase re-learned, predicting again within one turn (0.21″ in the 22 min after the flip) |
| Plate-solve sync 6′ / 30′ (18° / 90° of worm phase) | 0.20″ / 0.22″ in the first 7 min, 0.23″ / 0.22″ after; the fit recovers ±15/5/2″ and the stability test, judging the stability run since the sync, keeps the curve |
| ST4, no mount information | time-based, 0.25″ in min 50–70, nothing stored |
| Presets (Predictive both axes vs Classic) | all still beat Classic |
| 6 seeds, PE on ÷ off, min 45–75 (mean, worst; `PeriodicErrorBatchTests`) | GoodMount 0.93× / 0.96×, HighSeeing 0.80× / 0.92×, Backlash 0.88× / 0.93×, PoorPeriodicError 0.55× / 0.63× (0.23–0.30″ on), 180-tooth worm 0.43× / 0.58× (0.22–0.35″ on). Before the reliability work 0.88× / 0.98×, 0.70× / 0.94×, 0.79× / 0.86×, 0.48× / 0.59×, 0.34× / 0.37×: a detected period now needs five worm turns instead of two, so in detect mode the curve predicts from about 45 min on and is still settling in this window |

## Dec drift estimator

`DecDriftEstimator` measures the Dec drift of the **open-loop position**: the measured Dec offset plus the motion of
every correction that went out, fitted with a straight line over a sliding window (`WindowSec` 300 s, at least
`WindowFrames` 25 frame intervals). Guiding hides the drift in the offsets; the open-loop position shows the mount's
motion as if it had not been guided. It relies on the calibrated rate (a rate error scales the drift). The drift always
comes from this fit, also with Predictive: Predictive's own drift state follows Dec backlash (commanded corrections the
mount didn't make) and then claims a precision it doesn't have (on the simulated backlash mount −10.8 ± 1.1″/min against
a true −0.3″/min).

**Backlash.** After a reversal the Dec gears first cross their dead band: the corrections that went out then did not
move the mount, and adding them anyway makes the open-loop position jump by up to the dead band (8 px in the simulator's
backlash mount, far more than the drift of a few minutes). The corrections are therefore passed through a dead-band
model (a play that takes up to the dead band before the mount follows) for a grid of dead bands
(`BacklashCandidatesPx`), and the session's slope changes over `WanderLagSec` are summed for each. A real dead band shows
as a clear minimum: the right one makes the open-loop motion much smoother than none (below `BacklashEvidence` of it)
and larger ones get rougher again. Without backlash the sums only fall with the dead band and level off, because a large
dead band swallows the corrections, which follow the mount's own wander; that is no evidence, and the corrections are
taken as they are (dead band 0). The frame-to-frame changes can't decide it: in closed loop the corrections contain the
measurement noise of the frame before, which makes the no-backlash model look smooth. While a minimum shows but is not
yet that clear, the drift is uncertain by the difference that dead band makes, and while the dead band is in doubt
(`DoubtBacklash`: both directions are guided again after one only, so the gears may sit anywhere in their play) so is it
by the difference any smoother one makes. While one direction is guided, a reversal (a new direction, the safety valve,
settling) also leaves the frames out until the mount follows (`GuardReversals`). In closed loop small dead bands stay
unproven: the simulated 12″ (8 px) is learned, the 0.5 px (200 ms) of the EQMod mount of the real nights below is not,
and its corrections count in full. The doubt after a direction was given up matters: without it, a heavy-backlash mount
with Predictive (which reverses often) picked the wrong direction from the chatter of the corrections inside the gap
(Dec RMS 0.44″ → 0.32″ with the doubt).

**Fit.** Huber weights and a separate level per segment. A segment ends where the open-loop position may jump (the mount
moved while guiding was paused, the lock position moved with a dither or by hand, a Coach measurement pulsed the mount,
the star was lost for a while) and where it did jump from one frame to the next by more than any dead band explains
(`JumpSigmas` 5: a bump, a gust, a Dec motor that kept running after a very short pulse). A dither counts although its
offset and the pulses that follow it are known: they move the mount by several pixels, so a small error of the
calibrated rate would leave a step, and a dead band larger than the move would look wrong only because it swallows the
recovery. On a real night 10 ms Dec reversals left an EQMod Dec motor running for a few seconds: 12 jumps of 1–5 px in
the hour, each of which had read as a drift of several ″/min before the jump test.

**Uncertainty.** Measurement noise (seeing and centroid, independent from frame to frame) plus a random walk of the
mount (wander) with rate q:

Var(slope) = σ² / Σ(t − t̄)² + 1.2 · q / T,

the second term being the variance of the least-squares slope of a random walk over T seconds. Over a few minutes a
wandering mount looks like a drift, so q matters most, and a single window cannot tell it apart from the noise. It is
therefore measured over the whole session (`WanderMemorySec`, half an hour of memory) from the change of the open-loop
slope between successive minutes, which a steady drift cancels: for slopes over τ₁ and τ₂,

E[(Δslope)²] = q · (1/τ₁ + 1/τ₂) + σ² · (1/τ₁² + (1/τ₁ + 1/τ₂)² + 1/τ₂²).

Until that is known the mount is assumed to wander by one σ of the noise per minute (`PriorWanderSec`), a prior worth
`PriorWanderWeight` independent measurements.

**Long exposures.** The window covers at least 25 frames (5 minutes hold only 18 frames of 16 s, fewer than the 20 an
estimate needs), and the clipping of gusts in the wander starts after 5 frames.

## Dec guide mode Drift

`DecDirectionPolicy` picks the Dec guide direction of Dec guide mode Drift (not in PHD2) from the estimate above. Guiding
one way only never reverses the Dec gears, so backlash stops mattering.

- **Pick.** Both directions until a Dec drift has held for `HoldFor` (1 minute), then only the pulse direction that
  counters it: South pulses for a positive drift (the offset grows positive), North pulses for a negative one, as in
  `AxisCorrector`. A drift holds when it is significant (|drift| ≥ 2.5 σ) and strong enough to bring the star back from
  the frame-to-frame noise within `DriftFloorSec` (30 s), even one standard error weaker (|drift| − σ ≥ noise / 30 s).
  The hold is tracked in every Dec guide mode, so choosing Drift while guiding picks the direction on the next frame
  when the drift has held already.
- **Keep.** The direction stays while the drift is at least one standard error in its favour and, one standard error
  stronger, would reach the floor. Once that has not been so for `SwitchAfter` (2 minutes) it switches if the opposite
  drift holds then, else it goes back to both directions until a drift holds again.
- **Safety valve.** Both directions are allowed while the error stays beyond the valve limit (1.5 px, or 3 × the recent
  Dec RMS when larger) on the side the direction cannot correct for `ValveFrames` (10) frames, until it is back.
- The guider allows both directions while it settles (after a dither, at the start); calibration never filters. The
  Dec backlash compensation works only while both directions are guided, as in PHD2.

**Why these rules** (from the simulator and the two real nights below):

- Significance alone picked a direction without any drift (a wandering mount tested every frame looks like a drift now
  and then) and for drifts too weak for one direction, hence the floor. Asking the floor of the drift minus 2.5 σ never
  picked on the EQMod mount (1″/min at 3.11″/px with 0.12 px noise: 5 ± 1–1.5 mpx/s against a floor of 4 mpx/s); minus
  1 σ picks within 4–5 minutes.
- Keeping the direction only while the drift is 2.5 σ in its favour gave it up after 8–15 minutes on the EQMod mount
  (its drift slope over 5 minutes swings between 2 and 12 mpx/s); one σ keeps it. A pick waits for the drift to hold for
  a minute, so a bogus estimate right after a change doesn't pick.
- Keeping the direction through a reversal until the opposite drift holds left the star on the side the direction can't
  correct: Dec RMS 0.69–0.89″ instead of 0.26–0.28″ in the reversing case below; hence both directions once the drift no
  longer favours it, or is clearly too weak for one direction.
- Predictive learns only the pulses that went out: the step reports the pulse after the direction filter and the minimum
  pulse, and that is what the guider tells the algorithm. Closed loop, South only on the backlash mount (half of
  Predictive's requests suppressed): its own drift 1.08″/min for a true 1.00; told the suppressed requests as sent, it
  learns 0.67.
- Backlash compensation acts only while both directions are guided, as PHD2 switches it off for North and South ("no
  recovery from over-shoots"). Left on, a suppressed request counts as a reversal, and the next pulse in the allowed
  direction carries the compensation, which really moves the mount while the step reports it as slack taken up: with
  1500 ms of compensation on the backlash mount, Predictive in Drift had 3.67″ Dec RMS (0.12″ without it); with the
  rule 0.21″ against 0.11″ in a 12-minute run, the compensation acting only in the first minutes. The Coach suggests
  Drift without compensation, and only from 0.5″/min.

*Simulator results* (closed loop, `DecDriftBenchmarks.Compare`, true Dec RMS after the first 5 minutes, mean of 3 skies;
reversals of the Dec pulses; Classic Dec = Resist Switch):

| Case | Dec | Auto | South | Drift | Drift picked (sky 1) |
|---|---|---|---|---|---|
| Backlash: 12″ (8 px) Dec backlash, drift +1″/min | Resist Switch | 0.207″, 0 rev. | 0.213″, 0 rev. | 0.207″, 0 rev. | South@3.2 |
|  | Predictive | 0.114″, 92 rev. | 0.105″, 0 rev. | 0.120″, 0 rev. | South@3.3 |
| NewTarget: as Backlash, drift −1″/min (the last target's South is wrong) | Resist Switch | 0.241″, 1 rev. | 6.552″, 0 rev. | 0.237″, 0 rev. | North@3.3 |
|  | Predictive | 0.189″, 171 rev. | 6.551″, 0 rev. | 0.150″, 0 rev. | North@4.3 |
| Reversing: as Backlash, drift +1 → −1″/min in 60 min | Resist Switch | 0.263″, 2 rev. | 4.271″, 0 rev. | 0.264″, 2 rev. | South@3.2 Both@28.6 North@38.3 |
|  | Predictive | 0.269″, 272 rev. | 4.297″, 0 rev. | 0.277″, 83 rev. | South@3.3 Both@28.2 |
| Flip: as Backlash, meridian flip after 20 of 45 min | Resist Switch | 0.296″, 1 rev. | 8.262″, 0 rev. | 0.296″, 1 rev. | South@3.2 North@23.4 |
|  | Predictive | 0.169″, 196 rev. | 8.355″, 0 rev. | 0.203″, 1 rev. | South@3.3 North@23.4 |
| WeakDrift: as Backlash, drift +0.3″/min | Resist Switch | 0.199″, 2 rev. | 0.201″, 0 rev. | 0.184″, 0 rev. | South@4.8 Both@10.3 South@26.3 |
|  | Predictive | 0.275″, 122 rev. | 0.122″, 0 rev. | 0.322″, 83 rev. | South@4.4 Both@11.8 |
| GoodMount: no backlash, drift +1″/min | Resist Switch | 0.210″, 1 rev. | 0.212″, 0 rev. | 0.212″, 0 rev. | South@3.2 |
|  | Predictive | 0.101″, 32 rev. | 0.124″, 0 rev. | 0.109″, 0 rev. | South@3.2 |
| NoDrift: no backlash, no drift | Resist Switch | 0.235″, 2 rev. | 0.304″, 0 rev. | 0.235″, 2 rev. | never |
|  | Predictive | 0.099″, 117 rev. | 0.241″, 0 rev. | 0.099″, 117 rev. | never |
| ShortScope: like the mount of the logs: 3.1″/px, 1.5″ (0.5 px) backlash, +1″/min | Resist Switch | 0.356″, 0 rev. | 0.338″, 0 rev. | 0.356″, 0 rev. | South@3.2 |
|  | Predictive | 0.201″, 172 rev. | 0.179″, 0 rev. | 0.192″, 0 rev. | South@3.3 |

Reading: one direction removes the reversals wherever a drift holds. Resist Switch hardly reverses on these mounts, so
Drift guides like Auto. With Predictive Drift is within 10 % of Auto in most cases and better at the new target. After
the flip one sky of three reached 0.33″ (the others 0.13–0.15″) while the new direction first took up the 8 px of
backlash. A fixed South is fatal once the needed direction changes (new target, reversal, flip: 4–8″). The weak drift on
the heavy-backlash mount is the case Drift handles worst: South alone would have been best (0.12″), but the drift is too
weak for Drift to keep one direction, and in both directions Predictive suffers from the backlash (0.32″ against 0.28″ in
Auto).

*Real nights* (`DecDriftNightTests`, the guide logs replayed into the estimator and the direction logic, EQMod mount,
3.11″/px, Dec 57, drift ≈ +1″/min): one hour in Auto with Predictive (37 % of the Dec pulses reversed, 12 motor runs):
the drift is significant after 3.7 minutes (5.2 ± 1.0 mpx/s), Drift would have picked South after 4.3 minutes and kept
it for 44 minutes; the estimate stays positive all hour (median 7.1 mpx/s, 1.3″/min) and no dead band is learned.
Another night, guided South with Resist Switch: South after 4.8 minutes, kept all session (median 8.2 mpx/s). The
open-loop drift is a little faster than the net South corrections per hour (≈ 1″/min): those also carry the dithers.
