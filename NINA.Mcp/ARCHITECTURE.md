# NINA.Mcp Architecture

`NINA.Mcp` and `NINA.Mcp.Adapter` add an MCP (Model Context Protocol) server to pins, so that AI clients such as Claude Code can read the equipment state and drive the observatory: connect devices, slew, capture, focus, guide, and build, load, start and stop advanced sequences. Both projects are pins-specific and not part of upstream N.I.N.A.

## Projects

| Project | Responsibility | Depends on |
| --- | --- | --- |
| `NINA.Mcp` | MCP protocol (JSON-RPC 2.0, lifecycle, Streamable HTTP endpoint, sessions, Origin and token checks), background jobs, the tool catalog and the `IPinsObservatory` abstraction | BCL and the ASP.NET Core shared framework only; no NINA project, no NuGet package |
| `NINA.Mcp.Adapter` | `NinaObservatory` and `NinaSequenceService`: `IPinsObservatory` on the NINA mediators, image history, autofocus factory and sequencer; `AddPinsMcpForNina` | `NINA.Mcp` and the NINA libraries |
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

## Contracts

- **Security.** Requests without an `Origin` header (non-browser clients) are accepted. Browser origins must be loopback, a private, link-local or CGNAT (Tailscale) address, a `.local` name, the machine name, or listed in `PINS_MCP_ALLOWED_ORIGINS`; everything else gets 403 (DNS-rebinding protection). Without a token the endpoint is read-only unless `PINS_MCP_READONLY=0` is set explicitly; pins then logs a warning at startup.
- **Protocol.** Versions `2025-11-25`, `2025-06-18` and `2025-03-26`; JSON responses only (no SSE stream); batches only for `2025-03-26` and at most 16 messages; `Mcp-Session-Id` sessions expire after one idle hour.
- **Jobs.** Slews, captures, cooling, focusing, filter changes, guiding, connections and switches run as jobs that outlive the request. A tool waits up to `wait_seconds` (default 30, at most 55 to stay below common 60 s client timeouts) and otherwise returns the job id for `get_job`. A job claims one resource (`mount`, `camera`, `camera_cooler`, `focuser`, `filter_wheel`, `guider`, `switch` or a device slot) atomically and may require others to be idle. Cancelling a job cancels its token, which halts the hardware operation.
- **Sequence guard.** While the advanced sequence runs, hardware and editing tools are refused unless `force: true` is passed; `load_sequence`, `clear_targets`, `reset_sequence` and `start_sequence` are always refused. `start_sequence` is also refused while an MCP job holds hardware. `stop_all` is never refused.
- **Adapter rules.** Expected failures are returned as `OperationResult`, not thrown. Results are verified against the device state where NINA does not report failures (tracking, filter position, switch value). An idle mount gets no abort command, because some drivers stop tracking on abort. Model-facing numbers are formatted with the invariant culture.
- **Upstream merges.** The only edits to existing upstream files are one `ProjectReference` in `NINA/NINA.csproj` and the solution entries; the wiring lives in the pins-only `NINA/Program.cs`.

## Verification

`NINA.Mcp.Test` covers `NINA.Mcp` completely through the fake observatory, including HTTP tests against a real Kestrel host. `NINA.Mcp.Adapter` is compiled with `NINA/NINA.csproj`; its behavior depends on real devices and is verified on hardware or with the device simulators.
