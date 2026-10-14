# Cone error measurement — design

Status: approved 2026-10-14. Implementation: TNS C# plugin + TNS Vue app, branches `feat/cone-error`.

## Purpose

This feature gives PINS users a guided measurement of their telescope's cone error and tells them how to shim it out. Users run it from a Touch-N-Stars (TNS) panel. It works offline on the Pi, is usable in the dark, and leaves upstream NINA core untouched.

There are two modes:
- **A, automated:** the same RA/Dec is plate-solved on both pier sides near the meridian. The result is the cone error with its spread, the shim thickness, and which end to shim.
- **B, manual flip check (daytime):** the mount moves between the same hour angle and declination on the east and west pier sides, while the user watches a distant landmark. The Sun must stay outside an exclusion radius at every move.

## Repositories

| Part | Repository | Base |
|---|---|---|
| Backend: domain math, sessions, NINA adapters, `/api/cone-error/*` controller, tests | `NINA.Plugins/Touch-N-Stars` (TNS C# plugin) | `unstable` |
| Panel: `src/plugins/cone-error` | `Touch-N-Stars` (TNS Vue app) | `develop` |
| Design and implementation report | pins `docs/cone-error/` | `unstable` |

No NINA core file is changed. The plugin consumes existing public NINA APIs (`ITelescopeMediator`, `IImagingMediator`, `IPlateSolverFactory`, `IProfileService`) and the pins-specific `OnStepXTelescope` and `INDIClient`.

## Equipment facts the design rests on

- **Every NINA driver turns tracking on** before a slew and in `MeridianFlip`. A slew with tracking off is impossible, so mode B turns tracking off again after each move.
- **`ITelescope.SideOfPier` is get-only, and the ASCOM and INDI telescope classes are internal.** A pier side can only be forced through each mount's "preferred pier side":
  - native OnStepX: `:SX96,E#` / `:SX96,W#`
  - INDI `lx200_OnStep` / `lx200_proxisky`: the "Preferred Pier Side" switch

  Every other mount is refused with an explanation.
- **ASTAP is the only offline solver on Linux.** Its star databases are installed by pinsdaemon (D50, D05, G05, W08). Solves go through `IImageSolver` directly. `CenteringSolver` and `SolveAndSync` would sync the mount, so they are never used.
- **Mount-reported limits exist only on native OnStepX** (`GetMountSettings()`: horizon, overhead and meridian limits) **and INDI OnStep** ("Minutes Past Meridian", "Slew elevation Limit"). Elsewhere only PINS limits apply.
- **Pointing model:** OnStepX `:GX00#..:GX08#` returns the model terms (`:GX04#` = doCor, the cone term). They can be read on native OnStepX and on INDI over TCP. Elsewhere the state is unknown, so the UI warns and asks for an acknowledgment.

## Architecture

### Backend (TNS C# plugin), new folder `Touch-N-Stars/ConeError/`

The code has four layers:

1. **`Domain/`: pure, static, no NINA types, no natives.** All unit-tested.
   - `ConeMath`: wrapped ΔRA (hours, ±12 h); per-pair `c = ½·(ΔRA_W − ΔRA_E)·cos δ`; mode B `c = ½·(HA_W − HA_E)·cos δ`; `ShimMm = L·tan|c|`; `ShimEnd(c)`.
   - `ConeStatistics`: mean, sample SD, standard error, median/MAD with 3σ-equivalent MAD outlier rejection (only when N ≥ 4), and a quality rating:
     - "good": SE ≤ 0.5′ and SD ≤ 1′
     - "fair": SE ≤ 1.5′
     - "poor": anything else
     - "not significant": |c| < 2·SE, which gives the advice "no shimming needed / re-measure".
   - `SunEphemeris`: Meeus, *Astronomical Algorithms* 2nd ed., ch. 25 low-accuracy apparent Sun (about 0.01°), plus GMST from Meeus ch. 12.
     - Pure C#, so a missing native library can never silently put the Sun at (0,0).
     - Tested against Meeus example 25.a.
   - `Spherical`: angular separation (haversine), HA↔RA, alt/az from HA/Dec/latitude.
   - `GemGeometry`: mechanical axis model.
     - On pierEast, pointing (h,δ) has axes (A=h, B=δ). On pierWest it has (A=h+12h, B=180°−δ).
     - Converts axis angles back to a pointing.
     - Includes the counterweight-up angle for a side.
   - `FlipPathSampler`: sky positions along the possible paths between two axis states, sampled every 0.5° of axis travel.
     - Default paths: proportional-linear and equal-rate-simultaneous, each for **both** RA rotation directions.
     - Optional "strict" setting that adds RA-first and Dec-first L-paths (see Open questions).
   - `LimitSet` / `LimitValidator`: merges mount-reported and PINS limits, taking the stricter of each; unknown mount values fall back to PINS. It validates an endpoint per side:
     - altitude ≥ max(mount horizon, PINS minimum altitude, custom horizon at that azimuth)
     - altitude ≤ mount overhead limit
     - counterweight-up/past-meridian angle ≤ min(mount meridian limit for that side, `MaxMinutesAfterMeridian`·0.25°)
     - Sun separation ≥ radius at the endpoint and every path sample, evaluated at the Sun's position both now and at now + the move's time budget

     It returns typed reasons (`BelowHorizon`, `PastMeridianLimit{side,angle,limit,source}`, `SunTooClose{sep,radius,where}`, …).
   - `TargetPlanner`: suggests the mode A target.
     - Dec 0° (north) or −0° (south). If below minimum altitude at the meridian, it moves toward the pole up to |δ| = 30°.
     - RA is placed so the run's estimated HA window is centred on the meridian.
     - Each repetition gets a fresh target near the meridian, inside the confirmed Dec/HA envelope.
     - Refuses when the overlap window (both sides' past-meridian limits) is shorter than one pair's time budget.
   - `PierSideCheck`: whether the reported side equals the requested side.

2. **`Session/`: equipment-free orchestration over small ports.**
   - Ports: `IConeMount` (info snapshot with LST, RA/Dec in JNow, side, tracking, slewing, site; slew; stop; set tracking), `IPierSideControl` (`Capability`, `PrepareAsync(side)`, `RestoreAsync`), `IConeImager` (capture + solve, returning solved JNow coordinates, exposure mid-time and the mount position sampled before and after), `IPointingModelProbe`, `IClock`, `ISunEphemeris`.
   - `AutoMeasurementSession`: a state machine modelled on `NINA.GuideEngine/Coach/GuidingCoach.cs`, with one CTS, a phase enum, and `finally` → restore.
     - Phases: Idle → Validating → Slewing(side) → Settling → Exposing → Solving → … → Complete / Failed / Aborted.
     - Order alternates E,W / W,E so each pair after the first needs one flip plus one short slew.
     - Before every slew it re-validates against the confirmed envelope. Any deviation fails the run; the target is never widened silently.
   - `ManualFlipSession`:
     - begin: records tracking and turns it off.
     - capture: stores HA = LST_mount − RA_mount, Dec and side, and reports whether the other side is reachable.
     - planMove(side) → planId.
     - move(planId): re-validates; slews to RA = LST_mount − HA; tracking off again (every driver turns tracking on for a slew); up to 2 corrective re-slews until mount-reported |ΔHA| ≤ 1′ and |ΔDec| ≤ 1′.
     - recenterDone: computes the daytime estimate.
     - end: restores tracking.
   - `SafetyWatchdog`: a 1 Hz mount poll while any session runs.
     - Pointing within the Sun radius → `StopSlew` and abort.
     - A position jump while not slewing (sync or external move) → invalidate the pair and abort.
   - `RestoreJournal`: `TnsCache/cone-error/restore.json`, written *before* the preferred pier side or tracking is changed and cleared after a successful restore. A leftover journal shows as `restorePending` in status, with a "Restore" action.
   - `ResultStore`: `TnsCache/cone-error/results.json`, the last 50 results keyed by profile id + mount name, written under a lock like `FavoritesController.cs`. `settings.json` holds the Sun radius (default 30°, minimum 15°), the minimum altitude for mode A (20°) and mode B (0°), dovetail L, repetitions, strict path, and the per-mount "pier labels verified" flag.

3. **`Adapters/`: NINA bindings, not unit-tested.**
   - `NinaConeMount` wraps `ITelescopeMediator`: `GetInfo`, `GetCurrentPosition`, `SlewToCoordinatesAsync`, `StopSlew`, `SetTrackingEnabled`.
   - `NinaConeImager` wraps `IImagingMediator.CaptureImage` → `ToImageData` → `IPlateSolverFactory.GetImageSolver(GetPlateSolver, GetBlindSolver).Solve`.
     - Parameters come from `PlateSolveSettings` and the profile focal length / pixel size, with `DisableNotifications`.
     - Uses `Coordinates.Transform(Epoch.JNOW, midTime)`.
     - **Never** uses `CenteringSolver` or `Sync`.
   - Pier-side controls:
     - `OnStepXPierSideControl`: `GetDevice() is OnStepXTelescope`; limits from `GetMountSettings()`; side via `SendCommandBool(":SX96,E#"/":SX96,W#")`, restored to the earlier `:GX96#` value.
     - `IndiOnStepPierSideControl`: the INDI device has the "Preferred Pier Side" switch (elements "1"=West, "2"=East, "3"=Best), set through `INDIClient.Instance.SetProperty`. Limits from "Minutes Past Meridian" and "Slew elevation Limit"; the semantics will be checked in `lx200_OnStep.cpp` and pinned by tests on the converter.
     - `UnsupportedPierSideControl`: reason text; no move.
   - `OnStepModelProbe`: `:GX00#..:GX08#` through `SendCommandString` (native OnStepX, or INDI over TCP). Any non-zero term means "active", reporting `:GX04#` doCor as the cone term. Otherwise "unknown" → warning plus acknowledgment.
   - Inject `IPlateSolverFactory` into the `TouchNStars.cs` `[ImportingConstructor]` and `Mediators`.

4. **`Server/Controllers/ConeErrorController.cs`**, registered in `Server/TouchNStarsServer.cs`.
   - camelCase Newtonsoft envelope `{success,response}` / `{success:false,error,code}`, as in `InternalGuiderJson`.
   - A testable constructor taking a `ConeErrorService` (the `InternalGuiderService` pattern).
   - Refuses with 409 while a sequence runs (`ImagingActivity.IsSequenceRunning`), while guiding, while the camera is busy, or while another session is active. Abort and teardown in `TouchNStars.Teardown()` / `TouchNStarsServer.Stop()`.

**API under `/api/cone-error/`:**

| Endpoint | Purpose |
|---|---|
| `GET preconditions?clientTimeUtc=` | Mount, camera and solver state, pier-side capability with reason, pointing model state, site, Pi/phone time skew, limit sources, restorePending. |
| `GET/PUT settings` | Read and change the cone-error settings. |
| `POST auto/plan` | Body `{dovetailMm, repetitions, dec?, exposureS?}`. Returns `{planId, target, steps[] (side, RA/Dec, HA, alt, sunSep, CW-up angle), envelope, estDuration, checks[]}`. A plan expires after 60 s. |
| `POST auto/start` | Body `{planId}`. |
| `GET status` | Mode, phase, step i/n, message, pairs so far, error with code, and the result when done. |
| `POST abort` | Synchronous `StopSlew` + camera `AbortExposure` + cancel, then restore. Answers immediately. |
| `POST manual/begin`, `manual/capture`, `manual/plan-move {side}`, `manual/move {planId}`, `manual/recenter-done`, `manual/end` | Mode B steps, as described under `ManualFlipSession`. |
| `GET results`, `POST restore` | Result history; apply a pending restore. |

### Frontend (TNS app), new plugin `src/plugins/cone-error/`

- **Plugin files:**
  - `plugin.json`: `isPins: true`, `defaultEnabled: false`, author Nico.
  - `index.js`: aapa-style registration, i18n title.
  - `views/ConeErrorView.vue`.
  - `components/`: `ConePreconditions`, `ConeAutoSetup`, `ConePlanConfirm`, `ConeProgress`, `ConeResult`, `ShimDiagram` (inline SVG with `var(--color-*)` tokens, like the TenMicron SVGs), `ConeHistory` (before → after, after `NativeCoachHistory.vue`), and `ManualFlip*`.
  - `store/coneErrorStore.js`, `src/services/api/coneError.js` (`rawHttp` against `API_URL`, as in `api/nativeGuider.js`).
- **Wiring:** spread into `apiService.js`, then regenerate `pluginRegistry.js` and the API surface snapshot.
- **Behaviour:**
  - The view is derived from backend state, as in `NativeCoachTab.vue`.
  - 2 s `useBackgroundAwarePolling`.
  - Strings in `en.json` only, under `plugins.coneError.*`.
- **Dark use:**
  - Theme tokens only, no white surfaces, no flashing or pulsing animations, `min-h-touch`.
  - The Abort button is a sticky bottom `tns-btn-danger` with `min-h-[72px]` and a large `StopCircleIcon`. It has no confirmation, like `ButtonSlewStop.vue`.
  - Confirmations via `toastStore.showConfirmation`.

## Wizard flows

**Mode A, automated:**
1. **Preconditions:** green/red rows for mount connected, unparked and tracking; camera; ASTAP path; pier-side capability (the reason if unsupported, which blocks); pointing model (active = strong warning, unknown = acknowledgment checkbox); no sequence or guiding.
2. **Setup:** dovetail L in mm (`NumberInputPicker`), repetitions (3–10, default 5), the suggested target Dec (editable; altitude and HA window preview), exposure (defaults from plate-solve settings).
3. **Plan:** the list of every slew and flip with side, altitude, counterweight-up angle and Sun separation; the estimated duration; then "Start — mount will move". After the first arrival a one-time per-mount check asks: "Is the tube on the EAST side of the pier now?" (diagram). "No" aborts and explains that the pier labels are inverted.
4. **Progress:** a step list (Running/Done/Failed icons), the current pair and side, the latest c per pair, and the sticky large Abort.
5. **Result:** c in arcminutes ± SD (and SE); N used and rejected; shim t in mm (0.05 mm resolution); "Shim the FRONT/REAR mounting point" with a diagram of the OTA, dovetail, rings and arrow; a quality badge; before → after against the last result for this profile and mount.

**Mode B, manual flip check:**
1. **Preconditions:** mount connected; site set (not 0/0; agrees with the mount site); the Pi time compared with the phone time. A skew over 2 min, or a year before 2025, warns and offers the existing "set Pi time" action. A skew over 10 min blocks.
2. **Capture current position:** shows HA, Dec, side, and whether the other side is reachable (or why not).
3. **Move to east / Move to west:** the button for the current side is disabled. Each tap plans the move, shows the checks (limits from both sources, Sun at the endpoint and along the path), then a confirmation, then the move with a large Abort.
4. Links to `/camera` (loop + jog pad) for viewing and re-centering.
5. Optional "I re-centered the landmark": shows the daytime estimate c = ½·ΔHA·cos δ, labelled "daytime estimate". It is stored with `mode: "manual"` and never mixed with plate-solve results.
6. Exit restores tracking. So do abort, idle timeout (30 min) and plugin teardown.

## Measurement algorithm

- Per sample:
  - Mount position = mean of `GetCurrentPosition()` right before and right after the exposure. The sample is rejected if the two differ by more than 30″ or the side changed.
  - Solved J2000 → JNow at the exposure mid-time.
  - ΔRA = wrap(RA_solved − RA_mount).
- Pair i: c_i = ½·(ΔRA_W − ΔRA_E)·cos δ_i, in arcminutes.
- A failed solve (after the profile's attempts) re-exposes that side once. If it still fails, the pair is dropped and the run continues. Fewer than 2 valid pairs (or fewer than ⌈N/2⌉) fails the run with the reason.
- The result reports mean, SD, SE, median, and rejected outliers.

## Sign convention (stated once)

- **Definition:**
  - ΔRA = RA_solved − RA_mount, in the same epoch and wrapped.
  - **c = ½·(ΔRA_pierWest − ΔRA_pierEast)·cos δ**, with NINA/ASCOM pier labels (pierEast = the normal counterweight-down side for targets west of the meridian, so the tube is on the east side of the pier when pointing at the meridian).
  - Mode B uses c = ½·(HA_W − HA_E)·cos δ with mount-reported HA when the landmark is centred.
- **c > 0:** the objective end tilts toward the dovetail and saddle → **shim the FRONT (objective-end) mounting point** by t = L·tan|c|.
- **c < 0:** **shim the REAR (focuser-end) mounting point**.
- **Derivation:**
  - Frame: X = (HA 0, Dec 0), Y = east, Z = NCP.
  - Dec axis u_E = (sin h, cos h, 0) on pierEast, u_W = −u_E.
  - Actual pointing = ideal pointing + c_out·u, where c_out > 0 is an objective end tilted away from the saddle.
  - At h ≈ 0: ΔRA_E = +c_out/cos δ and ΔRA_W = −c_out/cos δ, so c = −c_out.
  - The same holds in both hemispheres.
  - This is the riskiest part. It was checked by a second, independent derivation (see below).
- **Tests:** a test-only vector forward model (`GemSimulator`) generates mount-reported and true pointings from a *physical* tilt, with no use of the estimator formula. It also adds polar misalignment, non-perpendicularity, noise and outliers. The estimator must recover the injected value and the correct shim end.
- **Guards against inverted driver labels:**
  - the one-time "which side is the tube on" check, stored per mount
  - verifying the reported side after every slew
  - the field-test sanity check (shim, re-measure, the error must shrink)

## Findings from the independent review (folded into the design)

- **Sign convention confirmed independently.** The same derivation, plus the identity ∂p/∂h = −cosδ·u_E, shows the result holds for every h, not just near the meridian.
  - TPOINT's CH and NP terms flip beyond the pole while MA and ME don't, which matches this model.
  - TPOINT's absolute sign and OnStepX's `doCor` sign are unverified. They are only ever used as magnitude cross-checks, **never** to choose front or rear.
- **Mode B parallax.** The optical axis sits a distance d from the RA axis along ±u, so a landmark at distance D biases the result by exactly the cone shape: c_meas = c − d/D.
  - The daytime estimate asks for d (mm, RA axis to tube centre) and D (m), corrects for it, and refuses the numeric estimate when d/D > 0.2′. The visual check UI says a landmark should be several km away.
- **Mode B pointing truth.** Without a solve, mount-reported coordinates can be degrees off. So:
  - After capture the UI shows the mount's alt/az and the user confirms it matches the landmark's real direction.
  - The Sun radius stays ≥ 30° by default with a minimum of 15°.
  - Every move confirmation includes a "finder and guide scope capped" checkbox.
- **Static pointing and the moving Sun.** With tracking off the Sun drifts about 15°/h relative to a fixed HA/Dec. The watchdog warns when the Sun reaches radius + 5° and raises a critical alert inside the radius. It stops any running move but **does not move the mount on its own** ("cover the telescope / end the session").
- **Phone disconnect.** During mode B moves, a server-side deadman calls `StopSlew` when no status poll arrives for 20 s. Mode A runs at night with no deadman, so locking the phone doesn't abort a run; this is documented.
- **Polar misalignment residual from HA drift.** Each pair is placed symmetrically around h = 0, and the order alternates E-W / W-E. ΔDec(W−E) is reported as a free index-error diagnostic. Samples with |ΔRA| or |ΔDec| > 2° are refused as a sync, epoch or site problem.
- **Inverted-label guard.** The one-time per-mount question is "Is the counterweight bar on the WEST side right now?", with a diagram, after the first pierEast arrival.
- **Other changes from the review:**
  - planIds expire after 60 s, and limits, Sun and LST are recomputed at execute time
  - epoch conversion goes behind `IEpochConverter`, so tests never touch SOFA or NOVAS
  - the restore journal only restores settings and never starts motion
  - limits are never relaxed to force a side
  - mount LST is compared with the Pi's LST (from the profile longitude), in addition to Pi time vs phone time
  - TNS framing/centring is locked out during a session
  - non-perpendicularity is reported as a limitation: the measured value is CH + NP·sin δ
  - mode B move confirmations include a collision warning: counterweight-up at low altitude can hit the tripod, so stay at the mount

## Sun path override (added after approval, 2026-10-14)

The flip-path sun check turned out to refuse daytime flips from a low southern landmark for most of the day. So a refusal whose **only** violation is the Sun near the path (`SunTooCloseOnPath`) can be overridden:
- **Scope:**
  - The endpoint check (`SunTooClose`) and every limit can never be overridden.
  - The override applies to one planned move (`POST manual/plan-move {side, overrideSunPath: true}`) after a confirmation in the app that names the closest approach. It is logged.
  - The move re-checks with the same rule right before it starts. The next plan is checked normally again.
- **Watchdog:** during an overridden move the watchdog alerts but does not stop the mount, because stopping mid-swing would leave the tube pointing near the Sun. Normal behaviour returns when the move ends.

## Error handling and abort

- **Abort:** cancels the CTS, calls `StopSlew` synchronously in the request thread, calls camera `AbortExposure`, then restores in `finally`: preferred pier side, then tracking (mode B: the earlier state). Restore failures stay in the journal and show as restorePending.
- **Pier side:**
  - Unsupported capability → the UI explains, and the plan refuses with no move.
  - Reported side ≠ requested side after a slew → "mount refused the pier side", stop, no further moves.
- **Limits:**
  - Any limit violation, a target reachable on only one side, or an expired or mismatched planId → refused with a typed reason. There is never a fallback move.
  - Mount refusals (OnStepX goto errors, NINA slew false or exception) → fail with the driver message.
- **Sun:** the watchdog trip and the sync/jump detector both mean stop + abort.
- **Concurrency:** a session refuses to start while a sequence runs, while guiding, while the camera is exposing, or while another session is active.

## Test plan (xUnit, `Touch-N-Stars.Tests/ConeError/`)

- **`ConeMathTests`:** positive and negative c; Dec 0/15/30°; noise; outliers (MAD rejection); RA wrap at 0h/24h; shim side and thickness; quality bands.
- **`GemSimulatorSignTests`:** an injected physical tilt (front-out and front-in) → correct end, in both hemispheres, with polar misalignment and non-perpendicularity present.
- **`SunEphemerisTests`:** Meeus 25.a, solstice/equinox declinations, GMST example 12.a.
- **`LimitValidatorTests`:** each limit kind; stricter of mount/PINS in both directions; mount unknown → PINS; reachable on one side only → refused.
- **`FlipPathSunTests`:** Sun at the endpoint; on the path only (endpoint safe); far away; several Sun positions and times of day, including morning/evening near due south.
- **`AutoSessionTests`** (fake ports with the forward model):
  - full run recovers c
  - solve failure on E, solve failure on W
  - abort mid-flip → `StopSlew` called, pier side restored, tracking unchanged
  - mount refuses the pier side
  - target outside limits → no slew call
  - `Sync` never called
- **`ManualFlipSessionTests`:** tracking restored after end, after abort, and after a move exception; corrective iterations converge; side-disabled rules; parallax correction (forward model with d/D) and refusal above 0.2′; deadman stops a move after 20 s without polls (virtual clock); the Sun-approach alert on static pointing doesn't move the mount.
- **Forward model:** a test-only, independent body-frame model R_RA·R_Dec·b covering cone, non-perpendicularity, polar misalignment, index error, noise, label inversion and landmark parallax. The expected shim end comes from which mounting point is nearer the saddle in the body frame, never from the estimator formula. xUnit Theories cover both hemispheres, h = ±0.3h and δ from −10° to 40°.
- **`ConeErrorApiTests`** (EmbedIO test host pattern): plan → start → poll → result end-to-end on the simulator; abort over HTTP; 409s.

INDI simulators can't force a pier side (no "Preferred Pier Side" property), so the end-to-end run uses the forward-model fakes behind the real controller. If an offline `indi_simulator_telescope` + CCD with GSC turns out to work here, I'll try preconditions and refusal paths against it and report the result.

## Open questions (my defaults in brackets)

1. Path model for the flip Sun check: [simultaneous paths in both directions by default; L-paths as a "strict" setting]. Making strict the default may block mode B for much of a winter day.
2. A detected active pointing model in mode A: [strong warning + explicit "measure residual anyway"] or a hard block?
3. ASCOM/Alpaca mounts with `CanSetPierSide` can only be reached by reflection on internal NINA types: [unsupported for now].
4. Non-perpendicularity also contributes at δ ≠ 0. Measuring at two declinations to separate it is out of scope [noted as a limitation].
5. Mode B daytime estimate needs the d (RA axis to tube) and D (landmark distance) inputs for the parallax correction: [required, estimate refused when d/D > 0.2′; the visual check alone needs no inputs].
