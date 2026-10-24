# Short exposures: memory in time, and a harmonic-drive scenario — design

Status: 2026-10-11, the time windows (§3) and the simulator (§4) implemented.

## 1. What we want

- **Predictive at 0.5–1 s exposures behaves as it does at 2 s,** where it was tuned and tested. Today its memories are
  counted in frames, so at short exposures they cover a fraction of the time they were set for (§2).
- **Harmonic-drive mounts in the simulator,** so Predictive can be tried on one at 0.5–1 s before anyone guides one
  with it (§4).
- **Not the goal:** a number for how much better short exposures or Predictive are on a real harmonic drive. The
  simulator can't say that (its seeing and mount are models); real logs and live tests can (stages 2 and 3, §6).

Nothing changes for frame intervals of 2 s and more: all results so far (simulator at 2 s, the rig at 2–2.5 s) stay
valid as they are.

## 2. The problem: memories counted in frames

Predictive keeps several memories as a number of frames. The frame interval Δt is the exposure plus download,
processing and pulses; on the rig 2 s exposures came 2.35 s apart, so below about 0.35 s per frame is added.

| Memory (constant) | What it does | Frames | 0.5 s (Δt ≈ 0.85 s) | 1 s (1.35 s) | 2 s (2.35 s) |
|---|---|---|---|---|---|
| Model scores (`ScoreWindowFrames`, λ = 1 − 1/120) | which of the 50 filters guides; also the seeing estimate | 120 | 1.7 min | 2.7 min | 4.7 min |
| Learning phase (same constant) | "Learning" until 120 frames have taught the scores | 120 | 1.7 min | 2.7 min | 4.7 min |
| Usual centroid noise (`UsualSigmaFrames` = 3 × 120) | median σ that tells a cloudy frame from a normal one | 360 | 5.1 min | 8.1 min | 14 min |

Why this matters at short exposures:
- **The model choice follows the periodic error's phase.** A worm of 478 s or a harmonic drive's cycle of several
  minutes is longer than 1.7 minutes of memory. The scores then favour a fast filter while the curve is steep and a
  slow one where it is flat, and the bank switches back and forth within one cycle.
- **The cloud median is too short.** `ALGORITHMS.md` ("What the simulator decided") chose 360 frames over 120 because
  a 4-minute cloud passage must not become the new normal. At 0.5 s, 360 frames are 5 minutes, close to the
  passage itself.
- **The learning phase ends after 1.7 minutes,** well under half the time it takes at 2 s.

Already in time, not affected: the Dec drift estimator (300 s, at least 25 frames), the periodic-error estimator (its
own time base; frames closer than 1.2 s are averaged).

## 3. The rule: a time window with a floor in frames

Each memory gets a length in seconds T and keeps its frame count as a floor:

    N = max(N_min, T / Δt)        λ = 1 − 1/N

Δt is the frame interval Predictive already measures (`intervalSec`, an exponential mean with weight 0.2 for the
newest interval, steady against one late frame).

| Memory | N_min | T | 0.5 s | 1 s | 2 s and longer |
|---|---|---|---|---|---|
| Model scores and seeing estimate | 120 | 240 s | 282 frames | 178 frames | 120 frames, as today |
| Learning phase | 120 | 240 s | 282 frames | 178 frames | 120 frames, as today |
| Usual centroid noise (median) | 360 | 720 s | 847 frames | 533 frames | 360 frames, as today |

**Why the floor in frames.** Whether the scores can tell two filters apart depends on how many prediction errors
they average, not on seconds; filters are often only ~3 % apart (`SwitchMargin` 0.97). With long exposures a window
in seconds alone would hold too few frames (at 8 s, 240 s are 30 frames) and the choice would flicker.

**Why 240 s and 720 s.** They are 120 and 360 frames at 2 s, the point where everything was tuned. They are the
largest values that leave every frame interval of 2 s and more unchanged: the simulator runs at 2 s exposure come at
least 2.1 s apart (plus 100 ms download, plus the pulses), so N stays at the floor and λ is exactly today's constant.
Those runs stay bit-identical: every guide step and debug note of 25-minute runs (GoodMount, Clouds, HighSeeing and
Backlash at 2 s, GoodMount at 2.5 s) was dumped before and after the change and compared.

**What this does not fix.** 240 s is still shorter than one periodic-error cycle (287–431 s on harmonic drives, 478 s
on the rig's EQMod worm), at any exposure. The rule makes 0.5–1 s behave as 2 s does, nothing more. That is enough
where it counts: while the periodic-error prediction is applied, the filters get its change as a known input
(`periodicStep`) and score only the residual, so the model choice follows the curve's phase mainly while the
prediction is off, in the first cycles or on a mount whose curve is rejected (`HarmonicDriveRough`).

**Learning phase.** It ends when the scores have been taught for both N_min frames and T seconds (summing Δt over the
frames that taught them). Counting both keeps it monotonic: if the exposure is shortened after the phase ended, N
grows, but the phase doesn't go back to "Learning". `Progress` = min(frames / N_min, seconds / T). At 2 s and longer
the frame count is the binding one, as today. The UI shows only the phase and the progress, so nothing there changes.

**Usual centroid noise.** The ring buffer gets a fixed capacity and the median is taken over its newest N entries.
The guider allows exposures down to 0.01 s, where N would reach several thousand; the capacity is capped at 4000
(12 minutes down to Δt ≈ 0.18 s; below that the window is shorter in time). The median sorts up to that many
values per frame and axis; the cost is to be measured on the Pi, and a running median can replace the sort if it
matters.

**An exposure change while guiding** (by the user, or by the Coach between trials): N follows Δt within a few frames.
Nothing is reset: the scores keep their sums, only the forgetting changes. The median simply looks at more or
fewer of the stored entries.

**What stays in frames, and why:**
- `MinScoreWeight` (10 frames before a filter may take over), `FitWindowFrames` (20, the fit check),
  `NegligibleSeeingFrames` (100): sample counts for a statistic, not durations.
- The periodic-error fade-in and fade-out (20 and 10 frames): smoothness per correction.
- `SummaryFrames` (a debug note every 50 frames): at 0.5 s a note every ~40 s; more log, no effect on guiding.
- `StoreEveryFrames` (the curve is stored every 200 frames): more often at short exposures, harmless.
- Outside Predictive: the runaway check (6 frames), the statistics window (100 frames, labelled as such in the UI)
  and the Dec safety valve (10 frames). They describe consecutive frames; the runaway check at 0.5 s spans about
  5 s.

## 4. Simulator: what it lacked for short exposures

Two gaps, and a third found on the way (§4.3). The new options (§4.1, §4.2) are off by default. The Poisson table
(§4.3) is not an option: below 20 e⁻ of background per pixel (exposures under about 1 s in the scenarios) every
scenario now draws different random numbers than before, so such runs are no longer bit-identical to earlier ones.
At 2 s the background is above that and nothing changes.

### 4.1 A harmonic-drive mount

**What is known** (makers' manuals and FAQs, reviews, forum measurements; sources at the end of this section; a
number marked † comes only from a forum snippet and is unverified):

- **Periods.** The dominant periods are all inside the estimator's search range of 180–1200 s: 270 s (iOptron HEM44),
  287–288 s (ZWO AM5N, AM3, AM7; Sky-Watcher Wave by users), 360 s (HEM15, HEM27), 430–432 s (original AM5, Rainbow
  RST-135, Pegasus NYX-101). Five cycles take 22–36 minutes.
- **Two lines at a ratio of 1.5.** ZWO and Sky-Watcher pair a 100:1 strain-wave gear with a 3:1 belt (300:1). The
  mechanically possible lines are 861.6 s (wave generator), 430.8 s (twice per wave-generator turn, the classic
  strain-wave error) and 287.2 s (motor pulley, also the wave generator's third harmonic). ZWO's own documents give 432 s for the
  original AM5 and 288 s for the later ones. A fit of a fundamental and 2 harmonics on 430.8 s can't hold a 287.2 s
  line; on 861.6 s it holds both.
- **Size.** Specs ±10″ (AM5N, AM7), ±15″ (AM3), ±20″ (AM5, NYX-101 measured by Pegasus), about ±30″ without a
  guarantee (RST-135). Owners measured RST-135s at 40–60″ peak to peak, up to 70″. iOptron publishes no figure.
- **Slope.** About 0.2–0.4″/s on the AM5, up to about 0.7″/s on some RST-135s: 0.1–0.35″ of motion within a 0.5 s
  frame.
- **Shape.** Not a clean sine: a 2nd harmonic about 20 dB down on one RST-135E, 2″ at 143 s and 3″ at 217 s on a
  16″ DIY mount, sub-bumps on ZWO's charts.
- **Repeatability.** Every maker says the curve changes with load, pointing and gear angle; ZWO says the changes are
  irregular, so playback can't correct them. ZWO's factory chart of one AM5 shows the peak to peak of single cycles
  drifting between 25.3″ and 35.9″ over the output turn, the envelope changing over tens of cycles, and neighbouring
  cycles visibly different. No source puts a number on the change from one cycle to the next.
- **Short components,** all below the search range: about 2.9 s (> 0.2″, AM5), 9.2 and 4.6 s (RST-135E, small),
  17 s and 2.4 s (3–4″ peak to peak†, HEM15), 62 s (AM5, probably the belt).
- **Dec.** The strain-wave gear has no backlash, the belt does: "tens of arcseconds" depending on tension (ZWO staff);
  a Guiding Assistant result above 200 ms counts as large. A HEM15 (worm in Dec) measured 562 ms.
- **Wind.** No counterweight on many setups; nobody has published wind torques or responses.
- **Guiding.** Makers and the PHD2 developers recommend 0.5–1 s exposures and a guide rate of 0.9× or more;
  reported RMS 0.3–0.8″.

**Three variants, spanning that range** (no unit is fitted; the spreads of amplitude and phase are guesses, since
nobody has measured them). Periods in seconds, amplitudes ± (semi-amplitude) on the RA axis:

| | A: modern, belt (AM5N / AM7 / Wave class) | B: typical (AM5 / NYX-101 class) | C: rough (RST-135 / HEM class) |
|---|---|---|---|
| Main lines | 287.2 s ±7″, 430.8 s ±3″ | 430.8 s ±18″, 215.4 s ±2″, 143.6 s ±2″ | 430.8 s ±25″, 215.4 s ±8″, 143.6 s ±3″ |
| Steepest slope of the main lines | ≈ 0.2″/s | ≈ 0.4″/s | ≈ 0.7″/s (the 2.4 s line alone reaches 3.9″/s) |
| Change over the gear's turn | amplitude ±10 %, over ~10 cycles | amplitude ±18 % over ~10 cycles, phase ±10° over ~2 cycles | amplitude ±30 % and phase ±30° from cycle to cycle |
| Short components | 2.9 s ±0.2″ | 2.9 s ±0.2″, 62 s ±0.3″ | 2.4 s ±1.5″†, 9.2 s ±0.3″ |
| Dec backlash | none | none | 4″ (≈ 300 ms at 0.9×) |
| Wind | none | none | gusts, 1 per minute, up to 2″, 3 s long (a guess) |

A's 430.8 s line is a guess of its size, there to test the two lines at a ratio of 1.5. Common to all three: the curve locked to the RA axis angle (`PeriodicErrorFollowsAxis`, as on a real gear), guide
rates 0.9×, Dec 20°, Dec drift 0.3″/min and the random walk of `GoodMount`.

**What this asks of the simulator:** today a periodic-error term is a fixed sine. A term gets an optional slow
change of amplitude and phase: smooth random curves (values hashed from the seed at knots one correlation length
apart, smoothly interpolated), as a function of the RA axis angle, so the same gear position gives the same curve
after a slew. Off by default. The short components and the Dec backlash need nothing new.

**Built:** `PeriodicErrorTerm` has `AmplitudeChange` (a fraction) and `PhaseChangeRad` with their knot distances
`AmplitudeChangeSec` and `PhaseChangeSec`. The knot values are drawn evenly within ± the size and joined by a
smoothstep, so the curve never leaves the bounds; its standard deviation is about half the size. In the three
scenarios every main line of a variant changes with knots a whole number of cycles of its dominant line apart (the
change comes from the gear, not from the line), each line with its own random curve, so the shape changes too; the
short components stay fixed sines. `HarmonicDriveModern` is A, `HarmonicDrive` B (in `Presets`, so the app lists it),
`HarmonicDriveRough` C.

**What the predictor meets here:**
- **A:** does the harmonic-sum periodogram pick 861.6 s, which holds both lines (100 "teeth")? If it picks 287.2 s,
  the 430.8 s line is left to the filter bank. Either is acceptable; a curve that makes the error larger is not.
- **B, C:** whether the stability test accepts a curve that changes from cycle to cycle (C should mostly be
  rejected; then the bank alone carries it), and how the bank copes with the short components, which a 0.5–1 s
  loop samples too slowly to follow and may alias.
- **"Worm teeth"** means sidereal day / period here: 200 for 430.8 s, 300 for 287.2 s, 240 for 359 s (HEM27). 270 s
  (HEM44) is no whole number (319.1), so only detection finds it.

**Sources:**
[ZWO AM5 manual V1.0](https://i.zwoastro.com/zwo-website/manuals/AM5_user_manual_V1.0.pdf),
[ZWO PE test report article](https://www.zwoastro.com/2022/06/04/10-things-you-need-to-know-about-the-custom-am5s-pe-test-report-provided-by-zwo/),
[ZWO AM5/AM5N experience blog 2024](https://www.zwoastro.com/2024/08/01/my-experience-with-zwo-am5-and-am5n-mounts/),
[ZWO AM5N manual](https://i.zwoastro.com/wp-content/uploads/2026/03/b063feaca7a20b4c23090773c351e501.pdf),
[ZWO AM7 manual](https://i.zwoastro.com/wp-content/uploads/2026/04/9772157ab11b61ad54cb77f2d242975e.pdf),
[ZWO forum: AM5 guiding thread](https://bbs.zwoastro.com/d/15989) and [Dec backlash thread](https://bbs.zwoastro.com/d/15711),
[Rainbow RST-135 FAQ](https://www.rainbowastro.com/faq-items/how-big-periodic-errors-of-rst-135-is/),
[Rainbow user group supplementary manual (FLO)](https://www.firstlightoptics.com/user/products/large/Supplementary%20Manual%20V16.pdf),
[Pegasus NYX-101 guiding recommendations](https://pegasusastro.com/wp-content/uploads/2023/01/NYX-101-Guiding-Recommendations.pdf),
[iOptron HEM27 Q&A](https://www.ioptron.com/Articles.php?ID=346),
[HEM15 review, astropix](https://www.astropix.com/html/reviews/hem15_review.html),
[Sky-Watcher Wave 150i](https://www.skywatcher.com/product/wave-150i/),
[PHD2 group: DIY harmonic mount](https://groups.google.com/g/open-phd-guiding/c/tsptmuvg2uI),
[PHD2 group: AM5 settings](https://groups.google.com/g/open-phd-guiding/c/dFkk2PQK7f4),
[Harmonic Drive CSF catalogue](https://www.harmonicdrive.net/_hd/content/documents/csf-csg.pdf),
[US patent 6459940 (strain-wave error at twice the wave-generator frequency)](https://patents.google.com/patent/US6459940).
† [Cloudy Nights: HEM15 periodic error](https://www.cloudynights.com/forums/topic/903589-hem15-tracking-guiding-periodic-error/)
(read only as a search snippet).

### 4.2 Seeing that is correlated from frame to frame

**Today:** the seeing moves the star by independent random steps in 50 ms cells. The mean over an exposure of T
seconds scatters as 1/√T, and two frames are independent. Short exposures then only cost noise, which the filters
average out over more frames.

**In the atmosphere** the image motion (tilt) has much of its power at low frequencies: its temporal spectrum falls
only as f^(−2/3) below about 0.3·v/D (v the wind speed, D the aperture), down to a cut-off near v/L0 set by the
outer scale L0 (Conan, Rousset & Madec 1995, JOSA A 12, 1559). For a guide scope of a few centimetres the upper corner
lies at tens of hertz; the lower one, for an outer scale of tens of metres, at a fraction of a hertz. Between the two,
the mean over an exposure scatters much more slowly than 1/√T (as T^(−1/6) well inside that range), and successive
frames are correlated. 0.5–2 s exposures lie near the lower corner, so how much this matters depends on wind and
outer scale. Where it does, short exposures see a slow wander the guider can't tell from the mount.

**The option:** extra seeing layers, each a list entry (cell length, σ of the layer), built like the existing one
(piecewise-constant cells, values hashed from seed, layer and cell, so the sky stays a pure function of time). Cells
of 0.5, 2 and 8 s approximate the low-frequency part. The values are not known for any site.

**Built:** `SkySimConfig.SeeingLayers`, a list of `SeeingLayer(CellSec, SigmaArcsec)`. A layer moves all stars
together: motion this slow comes from turbulence on scales of the wind speed times the cell, tens of metres, which the
light of all guide stars crosses together in the ground layer and largely still at 5 km (a 10′ field is 15 m wide
there). For multi-star guiding that is the safe side: it can't average a layer out.

**A limit of the yardstick:** the simulator scores the mount's true pointing error. A guider that follows slow
seeing moves the mount and loses there, although on an off-axis guider part of that motion is shared by the imaged
field. Its scores are therefore conservative for following seeing.

### 4.3 Render speed at short exposures

Measuring the run time found a third gap: below 20 e⁻ per pixel (the sky of the scenarios at
exposures under 1 s) the camera drew each background pixel's photon count with Knuth's method, about mean + 1 random
numbers per pixel, and a 25-minute run at 0.5 s took 130–260 s instead of about 18 s at 1 s. The background is now
drawn by inverting a table of the Poisson distribution, built once per frame for its mean (`PoissonTable`, with a
guide table, Chen and Asau 1974): the same distribution to double precision, one random number per pixel. A run at
0.5 s takes about 40 s. Frames with 20 e⁻ or more (1 s and longer in the scenarios) don't use it and are unchanged.

## 5. Code and tests

Engine:
- `Algorithms/PredictiveAlgorithm.cs`: `ScoreWindowSeconds` 240 and `UsualSigmaSeconds` 720 next to the frame floors;
  λ per frame from N (§3); learned seconds for the learning phase; the σ ring buffer with capacity 4000 and a median
  over its newest N entries. `PredictiveState.Progress` documented as in §3.
- `docs/ALGORITHMS.md`: the Predictive section states the windows in time with their floors.

Simulator (all off by default):
- `Simulation/SimulatorConfig.cs`, `SimulatedMount.cs`: the periodic-error terms of §4.1.
- `Simulation/SimulatorConfig.cs`, `SimulatedSky.cs`: the seeing layers of §4.2.
- `Simulation/SimulatorScenario.cs`: the three harmonic-drive scenarios (§7, decision 1).
- `Simulation/SimRandom.cs`, `SimulatedCamera.cs`: the Poisson table of §4.3.

Tests:
- `PredictiveAlgorithmTests`: N and λ at 0.5, 1, 2 and 8 s intervals (λ exactly 1 − 1/120 at 2 s and longer); the
  learning phase stays "Adapted" when the exposure drops from 2 s to 0.5 s; the median uses the newest N entries;
  after an exposure change N follows within a few frames.
- `SimulatedMountTests`, `SimulatedCameraTests`: the new options reproduce for a seed, are bit-identical when off, and
  their statistics match §4 (the scatter of a T-second mean for the seeing layers, amplitude and phase spread for
  the periodic error). `PoissonTableTests`: the table is the inversion of the distribution (against a plain
  inversion of the closed form), its frequencies, mean and variance, and a faint frame's skew (that of photon noise,
  where a normal approximation has none).
- The guide-step dumps of §3 stay identical with the simulator changes (the 2 s and 2.5 s runs don't touch them).
- The CI suite and the slow tests unchanged; `PredictiveComparisonTests` prints the same RMS as before the change.
  (The figures under "Predictive" in `ALGORITHMS.md` are older than the code: it prints 0.157″, 0.492″, 0.588″,
  0.176″ and 0.152″ for Predictive on both axes, before and after this change.)

## 6. Afterwards: real logs and the sky

- **Real logs (stage 2):** guide logs of harmonic-drive mounts at 0.5–1 s, best unguided runs of PHD2's Guiding
  Assistant. Replayed open-loop like `Data/periodic-error-night-*.csv`, they test the periodic-error estimator and
  the Dec drift estimator on real motion, and they set the ranges of §4.1, from several mounts, not fitted to one
  night. No public set exists: the PHD2 upload links in old threads are dead (all 7 tried, 2026-10-10), a few logs
  hang on PHD2 group threads (an RST-135E with a Guiding Assistant run, a HEM27) without a licence, so their authors
  would have to agree. Asking on the PHD2 group for unguided runs at 0.5–1 s is the practical route. ZWO's per-unit
  factory PE charts (bench curves) can at least bound the amplitude spread.
- **Live (stage 3):** short exposures on the EQMod rig in ABBA blocks (1 s against 2.5 s, `guide_log_stats.py`);
  a harmonic drive only with a tester who owns one, with the same protocol.

## 7. Decisions (2026-10-10)

1. **HarmonicDrive in the app's scenario list:** one variant, B (`SimulatorScenario.Presets` feeds the "Simulator
   scenario" setting); A and C only for tests.
2. **Variant C keeps its 2.4 s line (±1.5″),** although it comes only from a forum snippet.
