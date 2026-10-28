# Changelog
All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## Unreleased
### Added
- Internal guider: the Dec backlash model `PlayModel` (setting "Dec backlash model", with Dec backlash compensation on and the Predictive Dec algorithm; default `Fixed`, PHD2's pulse at every reversal as before). It tracks where the Dec gear is inside its play over every pulse, holds back corrections that would only cross the play while the Dec error is seeing (a CUSUM test against the frame-to-frame noise), and lengthens a needed correction by the play that is left on that side. It learns the size and shape of the play from the calibration's Dec return and from every take-up, also when the play changes over the night, and keeps it per profile and mount (`backlash-play-model.json`). Made for mounts whose Dec backlash varies or is too large for a fixed pulse: on the OnStepX of the 2026-10-12 guide log, 900 ms pulses at about 200 reversals an hour never moved the star. In the closed-loop simulations it cut the Dec RMS by 10–32 % on mounts with backlash (12 % with the Dec wander of that log) and the reversals by 70–98 %, unchanged without backlash (NINA.GuideEngine/docs/notes/PLAY-MODEL-BACKLASH.md). The status shows it as `DecBacklash`; the guide log gets an `INFO: Backlash play model: …` line per take-up. A calibration now records the Dec take-up after the North leg (`CalibrationData.DecTakeUp`).
- Internal guider: a guide pulse that an INDI mount driver rejects (an Alert on `TELESCOPE_TIMED_GUIDE_NS`/`_WE`) counts as not sent: the corrections and the Dec backlash play model don't take it for a move, and the guider raises the new warning `PulseRejected` (309) at most every 5 minutes with the count. Unlike a failed output it never stops guiding. A driver that reports the end of a pulse well before its length (less than half of it and at least 200 ms short) is noted in the NINA log; such a pulse still counts as sent, since some drivers end it as soon as the mount, which times it, has accepted it. Seen in PHD2 guide logs of 2026-09/10 on an OnStep mount over INDI: compensated Dec reversals ended at once and did not move the star, and PHD2's adaptive compensation grew from 20 ms to 3 s (NINA.GuideEngine/docs/notes/PLAY-MODEL-BACKLASH.md §6).
- Guiding Coach: suggests the Dec backlash model `PlayModel` (with compensation on and the measured backlash as its starting pulse) when the Dec backlash is soft (after a long move at least 1.5 × what a guiding reversal loses) or 1 s and more, with the Predictive Dec algorithm in Dec guide mode Auto; also above 3 s, where no fixed pulse was suggested. With another Dec algorithm it suggests the fixed pulse as before and says that the play model needs Predictive. One-sided Dec guiding (Drift) stays first when the Dec drifts steadily. New setting name `BacklashModel` in coach changes; new finding parameters `playModel`, `playModelNeedsPredictive` (both true when set) and `playReason` (`soft`, `large`) on `response.decBacklash`.
- MCP server for AI clients such as Claude Code at `/mcp` on the pins web host (port 4782): 32 tools to read the equipment and sequence state, connect devices, slew, park, capture with star analysis, cool, focus and autofocus, change filters, guide, set switches, and build (from templates or exposure plans), load, save, start, stop and reset advanced sequences. Long operations run as background jobs; while a sequence runs, tools that could disturb it are refused unless forced; `stop_all` stops everything. Configured with `PINS_MCP_ENABLED`, `PINS_MCP_TOKEN`, `PINS_MCP_READONLY` and `PINS_MCP_ALLOWED_ORIGINS`; without a token the endpoint is read-only. New projects `NINA.Mcp` (protocol, jobs, tools; no dependencies), `NINA.Mcp.Adapter` (NINA bindings) and `NINA.Mcp.Test`; see `NINA.Mcp/ARCHITECTURE.md`
- Internal guider simulator: the scenarios `UMi20S`, `UMi20SHeavy` and `UMi17R`, ProxiSky UMi mounts as measured on their guide logs (NINA.GuideEngine/docs/notes/UMI-STRAIN-WAVE.md): the strain-wave lines at 430.8 and 426.6 s, the 300-per-turn stage at 287.2 s with its harmonics, non-integer lines, the 17R's 856.5 s stage, guide rate 1×, seeing correlated over a few seconds, the IMX662/IMX462 guide cameras at the logged image scales and SNR. Not modelled: Dec pulses that act less than calibrated and partly a frame late.
- Internal guider simulator: the scenario `HarmonicDrive`, a strain-wave mount of the AM5 class (±18″ at 430.8 s with harmonics, a curve that changes over a few cycles, short components at 2.9 s and 62 s, guide rate 0.9×). For tests, two more strain-wave variants (`HarmonicDriveModern`, `HarmonicDriveRough`), periodic-error terms whose amplitude and phase change along the gear, and slow seeing layers that correlate successive frames. Faint sky backgrounds (exposures under about 1 s) render about three times faster; their noise is drawn differently, so such runs don't repeat earlier ones exactly.
- OnStepX, Proxisky UMi: the vendor settings of the manufacturer's Windows tool through the native driver: status LED, power-loss memory, ASIAIR homing, supervised home, Dec second home, auto tracking at power-up, RA travel limits (on/off, left/right), Dec travel limits (on/off, CCW/CW), the anti-collision system (on/off, sensitivity, collision counters and clearing them), supervised goto (on/off, tolerance) and the servo PID gains of both axes. Only what the mount answers for is shown; as in the vendor's tool, the Dec limits and Dec second home can be changed on a UMi17X only and the PID gains on a UMi20S and UMi17X (other models show them read-only). The status LED, auto tracking and PID gains take effect after the mount is power cycled and show as pending until then; PID gains are only written while the mount stands still and does not track, since writing them restarts the axis. The settings are read from the mount only when asked for. The Touch-N-Stars plugin offers them under `/api/onstepx/proxisky`.
- OnStepX: the new profile setting `TelescopeSettings.SerialBaudRate` (9600 by default, OnStepX's own default) for controllers whose firmware was built with another serial baud rate (19200 to 460800). It applies at the next connect; through the API `profile/change-value?settingpath=TelescopeSettings-SerialBaudRate`.
- Guide camera: an Alpaca camera by static IP (Alpaca Direct). The guide camera slot has its own "Alpaca Camera - Static IP" entry with its own address, so two Alpaca cameras without discovery (e.g. two ZWO Air cameras) can be imaging and guide camera. The Touch-N-Stars plugin sets its address under `/api/alpaca-direct/guidecamera/settings`. An Alpaca camera now counts as the same camera in both slots by its address and device number rather than its Id, so one camera can't be entered in both slots, discovered in one and by static IP in the other.
### Changed
- Updated NINA to 3.3.0.1069-nightly
- Focusers with native drivers (ZWO EAF, ToupTek AAF, Astroasis Oasis, Nitecrawler): a move ends as soon as the focuser reports that it has stopped at the target. Before, every move waited for the next device poll afterwards, 2–4 s with the default polling interval of 2 s, which made autofocus runs about 20–40 s longer. The focuser's state is now read right after the move instead. Mechanical settling after a move is left to the setting "Focuser settle time"; set it if the stars of the first autofocus frames after long moves look smeared. INDI, ASCOM, Alpaca and plugin focusers wait as before, since some INDI drivers report a move as done while the motor still runs
- ninaAPI image endpoints (prepared image, image history, camera, guide camera, live stack, screenshot, thumbnails): an image served without resizing is no longer copied twice before encoding, and a resized one once less; the encoded bytes go to the response without another copy. For the prepared image without `resize` that is two full-frame copies less per request; the images sent are unchanged
- Internal guider, Dec backlash play model: a take-up window opened at a reversal where the model expects no play counts a tenth of a window. On a mount without play the seeing of those windows kept the learned play around the gate's 60 ms threshold, and the gate held back about every other Dec pulse: the Dec RMS was 7 % worse than without compensation in the closed-loop simulation, now 3 % (Hysteresis 10 % → 2 %). A play that appears is still learned; every case with a play is unchanged (NINA.GuideEngine/docs/notes/PLAY-MODEL-BACKLASH.md §9).
- Internal guider, Dec backlash play model: its reversal gate no longer counts most of the Dec's wander as noise. A star that wanders like a random walk is really moving and has to be followed; the gate held it back until it had gone far. In the closed-loop simulations of a Sky Simulator session (0.12 px of random walk a frame) the Dec RMS fell by 9–12 % and with the 2026-10-12 log's wander by 7–11 %, other cases within ±0.005 px; there are more reversals where the wander is large (NINA.GuideEngine/docs/notes/PLAY-MODEL-BACKLASH.md §7).
- Internal guider, Dec backlash play model: switching the Dec backlash compensation back on keeps the gear's position the model tracked while it was off; only a change from the fixed compensation starts it over. Before, the first pulse after switching it on got half the play and no gate (on the OnStepX of 2026-10-18 a 0.15 px offset sent 947 ms and put the star 4″ the other way).
- Internal guider, Dec backlash play model: the evidence of its reversal gate is capped at the threshold, and changes from or to an offset of more than 12 σ (a dither, a jump) no longer enter its noise estimate. After a Dec dither the evidence used to stay above the threshold for about 100 frames (55 σ on the OnStepX of 2026-10-18) and the star's way back counted as wander, which brought the noise down to its minimum: reversals for offsets of 0.04–0.3 px went through, each with the whole play as compensation (16 of 2500 ms in 6 minutes after the meridian flip). Unchanged in the closed-loop simulation of a mount without backlash (NINA.GuideEngine/docs/notes/PLAY-MODEL-BACKLASH.md §8).
- Internal guider, Dec backlash play model: once the play was measured (calibration or the probe after a slew), the learned size may rise only to about 1.2 × the measured one (the nearest size of its grid, 1.09–1.30 ×); smaller sizes stay free. On the OnStepX of 2026-10-18 runs of pulses one way pushed it from about 3 s to 8 s, and above 4.5 s half of the reversals overshot (12 % below) and the Dec RMS doubled. A restored model keeps the measured size instead of rebuilding its prior around the "Dec backlash pulse" setting (NINA.GuideEngine/docs/notes/PLAY-MODEL-BACKLASH.md §8).
- Internal guider, Dec backlash play model: a frame after a correction of more than 4 σ no longer enters the noise estimate of its reversal gate. Its change measures how well the size of the correction is known, not the seeing; on logs whose compensation pulses had not moved the star the estimate settled at about 5–7 × the real noise and the gate let almost nothing through. Unchanged in the closed-loop simulations of a working mount.
- Internal guider: the RA and Dec algorithms default to Predictive (were Hysteresis and ResistSwitch). This applies to profiles whose internal guider algorithm was never set. As before, an algorithm set in the profile's PHD2 settings carries over, but not PHD2's own defaults (Hysteresis, Resist Switch), which every profile has unless they were changed.
- Internal guider: the Dec backlash compensation keeps the pulse it adjusted to while guiding, per profile and mount, across settings changes, reconnects and restarts, as long as the "Dec backlash pulse" setting is the one it started from (a new setting starts over from itself). Before, every settings change and every connect went back to the setting. The adjustment stays within 1.5 × the setting. The pulse in use is in the guider status (`DecBacklashPulseMs`) and the guide log notes each change.
- Internal guider: a secondary star that isn't measured says why (`RejectReason`): `NotGuiding`, `Settling`, `Stabilizing`, `BeyondMaxStars`, `GuideStarLost`, `Subframe` or `MultiStarOff` instead of `NotMeasured` for all of them. `NotMeasured` is left for the rare frame with none of these reasons; incident recordings with the old value still read as before.
- Internal guider, Predictive algorithm: its memories last a time instead of a number of frames, so guide exposures shorter than about 2 s behave as at 2 s, where it was tuned. The choice of model, the seeing estimate and the learning phase cover 240 s, the usual noise level of a frame 12 minutes, each at least as many frames as before (120, 120 and 360). At 0.5 s they used to cover well under half of that (the model memory under 2 minutes). Frame intervals of 2 s and longer guide exactly as before.
### Fixed
- Serial devices (Alnitak and Artesky flat panels, Pegasus FlatMaster): the port lists offer the /dev/serial/by-id names, which stay the same when a device is plugged into another USB socket or the ttyUSB numbers change at boot; AUTO uses them too. AUTO also honours the serial-number part of the device queries again, so an Alnitak panel is no longer confused with any other FTDI adapter (FTDI 0403:6001) on the same Pi, and a FlatMaster with another FTDI 0403:6015 device. Ports saved as /dev/ttyUSBn keep working
- DSLR and mirrorless cameras (libgphoto2): two cameras of the same model are both listed (before, none was), the ISO is no longer read from the camera during an exposure, and cancelling a bulb exposure no longer blocks the caller while the camera finishes it
- Wanderer rotator: a single communication error (e.g. a status read timing out under USB load) no longer disconnects it or aborts a move; it is only treated as lost after 3 errors within 30 s
- Nitecrawler focuser: a failed read of its configuration while connecting no longer sets its max step to 0
- Wanderer cover: opening and closing report the outcome correctly. Before, they waited for the opposite end: a cover that opened was reported as failed, and one whose motor had not started yet as already open or closed. A cover that stays in motion no longer hangs the open or close (and the next one) forever; it fails after 60 s, as does a cover that stops halfway
- Serial devices on AUTO port (Alnitak and Artesky flat panels, Pegasus FlatMaster): the port is found by its USB vendor and product ID again, and an Arduino Leonardo based device gets DTR. On Linux the lookup never found the IDs, so AUTO never matched a port
- Shutdown (systemctl stop/restart, an update): the INDI equipment is disconnected while the INDI server still runs, and pins waits up to 10 s for the drivers to confirm, so they close their hardware (cooler, ports). Before, the server and its drivers were stopped first and the drivers never got the disconnect. A park started through MCP (park_mount, also within secure_observatory) is no longer stopped halfway by the shutdown; it gets up to 60 s to finish, so the dome and cover are closed afterwards too. systemd now sends the stop signal to pins alone (`KillMode=mixed` in `pins.service`, stop timeout 150 s); before, it stopped the INDI server and its drivers at the same moment as pins, so they were gone before the disconnect too. pins ends them itself once the equipment is disconnected. An INDI server that ends during the shutdown no longer counts as a crash: there is no "Lost the connection to the INDI server" message, and pins doesn't start the server and its drivers again
- INDI: when indiserver stops (a crash, killed, out of memory) or the connection to it drops, the INDI devices are reported disconnected right away, each with its "connection lost" message, and pins reconnects to the server or restarts it together with the drivers that ran under it (a driver process that outlives its server is ended first). Before, every INDI device kept reporting itself connected with frozen values, commands went nowhere, and nothing but a restart of pins helped; a filter change, focuser or rotator move, slew, dome motion or exposure in progress waited for its timeout (up to 5 minutes), and some then counted as done. They now fail instead; most at once, a park or goto that has not started moving yet after its motion-start wait of up to 10 s. After the recovery the equipment is connected again as usual (connect all); pins does not reconnect it by itself, and a device only counts as connected once it has been connected again. After 3 recoveries within 10 minutes pins stops reconnecting and asks for a restart. A device that its INDI driver reports disconnected also shows its "connection lost" message now
- ninaAPI thumbnails: each session keeps its image history thumbnails in a `thumbnails-<process id>` folder next to the plugin, deleted only when pins shuts down cleanly. After a crash, a service restart or a power cut the folder stayed, one more for every such session. The plugin now deletes the folders of sessions whose process no longer runs when it starts, and the folder of an earlier session that had the same process id (common after a power cut, when the service starts the same way again)
- No more internet requests at startup: pins fetched the N.I.N.A. plugin catalog from nighttime-imaging.eu on every start (a background request with a 100 s timeout that only logged errors in the field), and "update all" could then have replaced the Touch-N-Stars or ninaAPI plugin of the pins packages with an upstream build. The catalog fetch and N.I.N.A.'s Windows update check now run on Windows only; pins gets its plugins and updates from its packages
- Crashes are logged: an exception that ends pins (for example from a folder watcher or a command) is written to the log with its type and stack trace before the process exits, as in N.I.N.A., and a failed background task nobody waited for is logged too. Before, the log just stopped and systemd restarted pins without a trace of the cause
- Prompts (message boxes) that came up before Touch-N-Stars had connected once since the start, e.g. after a restart following a power cut, failed with an error instead of being shown. They now reach Touch-N-Stars like any other prompt and take their default answer after 5 minutes without one
- Image compatibility layer (`BitmapSource.Create` and `CopyPixels` with a managed array): the stride is now the row pitch in bytes, as in WPF, instead of being ignored, so padded rows come out right; every caller in pins passes packed rows and gets the same images as before. Bad input throws instead of reading or writing past a buffer: an array too short for the image (before: part of the image left uninitialised, or an out-of-bounds read for arrays other than byte/ushort), a 16-bit array with an 8-bit image (before: twice the image's buffer read or written), a stride smaller than a row, a negative offset or a null array
- INDI cameras: a CCD1 update without image data during an exposure would have finished it with the previous frame, which the INDI client kept for the blob, and with a `.fits.z` format the download then failed. Such an update now empties the blob and is ignored. Stock libindi sends CCD1 only with an image, so this needed a driver that sends a state-only update
- ninaAPI `/prepared-image` with `debayer=true`, or `autoPrepare=true` on a colour frame, debayered an image that NINA had already debayered (profile setting "Debayer image" on) and served it garbled, without an error. It now serves NINA's debayered image. Touch-N-Stars does not send these parameters to this endpoint. An explicit `unlinked=true` on such an image served it white when the profile's unlinked stretch was off, since NINA then keeps no colour channels; it is now stretched linked
- PHD2 and internal guider: the PHD2 settings stored in the profile hold only what a client set (Touch-N-Stars records each one it changes in PHD2). Before, every profile got concrete values for all of them (200 mm focal length, gain 95, 1 s exposure, saturation 255 ADU, Hysteresis/Resist Switch …), so every PHD2 connect wrote them over PHD2's own configuration, and the internal guider took the 200 mm as its guide focal length without the "guide focal length is not set" warning. Existing profiles drop values that still equal those old defaults once, when they are loaded; values that were changed stay and are restored on connect as before. An internal guider whose own settings were never set took those values over and now uses its own defaults once (exposure 2 s instead of 1 s, max star HFD 20 instead of 10, automatic min move instead of 0.2 px, no guide focal length instead of 200 mm), so it calibrates again on the first start
- Player One cameras: a negative value sent to the camera, such as a cooling target of −10 °C, reached the SDK as 4294967286 on Linux, where its C `long` has 8 bytes and only the lower 4 were written. Settings are now written as a full `long`
- INDI filter wheels: a filter change waited without a limit for the driver to report the move done and kept a CPU core busy while waiting. A driver or `indiserver` that stopped answering hung the filter change, and with it the sequence, until a restart; now the wait ends after 1 minute and the filter change fails if the wheel is not at the filter
- INDI cameras: the offset of a sequence item or image type was ignored by drivers that offer the offset only in `CCD_CONTROLS` (toupbase, older `indi_asi_ccd`); the camera kept its previous offset. It now goes to the same element the offset setting uses
- Numbers no longer follow the system locale: on a Pi with a German locale ASTAP got its search radius as `-r 2,5`, so solves with coordinates fell back to blind solves, and a DSLR temperature of 12.5 read as 125. As in N.I.N.A., the process formats and parses numbers culture-invariant; the UI language still comes from the profile
- PHD2: starting PHD2 from pins on Linux reported "PHD2 start process error" although PHD2 had started, so the first connect of a night failed and the second one worked
- INDI mounts: a park that fails is reported as failed ("Mount failed to park", and a Park Scope step fails) instead of "Mount has parked". Before, a driver Alert was only shown as a driver error and the park went on as done, as did a mount still moving after 5 minutes or one that stopped without reporting itself parked. A park now needs the driver to report `TELESCOPE_PARK` PARK=On after the command; a mount already at its park position still parks as before
- FITS through CFITSIO ("legacy FITS writer" off, compressed FITS, reading `.fz` files): creating a file looked for the Windows library `cfitsionative.dll` and failed on Linux, and the image size went to CFITSIO as 32-bit integers where it expects 64-bit `long`, so a written file got NAXIS1 = width + height × 2³² and NAXIS2 = 0. Both now go through `libcfitsio.so` with `long` sizes. FITS files from the default legacy writer were not affected
- FITS through CFITSIO, when something fails: loading a compressed FITS, which every compressed INDI frame goes through, could free its memory twice when closing or reading the decompressed copy failed (e.g. a full `/tmp`), and glibc then aborts pins; a failed decompression also left its temporary file in `/tmp` and a file handle open. Writing with the "legacy FITS writer" off left a file handle open when creating or writing the image failed (e.g. a full disk), one more per frame, and the incomplete file stayed behind like a saved frame. The memory is now freed once, the handles are closed, and the temporary and incomplete files are deleted
- Packages: the third-party licenses (`3rd-party-licenses.txt`) and the internal guider's PHD2 notice (`THIRD_PARTY_NOTICES.md`) are installed next to `LICENSE.txt`; the headless project had dropped the copy rule NINA has for the list, and the notice was never copied. The list now names OpenCV and OpenCvSharp4 under Apache 2.0 (they were listed as BSD and MIT) and lost a leftover merge marker. The core package no longer contains the `.git` folder of the External native bundle with its Git LFS hooks
- Player One cameras: Touch-N-Stars showed no dew heater strength slider although the camera's heater power can be set from 0 to 100 %. The native generic camera driver had a target strength but no `MaxDewHeaterStrength`, which ninaAPI's camera settings provide as the slider's scale; ToupTek-alike and INDI cameras already had it. The maximum is now 100 % for cameras with a dew heater, and the target strength is limited to 1–100 % (previously any value was stored, and 0 switched the heater off while it was set to on)
- Internal guider, Dec backlash play model: its reversal gate also holds pulses while the gear's position is unknown (after a stop, a Dec guide mode change or a Dec pulse not sent). Before, the first Dec pulse after those got half the play without the gate, as switching the compensation on did before.
- Internal guider, Dec backlash play model: with the Dec backlash compensation off, the Predictive Dec algorithm is told the pulse as sent, as with the fixed compensation off, instead of the motion the model expects. The model still tracks the gear.
- Internal guider: a pulse the mount rejects counts as not sent for its axis only. Before, a rejected RA pulse kept the Dec pulse of the frame from being sent, and the Dec backlash play model forgot where the gear is.
- Internal guider: when the Dec play measurement after a slew finds North pulses moving the star South, guiding no longer starts with that calibration. After a meridian flip Dec is inverted (the Dec-flip setting saved) and measured again; otherwise the mount is calibrated again. On the OnStepX of 2026-10-19 the measurement reported it and Dec then ran away by 270 px (NINA.GuideEngine/docs/notes/PLAY-MODEL-BACKLASH.md §10).
- Internal guider: a runaway on the pulse duty alone no longer inverts Dec after a meridian flip; only a Dec error that keeps growing does. On 2026-10-18 Dec corrections swinging between North and South at ±0.3 px tripped the duty ten minutes after a correctly handled flip, Dec was inverted and the wrong "Reverse Dec after meridian flip" setting saved, which broke the next night's flip.
- Internal guider, Dec backlash play model: a calibration or a Dec play measurement now measures plays wider than 4 s (up to 8 s). Before, every play of 4–8 s came out as 4 s and the model could not learn more than 4.8 s; on the OnStepX's pier East (2026-10-19) a return through 8.4 s of play started the model at 3.7 s and 2.5 s compensations overshot by up to 3 px. Stored play models are kept (NINA.GuideEngine/docs/notes/PLAY-MODEL-BACKLASH.md §11).
- Internal guider, Dec backlash play model: when the Dec play cannot be measured after a meridian flip, what was learned on the other pier side is forgotten (back to the calibration's play). On the OnStepX of 2026-10-19 the model kept the "no play" of pier West after the flip to East, where a replay of the same frames learns 4.6 s, and the Dec RMS rose to 1.1″.
- Internal guider: a completed calibration starts the Dec guide algorithm over, and a runaway the algorithm of its axis. Predictive Dec kept what it learned from a runaway (seeing 2 px instead of 0.1 px, gain 0.97) after the recalibration and corrected the seeing on every frame.
- OnStepX: a park aborted on the way (a limit, a stop from another client) failed only after 10 minutes, since OnStepX then reports the mount as unparked rather than as park failed. Park now fails as soon as the mount stands still unparked, or when parking has not started 10 s after an unanswered park command.
- OnStepX: a goto stopped on the way (a limit, a stop from another client, a second goto) was reported as arrived. A goto that ends more than 1° from its target now fails, so NINA no longer carries on as if the mount had arrived.
- DSLR and mirrorless cameras (native RAW, e.g. Nikon NEF over libgphoto2): the flat wizard's auto brightness overexposed the flats, and the preview turned black once the frame saturated. RAW pixels are not scaled, yet their range was taken from the profile's camera bit depth (16 by default), so a 14-bit NEF read four times darker than it is (a 12-bit NEF sixteen times) and the wizard drove the panel into saturation, where the stretch maps a flat, clipped frame to black. The bit depth of a RAW image now also follows the white level LibRaw reads from the file and is lowered to the range the data actually reaches (the pixel values and saved files stay unchanged). Auto brightness flats, auto exposure flats and sky flats now measure their histogram target against the bit depth of the converted image instead of the profile's, which also fixes flats that came out four times too dark with bit scaling on and a bit depth below 16. Histogram targets that were lowered to work around this (e.g. 3%) need to go back to normal values.
- Internal guider: slews and centering (Slew to guider calibration position, Slew to RA/Dec and Alt/Az, Center, Center and Rotate, Solve and Rotate) started guiding after the move although none was running before. Stopping an idle internal guider answered "stopped", which these instructions take as "guiding was on, restart it"; like PHD2, it now answers that nothing was running. So the slew to the calibration position no longer guides, or even calibrates, ahead of the calibration that follows it.

## 1.1.64 - 2026-10-10
### Added
- Internal guider: choose the guide star yourself. While the guider is looping (not guiding), `IAdvancedGuider.SelectGuideStar(x, y)` makes the star nearest the position the guide star on the next frame, like clicking a star in PHD2; in multi-star mode its secondary stars are found around it (PHD2 guides a clicked star alone). It answers with the star and the number of secondary stars, or why it was refused (no star there, too close to the edge, busy guiding, calibrating or running the Guiding Coach, not looping, cancelled, no frame in time). The Touch-N-Stars plugin offers it as `POST /api/internal-guider/select-star`, for choosing the guide star by tapping it in the guide frame
- OnStepX mounts without INDI: a native mount driver for OnStepX controllers over USB serial, listed in the mount chooser as "OnStepX (serial)", or after the model the mount reports (e.g. "Proxisky UMi17S (OnStepX)") once its port is set: the scan asks the mount briefly, unless pins has the port open or another program holds it exclusively (as INDI does). Set the port in the new profile setting `TelescopeSettings.SerialPort` (e.g. /dev/ttyUSB0; through the API `profile/change-value?settingpath=TelescopeSettings-SerialPort`). It uses the commands and replies of INDI's OnStep driver: goto, sync, abort, park/unpark/set park, find home, tracking (sidereal, lunar, solar, King; a rate chosen on the mount shows too, and rate compensation switched off by lunar, solar or King comes back with sidereal), manual moves at OnStep's ten move rates, pulse guiding, site, elevation and time (synced to the mount when TimeSync is on) and the meridian flip as a goto. Proxisky UMi mounts (UMi17R, UMi17S, UMi20S) are recognized by their vendor commands and named after their model. The Touch-N-Stars plugin's manual moves (`/ws/mount-control`) and slew rate choice (`/api/indi/mount/slew-rates`, `/api/indi/mount/slew-rate`) drive it too, with OnStep's rates 0.25x to Max. Guide pulses go out at once, also while the driver is reading the mount, and a pulse counts as done only when the controller no longer reports one running (`G` in `:GU#`), so the internal guider never starts an exposure while the mount still moves; a pulse the controller would refuse (parked, a goto running or just started, which a pulse would abort, a motor fault, or a limit at move rates of 1x and slower) fails instead of being lost. Choosing a move rate of 1x or slower also makes it OnStepX's guide rate; pins then says so, since a guide calibration made at the old rate no longer fits. A command the controller refuses (park, unpark, find home, set park, tracking, goto targets, site and time) reports its reason, read from the controller with `:GE#` (e.g. "no park position set", "controller in standby"); park, unpark and find home then fail in NINA instead of being reported as done. A goto the mount refuses, or one that does not end, fails as well instead of NINA carrying on as if the mount had arrived, and cancelling a goto, park or home stops the mount. The mount shows as slewing during a goto, a manual move (also a slow one, which OnStepX runs as a guide pulse), homing (also with home sensors, which OnStepX runs without a goto; find home then also waits for the final move by the sensor offset, which the mount does not report) and while a manual move still brakes after its release, detected from the moving coordinates as with INDI. Park and find home first stop a running goto, manual move, braking, homing or guide pulse, for which the controller would refuse them. At connect pins warns when the mount's date and time are not set, since OnStepX then refuses every goto. The new profile setting `TelescopeSettings.PreferredPierSide` (East, West or Best; empty leaves the mount's own) is set on the mount at every connect and when it changes: with OnStepX's usual "Best" a meridian flip goto stays on the same side until the meridian limit, so a flip that ends on the wrong side now counts as failed and is retried, with a hint to set the preferred pier side. Custom tracking rates (NINA's sidereal shift for comets and asteroids, Touch-N-Stars' `PUT /equipment/mount/trackingrate`) are set as OnStepX's tracking rate offsets (`:SXTR`, `:SXTD`), up to 120 seconds of RA and 1800" of Dec per second; homing clears them. The driver never toggles DTR or RTS, since clearing DTR before RTS restarts an ESP32 controller.
- Internal guider: the option "Simultaneous RA and Dec pulses" (off by default) sends a frame's RA and Dec pulses at the same time, which shortens the guide cycle. It takes effect with mounts that time both axes separately, so far pins' native OnStepX driver; other mounts keep pulsing one after the other.
- Internal guider: the guide loop measures its own timing, and the status (`/api/internal-guider/status` in the Touch-N-Stars plugin, `AdvancedGuiderStatus.Timing`) reports it as frames per second and where a cycle's time goes: exposure, the capture beyond it (start, readout, transfer), processing, frame to first pulse, pulses until done and the rest, as medians of the last 20 cycles and for the last cycle, while the guide loop runs.
### Fixed
- Internal guider, multi-star: after the guide star changed to a neighbouring star (clouds, a reacquisition, or a star kept from before a failure or a slew), every secondary star stayed lost, because a lost secondary is only searched at its old offset from the guide star, and guiding silently went on with the guide star alone (on a rig night 1 of 12 stars was used). The secondary stars are now found again around the guide star when guiding starts on a kept star whose secondaries are mostly gone, and after 30 guiding frames in a row with all of them lost (alert 109 *Secondary stars found again*). The old list is kept when fewer than half as many stars are found, e.g. under clouds
- Touch-N-Stars manual moves ignored the profile's "primary reversed" and "secondary reversed" settings: the plugin's `/ws/mount-control` drives the mount directly, past NINA's TelescopeVM, which applies them. It now applies them itself, for INDI mounts and the native OnStepX driver.
- QHY cameras: two QHY cameras can now be connected at the same time, e.g. one as imaging camera and one as the native guider's guide camera. The QHY driver held a single camera handle for the whole process, so a second QHY camera was refused, and the filter wheel scan or a second connection ended up talking to the first camera. Each QHY camera now has its own handle (a camera and the filter wheel plugged into it still share one), nothing done on one camera, including disconnecting it mid-readout, waits for a readout on the other, and a camera is opened by its ID instead of its position in the last scan.
- QHY cameras and filter wheels: a connect that failed after the camera was opened left its handle open and the SDK initialized for the rest of the session, so the next connect reused the half-initialized handle (also after unplugging the camera). The handle is now closed again.
- QHY filter wheels: connecting the filter wheel of a camera that is already connected no longer re-initializes the camera under it (which reset its stream mode and resolution), and one QHY camera that can't be opened no longer hides the filter wheels of the cameras after it.
### Changed
- Updated NINA to 3.3.0.1066-nightly

## 1.1.63 - 2026-10-09
### Added
- Internal guider: the guiding engine of André Duffeck's pins-guider, a C# port of PHD2 (star finding, multi-star tracking, calibration, guide algorithms, backlash compensation, settling, dithering, guide log) with its own extensions (Predictive algorithm, periodic error, Dec guide mode Drift, pulse model, safety monitors, Guiding Coach, incident recorder and a closed-loop simulator), as the new project `NINA.GuideEngine` with its tests in `NINA.GuideEngine.Test`. It is not yet selectable as a guider; the connection to the guide camera, the mount and the guider list follows.
- Internal Guider: a guider built into pins, listed in the guider chooser next to PHD2. It runs the internal guiding engine on the camera in the guide camera slot: connecting it connects the guide camera if needed, and while it is connected it has the guide camera to itself. Corrections go to the mount (pulse guiding) or an INDI guide camera's ST4 port; the built-in simulator (setting `GuideSource`) guides without hardware. It is André Duffeck's pins-guider plugin moved into pins (up to his 2026-10-08 changes): its settings carry over, and its files (calibrations, darks, learned periodic error and pulse model, incidents, Guiding Coach reports, guide logs) move from `NativeGuider/` to `~/.local/share/NINA/InternalGuider/`. The plugin's own guide camera choice (`GuideCameraDriver`, `GuideCameraDevice`) is replaced by the guide camera slot. Guide logs are `InternalGuider_GuideLog_<night>.txt`.
- Internal Guider API in the Touch-N-Stars plugin: `/api/internal-guider/*` (status, guide frames as JPEG with star overlays, guide steps, alerts, calibration, settings also before connecting, loop/guide/pause/dither, dark library, Guiding Coach, incidents) and the live feed `/ws/internal-guider`; see the plugin's `INTERNAL_GUIDER_API.md`.
- OnStepX: a native serial connection to OnStepX mount controllers without INDI (`NINA.Equipment/SDK/TelescopeSDKs/OnStepXSDK`), with the commands and replies of INDI's OnStep driver. It connects, identifies the firmware and reads status, position, pier side and guide rate; Proxisky UMi mounts (UMi17R, UMi17S, UMi20S) are recognized by their vendor commands. It only reads so far and is not yet selectable as a mount; the mount driver, motion and guiding follow. It never toggles DTR or RTS, since clearing DTR before RTS restarts an ESP32 controller.
- Internal guider: `IAdvancedGuider`, `IGuidingCoach` and `IGuideIncidentRecorder` in `NINA.Equipment`, the interfaces through which the Touch-N-Stars plugin will show the internal guider's live frames, guide steps, calibration, statistics, alerts and settings, and run its Guiding Coach and incident recorder. From André Duffeck's pins-guider branch, without its contract version and its guide camera list, which the guide camera slot replaces.
### Changed
- INDI cameras: a frame is decoded in memory instead of being written to a temporary file, read back and deleted, which saves an SD card write per frame on a Raspberry Pi.
- Guide camera: the messages logged for every frame (starting the exposure, the INDI exposure and its image) are written at Debug level instead of Info, so a night of guiding no longer fills the log with them. The imaging camera keeps logging them at Info.
### Fixed
- Internal guider with an INDI mount: after every guide pulse the guider waited up to about 125 ms too long before the next pulse or exposure, because it read the mount's pulse-guiding state through a 100 ms cache and checked it every 25 ms. It now reads it directly (it is pins' own record of the pulse, no mount traffic) and checks every 5 ms, so the next frame starts right after the driver reports the end of the pulse. ASCOM and Alpaca mounts are unchanged.
- Guide camera API: two capture requests arriving together could both find the guide camera free; the second then failed with an error instead of "Guide camera is busy". The capture now takes the camera in one step (`IGuideCameraMediator.TryRegisterCaptureBlock`). While a guider holds the guide camera, `/equipment/guidecamera/abort-exposure`, `/set-binning`, the `/set-readout` routes and `/set-setting` for `BinX`, `BinY` and `ReadoutMode` answer 409 instead of aborting or changing its guide frame.

## 1.1.62 - 2026-10-08
### Added
- Guide camera: a second camera slot next to the imaging camera, for guiders that need one. It offers the same cameras (native SDK, INDI, Alpaca/ASCOM, simulator) except DSLRs, Alpaca Direct and cameras from third-party plugins, and keeps its own settings in the profile (`GuideCameraSettings`: device, gain, offset, binning, INDI driver and so on), so it never changes the imaging camera's. An INDI guide camera can use a different driver or the same one as the imaging camera. The API has it under `/equipment/guidecamera/...` like the imaging camera (connect, info, readout, binning, cooling, dew heater, USB limit, settings), with a `capture` that returns a guide frame without saving it or touching the imaging camera's last capture; `/equipment/info` and the profile include it, and websocket clients get `GUIDECAMERA-CONNECTED`/`-DISCONNECTED`/`-DOWNLOAD-TIMEOUT`. Plugins get it as `IGuideCameraMediator`. The Touch-N-Stars UI doesn't show it yet. A camera connected in one slot can't be connected in the other, also when it's listed under another brand (a ToupTek camera appears as ToupTek, Omegon and RisingCam). Only one QHY camera can be connected at a time, since pins' QHY driver handles one camera.
### Fixed
- NOVAS: getting the apparent position of the Sun, Moon or a planet (`NOVAS.PlanetApparentCoordinates`) or of an observer on the Earth's surface (`NOVAS.GetPositionOnEarthSurface`, `NOVAS.NOVAS_geo_posvel`) crashed pins on Linux. Three calls into the NOVAS library passed a struct by value where the library expects a pointer to it, which only works on Windows. pins itself doesn't call them yet, but plugins can
- ZWO cameras: listing the cameras opens and closes every ZWO camera to read its alias, also one that is connected, so a rescan through the API while a ZWO camera was connected, or a scan of the guide camera list, could close it. A connected camera is now read without closing it.
- INDI mounts: after a North guide pulse every South pulse also moved the mount North, and after a West pulse every East pulse moved it West, so guiding pushed the mount further off instead of correcting. pins sent only the requested direction of the guide property and left the opposite one at its last value; drivers built on the standard INDI guider interface pulse North (or West) whenever that value is not zero and never clear it after a pulse. Each pulse now sends both directions of the axis, the opposite one as 0
- INDI: two device types sharing one driver could lose it when one of them was switched to another driver. With mount and focuser both on OnStep's `indi_lx200_OnStep`, choosing a different driver for whichever of the two had loaded it first stopped the driver under the other one as well; a guide camera a plugin runs on the imaging camera's driver was hit the same way. And a device type switched to a driver that was already running for another type left its previous driver running. pins now remembers every device type a driver serves and stops a driver only once none of them uses it
- INDI mounts: a guide pulse only counted as running once the driver's Busy reply reached pins, so a guider asking right after sending it, or while that reply was still on its way behind a camera image, was told the mount had stopped and could start its next exposure while the mount was still moving. Like PHD2, pins now counts a pulse as running from the moment it is sent until the driver reports its end. A driver that never reports the end releases the pulse 500 ms after it should have ended, and pins logs a warning (at most every 10 minutes) that pulses with this driver may overlap the next guide exposure
- INDI: everything pins received from INDI travelled on one connection, so while a camera image was being transferred and decoded, guide pulse completions, exposure states, mount positions and a guide camera's frames all waited behind it, so guiding could stall during main-camera downloads. Every camera now receives its images on a connection of its own, the way PHD2 keeps its mount connection free of images, and images are decoded outside the lock the other updates need. If the INDI server refuses such a connection, the camera falls back to receiving its images on the main connection as before
### Changed
- Updated NINA to 3.3.0.1065-nightly
- System.Windows.Compat: preparing an image for display, saving or the API (crop, rescale, pixel-format conversion, PNG/JPEG encoding, drawing one bitmap into another) no longer copies the full image at every step. These now read the source pixels in place, 16-bit pixel copies no longer go through a full-size temporary array, grayscale conversion releases its colour channels as soon as they are merged, and star blob detection no longer duplicates its input. An image fetched through the API at full scale used to make about five full-size copies, which on a Raspberry Pi meant noticeably higher memory use and garbage-collector load per frame
- System.Windows.Compat: added the surface NINA 3.3.0.1065 needs to build headless: `BitmapSource.Clone`, `CopyPixels` into a managed array at an element offset, `Colors.Lime`, `Dispatcher.FromThread` and the `Dispatcher.InvokeAsync` overloads returning a `DispatcherOperation`. `NINA.Sequencer` no longer compiles the linked-template preview behaviors and `NINA.Test` no longer compiles the upstream tests that need the WPF view layer, so the solution builds again

## 1.1.61 - 2026-09-28
### Changed
- The plugin sources under `NINA.Plugins/` and the Touch-N-Stars app are no longer git submodules of this repository; local checkouts in those folders, `NINA/External` and `pinsdaemon` are ignored by git

## 1.1.60 - 2026-09-24
### Fixed
- HocusFocus auto-focus runs cropped every frame to NINA's inner crop ratio even with HocusFocus's **Use AutoFocus Crop** off. pins always uses HocusFocus for star detection, star annotation and auto-focus, but never saved that choice in the profile; HocusFocus's star detection reads the profile to tell its own auto-focus from NINA's, found no choice there and treated its own runs as NINA's. pins now saves HocusFocus as the profile's choice for all three whenever it is loaded, and again after every profile switch

## 1.1.59 - 2026-09-21
### Added
- `rawCommandBatch` can now also batch the LX200 commands that answer with a single character and no `#` (on 10micron `:Guaf#`, `:Gdat#`, `:GREF#`). The caller appends a mask such as `:Guaf#:GT#|cs` saying how each reply ends; without a mask every reply is still read up to its `#` as before. The 10micron plugin's status poll uses this to read its nine values in one round trip instead of nine, which previously kept the mount busy for about two seconds of every polling interval, including during slews and model builds
### Changed
- Updated NINA to 3.3.0.1058-nightly

## 1.1.58 - 2026-09-18
### Fixed
- INDI mounts: a goto the driver refused was not recognized as refused when the refusal arrived within the same second as the last position update, which is the normal case for a driver that answers within milliseconds. It was taken for a stale Alert left over from earlier, so each such goto waited 15 s for a reply and for motion that never came and was then treated as done, instead of being reported as refused at once. An Alert now only counts as stale when the mount's coordinates were already in Alert before the goto was sent
- 10micron model builder: after a build started from an unparked mount, the builder slewed back to the start position even when that position was below the mount's horizon limit, e.g. right after unparking by hand. The mount refused the goto, and on a network (TCP) connection the INDI LX200 driver then lost track of the mount's replies: every later goto was misread as refused while the mount still carried some of them out, giving trailed frames in the next build, or the mount stopped moving altogether until it was reconnected. The builder now reads the mount's horizon limit (`:Go#`) and only returns to the start position when it lies at least 1° above it, otherwise it leaves the telescope where it is and shows a warning
- INDI devices on a network (TCP) connection could fail with `Error! Server address is missing or invalid.` although address and port were configured: pins checked only once, before the connection mode was applied, whether the driver had a `DEVICE_ADDRESS` property, and a driver still in serial mode was remembered as having none. Transport properties are now re-checked after the mode switch, for serial, network and HTTP alike
- Altair, Ogma, Omegon, MallinCam, Risingcam and SVBony cameras could not connect, failing with a `NullReferenceException` in `put_Option`: the SDK bindings passed the camera id to the native library as a UTF-16 string on Linux, so the SDK saw only its first character, found no camera and returned no handle, which went unnoticed until the first option was set. The id is now passed as a plain C string, as the ToupTek binding already did. A camera, filter wheel or focuser of these brands that cannot be opened now reports "Could not open ..." instead of crashing

### Added
- INDI mounts on a network (TCP) connection expose a `rawCommandBatch` action that sends several raw LX200 commands in one write, so a plugin reading many values at once pays one round trip instead of one per command

## 1.1.57 - 2026-09-15
### Fixed
- INDI mounts: an exposure could start while the mount was still slewing, giving trailed frames — during a 10micron model build about one point in twenty, each ending as a failed plate solve. When a goto reached the driver while it was busy with its regular status poll, that poll's ordinary position update (state Ok, new timestamp) arrived first and was taken as the driver's reply, so the slew wait saw a mount that had not moved yet and returned at once. A goto now only counts as acknowledged by the driver's actual reply: EQUATORIAL_EOD_COORD turning Busy, a new Alert, or TARGET_EOD_COORD echoing the requested coordinates followed by the next position update (how OnStep answers). Status updates that arrive before the reply are ignored and logged. When no reply can be identified within 5 s the goto is no longer reported as rejected — which used to send a second goto into the running slew — and the slew wait first waits up to 10 s for the mount to start moving
### Changed
- `NINA.Test` no longer compiles `SimpleDSOContainerViewTest`, which binds to the simple sequencer's XAML view (`DataGrid`) and cannot build against the headless shim, so the solution builds again

## 1.1.56 - 2026-09-07
### Fixed
- ToupTek-alike cameras (ToupTek, Altair, Omegon, SVBony, Ogma, ...) could stop delivering images during fast exposure series such as bias frames, or stay unresponsive after an aborted exposure, until they were reconnected. Frames that arrive while no exposure is waiting for them (duplicate image events, frames from aborted or timed-out exposures) are now discarded right before the next exposure is triggered instead of after every download, a failed download and a stopped exposure flush the SDK frame queue, and the software trigger mode is re-armed after an exposure was stopped. Live view keeps discarding stale frames after every pull
- PHD2: the "no mount configured" and "no guide camera configured" errors and the mount/camera/bit-depth mismatch warnings shown while connecting the guider displayed a raw `MISSING LABEL Lbl...` placeholder instead of a message. The five `LblPhd2*` labels were never added to the locale file, and `Loc.Instance[key]` returns that placeholder rather than null, so the `?? "..."` fallback written behind each one could never fire; the labels now exist and the three mismatch warnings are formatted with the actual mount, camera and bit-depth values
- System.Windows.Compat: added the drawing and control surface the 3.3.0.1057 framing-assistant raster renderer and the centralized hyperlink navigation need to run headless: `ImageAttributes` and the destination-parallelogram `DrawImage` overload, `Bitmap.RotateFlip`, `Graphics.ScaleTransform`, compositing mode and quality, `ContextMenu`/`Separator`/`Orientation`/`MenuItem.Header`, plus `DispatcherOperation` and `NINA.WPF.Base.View` namespace stubs
### Changed
- Updated NINA to 3.3.0.1057-nightly
- `NINA.Test` no longer compiles the upstream tests that bind to XAML views (Nikon camera, imaging-layout data template, simple sequencer / anchorable sequence / guider chart / direct guider views, hyperlink behavior, dock manager), which cannot build against the headless shim

## 1.1.55 - 2026-08-05
### Fixed
- Relative-only INDI focusers (open-loop DC focusers with no absolute-position feedback, e.g. HitecAstro DC) failed every move with `Number property 'ABS_FOCUS_POSITION' not found`: the low-level move always wrote the absolute position instead of `FOCUS_MOTION`/`REL_FOCUS_POSITION`, movement state was tracked from a property such devices never send, a missing `FOCUS_MAX` was treated as a real limit of 0/-1 (clamping every target position or collapsing the per-step chunking), and a failed move left the focuser stuck reporting "moving" forever. Absolute focusers are unaffected
- INDI mounts: "Find home" failed with "Mount refused the home command" on drivers that expose both a home *search* and a go-to-home action (e.g. iOptron): the search action was always picked because it comes first in the driver's element list, and it is refused outright by some drivers. The go-to-home action is now preferred, which is also what NINA's Find Home means
- System.Windows.Compat: added the drawing surface the new offline framing sky map (Alt/Az grid, horizon and observation time) needs to render headless: a real `HatchBrush` covering the GDI+ hatch styles, `Graphics.FillPolygon`/`DrawLines`/`VisibleClipBounds`, `WriteableBitmap`'s back-buffer protocol, transform recording on `DrawingVisual`/`DrawingContext`, and `Dispatcher` on `DispatcherObject` for the thread-affinity checks
### Changed
- Updated NINA to 3.3.0.1053-nightly

## 1.1.54 - 2026-07-28
### Fixed
- INDI only accepts absolute rotator position

## 1.1.53 - 2026-07-24
### Fixed
- Logger now renames logfiles when time gets synchronized
- MessageBrokers Publish method can now be canceled
- SymbolBroker only generates log entries when count greater than 0

## 1.1.52 - 2026-07-22
### Fixed
- HocusFocus's "Replay Settings" prompt (shown when re-running a saved AutoFocus analysis from Touch-N-Stars) would open and hang forever: the headless dialog bridge only recognized OK/Cancel/Yes/No-style command names, so the prompt's actual choices never got wired to buttons, and dismissing its dead fallback button didn't notify the waiting backend either. Dialog view models that expose a `RequestClose` event (the pattern used by HocusFocus's headless-safe dialogs) now get all of their commands surfaced as buttons, and closing a headless dialog now raises `OnClosed` like a real window does
- Other headless dialogs sharing the same bridge as the Replay Settings fix above were still broken in related ways: the star-detection import dialog only ever showed "Cancel" (its "Apply" command was silently dropped whenever a recognized command name like Cancel was also present), and the HocusFocus frame-review dialog closed itself the moment you clicked Prev/Next/Fit to page through frames instead of just navigating. Both are fixed, and the modal's X/dismiss button now also works for dialogs with more than one button (it previously only worked when a dialog had exactly one)
### Changed
- Updated NINA to 3.3.0.1050-nightly

## 1.1.51 - 2026-07-20
### Fixed
- INDI: Connection can now be canceled
- ToupTek-alike filter wheels: filter changes no longer report ready before the wheel has physically finished moving (some firmware reports a matching position readback prematurely); the position setter now waits for the readback to stably confirm the commanded position
### Changed
- Updated NINA to 3.3.0.1049-nightly

## 1.1.50 - 2026-07-14
### Fixed
- Fixed an issue with Alpaca cameras

## 1.1.49 - 2026-07-13
### Fixed
- Fixed issue where GuideGraph config changes were ignored

## 1.1.48 - 2026-07-10
### Fixed
- System.Windows.Compat: `Blob.Area` (Accord.Imaging shim) now reports actual pixel count instead of contour point count, matching Accord semantics
- `Application.Current.Shutdown()` was a silent no-op; the host's shutdown lifetime is now wired up so it actually terminates the process (fixes plugin-update restart leaving the old instance running, and `--exitAfterSequence` never exiting)
- System.Windows.Compat: `CommandManager.RequerySuggested` now holds subscribers weakly, matching WPF, instead of pinning every `RelayCommand.CanExecuteChanged` subscriber's target for the process lifetime
- System.Windows.Compat: `Bitmap.LockBits` now throws instead of silently ignoring a requested sub-rectangle or pixel-format conversion it can't honor (previously handed back the full buffer in the Mat's own layout regardless of what was requested); the format check compares against the format the bitmap was created with, so `Format32bppPArgb`/`Format32bppRgb`/`Format16bppRgb565` bitmaps (which share a Mat type with other formats) can still be locked with their own format
- Loading a non-grayscale image file (color JPEG/PNG/GIF/TIFF) via the image loader no longer fails: `FormatConvertedBitmap` now implements all Gray8/Gray16/Bgr24/Bgra32/Rgb48 conversion pairs instead of silently handing back an unconverted clone that lied about its format for unhandled combinations
- Fixed `WeakEventManager` silently never attaching subscriptions to plain `event EventHandler`/`PropertyChangedEventHandler` sources (only worked for the rarer `event EventHandler<T>` shape) - this was breaking live propagation of target coordinate/location/profile changes through the sequencer (e.g. editing a target's coordinates after adding it to a container no longer updated dependent instructions). Also fixed cross-source hash-collision corruption and unbounded growth from repeated container cloning by keying subscriptions on source identity and self-pruning collected entries; subscriptions whose event args type derives from the manager's `TEventArgs` (e.g. `MouseButtonEventArgs` through `WeakEventManager<T, MouseEventArgs>`) now bind via relaxed delegate binding instead of failing, and static-method handlers can now be removed again
- System.Windows.Compat: `BitmapSource.CopyPixels` now copies row-by-row when the underlying Mat isn't continuous (e.g. an un-cloned ROI view) instead of a single flat copy that assumed no padding between rows
- System.Windows.Compat: `BitmapImage(Uri)` and `EndInit()` now actually load the file via `UriSource` instead of silently leaving the image empty
- System.Windows.Compat: `JpegBitmapDecoder(Stream)` now loops until the buffer is fully read instead of trusting a single `Read` call, which could truncate the image on network/chunked streams
- HocusFocus's surface-plot Z-axis label and per-star elongation-ellipse annotations were rendering unrotated in the wrong place: `Graphics`'s `TranslateTransform`/`RotateTransform` now apply to all drawing primitives (`DrawLine`/`DrawRectangle`/`DrawEllipse`/`DrawPolygon`/`DrawString`/`DrawBeziers`/`FillRectangle`/`FillEllipse` and every `DrawImage` overload), not just one `DrawImage` overload, and rotated ellipses now rotate in the correct (GDI+ clockwise) direction instead of mirrored; `Graphics.Save()`'s cloned transform is now disposed by `Restore()` instead of waiting on a finalizer
- The Framing Assistant now mirrors a flipped sky-survey image on the correct axis: `TransformedBitmap`'s negative-`ScaleX`/`ScaleY` flip directions were swapped (OpenCV's `FlipMode.X` is a vertical flip, not horizontal, despite the name)
- System.Windows.Compat: `TransformedBitmap` now supports every transform type - `TranslateTransform`, plus newly added `RotateTransform`/`MatrixTransform`/`TransformGroup` - through a general affine path that sizes the output to the transformed bounds like WPF, instead of silently copying the source unchanged for unhandled transform types; the parameterless-constructor + `BeginInit`/`EndInit` idiom no longer leaves the bitmap in a null-backed state that NREs on first use
- System.Windows.Compat: `RenderTargetBitmap.Render` now renders `DrawGeometry` operations (`RectangleGeometry`/`PathGeometry`/`GeometryGroup`) instead of silently skipping them, and `DrawImage` now clips to the canvas instead of rejecting the entire draw when a rect is only partially out of bounds
- System.Windows.Compat: `ResourceDictionary`'s indexer and `Add`/`TryGetValue` now share one real backing store instead of being two disconnected stores (the indexer setter previously dropped writes entirely)
- System.Windows.Compat: `Colors.Green` now matches WPF's `#FF008000` instead of `(0,255,0)` (which is actually `Colors.Lime`)
- System.Windows.Compat: `Rect.IsEmpty`/`Int32Rect.IsEmpty` now match WPF semantics (true only for the dedicated `Empty` sentinel/all-zero value) instead of treating any legitimately zero-width-or-height rect as empty
- System.Windows.Compat: `DispatcherOperation.Status`/`Wait()` now reflect the backing task's actual state instead of always reporting `Completed` immediately; `UnmanagedImage(BitmapData).ToManagedImage()` no longer returns a bitmap backed by a null Mat; `DialogService.ClickButton` no longer NREs on a null button name
- Removed the dead, unused `TouchNStars.Utility.DialogManagerExtensions` from System.Windows.Compat (zero callers anywhere in the solution)
- System.Windows.Compat performance on large sensors: rotated `DrawString` now warps a glyph-sized buffer instead of two full-canvas frames per label, and `Blob.Area`/`Fullness` (a per-blob mask fill) are computed lazily on first access instead of eagerly for every contour of every star-detection frame
- System.Windows.Compat: `CollectionViewSource.GetDefaultView` now caches one view per source collection (matching WPF) instead of returning a fresh view that discarded previously-set state, and the view now actually applies its `Filter` predicate consistently across enumeration, count, indexing, and `MoveCurrentTo`; a null source yields an empty view instead of one that NREs on first enumeration

## 1.1.47 - 2026-07-06
### Fixed
- INDI Telescope: Equatorial slew completion no longer exits early on mounts that ack a goto with Ok/Idle instead of Busy for short slews

## 1.1.46 - 2026-07-03
### Fixed
- INDI: Drain indiserver's stdout/stderr so a chatty driver can no longer fill the pipe buffer and hang the server
- INDI: Process incoming XML elements sequentially instead of in parallel, preserving protocol ordering (a def/set pair or a Busy/Ok pair could previously be applied out of order)
- INDI: Fix pending async-operation matching so a property update no longer resolves an unrelated pending operation on a same-prefixed property (e.g. CCD_EXPOSURE vs. CCD_EXPOSURE_ABORT, CONNECTION vs. CONNECTION_MODE)
- INDI: Fix AtMostOne switch rule never being recognized due to a parser typo
- INDI: Fix CanMoveAxis always reporting true regardless of whether the driver exposes motion properties
- INDI: FIFO writes for driver load/unload no longer block indefinitely while holding the driver lock if indiserver is hung
- INDI: Async operation completions now resume off the receive thread instead of inline, avoiding added latency and deadlock risk from continuations running inside internal locks
- INDI: UTF-8 characters split across TCP reads no longer get corrupted into replacement characters
- INDI Dome: Park now waits for the park motion to actually finish instead of returning as soon as the PARK switch value flips on
- INDI Camera: Debounce StartExposure after an abort (and warn) to reduce the chance a stale BLOB from the aborted exposure is delivered as the next frame
- INDI: Tolerant number parsing for sexagesimal/empty values instead of throwing and dropping the whole property vector
- INDI: Focuser/rotator/filter-wheel moves now time out instead of polling forever if the driver never reports completion
- INDI Flat Panel: Fixed a crash reading Brightness before FLAT_LIGHT_INTENSITY has arrived
- INDI: Guarded remaining unsynchronized access to the loaded-driver table
- INDI: Fixed a rare XML parsing edge case where a driver message containing a literal "/>" could truncate the element
- INDI: Fixed a rare receive-stream stall when a TCP read boundary landed inside a self-closing element (e.g. <message/>): the parser would wait forever for a closing tag that never arrives
- INDI: A DRIVER_INFO re-broadcast from an unrelated driver can no longer complete a different in-flight driver load
- INDI: Misc robustness fixes (pulse-guiding state tracking, UTC date parsing culture, duplicate constants, non-reentrant AxisRates enumerator, LX200 socket cleanup on dispose, clearer NotImplementedException messages)
- INDI: BLOB base64 decoding no longer copies the multi-megabyte payload twice per frame (noticeable on Raspberry Pi class hardware)
- INDI Flat Panel/Dome: cover, shutter, azimuth and park waits now time out instead of polling forever if the driver never reports completion (matching the earlier focuser/rotator/filter-wheel fix)
- INDI: Setting a switch element that does not exist on the vector no longer sends an illegal all-off OneOfMany update (e.g. a profile baud rate matching no DEVICE_BAUD_RATE element)
- INDI Switch Hub: value-update notifications are now dispatched off the receive thread instead of running equipment-layer handlers inside the client's internal lock
- INDI: A re-broadcast stale CONNECTION Alert no longer marks a fresh connection attempt as failed
- INDI: Removed SupportedActions declarations that hid the base implementation with a null value; removed dead COM-era ErrorCodes utility
- INDI: A device whose driver acknowledged CONNECT but then failed to connect (e.g. a TCP mount whose socket connect times out) is no longer reported as connected — the CONNECTION switch is re-checked after the ack
- INDI Telescope: Setting a tracking mode the mount does not support (e.g. King without TRACK_CUSTOM) no longer sends an illegal all-off TELESCOPE_TRACK_MODE update; switch-rule validation now checks the vector's actual elements everywhere
- INDI Telescope: Park/unpark/home waits now time out instead of polling forever if the mount never reports completion, and park/unpark wait for the driver's acknowledgement before polling (matching the earlier dome/focuser fixes)
- INDI: After an indiserver crash, disconnecting no longer waits a full DISCONNECT timeout per device (stale discovered devices are cleared)
- INDI Dome: Set park position now targets the correct DOME_PARK_OPTION property (previously a silent no-op)
- INDI: A FIFO driver command that timed out can no longer be delivered belatedly after indiserver becomes reachable again
- INDI: XML parse errors are now always logged (the previous filter matched locale-dependent exception text); misc cleanup (LX200 socket disposal race, leaked sockets on connect retries, spurious receive-error log on disconnect, CanSetTemperature now keyed on a writable CCD_TEMPERATURE, removed CommandAction redeclarations that hid base virtuals)
- INDI: A connection that died mid-BLOB no longer poisons the XML scanner state of the next connection (which would buffer everything and deliver nothing)
- INDI: Drivers slow to describe themselves (e.g. camera SDK enumeration) are no longer treated as failed loads — the enumeration wait was raised from 3s to 15s and a started driver is tracked as loaded immediately, so rescans no longer write duplicate "start" commands to the FIFO
- INDI Dome: Open/close shutter can no longer report completion while the shutter is still moving (ack-ordering race and late-Busy drivers are both handled; waits now also watch the property's own Busy state)
- INDI Telescope: Find home no longer returns immediately (claiming the mount is home) when the mount takes more than a second to start moving, and no longer claims home after giving up or being cancelled
- INDI Telescope: Turning tracking on/off on a mount without TELESCOPE_TRACK_STATE now reports "not implemented" instead of throwing an internal error
- INDI Camera: A failed exposure start now wakes the waiting capture immediately instead of parking it until the camera timeout; deferred profile switches are applied off the receive thread
- INDI Dome: The set-park-position capability is now keyed on the property it actually writes (DOME_PARK_OPTION)
- INDI: zlib-compressed BLOBs (".fits.z", sent when a device's CCD_COMPRESSION is enabled) are now inflated on receipt instead of being passed through as compressed garbage
- INDI: Reduced receive-path memory churn and footprint on Raspberry Pi class hardware (reused decode buffer, BLOB-sized string builder capacity is released after the frame); supplementary Unicode characters in device labels are no longer stripped
- INDI: A fast disconnect/reconnect cycle can no longer end up with the old receive loop reading the new connection's stream or clobbering the XML scanner state (the loop is now bound to its own stream and disconnect waits for it to exit)
- INDI Telescope: Slew completion waits (equatorial and AltAz) now give up with a warning after 5 minutes instead of polling forever, and the AltAz wait no longer returns early on drivers that take more than a second to start reporting motion (also unblocks AltAz mounts that hold HORIZONTAL_COORD busy while tracking)
- INDI: IINDIDevice now extends IDisposable so device adapters participate in standard disposal
- INDI Telescope: An AltAz slew is no longer falsely reported as rejected when the mount's coordinate property still carried an Alert state from an earlier failed operation, and a slew to the current position completes immediately instead of waiting out a fallback timeout
- INDI: Async property writes targeting an element the driver does not expose (e.g. a profile connection mode or baud rate this device lacks) now fail immediately with a clear warning instead of burning the full acknowledgement timeout (or being resolved as a false success by an unrelated re-broadcast)
- INDI Telescope: Abort slew now actually stops the mount — the command targeted a non-existent element name (ABORT_MOTION instead of the standard ABORT) and was silently skipped
- INDI Telescope: Find home now detects homing motion via the mount's actual position in addition to the reported slew state (OnStep firmwares home without ever reporting a slew), waits for the position to settle before declaring home, and reports an error when the driver refuses the home command (e.g. while parked) instead of pretending the mount is home
- INDI Telescope: The Slewing state itself now also detects motion from the mount's reported coordinates, so movement that no property state reflects (e.g. OnStep homing) shows up as slewing in the UI and sequencer; syncs are exempted so a platesolve sync's coordinate jump does not read as motion
- INDI Telescope: Park is no longer reported complete seconds too early on drivers that publish TELESCOPE_PARK as Ok while the mount is still slewing to its park position (observed on lx200_OnStep): pins now waits for the park motion to start and stop, and a driver-reported park failure surfaces as an error instead of "parked"
- INDI Telescope: Unpark now waits for the mount to actually report unparked instead of trusting the command acknowledgement — OnStep can reject the unpark reply on a serial hiccup while the controller unparks anyway, which previously made pins return early and fire the follow-up tracking command against a still-parked driver ("Telescope is Parked, Unpark before tracking"); a mount that genuinely stays parked now surfaces an error after 60s
- INDI: AtPark/IsParked (telescope, dome, flat panel) no longer read true the moment a park is commanded — the PARK switch shows the target while the vector state shows progress, so "at park" now additionally requires the park operation to no longer be in progress

## 1.1.45 - 2026-07-02
### Changed
- Updated NINA to 3.3.0.1048-nightly

## 1.1.44 - 2026-07-01
### Fixed
- INDI: Explicitly enable tracking after slew on AltAz mounts
- INDI: Fix first slew after connect being rejected as "below the horizon limit" (e.g. TPPA right after connecting) by awaiting the tracking-enable transition before the goto
- Fixes Cfitsio with byte array

## 1.1.43 - 2026-06-29
### Fixed
- Fixed Auto Restore Calibration option in phd2 guiding
- Fixed issue where max slew rate required mount reconnect

## 1.1.42 - 2026-06-24
### Added
- SequenceItem to slew to phd2 calibration position

## 1.1.41 - 2026-06-22
### Fixed
- INDI: Fix for direct USB connection

## 1.1.40 - 2026-06-17
### Added
- INDI: Control panel
### Fixed
- INDI: Supporting CONNECTION_HTTP on mounts
- INDI: Homing on some mounts

## 1.1.39 - 2026-06-15
### Fixed
- INDI: Geo sync issue with AM3/5/7 mounts

## 1.1.38 - 2026-06-11
### Added
- INDI Camera: Native INDI camera support, including a per-profile INDI camera driver setting and camera enumeration in the camera chooser
- INDI Camera: Broader driver compatibility by probing alternate property/element names for cooler power, offset, USB bandwidth, and dew heater control
- INDI Camera: Camera mode switches (low-noise/ultra, high-fullwell, tail-light LED) are applied from the profile on connect, deferring until the corresponding property arrives if it is not yet present
### Fixed
- INDI Mount: Send UTC time and UTC offset together as one atomic update; previously only the time was written, leaving a stale offset after power-on so the mount computed the wrong sidereal time and slews could fail
- INDI Mount: Alt/Az slew capability now reflects whether the mount actually accepts alt/az gotos; mounts that only publish alt/az read-only fall back to an equatorial slew instead of a silently-ignored command
- INDI: Defer device unregistration until a graceful disconnect is acknowledged, and recognize the idle-state disconnect acknowledgement, to avoid spurious disconnect timeouts
- INDI: A device-level property deletion (hot-unplug or driver shutdown) now removes the entire device so rescans no longer list stale devices
- INDI: Honor runtime element range changes (min/max/step) on number property updates
- INDI: The bounded server-ready wait is now performed inside device enumeration so a missing or broken INDI server cannot hang the device scan

## 1.1.37 - 2026-06-10
### Fixed
- LibRaw: Reverted from hardcoded struct offsets to libraw C API (libraw_raw2image, libraw_get_iwidth, libraw_get_iheight) for improved compatibility with system-installed libraw versions
- LibRaw: Fixed pixel layout handling for correct CFA sample extraction from multi-channel output
- libgphoto2: Wrapped constructor initialization in try-catch with proper cleanup; failures now throw instead of silently returning half-initialized cameras
- libgphoto2: Fixed unreliable shutterspeed property write for bulb mode; now refuses captures > 30s in Manual mode instead of truncating
- libgphoto2: Added shutter release guard to prevent multiple releases per bulb exposure
- libgphoto2: Added error checks for abilities list load/creation; gracefully skips device-type filtering if unavailable
- libgphoto2: Disabled CanShowLiveView (feature not implemented); property checks were unreliable
- libgphoto2: Use plain status checks instead of logging errors for optional properties like shutterspeed
- libgphoto2: Skip battery polling during active exposures to avoid disturbing captures
- libgphoto2: Fixed gp_port_info_list_lookup_path P/Invoke marshaling to use [MarshalAs(UnmanagedType.LPStr)] string
- libgphoto2: Catch per-camera initialization failures individually instead of aborting all enumeration
- Camera: AbortExposure now updates exposure state before broadcasting to ensure consumers see correct state

## 1.1.36 - 2026-06-09
### Fixed
- Fixed non-motorized INDI Flat panels

## 1.1.35 - 2026-06-08
### Fixed
- Retry mechanism for failing Nitecrawler serial comm
- Share invalid chars across platforms
- NULL char in DSLR image string

## 1.1.34 - 2026-06-02
### Added
- phd2 profile properties

## 1.1.33 - 2026-06-01
### Changed
- Updated NINA to 3.3.0.1046-nightly

## 1.1.32 - 2026-05-21
### Changed
- Updated NINA to 3.3.0.1043-nightly
- Logger colors (ERROR red, WARNING orange)

## 1.1.31 - 2026-05-20
### Changed
- Updated NINA to 3.3.0.1042-nightly
### Added
- GET/SET guide rate, if supported

## 1.1.30 - 2026-05-19
### Added
- Added Manual flat device, in order to properly control dark flat generation

## 1.1.29 - 2026-05-19
### Fixed
- AddedFixed issue with dark flats when no flat panel was connected

## 1.1.28 - 2026-05-18
### Changed
- Updated NINA to 3.3.0.1040-nightly
### Fixed
- Fixed OnStep mounts running into timeout when homing

## 1.1.27 - 2026-05-16
### Fixed
- INDI Focuser no longer show wrong step sizes
- Fixed an issue, where duplicating sequencer items reset the iteration count

## 1.1.26 - 2026-05-15
### Changed
- Updated NINA to 3.3.0.1039-nightly

## 1.1.25 - 2026-05-12
### Added
- Support for HocusFocus 3.0.0.26 plugin
- Support for NightSummary v3.0.0 plugin
- Support for INDI Switch pre/post-connection delay (to fix SV241 Pro connection and mirror [Ekos rule required](Yup, maybe say "mirror the Ekos rule required on the svbony website https://www.svbony.com/blog/review-of-the-new-sv241pro-power-controller-from-svbony"))

## 1.1.24 - 2026-05-11
### Fixed
- Fixes gphoto2 related issues with Nikon
- Fixes issue in GPCamera class where disconnect was not called on connect failure

## 1.1.23 - 2026-05-07
### Fixed
- Fixes a buffer overflow with QHY Filterwheels
- Fixes a race condition in QHY SDK

## 1.1.21 - 2026-05-05
### Added
- Profile entry for the slot number for ToupTekAlike Filterwheels

## 1.1.20 - 2026-05-03
### Changed
- Updated NINA to 3.3.0.1036-nightly
### Fixed
- Delayed GC

## 1.1.19 - 2026-04-29
### Changed
- Updated NINA to 3.3.0.1034-nightly
### Fixed
- SVBonySDK type mismatch
- Filestreams flushes to disk now so the write truly completes before returning

## 1.1.16 - 2026-04-28
### Changed
- Throw error on null char in file save path

## 1.1.15 - 2026-04-27
### Changed
- Updated NINA to 3.3.0.1033-nightly

## 1.1.14 - 2026-04-24
### Changed
- Updated NINA to 3.3.0.1030-nightly

## 1.1.13 - 2026-04-23
### Changed
- Updated NINA to 3.3.0.1026-nightly

## 1.1.12 - 2026-04-22
### Changed
- Updated NINA to 3.3.0.1025-nightly

## 1.1.11 - 2026-04-21
### Added
- Support INDI safety monitors and INDI domes (experimental)

## 1.1.10 - 2026-04-20
### Changed
- Updated NINA to 3.3.0.1024-nightly

## 1.1.9 - 2026-04-17
### Changed
- Some more stubs (colors etc)
- Updated NINA

## 1.1.8 - 2026-04-13
### Changed
- Some stubs to support LiveStack 1.1.0.0

## 1.1.7 - 2026-04-09
### Changed
- Updated to NINA 3.3.0.1023-nightly
### Fixed
- Fixed Atik EFW native driver implementation

## 1.1.5 - 2026-04-08
### Fixed
- Prevent premature collection on CopyPixels compatibility function

## 1.1.4 - 2026-04-07
### Fixed
- SWCREATE tag adjusted
- Disabled QHY query to System.Management functionality

## 1.1.3 - 2026-04-05
### Fixed
- Fixed race condition in Touptek SDK wrapper
- Fixed unhandled exception in status update bar

## 1.1.1 - 2026-04-03
### Added
- Support for Atik devices (experimental)

## 1.1.0 - 2026-04-01
### Added
- Support for INDI powerboxes (switches)
- Support for multi-filter flatwizzard

## 1.0.7 - 2026-03-31
### Fixed
- More issues with shared driver fixed.

## 1.0.6 - 2026-03-30
### Fixed
- Fixed INDI shared driver
- Fixed issues with compatibility layer

## 1.0.5 - 2026-03-27
### Fixed
- Fixed DSLR issues with the Image History tab
- Fixed DSLR issues with longer exposures when in BULB mode

## 1.0.4 - 2026-03-26
### Added
- Public methods for filterwheel calibration added

## 1.0.3 - 2026-03-25
### Fixed
- Message boxes and progress state will now also show current state on TNS reconnect

## 1.0.2 - 2026-03-24
### Fixed
- MessageBoxItem was not sent to TNS
- Dialogs were sometimes closed too early

## 1.0.1 - 2026-03-24
### Changed
- Updated to NINA 3.3.0.1021-nightly

## 1.0.0 - 2026-03-23
### Fixed
- Fixed an issue where HocusFocus Autorun could cause segmentation fault
