# Workspace integration

The native desktop client and browser workspace both use the gateway in `preview/server.mjs`. It starts the Python service through `packaging/native_service.py` in development, or the packaged `EEEngine` executable in a Windows installation. The browser uses same-origin `/api/` requests and `/api/ws`. The native client sends requests to the selected workspace and checks its identity on switch-scoped operations.

The gateway creates a session token, supplies it through private process input, and adds it to forwarded engine requests. The engine binds to a dynamically assigned loopback port. Connection secrets stay on the service side; the workspace submits login forms without placing credentials in URLs or browser storage.

The gateway accepts requests addressed to `127.0.0.1` or `localhost` on its own port. It answers 403 `forbidden` to a request addressed to another host name and to a request from another website's page (a foreign `Origin`, or an API request whose `Sec-Fetch-Site` is not `same-origin` or `none`), and 413 `too_large` to an API request body over 1 MiB, in the engine's error shape. A WebSocket upgrade that fails the same checks is closed. The engine makes its own checks of the same kind. When the engine cannot be reached, a forwarded request gets 503 with `The EE service is unavailable.`

## Editing and applying

Lighting controls edit a local draft. Choosing an effect, dragging a slider, or changing a color updates the preview. **Apply changes** sends the draft to the engine; **Discard** restores the applied state. Profiles are selected before their separate apply action.

REST mutations take effect when the engine accepts them. The client serializes dependent requests and retains failed or unapplied draft values for recovery. Other clients receive the resulting state through WebSocket messages. See [API.md](API.md) for individual request contracts.

| Workflow | Engine requests |
| --- | --- |
| Load session and lighting | `GET /api/session`, `/api/state`, `/api/ports`, `/api/presets` |
| Connect a switch | `POST /api/session/login` |
| Choose the lighting of a switch without saved lighting | `POST /api/session/scene-choice` |
| Connect with a saved login | `POST /api/session/restore` |
| Accept a changed SSH key on the computer running EE | `POST /api/session/host-key/replace`, then the login or restore again |
| Recover an interrupted connection | `POST /api/session/recover` |
| Return the switch to UniFi | `POST /api/session/handoff` |
| Read or change the fans | `GET /api/fans`, `PUT /api/fans` |
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

Source mode starts in an isolated simulator by default. Live mode uses the existing engine settings and credential store. It needs the Windows application instance lock, so live mode runs on Windows only. On macOS and Linux only the simulator works; with `EE_PREVIEW_LIVE=1` the engine cannot take the lock and the gateway stops at startup. Gateway preferences, engine data, and simulator data have separate locations described in [DEVELOPMENT.md](DEVELOPMENT.md#data-and-generated-files).

Closing the web session uses `/api/workspace/close` to coordinate engine shutdown, private access cleanup and the LAN listener. The gateway answers its Tailscale settings, LAN access settings and workspace close routes itself.

## Administration access

Local processes can use the workspace and engine routes through `127.0.0.1:4180`. Changing the Tailscale or LAN access settings and closing the workspace also need the per-launch administration key: the desktop app creates it, passes it to the gateway in `EE_ADMIN_KEY` and sends it in the `X-EE-Admin-Key` header. A browser gets administration access through a one-use link that the desktop app requests for **Open in browser** or that a source run prints; the gateway exchanges the link's code for cookies whose value is separate from the key. Other tabs show these settings read-only. The engine never receives the key or the cookies, and the Tailscale and LAN gateways never forward them or return `Set-Cookie`. [API.md](API.md#local-gateway-administration) has the details.

## Remote access

Devices on other computers reach the workspace through the Tailscale gateway or the LAN gateway, which forward to the local gateway. Both are off until the owner turns them on and run only in live mode, where they start with the gateway, before any switch is connected. Each forwards only the routes marked remote in `preview/remote-routes.mjs`: the list is default deny, the query is ignored and paths are matched after decoding. Switch login, saved-login removal, SSH key changes, UniFi Network setup, the Tailscale and LAN access settings, workspace close and any unlisted route stay local for all audiences and devices. `POST /api/session/host-key/replace` is local in two ways: the gateways refuse it, and the engine refuses a caller whose address is not a loopback address. `POST /api/session/forget-host-key` no longer exists. A client offers Accept new key only when `GET /api/private-access` returns `local: true`, which the local gateway sends with or without administration access and the remote gateways never send. `GET /api/session` reports `ssh_host_key` and `ssh_host_key_type` for the connected switch.

Tailscale is optional. While private access is off, the gateway runs no Tailscale command until someone turns private access on, changes its port or starts the Tailscale sign-in. The one exception is a route from an earlier setting that EE has not yet seen removed: the gateway reads `tailscale serve status` at startup, on refresh and when it stops until a startup check or refresh sees that route gone and records that in `private-access.json`. At start, on each scheduled retry and on refresh or sign-in, EE adds its own Tailscale Serve route only on a port with no Serve configuration, and it never changes other Serve configuration. When an enabled private address cannot be restored at startup because Tailscale is not connected yet, the gateway repeats the startup check with increasing delays (5 seconds up to 5 minutes) until the address is ready or the setting is turned off. When the gateway stops (Quit, the installer's `--quit`, Close web session), it removes exactly EE's route within 4 seconds after the lighting is returned, or after a Quit that could not confirm the return, and keeps the setting. An unconfirmed removal is logged as `private_route_cleanup_unconfirmed` and handled at the next start or at uninstall.

The private access setting has an `audience`: `owner` (default) admits only the Tailscale account that set it up, and `accounts` admits any account identity that Tailscale Serve reports for a tailnet request. `PUT /api/private-access` accepts `audience` with `enabled`; the status reports it. A tagged client can also connect when the workspace owner approves that individual device on the Windows host (see the [desktop guide](../desktop/README.md#connect-a-tagged-tailscale-device)). The host binds its approval to the Tailscale identities of both the workspace host and the selected device, and verifies the incoming device address with Tailscale WhoIs. Each approval is independent. Revoking one device leaves the others and the chosen account audience in place. The gateway refuses Funnel requests.

LAN access is off by default. When the owner turns it on, the LAN gateway listens on port 4280 (or the chosen port) on every IPv4 interface and keeps only connections between a private address of this computer and a private address elsewhere. Each device pairs with a code shown on the computer running EE and then sends its own key in a cookie; the desktop client keeps that key in `workspace-keys.json` and sends the cookie itself. The connection is plain HTTP and is not encrypted, and both clients say so. The desktop client refuses to send switch login and UniFi Network setup requests to a LAN address. [API.md](API.md#lan-access) describes the listener, pairing and the saved settings.

## Desktop host

The Windows installer launches the Avalonia window and a bundled EE gateway. `ServiceHost` verifies the gateway’s live or demo mode before connecting. The native shell owns navigation, the tray, window controls, sign-in and switch-scoped editor state. Hiding the window pauses its live frame subscription. Opening it again refreshes the workspace. Quitting an owned service calls the confirmed workspace-close path. Settings → About reads `GET /api/about` for the service version and disclaimer.

When the engine exits outside a workspace close, the gateway closes its listener and exits with code 1. The desktop window then shows that the service stopped and offers to start it again, and `desktop.log` records the exit code and the last output lines. A gateway started by hand becomes unreachable in the same case. Run from a source checkout, the desktop app starts the checkout's gateway with Node.js from the `PATH` and passes `EE_PREVIEW_PYTHON` through to it; a packaged app ignores that variable.

The standalone macOS and Linux clients use the same pages with an existing workspace service. Changing the workspace address first verifies the target's workspace response. Quitting a client connected by address leaves that service running, and such a client offers no workspace close. Client startup and packaging commands are in the [desktop guide](../desktop/README.md).

## Verification

Run the JavaScript and Python suites from the same checkout; `tools/verify.ps1` runs them together with the contract harness, the desktop checks and the browser smoke test (see [WORKFLOW.md](WORKFLOW.md#local-verification)). Exercise draft, apply, failure, reconnect, and profile persistence through the isolated simulator when changing contracts. Package verification checks source modules, web assets, third-party notices, and private-file exclusions. Physical switch output and installed application lifecycle require their own checks.
