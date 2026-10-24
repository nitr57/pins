# Flight recorder

The internal guider keeps the last minutes of guide frames and telemetry in memory. When something goes wrong it saves
them as an **incident**: the frames around the moment, what the guider measured and sent, what the mount reported and a
first guess at the cause. The Touch-N-Stars guider page lists incidents, replays them and downloads them as a zip.

This file is the design and the contract between the engine, the guider device (`InternalGuider` in NINA.Equipment), the Touch-N-Stars API and the frontend.

## 1. Behaviour

### Purpose

- A replay in the UI for the user, the next morning: "why did guiding fail at 02:13?"
- The same incident as a zip for offline analysis (bug reports, replaying real frames in tests).

### Triggers

| Kind | Alert codes / condition |
|---|---|
| StarLost | 100 StarLost, 101 StarReacquireTimeout |
| Runaway | 301 RunawayDetected |
| MountNotResponding | 302 MountNotResponding |
| SettleTimeout | 500 SettleTimeout |
| CameraFailure | 400 CameraCaptureFailed, 401 CameraReconnecting, 402 CameraFailed |
| MountPaused | 303 MountSlewing, 304 MountParked, 305 MountTrackingOff, 306 MountDisconnected (auto-pauses and the pulse-failure stop) |
| CalibrationFailed | 200-203 calibration failures |
| PulseLimited | 300 PulseLimitReached |
| PulseOutputFailed | 307 PulseOutputFailed |
| DecFlipCorrected | 225 DecFlipCorrected |
| Spike | one guide frame with total error > 4 × the rolling RMS (last 50 guide frames) **and** > 0.5 × the imaging pixel scale (1.5″ without an imaging scale). Not while settling, dithering, during Coach measurements or in the first 20 frames after guiding started. A spike waits 8 frames before it opens an incident: a runaway, a mount that stops responding or a lost star shows as a jump first, and when its alert follows within those frames it gives the incident its kind (the spike joins it). |
| Manual | the user's "Mark incident" (optional one-line note) |

Other codes (105, 106, 404, Info codes) never trigger. Deliberate actions (dither, settle, user stop, Coach measurement
frames) never trigger by themselves; a real alert during them does. Triggers between frames (camera retries, pauses, the
manual mark) belong to the next recorded frame.

### When it records

While guiding and while calibrating (states Calibrating, Guiding, LostLock, Reacquiring, Paused). Not while only looping.
On by default; the guider setting turns it off (then nothing is buffered).

### Window

- **Before:** the last 2 minutes (`PreSeconds` 120). For a calibration failure: the whole calibration from its first
  frame, but at most 5 minutes; when longer, the first frames and the last frames are kept (the middle is dropped with a
  Gap marker).
- **After:** until **recovered** + 30 s (`PostSeconds`). Recovered = the star is found again and the total error has
  stayed under 2 × the rolling RMS from before the occurrence (the RMS during it is inflated by the incident itself)
  for 10 consecutive guiding frames. The window also ends when guiding/calibration stops or fails (EndReason Stopped)
  or at the cap: an occurrence closes at trigger + (`MaxSeconds` 300 − `PreSeconds`) (EndReason Cap).
- **Manual mark:** the last 2 minutes + the next 30 s (EndReason Manual).

### Merging

- A trigger while an incident is open joins it (another entry in `Triggers`, a Trigger marker) and restarts the
  recovery condition. It does not add to Occurrences, which count reopenings only.
- A trigger of the **same kind** as the incident's first trigger within 10 minutes (`RepeatSeconds` 600) after the
  incident closed **reopens** it: Occurrences + 1 (Ongoing), the telemetry of the gap is appended (a telemetry-only
  ring of the last 10 minutes is kept after an incident closes), images only around each trigger and recovery (±5 frames
  of context images and crops, plus the key frames). A Gap marker covers each stretch without images. The stored
  incident is rewritten and a "saved" event is sent again.
- A different kind after the incident closed starts a new incident.

### What each frame keeps

- **Telemetry** (`IncidentFrameRecord`): state, settling/dithering/Coach flags, lock and star position, offsets,
  mount-axis errors, pulses sent (duration, direction, limited), SNR/mass/HFD, the centroid uncertainty of the offset
  ([measurement uncertainty](ALGORITHMS.md#measurement-uncertainty)), every tracked star, the mount snapshot, calibration step, and which images exist.
- **Crops**: the primary star (side = clamp(2 × search region + 1, 31, 95) px) and up to 8 secondaries (31 px), from the
  processed frame.
- **Context image**: the processed frame, mean-binned by `ContextBinning` = max(1, round(max(width, height) / 480)),
  16-bit.
- **Key frames**, full resolution: the last good frame before each trigger, the trigger frame, and the first frame after
  recovery (for ongoing incidents: at each occurrence). A ring of the last 2 full frames makes the "last good" frame
  available. At most 8 key frames per occurrence are held in memory, with one place kept for the recovery frame.
- Pixels are the **processed** frame (after dark/defect correction and noise reduction), exactly what the guider used.
  The FITS headers name the dark/defect map and the noise reduction.

Memory: about 40-50 MB for the 2-minute buffer at 1 s exposures and a 1936×1216 sensor (up to about 100 MB in a long
calibration). CPU: binning and crops, a few ms per frame on a Pi. Nothing is written until an incident is saved (on a
background task). On disk an incident takes about 40-45 MB for a single occurrence and 70-100 MB when ongoing, so the
default 1 GB budget holds about 10-25 incidents.

### Storage

- Folder given by the host; the host uses `CoreUtil.APPLICATIONTEMPPATH/InternalGuider/Incidents/` (~/.local/share/NINA/…).
- One folder per incident `<id>/`: `incident.json` (the `Incident` record, enums as strings), `summary.json` (for fast
  listing, with the kept flag), `context.fits`,
  `crops.fits` and `key.fits` (multi-HDU 16-bit FITS; each HDU has FRAME, KIND, STAR, X0, Y0, BINNING, DATE-OBS,
  EXPTIME and the preprocessing keywords). An index of HDU offsets in `images.json` gives random access.
- Id: `yyyyMMdd-HHmmss-<kind>` (UTC), with a `-2`, `-3` … suffix when taken.
- **Budget**: 1 GB and 50 incidents (`BudgetBytes`, `MaxIncidents`); simulator incidents have their own 200 MB and 50.
  Before saving, the oldest non-kept incidents of the same class (real / simulator) are deleted until the new one fits.
  When only kept incidents are left and they fill the budget, the new incident is saved without images
  (FramesOmitted Budget).
- **Disk space**: when saving the images would leave less than 2 GB free (`MinFreeBytes`), the incident is saved without
  images (FramesOmitted DiskSpace). Rotation runs first.
- Keep / release, delete one, delete all not kept.

### Diagnosis

`IncidentDiagnoser.Diagnose(Incident)` → `IncidentDiagnosis`: one likely cause, an English message, parameters for
localisation and evidence items (each with a code, parameters and optionally the frame it points at). Always worded as
"likely"; `Unclear` with the evidence when no rule fits.

| Cause | Rule (first match in this order wins; tune against the simulator) |
|---|---|
| Camera | the incident is a CameraFailure, or frames are missing/late around the trigger (capture failures, timeouts) |
| MountMoved | the mount snapshot shows slewing, tracking off, parked or disconnected, or the pier side changed |
| CalibrationMismatch | a Runaway or DecFlipCorrected, or the error grew over ≥ 4 frames while pulses were sent against it |
| MountNotMoving | MountNotResponding, or ≥ 3 pulses whose expected motion (duration × calibrated rate) is ≥ 3 px but the star moved < 25 % of it |
| FieldJump | the primary and ≥ 2 secondaries moved by the same vector (within 1.5 px) of ≥ 3 px (or ≥ ⅓ search region) between two consecutive frames (see below for jumps the tracker did not measure) |
| Clouds | all tracked stars' SNR/mass fell together by ≥ 50 % within ≤ 60 s, recovering afterwards (or a total loss) |
| Dew | all stars fading over minutes while HFD grows by ≥ 30 % |
| GuideStarOnly | the primary was lost or its mass/SNR jumped while the secondaries stayed normal (saturation, hot pixel, a star next to it) |
| DriftTooFast | the star drifted steadily toward the search-region edge (≥ 70 % of it) over ≥ 5 frames while pulses were limited (RaLimited/DecLimited) or at max duration |
| PeriodicSpike | the incident is a Spike, the worm period is known, and ≥ 2 earlier spikes of the same guiding session (`IncidentContext.EarlierSpikes`) lie within ±5 % of whole multiples (1-3) of the worm period before it |
| Unclear | nothing matched |

Field jump also covers the multi-star tracker not measuring the secondaries on the jump frame (the field counts as
having jumped when the secondaries, measured again a few frames later, are where the pulses since the jump put them)
and jumps that lose the star for up to 10 frames / 30 s (secondaries on the loss frame, including FallbackRejected ones,
shifted like the star found again; or, when nothing was measured, a sudden loss whose star and secondaries reappear
where a jumped field would be).

Evidence codes and their parameters (the diagnosis-level `Parameters` repeat those of the main evidence):

| Cause | Evidence codes {parameters} |
|---|---|
| camera | `cameraFailures` {count}, `frameGap` {seconds} |
| mountMoved | `mountSlewing`, `mountTrackingOff`, `mountParked`, `mountDisconnected` {}, `pierSideChanged` {from, to} |
| calibrationMismatch | `errorGrew` {frames, fromPx, toPx, axis}, `runaway` {axis}, `decFlipCorrected` {} |
| mountNotMoving | `pulsesWithoutMotion` {pulses, expectedPx, movedPx, axis} |
| fieldJump | `fieldJump` {jumpPx, jumpArcsec, stars} |
| clouds | `starsFaded` {stars, dropPercent, seconds}, `recoveredAfter` {seconds} |
| dew | `starsFadingSlowly` {stars, dropPercent, minutes}, `hfdGrew` {percent} |
| guideStarOnly | `primaryOnly` {dropPercent, secondaries}, `saturated` {}, `massJump` {percent} |
| driftTooFast | `driftToEdge` {percentOfRegion, frames, axis}, `pulsesLimited` {frames, axis} |
| periodicSpike | `spikeRepeats` {count, periodSeconds}, `spike` {errorArcsec, rmsArcsec} |
| unclear | `spike` {errorArcsec, rmsArcsec}, `starLost` {status}, `trigger` {kind} |

`axis` is `ra` or `dec`; `from`/`to` are pier sides (`East`, `West`). UIs localise causes and evidence by code with the
parameters and fall back to the English `Message` for codes they don't know.

Parameters carry the numbers the evidence text shows (e.g. `snrDropPercent`, `jumpPx`, `jumpArcsec`, `durationSec`,
`expectedPx`, `movedPx`, `periodSeconds`). Units are part of the name (like the Coach).

### Download (zip)

`pins-incident-<id>.zip`: `incident.json`, `context.fits`, `crops.fits`, `key.fits` (those that exist),
`settings.json` (the engine and guider settings at the time), `guide-log.txt` (the guide-log lines of the window
±1 min), `pins-log.txt` (the NINA/PINS log lines of the window ±1 min), `README.txt` (what the files are). The site
latitude/longitude and the home directory are masked in all text files of the zip.

## 2. Engine (NINA.GuideEngine)

Namespace `NINA.GuideEngine.Incidents`.

- `IncidentModel.cs`: the records above (`Incident`, `IncidentFrameRecord`, `IncidentTrigger`, `IncidentMarker`,
  `IncidentTags`, `IncidentContext`, `IncidentDiagnosis`, `IncidentEvidence`, enums). Shared by the recorder, the store
  and the diagnoser; add fields only, don't rename.
- `IncidentSettings` (record, in `GuiderSettings.Incidents`): `Enabled` (true), `PreSeconds` 120, `PostSeconds` 30,
  `MaxSeconds` 300, `RepeatSeconds` 600, `RecoveryFrames` 10, `RecoveryRmsFactor` 2, `SpikeFactor` 4,
  `SpikeImagingFraction` 0.5, `SpikeFallbackArcsec` 1.5, `SpikeWindow` 50, `SpikeWarmupFrames` 20.
- `IncidentRecorder` (internal to the guider, fed from the guide loop on the loop thread): per frame the processed
  `GuideFrame`, stars, lock, step values, mount snapshot, state and flags; alerts; state changes; calibration
  start/end. Builds crops and the context image right away (the frame object is not kept, except the 2-frame key
  ring). Saves through the store on a background task.
- `IncidentStore(string directory, IncidentStoreOptions options, Func<string, long>? freeBytes = null)`:
  `List()` (summaries newest first), `Get(id)`, `ReadImage(id, IncidentImageKind kind, long frame)`,
  `ReadCrops(id, frame)`, `SetKept`, `Delete`, `DeleteAllNotKept`, `WriteZip(id, Stream, IEnumerable<(string Name,
  string Content)> extras)`, `Usage` (bytes/count per class), `Save(...)`. Thread-safe.
- FITS writing lives in the engine (`Imaging/FitsWriter.cs`, multi-HDU, keywords); the host's dark library uses the
  engine writer too.
- `Guider`: `SetIncidentStore(IncidentStore? store, Func<IncidentTags> tags)`, `string? MarkIncident(string? note, out
  string? error)`, `string? RecordingIncidentId`, `IncidentStore? Incidents`. `AlertEvent` gains `string? IncidentId`
  (set when that alert started or joined an incident). New events: `IncidentStartedEvent(Timestamp, Id, Kind)`,
  `IncidentSavedEvent(Timestamp, Incident Summary)` (frames list empty in the event), `IncidentDeletedEvent(Timestamp,
  Id)`.
- Diagnosis runs when the incident closes (before saving), with `IncidentContext` filled from the settings, the
  calibration and the Predictive RA algorithm's worm period.
- Simulator: `Simulator.InjectFault(SimulatorFault fault)` at runtime: `Clouds` (transparency ramps to 0 over 10 s, stays
  90 s, ramps back), `Bump` (the mount jumps by 15 px in a random direction, once), `MountStopsResponding` (60 s),
  `CameraFailure` (3 failures in a row), `Runaway` (Dec pulses inverted for 120 s). `MountStopsResponding` also kicks
  RA by 7 px and `Runaway` Dec by 3 px at the start (on a good mount neither raised an alert otherwise); Bump moves 15
  sensor px. The host chooses the scenario from `SimulatorScenario.Presets` by name; it applies on the next connect.

## 3. Contract (NINA.Equipment/Interfaces/IAdvancedGuider.cs, pins)

Methods: `GetIncidents()` → `AdvancedIncidentList`, `GetIncident(id)` → `AdvancedIncident`,
`GetIncidentImage(id, kind, frame)` → `AdvancedIncidentImage` (kind "context" | "key"),
`GetIncidentCrops(id, frame)` → list of `AdvancedIncidentImage`, `SetIncidentKept(id, kept)`, `DeleteIncident(id)`,
`DeleteAllIncidents()` → count, `MarkIncident(note, out error)` → id, `WriteIncidentArchive(id, Stream, ct)`.
`AdvancedGuiderAlert.IncidentId`. Event type `"incident"` with `AdvancedIncidentEvent { Action: started | saved | deleted,
Id, Summary }`. Enum values travel as their names; causes and kinds as in §1 with a lower-case first letter for causes
(`clouds`, `fieldJump`, …) and PascalCase for kinds (`StarLost`, …); end reasons lower case (`recovered`, …);
FramesOmitted `null` | `diskSpace` | `budget`; marker types lower case. Incidents are readable, manageable and
downloadable while the internal guider is selected but not connected; only Mark needs a guiding or calibrating guider.

Guider settings (group "Incidents"; the simulator ones only when the guide camera is the simulator):

| Name | Type | Default | Meaning |
|---|---|---|---|
| IncidentRecorder | bool | true | record incidents |
| IncidentBudgetMb | int | 1000 (100-20000) | disk budget for real incidents |
| SimulatorScenario | enum | GoodMount | `SimulatorScenario.Presets` names |
| SimulateFault | action with Options | — | Clouds, Bump, MountStopsResponding, CameraFailure, Runaway: one button per option; setting the value runs that fault |

An `action` setting with `Options` renders one button per option; `TrySetSetting(name, option)` runs it.

## 4. HTTP API (Touch-N-Stars, /api/native-guider)

Same envelope and status codes as the rest of the native-guider API (camelCase, `{ success, response }`, 409
NotAvailable, 404 when unknown).

| Method | Path | Response |
|---|---|---|
| GET | `/native-guider/incidents` | `AdvancedIncidentList` |
| GET | `/native-guider/incidents/{id}` | `AdvancedIncident` (all frames' telemetry) |
| GET | `/native-guider/incidents/{id}/image?kind=context\|key&frame=N&maxWidth=1024&stretch=0.2&quality=80` | JPEG, rendered like `/image` (server-side STF stretch); headers X-Frame-Number, X-Image-Binning, X-Image-X0/Y0, X-Frame-Width/Height (image pixels) |
| GET | `/native-guider/incidents/{id}/crops?frame=N` | `{ frame, crops: [{ star, x0, y0, width, height, pixels[] }] }` (raw 16-bit, like frame-info's crops) |
| POST | `/native-guider/incidents/{id}/keep` body `{ kept: bool }` | the updated `AdvancedIncidentSummary` |
| DELETE | `/native-guider/incidents/{id}` | `{ deleted: true }` |
| DELETE | `/native-guider/incidents` | `{ deleted: n }` (all not kept) |
| POST | `/native-guider/incidents/mark` body `{ note? }` | `{ id }`, 409 Rejected with the error when refused |
| GET | `/native-guider/incidents/{id}/download` | `application/zip`, `Content-Disposition: attachment; filename="pins-incident-<id>.zip"` |

WebSocket `/ws/native-guider`: message type `incident` with `{ action, id, summary }`; `alert` messages carry
`incidentId`.

## 5. Frontend (Touch-N-Stars, src/components/guider/native)

- **Incidents tab** (phone and wide layouts), with a badge counting incidents saved since the tab was last opened
  (last-seen time per browser). List: time, kind(s), likely cause, duration, occurrences, recovered/stopped, profile and
  rig tags, simulator tag, kept pin, size; filters current profile first; budget line ("312 MB of 1 GB, 9 incidents");
  keep / delete / delete all (confirm once, says kept ones stay) / download; "recording…" row while one is open.
- **Replay** (full-screen): the frame (context image, or the key frame when there is one for that frame) with the
  overlays of the live frame view (stars, lock position, primary, and a search-region box); the star crops (peeper
  style); the guide graph of the window synced to the scrubber with trigger/recovery/gap markers; per-frame telemetry
  (time, state, SNR, HFD, mass, error ″, pulses sent, mount state, pier side, calibration step); the diagnosis card on
  top ("Likely: clouds" + evidence, tapping an evidence item jumps to its frame); controls: scrubber, step ±1,
  play/pause at 1× / 4× / 16× real time, jump to previous/next marker; download button; the note of a manual mark.
- **Alerts**: a Replay button on alerts with `incidentId` (event log rows and the alert modal) once that incident is
  saved; "recording…" before.
- **Mark incident** button in the guide controls row (only while guiding or calibrating): opens a one-line note prompt,
  then POST mark; a toast with "Replay" when saved.
- Settings: `action` settings with `options` render a button group (one button per option).
- Translations in all 14 locales; unit tests for the pure helpers (cause/evidence text, timeline and marker mapping,
  playback timing, badge counting).

## 6. Validation

- Engine closed loop, one test per simulator fault (clouds, bump, mount not responding, runaway, camera failure,
  calibration failure, slew): exactly one incident, the right trigger(s), the right likely cause; clouds with repeated
  losses → one ongoing incident; a spike trigger fires on a bump but not on a calm GoodMount hour; manual mark window;
  budget rotation, kept incidents, disk-space guard, simulator budget; store round trip (JSON + FITS + zip).
- Cost: buffer RAM measured in the simulator (target < 60 MB while guiding, 1936×1216 at 1 s), per-frame overhead timed
  (target < 5 ms on x86).
- Real app: fault injection from the UI; screenshots of the list, the replay (phone and desktop) and the zip opening
  with a FITS reader.
