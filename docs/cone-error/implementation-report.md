# Cone error measurement — implementation report

Date: 2026-10-14. Design: [design.md](design.md).

## Summary

PINS can now measure a telescope's cone error and advise how to shim it out. It runs from a new Touch-N-Stars panel, **Cone Error**, which is a PINS plugin that is off by default. The panel has two modes:

- **Measure (automated).** The mount measures a point near the meridian on both pier sides, for each of N repetitions, and plate-solves each sample offline with ASTAP.
  - It reports the cone error in arcminutes with SD/SE, the shim thickness for the entered mounting-point distance L, the end to shim (with a diagram) and a quality rating.
  - It compares the result with the last measurement for the same profile and mount.
  - It never syncs.
- **Daytime (manual flip check).** With tracking off, the mount moves between the same hour angle and declination on the east and west pier sides.
  - Before every move the limits and the Sun (endpoint and flip path) are checked, and the user must confirm.
  - A refusal for the Sun near the flip path alone can be overridden for one move, after an explicit confirmation.
  - There is a deadman, an idle timeout, and an optional parallax-corrected estimate from the re-centred landmark.

All commits are local. Nothing was pushed, rebased or amended.

| Repository | Branch (base) | Commits |
|---|---|---|
| pins | `feat/cone-error` (`unstable` eeb81e4f0) | 8a16def4a `docs: cone error measurement design`, 28b836692 `docs: cone error implementation report`, plus the override docs update |
| NINA.Plugins/Touch-N-Stars | `feat/cone-error` (`unstable` 2a2a942) | 49a0612 `cone error: domain math` → 5eb145e `cone error: measurement sessions` → 4c32a1c `cone error: API` → 9662b28 `cone error: per-move override of the sun path check` → 64083ad `cone error: fixes from the four-reviewer review` → b520081 `cone error: fixes from the second review pass` |
| Touch-N-Stars (app) | `feat/cone-error` (`develop` 6689531a7) | 6571f0e22 `feat(cone-error): cone error measurement panel (PINS)` → 130ac943b `feat(cone-error): override the sun path check for one daytime move` → eb1dc436c `fix(cone-error): review fixes in the panel` → 2ae52e106 `fix(cone-error): counterweight check answered once, shown when refused` |

No NINA core file was changed. `package-lock.json` in the app stays uncommitted, from your `npm install`. No version or CHANGELOG changes were made.

## Architecture as implemented

**TNS C# plugin, `Touch-N-Stars/ConeError/`:**

- **`Domain/`: pure C#, no NINA types, no natives.**
  - `ConeMath` holds the formulas, the shim calculation and the sign convention.
  - `ConeStatistics` uses outlier rejection after Iglewicz and Hoaglin (modified z-score > 3.5, applied from 4 pairs up) and assigns the quality bands.
  - `SunEphemeris`: Meeus ch. 25 low-accuracy Sun and ch. 12 sidereal time.
  - `Spherical`, `GemGeometry`.
  - `FlipPathSampler` samples the proportional and equal-rate axis paths in both RA directions; the optional strict mode adds the two L-shaped paths.
  - `LimitValidator` takes the stricter of the mount and PINS limits.
  - `TargetPlanner`: Dec 0° moving toward the pole up to 30°, each pair symmetric about the meridian, side order alternating.
  - `SunWindow` finds when a refused move becomes allowed.
- **`Session/`.**
  - Ports: `IConeMount`, which has no sync; `IPierSideControl`; `IConeImager`; `IMountDriverInspector`; `IObservatory`; `IClock`; `ISunProvider`.
  - `AutoMeasurementSession` and `ManualFlipSession`.
  - `SafetyWatchdog`: a 1 Hz Sun check, plus a sync/jump detector for the automated mode.
  - `ConeErrorStore`, which persists to `~/.local/share/NINA/TnsCache/cone-error/`:
    - `settings.json`
    - `results.json`, the last 50 results
    - `restore.json`, the restore journal
  - `ConeErrorService`, which handles preconditions, 60 s plans and the single running session.
- **`Adapters/`.**
  - `NinaConeMount`, `NinaConeImager` and `NinaObservatory`:
    - capture uses `IImagingMediator.CaptureAndPrepareImage`
    - the solve uses `IPlateSolverFactory.GetImageSolver(...).Solve`; `CenteringSolver` is never used
    - the solved J2000 position is converted to JNow at the exposure midpoint
  - `NinaMountDriverInspector`, `OnStepXPierSideControl` (`:SX96,E/W/B#`, original from `:GX96#`), `IndiOnStepPierSideControl` (INDI switch "Preferred Pier Side", elements 1/2/3 = West/East/Best, waits for state Ok) and `OnStepModelProbe` (`:GX00#..:GX08#`; `:GX04#` = doCor).
  - Mount limits come from OnStepX `GetMountSettings()`, or from the INDI "Minutes Past Meridian" (÷4) and "Slew elevation Limit" properties.
- **`Server/Controllers/ConeErrorController.cs`** serves `/api/cone-error/*` and is registered in `TouchNStarsServer.cs`. `TouchNStars.cs` now imports `IPlateSolverFactory`, and its `Teardown()` stops and restores a running session.

**API:**

| Area | Endpoints |
|---|---|
| General | `GET preconditions?clientTimeUtc=`, `GET/PUT settings`, `GET status` (polled every 2 s), `POST abort`, `GET results`, `POST restore` |
| Automated | `POST auto/plan`, `POST auto/start`, `POST answer` |
| Daytime | `POST manual/begin`, `manual/capture`, `manual/plan-move`, `manual/move`, `manual/stop`, `manual/recenter-done`, `manual/end` |

**TNS app, `src/plugins/cone-error/`:**
- Tabs: Measure, Daytime, History, Settings.
- A large sticky stop bar (72 px, one tap) shows whenever the mount can move.
- Status polling uses `useBackgroundAwarePolling` at 2 s.
- Only theme tokens are used, with no bright surfaces and no flashing.
- There is a shim diagram and a pier-side sketch.
- Strings live in `en.json` only.

### Deviations from the approved design, with reasons

1. **The `IPlateSolverFactory` DI change landed in the sessions commit, not the API commit.** The NINA imager adapter in that commit needs it, so it would not have compiled otherwise.
2. **The app's API module is plugin-local (`src/plugins/cone-error/api.js`) rather than spread into `apiService.js`.** The facade explicitly allows new code to import modules directly. This keeps the API surface snapshot and upstream files untouched.
3. **No `IEpochConverter` port.** The J2000→JNow conversion lives inside the NINA imager adapter, which returns JNow. Tests therefore never touch SOFA/NOVAS, which was the goal of that port.
4. **The outlier rule changed from 3σ to Iglewicz–Hoaglin (3.5 robust σ).** At 3σ, a seeded test rejected 2 of 8 normal samples; 3.5 is the published threshold.
5. **Stopping during a daytime move keeps the mode active.** The stop bar calls `manual/stop`, which stops the mount immediately. "End the daytime check" or `POST abort` then ends the mode and restores tracking. This avoids an accidental exit losing the captured position.
6. **The daytime check does not run the sync/jump detector.** The user re-centres the landmark with the jog pad, which is an intended move. The Sun watchdog does run.
7. **"Next allowed" hint.** The design required checking the flip path. Doing so showed that flips from a low southern landmark are refused for most of the day (see Limitations). A refused move therefore reports when it first becomes allowed, scanning in 5-minute steps over 12 h.
8. **Sun path override, added at your request after the first handoff.** A refusal whose only violation is the Sun near the flip path can be overridden for one planned move (`manual/plan-move {overrideSunPath: true}`), after a confirmation naming the closest approach.
   - The endpoint check and the limits are never overridable.
   - During an overridden move the watchdog alerts instead of stopping the mount mid-swing.
   - The next plan is checked normally again.
   - Details are in the design under "Sun path override".
9. **The design's lockout of other gotos is not implemented; detection replaces it.**
   - TNS framing and centring go through the ninaAPI plugin, which this feature does not change.
   - The automated run stops when a goto it did not start begins.
   - The daytime check drops the captured position after a move or sync from elsewhere.
10. **The open questions were implemented with their stated defaults**:
   - non-strict paths by default
   - an active or unknown pointing model needs an explicit acknowledgment and is not a hard block
   - ASCOM/Alpaca is unsupported
   - non-perpendicularity is reported as a limitation
   - the parallax inputs are required for the estimate, which is refused above 0.2′

## Sign convention (single statement)

**ΔRA = RA_solved − RA_mount (same epoch, wrapped to ±12 h); c = ½ · (ΔRA_pierWest − ΔRA_pierEast) · cos δ, using NINA/ASCOM pier labels; in the daytime check c = ½ · (HA_W − HA_E) · cos δ + parallax, with the mount-reported hour angles at which the landmark is centred. c > 0 means the objective end tilts toward the dovetail and saddle: shim the FRONT (objective-end) mounting point by t = L · tan|c|. c < 0: shim the REAR (focuser-end) mounting point.**

How the convention is backed up:
- It was derived twice independently; the design has the derivation.
- In code it lives only in `ConeMath`.
- It is tested against a separate physical forward model (`ConeForwardModel`). That model derives the pier label from the counterweight position and the expected end from the mounting-point heights above the saddle, never from the estimator. The tests cover both hemispheres, positive and negative tilts, δ from −20° to 30°, polar misalignment, index errors, non-perpendicularity, noise, outliers, inverted driver labels and landmark parallax.

## Test results, baseline vs final

**Plugin build** (`dotnet build Touch-N-Stars.sln --no-incremental`). Builds ran with a scratch HOME, so the post-build copy did not touch the deployed plugin in `~/.local/share/NINA/Plugins`.

| | Baseline (`unstable` 2a2a942) | Final (`feat/cone-error` b520081) |
|---|---|---|
| Errors | 0 | 0 |
| Warnings (total) | 362 | 362 |
| Unique warning lines | 346 | 346 (identical set, none from the new files) |

**Plugin tests** (`dotnet test Touch-N-Stars.Tests`):

| | Baseline | Final |
|---|---|---|
| Passed / total | 306 / 306 | 548 / 548 |
| New cone error tests | — | 242 |

The new tests by area:

| Area | Tests |
|---|---|
| Sign convention, forward model | 22 |
| Math and statistics | 36 |
| Sun ephemeris (Meeus examples 12.a, 12.b, 25.a) | 9 |
| Limits | 17 |
| Flip path (incl. southern hemisphere, via home, waypoints, dipped horizon) and sun window | 26 |
| Planner | 14 |
| Automated session | 28 |
| Daytime session (incl. sun override) | 46 |
| Adapters and store | 22 |
| API end-to-end | 22 |

The failure paths the spec named are all covered:
- a solve failure on the east side and on the west side
- an abort mid-flip, in the session and over HTTP
- a mount that ignores the requested pier side
- a target past the meridian limit or below the minimum altitude
- the Sun near the target, the path or the static pointing
- a sync or move from elsewhere
- a refused pier-label check
- a failed restore, which leaves the journal behind
- for the daytime check: refusal at each limit kind, stricter of mount/PINS in both directions, a target reachable on one side only, and tracking restored after end, abort, a failed move and the idle timeout
- for the override: it lifts the path refusal only, never the endpoint or a limit; it holds for one move; the watchdog alerts but does not stop an overridden swing, and still stops a normal move that comes near the Sun

**End-to-end.** NINA's simulators don't work here:
- The INDI telescope simulator has no "Preferred Pier Side" property, so the feature would correctly refuse it as unsupported.
- This machine has no GSC catalog for the CCD simulator and no ASTAP.

So the end-to-end test is `ConeErrorApiTests`. It runs the real controller on EmbedIO over the forward-model mount, solver and clock, going preconditions → plan → start → pier-label question answered over HTTP → poll → result, plus abort over HTTP and the daytime check over HTTP.

**TNS app.**
- `npm run lint` and `npm run format:check`: clean.
- Every new `.vue` file compiles with `@vue/compiler-sfc`.
- `npm run test:run`: 877/877 after all four app commits. No separate pre-change run was taken; the panel adds no app tests (convention) and changes no existing module.
- `npm run i18n:check` reports only the expected `missing (156)` keys in the 13 other locales; you keep TNS strings in `en.json` only.
- No `vite`/`npm run build` was run.

**pins.** Docs only, so no build was needed.

## Four-reviewer review and fixes

Four independent reviewers each reviewed all three repositories. All four confirmed the sign convention, the parallax term, the units, the Meeus ephemeris, the OnStepX meridian-limit semantics and the scope of the override.

Their findings were checked against the code, the OnStepX 10.20a source and lx200_OnStep, and fixed in 64083ad (plugin) and eb1dc436c (app). Each fix has a test that reproduces the bug.

**High:**
- **Tracking restore:** `ITelescopeMediator.SetTrackingEnabled` returns the tracking state, not whether it worked, so restoring "tracking off" looked like a failure. That left a journal behind that blocked both modes.
- **Stuck daytime move:** a failed daytime move left the phase at "Moving", which disabled End, Capture and both Move buttons.
- **Southern flip path:** the sun check sampled the flip over the north pole in the southern hemisphere.
- **OnStepX's real flip route:** the route via its home position was not sampled.

**Medium:**
- **INDI OnStep limits:** they were never used, because their state stays Idle.
- **Leaving the panel:** opening the panel's own live view or jog pad links stopped the status polls, so the deadman stopped the mount mid-flip (even during an overridden move).
- **Plan countdown:** it compared the Pi's clock with the phone's.
- **Abort during the counterweight question:** it was reported as "labels inverted".
- **Daytime drift:** corrections could not converge with NINA's settle time.
- **INDI pier-side confirmation:**
  - It returned before the driver had answered.
  - The stock driver rejects OnStepX's `1` and only reads `:GX96#` back on its next poll.
  - A sent but unconfirmed change was never restored.
- **Lockout of other gotos:** missing; detection replaces it.
- **Daytime estimate:** it ran without the counterweight check.
- **Runs from home:** they failed at the first slew, because OnStepX ignores the preferred side at home.

**Low:**
- Capture was not guarded during a move.
- A failed daytime start left a stuck session.
- The abort fallback did nothing after a finished session.
- Races between End and a move, and a session counted as finished before its restore had run.
- No cancellation check between preparing the side and the goto.
- The History tab trapped the user while a session ran.
- The exposure default ignored the saved setting.
- A settings save reset unsent fields.
- The clock check ran only in the app.
- A meridian limit of 0 made every plan impossible.
- The confirmed hour-angle envelope could be widened.
- The INDI switch could be ambiguous.
- Daytime step 6 of this report was impossible.

**Not changed:**
- **The ASTAP hint in JNow:** not a bug, because `PlateSolveParameter.Coordinates` converts to J2000.
- **Few usable daytime landmarks under the profile's meridian limit:** this is by design.

## Second review pass

All four reviewers checked their findings against the fixes. Every one was fixed or accepted (the goto lockout replaced by detection; the ambiguous INDI switch over serial falls back to Best).

Two reviewers checked parts of the reasoning against the source:
- **INDI confirmation:** INDIClient's local `SetProperty` changes neither the timestamp nor the state, and every driver set message copies both.
- **OnStepX flip route:** the via-home route and the home coordinates in both hemispheres match OnStepX.

They found one medium regression and one medium gap, both fixed in b520081 (plugin) and 2ae52e106 (app), each with a test:

**Medium:**
- **Stopped daytime move (all four reviewers):** stopping or failing a daytime move wiped the captured landmark. The reference position was not updated, so the stop looked like a move from elsewhere and the real error, e.g. the Sun stop, was overwritten.
- **Counterweight check far from the meridian:** it was accepted at any hour angle. On pierEast beyond 6 h from the meridian the counterweight really is east, so an honest answer would have been stored wrongly per mount. It is now accepted only within 4 h of the meridian, and only once per session.

**Low:**
- Abort cancelled before it stopped the mount.
- A settings update could add verified mounts, and a wrong type gave 500.
- The daytime check started when the mount kept tracking.
- The Sun cut-off ignored pointings below the horizon. It now follows the session's minimum altitude.
- Teardown waited 5 s while a move can take 15 s.
- A disconnected mount was reported as restored.
- A "No" to the counterweight check could be undone.
- An abort during the daytime start was lost.
- OnStepX's 60°/120° waypoint was not sampled.

## Limitations and open issues

- **The NINA adapters have not been run against real hardware or drivers.** This covers the OnStepX `:SX96` path, the INDI "Preferred Pier Side" path, the model probe, the capture and solve, and the J2000→JNow conversion. Their contracts were checked against the OnStepX 10.20a and INDI `lx200_OnStep` sources, but the first night is the real check.
- **Daytime flips are rarely allowed with a 30° Sun radius.**
  - A flip from a low southern landmark swings the tube over the pole through the eastern or western sky. At 50° N, in June, March and December, it comes within 30° of the Sun for most of the day; it is allowed only when the Sun is within a few degrees of the horizon or below it (around sunrise and sunset).
  - A Sun more than 2° below the horizon is ignored.
  - The UI shows when a move becomes allowed. You can override the path check per move; the endpoint check stays.
  - OnStepX's via-home route is sampled as well. Firmware-specific single-direction flips could relax the check, but the direction of a flip is not known.
- **Supported mounts.** Pier side forcing works only on the native OnStepX driver and the INDI OnStep and Proxisky drivers. Everything else, including ASCOM/Alpaca, 10micron and other INDI drivers, is refused with a reason. INDI's `TELESCOPE_PIER_SIDE` is read-only in pins and the ASCOM/INDI telescope classes are internal.
- **Pointing model.** It can be read on native OnStepX and on INDI OnStep over TCP; over a serial INDI connection it is "unknown" and needs an acknowledgment. A firmware without the alignment commands may answer `0`, which looks like "no model".
- **The measured value is CH + NP · sin δ** (non-perpendicularity). Near δ = 0° this is negligible. Separating the two terms would need two declinations.
- **A Dec index error combined with polar misalignment leaves a small second-order residual.** It is about 0.01′ for realistic values (5′ × 10′) and 0.15′ for 20′ × 25′. ΔDec(W−E) per pair is shown as a diagnostic.
- **Pier label inversion cannot be detected by software.** It is caught by the one-time counterweight question per mount, after the first east arrival, and finally by the before/after check.
- **Mode A has no deadman.** Locking the phone does not abort a run at night. The mode B deadman (20 s without status polls during a move) also fires when the app is backgrounded mid-move, which is intended.
- **PHD2 guiding is not detected;** the UI warns about it. The internal guider is detected.
- **Mount clock/site.** The Sun check uses the Pi's clock and the profile longitude; mount hour angles use the mount's sidereal time. Their difference is shown, warns at 1 minute and blocks at 8. The Pi-vs-phone clock difference warns at 2 minutes and blocks at 10.
- **The automated run's stop bar is only on the panel.** The status poll and the backend continue on other pages, but stopping from there needs the mount page's stop. The daytime check disables its own links during a move.
- **INDI at home:** pins' INDI telescope reports "at home" only after a NINA home command. A run started from the power-on home position can still be refused at the first slew (safely, "mount refused the pier side"). Moving the mount off home first avoids it.
- **INDI refusal looks confirmed:** when OnStepX refuses `:SX96` (replies `0`), the stock driver shows the requested side as Ok. The check of the reported side after the slew still stops the run.
- **INDI limits right after connect:** before the driver's first poll the INDI limit properties hold its built-in defaults (east 20, west −20 minutes). These are stricter than usual, so they can refuse plans with a confusing reason, but never allow too much.
- **False external slews on INDI:** pins' INDI slewing flag partly comes from a coordinate-motion heuristic. A late report could, rarely, be taken for a goto from elsewhere and stop a run. Not observed.
- **Gotos from elsewhere during the daytime check:** they are detected (more than 2° of movement drops the capture) but not prevented, and they get no endpoint or path Sun check of ours. The watchdog still alerts inside the radius.
- **Other INDI OnStep forks:** the INDI "Preferred Pier Side" confirmation relies on the stock driver reading `:GX96#` back on its status poll. A driver that never reads it back times out ("not confirmed") and refuses safely.
- **The panel has not been rendered in a browser or app** (no build). Nico builds and should look at it on a phone in the dark, especially the stop bar against the bottom navigation.
- **Translations** for the 13 other app locales are not generated.

## Field test plan: first night on a real mount

**Preparation, daylight or dusk:**
- ASTAP plus a D50 or D05 database installed.
- Focal length and pixel size set in the profile; site and time correct.
- Mount connected through the native OnStepX driver or INDI OnStep / Proxisky.
- Pointing model cleared.
- No sequence, no guiding.

1. **Preconditions.** Open Cone Error → Measure. All rows should be green. Starting from the home position is fine: the first slew then goes to the West side, where OnStepX puts it at home.
   - "Pier side control" names the OnStepX or INDI OnStep control.
   - "Mount limits" shows the mount as the source. Compare its meridian limits with the mount's own settings page.
   - "Pointing model" is None.
2. **Plan only.** Enter L, plan 3 repetitions, and check the table: altitudes, past-meridian angles below the limits, and large Sun separations. Let the plan expire once and confirm that Start is refused.
3. **Abort test first.** Start. At the first east arrival, look at the mount: the counterweight must be on the WEST side.
   - Answer truthfully. "No" stops the run; investigate the driver's pier side reporting before going on.
   - During the first flip, tap STOP. The mount must stop within about a second.
   - The preferred pier side must be back to its original value (`:GX96#` or the INDI property), and no "restore" banner may remain.
4. **Full run.** Run 5 repetitions and note the cone error, SD, SE and per-pair ΔRA and ΔDec. Expect SD below about 1′.
5. **Sign calibration, before any real shim.** Put a test shim of known thickness t (for example a 0.5 mm card) under the FRONT ring and measure again.
   - The cone error must decrease by about atan(t/L): for 0.5 mm at 200 mm, about 8.6′.
   - If it increases instead, the sign is wrong for this mount. Stop and report.
   - Remove the test shim.
6. **Sanity check.** Shim the advised end by the advised amount and measure again.
   - The before/after line must say the error got smaller, and |c| should approach the noise level.
   - If it got larger, the shim is at the wrong end: remove it and report.
7. **Repeatability.** Repeat once more later in the night, or on the next night.

## Daytime test (manual flip check)

1. **Verify the Sun exclusion first, from a safe position.** Point at the landmark with the Sun far away, low in the sky or behind you. Never test by pointing toward the Sun.
   - In Settings, temporarily raise the Sun radius to, for example, 90°.
   - Start the daytime check, capture, and plan a move. It must be refused with "Sun at the end" / "Sun along the way", and show when it would be allowed.
   - Set the radius back to 30°.
   - In the middle of the day, the plan for the flip must be refused for the path even though the landmark itself is far from the Sun.
2. **Clocks.** Check that the panel shows no Pi-clock warning; use "Set the Pi's time" if it does.
3. **Choose a landmark** at least 5 km away, near due south and low. Choose a time when the "allowed" hint permits flips, around sunrise or sunset. Cap the finder and the guide scope, and use the camera's live view, not the eyepiece.
4. **Move.** Capture, confirm that the shown alt/az matches the landmark, plan Move to the other side, read the checks and warnings, confirm, and watch the mount. Tap STOP once during a flip to test it, then move again.
5. **Read the shift.** In live view (Live view link → camera loop), the landmark's shift between the sides is about twice the cone error.
   - Optionally, re-centre the landmark with the jog pad, enter the RA-axis-to-tube distance and the landmark distance, and tap "Landmark re-centred".
   - Compare the estimate with the night result (same sign; size within about 1′).
6. **Override, only from a safe position.** End the daytime check, raise the Sun radius in Settings (it is fixed while a check runs), then start the check again. Do this when the Sun is far from the whole sky region of the flip, and raise the radius until the plan is refused for the path only.
   - Check that "Override the sun check for this move" appears and that the confirmation names the closest approach.
   - Check that the override does not appear when the endpoint is refused.
   - After the overridden move, check that the next plan is refused again without the override.
   - Set the radius back to 30°.
7. **End.** End the daytime check. Tracking must be back to its earlier state.
