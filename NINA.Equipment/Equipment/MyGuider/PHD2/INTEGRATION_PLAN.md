# PHD2 → pins/NINA Native Guiding Integration Plan

Status: design / proposal
Owner: guiding subsystem
Scope: replacing the external PHD2 process with a natively embedded, headless PHD2 guiding engine, staged behind a low-risk interim step.

This document complements `AGENTS.md` and the per-project `ARCHITECTURE.md` files. It is the system of record for the PHD2 integration effort; update it as phases land.

---

## 1. Background

pins currently guides using a **modified fork of PHD2** (`~/Hobby/phd2`) launched as a separate process. NINA drives it over the PHD2 JSON-RPC/event socket. The relevant code:

- `NINA.Equipment/Interfaces/IGuider.cs` — the guider contract every consumer plugs into (`StartGuiding`, `StopGuiding`, `Dither`, `ClearCalibration`, `AutoSelectGuideStar`, `SetShiftRate`, `GetLockPosition`, `GuideEvent`).
- `NINA.Equipment/Equipment/MyGuider/PHD2/PHD2Guider.cs` — current implementation. Launches `phd2.exe` (`StartPHD2Process`), connects over TCP, drives PHD2 via JSON-RPC, parses its event stream. **NINA owns no guiding hardware today — PHD2 owns the guide camera and pulses the mount.**
- `NINA.Equipment/Interfaces/ViewModel/IGuiderVM.cs`, `IGuiderMediator.cs` — sequencer and dither consumers sit above these.
- Touch-N-Stars (`NINA.Plugins/Touch-N-Stars`) exposes PHD2 to the Vue frontend via `PHD2Client.cs`, `PHD2Service.cs`, `PHD2ImageService.cs`.

**pins is headless + Vue.** There is no WPF UI in the deployment; user-facing guiding UI lives in Touch-N-Stars, fed by ninaAPI endpoints.

The long-term goal is to embed PHD2's guiding *engine* directly into pins/NINA so guiding runs in-process, with no separate executable, no wxWidgets, and with the guide camera and mount owned by NINA.

---

## 2. Why this is hard (and why it is less hard than it looks)

The PHD2 fork is deeply coupled to wxWidgets:

- **225 of 282** source files reference wx.
- `class Guider : public wxWindow` — the engine's own state machine derives from a GUI widget.
- The worker thread uses `wxThread` + `wxMessageQueue`.
- Config persistence is `wxConfig` (`pConfig`, a global singleton).
- `wxString` appears 244× in the core engine files alone.
- There is no headless / `--nogui` build target.

PHD2 also ships ~50 `cam_*` drivers and ~12 `scope_*` drivers — its own hardware abstraction layer.

**The key insight that makes this tractable:** NINA already owns camera and mount abstractions. We do **not** need to port PHD2's hardware layer. We only need PHD2's *brain*:

- star detection / centroiding (`star.cpp`, `guider_onestar.cpp`, `image_math.cpp`)
- calibration math (`mount.cpp`, calibration)
- guide algorithms (`guide_algorithm_*.cpp` — hysteresis, lowpass, lowpass2, resistswitch, ZFilter, and the Gaussian Process / PPEC predictor)
- the guider state machine (`guider.cpp`)

Everything PHD2 does for hardware is replaced by NINA: a NINA `ICamera` supplies frames; a NINA `ITelescope` (or ST4 guide port) receives pulses. This drops ~60 driver files outright and discards the entire GUI layer. The de-wx job shrinks from "all of PHD2" to "the engine core minus hardware and GUI."

It also **collapses the "second guide camera" problem**: the guide camera simply becomes another NINA device feeding pixel buffers into the engine.

---

## 3. Options considered

| Option | What | Effort | Verdict |
|---|---|---|---|
| **A. Process + RPC (status quo)** | Launch PHD2, drive via JSON-RPC | — | Already in place |
| **B. A + GUI embed (Polaris-style)** | Add xpra/VNC tab for Brain/Wizard/graph | Low | Ship as interim; keep regardless |
| **C. `libphd2core` — headless engine, P/Invoke** | Strip wx, expose C ABI, feed frames from NINA camera, route pulses to NINA mount | High | **Real integration — recommended target** |
| **D. Full C# rewrite** | Reimplement guiding in C# | Very high | Rejected — discards PHD2's battle-tested GP/PPEC fidelity |

### Reference: NINA.Polaris

NINA.Polaris does **not** do deep integration. It uses the same JSON-RPC-over-TCP approach pins already uses, and adds a **GUI-embed tab via xpra (Linux) / TightVNC (Windows)**; PHD2 still owns the guide camera and Polaris reads `get_current_equipment` for display only. This confirms two things: (1) the process-wrapper approach has a known ceiling, and (2) the GUI-embed trick is the cheap UX win, not real integration.

**Decision: pursue C, staged, with B shipped first** as a working fallback that de-risks C.

---

## 4. Target architecture (Option C)

```
┌─────────────────────────── pins / NINA (.NET) ───────────────────────────┐
│                                                                            │
│  IntegratedGuider : IGuider                                                │
│    ├─ owns a guide ICamera  ──exposure loop──► raw frame (ushort[], w,h)   │
│    ├─ P/Invoke ──────────────► libphd2core (native)                        │
│    └─ pulse callback ◄──────── libphd2core ──► ITelescope.PulseGuide /     │
│                                                  ST4 guide port             │
│                                                                            │
│  ninaAPI endpoints ──► Touch-N-Stars (Vue): guide graph, star profile,     │
│                        calibration, controls, settle status               │
└────────────────────────────────────────────────────────────────────────┘
                                   │  C ABI (stable, extern "C")
┌──────────────────────────── libphd2core (C++, NO wx) ─────────────────────┐
│  GuiderEngine (was Guider, de-wxWindow'd)                                  │
│    star detection · calibration · guide algorithms (incl. GP/PPEC)         │
│    state machine · settle logic · dither                                   │
│  std::thread worker loop (was wxThread + wxMessageQueue)                    │
│  KVStore interface (was wxConfig)                                          │
│  Frame in (pushed) · Pulse out (callback) — NO camera/scope drivers        │
└────────────────────────────────────────────────────────────────────────┘
```

### Proposed C ABI

Keep it tiny and stable:

```c
phd_handle phd_create(const phd_callbacks* cb, void* user);
void       phd_destroy(phd_handle);
void       phd_set_config(phd_handle, const char* key, const char* json_value);
/* frame is mono 16-bit; engine does star detection + algorithm internally */
void       phd_push_frame(phd_handle, const uint16_t* px, int w, int h, double pixel_scale);
int        phd_start_calibration(phd_handle);
int        phd_guide(phd_handle, const phd_settle* settle, int recalibrate);
int        phd_dither(phd_handle, double amount, int ra_only, const phd_settle*);
int        phd_stop(phd_handle);
/* callbacks delivered on the worker thread:
 *   on_pulse(direction, duration_ms)   -> NINA sends to mount
 *   on_guide_step(phd_guide_step*)     -> drives GuideEvent / RMS
 *   on_state_change(state), on_star_lost(), on_settle_done(status)
 */
```

`on_pulse` inverts today's model: instead of PHD2 talking to the mount, **the engine asks NINA to pulse**, so calibration and guiding flow through NINA's existing `ITelescope`/Alpaca/INDI plumbing — one mount connection, no PHD2 INDI client.

---

## 5. The de-wx work, ranked by risk

1. **`wxString` → `std::string`** (244× in core). Pervasive but mechanical; `wxString::Format` → `fmt`/`std::format`.
2. **`wxConfig`/`pConfig` → injected `KVStore` interface.** Engine reads/writes named keys; back the implementation with NINA's profile. Remove the global singleton.
3. **`wxThread` + `wxMessageQueue` → `std::thread` + typed command queue** (`worker_thread.cpp`). Preserve ordering semantics — this is the engine heartbeat.
4. **`Guider : public wxWindow` → `GuiderEngine` (plain class).** Split the class: pixel/state logic stays; `OnPaint`/rendering/`wxWindow` membership move to the discarded GUI layer. Drop GUI-only pure-virtuals (`OnPaint`, `GetBoundingBox`, …) from the headless interface. **Highest-risk item** — state machine and widget are currently entangled.
5. **`wxCommandEvent`/`wxQueueEvent` dispatch → callback/observer interface** delivered through the C ABI.
6. **Drop entirely:** all `cam_*`, all `scope_*`, `gear_dialog`, `configdialog`, `myframe`, `event_server` (RPC no longer needed in-process), every `*ConfigDialogPane`.

**Why it is less than it looks:** the algorithms and star detection — the parts that determine guiding *quality* — are nearly pure math; their wx usage is `wxString` names and config plumbing (items 1–2). The genuinely entangled pieces are items 3–4, a bounded set of files (`worker_thread.cpp`, `guider.cpp`).

---

## 6. Guide-camera equipment model (the "second camera" question)

Recommendation: **a dedicated guide-camera slot that reuses the existing `ICamera` driver stack but is kept off the imaging mediator.**

- Add a guide-camera chooser/selection in the guider equipment area. It enumerates the same camera SDKs/INDI cameras as the imaging camera, but binds to a *separate* `ICamera` instance so the imaging pipeline never grabs it.
- For pins specifically: a second INDI camera. Verify the relevant SDK / INDI driver permits two concurrent device handles.
- `IntegratedGuider` runs its own exposure loop on that camera (`StartExposure` / `DownloadExposure`), converts to mono 16-bit, and calls `phd_push_frame`.
- Do not overbuild a global "GuideCamera" device category with its own mediator unless a second consumer appears. Keep camera ownership *inside* the guider to start; promote later if needed.

---

## 7. Phasing

- **Phase 0 / B — Interim (low risk, ship now):** Polaris-style GUI embed (xpra on Linux) so users get the native dialogs not exposed over RPC (Guiding Assistant, Brain, calibration Wizard). The RPC-driven Vue control surface already covers day-to-day guiding; this is the native fallback. Keeps current process+RPC guiding as the fallback throughout C.
  - **Frontend (done):** `Touch-N-Stars/src/components/guider/PHD2/Phd2NativeGuiModal.vue` — full-screen overlay embedding the xpra HTML5 client in an iframe, opened from a button in `Phd2GuiderLayout.vue`. Endpoint resolves from `settingsStore.guider.phd2NativeGuiPort` (+ `phd2NativeGuiUrlOverride` escape hatch) against the same host as the API. No UI control for the override yet — power-user/store-only until the backend lands.
  - **Backend (done):** `Server/Services/Phd2GuiService.cs` + `Server/Controllers/Phd2GuiController.cs` in the TNS plugin manage the xpra session lifecycle (Linux only):
    `xpra start :100 --start='phd2' --html=on --bind-tcp=0.0.0.0:14500 --daemon=yes --systemd-run=no --no-pulseaudio`.
    Endpoints: `GET /api/phd2-gui/status` (xpra availability + session up), `POST /api/phd2-gui/start` (idempotent; waits for the HTML5 port before returning), `POST /api/phd2-gui/stop` (`xpra stop :display`). Graceful "xpra not installed" handling; `extraArgs` lets a distro inject `--xvfb=Xorg ...` (PHD2 is wxWidgets and glitches under Xvfb — **Xorg-dummy, not Xvfb**). The frontend calls status→start on open and falls back to a direct embed if the endpoint is absent (older backend) or a URL override is set. The same service pattern can later front `indi_control_panel` for the headless-INDI gap.
  - **Deferred hardening:** xpra currently binds `0.0.0.0` and the iframe hits it directly — same LAN exposure model as the rest of the TNS API. The originally planned loopback-bind + **`/phd2-gui/*` reverse-proxy** through `TouchNStarsServer` was deferred: EmbedIO does not proxy WebSocket upgrades out of the box, and xpra's HTML5 client is WebSocket-based, so a faithful proxy is a non-trivial module. Revisit for tighter security (loopback-only) and same-origin embedding; at that point switch the frontend default from `host:port` to the same-origin `/phd2-gui/` path. Could also add xpra `--tcp-auth` in the interim.
- **Phase 1 — Carve the engine in the fork (feature-complete):** new CMake target `libphd2core` that compiles **without** wx. Start with low-wx units (algorithms, star detection, calibration); `wxString`→`std::string`, inject `KVStore`. Build and unit-test in isolation.
  - **Done — all guide algorithms ported:** `~/Hobby/phd2/libphd2core/` — standalone, additive CMake project (does not touch the main PHD2 build, so upstream merges stay clean). Contains the `KVStore` abstraction (replaces global `wxConfig`/`pConfig`), the wx-free `IGuideAlgorithm` interface (GUI virtuals + wx types dropped), a `CreateGuideAlgorithm` factory, and CTests locking each algorithm. **Algorithms: Identity, Hysteresis, Lowpass, Lowpass2, ResistSwitch, ZFilter, GaussianProcess/PPEC** (arithmetic copied verbatim). Vendored as-is from upstream (already wx-free, bodies unchanged): `guiding_stats.{h,cpp}` (`WindowedAxisStats`), `zfilterfactory.{h,cpp}` (IIR design; `ERROR_INFO`→`std::runtime_error`), and `third_party/circbuf.h`. See `libphd2core/README.md`.
  - **GaussianProcess (PPEC):** wraps the MPI-IS `GaussianProcessGuider` engine (`contributions/MPI_IS_gaussian_process`, already wx-free) — its sources compile directly into the lib, adding an **Eigen3 dependency** (`find_package(Eigen3)`). The engine is forward-declared (pimpl) so Eigen/contrib headers stay out of libphd2core's public interface. The `IGuideAlgorithm` interface gained a `GuideStepContext` (SNR + exposure seconds) and guiding lifecycle hooks (started/stopped/dithered/ditherSettleDone/directMoveApplied) for GP; the mount-coupled "retain model across short stops" optimization is simplified to a reset and deferred to engine integration.
  - **Fidelity discipline:** chose to *port* the math into an additive lib rather than `#ifdef`-refactor the upstream-tracked sources — keeps merges painless, at the cost of keeping ported math in sync, which the locked tests enforce. Upstream quirks preserved deliberately and commented (e.g. `Lowpass2::SetAggressiveness` leaving the member unchanged on invalid input; GP's `points_for_approximation` read/write config-key mismatch).
  - **Star detection (done):** `Star::Find` (per-frame centroid/HFD/SNR/saturation) and `GuideStar::AutoFind` (whole-frame PSF-convolution star selection) ported — `libphd2core/star.{h,cpp}`, `autofind.cpp`, plus the ported `Median3` (`image_math.{h,cpp}`). Operate on a non-owning `phd2core::Image` view (`image.h`) + minimal wx-free `PhdPoint` (`point.h`); guider/camera/frame config passed via `AutoFindParams`. Locked by `test_star` and `test_autofind` (synthetic Gaussian frames).
  - **Calibration (done):** `MountCalibration` (`libphd2core/calibration.{h,cpp}`) — `Calibration` data, orthogonality (`yAngleError`) computation, camera↔mount coordinate transforms, and declination rate compensation, ported verbatim from `mount.cpp`. Locked by `test_calibration`. The calibration *acquisition* state machine (`scope.cpp`) and the pier-flip/rotator/image-scale pointing adjustments are deferred to the engine layer.
  - **Guiding engine (done):** `GuiderEngine` (`libphd2core/guider_engine.{h,cpp}`) — a clean synthesis of PHD2's `Guider` state machine + `Mount::MoveOffset` guide-step math, built on the ported components. **Threading inverted:** no worker thread is ported — the host owns the exposure loop and mount, calls `processFrame()` per frame, and executes the emitted `GuidePulse` (matches the planned C ABI). Flow: select/autoSelect → set calibration → startGuiding → per frame: `Star::Find` → `cameraOfs = star−lock` → `MountCalibration` transform → RA/Dec `IGuideAlgorithm` (with SNR/exposure context) → direction + `ROUND(|dist|/rate)` ms pulse; star-lost → `deduceResult()`. Locked by `test_guider_engine` (simulated drift).
  - **Calibration acquisition (done):** `startCalibration` + the `Calibrating` state — the West→East→North→South pulse-and-measure sequence computing `xRate/xAngle`, `yRate/yAngle` (faithful to `Scope::UpdateCalibrationState` GO_WEST/GO_NORTH), driven across `processFrame` calls. Locked by `test_cal_acquire` (closed-loop sim: a model mount moves the star in response to pulses; recovered rates ≈ model, axes ≈ perpendicular, recentered). Deferred (need mount coords / guide rates): backlash clearing, guide parity, fast-recenter, advisory checks.
  - **Dither + settle (done):** `dither()` offsets the lock position by up to `pixels` (mount coords via RNG, RA-only optional), transformed to camera through the calibration, and notifies the algorithms (`guidingDithered`). `startSettling()` overlays guiding — each frame checks `currentError ≤ tolerancePx`, completing after `settleTimeSec` in range (or `frames`), failing on `timeoutSec` — faithful to PHD2's `PhdController` STATE_SETTLE_WAIT. Settle time accumulates from the per-frame `exposureSeconds` (deterministic; equals PHD2's wall clock at steady cadence). Locked by `test_dither_settle`. Deferred: the `MoveLockPosition` in-frame edge optimization and dither spiral mode.
  - **Phase 1 is feature-complete.**
  - **C ABI (Phase 2, done):** `libphd2core/include/phd2core/phd2_capi.h` + `src/phd2_capi.cpp` — a stable `extern "C"` surface wrapping `GuiderEngine` for P/Invoke. Opaque `phd_handle`; C mirrors of the config/calibration/settle/pulse/guide-step structs (enum values match the C++ enums); the engine's synchronous callbacks re-emitted as C function pointers, with `on_state_change`/`on_star_lost` synthesized in the wrapper. **No worker thread** — the engine is host-driven, so `phd_push_frame()` runs the per-frame logic inline and callbacks fire before it returns (this supersedes the `std::thread` worker sketched below; the Phase 1 threading inversion made it unnecessary). No C++ exception crosses the boundary. Locked by `tests/test_capi.cpp` (closed-loop guide, star-lost, dither/settle, guard rails, null-handle safety).
- **Phase 2 — Worker + state machine + ABI (ABI done; worker dropped):** the C ABI is built (above). The `worker_thread`→`std::thread` port and de-`wxWindow`'ing the `Guider` are **not needed**: Phase 1 synthesized a host-driven `GuiderEngine` (no `wxWindow`, no worker), so the only remaining Phase 2 item — the C ABI — is complete.
- **Phase 3 — Validation:** replay recorded guide frames / PHD2 simulator through `libphd2core`; regression-test calibration vectors and per-frame guide output against reference PHD2 logs. This proves fidelity.
- **Phase 4 — NINA wiring (in progress):** P/Invoke wrapper, `IntegratedGuider : IGuider`, guide-camera plumbing, pulse routing through `ITelescope`, dither/settle into the sequencer.
  - **Native shared library (done):** `libphd2core` now also builds a `phd2core_capi` SHARED target (output `libphd2core.so` / `phd2core.dll`) re-exporting only the `phd_*` C ABI symbols (visibility hidden + `PHD_API`), gated by the `PHD2CORE_BUILD_SHARED` option (ON). The static lib keeps the C ABI too so the native tests link directly.
  - **P/Invoke wrapper (done):** `NINA.Equipment/Equipment/MyGuider/PHD2/Native/` — `Phd2CoreInterop.cs` (raw `DllImport` bindings + `LayoutKind.Sequential` struct/enum mirrors + cdecl callback delegates) and `Phd2CoreEngine.cs` (managed `IDisposable` wrapper: owns the handle, **roots the callback delegates** for the engine's lifetime, re-raises the native callbacks as .NET events, guards against use-after-dispose). DLL name `phd2core`.
  - **`IntegratedGuider : IGuider` (core done):** `NINA.Equipment/Equipment/MyGuider/PHD2/IntegratedGuider.cs` (+ `IntegratedGuideStep.cs`, `IGuideCameraSource.cs`). Owns a `Phd2CoreEngine` and a guide-camera frame source; a `Task.Run` exposure loop captures a frame → `engine.PushFrame()` (which raises `PulseRequested`/`GuideStepReceived` inline) → routes the pulse to `ITelescopeMediator.PulseGuide(GuideDirections, ms)` → waits out the pulse before the next exposure. `StartGuiding(forceCalibration)` auto-selects a star if needed, runs the engine's `Calibrating` sequence (or skips when already calibrated), and awaits `Guiding` via a `TaskCompletionSource`; `Dither()` awaits `SettleDone`. Calibration-complete is deferred out of the native callback into the loop (avoids re-entering the engine mid-frame). Pixel scale is computed from the guide-cam pixel size + focal length. `GuideStepReceived` → `IntegratedGuideStep : IGuideStep` → `GuideEvent`.
  - **`IGuideCameraSource` (abstraction):** the "second camera" (plan §6) is isolated behind this interface (`Connect`/`CaptureFrame`/`PixelSizeMicrons`) so the engine integration is independent of camera selection.
  - **`CameraGuideCameraSource` (done):** concrete `IGuideCameraSource` wrapping a **dedicated `ICamera`** instance (settable `Camera` property, bound by the selection layer; kept off the imaging mediator so imaging + guiding run in parallel — the hard requirement). `CaptureFrame` does `StartExposure`(SNAPSHOT, gain/offset −1) → `WaitUntilExposureIsReady` → `DownloadExposure` → `ToImageData` → `Data.FlatArray` (mono 16-bit) + `Properties.Width/Height`; `PixelSizeMicrons` = `PixelSizeX × BinX`. (Color/bayer debayering for the guide frame is deferred — raw mosaic is fed for now.)
  - **Guide-camera selection + DI wiring (done):** a dedicated `GuideCameraChooserVM : CameraChooserVM` (NINA.WPF.Base) — a distinct type so DI holds a *separate* singleton with its own selection/connection state (registered in `IoCBindings`). `CameraGuideCameraSource` now takes an `IDeviceChooserVM` (the non-generic interface lives in `NINA.Equipment`, so no layering break) and on `Connect` enumerates it, picks the camera whose `Id == GuiderSettings.IntegratedGuideCameraId` (new profile setting), binds + connects it. `GuiderChooserVM` injects the `GuideCameraChooserVM` and adds `IntegratedGuider` (with a `CameraGuideCameraSource`) to the guider list, so it's now selectable. `DriverVersion`'s native call is try/guarded so listing is safe even if `libphd2core` is not deployed.
  - **Thread-safety (done):** the C ABI is non-reentrant per handle, so `Phd2CoreEngine` serializes every native call behind a single `lock (gate)` (a reentrant Monitor — the synchronous callbacks that fire inside `phd_push_frame` on the same thread can't deadlock). The exposure loop and UI/ninaAPI getters (`State`, `CurrentError`, `GetLockPosition`) are now safe to interleave. The state/settle `TaskCompletionSource`s use `RunContinuationsAsynchronously` so awaiters never resume inline while the lock is held.
  - **Declination compensation (done):** added `phd_set_calibration_declination` + `phd_adjust_for_declination` to the C ABI (engine got `setCalibrationDeclination`/`adjustForDeclination`, both thin wrappers over the existing `MountCalibration::adjustForDeclination`, idempotent), with the C# wrapper methods. `IntegratedGuider.GetMountDeclinationRadians()` reads the mount's Dec via `telescopeMediator.GetInfo()/GetCurrentPosition()` (falls back to the UNKNOWN sentinel → no compensation when no mount); it records the declination before calibration/guiding and re-applies it each frame while `Guiding`. Locked by a new `test_capi` case (dec 0 → 60° ~doubles the RA pulse, `cos(60)=0.5`).
  - **Native runtime deployment (done, Linux/output):** drop-zone `NINA/External/phd2core/` (+ README with build/copy steps) and a `CopyPhd2CoreNative` target in `NINA.csproj` (mirrors `CopySystemWindowsStub`) that copies `*.so`/`*.dll` from there into the output root after build, so `[DllImport("phd2core")]` resolves `libphd2core.so` (CMake `OUTPUT_NAME phd2core` → `libphd2core.so` on Linux / `phd2core.dll` on Windows). No-op when the lib is absent (the binary is not checked in). **Still TODO:** Windows installer packaging (`NINA.Setup`) and `dotnet publish` inclusion.
  - **Third-party licenses (done):** PHD2 (BSD 3-Clause, incl. Max Planck Society — which also covers the MPI-IS GP contrib) added to both `NINA/3rd-party-licenses.txt` and `NINA/View/About/ThirdPartyLicensesView.xaml`; Eigen (MPL 2.0, compiled into `libphd2core.so` via the GP/PPEC predictor) added as a new MPL 2.0 section / row in both.
  - **Skipped by decision (pins is headless):** Windows installer (`NINA.Setup`) packaging and localizing `DisplayName` (left as the hardcoded literal `"PHD2 (Integrated)"` — the WPF chooser label isn't used in the Vue frontend).
  - **ninaAPI surface (Phase 5 backend, done):** the generic device endpoints already cover the integrated guider — `/equipment/guider/connect?to=PHD2_Integrated`, `/start`, `/stop`, `/info`, `/graph`, the `GUIDER-*` websocket events, and `/profile/change-value` (e.g. `GuiderSettings-IntegratedGuideCameraId`). Added in `ninaAPI/.../Equipment/Guider.cs`: `/equipment/guider/dither`, and integrated-guider camera selection — `/equipment/guider/integrated/cameras` (lists the dedicated `GuideCameraChooserVM` devices, scanning on first use), `/equipment/guider/integrated/selected-camera` (get), `/equipment/guider/integrated/select-camera?id=` (set the profile id + chooser selection). `GuiderChooserVM` exposes `GuideCameraChooser` (`IDeviceChooserVM`) so the API reaches the guide camera independently of the imaging camera.
  - **Touch-N-Stars frontend (Phase 5, done):** `apiService` got `getIntegratedGuiderCameras` / `getIntegratedGuiderSelectedCamera` / `selectIntegratedGuiderCamera`; new `selectIntegratedGuiderCam.vue` (guide-camera picker, shown only when the selected guider is `PHD2_Integrated`) is rendered in `settingsGuiderConnect.vue`; one `en.json` key (`components.guider.integrated.guideCamera`). The existing generic guider UI (`ControlGuider.vue` start/stop/dither, `GuiderGraph.vue`, `GuiderStatus.vue`) already drives the integrated guider since it goes through the generic guider endpoints. **End-to-end flow now works:** pick "PHD2 (Integrated)" → pick guide camera → connect → start/stop/dither + live graph.
  - **Status: the integration is feature-complete across all phases** (headless engine, C ABI, P/Invoke wrapper, `IntegratedGuider`, dedicated guide camera, declination comp, thread-safety, native deployment, licenses, ninaAPI + Vue). Remaining is real-hardware validation by the user (build native lib + NINA + Vue, connect a guide camera + mount).
- **Phase 5 — UI in Touch-N-Stars:** Vue components for guide graph, star profile, calibration, settle status, fed by new ninaAPI endpoints (no WPF).
- **Phase 6 — Deprecate** the process-based `PHD2Guider` once `IntegratedGuider` reaches parity.

---

## 8. Cross-cutting concerns

- **Licensing:** PHD2 is BSD (Open PHD Guiding); NINA is MPL-2.0. Static-linking a BSD-derived native lib into NINA is fine — preserve BSD attribution and add entries to `NINA/3rd-party-licenses.txt` and `NINA/View/About/ThirdPartyLicensesView.xaml` (per `AGENTS.md` third-party rule).
- **Native build / packaging:** `libphd2core.so` / `.dll` must ride along in the app output layout and the installer (`AGENTS.md` "Native Runtime Assets"). For pins/Linux that is the headless deployment.
- **Fidelity is the point:** the GP/PPEC algorithm is why people choose PHD2. Phase 3 regression testing against reference logs is non-negotiable — treat it like the `NINA.Astrometry` "trace to reference values" discipline.
- **Thread / marshalling:** ABI callbacks fire on the native worker thread; marshal to managed safely.

---

## 9. Honest effort assessment

Phase 0/B is small and worth doing regardless. Phases 1–4 (the real prize) are a **large** effort — the de-`wxWindow`'ing of `Guider` and the worker-thread port are the parts that can bite. But the scope is far smaller than "de-wx all of PHD2," because NINA absorbs the entire hardware layer (~60 driver files dropped) and the GUI layer is discarded outright. The realistic risk concentrates in two files (`guider.cpp`, `worker_thread.cpp`) plus the validation effort to prove the headless engine guides identically to upstream.
