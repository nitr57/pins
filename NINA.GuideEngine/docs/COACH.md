# Guiding Coach — design

Status: agreed 2026-09-23. Replaces the "port PHD2's Guiding Assistant" idea: PHD2's assistant is inspiration
only. Goal: **teach the user how to get better guiding and which settings to use** — diagnose, explain,
recommend, *prove* recommendations on the user's own sky, and track progress over time.

## 1. Principles

* **Findings, not numbers.** Every measurement becomes a finding: what was measured, whether it is good,
  *why it matters* and *how to fix it*, with an estimated impact on guiding RMS so actions can be ranked.
* **Codes + parameters.** The engine emits findings as stable codes with numeric/string parameters; the
  UI renders localized teaching texts from the code (all 14 TNS languages). Engine English text is only
  a fallback for logs.
* **Prove it.** Recommended settings are verified with short guided trials against the current settings.
* **Safe and reversible.** Measurements never leave guiding in a changed state: temporary settings are
  restored unless the user applies them; safety monitors never trip because of a measurement; cancel works
  at any time.
* **Progress over time.** Each completed session produces a report that is stored; the UI compares with
  earlier reports.

## 2. Session and steps

A session runs selected steps in this order (all selected by default), then builds the report:

| Step | Needs | Default duration | What it does |
|---|---|---|---|
| 1 CameraCheck | camera; guiding/looping is stopped by the coach | ~2–3 min | exposure × gain sweep (existing settings-finder measurement) |
| 2 Drift | calibration (the coach calibrates first if needed) and a star | 3 min (min 2) | guiding output off; seeing vs. mount error |
| 3 MountResponse | calibration | ~1–2 min | Dec backlash, pulse response per direction |
| 4 Trials | calibration | 4 × 2 min | guided A/B comparison of settings sets |
| 5 Report | — | — | error budget, grade, ranked actions, history |

Steps can be skipped (`SkipCoachStep`) or the whole session cancelled. A step that cannot run (e.g. no
calibration and calibration fails) produces a `problem` finding explaining why and the session continues
with the remaining steps where possible.

### 2.1 CameraCheck
Per exposure × gain (defaults: exposures 1, 2, 3 s; gains: current plus two spread over the camera's gain
range; 5 frames each) measure SNR, HFD, saturation, usable stars (AutoFind count ≥ min SNR) and **centroid
jitter** (std of primary positions after removing a linear drift) in px and arcsec. Feasible = not
saturated, SNR ≥ 15, star found. Candidate = lowest jitter; within 10 % prefer more stars, then shorter
exposure. It replaces the current settings only when that is justified: the current combination is not feasible, has
fewer than 3 usable stars while the candidate has at least 3, or the candidate's jitter is **significantly** lower
(ln(σ²cur/σ²cand) > z·√(2/νcur + 2/νcand), ν = 2·(frames − 2), z the one-sided 95 % quantile Bonferroni-corrected for the
alternatives it was picked from; see [notes/COACH-SUGGESTIONS.md](notes/COACH-SUGGESTIONS.md)). Otherwise the current
settings stay (`camera.good`). The chosen exposure/gain are used (temporarily) for the following steps.

Findings (examples): `camera.recommendation` {exposure, gain, jitter, snr, stars}, `camera.snrLow` {snr,
noiseArcsec}, `camera.saturated` {exposure, gain}, `camera.defocused` {hfd} (HFD > 4 px **and** > 5″: undersampled
guide scopes at ≥ 3″/px show ~2 px ≈ 6″ in perfect focus, finely sampled ones many px for a normal star),
`camera.fewStars` {stars} (< 3: multi-star can't help), `camera.noDarks` (no dark library loaded; hot pixels),
`camera.good`.

### 2.2 Drift (guiding output off)
Samples mount-axis star positions (calibration transform) every frame for `DriftSeconds`. The position is the
multi-star combined position (SNR-weighted mean displacement of the guide stars, as the guider's multi-star offset), so
seeing and drift are measured with the same averaging as the guided RMS and the error budget adds up.
* **Seeing** = high-frequency RMS per axis (high-pass, cutoff ~ max(6 s, 3 × exposure)).
* **RA periodic error**: detrend, then least-squares sinusoid scan over periods 60–1200 s (only report a
  period when the run covers ≥ 1.2 periods; otherwise peak-to-peak and max rate only). Amplitude, period,
  max drift rate ″/s.
* **Dec drift** (linear fit) → **polar alignment error** ≈ 3.8197·|decDrift px/min|·scale/cos(dec)
  (dec unknown → assume 0, parameter `decAssumed`).
* **Drift-limiting exposure**: longest exposure for which RA drift during an exposure stays below the
  seeing RMS.
* **Wind/gusts**: share (percent) of frame-to-frame jumps > 4 σ of the high-frequency jitter.

Findings: `drift.seeing` {rms, level good|average|poor}, `drift.periodicError` {amplitude, period},
`drift.raRate` {maxRate}, `drift.polarAlignment` {arcmin, decAssumed}, `drift.exposureLimit` {seconds},
`drift.wind` {gustPercent}.

### 2.3 MountResponse (pulses, guiding output off)
* **Dec backlash**, own method (fixes PHD2's low bias and edge cases): clear backlash northwards until
  consecutive moves are consistent, then reverse to south with fixed pulses and measure the pulse time
  spent before the star moves at the calibrated rate again (dead band = consumed ms). That is the lost motion after a
  long move (`LargeMoveBacklashMs`). Guiding reverses after small moves, and elastic (soft) play loses less there. So
  after the Dec response pulses a **reversal test** climbs a ladder of alternating South/North pulses (0.25/0.45/0.65/0.85 ×
  the large-move value; per step a lead-in pulse, then 2 evaluated reversals, 3 frames each). At the first step whose
  reversals move the star, their mean lost time P − move/r (r the direction's measured rate) is the reported and
  compensated backlash (`BacklashMs`, at most the large-move value). When no step moves the star (a hard dead band), the
  large-move value stays. Report ms and ″. Frames that began their exposure during a pulse (arrived sooner than pulse +
  exposure after it) are not used in any pulse measurement.
* **Pulse response**: for each direction (E, W, N, S) pulses of 100/250/500/1000 ms (capped at max pulse)
  twice; the move is measured between the mean of 3 frames before and 3 frames after each pulse (drift-corrected)
  and compared with the expected move (calibration rate). Noise-aware: σ of a measured move comes from the drift
  step's seeing (else from the scatter within the averaging windows); a duration is only judged when its expected
  move is ≥ 3 σ of the (mean) measurement, otherwise it is inconclusive and produces no finding. Yields the **minimum
  effective pulse** (smallest conclusively effective duration, i.e. ≥ 50 % of the expected move), **stiction** (only
  when a shorter duration was conclusively ineffective, < 50 %), **asymmetry** (W vs E, N vs S ratio) and **rate
  mismatch** (measured/calibrated rate). The min-move suggested for stiction is the effective pulse at the calibrated
  rate, capped at max(2 × the seeing-based min-move, 0.6 px).

Findings: `response.decBacklash` {ms, arcsec}, `response.minPulse` {axis, ms}, `response.asymmetry`
{axis, ratio}, `response.rateMismatch` {axis, ratio}, `response.good`.

### 2.4 Trials (guided)
Candidate settings sets derived from the previous steps:
* **A – current** settings;
* **B – coach suggestion**: min-move from seeing (≈ 1.28–1.65 × seeing σ in px, floor 0.1 px), RA
  aggression/hysteresis from PE rate and oscillation, Dec: backlash compensation from the backlash result,
  Dec guide mode Drift (one direction along the measured drift) when backlash is large and drift is one-sided, exposure/gain from CameraCheck
  capped by the drift-limiting exposure;
* **C – variant**: B with the one parameter the coach is least sure about changed (e.g. RA aggression
  ±0.15 or the alternative exposure);
* **A′ – current again** (optional `RepeatBaseline`, default on) to detect changing conditions.
Each trial: apply temporarily, settle (tolerance from the profile, max 30 s), then guide `TrialSeconds`,
measure RMS RA/Dec/total, peak, oscillation index, frames. Significance: guide errors are autocorrelated, so each
trial uses an effective sample size N_eff = N·(1−ρ²)/(1+ρ²) (the AR(1) form for a variance estimate, as RMS values are
compared) from the lag-1 autocorrelation ρ of its guide-step errors (per axis, ρ clamped to [0, 0.95]; the smaller of the
two axes is used), and the standard error of its RMS is ≈ RMS/√(2·N_eff). The best alternative (B or C) is declared `trials.winner` only if its improvement over A exceeds
2 × the combined standard error of the two RMS values; otherwise `trials.noImprovement` (the current settings
are as good as the alternatives within the noise; B and C stay applicable as `trial:<id>`). Thin clouds (see [measurement uncertainty](ALGORITHMS.md#measurement-uncertainty)):
each frame's centroid uncertainty σ (star size and SNR, `MeasurementUncertainty`) gives a trial's *cloud noise*, the
σ² of its frames beyond 1.5² × the trial's median σ² averaged over all its frames (RA + Dec, in ″); the improvement must
also exceed the 2 standard errors with each trial's cloud noise taken out of its RMS (in quadrature). So a cloud over the
baseline cannot make an alternative win, and a cloud over an alternative never helps it; the RMS values shown are the
measured ones, and differences between the trials' usual σ (another exposure or gain is what B or C may try) count as
before. If A and A′ differ by > 25 % **and** by more than 2 combined standard errors, a `trials.conditionsChanged`
finding warns that the comparison is unreliable. Settings are restored at the end (the user applies the winner
explicitly).

Without a suggestion only A (and A′) are guided: `trials.nothingToTry` {rms, predictive} instead of a winner. Min move,
aggression and hysteresis are never suggested (nor offered in findings) for an axis with the Predictive algorithm, which
sets its own gain and needs no dead band; with Predictive on both axes the trials only try camera, Dec guide mode and
backlash changes.

Findings: `trials.winner` {id, rms, improvement}, `trials.conditionsChanged` {change}, `trials.noImprovement`,
`trials.nothingToTry` {rmsArcsec, predictive}.

## 3. Report
* **Guided RMS** (from trials, else from the last guiding window).
* **Error budget** (quadrature): seeing (Drift), centroid noise (CameraCheck, ≈ HFD/SNR-based estimate at
  the chosen settings), mount/other = √(max(0, guided² − seeing² − noise²)). Backlash and polar alignment
  are listed as contributors with their findings.
* **Grade** from RMS / imaging scale (profile camera pixel size + telescope focal length):
  excellent < 0.5, good < 0.75, fair < 1, poor ≥ 1 (no imaging scale → grade by RMS in ″).
* **Ranked actions**: findings with severity warning/problem ordered by `ImpactArcsec` (estimated RMS
  improvement), each with an optional one-tap *Apply* (setting + value).
* **History**: the host stores reports; the UI shows trends (RMS, grade, PA error, backlash) and compares with
  the previous report.

## 4. Live hints (while guiding normally)
A lightweight analyser over the guiding statistics window emits dismissable hints (same finding codes,
`hint.*`), rate-limited to one per code per 10 minutes: `hint.raOscillation` (oscillation index > 0.6 →
lower RA aggression), `hint.raSluggish` (index < 0.15 with large RMS → raise aggression),
`hint.pulseLimited`, `hint.lowSnr`, `hint.decDrift` (one-sided Dec corrections → polar alignment),
`hint.seeingBound` (RMS close to the seeing floor: settings are fine, seeing limits).

## 5. Engine/host/UI split
* Engine (`NINA.GuideEngine.Coach`): session orchestrator (async, drives the Guider through its public API
  plus internal measurement hooks), step implementations, analysers, findings, report, live hint analyser;
  tests with the closed-loop simulator in virtual time.
* Host (`InternalGuider`): `IAdvancedGuider` Coach members → engine; applies recommendations to the guider settings; stores
  reports as JSON (`~/.local/share/NINA/InternalGuider/Coach/`); provides the imaging scale and gain range.
* TNS: plugin API `/api/native-guider/coach/*` + WebSocket event types `coach` and `hint`; a *Coach* tab on
  the internal guider page (step selection, live step views, report card, history) and a hint chip in the state
  strip. All finding codes have localized texts: title, explanation (why it matters) and how to fix.

## 6. Contract

`IAdvancedGuider` (pins `NINA.Equipment/Interfaces/IAdvancedGuider.cs`): `StartCoach(options)` returns
`AdvancedCoachStartResult { Accepted, Message, MessageCode, MessageParameters, Status }` — a rejection is reported only in
the result and never modifies the status of a running or the last session; `SkipCoachStep`, `CancelCoach`,
`GetCoachStatus`, `ApplyCoachActions(ids)` (finding ids, `trial:<id>` or ids of active live hints — an applied hint is
dismissed), `GetCoachHistory(max)` (reports of the active profile; each report keeps its `ProfileName`), `DismissHint(id)`;
`AdvancedGuiderStatus.Hints` / `CoachRunning`; events `coach` (AdvancedCoachStatus) and `hint` (AdvancedCoachFinding). The
engine mirrors these DTOs in `NINA.GuideEngine.Coach`; the host maps them 1:1.

Codes with parameters: status and step status carry `MessageCode` + `MessageParameters` (e.g. `coach.interrupted`
{reason}); each step carries an English `Detail` (logs) plus `DetailCode` + `DetailParameters` for the UI:
`camera.combination` {exposureSeconds, gain, index, total}, `calibrating`, `drift.measuring`, `response.backlash`,
`response.pulses` {direction, ms}, `trial.settling` {id}, `trial.running` {id}; null when none.

Periodic error model (AdvancedCoachDrift, only when a period was found), on the RA samples (arcsec, relative to the first
sample, T = sample time in s): `Ra(T) = PeriodicErrorOffsetArcsec + RaDriftArcsecPerMin·T/60 +
PeriodicErrorAmplitudeArcsec·sin(2πT/PeriodicErrorPeriodSeconds + PeriodicErrorPhaseRad)`. Without a period the amplitude
is half the peak-to-peak of the detrended, smoothed RA motion. `GustPercent` is 0..100.

Session rules:
* Accepted from Stopped/Looping/Selected/Guiding/Paused; rejected while calibrating, building darks, disconnected or
  when another session runs (`coach.busy`, `coach.notConnected`).
* Guiding active at the start → resumed with the original settings when the session ends/cancels/fails.
* External Start/Stop guiding, dither, loop stop, disconnect or a confirmed mount slew/tracking stop cancel the session
  (`coach.interrupted` with parameter `reason`); temporary settings are restored in every exit path.
* Mount-state debounce (one rule for the guide loop and the camera check): Disconnected → `disconnect` and Parked →
  `slew` interrupt at once. Slewing/Homing (`slew`) and TrackingOff (`trackingOff`) interrupt only when the report
  persists ≥ `SafetySettings.CoachMountInterruptSeconds` (default 8 s; must exceed the longest pulse plus INDI's 3 s
  coordinate-motion window, which can report "slewing" after guide pulses), or at once when the pointing moved more than
  `LargeMoveArcmin` since the report started (a real goto, also when it ended between two checks); the timer resets when
  the report clears. While a report is active but not confirmed: drift samples are skipped; the pulse test skips the frames
  (a pulse that was not sent, or whose measurement spans a real mount motion, is redone, at most 3 times; the backlash test
  restarts); the camera check waits; trials keep guiding under the guider's normal pause, and the paused time does not
  count toward `TrialSeconds`. Normal guiding outside a session is unchanged (corrections pause at once).
* Measurement frames (CameraCheck, Drift, MountResponse) are excluded from the guiding stats and safety monitors;
  trials use normal guiding (stats and safety active; runaway during a trial fails that trial, not the session).
* Setting names in `Changes` are the guider setting names (`AdvancedGuiderSetting.Name`): ExposureSeconds, Gain,
  Binning, MultiStar, RaAggression, RaHysteresis, RaMinMove, DecAggression, DecMinMove, DecGuideMode, DecAlgorithm,
  BacklashCompensation, BacklashPulseMs, MaxRaDurationMs, MaxDecDurationMs. Values are invariant-culture strings.

Failure/step message codes: `coach.busy`, `coach.notConnected`, `coach.noStar`, `coach.noCalibration`,
`coach.calibrationFailed`, `coach.starLost`, `coach.noPulseOutput`, `coach.interrupted` {reason: guiding|dither|
stopped|slew|trackingOff|disconnect}, `coach.cameraError`, `coach.internal`.

## 7. Finding catalogue

Severity in brackets; `→` = setting changes carried in `Changes`. Parameter units are part of the name
(Arcsec, Px, Ms, Seconds, Percent, Arcmin); unit-less ratios/indices are plain.

| Code | Severity | Parameters | Meaning / action |
|---|---|---|---|
| camera.recommendation | info | exposureSeconds, gain, jitterArcsec, snr, stars, currentJitterArcsec | best exposure × gain → ExposureSeconds, Gain |
| camera.good | good | snr, jitterArcsec | current camera settings are as good as any measured, within the noise |
| camera.noFeasible | problem | reason (saturated\|lowSnr\|noStar) | no combination gave an unsaturated star with SNR ≥ 15 |
| camera.snrLow | warning | snr, noiseArcsec | best SNR < 25: centroid noise adds to the RMS; longer exposure, higher gain, darks, focus |
| camera.saturated | warning | exposureSeconds, gain | current settings saturate the guide star (centroid bias) → lower gain/exposure |
| camera.defocused | warning | hfdPx, hfdArcsec | HFD > 4 px and > 5″ → focus the guide camera |
| camera.fewStars | info | stars | < 3 usable stars: multi-star can't average seeing; longer exposure / different field |
| camera.noDarks | info | — | no dark library: hot pixels can be mistaken for stars; build darks |
| drift.seeing | good/info/warning | rmsArcsec, level (good\|average\|poor) | seeing floor; guiding can't beat it |
| drift.minMove | info | raPx, decPx | min-move from seeing → RaMinMove, DecMinMove |
| drift.periodicError | info/warning | amplitudeArcsec, periodSeconds (null if unknown), maxRateArcsecPerSec | worm PE; PEC, shorter exposure |
| drift.exposureLimit | good/warning | seconds, currentSeconds | exposure longer than the RA drift allows → ExposureSeconds |
| drift.polarAlignment | good/info/warning/problem (<3′/<5′/<10′/≥10′) | arcmin, decAssumed, driftArcsecPerMin | polar alignment |
| drift.wind | warning | gustPercent | gusts/vibration; shield the scope, avoid chasing (higher min-move) |
| drift.decGuideMode | info | mode (Drift; North\|South in older reports), driftArcsecPerMin, backlashMs | backlash ≥ 1 s and a Dec drift ≥ 0.5″/min: one-sided Dec guiding avoids backlash → DecGuideMode Drift, which follows the drift when it reverses, and BacklashCompensation false when it is on (not raised when the mode already is Drift) |
| response.decBacklash | good/info/warning | ms, arcsec, largeMoveMs, largeMoveArcsec; when set playModel (true), playModelNeedsPredictive (true), playReason (soft\|large) | Dec backlash at guiding reversals (the large-move value for reference), from 100 ms, only in Dec guide mode Auto and when drift.decGuideMode is not raised (the compensation works only while both directions are guided). A play a fixed pulse can't serve (playReason **soft**: largeMoveMs ≥ 1.5 × ms, the play depends on the preceding move; **large**: ms ≥ 1000) with the Predictive Dec algorithm → playModel, BacklashCompensation true, BacklashModel PlayModel, BacklashPulseMs = ms as the model's prior (capped at 8000, its largest size; no 3 s limit). Otherwise up to 3000 ms → BacklashCompensation true, BacklashPulseMs = ms (the fixed pulse); with a soft or large play and another Dec algorithm also playModelNeedsPredictive (the play model would fit; the coach doesn't change the algorithm for it). The coach never switches PlayModel back to Fixed. Or mechanical fix / slight Dec imbalance |
| response.minPulse | warning | axis (Ra\|Dec), ms | shorter pulses were conclusively ineffective (expected move ≥ 3 σ, moved < 50 %): stiction → min-move above it (capped at max(2 × seeing min-move, 0.6 px)) |
| response.asymmetry | warning | axis, ratio | one direction moves much more (balance, gear mesh, guide rate) |
| response.rateMismatch | warning | axis, ratio | measured rate ≠ calibration → recalibrate |
| response.good | good | — | mount responds cleanly |
| trials.winner | good/info | id, rmsArcsec, baselineRmsArcsec, improvementPercent | best set, better than A by > 2 combined standard errors; → its settings |
| trials.noImprovement | good | rmsArcsec | current settings are as good as the alternatives within the noise |
| trials.conditionsChanged | warning | changePercent | baseline changed > 25 % and > 2 combined standard errors: comparison unreliable |
| trials.nothingToTry | info | rmsArcsec, predictive | no suggestion to trial, only the current settings were guided |
| report.seeingLimited | good | seeingArcsec, guidedArcsec | seeing dominates: settings are fine |
| report.mountLimited | warning | mountArcsec, guidedArcsec | mount part dominates: see mount findings |
| report.noImagingScale | info | — | set camera pixel size and telescope focal length for a grade relative to the image scale |
| hint.raOscillation | warning | index | RA overcorrects → RaAggression −0.1 (≥ 0.3) |
| hint.raSluggish | info | index, rmsRaArcsec | RA corrects too little → RaAggression +0.1 (≤ 1.0) |
| hint.pulseLimited | warning | axis, percent | many pulses at the max duration → MaxRa/DecDurationMs or recalibrate |
| hint.lowSnr | warning | snr | star SNR dropped (clouds, dew) |
| hint.decDrift | info | driftArcsecPerMin, arcmin | Dec corrections one-sided → polar alignment / DecGuideMode Drift (a change only from Auto) |
| hint.seeingBound | good | rmsArcsec | RMS near the seeing floor: nothing to tune |

## 8. Engine implementation notes

* **API** (`NINA.GuideEngine.Coach`): `GuidingCoach(Guider, ICoachHost)` with `Start`, `SkipStep`, `Cancel`, `Status`,
  `ApplyActions`, `GetHistory`, `IsRunning`, `Completion`; status updates as `CoachStatusEvent`, hints as `CoachHintEvent`
  through `Guider.EventRaised` (delivered by the guide loop behind the pulses). `Guider.ActiveHints` / `DismissHint`.
* **Temporary settings** are an overlay on top of the host settings (`CoachSettingsMap` maps the guider setting names);
  host `UpdateSettings` keeps working during a session and every exit path simply drops the overlay.
* **Measurements** run in the guide loop as frame hooks: guiding output off, frames kept out of the statistics and safety
  monitors, positions from a multi-star combined meter (SNR-weighted mean displacement of the guide stars from their positions
  at the start; no stabilisation/excursion gates, which are made for guiding near the lock position), hook pulses issued through the normal pulse path and clamped to the max pulse. The camera
  check captures outside the loop with the camera leased; a loop start meanwhile interrupts the session and starts after
  the camera is released.
* **Seeing** is the high-frequency RMS about a centred moving average over max(6 s, 3 × exposure) (no phase lag, drift and
  slow PE removed; corrected for the average's own noise share).
* **Backlash**: North pulses until two consecutive moves agree (≥ 40 % of the calibrated move), then South pulses; once three
  South moves in a row are substantial and the last two agree, the last two pulses moved at the full South rate r and the
  dead band is D = k·P − (y′₀ − y′ₖ)/r (averaged over two points, drift-corrected). No PHD2 low bias; asymmetric Dec rates
  are handled (D is South pulse time). D is the large-move value. The reversal ladder after the Dec response pulses gives
  the guiding value: at the first step whose 2 reversals (after the lead-in) move the star by ≥ 3 σ of their mean, the mean
  of P − move/r, clamped to 0…D; a disturbed reversal pulse restarts the test once.
* **Interrupts**: host guiding commands (start/stop guiding, pause/resume, dither, star selection, lock position, clear
  calibration, loop start/stop), confirmed mount slews/tracking stops (debounced, see §6), park/disconnect, camera failure
  and a cancelled loop cancel the session with
  `coach.interrupted` {reason}; the guider state is then left to the host. A runaway or unresponsive mount during a trial
  fails only that trial. Cancel and normal end resume guiding with the original settings when it was active at the start.
