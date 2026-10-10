# NINA.Equipment Architecture

## Purpose

`NINA.Equipment` is the hardware integration layer. It defines device abstractions and implements concrete adapters for cameras, mounts, focusers, guiders, domes, switches, weather devices, GNSS sources, planetarium integrations, and vendor SDK wrappers.

Build shape from `NINA.Equipment.csproj`:

- Target framework: `net10.0-windows`
- Output type: `Library`
- WPF enabled

## Top-Level Structure

- `Interfaces/`
  Device contracts such as `ICamera`, `ITelescope`, `IFocuser`, `IGuider`, `IDome`, `ISwitchHub`, `IWeatherData`, plus factories like `IGnssFactory` and `IPlanetariumFactory`.
- `Equipment/`
  Concrete implementations grouped by device category:
  - `MyCamera`
  - `MyTelescope`
  - `MyFocuser`
  - `MyFilterWheel`
  - `MyGuider`
  - `MyDome`
  - `MySwitch`
  - `MyFlatDevice`
  - `MyWeatherData`
  - `MyGPS`
  - `MyPlanetarium`
  - `MyRotator`
  - `MySafetyMonitor`
- `SDK/`
  Native/vendor interop wrappers for camera, focuser, filter wheel, flat device, rotator and telescope SDKs.
- `Utility/`
  Discovery and integration helpers such as `ASCOMInteraction` and `AlpacaInteraction`.
- `Exceptions/` and `Model/`
  Equipment-specific support types.

## Implementation Pattern

The project follows a clear pattern:

- interfaces define the device surface
- category folders contain concrete implementations
- discovery helpers instantiate those implementations
- SDK folders isolate vendor-specific interop

Examples from the code:

- `Utility/ASCOMInteraction.cs`
  Enumerates installed ASCOM drivers and creates `AscomCamera`, `AscomTelescope`, `AscomFilterWheel`, `AscomFocuser`, and related wrappers.
- `Utility/AlpacaInteraction.cs`
  Discovers network devices through `AlpacaDiscovery.GetAscomDevicesAsync(...)`, wraps them in the same adapter types, and also exposes direct Alpaca clients such as `AlpacaDirectCamera`.
- `Equipment/MyCamera/GenericCamera.cs`
  Provides a reusable `ICamera` implementation on top of an `IGenericCameraSDK`.
- `Equipment/MyCamera/FileCamera.cs`
  Provides a non-hardware camera implementation backed by files on disk.
- `Equipment/MyGuider/MGENGuider.cs`
  Adapts the `NINA.MGEN.IMGEN` library into the guider abstraction.
- `Equipment/MyGuider/Internal/`
  pins-only: the Internal Guider, which hosts the guiding engine `NINA.GuideEngine` as a NINA guider and takes its
  frames from the guide camera slot (`IGuideCameraMediator`). One instance, created through DI and listed by the
  guider chooser; its files keep their author's layout (own `.editorconfig`). See
  [`NINA.GuideEngine/ARCHITECTURE.md`](../NINA.GuideEngine/ARCHITECTURE.md#host-boundary).
- `Interfaces/IAdvancedGuider.cs`, `IGuidingCoach.cs`, `IGuideIncidentRecorder.cs` and `Equipment/MyGuider/Advanced/`
  pins-only: optional extensions of `IGuider` for the internal guider, through which UIs read live frames, guide steps,
  calibration, statistics, alerts and settings, and run the Guiding Coach and the incident recorder. UIs find them with
  `guiderMediator.GetDevice() as IAdvancedGuider`. The DTOs in `MyGuider/Advanced` are plain JSON-serializable classes
  and the vocabularies (states, event types, codes) are string constants. The contract has no version: its implementer
  and its consumers (the Touch-N-Stars plugin) are built together with pins.
- `Equipment/MyPlanetarium/PlanetariumFactory.cs` and `Equipment/MyGPS/GnssFactory.cs`
  Select concrete external integrations from profile settings.

## ASCOM, Alpaca, And Native SDKs

The code supports multiple backends in parallel:

- ASCOM COM drivers through `ASCOMInteraction`
- Alpaca discovery and direct network clients through `AlpacaInteraction`
- native vendor SDKs under `SDK/*`
- file-based or built-in utility devices like `FileCamera`

The `Equipment/AscomDevice.cs` base class is the shared adapter foundation for many ASCOM/Alpaca implementations.

### OnStepX (pins)

`SDK/TelescopeSDKs/OnStepXSDK` talks to OnStepX mount controllers over USB serial without INDI. Commands, formats and
reply types follow INDI's `lx200_OnStep.cpp` and `lx200driver.cpp`; vendor firmware built on OnStepX derives from
`OnStepXDevice` (`ProxiskyUmiDevice` adds the Proxisky `:P…` commands of INDI's `lx200_proxisky.cpp`), and
`OnStepXDevice.Connect` picks the class from what the controller reports.

`Equipment/MyTelescope/OnStepXTelescope` is the `ITelescope` on top, listed by `TelescopeChooserVM` and connected on
`TelescopeSettings.SerialPort`. `OnStepXTelescope.Discover` names the list entry after the model the mount reports: it
opens the port briefly with short timeouts, but not one this process has open (`OnStepXSerialPort.IsOpenInProcess`);
a port another program holds exclusively (TIOCEXCL, as INDI and .NET do) fails to open, others are not detected, and a
scan never replaces a known model with the generic name. `Id` and `Name` stay fixed for the profile's selection. It
reads the mount's state as one set of reads at most every 250 ms (the UI polls about 30 properties at a time); an
unreadable state is not retried by the rest of that poll, and the connection counts as lost only after three failures
over at least 2 s. It handles the OnStep behaviours `INDITelescope` documents: a goto right after tracking was switched
on is refused as below the horizon (one retry).
Homing ends when the controller clears its homing flag `h` in `:GU#`; `H` only means the axes are within the home
tolerance, which the UMi misses after a long slew. The `:GU#` flags are decoded as OnStepX 10.20a writes them
(`src/telescope/mount/status/Status.command.cpp` of hjd1964/OnStepX at 79492e8, the base of the UMi firmware); INDI
still reads some of them as OnStep 3.x did (e.g. `W`, the pier side, as a PEC state). Where the firmware answers a
command INDI sends blind, the driver reads the answer: `:hP#` is `1` or `0` (OnStepX replies `1`/`0` without `#` unless
a command sets `numericReply = false`, see `src/libApp/commands/ProcessCmds.cpp`).

- It does not use `NINA.Core.Utility.SerialCommunication.SerialSdk`: OnStepX replies end in `#`, are a single bare
  character, or are absent, and an unknown command is answered with a bare `0`.
- ESP32 controllers reset while RTS is set and DTR is not. `OnStepXSerialPort` keeps both set, as Linux sets them on
  open, and never toggles them; clearing DTR before RTS restarts the mount (about 8 s without replies).
- Refusals: a command answered `0` is followed at once, in the same exchange, by `:GE#` (the controller's last
  command error, `ProcessCmds.cpp`; codes in `OnStepXCommandError`, the same in 10.20a and 10.24c) and throws
  `OnStepXCommandRefusedException` with that reason. `:hC#` has no reply but records its error, so `:GE#` follows it
  too. `OnStepXTelescope` turns refusals of goto, park, unpark, find home and set park into exceptions: `TelescopeVM`
  ignores the result of `SlewToCoordinates` and reports a park as done unless `Park` throws. A cancelled goto, park or
  home stops the mount (`:Q#`), as ASCOM drivers do.
- Park and home: `Park::request` and `Home::request` refuse during a goto (also while it brakes) and any guide motion,
  so the driver stops everything first and waits until `:GU#` shows neither a slew nor `g`/`G`.
- Manual moves: `Slewing` covers gotos, homing and manual moves. A move faster than 2x shows as `g`, a slower one as a
  pulse guide (`G`), so the driver also counts the moves it started, until a status read after the move command shows
  neither flag. A stop is sent only for an axis known to move, since `:Qn#` and friends also end a guide pulse (and
  abort homing). Two motions show no motion flag in `:GU#`: homing with home sensors runs as a guide, not a goto
  (`Home::request`), and a fast move brakes after its stop as `GA_BREAK`, which `Guide::active()` leaves out. The driver
  counts `h` and, like `INDITelescope`, RA/Dec changing faster than 0.05°/s, taken over at least 0.5 s because `:GR#`
  has whole seconds.
- Custom tracking rates are OnStepX's tracking rate offsets (`:SXTR,n.n#`, `:SXTD,n.n#`, `Mount.command.cpp`), in
  arc-seconds per sidereal second with RA counted in RA: ASCOM's RA rate times 15, its Dec rate (per SI second)
  divided by 1.0027379. `:GU#` does not show them, so the driver keeps what it set and reads `:GXTR#`/`:GXTD#` only at
  connect and after homing, which clears them (`Home::reset`). INDI's `lx200_OnStep` sends `:RA`/`:RE` for this, which
  OnStepX takes as guide rates.
- Tracking rate and rate compensation come from `:GU#` (`(` lunar, `O` solar, `k` King; `r`/`t` with `s` for one
  axis). `:TL#`, `:TS#` and `:TK#` turn compensation off and `:TQ#` does not restore it, so the driver restores it on
  the way back to sidereal.
- Meridian flip: a plain goto. OnStepX picks the side by its preferred pier side (`:GX96#`/`:SX96,[EWB]#`); with
  "Best" it stays on the current side until the meridian limit (`Goto.cpp` `setTarget`). The driver sets
  `TelescopeSettings.PreferredPierSide` at connect and on change (OnStepX forgets it at power off unless built with
  `PIER_SIDE_PREFERRED_MEMORY`) and treats a flip that ends on the wrong side as failed, so NINA's retries go on.
- Guide pulses: `:Mg` has no reply, so `OnStepXTransport.SendBlindNow` writes it past a read in progress (only the
  writing of commands is serialized, and not between a refused command and its `:GE#`) and returns when the command is
  on the line, estimated from the characters queued at 9600 baud. A pulse counts as running until then plus its
  duration plus 15 ms, and after that until `:GU#` no longer shows `G`, so no guide exposure starts while the mount
  moves: on a UMi17S most pulses ended 0-15 ms after the estimate, but USB serial now and then delays a command by up
  to ~80 ms. Pulses the controller would refuse (`Guide::validate`) throw, judged on the last known status, never on a
  fresh read that would delay the pulse; so does a pulse after a goto, park or home was sent and before a status read
  after it, since a pulse aborts a goto.
- Tests: `NINA.Test/Equipment/OnStepX`, with a scripted fake port; `OnStepXHardwareTest` is explicit and runs against
  a real mount on `ONSTEPX_PORT`: it reads, and its pulse test moves the mount a few arcseconds. Gotos, parks and moves
  are checked by hand on the mount.

## Special Integration: SBIG Camera Service

This project also contains the SBIG-specific camera service bridge:

- `Equipment/MyCamera/SBIGCamera.cs`
- `Equipment/MyCamera/SBIGCameraASCOMService.cs`

That code uses the protobuf/gRPC camera contract generated in `NINA.Core` plus `GrpcDotNetNamedPipes`. This is an exception to the otherwise simple adapter model and is worth preserving as equipment-layer code rather than moving into UI projects.

## Dependency Position

Project references:

- `NINA.Core`
- `NINA.Astrometry`
- `NINA.Image`
- `NINA.MGEN`
- `NINA.Profile`
- `nikoncswrapper`

That dependency set is consistent with the code:

- shared models/utilities from `NINA.Core`
- coordinate/math types from `NINA.Astrometry`
- exposure/image conversion types from `NINA.Image`
- guider transport for MGEN from `NINA.MGEN`
- runtime settings from `NINA.Profile`

There is no dependency on the main app shell or plugin loader.

## Contribution Notes

- Put new device interfaces in `Interfaces/` before adding implementations.
- Keep vendor DLL interop and P/Invoke code under `SDK/` or device-specific implementation folders.
- Use the existing discovery helpers for ASCOM/Alpaca-backed devices instead of inventing new scanning paths.
- UI-facing mediators and view models do not belong here; those live in `NINA.WPF.Base` and `NINA`.
- If a device needs persisted settings, wire it through `IProfileService` rather than storing state inside the adapter alone.
