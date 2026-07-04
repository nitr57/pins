# NINA.INDI Architecture

## Purpose

`NINA.INDI` is the PI 'N' Stars (pins) library that lets the application talk to astronomy hardware through the [INDI](https://indilib.org/) protocol instead of (or alongside) ASCOM/Alpaca. It owns the indiserver process lifecycle, the wire protocol, a global property store, and a set of per-device-type adapters.

Build shape from `NINA.INDI.csproj`:

- Target framework: `net10.0`
- Output type: `Library`
- Project references: `NINA.Core`, `NINA.Astrometry` only

This project is pins-specific; it does not exist in upstream NINA. It is consumed solely by `NINA.Equipment`, which wraps the INDI device adapters as concrete `NINA.Equipment` devices (camera, telescope, focuser, …).

## Top-Level Structure

- `INDIClient.cs`
  The hub. A process-wide singleton (`INDIClient.Instance`) that starts/stops `indiserver`, maintains the TCP connection, parses inbound XML, owns the global property store and message log, loads/unloads drivers, and routes property updates to registered devices.
- `Protocol/`
  Wire-protocol layer. `INDIProperty.cs` (the number/switch/text/light/blob property + element model), `INDIProtocolParser.cs` (XML ⇄ model), and `INDISnapshot.cs` (immutable, JSON-friendly snapshots for the control panel).
- `Devices/`
  Per-device-type adapters. `INDIDevice.cs` is the base class; `INDICamera`, `INDITelescope`, `INDIFocuser`, `INDIFilterWheel`, `INDIDome`, `INDIRotator`, `INDIFlatDevice`, `INDISafetyMonitor`, `INDISwitchHub`, and `INDIWeatherData` specialize it.
- `Interfaces/`
  The contracts `NINA.Equipment` depends on: `IINDIDevice` plus one interface per device type.
- `Enums/`
  Protocol and domain enums: `PropertyState` (Idle/Ok/Busy/Alert), `PropertyPermission` (ro/wo/rw), `PropertyRule` (switch rules), `DeviceInterface` (the INDI `DRIVER_INTERFACE` bit flags), plus device-domain enums (cover/shutter/tracking-rate).
- `Model/`, `INDISwitchDescriptor.cs`
  Small supporting value types (axis rates, tracking rate, slew-rate capability, switch descriptors).

## Server Lifecycle (Linux-only)

`INDIClient` manages a local `indiserver` started in **FIFO mode**, which is what makes drivers loadable/unloadable at runtime:

- On construction it runs `StartServerInFifoMode()`: `pkill -9 indiserver`, `mkfifo /tmp/indiFIFO`, then launches `indiserver -v -p 7624 -m 1000 -f /tmp/indiFIFO`.
- Drivers are started/stopped by writing `start <driver>` / `stop <driver>` lines into the FIFO.
- Readiness is exposed through `WaitForServerReadyAsync`; callers (e.g. device enumeration) wait on it with a bounded timeout so a missing/broken server never hangs the app.
- `Dispose` / `CleanupServer` kill the server process and remove the FIFO.

This is inherently a Linux deployment story (`mkfifo`, `pkill`, an `indiserver` binary on `PATH`). It aligns with pins being a headless Linux fork — see "Runtime Model (pins fork)" in [`../AGENTS.md`](../AGENTS.md).

## Wire Protocol And Property Model

INDI is line-oriented XML over TCP. The receive path is in `INDIClient.ReceiveLoop` → `ProcessXmlMessage` → `ProcessElement`:

- `ProcessXmlMessage` extracts complete XML elements from a streaming buffer. It has a dedicated **large-element fast path** (`_pendingBigTagName`) so multi-megabyte `setBLOBVector` payloads (camera images) are scanned in O(n) rather than O(n²).
- `ProcessElement` dispatches on element name: `defXxxVector` defines a new property, `setXxxVector` updates one, `delProperty` removes a property (or, with no `name`, an entire device), and `message` is a human-readable driver/server log line.
- `INDIProtocolParser` is the single place that converts between XML and the typed `INDIProperty` subclasses. Keep parsing/serialization here, not in the client or devices.

Property objects are **mutated in place** and shared by reference between the global store and every registered device for that device name. A `set*` update therefore propagates to all observers automatically; outbound writes serialize the (already-mutated) object via `INDIProperty.ToXml()`.

## Global Property Store And The Control Panel

Beyond the devices NINA itself connects, `INDIClient` keeps a global store of **every** property of **every** device currently visible to indiserver:

- `_allProperties` (deviceName → name → property), guarded by `_lock`.
- A rolling buffer of `<message>` log lines (`_messages`, capped at `MaxMessages`).
- `GetDeviceSnapshots` / `GetMessages` produce immutable `INDISnapshot` DTOs that can be serialized off-thread.
- `SetProperty(device, name, elements, out error)` is a generic, type-aware write to any writable property, honoring switch rules.

This store is what powers the generic Touch-N-Stars **INDI control panel** (the pins UI is the Touch-N-Stars Vue app driving the backend through the `ninaAPI` plugin; see "Runtime Model (pins fork)" in [`../AGENTS.md`](../AGENTS.md)): it can inspect and modify devices NINA never connected (e.g. a guide camera owned by PHD2).

## Device Adapter Model

`INDIDevice` is the base for all device-type adapters and encapsulates the hard parts of driving INDI drivers:

- **Registration / routing** — registers with `INDIClient` so it receives `On{Number,Switch,Text,Light,Blob}PropertyUpdated` callbacks. Multiple adapters can share one INDI device name (a single driver like `indi_lx200generic` can expose both a telescope and a focuser); all registered instances get every update.
- **Connection classification** — distinguishes a direct-USB/SDK device (only `CONNECTION` present) from one needing transport config (`CONNECTION_MODE`/`DEVICE_PORT`/`DEVICE_ADDRESS`/`DEVICE_AUTO_SEARCH`), and is careful never to misclassify a non-responding driver. `OnPreConnect` applies serial/TCP/HTTP connection settings before sending `CONNECT`.
- **Async set with acknowledgement** — `Set{Number,Switch,Text}ValueAsync` send a property and await the driver's state transition (Busy/Ok/Alert/Idle). These carry guards for INDI's quirks: 1-second timestamp resolution (stale Ok/Alert detection via a `DesiredReached` predicate), drivers that ack a successful disconnect with `state=Idle`, and shared drivers already connected via another interface.
- **Raw LX200 escape hatch** — `CommandBlind/Bool/String` open a persistent raw TCP socket to the mount for LX200 commands when the device is in TCP mode.

Subclasses override `GetRequiredConnectionProperties`, `OnPreConnect`, and the `On*PropertyUpdated` hooks to add device-specific behavior; they read/write INDI properties through the base helpers rather than touching the socket.

## Concurrency Notes

- `_lock` guards the global property store and routing tables (held while `ProcessElement` mutates state).
- `_driverLock` guards driver load/unload and the discovered-devices table.
- `_getDriversSemaphore` serializes `GetDevices` so concurrent enumerations can't race driver load/unload.
- `_operationLock` serializes socket writes; per-device `_asyncOperationsLock` guards the pending-async-operation map.
- `ProcessXmlMessage` processes elements strictly sequentially, in wire order. INDI is a stateful, ordered stream (a `defXxxVector` must be applied before the `setXxxVector` that follows it in the same batch; consecutive coordinate updates must apply oldest-first), so do not parallelize this loop.

When adding behavior, respect which lock owns which state; the comments in `INDIClient.cs` document several non-obvious invariants (e.g. why driver eviction is scoped per NINA device-type category even when two categories share an INDI interface bit).

## Dependency Position

`NINA.INDI` is a transport/protocol/device library, not a feature-composition layer:

- It references only `NINA.Core` and `NINA.Astrometry`.
- It is consumed only by `NINA.Equipment`.
- It has no WPF or `IProfileService` dependency; connection settings are passed in via `ConfigureConnectionProperties` rather than read from a profile here.

## Contribution Notes

- Keep XML parsing/serialization inside `Protocol/`; keep the shared `INDIProperty` model the single source of truth and pass the same property reference to store and devices.
- Maintain the `IINDIDevice` / per-type interfaces when adding capabilities; `NINA.Equipment` depends on them.
- Do not add WPF or profile-setting dependencies here — keep the project portable and headless-friendly.
- The server lifecycle assumes a Linux host with `indiserver`, `mkfifo`, and `pkill` available; treat that as a platform constraint.
- This project participates in the pins INDI control panel and INDI equipment integrations; cross-check the related pins project notes and `NINA.Equipment` when changing the property store or device adapters.
