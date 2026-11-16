# Workspace integration

The native desktop client and browser workspace both use the gateway in `preview/server.mjs`. It starts the Python service through `packaging/native_service.py` in development, or the packaged `EEEngine` executable in a Windows installation. The browser uses same-origin `/api/` requests and `/api/ws`. The native client sends requests to the selected workspace and checks its identity on switch-scoped operations.

The gateway creates a session token, supplies it through private process input, and adds it to forwarded engine requests. The engine binds to a dynamically assigned loopback port. Connection secrets stay on the service side; the workspace submits login forms without placing credentials in URLs or browser storage.

## Editing and applying

Lighting controls edit a local draft. Choosing an effect, dragging a slider, or changing a color updates the preview. **Apply changes** sends the draft to the engine; **Discard** restores the applied state. Profiles are selected before their separate apply action.

REST mutations take effect when the engine accepts them. The client serializes dependent requests and retains failed or unapplied draft values for recovery. Other clients receive the resulting state through WebSocket messages. See [API.md](API.md) for individual request contracts.

| Workflow | Engine requests |
| --- | --- |
| Load session and lighting | `GET /api/session`, `/api/state`, `/api/ports`, `/api/presets` |
| Connect a switch | `POST /api/session/login` |
| Connect with a saved login | `POST /api/session/restore` |
| Recover an interrupted connection | `POST /api/session/recover` |
| Set individual colors | `PUT /api/ports` |
| Return a port to group lighting | `DELETE /api/ports/{port}` |
| Apply a group effect | `PUT /api/zones/{id}/effect` |
| Update effect settings | `PATCH /api/zones/{id}/params` |
| Apply an edited scene or rule priority | `GET /api/scene`, then `PUT /api/scene` with the expected revision and output confirmation |
| Test rules for a port and time | `POST /api/rules/test` |
| Save, edit, delete, or run a weekly schedule | `/api/schedules`, `/api/schedules/{id}`, `/api/schedules/{id}/run` |
| Change default brightness | `PUT /api/brightness` |
| Save a profile or preview | `POST /api/presets` |
| List or edit profile records | `GET /api/profiles`, `PATCH /api/profiles/{id}` |
| Apply a saved profile | Read its revision and layout, then commit its scene with `PUT /api/scene` |
| Release or resume output | `POST /api/release`, `POST /api/resume` |

Effect selection can request `clear_overrides: true` for its group. The engine accepts the effect and clears that group's manual colors together. Port membership, optical indicators, and available controls come from the session's qualified topology.

## Rendering and telemetry

Connected port previews use the engine's frames and playback metadata. `preview/port-animation.mjs` and `preview/effect-renderer.mjs` render the visual feedback between updates. Gallery previews use the current color settings; illustrative traffic in a gallery is separate from measured port traffic.

UniFi Network enriches port names and telemetry after the switch connects. The engine remains the source for link, power, rule ownership, and output state. Session generation and workspace identity scope pending drafts and selections so a response from an older connection cannot replace the current one.

The effect catalog includes parameter types, ranges, units, choices, independent color brightness, and random-color companions. `tools/extract_catalog.py` reads the backend definitions to generate `src/catalog.mjs`. Fixtures under `tests/fixtures/` compare deterministic preview behavior with backend-rendered output.

## Session and storage

The workspace subscribes before restoring initial state so delayed connection results reach the login screen. Connecting, recovery, pending handoff, and failure states have separate handling. A successful HTTP response alone does not establish that cleanup or a physical lighting change occurred; use the corresponding engine result and device acceptance checks.

Source mode starts in an isolated simulator by default. Live mode uses the existing engine settings and credential store. Gateway preferences, engine data, and simulator data have separate locations described in [DEVELOPMENT.md](DEVELOPMENT.md#data-and-generated-files).

Closing the web session uses `/api/workspace/close` to coordinate engine shutdown and private access cleanup. The gateway's Tailscale management routes are local administration endpoints. Remote access goes through the private gateway's access checks before reaching the workspace. When an enabled private address cannot be restored at startup because Tailscale is not connected yet, the gateway repeats the read-only startup check with increasing delays (5 seconds up to 5 minutes) until the address is ready or the setting is turned off. A retry never changes the Tailscale Serve configuration. The private access setting has an `audience`: `owner` (default) admits only the Tailscale account that set it up, and `accounts` admits any account identity that Tailscale Serve reports for a tailnet request. `PUT /api/private-access` accepts `audience` with `enabled`; the status reports it. A tagged client can also connect when the workspace owner approves that individual device on the Windows host (see the [desktop guide](../desktop/README.md#connect-a-tagged-tailscale-device)). The host binds its approval to the Tailscale identities of both the workspace host and the selected device, and verifies the incoming device address with Tailscale WhoIs. Each approval is independent. Revoking one device leaves the others and the chosen account audience in place. Local administration routes remain local for all audiences and devices, and the gateway refuses Funnel requests.

## Desktop host

The Windows installer launches the Avalonia window and a bundled EE gateway. `ServiceHost` verifies the gateway’s live or demo mode before connecting. The native shell owns navigation, the tray, window controls, sign-in and switch-scoped editor state. Hiding the window pauses its live frame subscription. Opening it again refreshes the workspace. Quitting an owned service calls the confirmed workspace-close path.

The standalone macOS and Linux clients use the same pages with an existing workspace service. Changing the workspace address first verifies the target's workspace response. Quitting a client connected by address leaves that service running. Client startup and packaging commands are in the [desktop guide](../desktop/README.md).

The alternative WPF client remains in the repository as a separate consumer. Its source and build paths are described in [DEVELOPMENT.md](DEVELOPMENT.md#retained-interfaces).

## Verification

Run the JavaScript and Python suites from the same checkout. Exercise draft, apply, failure, reconnect, and profile persistence through the isolated simulator when changing contracts. Package verification checks source modules, web assets, third-party notices, and private-file exclusions. Physical switch output and installed application lifecycle require their own checks.
