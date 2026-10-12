# ProxiSky UMi replay nights

Guide logs of four ProxiSky UMi strain-wave mounts, rebuilt as replay data: 13 nights, about 80 guiding hours, about 270 000
frames, 760 dithers. Used by `Algorithms/UmiReplayTests.cs` (loader `UmiNight`); the analysis behind the numbers below is in
[docs/notes/UMI-STRAIN-WAVE.md](../../../NINA.GuideEngine/docs/notes/UMI-STRAIN-WAVE.md).

| Folder | Mount | Imaging scope (load) | Guide camera, scale | Exposure | Nights |
|---|---|---|---|---|---|
| `UMi20S-1` | UMi20S #1 | FSQ106 (light, short lever) | GPM662M, 128 mm, 4.67″/px | 0.5 s | 2026-09-05, 09-11 |
| `UMi20S-2` | UMi20S #2 | Ocal H-2 (much heavier, longer lever) | GPM662M, 250 mm, 2.39″/px | 0.5 s | 2026-09-05, 09-11, 09-14 |
| `UMi17S` | UMi17S | SQA85 | GPM462M, 208 / 250 mm, 2.88 / 2.39″/px | 0.5 s | 2026-09-05, 09-11 |
| `UMi17R` | UMi17R (servo in RA only, other gearbox) | SQA85 (a second one) | GPM462M, 190 / 250 mm, 3.15 / 2.39″/px | 1 s | 2026-08-11, 08-12, 08-22, 09-05, 09-11, 10-05 |

All were guided by PHD2 2.6.15 (pins build) through INDI LX200 OnStep at 1× sidereal guide rate, RA with Predictive PEC
(gain 0.6–0.9), Dec with Resist Switch (aggression 60–90 %), min move 0.1 px, NINA dithering every few minutes. Site
48.5° N; all targets near Dec +39–41°. The 20S and 17S nights of 2026-09-05 and 09-11 ran side by side at the same site.

## Format

One gzip CSV per mount and night (`<mount>/<YYYY-MM-DD>.csv.gz`, the date of the evening). Comment lines first:

- free text: source log, optics, conventions;
- `#calibration,...`: each calibration (time, Dec, pier side, RA/Dec angle and rate, step, backlash-clearing steps);
- `#run,...`: one line per guiding run, after a header line `#run,run,start,...` naming the fields: start (local time),
  hour angle and RA (h, PHD2 header, 0.01 h resolution), Dec, pier side, Alt/Az, RA/Dec rate (px/s; PHD2 has scaled the RA
  rate for the declination), camera angles, exposure, the PHD2 algorithm settings, the PPEC period at the start, and the
  frames PHD2 dropped (no star).

Then one row per frame with a star:

| Column | Meaning |
|---|---|
| `frame` | PHD2 frame number (restarts with every run) |
| `time_s` | seconds since the first run of the night began |
| `settling` | 1 between PHD2's "Settling started" and "Settling complete/failed" |
| `dither` | 1 on the first frame after a dither |
| `ra_px`, `dec_px` | offset along the mount axes (PHD2 RARawDistance / DECRawDistance) |
| `ra_corr_px`, `dec_corr_px` | the pulse that went out after the frame, in px at the run's calibrated rate, positive = reducing a positive offset (West / South) |
| `run` | guiding run (see `#run`) |
| `dither_ra_px`, `dither_dec_px` | lock position change in mount px just before the frame (PHD2 logs "DITHER by" in mount axes) |
| `snr`, `star_mass`, `error_code` | PHD2's values |

The first eight columns are those of `night-2026-09-27.csv`. The open-loop position (the mount's motion as if unguided)
is `offset + Σ lock changes + Σ earlier corrections` per run; `UmiRun.OpenLoop` builds it, with an optional pulse effect.
Across a dither it is continuous to the seeing (median step < 1 px; checked by
`Every_night_loads_and_its_open_loop_motion_is_continuous_over_the_dithers`).

## Measured on these data

- **Pulse effect** (two-stage least squares on the dithers, window ending after two frames without a pulse): RA 1.00–1.04
  on all four (the calibration is right); Dec **0.71 (17R), 0.66 (17S), 0.86 (20S #1), 0.90 (20S #2)**. On 17R, 17S and
  20S #2 part of a Dec pulse's motion reaches the star only in the frame after the next (~150–250 ms late).
- **Dec backlash** is negligible (≤ 0.4″ at a reversal); Dec has no periodic error.
- **RA periodic error**, see the note: lines at ~100/day, ~200–202/day, exactly 300/day with its harmonics (k = 3, 6, 9, 12,
  15 of 861.6 s), and non-integer lines at 106.8 s (all four) and 80.5 s (17S only).
- **Seeing** (< 20 s part of the open-loop motion, on the sky): σ 0.41–0.59″ per axis, correlated over 2–3 frames (lag 1:
  0.45), RA and Dec uncorrelated.

## Known events

- `UMi20S-1/2026-09-05.csv.gz`, run 6, from t ≈ 21 150 s (2026-09-06 03:30:56, hour angle +0.33 h): the mount stops
  tracking (OnStep meridian limit before NINA's flip); RA runs away at 11.3″/s (sidereal × cos Dec) for 158 s while
  PHD2 sends 250 ms pulses, then NINA flips. Leave out the last 160 s of that run for statistics.
- `UMi20S-1/2026-09-11.csv.gz`, run 14, 2026-09-12 04:44:42: a single frame 59″ RA / −35″ Dec off (a centroid glitch, SNR
  normal); the next frame is back.
- Several 2–6″ jumps within seconds on two or three mounts at once (e.g. 2026-09-06 00:35:46–52 and 02:48:45–47): wind.
- Runs shorter than a minute at Dec 0 are the guiding right after a calibration.
