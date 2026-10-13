# NINA.Mcp Architecture

`NINA.Mcp` and `NINA.Mcp.Adapter` add an MCP (Model Context Protocol) server to pins, so that AI clients such as Claude Code can read the equipment state and drive the observatory: connect devices, slew, plate solve and center, capture, focus, guide, move the rotator, flat panel and dome, and build, load, start and stop advanced sequences. For planning and diagnosis they also expose target planning for a night, the internal guider's statistics, Guiding Coach reports and flight recorder incidents, and the pins log. Both projects are pins-specific and not part of upstream N.I.N.A.

## Projects

| Project | Responsibility | Depends on |
| --- | --- | --- |
| `NINA.Mcp` | MCP protocol (JSON-RPC 2.0, lifecycle, Streamable HTTP endpoint, sessions, Origin and token checks), background jobs, the tool catalog, the `IPinsObservatory` abstraction, and the pure helpers behind it: `PropertyBags` (DTO to JSON-friendly bag), `PinsLogReader` (backwards log tail) and `NightPlanner` (twilight, transit and windows from sampled altitudes) | BCL and the ASP.NET Core shared framework only; no NINA project, no NuGet package |
| `NINA.Mcp.Adapter` | `NinaObservatory` and `NinaSequenceService`: `IPinsObservatory` on the NINA mediators, image history, autofocus factory, plate solver factory, dome follower, astrometry and sequencer; `AddPinsMcpForNina` | `NINA.Mcp` and the NINA libraries |
| `NINA.Mcp.Test` | NUnit tests of `NINA.Mcp` (protocol, transport, jobs, every tool against a fake observatory) | `NINA.Mcp` |

The protocol subset is implemented by hand instead of using the MCP C# SDK, which keeps `NINA.Mcp` free of dependencies (no license inventory changes) and testable without NINA.

## Runtime

`NINA/Program.cs` already runs an ASP.NET Core `WebApplication` on `0.0.0.0:4782` for the SignalR hubs. The MCP endpoint is mapped on the same host:

1. `McpServerOptions.FromEnvironment(CoreUtil.Version)` reads the `PINS_MCP_*` variables.
2. `builder.Services.AddPinsMcpForNina(options)` registers the server, the tools, the adapter and a log forwarder into the pins log. It registers nothing when the endpoint is disabled.
3. `app.MapPinsMcp()` maps `POST`, `GET` (405) and `DELETE` at `/mcp`.

The adapter's NINA services come from the same service collection (`IoCBindings`). Everything is a lazily resolved singleton, so a broken registration fails the MCP request (500), not the pins start.

Request flow: HTTP → `McpHttpEndpoint` (Origin, token, session, size and header checks) → `McpDispatcher` (JSON-RPC, `tools/list`, `tools/call`) → `ObservatoryTool` (argument validation, sequence guard, job start) → `IJobManager` (background work outliving the request) → `NinaObservatory` → NINA mediators.

## Configuration

| Variable | Default | Effect |
| --- | --- | --- |
| `PINS_MCP_ENABLED` | on | `0`/`false` disables the endpoint completely |
| `PINS_MCP_TOKEN` | unset | Requires `Authorization: Bearer <token>` on every request |
| `PINS_MCP_READONLY` | on without token, off with token | Read-only lists only queries plus `stop_all` and `cancel_job` |
| `PINS_MCP_ALLOWED_ORIGINS` | unset | Extra browser origins (exact scheme://host:port), comma separated |

Set them with a systemd drop-in (`sudo systemctl edit pins`):

```ini
[Service]
Environment="PINS_MCP_TOKEN=<long random string>"
```

Connect Claude Code from a machine in the same network:

```bash
claude mcp add --transport http pins http://<pins-host>:4782/mcp --header "Authorization: Bearer <token>"
```

The endpoint is plain HTTP, so the token can be read by anyone who can sniff the network (an open access point at a star party). Use it on a trusted network, or reach pins through Tailscale or an SSH tunnel (`ssh -L 4782:localhost:4782 <pins-host>`, then `http://localhost:4782/mcp`).

## Contracts

- **Security.** Requests without an `Origin` header (non-browser clients) are accepted. Browser origins must be loopback, a private, link-local or CGNAT (Tailscale) address, a `.local` name, the machine name, or listed in `PINS_MCP_ALLOWED_ORIGINS`; everything else gets 403 (DNS-rebinding protection). Without a token the endpoint is read-only unless `PINS_MCP_READONLY=0` is set explicitly; pins then logs a warning at startup.
- **Protocol.** Versions `2025-11-25`, `2025-06-18` and `2025-03-26`; JSON responses only (no SSE stream); batches only for `2025-03-26` and at most 16 messages, processed concurrently; `Mcp-Session-Id` sessions expire after 24 idle hours (a client typically starts a sequence and returns hours later), at most 1000 are kept. Protocol-level internal errors carry no exception details; those go to the log.
- **Jobs.** Slews, plate solves, centering, captures, cooling, focusing, filter changes, guiding, connections, switches, rotator moves and flat panel and dome commands run as jobs that outlive the request. A tool waits up to `wait_seconds` (default 30, at most 55 to stay below common 60 s client timeouts) and otherwise returns the job id for `get_job`. A job claims one resource (`mount`, `camera`, `camera_cooler`, `focuser`, `filter_wheel`, `guider`, `switch`, `rotator`, `flat_device`, `dome` or a device slot) and may require others to be idle; claim and idle check are one atomic step. Short operations that are not jobs (`set_tracking`, `start_sequence` until the sequence runs) reserve their resources the same way. Mount motion requires the guider idle (slew and home also the camera); capture, autofocus and plate solving require the mount, rotator and flat device idle. `plate_solve` holds the camera; `center_target` holds the mount and requires camera, guider, filter wheel, focuser, rotator and flat device idle. Rotator moves and flat device commands require the camera idle. Dome commands require nothing idle, so closing the dome is never blocked by another job. Cancelling a job cancels its token, which halts the hardware operation; failing halts are reported by `cancel_job` and `stop_all`. A job whose work ignores the token stays running with `cancelRequested`, and `cancel_job` with `abandon: true` ends it as `abandoned` and releases its resource. At shutdown pins cancels every job and waits up to five seconds before disconnecting equipment.
- **Sequence guard.** While the advanced sequence runs, hardware and editing tools are refused unless `force: true` is passed; `load_sequence`, `clear_targets`, `reset_sequence` and `start_sequence` are always refused. `start_sequence` is also refused while an MCP job holds hardware. `stop_all` is never refused.
- **Adapter rules.** Expected failures are returned as `OperationResult`, not thrown. Results are verified against the device state where NINA does not report failures (tracking, filter position, switch value). An idle mount gets no abort command, because some drivers stop tracking on abort; a slew, park or home started over MCP counts as moving from the moment it is sent, because the cached `Slewing` flag lags by a polling interval. Park and home stop guiding first, like their sequence instructions. Capture and autofocus hold the camera capture block (as the flat wizard, framing and snapshot tools do) unless the advanced sequence runs. Captures are stretched and star-detected with the profile's image settings, like sequence light frames. Plate solving and centering use the profile's plate solve settings and NINA's capture and centering solvers like the SolveAndSync and Center instructions (centering stops guiding first and restarts it afterwards when it was running; its correction slews count as mount motion for `stop_all`), and hold the camera capture block like captures. Rotator, flat device and dome results are verified against the device state where the mediator returns no result (flat device). Loading a sequence clears NINA's save path, so NINA's Save cannot overwrite the previously loaded file; linked templates are refreshed on load and materialized before the start validation, as NINA does on start. Model-facing numbers are formatted with the invariant culture.
- **Diagnostics data.** Guider data comes from the internal guider's `IAdvancedGuider`, `IGuidingCoach` and `IGuideIncidentRecorder` contracts and reaches the tools as property bags (`PropertyBags`), so `NINA.Mcp` needs no reference to the DTOs; raw coach samples and per-frame star lists are left out unless asked for, incident images never leave pins. Coach reports and incidents stay readable while the guider is disconnected. Other guiders only report state and RMS.
- **Log.** `read_log` reads the current run's log file (the newest in `~/.local/share/NINA/Logs` whose name carries this process id) or the previous run's, backwards from the end with a byte budget (32 MB), so it is cheap on a long night log. Levels, text and age filters apply while scanning; the result says how much was scanned.
- **Planning.** `plan_targets` samples one observing night (local noon to noon, 5-minute steps, UTC stepping across DST) at the profile's site: target altitude and azimuth with NINA's SOFA transform (no refraction), sun and moon with NOVAS, the custom horizon at each azimuth. `NightPlanner` derives twilight, dark hours, transit, limit crossings (linear interpolation between samples), usable dark hours and the longest window; resolution is the sampling step.
- **Upstream merges.** The only edits to existing upstream files are one `ProjectReference` in `NINA/NINA.csproj` and the solution entries; the wiring lives in the pins-only `NINA/Program.cs`.

## Verification

`NINA.Mcp.Test` covers `NINA.Mcp` completely through the fake observatory, including HTTP tests against a real Kestrel host. `NINA.Mcp.Adapter` is compiled with `NINA/NINA.csproj`; its behavior depends on real devices and is verified on hardware or with the device simulators.
