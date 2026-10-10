# Native Guider — user guide

The internal guider runs inside pins (no PHD2 process). It appears in the guider chooser as
**Native Guider** next to PHD2; switching back to PHD2 is just selecting PHD2 again.

## 1. Requirements

* A guide camera in the **guide camera slot** of the equipment, next to the imaging camera: any camera the slot
  offers (native SDK, INDI, Alpaca/ASCOM). The slot keeps one physical camera out of both slots; an INDI guide
  camera's driver is loaded separately and never unloads the main camera's driver.
* Mount connected in PINS with **pulse guiding** support (INDI, ASCOM/Alpaca…), or the guide camera's
  **ST4** port (`PulseOutput = CameraST4`, INDI guide cameras; works without a mount connection, but then pier
  side and declination are unknown — recalibrate after a meridian flip).

## 2. First-time setup (Touch-N-Stars)

1. Equipment → Guide camera: choose the guide camera. Connect the **mount**.
2. Equipment → Guider: choose **Native Guider** and press connect. Connecting the guider also connects the
   guide camera if it isn't connected yet; disconnecting the guider leaves it connected. While the guider is
   connected it has the guide camera to itself: other captures with it are refused.
3. Open the **Guiding** page → Settings (or use the API, see below) and check the *basic* settings:

| Setting | What to set | Default / fallback |
|---|---|---|
| `GuideSource` | `GuideCamera` (the guide camera slot) or `Simulator` (built-in simulator, no hardware) | `GuideCamera` |
| `FocalLengthMm` | guide scope focal length (main scope FL with an OAG) | PHD2 focal length from the profile, else unknown |
| `ExposureSeconds` | 1–4 s typical | PHD2 exposure from the profile, else 2 s |
| `Gain` | camera gain in the driver's native units (−1 = the guide camera's gain setting) | −1 (PHD2's gain is a percentage and is not carried over) |
| `Binning` | 1 (2 for very long focal lengths) | 1 |
| `DecGuideMode` | Auto / North / South / Off / Drift (see [Guiding](#3-guiding)) | PHD2 setting, else Auto |

Every setting you never changed falls back to the matching **PHD2 setting of the pins profile**, so an
existing PHD2 setup (algorithms, aggressiveness, min-move, max pulses, Dec mode…) carries over. Settings of the
pins-guider plugin carry over too (they are stored under the same key).
Settings marked *requires reconnect* (guide source, guide output) apply after reconnecting.

**Darks**: if `UseDarkLibrary` is on (default), the guider uses its own dark library, or — if none was
built — **PHD2's dark library** (`~/.phd2/darks_defects/PHD2_dark_lib_*.fit`) when its frame size matches.
Build a fresh one from the Guiding page (cover the scope; default 0.5–4 s, 5 frames each).

### Settings without the UI (curl)

```sh
H=http://<pins-host>:1888/v2/api/equipment/guider
curl "$H/get-settings"                                                   # all device properties
curl "$H/set-setting?settingName=FocalLengthMm&newValue=248"
curl "$H/set-setting?settingName=GuideSource&newValue=Simulator"
curl "$H/set-setting?settingName=InternalGuiderSetting&newValue=MinSnr=8"  # any setting: Name=Value
```

## 3. Guiding

* **Start guiding** (TNS button, or the sequencer's *Start Guiding*): selects the best star
  (auto-select avoids hot pixels, saturated, crowded and edge stars), calibrates if there is no valid
  calibration (or when forced), starts guiding and waits until settled (NINA profile settle pixels /
  time / timeout).
* **Calibration** is PHD2's procedure (West/East/backlash/North/South), is **saved** and reused across
  sessions, adjusted for declination, binning and pier side. After a meridian flip the calibration is
  flipped automatically; a **post-flip Dec self-check** watches the first corrections and fixes the
  *Reverse Dec after meridian flip* setting if Dec runs away.
* **Dithering** uses the NINA profile dither settings (pixels, RA only) and PHD2's settle semantics.
* **Multi-star**: up to 9 stars (PHD2 algorithm). If the primary star drops out for a frame, its
  position is estimated from ≥3 agreeing secondaries instead of declaring the star lost.
* **Guide algorithms**: RA Hysteresis (0.7 / 0.1), Dec ResistSwitch (1.0, fast switch), min-move from the
  image scale — PHD2 defaults; Lowpass2 available. Not in PHD2, for every algorithm: a **minimum pulse** (`MinPulseMs`,
  20 ms; 0–50 ms, 0 = any length as in PHD2). A shorter guide pulse is rounded to 0 or to the minimum, whichever is
  nearer; calibration, recenter and manual moves keep their length. EQMod drops pulses under 10 ms, and a 10 ms Dec
  reversal left a Sky-Watcher Dec motor running for seconds.
* **Predictive algorithm (experimental)**: set `RaAlgorithm` and/or `DecAlgorithm` to `Predictive`. It learns the
  seeing and the mount's own motion from the guide frames and corrects the error it predicts for the next frame, so
  aggression, hysteresis and min move don't apply to it (nor does the Coach's advice for them); the settings sheet hides
  them. It needs a few minutes of guiding to learn (about 120 frames, not counting the settling after a dither). A frame
  whose star position is noisier than usual (thin clouds dimming the stars; judged from their size and SNR) counts less
  instead of being chased, never more than a usual frame; noise that lasts (over half of the last 360 frames) becomes the
  usual level. A dither, a pause, stopping and starting, other settings, its own parameters and Coach trials keep what
  it learned; another algorithm, a new binning or reconnecting the guider start over. The status strip shows *Learning
  sky & mount n %* while it learns; the Statistics card shows per axis its frame weight and the seeing, mount motion and
  drift it learned. The PINS log (Debug level) records its model changes, jumps, stretches of noisier frames and a
  summary every 50 frames. See [ALGORITHMS.md](ALGORITHMS.md#predictive) (Predictive, measurement uncertainty) for how it
  works and how it compares with the PHD2 algorithms in the simulator.
* **Correction pace** (Predictive, advanced settings `RaCorrectionPace` / `DecCorrectionPace`, 0.2–1, default 0.5): the share of the
  remaining error corrected per frame. At 0.5 an offset, e.g. a gust that shifted the star, is taken out over 2–3
  frames instead of all at once, so the error doesn't swing past zero and the pulses stay moderate. 1 corrects all of
  it at once, as before. Drift and periodic error are always corrected in full. See
  [ALGORITHMS.md](ALGORITHMS.md#correction-pace).
* **Pulse model** (advanced setting `PulseModel`, on by default, any algorithm): learns from the dithers how far a
  guide pulse really moves the star on each axis, and sizes the pulses by it. On the EQMod test mount, PINS's
  calibration made RA pulses 14 % too strong and Dec pulses 22 % too weak.
  * It starts as calibrated and changes only what the dithers clearly show, after about 10 dithers.
  * The guide log records each change (`INFO: Pulse model: …`).
  * What it learned is kept per profile and mount until the next calibration.
  * It doesn't compensate Dec backlash: for that, use Dec guide mode Drift below.

  See [ALGORITHMS.md](ALGORITHMS.md#pulse-model).
* **Periodic error prediction** (Predictive on RA, `PeriodicErrorPrediction`, on by default): learns the periodic error
  of the RA worm and corrects it before it shows. The worm period comes from `WormTeeth` (the teeth of the RA worm
  wheel, e.g. 180 on an EQ6-R, 135 on an HEQ5), or is detected after about five worm turns (40 min for a 480 s worm) and
  snapped to a whole tooth count once that is unambiguous; only a set or snapped period is taken for the worm's. Either
  way the curve counts only once it stays the same from one worm turn to the next (two turns with `WormTeeth` set), so
  that a wobble of the mount that comes and goes is not taken for periodic error. The turns add up while guiding stops
  and starts again (autofocus, a filter change) and the mount tracks on; a slew, a plate-solve sync or a meridian flip
  starts the count over, since a sync shifts the curve against the hour angle (without a mount connection, every start
  of guiding does). A detected period that stops being stable is forgotten and the search goes on; its stored curve is
  deleted once another period is found or the data show a different curve, but kept when a poor night only can't
  confirm it. A known tooth count (set, snapped, or stored with the curve) is never forgotten this way: its curve is just
  not applied while it is not stable. The curve (worm fundamental + 2 harmonics) is fitted against the mount's hour
  angle, so it holds for any target. It is applied only while it predicts better than guiding without it, fading in and
  out; a periodic error that moves the star between two guide frames by less than 0.15 of the seeing σ is shown as *too
  small to predict*, whether its curve is stable yet or not. Any frame rate works (frames closer than 1.2 s are averaged
  for the detection). The curve is stored per profile and mount (`periodic-error.json` next to the calibrations) and
  restored on connect, so the next night starts with it; *Forget periodic error* (a button in the settings sheet)
  forgets it after work on the mount. After a meridian flip a known tooth count keeps the phase, otherwise the phase is
  learned anew. Without a mount connection (ST4 only) it works from the time and stores nothing. The status strip shows
  *Learning periodic error n %* while it learns (from the data since the last slew, sync or flip); the Statistics card
  shows its ± swing at the target, the period and the teeth, and *negligible* with a line when it is too small to
  predict; the Coach's drift step reports the learned curve instead of its own short measurement. The PINS log (Debug)
  and the guide log note its detection, the stability verdicts, the fit and when it switches on or off. Details in
  [ALGORITHMS.md](ALGORITHMS.md#periodic-error-prediction).
* **Dec guide mode Drift** (`DecGuideMode` = `Drift`, opt-in; not in PHD2): guides Dec in one direction only, against
  the Dec drift it measures while guiding, so the Dec gears never reverse and backlash stops mattering. Like PHD2's
  North or South, but it picks the direction itself, and a new target, a meridian flip or a drift that reverses don't
  leave it guiding the wrong way. Best for a mount with noticeable Dec backlash and a polar-alignment drift of about
  0.5″/min or more (the Guiding Coach suggests it then); without backlash Auto guides a weak drift a little better.
  * *Drift*: the slope of the open-loop Dec position (measured error + every correction that moved the mount) over the
    last 5 minutes, with its uncertainty (seeing, and the mount's own wander learned over the session). With exposures
    longer than 12 s the fit covers the last 25 frames instead, and a direction is picked later (with 30 s exposures
    after about 11 minutes). The dead band of the Dec gears is learned from the reversals, so corrections that only took
    up backlash don't count as motion. A dither, a pause, a lost star and a sudden jump of the star (a bump, or a Dec
    motor that kept running after a very short pulse) start a new stretch of the fit instead of counting as drift.
  * *Direction*: both directions until a drift has held for a minute (at least 2 minutes of guiding first): significant
    (2.5 σ), and even one σ weaker it brings the star back from the frame-to-frame noise within 30 s (a weaker drift would
    leave the star waiting on the other side), then only the direction that counters it. It stays while the drift is at
    least one σ in its favour and not clearly too weak (one σ stronger it would reach that floor); when that has not been
    so for 2 minutes, it switches to the other direction if that drift holds, else it goes back to both directions. After
    that the gears may sit anywhere in their play, so a direction is picked again only when the drift holds whatever the
    dead band is (with an algorithm that reverses often, like Predictive, usually not before the dead band is learned).
  * Both directions also while settling after a dither or at the start, during calibration, and while a *safety valve*
    is open: the error stayed beyond max(1.5 px, 3 × the recent Dec RMS) on the side the direction can't correct for
    10 frames; it closes when the error is back.
  * A new guiding start, a meridian flip, a large slew or a new binning start over; a dither, a pause or a lost star keep
    the drift. Choosing Drift while guiding uses the drift measured so far (it is measured in every Dec guide mode): when
    it has held for a minute already, one direction is guided from the next frame on (so a Coach trial with Drift guides
    one way from its start).
  * The guide algorithm learns only the pulses that went out: a correction the direction suppresses is no correction
    for Predictive.
  * *Backlash compensation* acts only while both directions are guided (like PHD2, which switches it off for North and
    South): one direction never reverses the gears. The Coach suggests Drift without it.
  * *Shown* in the Statistics card (Dec direction, drift) and next to the Dec RMS in the status strip; the guide log gets
    an `INFO: Dec guide direction: …` line on every change and when the valve opens or closes, the PINS log (Debug) also
    a summary every 100 frames with the learned dead band and the jumps left out. Simulator and real-night results in
    [ALGORITHMS.md](ALGORITHMS.md#dec-guide-mode-drift).

## 4. Safety protocols

| Situation | What the guider does |
|---|---|
| Star lost (clouds, obstruction) | no corrections; searches near the last position, then full frame (only accepting a star near the old position); after 60 s (`ReacquireTimeoutSec`) a critical notification, then it **keeps searching** and resumes by itself when the star returns (`StopOnLostStarTimeout` in the engine stops instead) |
| Corrections make the error grow / excessive pulse duty | **Runaway** → stops guiding, notification |
| Large pulses without star movement | **Mount not responding** → stops, notification |
| Mount slewing / parked / tracking off | pauses corrections, resumes afterwards; after a large move it waits for an explicit resume / restart |
| Guide camera errors | retries 2×, reconnects once, then stops with a notification |
| Calibration looks wrong (non-orthogonal, odd rates, few steps) | warning, guiding continues |
| Dec runs away within 15 min after a meridian flip | Dec direction inverted once (and the *Reverse Dec after meridian flip* setting saved); a second runaway stops guiding |
| Max pulse | hard limit per axis (`MaxRaDurationMs` / `MaxDecDurationMs`) |

If your mount driver misreports *slewing* or *tracking*, turn off `PauseWhenSlewing` /
`PauseWhenTrackingOff`.

### Flight recorder (incidents)

While guiding or calibrating, the guider keeps the last 2 minutes of frames in memory. When something goes wrong it
saves an **incident**: the frames from 2 minutes before until 30 s after guiding recovered (at most 5 minutes), what it
measured and sent each frame, what the mount reported, and a first guess at the cause ("Likely clouds: all 12 stars
faded by 100 % within 8 s and came back after 97 s").

* **Triggers**: star lost, runaway, mount not responding, settle timeout, camera failures, mount pauses (slewing,
  parked, tracking off, disconnected), failed calibrations, pulse limit reached, pulse output failed, the Dec
  correction after a flip, a sudden **spike** (one frame far above the recent RMS and above half the imaging pixel
  scale), and **Mark** in the guide controls (2 minutes before + 30 s after, with an optional note). Dithers, settling
  and Coach measurements never trigger by themselves. The sequencer's own slews stop guiding first, so they record
  nothing.
* The same trouble again within 10 minutes (e.g. passing clouds) joins the incident as an **ongoing** one: the
  telemetry stays complete, the images are kept around each loss and recovery.
* **Touch-N-Stars → Guider → Incidents**: the list (newest first, the current profile's first), **Replay** (frame with
  stars, lock position and search region, star crops, the guide graph with markers, per-frame values, the likely cause
  and its evidence; play at 1×/4×/16×), **Keep** (rotation skips it), **Delete**, **Delete all** (kept ones stay) and
  **Download** (zip: frames as FITS, telemetry/diagnosis/settings as JSON, the guide-log and PINS-log excerpts with
  site location and home paths masked). Alerts that started an incident get a Replay button. Incidents can be viewed
  without connecting the guider.
* **Storage**: `IncidentBudgetMb` (1000 MB by default, at most 50 incidents); the oldest non-kept incidents go first. An
  incident is about 40 MB, an ongoing one up to about 100 MB. With less than 2 GB free on the disk an incident keeps
  its telemetry and diagnosis but no images. Simulator incidents have their own 200 MB.
* `IncidentRecorder` turns it off. It costs about 45 MB of memory and 2 ms per frame.
* **Simulator**: with the simulated guide camera, `SimulatorScenario` picks the simulated mount and sky (reconnect to
  apply) and `SimulateFault` injects clouds, a bump, a mount that stops responding, camera failures or a runaway.

## 5. Guiding Coach

The **Coach** tab on the Guiding page measures your rig under tonight's sky, explains what limits your guiding and
recommends settings — and proves them with short guided trials. A full session takes about 15 minutes; pick steps
to make it shorter. Design details: [COACH.md](COACH.md).

1. **Camera check** — loops exposure × gain combinations (guiding stopped) and recommends the combination with the
   lowest centroid noise that doesn't saturate the star.
2. **Sky & mount drift** — 3 minutes with guiding output off: seeing, periodic error, RA/Dec drift, polar alignment
   error, wind gusts, longest useful exposure. Calibrates first if needed.
3. **Mount response** — Dec backlash, smallest pulse that moves the mount, direction asymmetry, guide rate check.
4. **Guided trials** — guides ~2 minutes each with your current settings, the coach's suggestion and a variant
   (and your settings again to detect changing conditions). Thin clouds during your settings' trial don't make the
   alternative win (judged from each frame's centroid uncertainty). Your settings are restored afterwards.
5. **Report card** — grade relative to your imaging scale (camera pixel size + telescope focal length from the
   profile), error budget (seeing / centroid noise / mount), ranked actions with *Apply* buttons, and a per-night
   history (per profile).

For an axis with the Predictive algorithm the coach suggests no min move, aggression or hysteresis (it sets its own
gain); if nothing else is worth a trial, the trials only measure your current settings (*Nothing to try*).

Guiding that was running when the coach started is resumed with your original settings when it ends — also on
cancel or errors. Starting/stopping guiding, dithering or a slew (e.g. from a running sequence) interrupts the
session. While guiding normally, the guider shows occasional **hints** (e.g. RA oscillating → lower aggression) as a
dismissable chip in the status strip; `LiveHints` in the engine settings is on by default.

## 6. Files

* Guide logs (PHD2 format, open with PHD2 Log Viewer): `~/.local/share/NINA/InternalGuider/Logs/InternalGuider_GuideLog_<night>.txt`
  (one file per night, noon to noon). Settings changed while guiding appear as `INFO: Guiding parameter change, …` lines
  (the header lines that changed). `NINA.GuideEngine/tools/guide-log-stats` summarises a log per block (RMS, overshoot after large
  corrections, pulse lengths, Dec reversals).
* Calibrations: `~/.local/share/NINA/InternalGuider/calibrations.json`
* Learned periodic errors and pulse models: `~/.local/share/NINA/InternalGuider/periodic-error.json`,
  `~/.local/share/NINA/InternalGuider/pulse-model.json`
* Dark libraries: `~/.local/share/NINA/InternalGuider/Darks/`
* Guiding Coach reports: `~/.local/share/NINA/InternalGuider/Coach/coach-*.json`
* Incidents (flight recorder): `~/.local/share/NINA/InternalGuider/Incidents/<id>/` (`incident.json` + FITS images)
* pins log (search for `InternalGuider`): `~/.local/share/NINA/Logs/`

The files of the pins-guider plugin (`~/.local/share/NINA/NativeGuider/`) move to `InternalGuider/` the first time
pins starts with the internal guider.

## 7. Deployment

The internal guider is part of pins: the engine is `NINA.GuideEngine`, the guider device
`NINA.Equipment/Equipment/MyGuider/Internal`. The Touch-N-Stars guider page talks to it through the Touch-N-Stars plugin.

## 8. Troubleshooting

* *"The guide camera could not be connected"* — choose a guide camera in the guide camera slot and check that it
  connects there.
* *"The guide camera is busy"* — another capture with the guide camera (e.g. a preview through the API) is running;
  connect the guider when it has finished.
* *"No suitable guide star"* — increase exposure/gain, check focus, lower `MinSnr`.
* *Calibration failed: RA did not move* — check the guide output (mount driver pulse guiding / ST4 cable),
  guide rate and that the mount tracks.
* Arcsec values look wrong — set `FocalLengthMm`.
* The guide frame is white/says *saturated* — the sky is still too bright or exposure/gain are too high (a 12-bit
  camera like the ASI120 saturates at 65520 ADU); reduce exposure or gain, or wait for darkness.
* Back to PHD2: select PHD2 in the guider chooser; nothing else changes.
