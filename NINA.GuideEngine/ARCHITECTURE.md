# NINA.GuideEngine Architecture

## Purpose

`NINA.GuideEngine` is the engine of the pins internal guider: a guiding engine with PHD2-level precision that runs inside the
application instead of the external PHD2 process. It finds and tracks guide stars, calibrates, computes corrections with
PHD2's guide algorithms and its own extensions (Predictive, periodic error, Dec guide mode Drift, pulse model), settles
and dithers, watches for faults, records incidents, writes a PHD2-compatible guide log and runs the Guiding Coach.

The engine was written by André Duffeck as the `pins-guider` plugin (`PinsGuider.Engine`, imported from commit
`0cec403`) and is developed in pins from now on. It has no dependency on any NINA project. Its host is the guider device
`InternalGuider` in `NINA.Equipment/Equipment/MyGuider/Internal` (see Host Boundary); how to use the guider is in
[docs/USAGE.md](docs/USAGE.md).

Build shape from `NINA.GuideEngine.csproj`:

- Target framework: `net10.0`
- Output type: `Library`
- Project references: none
- Implicit usings and nullable reference types enabled; unsafe code allowed (pixel loops)

## Top-Level Structure

- `Core/`
  Value types shared by everything (`GuideFrame`, `GuidePoint`, directions, `PulseCommand`, optics, sidereal time) and
  the host contracts in `Hardware.cs` and `IClock.cs` (see Host Boundary).
- `Imaging/`
  Frame preprocessing: dark library, defect map, noise reduction, binning (`FramePreprocessor`, `ImageMath`), and a
  FITS writer for incident frames.
- `Stars/`
  Port of PHD2's star finding (`StarFinder`, `StarAutoFinder`, `MassChecker`).
- `MultiStar/`
  Port of PHD2's multi-star tracking with the primary-dropout fallback, and the measurement uncertainty model.
- `Calibration/`
  Calibration process, step calculator, sanity checks, the stored-calibration adjuster (binning, declination, pier
  side), the post-flip Dec self-check and the camera-to-mount transform.
- `Algorithms/`
  Guide algorithms behind `IGuideAlgorithm` (Hysteresis, Lowpass, Lowpass2, ResistSwitch, Predictive, identity), the
  per-axis corrector, min-move, pulse limiter, backlash compensation, Dec guide mode, Dec drift estimator, periodic error
  and the pulse model with their stores.
- `Guiding/`
  `Guider`: the loop (expose, process, correct), state machine, settling, dithering, safety monitors, faults and events
  (`GuiderEvents`). Split into partial files by concern.
- `Stats/`
  Guiding statistics (RMS, peak, drift, polar alignment error, oscillation, duty, SNR).
- `Logging/`
  PHD2-compatible guide log (`GuidingLog`) and the bridge from guider events to it.
- `Incidents/`
  Flight recorder: keeps the last minutes of frames and telemetry, saves an incident when something goes wrong and
  diagnoses it. See [docs/INCIDENTS.md](docs/INCIDENTS.md).
- `Coach/`
  Guiding Coach: camera check, drift, mount response and guided trials, with a report card. See
  [docs/COACH.md](docs/COACH.md).
- `Simulation/`
  Closed-loop simulator (sky, camera, mount with drift, periodic error, seeing and backlash) on a virtual clock, used by
  the tests and as a selectable guide source.
- `docs/`
  [USAGE.md](docs/USAGE.md) (how to set up and use the guider), [ALGORITHMS.md](docs/ALGORITHMS.md) (the algorithms that are not PHD2 ports and the simulator results behind them),
  [COACH.md](docs/COACH.md), [INCIDENTS.md](docs/INCIDENTS.md), and design notes in `docs/notes/`.
- `tools/`
  PHD2 parity harness, PHD2 golden-sequence generator and a guide log statistics script, see
  [tools/README.md](tools/README.md).

## Host Boundary

The engine is host-agnostic and deterministic under test. Everything outside it comes in through the interfaces in
`Core/Hardware.cs` and `Core/IClock.cs`:

- `ICameraSource` (optionally `IGainRange`): connects, captures `GuideFrame`s, aborts, reconnects.
- `IPulseOutput`: sends a guide pulse and returns when it is done.
- `IMountState`: a snapshot of the mount (connected, declination, pier side, slewing, parked, tracking, guide rates).
- `IClock`: time, so tests and the simulator run on a virtual clock.

The engine reports through `GuiderEvents`, which use PHD2's event names and fields. Persistence (calibrations, periodic
error, pulse model, darks, incidents, coach reports) goes through stores whose location the host chooses.

The host, `InternalGuider` in `NINA.Equipment`, implements them with pins' equipment:

- `GuideCameraSource`: the guide camera slot (`IGuideCameraMediator`). Frames are captured and downloaded through the
  slot; while the guider is connected it holds the slot's capture block, so no other capture runs between two guide
  frames. The slot chooses, connects and configures the camera; the guider connects it if needed but never
  disconnects or reconnects it, and disconnects itself when the camera goes away.
- `MountPulseOutput` (pulse guiding through `ITelescopeMediator`, waiting for the pulse to end), `CameraSt4PulseOutput`
  (an INDI guide camera's ST4 port) and `NinaMountState`.
- The engine's `Simulator` when the `GuideSource` setting is `Simulator`.

It also implements `IGuider` for the sequencer and ninaAPI, and the `IAdvancedGuider` contract through which the
Touch-N-Stars plugin shows frames, steps, calibration, statistics and settings and runs the Guiding Coach and the
incident recorder. Its settings are kept per profile in the plugin settings under the id of the pins-guider plugin
(`InternalGuiderOptions`); a setting that was never set falls back to the PHD2 setting of the profile. Its files live
in `~/.local/share/NINA/InternalGuider/`.

## Engine Design

The core is a faithful port of PHD2 (commit `a6c02722`, BSD-3). Ported files keep the PHD2
copyright header and name the source file (see `THIRD_PARTY_NOTICES.md`). KStars (GPL) is used for
ideas only — no code is copied.

### Imaging pipeline
Capture → dark subtraction **or** defect-map correction → optional noise reduction (2×2 mean /
3×3 median) → optional software binning → star processing. Frames are `ushort` with a pedestal.
* Dark library: 5 frames per exposure, pick smallest dark ≥ exposure else longest;
  `pedestal = max(median(dark) − median(light), 0)`, `out = clamp(light + pedestal − dark)`.
* Defect map: master dark, 15×15 median filtered, σ-threshold hot/cold pixels, replaced by the
  median of their 8 neighbours. Replaces dark subtraction when loaded.
* No auto-exposure in v1.

### Star finding (port of `star.cpp`)
* `Star.Find`: 3×3 [1 2 1] smoothed peak search within ±searchRegion; background annulus
  r ∈ (7, 12] with iterative 2σ clipping; single-pass background-subtracted first moment over
  pixels r ≤ 7 above bg + 3σ; SNR = mass / sqrt(mass/0.5 + σ²·n·(1 + 1/nbg)); HFD by cumulative
  mass interpolation; saturation test; result codes (LowMass <10, LowSNR <3, LowHFD <1.5, HiHFD >20,
  MassChange, Saturated, Edge, Error).
* `AutoFind`: median3 → optional ×2 downsample → 9×9 PSF convolution → local maxima → merge,
  crowding and edge rejection → 3-pass selection → multi-star candidate list.
  Extra (KStars idea, own code): penalise lowest-HFD candidates (hot pixels), crowding, edges.
* Mass checker: 0.5 threshold, 45 s window, median + high/low water marks.

### Multi-star (port of `guider_multistar.cpp` + fallback)
* Up to 9 stars, min SNR 6. Always full frames; per frame only small search boxes around known
  stars are measured; full-frame AutoFind only at start, reacquire, and after dither recovery.
* PHD2 combination: 5-frame stabilisation, 5σ enter / 2σ exit, per-star 2.5σ rejection, SNR-ratio
  weights, weighted offset used only if smaller than the primary-only offset; hot-pixel (zero
  displacement) eviction; reference re-snap after lock moves. Optional jump filter.
* **Primary-dropout fallback** (default on, toggle): if the primary is lost/saturated for a frame and
  ≥ 3 secondaries agree, the primary position is estimated from the secondaries' displacements and
  guiding continues instead of emitting StarLost.
* Optional subframe mode: single star, faster download.

### Calibration (port of `scope.cpp`, `mount.cpp`, `calstep_dialog.cpp`)
* Distance `ceil(max(25 px, 20″/scale))`; step from guide rate, declination, 12 steps, 50 ms
  rounding. Default 750 ms if rate unknown.
* State machine WEST → EAST → CLEAR_BACKLASH → NORTH → SOUTH → NUDGE_SOUTH → COMPLETE with fast
  recenter; separate xAngle/yAngle and xRate/yRate; PHD2 camera→mount transform with yAngleError.
* Sanity alerts (overrideable warnings): steps < 4, orthogonality error > 12.5°, RA/Dec rate ratio vs
  cos(dec) off by > 0.2, Dec rate differs > 20 % from previous calibration.
* Reuse: stored per profile keyed by camera + mount + binning + focal length; adjustments for
  binning, declination (skip / alert if calibrated above |60°|), pier side (flip xAngle, Dec flip per
  mount setting), guide-rate change > 5 % alert, pixel-size change invalidates.
* **Post-flip Dec self-check**: for the first 5 guide frames after a pier-side change, verify Dec
  corrections reduce the error; if the error grows consistently in the corrected direction, invert
  Dec, alert, persist the mount setting. Besides the self-check, a Dec runaway within 15 minutes after
  a flip inverts Dec once (alert + persisted setting) before a second runaway stops guiding.

### Guide algorithms
Pluggable `IGuideAlgorithm` (so PPEC/GP and dark guiding can land in v2).
* RA default Hysteresis (aggression 0.7, hysteresis 0.1), alternative Lowpass2 (aggressiveness 80 %).
* Dec default ResistSwitch (aggression 1.0, fast switch on), alternatives Hysteresis/Lowpass2.
* Min-move default `max(0.1515 + 0.1548/scale, 0.15)` px.
* Dec guide mode Off / Auto / North / South, and Drift (not in PHD2: one direction along the measured drift, see
  [ALGORITHMS.md](docs/ALGORITHMS.md#dec-guide-mode-drift)). Dec backlash compensation (adaptive, PHD2 port; as in PHD2 only while both Dec
  directions are guided).
* Max pulse 2500 ms per axis (hard clamp).
* Min pulse 20 ms (not in PHD2; 0 = PHD2, any length), for every algorithm: an algorithm or deduced pulse shorter than
  it is rounded to 0 or to it, whichever is nearer, before the Dec backlash compensation. EQMod drops pulses under
  10 ms, and a 10 ms Dec reversal left a Sky-Watcher Dec motor running for seconds.

### Loop, timing, settling, dithering
* Fixed-cadence loop on its own task: expose → process → send RA pulse then Dec pulse (per-mount
  option: simultaneous) → wait for pulse completion → next exposure.
* Settling exactly as PHD2: EMA α = 0.3 of offset distance (RA-only when RA-only), value 100 if no
  star for > 20 s, success after `time` in range, failure after `timeout`. Mapped from NINA
  `SettlePixels`, `SettleTime`, `SettleTimeout`.
* Dither: random ±amount × scale (RA-only option), mount→camera conversion, edge reflection;
  fast recenter moves after dither and calibration.
* Events use PHD2 names and fields (GuideStep, StarLost, SettleBegin/Settling/SettleDone,
  StartCalibration/Calibrating/CalibrationComplete/CalibrationFailed, GuidingDithered, Paused,
  Resumed, LockPositionSet, StarSelected, Alert, AppState). No PHD2 TCP/JSON-RPC server.
* `Guider.Timing` (`CycleTimer`): the loop timestamps each cycle (capture start, frame ready, processed, pulses
  handed over, pulses done) and reports the last cycle, medians of the last 20 and the frame rate. A cycle broken
  off by a failed capture, a pause or the loop stopping is dropped. Timestamps come from `IClock.UtcNow`, so tests
  in virtual time measure virtual cycles.

### Statistics
RMS RA/Dec/total (population σ over a window, px and ″, dither/settle excluded), peak per axis,
drift RA/Dec (″/min, linear fit), polar alignment error estimate (3.8197·|decDrift px/min|·scale /
cos dec), oscillation index, correction duty (% frames with a pulse), SNR min/avg, star count.

### Guide log
PHD2-compatible guide log format so PHD2LogViewer and similar tools can analyse sessions.

### Safety and error handling

| Condition | Behaviour |
|---|---|
| Star lost | No corrections; reacquire 60 s (last position, then full-frame AutoFind accepting only a candidate within 3× the search region of the last position, mass/SNR plausibility vs previous star); then the critical alert once, and the guider keeps searching (like PHD2) and resumes when the star returns. `SafetySettings.StopOnLostStarTimeout` stops instead (`Failed` + event). No timer runs while guiding is paused |
| Max pulse | Hard clamp per axis; repeated limit hits → alert |
| Runaway (total correction per minute over limit) | Stop guiding + alert |
| Mount not responding (≥ 3 consecutive large RA pulses, no star movement; Dec pulses are legitimately absorbed by backlash) | Stop guiding + alert |
| Calibration sanity failure | Warning, user may override |
| Pier side changed | Auto flip per rules + Dec self-check (see Calibration); option to force recalibration |
| Mount slewing / parked / homing / tracking off | Auto-pause; resume after reacquire + settle; after large move only on sequencer request |
| Camera stall / BLOB timeout | Retry exposure ×2 → reconnect camera ×1 → `Failed` |

Every error has a stable code, a human explanation and a suggested fix, used identically in logs,
API and TNS. Guiding-stopping errors additionally trigger a push notification.

The guider keeps searching after a lost-star timeout because NINA does not stop imaging when a guider
stops: a passing cloud would otherwise leave the rest of the night unguided unless a Restore Guiding
trigger exists.

## Tests

`NINA.GuideEngine.Test` holds the engine tests (NUnit, FluentAssertions):

- unit tests on synthetic star fields with sub-pixel truth;
- golden tests against sequences generated from PHD2's own algorithm sources (`Golden/phd2-golden.json`, regenerated by
  `tools/phd2-algo-golden`);
- replays of real guide-log data (`Data/*.csv`);
- closed-loop simulator tests.

The `Slow`, `Benchmark` and `Performance` categories take several minutes and are excluded from the default run:

```sh
dotnet test NINA.GuideEngine.Test --filter "TestCategory!=Slow&TestCategory!=Benchmark&TestCategory!=Performance"
```

`Parity` compares star finding with PHD2's compiled `star.cpp` and is skipped unless the CLI from
`tools/phd2-parity/build.sh` exists. Benchmarks that run for many minutes are `[Explicit]`.

## Dependency Position

- Depends on nothing but the .NET base library.
- Must stay free of NINA references, so the engine can be tested, replayed and simulated without the application.

## Contribution Notes

- This project keeps its own code style through its own `.editorconfig` (`root = true`): Allman braces, file-scoped
  namespaces, LF line endings. The pins `.editorconfig` does not apply here.
- Every file starts with an `SPDX-License-Identifier`. Files ported from PHD2 are `MPL-2.0 AND BSD-3-Clause`, keep the
  PHD2 copyright lines and name the PHD2 source file and commit they were ported from (see
  [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)). A deliberate deviation from PHD2 is marked `Deviation from PHD2`
  in a comment that gives the reason.
- Numerical changes need tests with reference values: PHD2 golden sequences, parity runs or simulator results, as in
  [docs/ALGORITHMS.md](docs/ALGORITHMS.md).
