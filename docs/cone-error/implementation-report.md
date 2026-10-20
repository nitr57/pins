# Cone error measurement — implementation report

Date: 2026-10-14, rebuilt 2026-10-20. Design: [design.md](design.md).

## Summary

PINS can measure a telescope's cone error and advise how to shim it out. The measurement runs from a Touch-N-Stars panel, **Cone Error**. It is a PINS plugin and is off by default.

At night the mount measures, for each of N pairs:
- the same coordinates just east of the meridian on the west pier side
- then, after NINA's own meridian flip, on the east side

Each side is plate-solved offline. The panel reports:
- the cone error in arcminutes with SD/SE
- the shim thickness for the entered mounting-point distance L, and the end to shim, with a diagram
- a quality rating
- a before/after comparison with the last measurement on the same profile and mount

It never syncs and never forces a pier side, so it works with every mount NINA can flip: ASCOM, Alpaca, INDI and native OnStepX.

**History:**
- **The first version (2026-10-14)** forced the pier side itself:
  - OnStep `:SX96`/`:GX96#` and the INDI "Preferred Pier Side" switch, plus the OnStep model probe
  - its own merged mount limits, a Sun watchdog and a restore journal
  - a daytime landmark mode with flip-path Sun checks

  That worked only on OnStep mounts and was too complicated.
- **The rebuild (2026-10-20)** measures through `ITelescopeMediator.MeridianFlip` instead and drops all of that. The plugin's cone-error code, including tests, went from 9,542 to 4,741 lines, and the panel from 2,038 to 1,373.

All commits are local. Nothing was pushed, rebased or amended.

| Repository | Branch (base) | Commits |
|---|---|---|
| pins | `feat/cone-error` (`unstable`) | 8a16def4a design → 28b836692 report → bd3237605, 21652d306, 9f0b6324c, 5f2c60fc3 (override and review passes) → this docs rebuild |
| NINA.Plugins/Touch-N-Stars | `feat/cone-error` (`unstable`) | 49a0612 domain math → 5eb145e sessions → 4c32a1c API → 9662b28 sun path override → 64083ad, b520081, 00ab046 (three review passes) → **e590e36 `cone error: measure through NINA's meridian flip`** |
| Touch-N-Stars (app) | `feat/cone-error` (`develop`) | 6571f0e22 panel → 130ac943b override → eb1dc436c, 2ae52e106, 324107460 (review fixes) → 13cc5d04e stop button inline → **2ff5bbdbb `feat(cone-error): measure through NINA's meridian flip`** |

Notes:
- You merged `unstable` into the plugin branch (b988c29) and pins (1fbd153b6), and `develop` into the app branch (10ad06d8c).
- No NINA core file was changed.
- `package-lock.json` in the app stays uncommitted (from your `npm install`).
- There are no version or CHANGELOG changes.

## Architecture as implemented

The [design](design.md) describes it, and the code follows it. In short:

- **Plugin, `Touch-N-Stars/ConeError/`:**

  | Folder | Contents |
  |---|---|
  | `Domain/` | `ConeMath`, `ConeStatistics`, `SunEphemeris`, `Spherical`, `TargetPlanner` |
  | `Session/` | ports, `AutoMeasurementSession`, `ConeErrorStore`, `ConeErrorService` |
  | `Adapters/` | `NinaConeEnvironment.cs`: mount, imager, observatory |

  The controller is `Server/Controllers/ConeErrorController.cs`.
- **Removed in the rebuild:**
  - `GemGeometry`, `LimitValidator` and `FlipPathSampler`, `SunWindow`
  - `ManualFlipSession`, `SafetyWatchdog`
  - `MountDrivers.cs`: pier-side controls, model probe, limits inspector
  - the restore journal
  - the endpoints `manual/*` and `restore`
- **App, `src/plugins/cone-error/`:**
  - Measure and History tabs.
  - `ConeRun.vue` follows `NativeCoachRun.vue`, with "Stop the mount" inline.
  - The view polls with `useBackgroundAwarePolling`.
  - Removed: `ManualFlip.vue`, `ConeSettings.vue`, the store-level poller (it only served the daytime deadman) and 70 locale keys.

**What a flip does per driver**, checked in the pins sources (`AscomTelescope`, `AlpacaDirectTelescope` → `AscomTelescope`, `IndiTelescope`, `OnStepXTelescope`, `TelescopeVM`):
- Every driver turns tracking on and returns early when the mount is already on the expected side (`UseSideOfPier`).
- ASCOM/Alpaca set `SideOfPier` when `CanSetPierSide`. Otherwise they slew to the target and let the mount choose.
- INDI and OnStepX run their own flip, with retries.
- `TelescopeVM` waits for the dome and the next mount update, but does not settle. The session settles itself (profile settle time + 2 s).

## Sign convention (single statement)

**ΔRA = RA_solved − RA_mount (same epoch, wrapped to ±12 h); c = ½ · (ΔRA_pierWest − ΔRA_pierEast) · cos δ, using NINA/ASCOM pier labels. c > 0 means the objective end tilts toward the dovetail and saddle: shim the FRONT (objective-end) mounting point by t = L · tan|c|. c < 0: shim the REAR (focuser-end) mounting point.**

It was derived twice independently, confirmed by all four reviewers, lives only in `ConeMath`, and is tested against the independent physical forward model `ConeForwardModel` (unchanged by the rebuild).

## Test results, baseline vs final

**Plugin build.** It was built with a scratch HOME, so the post-build copy did not touch the deployed plugin in `~/.local/share/NINA/Plugins`. The full non-incremental build of `Touch-N-Stars.csproj` gives 0 errors and 33 warnings, none of them in the cone-error files (neither in the plugin nor in the tests).

**Plugin tests** (`dotnet test Touch-N-Stars.Tests`):

| | Before the rebuild (00ab046, on the old `unstable` base) | Final (e590e36) |
|---|---|---|
| Passed / total | 551 / 551 | 471 / 490 |
| Cone error tests | 245 | 138, all passing (three runs in a row) |
| Failing | — | 19 `LuckyCaptureFocusApiTests` |

About the 19 failures:
- They are not caused by this branch. They came in with your `unstable` merge (f1da3ab, LuckyCapture bridge).
- They fail the same way on a clean `unstable` worktree: 19/19.
- The cause is the test host binding the shared-framework `System.Windows` facade instead of pins' compat `System.Windows.dll`: `FileLoadException … manifest definition does not match`. The test project turns on self-contained only when `PinsRoot` is set, and that is not the case on a plain Linux build.
- I left it alone. It is outside this feature.

**Cone error tests by area:**

| Area | Tests |
|---|---|
| Sign convention (forward model) | 22 |
| Math, statistics, Sun ephemeris | 45 |
| Planner | 15 |
| Session | 25 |
| Adapters and store | 13 |
| API end-to-end over EmbedIO | 18 |

The session tests run against a fake mount that behaves like NINA's drivers:
- A goto lands on the counterweight-down side, or, as an option, stays on its side.
- A flip goes to the expected side, or, as an option, cannot change side.

The failure paths covered:
- a mount that stays east at the goto (flipped west and completed)
- a mount that cannot flip, and one that never reaches the west side (stops with no further moves)
- a solve failure on either side; a solve that works on the second try; all solves failing; movement during an exposure
- abort while waiting for the meridian, mid-flip (stop before cancel) and during the question; a flip that fails because of the abort
- tracking off before → off after, also after an abort
- a target below the minimum altitude (no move)
- the counterweight question: yes, no, a wrong id
- over HTTP: plan reuse, expiry, `Busy`, a missing pointing-model confirmation, a missing L, a running sequence, a disconnected mount, a wrong Pi clock, daylight, settings patching

**End-to-end with hardware or simulators** was not run here. There is no ASTAP or star catalogue on this machine. `ConeErrorApiTests` runs the real controller over the fakes instead.

**TNS app:**
- `eslint` on the plugin and `prettier --check` on the plugin and `en.json`: clean.
- Every `.vue` file compiles with `@vue/compiler-sfc`.
- `npm run test:run`: 918/918.
- `npm run i18n:check` reports only the expected `missing (86)` in the 13 other locales (you keep TNS strings in `en.json` only), and no unused keys.
- No `vite`/`npm run build` was run.

**pins:** docs only.

## Reviews

Four independent reviewers reviewed the first version in three passes. Their findings were fixed in 64083ad, b520081 and 00ab046 (plugin) and eb1dc436c, 2ae52e106 and 324107460 (app); the earlier version of this report has the list.

Most of the code they reviewed was removed in the rebuild: the pier-side forcing, the INDI confirmation, the flip-path Sun sampling, the watchdog, the journal and the daytime mode. What carries over:
- the sign convention and the forward model
- the statistics
- the `SetTracking` semantics (`SetTrackingEnabled` returns the state, and a disconnected mount never "succeeds")
- an abort stops before it cancels
- an abort during the question is an abort, not inverted labels
- the plan countdown counts from receipt, not from the Pi-vs-phone clock
- settings patches change only the fields sent and cannot add verified mounts
- abort stops the mount even after a run has finished
- the clock checks run on the server

The rebuild itself has not been reviewed by the four reviewers.

## Limitations and open issues

- **Not run against real hardware.** The capture and solve path, J2000→JNow, and each driver's `MeridianFlip` in this use are untested on a mount. The first night is the real check.
- **The mount has to behave like a sequence flip:**
  - At a goto to a target just east of the meridian it must end up on the west side, or `MeridianFlip` must bring it there.
  - At the flip, with the target "minutes after meridian" past the meridian, it must go to the east side.
  - Mounts whose own meridian limit lets them track past that point without flipping need "minutes after meridian" set beyond that limit, as they do for a sequence flip.
  - If the side is wrong, the run stops with a clear `PierSideNotReached`. It does not work around the problem.
- **ASCOM/Alpaca with a "stays on its side" goto:** NINA's ASCOM flip judges the target side 0.2 h ahead (padding in `AscomTelescope.MeridianFlip`). For a target 2–3 min east of the meridian it therefore sees "east" and does not flip west. Such a mount stops with `PierSideNotReached` in the first pair. Mounts that pick the counterweight-down side at a goto are not affected.
- **Pointing model:** it cannot be detected generically, so the user confirms that none is active. An active model hides most of the cone error, so the result would be too small, but it would not point at the wrong end.
- **The measured value is CH + NP · sin δ.** Near δ = 0° this is close to CH.
- **Pier label inversion cannot be detected by software.** The one-time counterweight question per mount catches it, and finally the before/after check.
- **Each pair waits for the meridian.** A pair takes about the slew time plus "minutes after meridian" plus 0.5 min plus the flip. With 5 min after meridian, 3 pairs take roughly 25–30 min.
- **Night only:** blocked while the Sun is above −6° at the profile site.
- **PHD2 guiding is not detected** (warning only); the internal guider is.
- **The stop button is only on the panel.** The run goes on when you open other pages. From there, use the mount page's stop.
- **The panel has not been rendered in a browser or the app** (no build). Please look at it on a phone in the dark.
- **Translations** for the 13 other app locales are not generated.

## Field test plan: first night on a real mount

**Preparation:**
- ASTAP with a D50 or D05 database.
- Focal length and pixel size in the profile; site and time correct.
- In the profile's meridian flip settings, "minutes after meridian" ≥ 1 and ≤ "max minutes after meridian".
- Pointing model cleared, no sequence, no guiding.
- Any mount that NINA flips in a sequence.

1. **Checks.** Open Cone Error → Measure after the end of civil twilight. Every check must be green. The Sun altitude is shown.
2. **Plan only.** Enter L, plan 3 pairs, and read the summary: declination, altitude, minutes, flip time. Let the plan expire once and confirm that Start is refused.
3. **Stop test first.** Tick "no pointing model" and start.
   - At the first west arrival, look at the mount: the counterweight must be on the EAST side, the tube on the west. Answer truthfully. "No" stops the run; check the driver's pier side reporting before going on.
   - During the first meridian flip, tap "Stop the mount". The mount must stop within about a second.
4. **Full run.** Run 5 pairs.
   - Watch that every flip happens after the countdown and lands on the east side.
   - Note the cone error, SD and SE and the per-pair ΔRA/ΔDec. Expect an SD below about 1′.
5. **Sign calibration, before any real shim.** Put a test shim of known thickness t (for example a 0.5 mm card) under the FRONT ring and measure again.
   - The cone error must decrease by about atan(t/L): for 0.5 mm at 200 mm, about 8.6′.
   - If it increases instead, stop and report.
   - Remove the test shim.
6. **Sanity check.** Shim the advised end by the advised amount and measure again. The before/after line must say the error got smaller.
7. **Other drivers.** If you can, repeat a short run (2 pairs) with an Alpaca or ASCOM mount and with INDI, to see each driver's flip in this use.
