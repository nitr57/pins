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
INDI holds its port exclusively, so the open fails there. `Id` and `Name` stay fixed for the profile's selection. It reads the mount's state as one set of reads at most every 250 ms (the UI polls about
30 properties at a time), keeps guide pulses per axis by time like INDI, and handles the OnStep behaviours
`INDITelescope` documents: a goto right after tracking was switched on is refused as below the horizon (one retry).
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
- Tests: `NINA.Test/Equipment/OnStepX`, with a scripted fake port; `OnStepXHardwareTest` is explicit and read-only
  against a real mount on `ONSTEPX_PORT`. Motion is checked by hand on the mount.

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
