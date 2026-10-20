# Cone error measurement — design

Status: approved 2026-10-14, rebuilt on NINA's meridian flip 2026-10-20. Implementation: TNS C# plugin + TNS Vue app, branches `feat/cone-error`. Report: [implementation-report.md](implementation-report.md).

## Purpose

This feature gives PINS users a guided measurement of their telescope's cone error and tells them how to shim it out. Users run it at night from a Touch-N-Stars (TNS) panel. It works offline on the Pi, is usable in the dark, and leaves upstream NINA core untouched.

The mount looks at the same RA/Dec on both pier sides, close to the meridian, and plate-solves each side. The offset between the sides is twice the cone error. The result is the cone error with its spread, the shim thickness, and which end to shim.

## Repositories

| Part | Repository | Base |
|---|---|---|
| Backend: domain math, session, NINA adapters, `/api/cone-error/*` controller, tests | `NINA.Plugins/Touch-N-Stars` (TNS C# plugin) | `unstable` |
| Panel: `src/plugins/cone-error` | `Touch-N-Stars` (TNS Vue app) | `develop` |
| Design and implementation report | pins `docs/cone-error/` | `unstable` |

No NINA core file is changed. The plugin uses only public NINA APIs: `ITelescopeMediator`, `IImagingMediator`, `IPlateSolverFactory` and `IProfileService`.

## The idea: measure the way a sequence flips

Each pair is measured the way a sequence handles a meridian flip:

1. **Goto** a target just east of the meridian. With the counterweight down, the mount sits on the **west** pier side.
2. **Plate-solve** there, half a minute before the target reaches the meridian.
3. **Keep tracking** until the target is the profile's "minutes after meridian" past it.
4. **Flip** with `ITelescopeMediator.MeridianFlip`, the same call a sequence's flip makes. The mount goes to the **east** pier side, still on the same coordinates.
5. **Plate-solve** again.

So nothing forces a pier side. Every mount NINA can flip works with its own driver's flip:

| Driver | What `MeridianFlip` does |
|---|---|
| ASCOM / Alpaca | sets `SideOfPier` when the mount allows it (`CanSetPierSide`), otherwise a goto past the meridian |
| INDI | pins' own flip in `IndiTelescope` |
| Native OnStepX | its own flip in `OnStepXTelescope` |

The reported pier side is checked after every move. If the side is wrong, the run stops with `PierSideNotReached` and makes no further move.

If a mount stays on the east side at the first goto, which some mounts do within their meridian limits, the session asks `MeridianFlip` to bring it to the west side first.

**Equipment facts the design rests on:**
- **Every NINA driver turns tracking on** for a goto and in `MeridianFlip`. If tracking was off before the run, it is turned off again at the end.
- **ASTAP is the only offline solver on Linux.** Solves go straight through `IImageSolver`. `CenteringSolver` and `SolveAndSync` would sync the mount, so they are never used.
- **`TelescopeVM.MeridianFlip` does not settle.** It waits for the dome and the next mount update only, so the session settles itself, using the profile settle time.

## Architecture

### Backend (TNS C# plugin), folder `Touch-N-Stars/ConeError/`

1. **`Domain/`: pure C#, no NINA types, no natives.** All of it is unit-tested.
   - **`ConeMath`:**
     - wrapped ΔRA (hours, ±12 h)
     - per pair `c = ½·(ΔRA_W − ΔRA_E)·cos δ`
     - `ShimMm = L·tan|c|` and the end to shim
   - **`ConeStatistics`:** mean, SD, SE and median, with outlier rejection after Iglewicz and Hoaglin (modified z-score > 3.5, applied from 4 pairs up). It also sets the quality:
     - Good: SE ≤ 0.5′ and SD ≤ 1′
     - Fair: SE ≤ 1.5′
     - Poor: anything else
     - Insufficient: too few pairs
     - not significant: |c| < 2·SE, which gives the advice "no shim needed, or measure again"
   - **`SunEphemeris`:** Meeus, *Astronomical Algorithms* 2nd ed., ch. 25 low-accuracy Sun and ch. 12 sidereal time. It is used only for the darkness check.
   - **`Spherical`:** angular separation, HA↔RA, alt/az.
   - **`TargetPlanner`:**
     - The declination is 0°. When the target would culminate below the minimum altitude (or the custom horizon) plus a 5° margin, it moves toward the visible pole, up to ±30°.
     - The RA puts the west sample 0.5 min before the meridian.
     - The flip is at max(1 min, the profile's "minutes after meridian"). The plan is refused when that is beyond the profile's "max minutes after meridian".
     - It works out the time of every sample and flip and the duration of the whole run.
2. **`Session/`: equipment-free orchestration over small ports.**
   - **Ports:**
     - `IConeMount`: snapshot, `SlewAsync`, `FlipAsync`, `Stop`, `SetTracking`; there is no sync
     - `IConeImager`: capture and solve, returning JNow coordinates
     - `IObservatory`: profile facts and the camera, sequence and guider state
     - `IClock`
   - **`AutoMeasurementSession`:** one CTS, a phase enum, and tracking put back in `finally`.
     - Phases: Preparing → Slewing → (WaitingForAnswer) → Settling → Exposing → WaitingForMeridian → Flipping → Settling → Exposing → … → Complete / Failed / Aborted.
     - Each new pair recomputes the target from the mount's current sidereal time, so the plan cannot drift.
   - **`ConeErrorStore`:** persists to `~/.local/share/NINA/TnsCache/cone-error/`:
     - `settings.json`: minimum altitude (20°), L, repetitions, exposure, and the mounts whose pier labels were confirmed
     - `results.json`: the last 50 results
   - **`ConeErrorService`:** preconditions, plans that expire after 60 s, and the one running session.
3. **`Adapters/NinaConeEnvironment.cs`: NINA bindings.**
   - **`NinaConeMount`:**
     - live values from the device, positions of date (JNow)
     - `SlewToCoordinatesAsync`, `MeridianFlip` and `StopSlew`
     - `SetTracking` compares the state that `SetTrackingEnabled` returns with the request
   - **`NinaConeImager`:**
     - capture through `IImagingMediator.CaptureAndPrepareImage`, with the profile's plate-solve settings
     - solve with `IPlateSolverFactory.GetImageSolver(...)`
     - J2000 → JNow at the exposure midpoint
   - **`NinaObservatory`:** the profile (site, custom horizon, meridian flip settings, settle time, focal length, solver) and the camera, sequence and internal-guider state.
4. **`Server/Controllers/ConeErrorController.cs`**, registered in `TouchNStarsServer.cs`.
   - Newtonsoft camelCase, enums as strings and NaN as null.
   - The envelope is `{success, response}` or `{success: false, error, code}`.
   - The status is 400 for bad input, 409 for a refusal.
   - `TouchNStars.cs` injects `IPlateSolverFactory`, and its `Teardown()` stops a running measurement.

**API under `/api/cone-error/`:**

| Endpoint | Purpose |
|---|---|
| `GET preconditions?clientTimeUtc=` | Mount, profile, camera/sequence/guider state, clock checks, Sun altitude, `blockers`, `warnings`, `ready`. |
| `GET/PUT settings` | Only the fields sent change; the verified-mount list cannot be written by a client. |
| `POST auto/plan` | `{dovetailMm, repetitions, declination?, exposureSeconds?, clientTimeUtc}` → `{planId, expiresInSeconds, plan{declinationDegrees, flipAfterMinutes, pairs[], estimatedSeconds, problems, ok}, askPierLabels, warnings}`. |
| `POST auto/start` | `{planId, noPointingModel}`. Without `noPointingModel: true` it is refused with `PointingModelNotConfirmed`. |
| `POST answer` | `{id, yes}`: the one-time counterweight check. |
| `GET status` | Phase, message, pair i/n, current side, `secondsUntilFlip`, pairs so far, question, error, result, previous result. Always 200. |
| `POST abort` | `StopSlew` and `AbortExposure` at once, then cancel. Stops the mount even when nothing runs. |
| `GET results` | History, newest first. |

### Frontend (TNS app), plugin `src/plugins/cone-error/`

- **Plugin files:**
  - `plugin.json` (`isPins: true`, off by default) and `index.js`
  - `api.js`, plugin-local over `rawHttp`, as in `api/nativeGuider.js`
  - `store/coneErrorStore.js`
- **Tabs:**
  - **Measure:** checks → inputs (L, repetitions, exposure; "More options" holds the declination and the minimum altitude) → plan → run → result.
  - **History.**
- **Run card:** follows `NativeCoachRun.vue`:
  - header with icon, phase and %
  - progress bar
  - one row per pair
  - the countdown to the meridian flip
  - the counterweight question
  - the `tns-btn-danger` "Stop the mount", inline, one tap, no confirmation
- **Polling:** the view polls the status every 2 s with `useBackgroundAwarePolling`. Leaving the panel does not stop the run; the panel shows it again on return.
- **Dark use:** theme tokens only, no bright surfaces, no animation.
- **Strings:** `en.json` only, under `plugins.coneError.*`.

## Wizard flow

1. **Checks.** Blockers:
   - mount not connected or parked
   - camera not connected or exposing
   - solver problem, no focal length, site at 0/0
   - a sequence running, the internal guider guiding
   - Pi clock unset or more than 10 min off the phone
   - mount sidereal time more than 8 min off the Pi's
   - **the Sun above −6°**: the target is near the meridian, so it needs a dark sky

   Warnings: clock skew over 2 min, sidereal time skew over 1 min, and PHD2, which cannot be detected.
2. **Inputs:** L in mm, repetitions (2–10, default 3), exposure (defaults from the plate-solve settings), and optionally the declination and the minimum altitude.
3. **Plan:** declination, altitude, number of pairs, duration and the flip time after the meridian. The user ticks **"No pointing model is active in the mount"** (a model corrects the cone error away), then confirms "the mount moves". The plan is valid for 60 s.
4. **Run:** on the first west arrival of a mount not yet confirmed, a one-time question with a sketch: "Is the counterweight bar on the EAST side of the mount (the tube on the west side)?" "No" stops the run (`PierLabelsInverted`), because the advice would point at the wrong end.
5. **Result:**
   - c in arcminutes ± SD/SE
   - pairs used and outliers
   - shim t in mm, rounded to 0.05 mm, and "Shim the FRONT/REAR mounting point" with a diagram
   - the quality
   - before → after against the last result for this profile and mount

## Measurement algorithm

- **Per sample:**
  - The mount position is the mean of the snapshots right before and after the exposure. The sample is rejected when they differ by more than 30″ or the side changed.
  - Solved J2000 → JNow at the exposure midpoint.
  - ΔRA = wrap(RA_solved − RA_mount). Offsets over 2° are refused as a sync, epoch, time or site problem.
- **Per pair:** c_i = ½·(ΔRA_W − ΔRA_E)·cos δ, in arcminutes. ΔDec(W − E) is kept as a diagnostic.
- **Failed solves:** each side gets two attempts. If both fail, the pair is dropped and the run goes on with the next pair.
- **Run result:** fewer than max(2, ⌈N/2⌉) valid pairs fails the run (`TooFewPairs`). Otherwise the result has the mean, SD, SE, median and the rejected outliers.

## Sign convention (stated once)

- ΔRA = RA_solved − RA_mount, same epoch, wrapped.
- **c = ½·(ΔRA_pierWest − ΔRA_pierEast)·cos δ**, using NINA/ASCOM pier labels. pierWest is the counterweight-down side for a target east of the meridian: the tube is west of the pier.
- **c > 0:** the objective end tilts toward the dovetail and saddle → **shim the FRONT (objective-end) mounting point** by t = L·tan|c|.
- **c < 0:** **shim the REAR (focuser-end) mounting point**.
- **Derivation:**
  - Frame: X = (HA 0, Dec 0), Y = east, Z = NCP; Dec axis u_E = (sin h, cos h, 0) on pierEast, u_W = −u_E.
  - Actual pointing = ideal pointing + c_out·u, with c_out > 0 for an objective end tilted away from the saddle.
  - Then ΔRA_E = +c_out/cos δ and ΔRA_W = −c_out/cos δ, so c = −c_out.
  - The identity ∂p/∂h = −cos δ·u_E makes this hold for every h, in both hemispheres.
- **Checks:**
  - The convention was derived twice independently and confirmed by four reviewers.
  - It is tested against a separate physical forward model (`ConeForwardModel`), which never uses the estimator.
  - TPOINT's and OnStepX's `doCor` signs are not used.
- **Guards against inverted driver labels:**
  - the one-time counterweight question per mount
  - the reported side checked after every move
  - the before/after check after shimming

## Error handling and abort

- **Abort:** `StopSlew` first, then camera `AbortExposure`, then cancel. A goto or flip that fails because of the abort is reported as Aborted, not as a failure. Tracking is put back in `finally`.
- **Moves:**
  - A refused goto or flip (false or an exception) fails with the driver's message (`SlewFailed`, `FlipFailed`).
  - A wrong pier side after a move stops the run (`PierSideNotReached`).
- **Plans:** an expired, unknown or reused `planId` is refused. The preconditions are checked again at the start.
- **Concurrency:** one measurement at a time (`Busy`). The preconditions refuse a running sequence, internal guiding or an exposing camera.

## Not in scope

- **Daytime flip check (landmark, tracking off).** It needs the pier side forced at a fixed hour angle, which only some drivers can do (raw OnStep commands, an INDI OnStep switch). It was built first, then removed in favour of a generic measurement.
- **Separating non-perpendicularity.** The measured value is CH + NP·sin δ, which near δ = 0° is close to CH. Separating the two would need two declinations.

## Test plan (xUnit, `Touch-N-Stars.Tests/ConeError/`)

- **`ConeSignForwardModelTests`:** physical tilts → correct end, both hemispheres, polar misalignment, index error, non-perpendicularity, noise, outliers, inverted labels.
- **`ConeMathTests`, `SunEphemerisTests`:** formulas, statistics, quality bands; Meeus examples 12.a, 12.b, 25.a.
- **`TargetPlannerTests`:**
  - declination suggestion in both hemispheres
  - refusals: altitude, custom horizon, |δ| > 30°, repetitions, flip after the maximum
  - the 1 min minimum flip time
  - sample and flip hour angles, pair order, duration
- **`AutoMeasurementSessionTests`** (fake mount with NINA-like goto/flip behaviour, the forward model as solver, a virtual clock):
  - the full run recovers the tilt and the end
  - each pair is a goto west plus a flip east
  - the flip waits for the meridian
  - a mount that stays east at the goto, a mount that cannot flip
  - a solve failure on either side, second-try solves, all solves failing, movement during an exposure
  - abort while waiting, mid-flip (stop before cancel) and during the question
  - tracking put back
  - minimum altitude
  - the counterweight question (yes, no, wrong id)
  - result storage, noise and outliers
- **`ConeAdapterTests`:**
  - the mount adapter slews, flips through `MeridianFlip`, stops and never syncs
  - `SetTracking` semantics
  - refused, throwing and cancelled flips
  - store round trips
- **`ConeErrorApiTests`:** the real controller on EmbedIO over the fakes: preconditions → plan → start → question over HTTP → result; abort over HTTP; plan reuse, expiry, `Busy`, the pointing-model confirmation, dovetail, sequence, disconnected mount, clocks, daylight, settings patching.
