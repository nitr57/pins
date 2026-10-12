# ProxiSky UMi strain-wave mounts — what their guide logs show

Status: **analysis** (2026-10-12) of 13 nights of PHD2 guide logs on four ProxiSky UMi mounts (UMi20S ×2, UMi17S,
UMi17R), about 80 guiding hours. The nights are replay data in `NINA.GuideEngine.Test/Data/umi` (format and
provenance in its [README](../../../NINA.GuideEngine.Test/Data/umi/README.md)); `UmiReplayTests` replays them. Nothing in
the engine was changed for this note.

**In short.**

- The RA error of these mounts is not a worm with harmonics. It is several line families: ~100/day, a close pair at
  200/202 per day, exactly 300/day with its harmonics up to the 5th, and non-integer lines (106.8 s on all, 80.5 s on the
  17S). Detection locks onto one of them; a tooth count of 100 fails the stability test, which looks at the fundamental only.
- All of it is slow (at most ~0.2″/s). In a closed loop over the mounts' rebuilt motion, the periodic-error prediction
  never switched on and would not have mattered much. What matters is not chasing the seeing: against the PHD2
  settings the nights were guided with, Predictive at pace 0.5 cut the true RMS by 12–27 % on the 20S and 17S, and so did
  soft Classic settings; on the 17R neither changed it by more than a few percent (Classic soft made it worse).
- The seeing is correlated over 2–3 frames. Then the measured RMS prefers more aggressive settings than the true RMS does:
  a trial judged on the measured RMS can pick the worse setting.
- Dec pulses move the star 0.66–0.90 of the calibrated amount; RA pulses 1.00–1.04. Dec backlash is negligible.
- One mount stopped tracking at its meridian limit three minutes before NINA's flip, and PHD2 guided on as the star ran
  away at the sidereal rate.

## 1. Data

PHD2 2.6.15 (pins build), INDI LX200 OnStep, guide rate 1×, RA Predictive PEC (gain 0.6–0.9), Dec Resist Switch
(60–90 %), min move 0.1 px, exposures 0.5 s (17R: 1 s), frame interval 0.86–0.9 s (17R 1.35 s), a dither every few
minutes, targets at Dec +39–41°. The 20S and 17S nights of 09-05 and 09-11 ran side by side.

The open-loop motion is rebuilt per run as offset + lock changes + earlier corrections (PHD2 logs `DITHER by` in mount
axes; the camera→mount transform reproduces the logged raw distances to < 0.3 px). Unless stated otherwise, RA is in
RA-axis arcsec (sky ÷ cos Dec) and Dec on the sky.

## 2. Pulse response

From the dithers (an exogenous step), two-stage least squares with the dither as instrument, each window ending after two
frames without a pulse so that every pulse is fully visible:

| Mount | RA effect | Dec effect | Dec, part arriving a frame late |
|---|---|---|---|
| UMi20S #1 | 1.00 | 0.86 | small |
| UMi20S #2 | 0.97 | 0.90 | yes, ~150 ms |
| UMi17S | 1.03 | 0.66 | yes, ~200 ms |
| UMi17R | 1.04 | 0.71 | yes, ~250 ms |

- A fixed loss per pulse (dead time) fits better only when the window ends right after the pulses. With fully visible
  windows the loss per pulse is none within its uncertainty on RA (e.g. 17R 13 ± 267 ms) and 32–74 ms on Dec (17S, 17R). The apparent dead time is late arrival, as the EQMod rig showed
  ([DEC-PULSE-MODEL.md](DEC-PULSE-MODEL.md)).
- Dec reversals lose ≤ 0.4″ (−0.2 … +0.5 px); the effect does not depend on N/S or pier side.
- The pulse model's design (an effect per axis, learned from the dithers) fits these mounts; the effects here would be
  learned and are within its 0.67–1.5 bounds except the 17S's 0.66.

## 3. RA periodic error

### 3.1 Lines

Averaged periodograms of the open-loop RA over the runs ≥ 40 min, then a per-run least-squares comb; median per-run
amplitude in RA-axis arcsec (k = harmonic of 861.6 s = sidereal day / 100):

| | k1 | k2 | k3 | k4 | k6 | k9 | k12 | k15 | 106.8 s | 80.5 s |
|---|---|---|---|---|---|---|---|---|---|---|
| UMi20S #1 | 1.5 | 4.5 | 1.9 | 0.5 | 0.8 | 0.2 | 0.2 | 0.3 | 1.0 | 0.2 |
| UMi20S #2 | 1.5 | 4.1 | 4.0 | 0.5 | 1.0 | 0.4 | 0.7 | 0.4 | 0.8 | 0.1 |
| UMi17S | 1.6 | 5.2 | 5.6 | 0.5 | 1.1 | 0.4 | 0.3 | 0.6 | 0.8 | **1.3** |
| UMi17R | **10.7** | 6.0 | 2.5 | 1.0 | 0.9 | 0.6 | 0.5 | 0.4 | 0.7 | 0.2 |

Between 2 and 20 s RA and Dec have about the same power on the sky (RA/Dec PSD ratio in axis units 1.3–2.1 against
1/cos² Dec = 1.6–1.8): no mechanical error at short periods, the rest is seeing. Exception, weakly: the 20S #1 has
2.0–2.8 there, ~25 % more RA than Dec amplitude at 5–20 s (servo jitter of its RA PID?); the 20S #2 ~10 %. Dec has no line (per-run Dec amplitudes at the RA lines are at
noise level, coherence with RA mostly < 0.5).

### 3.2 Periods (incoherent sum over the runs, bootstrap 68 %)

| Line | 20S (both) | 17S | 17R | per sidereal day |
|---|---|---|---|---|
| fundamental (with k2, k3) | 860.4 s (860.0–861.0) | 859.4 s (858.7–860.0) | **856.5 s (855.8–857.1)** | 100.15 / 100.26 / **100.60** |
| k2 line | 430.3 s | **426.0 s** (424.8–427.1) | 427.8 s | 200.2 / **202.3** / 201.4 |
| k3 family (with harmonics 2–5) | 286.9 s | 287.4 s | 287.4 s | **300.0** on all |
| 106.8 s | 106.1 s | 106.8 s | 106.9 s | 812 / 807 / 806 |
| 80.5 s | — | 80.5 s | — | 1070 |

Reading (an interpretation of a strain-wave gear, not a vendor specification):

- **300/day with harmonics 2–5** is a stage geared rigidly to the axis turning 300 times per axis turn (e.g. motor or
  pulley for 100:1 × 3:1). Its curve is sharp (strong k6, k9, k12, k15).
- **200 and 202/day** are the strain-wave pair: twice per wave-generator turn against the fixed circular spline (2 × 100)
  and against the flexspline that turns with the axis (2 × 101). Within a run of an hour they are one line; over a night
  they beat (1 cycle/day apart, 15°/h). The 20S is dominated by 200, the 17S by 202, the 17R lies in between.
- **The 17R's fundamental (100.6/day)** is no whole number: not geared rigidly to the axis (a belt?); it has a different
  gearbox.
- 106.8 s and 80.5 s are not whole numbers per day either.

### 3.3 Phase against the RA axis angle

Per run, phase at the axis angle (hour angle + 12 h on the West pier side, as the guider forms it), circular
concentration R over the runs of all nights (1 = the same phase every time):

| | 300/day West | 300/day East | 200/day West / East | 100/day West / East |
|---|---|---|---|---|
| UMi20S #1 | 0.89 | 0.97 | 0.67 / 0.95 | 0.90 / 0.73 |
| UMi20S #2 | 0.96 | 0.76 | 0.69 / 0.50 | 0.55 / 0.69 |
| UMi17S | 0.93 | 0.86 | 0.86 / 0.69 | 0.94 / 0.80 |
| UMi17R | 0.26 | 0.42 | 0.27 / 0.36 | 0.27 / 0.16 |

- The 300/day family keeps its phase over nights on each pier side, but **shifts by 70–86° at the flip** (20S #1 +71°,
  20S #2 −86°, 17S −69°), where 300 × 180° predicts 0°. That is about 14′ of axis angle: the hour angle the mount reports
  after the flip and sync is not the mechanical axis angle by that much. A stored curve restored after a flip by
  "teeth × 180°" would be off by that.
- On the 200/202 pair the phase walks through a night (the beat). On the 17R nothing is locked.
- A model locked to the axis angle over all nights of a mount (lines 100, 101, 200, 202, 300, 303) leaves 4.5″ RMS on
  the 17S where a fit per run leaves 3.4″; on the 17R 10.1″ against 4.7″. On these mounts the curve has to be learned per
  session; a stored one is a start at best.

### 3.4 The periodic-error estimator on these nights

`Periodic_error_estimator_on_the_nights` (each night in one estimator, runs as segments, axis angle from the run headers):

- detect: the 17R's ~851–859 s in 4 of 6 nights; the 17S 287.4 s in 1 of 2; the 20S #1 431 s (small against the seeing)
  in 1 of 2; the 20S #2 426 s in 1 of 3. The other nights end with no period; candidates fail "not significant in every
  part" with part amplitudes of 5–9″ against 2″ noise: the lines the model leaves out (430 s when it holds 287 s, 287 s
  when it holds 430 s) count as noise.
- `WormTeeth` 100: 861.6 s, mostly "not stable" or "not significant". Every part must show a significant fundamental,
  and on the 20S and 17S the fundamental is ~1.5″ while k2 and k3 are 4–6″.

## 4. Dec

Polar alignment from the open-loop Dec drift against the hour angle per night: 1.3–3.1′ on all mounts, consistent over
nights; drift 0.2–1″/min. Dec guide mode Drift has no backlash to avoid on these mounts.

## 5. Seeing and the true error

The part of the open-loop motion under 20 s (on the sky): σ 0.41–0.59″ per axis, lag-1 autocorrelation 0.42–0.49 (lag 2
0.22–0.27, lag 3 0.07–0.16), RA·Dec correlation −0.01 … +0.09. That is seeing: equal on both axes, independent between them,
and correlated over 2–3 frames at 0.9–1.35 s per frame.

The closed loop below takes the 20 s mean of the open-loop motion as what the mount really did (the long exposure averages
the seeing) and judges each algorithm on it (true RMS). The measured RMS adds the seeing, and since the seeing is
correlated, a loop that follows it lowers the measured RMS while it moves the mount. Example, 20S #1 RA: pace 1.0
measured 0.473″ / true 0.420″, pace 0.5 measured 0.505″ / true 0.378″. **The measured RMS ranks these the wrong way
round.** The Coach's trial judge works on the measured RMS.

## 6. Closed loop over the mounts' motion

`Closed_loop_on_the_mounts`: every run ≥ 20 min is guided again by the algorithm set; the algorithms see the rebuilt
open-loop motion (seeing included) minus the lock and their corrections, which reach the star with the axis's measured
pulse effect, RA 10 % and Dec 35 % of it a frame late; dithers are recentred at once; max durations, min pulse 20 ms.
True RMS RA / Dec on the sky (total), frame-weighted:

| Algorithms | UMi20S #1 | UMi20S #2 | UMi17S | UMi17R |
|---|---|---|---|---|
| Classic with the night's PHD2 settings | 0.45 / 0.49 (0.66) | 0.44 / 0.48 (0.65) | 0.53 / 0.49 (0.72) | 0.43 / 0.43 (0.61) |
| Classic soft (RA 0.5, Dec 40 %, min move 0.3″) | 0.38 / 0.33 (0.50) | 0.41 / 0.35 (**0.54**) | 0.49 / 0.39 (**0.63**) | 0.46 / 0.49 (0.67) |
| Predictive, pace 0.5 | **0.38 / 0.30 (0.48)** | 0.43 / 0.33 (**0.54**) | 0.52 / 0.37 (0.64) | 0.45 / 0.43 (0.62) |
| Predictive, pace 0.5, pulse model | 0.38 / 0.32 (0.49) | 0.43 / 0.34 (0.55) | 0.52 / 0.42 (0.67) | 0.45 / 0.40 (**0.60**) |
| Predictive, pace 1.0 | 0.42 / 0.38 (0.56) | 0.46 / 0.43 (0.63) | 0.55 / 0.47 (0.72) | 0.44 / 0.40 (0.60) |
| Predictive, pace 0.5, pulse model, PE detect | as without PE | as without PE | as without PE | as without PE |

- Pace 0.5 beats pace 1.0 on all four; most of the gain against the PHD2 settings is on Dec (Resist Switch at 90 %).
- Soft Classic settings do as well as Predictive on the 20S #2 and the 17S, nearly on the 20S #1: the gain is not
  chasing the seeing, which either can do. Predictive gets there without tuning, and does not lose on the 17R.
- On the 17R nothing helps much: its 11″ fundamental needs a loop that follows, and softer settings lose on RA what
  they win on Dec.
- The pulse model helps the 17R's Dec and costs a little on the others, where a share of each Dec pulse arrives late:
  lengthened pulses put more of it into the next frame. Without late arrival (second test case) it is neutral to
  slightly better everywhere.
- The periodic-error prediction never switched on in a run (runs of 20–180 min, detection after five turns).

Limits: the rebuilt motion is what PHD2's guiding left plus its corrections; the late share and the pulse effect are
single numbers per axis; a dither is recentred instantly; the "true" error assumes the main camera does not share the
guide camera's seeing under 20 s. An on-sky A/B on star FWHM and eccentricity is the check.

## 7. Events

- **Tracking stopped at the meridian limit** (20S #1, 2026-09-06 03:30:56, hour angle +0.33 h): RA ran away at 11.34″/s =
  15.04″/s × cos 41.3° for 158 s, PHD2 sending 250 ms pulses each frame, until NINA ended guiding for its flip. OnStep's
  limit was earlier than NINA's flip time. A guider can tell this signature at once (an RA error growing at the sidereal
  rate × cos Dec while every pulse is at its maximum); the native guider pauses on "tracking off" only if the mount
  reports it.
- **One-frame centroid glitch** (20S #1, 2026-09-12 04:44:42): +59″ RA / −35″ Dec for one frame. The max duration kept the
  damage to ~1″.
- **Wind**: 2–6″ frame-to-frame jumps on two or three mounts within seconds of each other (e.g. 2026-09-06 00:35:46–52,
  02:48:45–47); 976 jumps over 6σ in all, 78 coincident groups (rough chance level ~30).

## 8. What this suggests for the engine (not done)

1. **Predictive at pace 0.5 for these mounts** (or Classic much softer than the PHD2 profiles here): the one change with a
   clear gain, on three of four; Predictive needs no tuning and holds on the 17R.
2. **Trial judge on a seeing-robust measure.** The RMS of the frame errors averaged over ~10–20 s (or of the lowpassed
   error) ranks the settings like the true RMS in §6; the raw measured RMS can prefer the aggressive one.
3. **Periodic error for strain-wave mounts** (gain small, see §6): test stability on the total harmonic power, not the
   fundamental only; allow more than three harmonics (the 300/day family needs five); take a period in seconds besides a
   tooth count (the 17R's 856.5 s is 100.6 teeth); do not assume the phase survives a flip (70–86° here).
4. **Runaway at the sidereal rate**: alert and pause when the RA error grows at ~15″/s × cos Dec with saturated pulses for
   a few frames, whether or not the mount reports tracking off.
5. **Pulse model with late arrival**: the effect alone is learned; where a share arrives a frame late, lengthening the
   pulses adds to it. A late share learned alongside the effect (as in [CORRECTION-PACE.md](CORRECTION-PACE.md)) would
   let the pulse model help the Dec of the 17S/20S #2 too.

## 9. Reproducing

- Data: `NINA.GuideEngine.Test/Data/umi`, rebuilt from the PHD2 logs by `NINA.GuideEngine/tools/phd2-log-replay`.
- `dotnet test NINA.GuideEngine.Test --filter "FullyQualifiedName~UmiReplayTests"` checks the data; the explicit
  `Closed_loop_on_the_mounts` and `Periodic_error_estimator_on_the_nights` print §6 and §3.4.
- §2, §3.1–3.3, §4, §5 and §7 come from numpy scripts on the same files, not in the repository (dither IV fits, Welch and direct periodograms,
  per-run least-squares combs, bootstrap over runs); the method is described with each table.
