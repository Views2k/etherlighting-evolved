# EE API

This document describes the API used by the EE workspace and other clients.

- `npm start` serves the workspace at `http://127.0.0.1:4180` and forwards `/api` and `/api/ws` to its private
  engine service. See [INTEGRATION.md](INTEGRATION.md) for the gateway and session flow. It also prints a one-use
  administration link (see [Local gateway administration](#local-gateway-administration)). The desktop app starts
  the same gateway on the first free port from 4180, or from 4190 for the demo, trying 20 ports.
- Devices on Tailscale and paired devices on the LAN reach that gateway through the Tailscale gateway and the LAN
  gateway, which forward only the requests listed for remote use (see [Remote access](#remote-access)).
- The separate CLI server, `etherlighting-evolved serve`, listens on `http://127.0.0.1:8787` by default and serves
  interactive OpenAPI documentation at `/docs`; its page at `/` links there. That documentation belongs to the CLI
  server, not the workspace gateway.
- Request and response bodies are JSON. Colors are `"#rrggbb"` strings. The server also accepts `"f0f"`, names
  such as `"white"`, `[r, g, b]` and `{"r":..,"g":..,"b":..}`, but always returns `#rrggbb`.
- Ports are numbered from 1, matching the labels on the switch. Use the state `ports` and `sfp` arrays for
  controllable port membership. `hardware_profile` describes the detected physical layout; its stacking
  ports are not lighting targets. Optical-only models have an empty copper `ports` array.
- Times use the local time of the machine running EE, in 24-hour `"HH:MM"` format.

## Offline lighting

The desktop opens the saved switch in the usual Lighting workspace. Apply uses the
normal scene revision and `PUT /api/scene`. On a qualified Pro XG 8 PoE with a
saved login, EE also commits supported scenes to a lighting player on the switch. EE
keeps exclusive live output while connected. When it closes, or its grant expires after
connection loss, the powered switch resumes the committed scene from RAM.
Activity Glow, Live Traffic and Traffic Trails read port counters, PoE Usage reads
switch power measurements, and Daylight follows the switch clock. The base effects
continue offline. EE evaluates automation rules and schedules only while it runs.

`GET /api/session` reports the player in `switch_playback`: `null` without a player record, `playing` while EE's
player runs on the connected switch, `last_confirmed` when EE started with a record from an earlier run, and
`handoff` while Handoff to UniFi stops the player. `offline_lighting_error` is the message shown when the switch has
not taken the current scene, or `null`. [`POST /api/session/handoff`](#post-apisessionhandoff) stops the player and
returns lighting to UniFi.

## UI flow

State and frame messages include `uplink_bandwidth`, measured from the existing physical-port byte counters.
Upload is transmitted traffic and download is received traffic across the detected active uplink ports.
Each successful port-status read supplies a counter sample. Once two compatible samples are available,
the next workspace frame includes their measured rate. `sample_period_ms` records the actual time between
captures, including switch response time. A missed read leaves the last measurement at its original capture time.
Each physical uplink is counted once, including optical uplinks. Virtual aggregation rows are excluded.

| Field | Meaning |
| --- | --- |
| `status` | `waiting`, `ready`, `stale`, `no_uplink`, or `unavailable` |
| `ports` | Physical uplink port IDs included in the sample |
| `upload_bps`, `download_bps` | Directional bits per second, or `null` when a reading is unavailable |
| `history` | Measured intervals from the last minute as `[offset_ms, upload_bps, download_bps]`, relative to the newest sample. Clears when counters or uplink membership reset. |
| `sample_period_ms`, `sample_age_ms` | Measurement duration and age in milliseconds |
| `sequence` | Increases for each completed measurement |
| `refresh_interval_ms` | Configured delay between port-status polls in milliseconds (default `1000`) |

Counter resets, uplink membership changes, and reconnection start a new measurement window. A reading is
current for ten seconds from capture. Each frame reports its age at the time of the snapshot.
While the same uplink is connected, the panel can keep an expired measurement visible as “Last reading.”
Its peak and chart then describe the minute ending at that measurement. A counter reset, uplink change,
or disconnected session clears the previous readings. A missed port-status sample can age out while the
switch connection stays active.

`POST /api/session/recover` revalidates the current switch and cleans up its owned output before resuming
the retained scene after an interrupted connection. It preserves saved credentials and rejects changed
device identity, changed capabilities, or unconfirmed helper cleanup. State/frame `reconnect_required`
indicates that explicit recovery is needed. Pending handoff must finish before recovery can resume lighting.

1. Subscribe to session messages at `/api/ws` and call `GET /api/session`. If `logged_in` is true, load the
   main screen. Otherwise show the login screen, filled in from `remembered` if present. While `connecting`
   is true, show saved-login progress; a later session message reports completion or an actionable error.
2. The login screen has fields for IP address, SSH username and password, plus a *Save login* option.
   It submits to `POST /api/session/login`. Use saved login calls `POST /api/session/restore` without sending
   credentials from the browser. When the session reports `pending_scene_choice`, ask whether to use the saved
   lighting or start a new setup and send the answer to
   [`POST /api/session/scene-choice`](#post-apisessionscene-choice).
3. Keep the same WebSocket after entering the workspace. It sends the session, the full state, and then a
   frame with every port at the engine's frame rate (see [WebSocket](#websocket-apiws)). Draw the ports from the
   frames.
4. Lighting controls edit a local draft. Color pickers, effect settings and brightness sliders update the preview;
   Apply changes sends the draft through REST. Discard restores the applied view. Selecting a profile leaves
   output unchanged until its Apply profile action. Do not send writes while dragging controls or browsing previews.
5. Scope saved browser drafts and selections by `workspace_id`. Clear the previous workspace's in-memory state
   when it changes. Add the UniFi Network controller after the real switch is connected to enrich its port names.

REST and WebSocket mutations take effect immediately when received; Apply is a browser workflow, not a server
transaction. Serialize dependent requests and retain failed or unapplied draft changes for recovery. Other open
clients receive resulting `state` messages.

Logging in with the host `demo` connects to the built-in simulator, which the login screen can offer as a demo
option.

## Errors

Errors include a code for the UI to check and a message that can be shown to the user:

```json
{ "detail": { "code": "auth_failed", "message": "SSH login failed: wrong username or password" } }
```

Some error objects carry more fields, such as the fingerprints of a changed SSH key or the `schedules` of a profile
in use. A few answers use a plain string as `detail`: the gateway's 503 when the engine cannot be reached
(`"The EE service is unavailable."`), the 503 of the Tailscale and LAN gateways when the local gateway cannot be
reached (`"EE is reconnecting. Try again shortly."`), the 400, 405, 413 and 503 answers of the private access setting
routes, and the 409 of `POST /api/workspace/close`. A request body that does not match a route's model gets FastAPI's
422 with a list of problems in `detail`, except on the routes listed under `invalid_credentials`. Some saved-library
and port-name refusals carry `message` without `code`.

| Status | Code | Meaning |
|---|---|---|
| 400 | `missing_host`, `missing_username`, `bad_port` | Login form incomplete |
| 400 | `invalid` | Invalid request; the message gives the reason |
| 400 | `invalid_settings` | The engine settings or the saved lighting setup could not be applied at login |
| 400 | `invalid_scene_choice` | `choice` is not `use_saved`, `start_new` or `cancel` |
| 400 | `scene_revision_required` | `POST /api/presets` has a `scene` without `expected_revision` |
| 401 | `auth_failed` | Wrong SSH username or password |
| 401 | `bad_token` | API token missing or wrong (only when the server has one set) |
| 403 | `forbidden` | A browser request from another website, or a `Host` other than `127.0.0.1` or `localhost` at the local gateway or a local server |
| 403 | `admin_required` | This local gateway action needs the per-launch administration key, or a browser opened from the desktop app or from the printed administration link |
| 403 | `local_access_required` | Only the computer running EE can make this request: the Tailscale and LAN gateways refuse it, and `POST /api/session/host-key/replace` also refuses a caller whose address is not a loopback address |
| 403 | `private_access_denied` | The Tailscale identity is not approved, or the path cannot be opened through private access |
| 403 | `lan_access_denied`, `lan_pairing_required`, `lan_pairing_failed` | LAN access refusals; see [LAN access](#lan-access) |
| 404 | `not_found` | No such zone, rule, preset, schedule, history entry or paired device |
| 409 | `not_logged_in` | Not logged in to a switch |
| 409 | `workspace_changed` | The `X-EE-Workspace` header names another switch session than the current one |
| 409 | `handoff_unconfirmed` | A Handoff to UniFi has not been confirmed; lighting changes and connections wait for it |
| 409 | `handoff_save_failed`, `handoff_recovery_required` | See [`POST /api/session/handoff`](#post-apisessionhandoff) |
| 409 | `no_saved_login`, `saved_login_unavailable` | `POST /api/session/restore` finds no saved login, or the saved login cannot be read |
| 409 | `output_recovery_required` | An earlier lighting session on this switch has no confirmed shutdown, and its recovery did not finish |
| 409 | `output_unconfirmed` | The switch did not confirm a lighting or SFP output change |
| 409 | `scene_changed` | The scene revision in the request is not the current one |
| 409 | `scene_choice_expired`, `scene_choice_stale`, `saved_scene_unavailable` | See [`POST /api/session/scene-choice`](#post-apisessionscene-choice) |
| 409 | `host_key_changed` | The switch's SSH key differs from the saved key; nothing but the SSH key exchange was sent |
| 409 | `host_key_stale` | The SSH keys in a key replacement are not the saved key and the key EE last saw at that address |
| 409 | `host_key_in_use` | EE still has an open switch connection, so a changed SSH key cannot be accepted yet |
| 409 | `switch_playback_reserved` | Switch playback is saved for a switch; also returned by `POST /api/session/host-key/replace` |
| 409 | `not_simulated` | Simulator controls require a demo login |
| 409 | `fans_unconfirmed` | The switch did not confirm a fan change; the previous fan settings are kept |
| 409 | `history_unavailable` | Lighting history cannot be read for this session |
| 409 | `profile_storage_unavailable`, `schedule_storage_unavailable` | Saved profiles or schedules cannot be read or saved, or were saved by a newer version of EE |
| 413 | `too_large` | The request body is larger than 1 MiB, or than 1 KB for a LAN access setting or pairing |
| 422 | `invalid_credentials` | The fields of `POST /api/session/login`, `POST /api/session/host-key/replace`, `POST /api/controller/connect`, `POST /api/controller/consoles` or `PUT /api/controller/preferences` were not accepted |
| 422 | `no_etherlighting` | Logged in, but the device has no Etherlighting |
| 422 | `compatibility_verification_required` | The switch was identified, but its model or firmware build has not been reviewed for lighting output |
| 422 | `invalid_saved_scene` | The saved lighting for this switch cannot be used; see [`POST /api/session/login`](#post-apisessionlogin) |
| 500 | `saved_login_failed` | Saving failed; sign in with Save login turned off |
| 500 | `saved_login_forget_failed` | The saved login could not be removed |
| 500 | `scene_save_failed` | The lighting setup for the switch could not be saved |
| 500 | `host_key_save_failed` | The saved SSH keys could not be read or updated |
| 500 | `fan_settings_save_failed` | The switch runs the new fan setting, but EE could not save it on this PC |
| 502 | `unreachable`, `ssh_error` | The switch couldn't be reached, or SSH failed |
| 503 | `offline_scene_unconfirmed` | The lighting is applied in EE, but the switch has not confirmed its offline copy |
| 503 | `port_names_unavailable` | Port names cannot be read or saved, or were saved by a newer version of EE |
| 503 | `workspace_closing` | EE is closing: `POST /api/workspace/admin-link` and LAN access changes |

The [private access](#get-apiprivate-access), [LAN access](#lan-access),
[UniFi Network controller](#unifi-network-controller), [profile](#stable-profile-records),
[schedule](#schedules) and [saved library](#saved-effect-settings-and-port-selections) sections list the codes of
their own requests.

### Request checks

If the server has an API token, send `Authorization: Bearer <token>` with every request and with the WebSocket
upgrade. The workspace gateway creates the engine's token at each start and adds it to the requests it forwards, so
clients of the gateway send no token.

The engine refuses with `403 forbidden` a change (any method other than `GET`, `HEAD` and `OPTIONS`) or a WebSocket
connection whose `Origin` is another site, and a request to `/api/` that carries `Sec-Fetch-Site: cross-site` or
`same-site`, unless the `Origin` is the server itself or is listed in `cors_origins`. Requests without these headers,
such as those from scripts and the desktop app, are not affected. When the server listens only on this computer, it
also refuses a `Host` other than `localhost`, `127.0.0.1` or `::1`. A refused WebSocket is closed with code 4403.

Request bodies are limited to 1 MiB. `PUT /api/zones` accepts at most 64 zones, and `PUT /api/rules`,
`PUT /api/rules/order` and `POST /api/rules/test` at most 256 rules; longer lists return 422. `POST /api/rules`
returns `400 invalid` when 256 rules already exist, and `PUT /api/scene`, history restore, `POST /api/presets` with a
`scene`, and `POST /api/presets/{name}/apply` return `400 invalid` for a scene with more than 256 rules. In
`POST /api/session/login`, a `host` longer than 253 characters, a `username` longer than 256 or a `password` longer
than 4096 returns `422 invalid_credentials`.

A request that sends `X-EE-Workspace` with the `workspace_id` from `GET /api/session` is refused with
`409 workspace_changed` when the connected switch changed in the meantime. Every request that works on the connected
switch checks the header, and so do logout, handoff, `PATCH /api/profiles/{profile_id}` and the schedule changes.

### SSH host keys

The first connection to an address that has no saved SSH key signs in with the username and password and then
saves the key the switch presented (trust on first use). This applies to a login, a saved-login restore, the
automatic login at startup and the engine's reconnect. While a switch is connected, `GET /api/session` reports its
key in `ssh_host_key` and `ssh_host_key_type`, so a user can compare it with what an SSH client shows.

When the switch presents a key that differs from the saved one, EE ends the connection during the SSH key exchange,
before any username or password is sent, and the login, the restore, `GET /api/session` `error` and the WebSocket
`session` message report both keys with HTTP 409:

```json
{ "code": "host_key_changed",
  "message": "The switch's SSH key changed. Check the new fingerprint before signing in.",
  "host": "192.168.1.2", "port": 22,
  "ssh_host_key": "SHA256:Zt4Wc8Hn1Lq6Rv3Xb9Me2Kd7Pj0Ys5Ug8Fa1Ni4Ow7E",
  "ssh_host_key_type": "ssh-rsa",
  "saved_ssh_host_key": "SHA256:qmA9mR0m2rC3k7Yw1xB5nT8fJ4hL6sV0pD2eG9uZ1aQ" }
```

`host` is the trimmed host of the attempt and `port` an integer. Fingerprints have the form that `ssh` and
`ssh-keygen -l` print. `ssh_host_key_type` is `null` when the type is not known. `ssh_host_key` is `null` only
when the SSH library refused the key without asking EE, for example a host certificate; there is then nothing to
accept. `saved_ssh_host_key` is the saved key. A changed key happens after the switch is reset or replaced, or when
another device answers at that address.

To accept a changed key, a client on the computer running EE shows both fingerprints and echoes them back to
[`POST /api/session/host-key/replace`](#post-apisessionhost-keyreplace), then repeats the login or the restore. That
attempt checks the newly saved key during the key exchange. Keys saved by earlier releases are used as they are.
For a headless setup, accept a changed key with `eectl probe` and `--accept-host-key`, which refuses when switch
playback is saved for that switch. `eectl forget-host` removes a saved key; the next connection then saves the key
the switch presents.

## Remote access

Other devices reach the workspace through one of two gateways in the gateway process, both off until the owner turns
them on:

- The Tailscale gateway (private access) listens on `127.0.0.1` behind a Tailscale Serve route on the private HTTPS
  port, 8444 by default. It accepts a request only from Tailscale Serve on this computer, with one `Host`,
  `X-Forwarded-Host` (both the private address), `X-Forwarded-Proto: https` and an `X-Forwarded-For` Tailscale
  address, and without `Tailscale-Funnel-Request`, `Authorization` or `Forwarded`. The identity must be the Tailscale
  account that set up private access, any account in the tailnet when the audience is `accounts`, or a tagged device
  that the owner approved on this computer. Otherwise the answer is `403 private_access_denied` ("Private access
  requires an approved Tailscale account or device.").
- The [LAN gateway](#lan-access) listens on the computer's private IPv4 addresses and accepts paired devices.

Both apply the same `Origin` and `Sec-Fetch-Site` rules as the local gateway: a change or WebSocket upgrade must send
the gateway's own origin, and only a `GET` navigation to a workspace page may come from another site. A path that
contains an encoded slash or backslash, a dot segment, a double slash or a control character is refused before the
route check, with `private_access_denied` or `lan_access_denied`. Each gateway then compares the request's method
and decoded path, without the query and a trailing slash, with the entries in `preview/remote-routes.mjs`. A request
is forwarded only when at least one entry matches and every matching entry has `"remote": true`. Every other `/api`
request, including a route added later without an entry, gets this answer before anything reaches the engine:

```json
{ "detail": { "code": "local_access_required", "message": "This action is available only on the computer running EE." } }
```

The answer is `403` with `Content-Type: application/json`, `Cache-Control: no-store` and
`X-Content-Type-Options: nosniff`. The query string does not change the result, so
`POST /api/session/logout?forget=true` is refused like `POST /api/session/logout`. Workspace pages, scripts, styles
and images allow `GET` and `HEAD`.

These requests are local-only:

| Request | Reason |
|---|---|
| `POST /api/controller/cancel` | UniFi Network setup: cancels a pending site choice |
| `POST /api/controller/connect` | UniFi Network setup: sends and can save controller credentials |
| `POST /api/controller/consoles` | UniFi Network setup: sends an API key to UniFi |
| `POST /api/controller/disconnect` | UniFi Network setup: disconnects the controller |
| `PUT /api/controller/preferences` | UniFi Network setup: saves or removes the controller login |
| `PUT /api/fps` | Output tuning outside the scene; no client sends it |
| `GET /api/lan-access` | LAN access status, pairing code and paired devices on the computer running EE |
| `PUT /api/lan-access` | LAN access settings |
| `POST /api/lan-access/code` | Shows a new pairing code on the computer running EE |
| `DELETE /api/lan-access/devices/{device_id}` | Removes a paired LAN device |
| `POST /api/lan-access/pair` | Answered by the LAN gateway before route checks; never forwarded |
| `PUT /api/output` | Output tuning outside the scene; no client sends it |
| `PUT /api/private-access` | Tailscale settings |
| `POST /api/private-access/login` | Starts Tailscale sign-in on the host computer |
| `POST /api/private-access/refresh` | Runs Tailscale checks on the host computer |
| `POST /api/session/forget` | Deletes the saved switch login |
| `POST /api/session/host-key/replace` | Accepts a changed SSH host key, part of signing in to a reset or replaced switch |
| `POST /api/session/login` | Signs in to a switch or the demo with typed details |
| `POST /api/session/logout` | Ends the switch session; `forget=true` deletes the saved login |
| `PUT /api/simulator/anomaly/{port}` | Demo simulator test control; the demo starts only from a local login |
| `PUT /api/simulator/links/{port}` | Demo simulator test control; the demo starts only from a local login |
| `PUT /api/simulator/poe/{port}` | Demo simulator test control; the demo starts only from a local login |
| `POST /api/workspace/admin-link` | Issues a one-use administration link to the desktop app that started the gateway |
| `POST /api/workspace/close` | Stops the service on the host computer |

Both gateways answer `GET /api/workspace` and `GET /api/private-access` themselves. The private access answer is the
read-only status with `can_configure`, `can_close_workspace` and `can_change_port` false and `setup_action` and
`action_url` null, without `admin_required` and `local`. A WebSocket upgrade is accepted only at exactly `/api/ws`; a
query, trailing slash or encoded form is refused with `403 Forbidden`. Every WebSocket command is available to remote
devices, so an action that must stay on the computer running EE needs a REST route.

A forwarded request carries only `Accept`, `Accept-Language`, `Content-Type`, `Content-Length`, `X-EE-Workspace`, the
cache and range headers and the WebSocket handshake headers, with `Host` and `Origin` set to the local gateway. No
`Cookie`, `Authorization` or `X-EE-Admin-Key` header reaches the local gateway, so an administration link is never
redeemed through a remote gateway, and `Set-Cookie` is removed from every answer. A request body over 1 MiB gets
`413 too_large`.

### `GET /api/workspace`

Answered by the gateway, never by the engine. `live` is true when the workspace controls a real switch and false
for the demo. `remote` is true when the request came through the Tailscale or LAN gateway:

```json
{ "live": true, "remote": false }
```

Through the Tailscale gateway the answer is always `{"live": true, "remote": true}`, because both remote gateways run
only in live mode. The LAN gateway adds `lan` and `paired` (see [Pairing](#pairing-and-the-device-cookie)). When
`remote` is true, clients leave out switch login, Forget saved login and UniFi Network setup.

### `GET /api/last-session`

Answered by the local gateway and forwarded from the remote gateways. It reports how the previous workspace service
ended, for Settings:

```json
{ "available": true,
  "last_session": { "time": "2026-11-16T20:10:12.000Z", "stage": "shutdown", "outcome": "closed", "build": "0.6.0" } }
```

`stage` is `engine`, `shutdown`, `parent_cleanup` or `desktop_exit`, and `outcome` is `closed`, `failed` or
`cleanup_unconfirmed`. `build` is the EE version or `null`, and `attempts` (1 to 10) appears after a desktop
connection loss. `last_session` is `null` when nothing was recorded. `available` is false when the record cannot be
read. Other methods get 405 with an empty body.

## Local gateway administration

The gateway at `http://127.0.0.1:4180` answers the Tailscale settings, LAN access settings and workspace close
requests itself. Changing Tailscale or LAN access settings and closing the workspace need administration access.
Every other request, including `GET /api/private-access` and `GET /api/lan-access`, works without it.

A request has administration access when it carries one of these:

- The header `X-EE-Admin-Key: <key>`. The key is 64 lowercase hexadecimal characters and changes each time the
  gateway starts. The desktop app creates it and passes it to the gateway in the `EE_ADMIN_KEY` environment variable.
  Without that variable the gateway creates its own key. The key is kept in memory only.
- A cookie named `ee_admin_<port>`, for example `ee_admin_4180`, with the value the gateway set for this launch. A
  browser gets it by opening an administration link (`GET /?admin_code=<code>`). Every `ee_admin_<port>` entry in the
  `Cookie` header is checked.

The administration link sets three cookies with the same name and value, one for each path that reads them:

```
Set-Cookie: ee_admin_4180=<value>; Path=/api/private-access; HttpOnly; SameSite=Strict
Set-Cookie: ee_admin_4180=<value>; Path=/api/lan-access; HttpOnly; SameSite=Strict
Set-Cookie: ee_admin_4180=<value>; Path=/api/workspace/close; HttpOnly; SameSite=Strict
```

`<value>` is a random value created once per launch, separate from the key. The cookies have no expiry date, so they
last until the browser session ends, and a gateway started later does not accept them. The port is part of the name
so the demo gateway (4190) and the live gateway (4180) do not replace each other's cookie. Browsers do not separate
cookies by port, so another web server on `127.0.0.1` receives the cookie for requests under those three paths.

The gateway removes `Origin`, `Cookie` and `X-EE-Admin-Key` from every request and WebSocket upgrade it forwards to
the engine. The Tailscale and LAN gateways forward neither `Cookie` nor `X-EE-Admin-Key` and remove `Set-Cookie` from
their answers.

### `GET /?admin_code=<code>`

Opens the workspace and gives the browser administration access. The gateway handles it only for a `GET` without an
`Origin` header, which is how browsers navigate to a page; a `HEAD` request, a request with `Origin`, and requests
through the Tailscale and LAN gateways, which always send `Origin`, open the workspace without redeeming the code.

When the query has exactly one `admin_code` and it is a valid code that has not been used or expired, the code is
used up and the answer is `303 See Other` with `Location: /`, the three cookies, `Cache-Control: no-store`,
`Referrer-Policy: no-referrer` and an empty body. Any other `admin_code` (unknown, used, expired, malformed or
repeated) gets the same answer without `Set-Cookie`, and no code is used. Other query parameters are ignored.

A code is 64 lowercase hexadecimal characters. A code from `POST /api/workspace/admin-link` expires after 120 seconds.
The code in the link that `npm start` prints has no time limit; it ends when it is used or when the gateway stops. At
most eight codes are outstanding; a ninth replaces the oldest.

### `POST /api/workspace/admin-link`

Takes no request body and requires the `X-EE-Admin-Key` header; the cookies do not count. The desktop app uses it for
Open in browser:

```json
{ "code": "<64 lowercase hexadecimal characters>" }
```

The browser then opens `http://127.0.0.1:<port>/?admin_code=<code>`. Errors:

| Status | Answer |
|---|---|
| 403 | `admin_required`: "This request needs the administration key of the EE app that started this workspace." |
| 405 | Empty body, for any method other than `POST` |
| 503 | `workspace_closing`: "EE is closing. Open EE again to use the browser workspace." |

### `GET /api/private-access`

Always answers 200:

```json
{
  "available": true, "enabled": true, "ready": true, "url": "https://host.example.ts.net:8444", "error": null,
  "setup_action": null, "action_url": null, "audience": "owner", "https_port": 8444, "can_change_port": false,
  "can_configure": true, "can_close_workspace": true, "admin_required": false, "local": true
}
```

`enabled` is the saved setting and `ready` is true while the private address works; `url` is that address, or
`null`. `audience` is `owner` (only the Tailscale account that set up private access) or `accounts` (any account in
the tailnet). `https_port` is the Tailscale HTTPS port, and `can_change_port` is true only while private access is
off and no cleanup is pending. `setup_action` names the Tailscale step to take next, `install`, `signin` or
`approve`, and `action_url` is its link or `null`. `local` is true in every answer of the local gateway, with or
without administration access, and tells clients that they run on the computer running EE.

`admin_required` is false with administration access. Without it, the answer is the read-only form that the remote
gateways give: `setup_action` and `action_url` are null, `can_change_port`, `can_configure` and `can_close_workspace`
are false, and `admin_required` is true. A pending Tailscale sign-in link therefore never reaches another local user.

Tailscale is optional. `available` is true once EE has read this computer's Tailscale identity. While private access
is off, EE reads it only after a `PUT` that turns private access on or a sign-in request since EE started, and a
`PUT` that turns it off ends that setup. On a computer without Tailscale where private access was never turned on,
the answer is therefore `available: false`, `enabled: false`, `error: null` and `setup_action: null`, and EE runs no
Tailscale command.

`error` is `null` or `{code, message}`:

| Code | Message |
|---|---|
| `tailscale_not_installed` | Install Tailscale on this computer, sign in, then refresh status. |
| `tailscale_not_running` | Connect Tailscale on this computer, then refresh status. |
| `tailscale_signin_required` | Connect this computer to Tailscale, then check again. |
| `tailscale_approval_required` | Complete the approval in Tailscale, then refresh status and apply the setting. |
| `tailscale_permission_required` | Tailscale needs permission to manage private access. Open EE as an administrator and try again. |
| `tailscale_device_identity_required` | Use a personal Tailscale account on the computer running EE, then refresh status. |
| `tailscale_unavailable` | Open Tailscale on this computer, then check again. |
| `private_access_identity_changed` | The approved Tailscale account or computer identity changed. Turn private access off before setting it up again. |
| `private_access_port_in_use` | Another Tailscale service uses this port. Keep that service unchanged and choose a separate port for EE. |
| `private_access_port_change_requires_off` | Turn private access off and confirm cleanup before changing its port. |
| `private_access_funnel_enabled` | This port is configured for public access. EE requires a private Tailscale Serve address. |
| `private_access_route_unavailable` | The saved private address is unavailable. Apply the private access setting to reconnect it. |
| `private_access_listener_failed` | The private EE connection could not start. Check that its local port is available. |
| `private_access_save_failed` | The private access setting could not be saved. Check the local data folder and try again. |
| `private_access_setup_unconfirmed` | Private access setup could not be confirmed. Its EE connection remains closed; check again before retrying. |
| `private_access_cleanup_unconfirmed` | The private EE connection is closed, but removal of its Tailscale route is unconfirmed. Check again before retrying. |
| `private_access_shutdown_unconfirmed` | Private access shutdown could not be confirmed. Review the local connection before retrying. |
| `private_access_invalid_configuration` | The private access configuration could not be verified. Review its saved settings before retrying. |

When private access is on but cannot be opened because Tailscale is not running, not signed in or not answering yet,
its address is unavailable or its listener could not start, the gateway runs the startup check again after 5, 10, 20 and 40
seconds, 1 and 2 minutes and then every 5 minutes, until the address works or the setting is turned off.

The demo gateway answers `{"available": false, "enabled": false, "ready": false, "can_configure": false,
"can_close_workspace": <access and not closing>, "admin_required": <no access>, "local": true}`. The remote
gateways' answer has no `admin_required` field; clients treat a missing field as false.

Clients show their read-only help for a local tab without administration access when `admin_required` is true and
`https_port` is an integer, which only the live gateway reports. They show the close help in place of Close web
session when `can_close_workspace` is not true and `admin_required` is true.

### `PUT /api/private-access`, `POST /api/private-access/refresh`, `POST /api/private-access/login`

`PUT` takes `{"enabled": true, "https_port": 8444, "audience": "owner"}`; `https_port` (1024–65535) and `audience`
(`owner` or `accounts`) are optional. The other two take no body. Without administration access each answers 403
before the body is read:

```json
{ "detail": { "code": "admin_required", "message": "To change private access, choose Open in browser in the EE tray menu. If EE runs from source, use the administration link it printed when it started." } }
```

With administration access the answer is the status fields above with `can_configure` true and `admin_required`
false, without `can_close_workspace` and `local`. A problem with Tailscale is reported in `error` with a 200 answer.
An invalid setting returns 400, a body over 1 KB returns 413, another method returns 405, a request while EE is
closing returns 503, and an unexpected failure returns 409 with
`{"detail": {"code": "private_access_failed", "message": "Private access could not be updated. Refresh status and try again."}, "status": {...}}`.

Refresh and sign-in repeat the startup check. When private access is on and its HTTPS port has no Tailscale Serve
configuration, that check adds EE's route again, so a refresh after the Tailscale HTTPS approval completes a saved
setup. When a saved setting is off, the check reads the Tailscale Serve status to confirm that EE's route is gone and
reports `private_access_cleanup_unconfirmed` if it is still there; a Serve status that Tailscale cannot give is not
reported. Sign-in runs `tailscale login` only when Tailscale reports that it needs a login.

EE adds its own Serve route only on a port with no Serve configuration, and it never changes other Serve
configuration. Turning private access off removes the route. When the gateway stops (Quit, Close web session or the
installer closing EE), it removes exactly that route within 4 seconds and keeps the setting, so the next start adds
the route again. A removal that cannot be confirmed is recorded as `private_route_cleanup_unconfirmed` in
`service-diagnostics.log`.

### `POST /api/workspace/close`

Returns the switch lighting, stops the engine, ends private access, closes the LAN listener and closes the gateway.
In live mode, ending private access also removes EE's Tailscale Serve route; private access and LAN access stay on in
their settings, and EE opens them again when it next starts. A removal that cannot be confirmed does not make the close
fail.

| Status | Answer |
|---|---|
| 200 | `{"closed": true}` |
| 403 | `admin_required`: "To close the web session, choose Open in browser in the EE tray menu, or quit EE. If EE runs from source, use the administration link it printed when it started." |
| 405 | Empty body, for any method other than `POST` |
| 409 | `{"detail": "The service has not confirmed shutdown. Wait and try again."}` |

A refused request changes nothing, so lighting and private access keep working.

## LAN access

LAN access lets devices on the owner's network open the workspace at the address of the computer running EE, over
plain HTTP and without Tailscale. It is off until the owner turns it on, it runs only in live mode, and each device
pairs with a single-use code shown on the computer running EE. Paired devices get the same route limits as devices on
Tailscale: the LAN gateway compares each request with `preview/remote-routes.mjs` exactly as the Tailscale gateway does.
Tailscale private access and LAN access can both be on; they use separate listeners and separate device lists.

### The unencrypted connection

Traffic between a paired device and the computer running EE is not encrypted. Anyone who can watch or change traffic
on the network can read what crosses it: lighting, fan and handoff commands and scene changes (REST and WebSocket);
workspace data, including the session status (the switch address, SSH port and SSH username, never passwords), the
switch model and port status, port names, the UniFi Network address and site names, device names and telemetry from
UniFi Network when it is connected, profiles, schedules, rules, saved effect settings, fan status and the Tailscale
private address when private access is on; the device's key, in its cookie, with every request; and the pairing code,
once. With a copied key they can do anything a paired device can until that device is removed or LAN access is turned
off, and they can change what a device shows.

LAN access never carries switch passwords and saved logins (`POST /api/session/login`, `/api/session/forget`,
`/api/session/logout` and the host-key routes are local-only, and `GET /api/session` returns no passwords), UniFi
Network passwords and API keys (the controller setup routes are local-only), the administration key or cookie, or
saved key hashes. The desktop client also refuses to send `POST /api/session/login`, `POST /api/controller/connect`,
`POST /api/controller/consoles` and `PUT /api/controller/preferences` to a LAN address, with
`403 local_access_required`, before anything is sent. Use Tailscale for an encrypted connection.

### Listener

The LAN gateway listens on `0.0.0.0:<port>`, IPv4 only, with port 4280 unless the owner chose another from 1024 to
65535. The port is fixed: when it is busy, LAN access reports `lan_access_port_in_use` instead of trying another, so
the addresses saved on devices keep working. The gateway keeps a connection only when it arrived at one of this
computer's current private IPv4 interface addresses (10/8, 172.16/12 or 192.168/16) from a private IPv4 address;
anything else, including Tailscale's 100.64/10 addresses, loopback, link-local, public and IPv6 addresses, is closed
before any HTTP is read. Any private source address is accepted, so devices on other networks that the router routes
to this computer can pair. The gateway accepts at most 64 connections, at most 16 from one address, and request
headers of at most 8 KB.

Each request is then checked:

1. `Host` must be exactly `<address the connection arrived on>:<port>`, and that address must still be one of this
   computer's private addresses. A DNS name, another port or a public address is refused, so a page on a name that
   resolves to this computer cannot use a device's cookie or guess codes.
2. `Host`, `Origin`, `Sec-Fetch-Site`, `Sec-Fetch-Mode` and `Cookie` may appear only once, without control characters,
   and at most 1024 characters long (4096 for `Cookie`). Any `Authorization`, `Forwarded` or `X-Forwarded-*` header is
   refused.
3. `Origin`, when present, must be `http://<Host>`, and changes and WebSocket upgrades must send it. `Sec-Fetch-Site`
   other than `same-origin` or `none` is refused except for a `GET` navigation to a workspace page.
4. The `ee_lan_<port>` cookie identifies a paired device. Without one, only workspace pages (`GET` and `HEAD` outside
   `/api`), `GET /api/workspace` and `POST /api/lan-access/pair` are answered.

Every refusal in steps 1 to 3 is:

```json
{ "detail": { "code": "lan_access_denied", "message": "Open EE at the address shown in LAN access on the computer running EE." } }
```

A path that cannot be classified gets `lan_access_denied` with "This request cannot be opened through LAN access.",
and any other request from a device that is not paired gets:

```json
{ "detail": { "code": "lan_pairing_required", "message": "Pair this device first. Enter the pairing code shown on the computer running EE." } }
```

A paired device's requests then follow the route entries: `GET /api/private-access` gets the same read-only Tailscale
status the Tailscale gateway gives, without `local`, and every request without a remote entry gets
`403 local_access_required`. A WebSocket upgrade is accepted only at exactly `/api/ws` and only for a paired device;
the gateway checks each socket again every 15 seconds and at once when a device is removed, including a socket whose
upgrade the engine has not answered yet. A request body over
1 MiB gets `413 too_large`. The gateway forwards neither `Cookie` nor `X-EE-Admin-Key`, always sends an `Origin`
upstream, so `/?admin_code=` is never redeemed through it, and removes `Set-Cookie` from upstream answers.

Do not forward the LAN port on a router. A router that forwards a public port and rewrites the source address would
pass the connection check; the `Host` check and pairing still apply.

### Pairing and the device cookie

`POST /api/lan-access/code` on the computer running EE shows a code of 6 digits. Only one code is active; a new code
replaces the old one. A code works once and ends after 5 minutes, after 5 wrong attempts, when it is used, or when
LAN access turns off or EE stops. Codes are kept in memory only and never appear in a status without administration
access. Attempts are checked one at a time.

The device sends the code on the LAN gateway. The body is at most 1 KB:

```json
{ "code": "482 913", "name": "Kitchen iPad", "client": "browser" }
```

Spaces and hyphens in `code` are ignored. `name` is trimmed and must have 1 to 40 characters without control, format
or line and paragraph separator characters; duplicate names are allowed. `client` is `browser` (the default) or
`app` and only labels the device in the list. A body that fails these checks gets `400 invalid` and costs no attempt.
A wrong or missing code gets `403 lan_pairing_failed`. A matching code is used up at once; if the device cannot be
saved, the answer is `500 lan_pairing_save_failed` and the owner shows a new code. Success:

```
HTTP/1.1 200 OK
Content-Type: application/json
Cache-Control: no-store
X-Content-Type-Options: nosniff
Referrer-Policy: no-referrer
Set-Cookie: ee_lan_4280=<64 lowercase hex>; Path=/api; Max-Age=31536000; HttpOnly; SameSite=Strict

{ "paired": true, "device": { "id": "3f9c0a1b2c3d4e5f", "name": "Kitchen iPad", "client": "browser", "paired_at": "2026-11-16T20:10:12.000Z" } }
```

The key is never in the body, and the cookie has no `Secure` attribute because the connection is plain HTTP. The
desktop client reads the key from `Set-Cookie` and sends `Cookie: ee_lan_<port>=<key>` itself, also on the WebSocket
upgrade. Every `ee_lan_<port>` entry in the `Cookie` header is compared with every saved key hash, so a decoy cookie
cannot hide the device's own. Browsers send cookies to every port of the same host: `Path=/api` limits the cookie to
API requests, but another service at the same address on a different port receives it for paths under `/api`, as the
administration cookie on 127.0.0.1 does.

A device that pairs again gets a new entry. At most 16 devices can be paired. A device's pairing does not depend on
its own address. When the address of the computer running EE changes, each device opens the new address and pairs
again.

On the LAN gateway, `GET /api/workspace` answers whether this device is paired:

```json
{ "live": true, "remote": true, "lan": true, "paired": false }
```

The local gateway's answer has no `lan` field, and the Tailscale gateway's answer is unchanged.

### Routes on the local gateway

| Request | Access | Body |
|---|---|---|
| `GET /api/lan-access` | Anyone local; administration access gets the full form | none |
| `PUT /api/lan-access` | Administration | `{"enabled": true, "port": 4280}`; `port` (1024–65535) is optional; no other keys; at most 1 KB |
| `POST /api/lan-access/code` | Administration | none |
| `DELETE /api/lan-access/devices/{device_id}` | Administration; `device_id` is 16 lowercase hexadecimal characters | none |

Each answers 200 with the status below. Administration access is the key header or the `ee_admin_<port>` cookie from
an administration link, which also sets that cookie with `Path=/api/lan-access`. Without it, a change gets
`403 admin_required` before the body is read:

```json
{ "detail": { "code": "admin_required", "message": "To change LAN access, choose Open in browser in the EE tray menu. If EE runs from source, use the administration link it printed when it started." } }
```

Another method on these paths gets 405 with an empty body, and any other URL that starts with `/api/lan-access`,
including `/api/lan-access/pair`, gets `404 not_found` ("This LAN access request does not exist.") and never
reaches the engine. In demo mode every request answers `{"available": false, "enabled": false, "ready": false,
"can_configure": false, "admin_required": <no access>}`. These routes are local-only on both remote gateways.

Turning LAN access on opens the listener first and saves the setting only once it is open. Turning it off closes the
listener and every connection, ends the code, removes every paired device and saves the off setting; `PUT` with
`"enabled": false` writes it even when LAN access is already off. The port changes only while LAN access is off.
Removing a device refuses its requests at once, closes its WebSockets before the answer is sent, and then saves the
list. When EE closes, the listener stops and the setting stays, so the next start opens it again; a saved setting
that cannot open its port is reported in `error`, and `PUT {"enabled": true}` retries it.

### Status

With administration access:

```json
{
  "available": true, "enabled": true, "ready": true, "port": 4280,
  "addresses": [ { "url": "http://192.168.1.2:4280", "interface": "Ethernet" } ],
  "error": null,
  "pairing": { "code": "482913", "expires_at": "2026-11-16T20:15:00.000Z", "attempts_left": 5, "ended": null },
  "devices": [ { "id": "3f9c0a1b2c3d4e5f", "name": "Kitchen iPad", "client": "browser",
                 "paired_at": "2026-11-16T20:10:12.000Z", "paired_from": "10.0.0.1",
                 "last_used_at": "2026-11-16T20:14:03.000Z", "last_address": "10.0.0.1" } ],
  "max_devices": 16, "can_configure": true, "can_change_port": false, "admin_required": false
}
```

`addresses` lists each private IPv4 address of this computer's network interfaces, 192.168/16 first, then 10/8, then
172.16/12, with the interface name, as read at that moment; it is empty while LAN access is not ready. Hyper-V, WSL,
Docker and VPN adapters are listed too. `pairing` is null until a code is shown after LAN access turns on; after a
code ends, `code` is null and `ended` is `used`, `expired`, `attempts` or `failed`. `last_used_at` and `last_address`
are kept in memory only and are null until the device makes a request after EE starts. Without administration access,
`pairing` and `devices` are null, `can_configure` and `can_change_port` are false and `admin_required` is true. While
EE is closing, `can_configure` is false and changes get `503 workspace_closing` ("EE is closing. Reopen EE to change
LAN access.").

A failure during an operation is reported in `error` with a 200 answer:

| Code | Message |
|---|---|
| `lan_access_port_in_use` | Another program uses this port. Choose another LAN port in Connection options. |
| `lan_access_listener_failed` | EE could not open its LAN port. Choose another LAN port in Connection options, then try again. |
| `lan_access_save_failed` | The LAN access setting could not be saved. Check the local data folder and try again. |
| `lan_access_turned_off` | The change could not be saved, so EE turned off LAN access and removed every paired device. Check the local data folder, then turn on LAN access again. |
| `lan_access_revoke_unsaved` | EE turned off LAN access and removed every paired device, but could not save this. Paired devices can connect again after EE restarts. Check the local data folder, then apply the setting again with LAN access off. |
| `lan_access_invalid_configuration` | The saved LAN access settings could not be read, so LAN access is off and no devices are paired. Turn it on to start again. |
| `lan_access_port_change_requires_off` | Turn off LAN access before changing its port. |

When turning LAN access off or removing a device cannot be saved, EE turns LAN access off in memory and deletes its
settings file, which reads as off with no devices, so a restart cannot bring the devices back (`lan_access_turned_off`).
If the file cannot be deleted either, the status says so (`lan_access_revoke_unsaved`).

Requests refused with their own status:

| Status | Code | Message |
|---|---|---|
| 400 | `invalid` | `PUT`: "Choose a valid LAN access setting and a port between 1024 and 65535." Pairing: "Enter the 6-digit pairing code and a name of up to 40 characters for this device." |
| 403 | `lan_access_denied` | "Open EE at the address shown in LAN access on the computer running EE." or "This request cannot be opened through LAN access." |
| 403 | `lan_pairing_required` | "Pair this device first. Enter the pairing code shown on the computer running EE." |
| 403 | `lan_pairing_failed` | "The pairing code is wrong or has expired. Check the code on the computer running EE, or choose Pair a device there to show a new one." |
| 404 | `not_found` | "That device is no longer paired." or "This LAN access request does not exist." |
| 409 | `lan_access_off` | "Turn on LAN access before pairing a device." |
| 409 | `lan_access_device_limit` | "16 devices are paired, the most EE allows. Remove one before pairing another." |
| 409 | `lan_access_failed` | "LAN access could not be updated. Refresh status and try again." The answer also carries `status`. |
| 413 | `too_large` | "The request is too large." A setting or pairing body over 1 KB, or any body over 1 MiB. |
| 500 | `lan_pairing_save_failed` | "The device could not be saved on the computer running EE. Ask for a new pairing code there and try again." |

### Saved settings

The setting and the paired devices are kept together in `lan-access.json` in the workspace data folder, next to
`private-access.json`, so turning LAN access off and removing every device is one write:

```json
{ "version": 1, "enabled": true, "port": 4280,
  "devices": [ { "id": "3f9c0a1b2c3d4e5f", "name": "Kitchen iPad", "client": "browser",
                 "key_sha256": "<64 lowercase hex>", "paired_at": "2026-11-16T20:10:12.000Z", "paired_from": "10.0.0.1" } ] }
```

The file holds only the SHA-256 of each key. It must be a regular file of at most 16 KB, not a link, and on macOS and
Linux owned by the user with no group or other permissions. A file that does not match this layout exactly is
reported as `lan_access_invalid_configuration` and keeps LAN access off; the next successful `PUT` writes a new file
with no devices. A missing file means off with no devices. A per-user uninstall deletes the file.

### Windows Defender Firewall

The installer adds and removes no firewall rule, and EE does not check the firewall. The first time EE's `node.exe`
listens on the network, Windows shows a Windows Security Alert for Node.js JavaScript Runtime; the owner allows it on
private networks only. The rule belongs to the program path, so any script that this `node.exe` runs can accept
connections on the allowed networks, and the rule stays after LAN access is turned off and after uninstalling. A rule
for private networks does not apply while Windows lists the current network as public. The
[desktop guide](../desktop/README.md#connect-over-your-network-without-tailscale) covers allowing, undoing Cancel and
removing the rule.

## Session

### `GET /api/session`

```json
{
  "logged_in": true,
  "connected": true,
  "connecting": false,
  "host": "192.168.1.2",
  "port": 22,
  "username": "admin",
  "simulated": false,
  "workspace_id": "opaque-workspace-id",
  "switch": { "hostname": "USW-Pro-XG-8-PoE", "etherlight_mcu": true, "port_rgb": true },
  "ssh_host_key": "SHA256:qmA9mR0m2rC3k7Yw1xB5nT8fJ4hL6sV0pD2eG9uZ1aQ",
  "ssh_host_key_type": "ssh-ed25519",
  "error": null,
  "remembered": { "host": "192.168.1.2", "port": 22, "username": "admin" },
  "has_saved_login": true,
  "saved_login_error": null,
  "pending_scene_choice": null,
  "automatic_connect_paused": false,
  "handoff_state": "none",
  "switch_playback": "playing",
  "offline_lighting_error": null,
  "handed_off": false,
  "fan_handoff": null
}
```

`connected` can become false while logged in, for example when the switch reboots. EE reconnects automatically,
and `error` holds the last connection error until it does.

`pending_scene_choice` is set while a new connection waits for the user to choose its lighting (see
[`POST /api/session/scene-choice`](#post-apisessionscene-choice)), otherwise `null`. `handoff_state` is `none`,
`pending` while a Handoff to UniFi runs, `complete` after a confirmed handoff and `unconfirmed` after one that EE
could not confirm. `automatic_connect_paused` is true after a handoff until the next successful connection, so EE
does not reconnect at startup; `handed_off` is true after a confirmed handoff while no switch is connected.
`switch_playback` and `offline_lighting_error` describe the [lighting player](#offline-lighting).

`fan_handoff` reports what [Handoff to UniFi](#post-apisessionhandoff) did with the fans:

| Value | Meaning |
|---|---|
| `"returned"` | During the last handoff EE stopped its fan control on the switch, and the switch confirmed that its own fan controller runs and is not paused. |
| `"unconfirmed"` | EE could not confirm that the fans are back on the switch's own controller: the switch was not connected or could not be read and EE could not rule out its own fan control, a fan command failed, or the 15-second limit ran out. |
| `null` | No EE fan control was found, the switch has no fan control, no handoff ran since the last connection, or no switch was connected. |

The value is kept in memory only. A successful connection resets it to `null`. A handoff that retries an
unconfirmed one after its switch connection was closed keeps the previous value; any other handoff sets it to its
own result, or to `null` when no switch was connected.

`error` is the engine's message while a switch is logged in and its engine has one; otherwise it is the error
object of the last failed attempt. That object can be an [SSH host key](#ssh-host-keys) error with its fingerprints,
also while another switch is logged in, so a client opens a key review only when `logged_in` is false. The WebSocket
`session` message carries the same object. When a reconnect is refused because the switch's key changed, the
engine's message asks the user to quit EE, open it again and review the key on the Connect page; the startup
connection then reports both fingerprints.

`ssh_host_key` and `ssh_host_key_type` describe the SSH key of the connected switch, which is the key EE saved for
its address: the fingerprint as `ssh-keygen -l` prints it and the key algorithm, such as `ssh-ed25519`. Both are
`null` while no switch is logged in, for the demo, and the type is `null` when it is not known.

`connecting` is true during automatic startup login and returns to false after success, failure or cancellation.
The server publishes both transitions as session messages. Clients must keep listening when their initial
session read happens before startup login completes. Explicit logout stops the lighting session without retrying;
the saved login remains available for the next app launch unless the user chooses Forget.

`remembered` contains only the address, SSH port and username. `has_saved_login` also remains true if a stored
record cannot be read; `saved_login_error` then contains an actionable message. Failed automatic login due to an
unreadable record sets `error.code` to `saved_login_unavailable` and leaves the stored record in place.

`workspace_id` is an opaque, stable identifier for browser-local drafts and preferences. A real workspace is
scoped to the normalized switch address, SSH port and username; demo has its own scope. It is `null` before a
workspace is selected. Treat it as a storage key, not an authentication token, and do not reuse a draft across
different workspace IDs.

### `POST /api/session/login`

```json
{ "host": "192.168.1.2", "username": "admin", "password": "...", "port": 22, "remember": true }
```

Returns the session as above, or an error. When switching to a different switch, the current session keeps
running until the new login succeeds.

`remember` defaults to false. On Windows, true encrypts the entire login payload for the current Windows user
with DPAPI before replacing the saved record. Protection or saving failure leaves the previous session and
saved record intact. An existing legacy record is migrated atomically when loaded; migration must succeed before
automatic login uses it. Passwords are never included in API or WebSocket responses. Demo login preserves an
existing saved switch login.

The login reads the lighting saved for this switch, under the identity of its SSH key and port layout:

- When the switch has saved lighting, EE applies it and the session starts.
- When it has none, the connection stays open and the session reports `pending_scene_choice` for 5 minutes. The
  user chooses between the lighting saved by releases that kept one setup for every switch and a new setup.
- When the saved file cannot be used and no switch playback record exists, EE moves the file aside as
  `<name>.corrupt-<UTC time>` and offers the same choice.
- With a playback record, an unusable file returns `422 invalid_saved_scene` ("The saved lighting for this switch
  could not be verified.") and stays where it is, because the player on the switch was built from it. A file that
  cannot be read, or that a newer version of EE saved ("...saved by a newer version of EE. Update EE to use it."),
  also returns `422 invalid_saved_scene` and is never moved.

### `POST /api/session/scene-choice`

```json
{ "connection_id": "<from pending_scene_choice>", "choice": "use_saved", "legacy_revision": "<from pending_scene_choice>" }
```

Finishes a connection that waits for its lighting. `pending_scene_choice` holds `connection_id`,
`legacy_revision` (a hash of the older shared `scene.json`, or `absent`) and `legacy_available` (whether that file
holds a usable setup). `choice` is `use_saved` to apply that file, `start_new` to start from the default setup, or
`cancel` to close the connection. `connection_id` is 1 to 128 characters and `legacy_revision` at most 128. Returns
the session.

| Status | Code | When |
|---|---|---|
| 400 | `invalid_scene_choice` | `choice` is another value |
| 409 | `scene_choice_expired` | No connection waits with this ID, the 5 minutes ran out, or the switch connection changed |
| 409 | `scene_choice_stale` | `use_saved` with a `legacy_revision` that no longer matches `scene.json`; the session reports the new revision |
| 409 | `saved_scene_unavailable` | `use_saved` while `scene.json` holds no usable setup |
| 422 | `invalid_saved_scene` | `scene.json` could not be applied to this switch |
| 500 | `scene_save_failed` | The chosen setup could not be saved for this switch |

### `POST /api/session/restore`

Takes no request body and reconnects using the server's protected saved switch login. Returns the session.
If a real switch session already exists, or a connection waits for its lighting choice, returns it unchanged.
Concurrent restore requests share one connection attempt. A demo session remains active until the saved real-switch
login succeeds. After a Handoff to UniFi (`automatic_connect_paused`), a restore returns the session unchanged unless
the query has `resume=true`, which the clients' Connect with saved login button sends.

The server publishes `connecting: true` and the final session transition. Missing saved credentials return
HTTP 409 with `no_saved_login`; an unreadable saved record returns HTTP 409 with `saved_login_unavailable`.
Connection errors use the normal login error contract. The saved record is retained, and its password is never
returned to the browser.

Devices on Tailscale or the LAN reconnect with this route. Like a login, a restore to an address without a saved
SSH key saves the key the switch presents. Signing in with typed details and accepting a changed SSH host key are
available only on the computer running EE.

### `POST /api/session/recover`

Takes no request body. After an interrupted connection (`reconnect_required` in the state), it checks the current
switch again, cleans up EE's own output there and resumes the retained scene. Returns the state. It refuses a changed
device identity, changed capabilities or unconfirmed helper cleanup with `409 output_unconfirmed`, and a pending
handoff with `409 handoff_unconfirmed`.

### `POST /api/session/forget`

Deletes the saved login, returns the updated session and publishes a session message. The current switch
connection keeps running. This also removes an unreadable record when the user explicitly chooses Forget.

### `POST /api/session/host-key/replace`

```json
{ "host": "192.168.1.2", "port": 22,
  "saved_ssh_host_key": "SHA256:qmA9mR0m2rC3k7Yw1xB5nT8fJ4hL6sV0pD2eG9uZ1aQ",
  "ssh_host_key": "SHA256:Zt4Wc8Hn1Lq6Rv3Xb9Me2Kd7Pj0Ys5Ug8Fa1Ni4Ow7E" }
```

Replaces a changed SSH key, after a `host_key_changed` error. `host` is 1 to 253 characters, `port` a JSON integer
from 1 to 65535 (default 22), and `ssh_host_key` and `saved_ssh_host_key` match `^SHA256:[A-Za-z0-9+/]{43}$`.
Other fields and values of another JSON type return `422 invalid_credentials`. `saved_ssh_host_key` must be the key
saved now and `ssh_host_key` the key EE last saw at that address. The route does not contact the switch: the
retried login or restore verifies the saved key during the SSH key exchange. It also deletes the switch playback
record of the replaced key, as a confirmed handoff does, because that player ran in the previous switch's memory.
Lighting, schedules and port names saved for the previous key stay saved but are not used with the new key. Returns
the session, clears a `host_key_changed` `error` for that address and publishes a session message.

| Status | Code | When |
|---|---|---|
| 200 | | The key is replaced, or was already the saved key |
| 400 | `missing_host` | `host` is empty after trimming |
| 403 | `local_access_required` | The caller's address is not a loopback address (127.0.0.0/8 or ::1) |
| 409 | `host_key_in_use` | EE still has an open switch connection, a lighting choice waiting, or a shutdown it has not confirmed |
| 409 | `host_key_stale` | No key is saved, the saved key is not `saved_ssh_host_key`, the two keys are equal, or `ssh_host_key` is not the key EE last saw there |
| 409 | `switch_playback_reserved` | The switch playback record cannot be read, waits for a handoff, or belongs to another switch |
| 500 | `host_key_save_failed` | The saved keys or the playback record could not be updated; the saved key and the record are unchanged |

The route refuses a caller whose address is not a loopback address before it reads the request body, and the
Tailscale and LAN gateways refuse it, so a changed SSH key is accepted only on the computer running EE. The desktop
app, the browser workspace and the gateway reach the service from 127.0.0.1. `POST /api/session/host-key/accept` and
`POST /api/session/forget-host-key` no longer exist and return 404.

A client offers to accept a changed key only when the workspace gateway's `GET /api/private-access` returns
`local: true`. The gateway on the computer running EE includes that field in its answer, with or without permission
to change private access. The Tailscale and LAN gateways answer the request themselves without the field, and
clients treat a failed request as remote.

### `POST /api/session/logout`

Stops EE from controlling the LEDs. The switch's own colors return within about two seconds. `?forget=true` also
deletes the remembered login, and returns `409 switch_playback_reserved` while switch playback is saved. A lighting
cleanup that the switch does not confirm returns `409 output_unconfirmed`. Returns the session.

### `POST /api/session/handoff`

Takes no request body. Stops EE lighting and its switch helpers, returns the fans of a USW-Pro-XG-8-PoE to the
switch's own controller, closes the SSH connection, and pauses automatic connection until you connect again. Saved
logins, profiles, the lighting setup and the fan mode saved for each switch are kept. When switch playback is saved,
it also stops the lighting player, reconnecting with the saved login if needed. Returns the session. Errors:
409 `handoff_unconfirmed` when EE could not confirm that its lighting and SSH activity stopped (retry the handoff;
automatic connection stays paused), `handoff_save_failed` when the handoff state could not be saved, and
`handoff_recovery_required` when an earlier handoff has no confirmed shutdown. A player that could not be stopped is
retried every 30 seconds. The fan step takes at most 15 seconds; `fan_handoff` reports its result and never changes
`handoff_state`. The next connection to the switch restores its saved fan mode.

### `GET /api/about`

Returns `name`, `short_name`, `version`, `tagline` and `disclaimer`, the unofficial-project notice. The disclaimer
should be visible in the UI, for example on the login screen and an About page.

### `GET /api/health`

Returns `{"ok": true, "name": "Etherlighting Evolved", "version": "<version>", "logged_in": false}`. `logged_in` is
true while a switch or the demo is connected.

## UniFi Network controller

The controller connection enriches the connected switch's port information. Account login supplies names and
topology; an official API key supplies the selected console's port metadata. Lighting output, link state,
traffic and PoE measurements continue to come from the switch session. Controller setup requires a real switch
session; a missing or demo session returns HTTP 409 with `switch_required`.

### `GET /api/controller`

Returns cached status without making a controller request:

```json
{
  "connected": true,
  "configured": true,
  "url": "https://controller.example:8443",
  "controller_kind": "self_hosted",
  "auth_method": "password",
  "console_id": null,
  "capabilities": {"client_mapping": true, "port_metadata": true},
  "site": "default",
  "sites": [{"id": "default", "name": "Home"}],
  "pending_site_choice": null,
  "switch_name": "Lighting switch",
  "updated_at": "2026-10-30T18:00:00+00:00",
  "stale": false,
  "error": null,
  "mapped_ports": 7,
  "preferences_supported": true,
  "remember_on_connect_supported": true,
  "saved_target": null,
  "auto_reconnect": false,
  "saved_login_available": false,
  "saved_login_matches_connection": false,
  "can_save_login": true,
  "reconnect_state": "off",
  "saved_login_error": null
}
```

`configured` includes a pending connection awaiting a certificate, verification code or site choice.
`controller_kind` is `self_hosted`, `unifi_os`, or `cloud_connector`; it is `null` before configuration.
`auth_method` is `password` or `api_key`. The `capabilities` flags describe the current adapter's data.
`console_id` identifies the selected Site Manager console for cloud access.
`supported_auth_methods` advertises the implemented setup options even before configuration:
`{"self_hosted": ["password"], "unifi_os": ["password", "api_key"], "cloud_connector": ["api_key"]}`.
`console_discovery_supported: true` enables the Cloud Connector's console picker.
`connected` requires an authenticated session and a successful, fresh match to the connected switch.
`mapped_ports` counts ports with controller-backed metadata, not linked ports. `updated_at` is the last successful
topology refresh in UTC; it is `null` before the first success. `error` is `null` or a sanitized object with
`code` and `message`.

EE refreshes the selected site approximately every 15 seconds. Data older than 45 seconds, or a failed refresh,
sets `stale` and removes controller enrichment from port responses. `connected` becomes false and `mapped_ports`
becomes zero until a successful refresh. Cached data is also scoped to the connected switch address.

The same status object appears as `controller` in `GET /api/state` and WebSocket state/frame messages.
Enriched entries in `GET /api/ports` and frame `ports`, plus state/frame `sfp`, can include:

```json
{"port": 1, "name": "Workstation", "controller": {"source": "unifi", "kind": "client", "count": 1}}
```

`kind` is `client`, `device`, `multiple`, or `port_label`. Names follow explicit controller switch-port topology;
multiple clients on one port produce a count label. Adopted-device topology takes precedence over clients behind
that device. Configured port labels provide a fallback. Existing lighting and telemetry fields are preserved.

Client mappings include a bounded `controller.clients` list with `name` and
`name_source` (`assigned`, `hostname`, or `null`). Assigned controller names appear
first, followed by hostname fallbacks. The port title uses assigned names when
available; Port details retains every returned associated client. `count` is the
total number of associated clients, and `clients_truncated` indicates more than
64 records. Multiple assigned names remain visible together in the port title.

The official API adapter adds `controller.source: "unifi_integration"`, `kind: "port"`,
`client_mapping: false`, the documented port `state`, optional `speed_mbps`, and optional `poe`
fields (`enabled`, `type`, `standard`, `state`). These stay inside `controller`; the switch session's
measurements and port names remain authoritative. The official schema does not supply the physical
client-to-port join used by account login. A UI can offer account login for device names when
`capabilities.client_mapping` is false.

### `POST /api/controller/connect`

```json
{
  "url": "https://controller.example:8443",
  "controller_kind": "self_hosted",
  "auth_method": "password",
  "username": "controller-user",
  "password": "...",
  "site": null,
  "certificate_sha256": null,
  "verification_code": null
}
```

`controller_kind` selects `self_hosted` for the standalone Network application, `unifi_os` for a UniFi console
or UniFi OS Server, or `cloud_connector` for an explicitly selected Site Manager console.
It defaults to `self_hosted`, and `auth_method` defaults to `password`, for existing callers. `url` is the direct HTTPS origin,
including its port. The usual ports are 8443 for standalone Network and 443 for UniFi OS; the supplied origin
is used exactly. Paths, query strings, embedded credentials and redirects are rejected.
`site` is the controller's site ID (a UUID for official API access). A single accessible
site is selected automatically. With multiple sites, the response includes
`pending_site_choice: {"url": "https://controller:8443", "controller_kind": "self_hosted",
"auth_method": "password", "console_id": null, "capabilities": {...}, "sites": [...]}`;
submit again with the selected ID. During first setup, the response also includes
`error.code: "site_required"` and the top-level `sites` choices. When replacing a working connection,
the top-level status and telemetry keep describing that connection until the candidate's selected
site has been read and matched successfully. Canceling or failing that selection preserves the
working connection. Saving a login is unavailable while a site choice is pending.

Omit `remember` or send `false` for a session-only connection. The previous saved login stays intact.
When `remember_on_connect_supported` is true, send `"remember": true` to save the new login after EE
authenticates it and matches the selected site to the connected switch. Include the flag on the final
request after certificate approval or site selection. Check `saved_login_matches_connection` in the result.
If Windows cannot save the login, the connection stays active and `saved_login_error` describes the failure.
The preferences endpoint can retry the save using that active connection.
Subsequent requests to the same origin, controller type, sign-in method and console ID
may leave the selected credential fields blank to continue that pending or active connection. An omitted fingerprint
retains the certificate already approved for that target. A failed replacement connection preserves a different
active controller connection. On the self-hosted path, `verification_code` is an optional one-time code of 6–8 digits; it is neither saved
nor automatically replayed.

TLS certificates are verified before credentials are sent. An untrusted certificate returns HTTP 400:

```json
{"detail": {"code": "certificate_untrusted", "message": "Verify this controller certificate before signing in.",
            "certificate_sha256": "64-hex-digit-fingerprint"}}
```

Show the fingerprint for explicit user approval, then resubmit it as `certificate_sha256`. The client compares the
approved SHA-256 fingerprint before any authenticated request. A later mismatch returns `certificate_changed`
and requires approval of the new fingerprint. Certificate approval and account verification are separate steps.

Other actionable errors include `invalid_url`, `credentials_required`, `authentication_failed`,
`two_factor_required`, `verification_failed`, `site_not_found`, `no_sites`, `switch_not_found`, `unreachable`,
`rate_limited`, `invalid_controller_kind`, `controller_local_account_required`, and `unsupported_controller`.
UniFi OS accounts that require an additional authentication flow return `controller_local_account_required`;
use a local account with Network access. Use sanitized messages; response bodies do not include
controller diagnostics, passwords or cookies.

The self-hosted adapter posts to `/api/login`. The UniFi OS adapter posts to `/api/auth/login`.
Data requests are GETs to `/api/self/sites`, `/api/s/{site}/stat/device`, and `/api/s/{site}/stat/sta`,
with `/proxy/network` prepended for UniFi OS. Routes are selected explicitly; a failed login never tries
another controller type. Cookies and validated CSRF tokens remain scoped to the approved origin in server memory.

#### Official API-key access

Direct console example:

```json
{"url": "https://console.example", "controller_kind": "unifi_os", "auth_method": "api_key",
 "api_key": "...", "console_id": null, "site": null, "certificate_sha256": null}
```

Cloud Connector example:

```json
{"url": "https://api.ui.com", "controller_kind": "cloud_connector", "auth_method": "api_key",
 "api_key": "...", "console_id": "selected-console-id", "site": null, "certificate_sha256": null}
```

Send only the selected authentication method's secret. API keys are sent as `X-API-Key` over verified
HTTPS; they are not returned in status or errors. Local console certificates use the explicit trust flow
above. Cloud access requires the fixed `https://api.ui.com` origin and public certificate validation;
it rejects certificate overrides. Keys remain in server memory unless the user explicitly saves
automatic reconnect, which uses the same Windows DPAPI protection as account login.

The adapter uses only the official Network Integration GET routes for sites, adopted devices and
device details. The direct prefix is `/proxy/network/integration`; the Cloud Connector prefix is
`/v1/connector/consoles/{console_id}/proxy/network/integration`. Each prefix is followed by
`/v1/sites`, `/v1/sites/{site}/devices`, or `/v1/sites/{site}/devices/{device}`. It does not fall back
to another origin or route family. This API-key path is offered for UniFi OS and Cloud Connector;
the standalone controller adapter continues to use account login.

Site and device lists are consumed completely in pages of 200, up to 1,000 entries. Invalid,
duplicate or incomplete lists fail with `api_response_incomplete`. Matching requires a unique
literal switch IP in the selected site and consistent device ID, MAC and IP between the list and
detail response. A switch connected by hostname returns `switch_identity_required`; reconnect it
by IP to use this adapter. Matching controller metadata does not qualify a device for lighting.
The verified SSH hardware profile and output ownership checks remain independent.

Requests are limited to 40 per minute per adapter. HTTP 429 honors a bounded `Retry-After`, with
a 60-second default. Automatic retries preserve the selected connection and back off; stale
metadata is removed until a complete refresh succeeds. HTTP 401/403 requires user action.
New errors include `invalid_auth_method`, `api_key_required`, `invalid_api_key`, `invalid_console_id`,
`invalid_cloud_origin`, `switch_identity_required`, `api_response_incomplete`, and `integration_unavailable`.

Route and field contracts follow the official [Network 10.6.106 OpenAPI schema](https://developer.ui.com/network/v10.6.106/openapi.json)
and [Site Manager connector schema](https://developer.ui.com/site-manager/v1.0.0/openapi.json).
Cloud Connector requires a console firmware version supporting that API (documented as 5.0.3 or later).
Use the installed Network application's Integrations documentation to verify its available endpoints.

### `POST /api/controller/consoles`

```json
{"api_key": "..."}
```

Reads the consoles available to the entered Site Manager key without changing the current controller
connection. The response is `{"consoles": [{"id": "opaque-console-id", "name": "Console name",
"type": "ucore", "blocked": false}]}`. Use the selected `id` as `console_id` on the normal connect
request. `name` uses a bounded reported display name when supplied, otherwise a short ID label;
it is never used for device matching. A blocked console should remain visible but unavailable for selection.

Only GET `https://api.ui.com/v1/hosts?pageSize=100` and encoded continuation tokens are used. The
complete result is capped at 1,000 consoles over ten pages. Duplicate IDs, repeated tokens and
incomplete pages are rejected. HTTP 429 cooldown and a request budget survive subsequent discovery
requests, including a changed key. Discovery's key is cleared after its operation and is not saved.
The response excludes account details, addresses and raw controller data. Empty access returns
`no_consoles`; existing sanitized authentication, transport and pagination errors also apply.

### `POST /api/controller/cancel`

Discards a pending controller connection or site choice and returns the full controller status.
The current working connection, telemetry and saved reconnect record remain unchanged. The
response has `pending_site_choice: null`. This request clears candidate credentials from EE
memory without making a controller request.

### `POST /api/controller/disconnect`

Takes no request body. Clears the in-memory controller credentials, cookies and cached names, then returns the
disconnected status. The switch session and its lighting remain active. If automatic reconnect is enabled,
explicit Disconnect pauses it for this runtime without deleting the saved login. Normal application shutdown
also preserves the saved opt-in.

### `PUT /api/controller/preferences`

```json
{"auto_reconnect": true}
```

This endpoint accepts only a JSON boolean `auto_reconnect` and rejects unknown fields. Enabling requires a
currently authenticated, fresh controller session that successfully matched the connected real switch.
It saves that session's canonical HTTPS origin, controller type, selected site, approved certificate pin and
sign-in credentials (account login or API key, including the selected cloud console ID when applicable) under
current-user Windows DPAPI, using a separate atomic controller record. Verification codes and cookies are never
saved. An unprotected plaintext record is rejected. A save failure leaves the current connection and prior
saved record intact. Existing protected records without a controller type load as `self_hosted` without rewriting
the file. The response is the full controller status.

Disabling deletes the saved controller record and leaves the active connection running. A deletion failure
returns an error and retains the persisted setting. Enabling without a matched live session returns HTTP 409
`controller_login_required`. Storage errors return HTTP 500 with `controller_login_save_failed` or
`controller_login_remove_failed`; loading failures appear as `saved_controller_login_unavailable` in status.

Feature-detect `preferences_supported` before showing this setting. `auto_reconnect` is the persisted opt-in;
`saved_login_available` means its protected record was successfully loaded. `can_save_login` describes the
active session's eligibility. `saved_login_matches_connection` distinguishes the saved login from a different
active controller or credential set. When it is false and saving is eligible, offer an explicit Apply even
if the checkbox is already checked. A new manual connection never implicitly replaces saved credentials.

Account-based controller requests may include an `operation` with a `controller_error` (`login`, `sites`, `devices`, or `clients`). An unexpected upstream HTTP status is returned as `upstream_status`. Controller response bodies are private.

`reconnect_state` is `off`, `waiting`, `connecting`, `retrying`, `connected`, `paused`, or `attention`.
`saved_login_error` is null or a sanitized `{code,message}` object. No saved credential fields are returned.
`saved_target` is null without a readable saved login. Otherwise it contains only `url`, `controller_kind`,
`auth_method` and `console_id`. A disconnected form can restore these choices while waiting for the switch.
Only an active or pending controller session can reuse credentials during a manual connection.
After a live switch session is established, the protected controller login is restored automatically within
the next local lifecycle tick. No restore runs for a missing, disconnected, or simulated switch session.
The controller's usual TLS validation and approved pin comparison remain mandatory.

Transient reachability, busy-controller and controller-service failures get at most three additional attempts
after 5, 15 and 60 seconds. Authentication, certificate, verification-code, site and switch-match errors stop
automatic requests until an explicit user action. Normal successful topology refresh remains approximately
15 seconds. An in-flight bounded controller operation drains before shutdown clears its in-memory session.

## State

### `GET /api/state`

```json
{
  "mode": "running",
  "connected": true,
  "error": null,
  "switch": { "hostname": "USWProXG8PoE-SIM", "etherlight_mcu": true, "port_rgb": true },
  "num_ports": 8,
  "fps": 25.0,
  "brightness": 100,
  "dimming": "rgb",
  "output": { "gamma": 2.2, "transition": 0.6, "max_commands": 200.0 },
  "zones": [
    { "id": "all", "name": "All ports", "ports": [1, 2, 3, 4, 5, 6, 7, 8],
      "effect": { "id": "static", "name": "Static", "params": { "color": "#ffffff" } } }
  ],
  "rules": [
    { "id": "e00e3872", "name": "Anomaly", "enabled": true, "match": "all", "ports": null,
      "conditions": [{ "field": "anomaly", "op": "is", "value": true }],
      "look": { "color": "#ff9f0a", "style": "blink", "rate": 0.5, "brightness": null } },
    { "id": "0ee7bc65", "name": "Offline", "enabled": true, "match": "all", "ports": null,
      "conditions": [{ "field": "link", "op": "is", "value": false }],
      "look": { "color": "#ff0000", "style": "solid", "rate": 1.0, "brightness": null } }
  ],
  "overrides": { "3": { "color": "#bf5af2", "brightness": null } },
  "revision": 12,
  "stats": { "frames": 1830, "commands": 2401, "commands_per_second": 27.4, "command_budget": 30.0,
             "switch_cpu_idle": 0.62, "switch_load": 0.85, "last_write_ms": 61.2, "avg_write_ms": 64.4 }
}
```

- `mode` is `running`, or `released` when the LEDs have been handed back to the switch.
- `revision` increases with every change.
- `output` holds the advanced output settings (see Global controls).
- `stats` is for a diagnostics view. `commands_per_second` is how many port commands EE is sending,
  `command_budget` the most it will send right now (lower than `max_commands` while the switch is busy), and
  `switch_cpu_idle` and `switch_load` come from the switch (`null` until read). `last_write_ms` and
  `avg_write_ms` include the elapsed frame write and pacing time. `direct_rgb` identifies the direct socket
  output path. `ack_p95_ms` is the recent per-port acknowledgment latency, excluding pacing waits, or `null`
  without fresh samples. `cpu_sample_age_ms` is the age of the latest usable CPU sample, or `null` when unknown.
  `switch_health` holds the switch's memory, temperature and storage readings, `health_sample_age_ms` their age and
  `health_error` is `null`, `unavailable` or `invalid_response`.
- `reconnect_required` is true when an interrupted connection needs
  [`POST /api/session/recover`](#post-apisessionrecover) before lighting resumes.
- `hardware_profile` describes the detected model and its physical port layout, or `null`.
- The state also carries `schedule_clock` ([WebSocket](#websocket-apiws)), `identify`
  ([Port identification](#port-identification)), `telemetry` and `poe_summary`
  ([Port telemetry](#port-telemetry-and-poe-budget)), `history_error` ([Lighting history](#lighting-history)),
  `sfp` and `sfp_sync` ([SFP indicator lighting](#sfp-indicator-lighting)), `uplink_bandwidth` ([UI flow](#ui-flow)),
  `fans` ([Fans](#fans), `null` until the session attaches fan control) and `controller`
  ([UniFi Network controller](#get-apicontroller)).

### `GET /api/ports`

One entry per LED port. WebSocket frames contain the same entries.

```json
{
  "port": 2,
  "color": "#ffffff",
  "output": { "color": "#ffffff", "level": 100 },
  "preview": "#ffffff",
  "source": { "kind": "zone", "id": "all", "name": "All ports" },
  "override": null,
  "link": { "port": 2, "admin_up": true, "link_up": true, "speed_mbps": 2500, "full_duplex": true,
            "rx_packets": 1636, "tx_packets": 5086, "rx_bytes": 1473091, "tx_bytes": 4577516,
            "uplink": false, "stp_state": "forwarding", "anomaly": "0x0", "lag": null },
  "poe": { "port": 2, "enabled": true, "delivering": true, "pd_class": 4, "power_w": 12.31,
           "voltage_v": 53.6, "current_ma": 229.59, "mode": "Dot3bt-Type4", "tier": "poe+" },
  "facts": { "link": true, "speed": 2500.0, "full_duplex": true, "uplink": false, "anomaly": false,
             "stp_state": "forwarding", "lag": false, "traffic": 0.0, "poe": "poe+", "poe_power": 12.31 },
  "traffic_bps": 0
}
```

- `color` is the color chosen for the port. `preview` approximates how the LED looks at its brightness and is the
  value to use when drawing the port. `display_rgb` is the composed color scaled by its brightness as three numbers
  from 0 to 255, before the switch's transfer curve, for clients that draw with more precision.
- `source` is what set the color: `{"kind": "zone", ...}`, `{"kind": "rule", "id", "name"}`,
  `{"kind": "override"}` for a manual color, or `{"kind": "none"}` for a port that isn't in a zone.
- `link` and `poe` are `null` until the switch has reported them. `poe` stays `null` on switches without PoE.
  `facts` contains the values rules are checked against, using the same field names as rule conditions.

## Effects

Live Traffic (`traffic`) uses each port's negotiated link speed as its capacity with logarithmic response.
If an online port reports zero speed, capacity defaults to 1,000 Mbps. Older scenes containing `max_mbps`
or `scale` load normally; those retired values are omitted on save.
Activity Glow compares eligible device ports against the busiest device, with `minimum_mbps` setting the
traffic rate at which the busiest device first reaches full glow.

PoE Usage (`poe_usage`) blends `low_color` and `high_color` using delivered power, with the fixed Pro XG 8 PoE
per-port reference of 60 W: `min(1, log1p(watts) / log1p(60))`. `no_power_color` supplies the resting color
when power is zero or unavailable. Only a delivering PoE reading supplies a positive power value; an uplink
can still supply power. Every color supports the standard independent brightness and randomization controls.
`speed` is Fade speed, 0.1–5× (default 1). Changing the power reading eases both the color and the powered-state
blend using elapsed time, with a 0.6-second time constant at 1×. Zone-local port indices retain the physical
port's power reading, including reordered groups.

## SFP indicator lighting

On a qualified switch, `GET /api/state` and WebSocket state/frame messages include `sfp`, separate from the
copper `ports` array. This example is the Pro XG 8 PoE; other models report their own indicator IDs:

```json
[{"port":9,"mode":"color","color":"#2288ff","brightness":70,"link":false,"anomaly":false},
 {"port":10,"mode":"unmanaged","color":"#ffffff","brightness":100,"link":null,"anomaly":null}]
```

`PUT /api/sfp/{port}` accepts a nonempty subset of these settings and returns the full state:

| Field | Values |
|---|---|
| `mode` | `color`, `off`, `unmanaged` (Link status) |
| `effect` | `steady`, `pulse`, `color_cycle`, `two_color` |
| `color`, `secondary_color` | RGB color |
| `brightness`, `color_brightness`, `secondary_brightness` | Integer 0–100 |
| `period` | Cycle duration, 2–30 seconds |

Color mode runs the selected effect; Off holds black while retaining custom ownership. Link status applies
the scoped status-controlled LED baseline after draining output. This is not a snapshot of the controller's
previous color policy. Settings remain saved when changing modes. Each color's brightness is applied before
blending; SFP brightness and master brightness multiply the result once. Link and anomaly facts stay independent.

When `switch.sfp_native_gradient` is true, Pulse and Two-color fade use finite native segments for periods
2, 4, and 6 seconds (one, two, or three seconds per leg). Color cycle uses six native segments for periods
6, 12, and 18 seconds. Existing custom periods remain saved exactly and retain their software playback.
Clients should offer native durations for new selections and retain an explicit Custom value for an existing
period. Feature-detect the capability and `sfp[].playback` before displaying native playback controls.

The next selector is prepared within two command intervals plus one render tick of the next boundary. Each
target waits until its prior acknowledged start plus the nominal segment duration. This is a timing estimate,
not hardware completion readback. The selector is restored to steady after enrollment, including interrupted
preparation. Keepalive never restarts a finite segment. The existing adaptive
command ceiling, acknowledgment checks, and CPU backoff apply to every selector and target request. Custom
software effects retain the independent SFP sample clock of up to 50 Hz under that same command budget.

Each indicator includes `playback: {kind, periods, segment_seconds}`. Kind is `native`, `custom`, `steady`,
or `follow`; `periods` lists the selected effect's native cycle durations. `segment_seconds` is null for custom
and steady playback. `native_transition`, when present, contains `duration`, clamped `elapsed`, `from_rgb16`,
and `to_rgb16`. RGB16 values are in 0–65280. Interpolation is linear between the encoded endpoints; the native
preview must not apply the custom effect's cosine or hue easing. `display_rgb` is the modeled current output
with the transfer curve inverted, based on acknowledged segment start times. It is not physical LED readback.

Pulse alternates primary and black. Two-color fade alternates primary and secondary. Color cycle has six
equally spaced HSV hue anchors starting at the primary hue, with saturation at least 75%. Per-color, SFP, and
master brightness are applied once before encoding each anchor through the configured gamma. Each SFP target
is separately acknowledged; grouped enrollment is scoped to the qualified indicators and does not imply atomic
hardware updates. Unknown or ambiguous output is reported through the existing control status.

Each SFP entry also contains `revision`, `acknowledged_revision`, and
`control: {state, owned, error}`. Control state is `idle`, `active`, `pending`, or `uncertain`. A matching
acknowledged revision confirms the requested output reached the native writer. The endpoint waits up to five
seconds for that acknowledgment; a longer or released operation returns explicit pending state. A failed
transition returns HTTP 409. Clients should preserve pending state until the matching state/frame arrives.

Indicators start in `unmanaged`. Settings save with scenes and restore after restart; older scenes
without `sfp` restore unmanaged indicators. Copper group effects, rules, and manual-color clearing leave SFP
settings intact. An optical indicator can opt into a regular effect through explicit membership in
`zones[].ports`. Its `effect_zone` is the group ID while enrolled or null otherwise. While enrolled,
`effective_settings` is null. The group uses the indicator's own position and telemetry. Matching explicit
SFP rules still take priority, and its own SFP brightness scales the result. Its saved indicator mode,
colors, native effects and synchronization settings resume when the port leaves the group.
`PUT /api/sfp/{port}` accepts a brightness-only edit while the port belongs to an effect group. To change
its indicator appearance, remove the port from the group first or stage both changes in one scene.
Global release drains output, ends native Breathing, and releases owned SFP indicators.
An unsupported or unidentified model reports an empty `sfp` array and rejects SFP writes.

### SFP synchronization

State, frames, scenes, and presets include `sfp_sync: {"mode":"independent","source_port":1}`.
`PUT /api/sfp/sync` accepts a nonempty subset of these two fields and returns the full state after waiting for
the indicators' existing output acknowledgments. The same pending and failure behavior described above applies.
At least two qualified indicators are required for synchronization. In Ethernet mode, `source_port` must be
a member of the current copper layout. Optical-only models offer Independent and Together.
Unknown fields and invalid values return HTTP 400 without changing the scene.

- `independent`: each indicator uses its own saved SFP settings (the default for new and older scenes).
- `together`: all use the lowest-numbered qualified SFP port's settings. Native target timing shares a boundary plan while respecting each
  indicator's prior segment duration. An unusually late acknowledgment realigns the next pair instead of
  accumulating phase drift. Each indicator retains its own acknowledged start time for display. Editing the leader
  waits for the affected outputs. Other indicators' independent settings stay saved for when independent mode returns.
- `ethernet`: all track the selected copper port's final composed color and brightness, including manual
  colors and matching rules. With native support, each one-second boundary samples the current source target
  during preparation and fades toward it; intermediate source changes do not restart the segment. This is a color follower,
  not an exact phase copy of fast effects. Source brightness is used once before the SFP transfer curve;
  dormant independent SFP settings do not dim or mask it. Without native support the existing software follower
  remains in use.

Explicitly targeted SFP rules retain priority over synchronized output. In together mode, the leader's explicit Off
setting retains the existing Off policy for its group. In ethernet mode, dormant Off settings do not
suppress the source or its targeted SFP rules. Each SFP state entry has `effective_settings`: its active SFP
settings for independent/together mode, or `null` when following a copper port or running a group effect. Top-level per-indicator fields
remain the saved independent settings. Synchronization preserves the existing writer, pacing, CPU backoff,
ownership, and release behavior.

### Synchronized Breathing

`breathing.playback` selects `custom` or `synchronized`. Existing scenes default to Custom. Synchronized
playback uses a fixed primary color with its independent brightness. Copper members require
`switch.native_breathing` and use the native selector. SFP members use the regular effect frame and
their existing per-indicator writer.
`pace` is 1 (about 3.08 seconds), 2 (2 seconds), or 3 (1 second). All synchronized groups share that pace;
changing it updates every synchronized group. Custom settings remain saved when changing playback.
Manual copper colors and matching rules take priority.

## Effect configuration

### `GET /api/effects`

Lists every effect and its settings. The UI should generate effect controls from this list.

```json
{
  "id": "comet",
  "name": "Comet",
  "description": "A bright point with a fading tail that moves along the ports.",
  "category": "animated",
  "params": [
    { "key": "color", "label": "Color", "type": "color", "default": "#64d2ff", "random_param": "random_color", "brightness_param": "brightness_color" },
    { "key": "background", "label": "Background", "type": "color", "default": "#000000", "random_param": "random_background", "brightness_param": "brightness_background" },
    { "key": "speed", "label": "Speed", "type": "float", "default": 1.0, "min": 0.25, "max": 12, "step": 0.25,
      "unit": "ports/s" },
    { "key": "tail", "label": "Tail", "type": "float", "default": 3.0, "min": 0.5, "max": 8, "step": 0.5, "unit": "ports" },
    { "key": "bounce", "label": "Bounce", "type": "bool", "default": false },
    { "key": "random_color", "label": "Randomize color", "type": "bool", "default": false, "randomizes": "color" },
    { "key": "random_background", "label": "Randomize background", "type": "bool", "default": false, "randomizes": "background" },
    { "key": "brightness_color", "label": "Color brightness", "type": "float", "default": 100.0, "min": 0.0, "max": 100.0, "step": 1.0, "unit": "%", "dims": "color" },
    { "key": "brightness_background", "label": "Background brightness", "type": "float", "default": 100.0, "min": 0.0, "max": 100.0, "step": 1.0, "unit": "%", "dims": "background" },
    { "key": "color_transition", "label": "Color transition", "type": "float", "default": 8.0,
      "min": 1.0, "max": 30.0, "step": 0.5, "unit": "s" }
  ]
}
```

| Param type | Control |
|---|---|
| `color` | Color picker |
| `float`, `int` | Slider from `min` to `max` in steps of `step`, with `unit` shown next to the value |
| `bool` | Toggle |
| `choice` | Segmented control or menu of `choices` |

`category` is `static`, `animated`, `time` (Daylight follows the clock) or `data` (Live Traffic, Traffic
Trails and Activity Glow use port traffic).

Every explicit color also names a `brightness_param`. Its numeric companion identifies the color through
`dims`, ranges from 0 to 100 percent and defaults to 100 for existing scenes. It scales that color once after
randomization and before effect blending. Master brightness and output gamma still apply to the composed frame.
Both independent color brightness and randomization are retained in saved scenes.

Live Traffic, Traffic Trails and Activity Glow accept `include_uplinks` (default false). Enable it to
include the measured rates of selected uplinks. Live Traffic's explicit custom uplink color still wins when
enabled. Activity Glow (`activity_glow`) compares known-online rates within its group. `minimum_mbps` sets the minimum scale, `speed` adjusts the attack and release, and `active_color` and
`idle_color` set its endpoints. Unknown, offline or invalid-rate ports contribute zero traffic.

Every explicit color setting includes a `random_param` naming its independent randomization toggle, such as
`random_background`. The companion boolean includes `randomizes: "background"`. Fixed colors remain saved
while randomization is enabled; disabling it restores the chosen color. `color_transition` sets the time between
random color targets, from 1 to 30 seconds, independently of movement speed. Transitions follow an eased hue
curve rather than choosing a new color every frame.

For example, this Marquee configuration keeps the primary color fixed and changes the background color:

```json
{"id": "marquee", "params": {"color": "#eab977", "background": "#000000", "random_background": true,
                              "color_transition": 8.0}}
```

Effect metadata may include `exclusive_params`, a list of boolean pairs. Starry Night's per-sparkle colors and
Strobe's per-flash colors each exclude continuous primary-color randomization. A partial update enabling one
automatically disables its alternative; explicitly enabling both in one request is rejected. Background colors
remain independent. Scenes and presets retain all these settings.

## Zones

A zone is a group of ports running one effect. The default zone contains the copper ports. An explicit
zone can contain copper ports, qualified SFP indicators, or both.

| | |
|---|---|
| `GET /api/zones` | The zones, as in the state |
| `PUT /api/effect` | `{"id": "rainbow", "params": {...}}`: one effect on all copper ports and previously enrolled SFP indicators (replaces all zones) |
| `PUT /api/zones` | `{"zones": [{"name": "Desk", "ports": [1,2,3,4], "effect": {"id": "fire", "params": {}}}, ...]}` |
| `PUT /api/zones/{id}/effect` | `{"id": "comet", "params": {...}}`: change one zone's effect |
| `PATCH /api/zones/{id}/params` | `{"params": {"speed": 1.25}}`: change some settings and keep the rest |

A port can be in only one zone. Copper ports outside a zone stay off unless a rule or manual color sets them.
SFP indicators outside a zone use their indicator settings. Zone IDs are made from the name (`"Desk"` becomes `"desk"`) unless an id is given.

Both effect-setting routes accept `"clear_overrides": true`. The effect is validated and applied together with
removal of manual colors on its target ports. Other groups keep their manual colors. Omit the flag to retain
manual colors. The UI uses this flag when selecting an effect. WebSocket `set_effect` accepts the same flag.
Changing speed through the parameter route preserves animation position.

## Rules

Rules set a port's color when their conditions match. They are checked in order, and the first match is used. The
default setup is a static white zone plus an Offline rule that turns ports without a link red.

| | |
|---|---|
| `GET /api/rules` | The rules, in order |
| `POST /api/rules` | Add a rule. `"position": 0` puts it first; by default it goes last |
| `PATCH /api/rules/{id}` | Change part of a rule: `{"enabled": false}`, `{"look": {...}}`, ... |
| `PUT /api/rules/order` | `{"ids": [...]}`: set the order (every id exactly once) |
| `PUT /api/rules` | `{"rules": [...]}`: replace all rules |
| `DELETE /api/rules/{id}` | Delete a rule |
| `GET /api/rules/fields` | The fields conditions can use (below) |
| `GET /api/rules/templates` | Ready-made rules |

`POST /api/rules` answers 201 and `PATCH` 200 with the rule; `PUT`, `PUT /api/rules/order` and `DELETE` return all
rules.

A rule:

```json
{
  "name": "PoE++ devices",
  "enabled": true,
  "match": "all",
  "ports": null,
  "conditions": [{ "field": "poe", "op": "in", "value": ["poe++", "poe+++"] }],
  "look": { "color": "#bf5af2", "style": "pulse", "rate": 0.25, "brightness": null }
}
```

- `match`: `all` conditions must be true, or `any` of them. A rule without conditions always matches.
- `ports`: explicit lighting ports, or `null` for all copper ports. Verified SFP indicators can be targeted
  explicitly with `[9]`, `[10]`, or `[9,10]`.
- `look.style`: `solid`, `blink`, `pulse`, `flash` or `effect`. `effect` keeps the zone's color and only applies
  `brightness`, for example to dim ports at night.
- `look.rate`: cycles per second, with a minimum of 0.05. Maximums are 0.33 for pulse, 1 for blink and 0.5 for
  flash. Defaults are 0.25 for pulse and 0.5 for blink/flash. Older saved rates are clamped to these ranges.
- `look.brightness`: 0-100 to override the global brightness on matching ports, or `null`.

An explicit SFP rule uses that indicator's link, anomaly, spanning-tree and other available port facts.
The first matching rule takes priority over its saved color or effect. A rule temporarily takes over
Link status while its condition matches, then restores Link status when cleared, disabled or deleted.
Outside regular effect groups, Independent Off and together mode's shared Off remain off. `effect` preserves the current Color effect or
followed copper color, or the enrolled port's group effect. It leaves independent Link status under switch control. In independent/together mode,
the effective SFP brightness still multiplies the chosen rule or master brightness. Ethernet mode uses the
source's composed brightness unless the SFP rule overrides it. SFP state includes `rule`
(`id` and `name`, or `null`), `preview`, and `facts` for the UI; scenes store rule settings and base SFP
settings, not transient rule ownership.

### `GET /api/rules/fields`

```json
{
  "fields": [
    { "key": "link", "label": "Link up", "type": "bool", "operators": ["is"],
      "description": "A device is connected and the link is up." },
    { "key": "speed", "label": "Link speed", "type": "number",
      "operators": ["gt", "ge", "lt", "le", "eq", "ne", "between"], "unit": "Mbps" },
    { "key": "poe", "label": "PoE type", "type": "choice", "operators": ["is", "is_not", "in", "not_in"],
      "choices": ["off", "poe", "poe+", "poe++", "poe+++"] },
    { "key": "time", "label": "Time of day", "type": "time", "operators": ["between"] }
  ],
  "styles": ["solid", "blink", "pulse", "flash", "effect"]
}
```

All fields: `link`, `speed`, `full_duplex`, `uplink`, `anomaly`, `stp_state`, `lag`, `traffic` (Mbps), `poe`,
`poe_power` (W), `time` and `weekday`. Condition values by type:

| Type | `value` |
|---|---|
| `bool` | `true` or `false` |
| `number` | a number, or `[low, high]` for `between` |
| `choice` | one of `choices`, or a list of them for `in` and `not_in` |
| `time` | `["22:00", "07:00"]`; if the end is before the start, the range crosses midnight |
| `weekday` | `["sat", "sun"]` |

A condition on a value the switch hasn't reported (for example PoE on a switch without PoE) never matches.

## Manual colors

A manual color overrides zones and rules on that port until it is cleared.

| | |
|---|---|
| `PUT /api/ports/{port}` | `{"color": "#ff00ff", "brightness": 60}` (`brightness` is optional) |
| `PUT /api/ports` | `{"ports": {"1": {"color": "#ff0000"}, "2": {"color": "#00ff00"}}}` |
| `DELETE /api/ports/{port}` | Clear one port |
| `DELETE /api/ports` | Clear all ports |

The browser keeps picker and brightness edits local until Apply. The WebSocket `set_port` equivalent remains
available to API clients and applies immediately.

## Global controls

| | |
|---|---|
| `PUT /api/brightness` | `{"value": 0-100}` |
| `PUT /api/fps` | `{"value": 1-30}`: highest copper animation frame rate |
| `PUT /api/output` | `{"gamma": 2.2, "transition": 0.6, "max_commands": 200}`, any of them (advanced) |
| `POST /api/release` | Hand the LEDs back to the switch's own Etherlighting |
| `POST /api/resume` | Resume control of the LEDs |

`PUT /api/output` settings:

- `gamma` (1-3): the LEDs' brightness curve. Effects and brightness are in perceived terms; 1 turns the
  correction off.
- `transition` (0-5 s): how long a port takes to fade to a new color when its effect, zone or rule changes.
  Manual colors always apply at once.
- `max_commands` (5-200): requested port-command ceiling. Output starts at at most 30 and increases only on the
  direct SSH socket path, with fresh CPU and response-time evidence. The shell fallback stays capped at 30.
  `stats.command_budget` reports the current effective ceiling. An explicitly configured value of 30 remains 30.

## Fans

Fan control is available on the USW-Pro-XG-8-PoE. EE saves the fan mode for each switch, keyed by the same switch
identity as the switch's saved lighting. A switch EE has not controlled before starts in `switch`. The chosen mode
runs on the switch from RAM (`/tmp/ee-fan-control-v1`), so it continues while EE is closed, after logout and after
Quit. The engine checks it every 5 seconds and starts it again after a switch restart, and at the next connection
after a handoff. In `switch` mode the check stops EE fan control that has no owner line (started by releases up to
0.5.1) or that was started from this PC's saved settings, and returns the fans to the switch's own controller. It
leaves other fan control alone; see `hold`. EE writes an owner line with each setting it sends (the second line of
the controller's `config` file); the value is saved with the switch's fan file on this PC and never appears in an API
response.

### `GET /api/fans`

Needs a connected switch or the demo, and answers `null` until the session attaches fan control:

```json
{
  "supported": true,
  "unsupported_reason": null,
  "settings": {"mode": "curve", "curve": "medium", "manual_percent": 60, "target_celsius": 55},
  "reading": {
    "cpu_celsius": 62.3, "board_celsius": 56.0, "sfp_celsius": 57.8, "port_celsius": 80.1,
    "fans": [{"rpm": 3020, "percent": 55}, {"rpm": 3050, "percent": 55}],
    "daemon": "paused", "controller": "running", "script_current": true,
    "reason": "curve", "config": "curve 45 30 60 50 70 75 80 100", "duty": 55
  },
  "error": null,
  "settings_error": null,
  "hold": null,
  "curves": {"low": {"name": "Low", "points": [[55, 30], [70, 40], [80, 60], [90, 100]]}, "...": {}},
  "limits": {"manual_percent": [30, 100], "target_celsius": [45, 85]}
}
```

- `settings` is the mode saved for the connected switch. With `hold` `settings_unreadable` or `settings_newer` it is
  `switch`, which is not enforced.
- `reading` is `null` until the first sample. `port_celsius` is the hottest port chip. `daemon` is the switch's own
  fan controller: `running`, `paused` or `absent`. `controller` is EE's: `running`, `stopped` or `absent`. `config`
  is the setting line of EE's controller, without the owner line.
- `reason` names what set the speed in the last step: the mode, `guard` when a sensor near the switch's limit raised
  the speed (CPU 85-95 °C, SFP 65-75 °C, port chips 100-110 °C), or `fault` when a sensor could not be read and
  the fans ran at 100%. After six failed reads EE's control stops and the switch's controller resumes.
- `error` is the last fan command error, or else the message of a setting the switch runs but EE could not save
  (`fan_settings_save_failed`), or else `null`.
- `settings_error` says why the saved fan mode is not in use, for example because the fan file for this switch could
  not be read or was saved by a newer version of EE. It stays until a change is saved.
- `hold` is `null` when the check follows `settings`. `other_installation`: EE fan control from other settings, for
  example EE on another PC, is running; EE reads it and leaves it running. `settings_unreadable`: the fan file for
  this switch could not be read or created; EE does not change running fan control and only cleans up a stopped EE
  controller. A successful `PUT /api/fans` clears either of these holds. `settings_newer`: the fan file for this
  switch was saved by a newer version of EE. The check behaves as with `settings_unreadable`, but `PUT /api/fans`
  refuses every change, `switch` included, so the hold stays until EE is updated; Handoff to UniFi still returns the
  fans to the switch's own controller. `hold` is `null` when fan control is not supported.

### `PUT /api/fans`

Any of `{"mode": "switch" | "curve" | "manual" | "target", "curve": "low" | "medium" | "high" | "max",
"manual_percent": 30-100, "target_celsius": 45-85}`. Missing fields keep their saved values. The response is the
`GET /api/fans` object after the switch confirms the change. The change is saved for the connected switch only. It
replaces EE fan control started from other settings, and `switch` stops any EE fan control.

An invalid value returns `400 invalid`; a change the switch does not confirm returns `409 fans_unconfirmed` and
keeps the previous settings, including an unreadable fan file and its `hold`. A model without fan control returns
`400 invalid` with the reason. So does any change, `switch` included, while the fan file for this switch was saved
by a newer version of EE (`hold` `settings_newer`); nothing is sent to the switch and the file is not overwritten. A
change that arrives while Handoff to UniFi stops fan control returns `409 fans_unconfirmed` with the message "Fan
control is stopping for Handoff to UniFi.". When the switch runs the new setting but EE cannot write the fan file, the
response is `500 fan_settings_save_failed` with the message "The switch runs the new fan setting, but EE could not
save it on this PC. Check the data folder and choose the setting again." The new setting stays in effect for the
session, and the same message stays in `error` until a later save succeeds.

- `curve` sets the fan speed from the CPU temperature along the curve's points, interpolated, and slows down by at
  most 2% per 5-second step.
- `manual` runs both fans at `manual_percent`.
- `target` adjusts the speed to keep the CPU at or below `target_celsius` with the lowest fan speed. A new target
  starts at the speed estimated to hold it, from the CPU reading and the average speed of the last couple of
  minutes, and corrects from there without running the fans far above that speed.
- A new mode or setting takes effect within about a second and starts at its own speed; it does not ease from the
  previous mode's speed.

The same object is `fans` in `GET /api/state` and WebSocket state messages, and arrives every 5 seconds as a
`fans` WebSocket message.

### Saved fan settings

Each switch's fan mode is saved in `device-fans/<identity>.json` in the data directory as
`{"version": 1, "identity": "<64 hex>", "owner": "<48 hex>", "settings": {...}}`. A file that holds no JSON object
is moved aside as `<identity>.json.corrupt-<UTC time>`. A file that cannot be read, or that has an invalid format
version, another identity, an invalid owner or invalid settings, sets `hold` to `settings_unreadable` and stays where
it is until the next successful `PUT /api/fans` replaces it. A file with a newer format version sets `hold` to
`settings_newer` and is never replaced. The demo keeps `fans-demo.json`.

Releases up to 0.5.1 saved one fan mode for every switch in `fans.json`. When no switch has a fan file yet, the first
USW-Pro-XG-8-PoE that EE connects to receives that mode. Saving it sends nothing to the switch, and the fan control
that 0.5.1 started there has no owner line, so EE adopts it. Because `fans.sh` changed after 0.5.1 (`script_current`
is `false`), the first check on that switch restarts fan control once with the same mode and adds the owner line;
while it restarts, the switch's own controller runs the fans. `fans.json` stays in place and is no longer read, so
release 0.5.1 still finds its own setting after a downgrade; it ignores `device-fans/`. When `fans.json` or
`device-fans/` cannot be read, or the new file cannot be written, the switch gets `hold` `settings_unreadable` and
the next connection tries again.

## Presets

A preset stores zones, rules, brightness and manual colors.

| | |
|---|---|
| `GET /api/presets` | `{"Movie night": {...}, ...}` |
| `POST /api/presets` | `{"name": "Movie night"}` creates a profile from the current setup; existing names return 409 |
| `POST /api/presets/{name}/apply` | Load a preset |
| `DELETE /api/presets/{name}` | Delete a preset |
| `POST /api/presets/{name}/duplicate` | `{"name":"New name"}` copies the saved snapshot without applying it |
| `POST /api/presets/{name}/rename` | `{"name":"New name"}` renames the saved snapshot without applying it |

Apply uses the same scene transaction as `PUT /api/scene` with `confirm_output`, and returns the state after
the switch acknowledges the SFP output. It returns `409 output_unconfirmed` when that output is not confirmed,
and `503 offline_scene_unconfirmed` when the lighting is applied in EE but the switch has not confirmed its offline
copy.

To save a preview without applying it, `POST /api/presets` accepts `scene` and
`expected_revision` alongside `name`. The revision must match the current scene
(`409 scene_changed`), and a `scene` without it returns `400 scene_revision_required`;
the supplied scene uses the same topology, capability, and setting validation as
Apply. Saving leaves engine output and the current scene unchanged. This endpoint creates
profiles only and answers 201 with the preset mapping. An existing name returns
`409 already_exists`, including when an older client sends `overwrite: true`. Omitting `scene` saves the applied
setup.

Rename and duplicate return the updated preset mapping; duplicate answers 201. An existing target name returns
`409 already_exists`; a missing source returns `404 not_found`; an invalid name returns `400 invalid`. Names contain
1–64 letters, numbers, spaces, periods, underscores, or hyphens. A failed save preserves the original file and returns
`409 profile_storage_unavailable`. `DELETE /api/presets/{name}` returns `409 profile_in_use` while enabled schedules
use the profile.

### Stable profile records

`GET /api/profiles` returns `{"profiles":[{"id":"...","revision":1,"name":"Evening","scene":{...},"schedules":[]}]}`.
The ID survives rename. Duplicate creates a different ID. `schedules` lists dependent schedule
IDs, names, and enabled states. Legacy name-to-scene storage migrates atomically before IDs are returned.
`GET /api/presets` retains its name-to-scene response for older clients.

`PATCH /api/profiles/{id}` requires `expected_revision` and one of these actions:

- `{"action":"replace","expected_revision":1}` replaces this existing profile with the applied setup.
- `{"action":"rename","name":"Night","expected_revision":1}` changes its name.
- `{"action":"duplicate","name":"Night copy","expected_revision":1}` copies the saved setup to a new ID.
- `{"action":"delete","expected_revision":1,"disable_schedules":true}` deletes it and disables enabled dependent schedules.

The response is the updated `/api/profiles` envelope. A missing or changed identity returns
409 `profile_changed`, so a stale editor cannot recreate a renamed/deleted profile. Deleting a profile
with enabled dependent schedules requires `disable_schedules: true`; otherwise it returns
409 `profile_in_use` with the affected schedules. Existing name-based delete also rejects enabled
dependents. Profile mutations do not change live output. The browser validates saved topology before
Apply, while the engine remains authoritative.

### Scene undo

`GET /api/scene` returns `{"scene":{...},"revision":"..."}`. The scene includes groups,
rules, individual port colors, brightness, and SFP settings. The revision hashes canonical
scene content and treats integral JSON numbers such as `6` and `6.0` equally.

Version 3 scenes include `topology: {version: 1, model, copper_ports, sfp_ports}`. The model is the detected
profile ID, or null for a simulator. Port lists must match the current layout before any scene state changes.
Version 2 scenes retain their original eight-copper-port interpretation. Version 1 scenes containing only
an all-port effect can migrate to the current copper layout; port-specific legacy scenes require their
original layout. A topology mismatch returns 400 and leaves the current scene unchanged.

`PUT /api/scene` accepts `{"scene":{...},"expected_revision":"..."}`. The revision must
match the current scene before the existing snapshot validator restores the requested
setup. A changed scene returns 409; invalid settings return 400. The response contains
the restored scene and its revision. This operation preserves release/resume mode.

Names are 1-64 characters: letters, digits, spaces, `.`, `_` or `-`. The current setup is also saved
automatically and restored on the next login.

## Schedules

| | |
|---|---|
| `GET /api/schedules` | `{"schedules": [...], "actions": [...], "history": [...]}` |
| `POST /api/schedules` | Add a schedule |
| `PATCH /api/schedules/{id}` | Change part of a schedule |
| `DELETE /api/schedules/{id}` | Delete a schedule |
| `POST /api/schedules/{id}/run` | Run a schedule now; returns the history entry |

```json
{
  "id": "349952d5",
  "name": "Evening",
  "enabled": true,
  "at": "19:00",
  "days": ["mon", "tue", "wed", "thu", "fri"],
  "action": { "type": "brightness", "value": 40 },
  "next_run": "2026-10-29T19:00"
}
```

Actions: `{"type":"preset","profile_id":"..."}`, `{"type":"brightness","value":40}`,
`{"type":"release"}`, and `{"type":"resume"}`. A profile action accepts a legacy `preset` name at
creation, resolves it immediately to a stable ID, and returns its current name for display. Rename
and name reuse never retarget a bound schedule. On migration, unresolved legacy references are
disabled and assigned a missing ID for explicit review. Saving another profile with that name does
not reconnect the schedule.

Omitted `days` defaults to every day. An explicit empty list or null is rejected. Times are on the EE
computer in its local time zone. `/api/schedules` includes `clock`, `history_available`, and the last
100 persisted runs. Each schedule includes `revision`, `last_run`, `needs_device_confirmation`,
`profile_missing`, and `conflicts`. `clock` is `null` while no switch is connected. Save binds the schedule to the
connected switch and returns `409 not_logged_in` without one; `POST` answers 201 with the schedule, and `DELETE`
returns the remaining schedules. Browser edits, Delete, and Run now send `expected_revision`; a changed schedule
returns 409 `schedule_changed`. Run now returns `409 schedule_confirmation_required` for a schedule that has not been
applied for the connected switch. A schedule file that cannot be read or saved, or that a newer version of EE saved,
returns `409 schedule_storage_unavailable`.

The service writes an occurrence claim before any scheduled mutation. Its key contains schedule ID,
bound device, local calendar date, and minute. Restart during that minute cannot repeat it. Both
folds of a repeated DST minute count once. A skipped local minute is not caught up. Competing actions
run in saved list order, and the editor asks for confirmation before saving a same-minute conflict.
Manual Run now records a separate trigger and does not consume a timed occurrence.

A run starts as pending. Production execution waits for the existing release cleanup or relevant SFP
output acknowledgments before reporting `ok`. Failure or interruption remains failed/unconfirmed.
On restart, an unfinished persisted run displays an unconfirmed result and is not replayed. If the
occurrence journal cannot be read or claimed, scheduled writes stop until its history can be verified.

### Rule tester

`POST /api/rules/test` accepts `{"port":1,"at":"2026-11-04T18:30","rules":[...]}`.
`at` is an optional local date/time on the EE computer; offsets are rejected. The endpoint parses the
supplied rule order and evaluates the engine's current port facts without writing a scene or lighting.
The response lists each condition's actual value and pass result, rule scope/enabled state, the first
matching rule, the effective source (including individual-color precedence), current output mode,
and the evaluated scene revision. Optical rules use the same explicit scope and native-setting
eligibility as the engine. Changing the supplied order tests priority without saving it.

## Saved effect settings and port selections

`GET /api/library/{kind}` returns `{"items":[...]}` for `kind` equal to `effect` or `selection`.
Each item contains `id`, `revision`, `kind`, `name`, `binding` (an opaque switch scope for selections, `null` for
effect settings) and `value`. Effect settings are shared across switches.
Selections are bound to the connected switch and retain its port topology.

`POST /api/library/{kind}` creates an item with `{"name":"...","value":{...}}`.
An effect value is `{"id":"activity_glow","params":{...}}` and uses the existing effect validator.
A selection value is `{"ports":[1,3,9]}` and must use available ports. The stored selection also includes
its topology. Neither operation changes a lighting scene or selection in another client.

`PUT /api/library/{kind}/{id}` replaces or renames an item with `name`, `value`, and
`expected_revision`. `DELETE` on the same path requires `{"expected_revision":"..."}` and returns
`{"deleted": true}`. `POST` answers 201 and `PUT` 200 with the saved item.
Missing, changed, or out-of-scope items and a name already used in one kind/scope return 409 `library_changed`.
The host bounds the library to 256 items and preserves unreadable storage for review.
Recalling an effect creates a local lighting preview. Recalling a selection only selects ports.

These refusals carry only `message`: 404 for another `kind`, 400 for an invalid name or value or a full library,
and 503 when the library file cannot be read or saved.

## Port identification

`POST /api/identify` accepts `{"port": 3}` and starts a ten-second identification on one qualified
lighting port. It returns `id`, `port`, and `seconds_remaining`. Send that exact ID with
`DELETE /api/identify`, for example `{"id": "<returned-id>"}`, to stop early. A stale ID cannot stop a
newer identification. An overlapping start is rejected until the current identification finishes. `port` is a JSON
integer from 1 to 128 and `id` a 32-character string; other fields return 422. `DELETE` returns
`{"identify": <the current identification or null>}`.

Identification requires a connected, running output session owned by EE. The host owns the timer.
The temporary white/blue indicator does not alter the scene or its revision. Expiry and Stop resume
the current rules, effects, colors, and brightness, including changes applied during identification.
Release, disconnect, and shutdown cancel the temporary intent. SFP identification uses the existing
qualified optical output and release paths. An SFP following an identified Ethernet port continues
showing the Ethernet port's underlying lighting.

State and frame messages contain `identify`, either `null` or the same active identification object.
The command response records accepted intent. Existing port output and SFP control fields continue
to report the displayed and acknowledged output state.

## Lighting history

`GET /api/history` returns `entries`, `limit`, and `error`. Entries contain an opaque `id`, a UTC ISO
`at` timestamp, the scene `revision`, and the saved `scene`. Newest entries come first. History retains
up to 50 distinct successive scenes for the connected device and 250 entries across stored devices.
Identical successive scenes are deduplicated. Scene changes made by schedules and other clients are
included. Temporary identification and telemetry are excluded.

`POST /api/history/{id}/restore` requires `{"expected_revision": "<current-scene-revision>"}`. Read
`GET /api/scene` when opening the review and retain that revision through confirmation. Restore uses
the normal scene transaction, topology validation, revision guard, and SFP output confirmation.
An intervening edit returns `scene_changed` without replacing it. A successful restore creates a new
history entry so the replaced scene remains available.

History is scoped to the existing stable device binding, with a separate demo scope. A missing or
invalid history store returns `history_unavailable`; the original file remains in place. A save
failure is also exposed as `history_error` in state and frame messages and `error` in the history
response. It does not roll back an already applied lighting change.

## Port telemetry and PoE budget

State and frame messages contain `telemetry` with `generation`, `sequence`, `sampled_at`,
`sample_age_ms`, and `stale`. A sequence advances once per collected link sample, independently of
frame delivery. The opaque generation changes after reconnection. Use both fields to deduplicate
samples and begin a new event/trend window after a monitoring interruption. `sampled_at` is an ISO
host timestamp with its UTC offset. Clients also expire readings when incoming samples stop.

Per-port `traffic_bps` is combined receive plus transmit bits per second. Its corresponding
`facts.traffic` is Mbps. Missing samples, the first counter observation, and counter resets produce
`null`; two valid unchanged counter samples produce zero. These fields do not split upload and
download. The separate uplink bandwidth object retains its directional measurements.

The `poe_summary` object provides the reported switch-wide `budget_w`, measured `draw_w`,
`headroom_w`, `complete`, `sample_age_ms`, and `consumers`. Consumers contain `port` and `power_w`,
sorted by draw. All power values are watts. The budget comes from the parsed switch response.

| Status | Meaning |
| --- | --- |
| `ready` | Fresh complete table and reported budget; headroom is budget minus measured draw |
| `partial` | Some expected copper-port measurements are missing or malformed |
| `stale` | The sample aged out or the connection was interrupted |
| `budget_unavailable` | Complete fresh measurements without a reported total budget |
| `unavailable` | No parsed PoE measurements |

Only `ready` supplies a numeric headroom. A partial draw is the measured subset and must be labeled
as such. Negative headroom is retained if the measured draw exceeds the reported budget. The current
adapter requires a valid measurement row for every copper port before declaring a complete sample.
It uses the existing telemetry collection interval and adds no switch polling.

## Catalogs

| | |
|---|---|
| `GET /api/effects` | Effects and their settings |
| `GET /api/rules/fields` | Rule fields, operators and styles |
| `GET /api/rules/templates` | Ready-made rules |
| `GET /api/colors` | Named colors (`{"white": "#ffffff", ...}`) for swatches |
| `GET /api/about` | Name, version and disclaimer ([`GET /api/about`](#get-apiabout)) |
| `GET /api/health` | Service status ([`GET /api/health`](#get-apihealth)) |

These endpoints work without logging in.

## WebSocket: `/api/ws`

When an API token is configured, clients authenticate with an `Authorization: Bearer <token>` header. A missing
or wrong header closes the socket with code 4401. The token is not accepted as a query parameter, so it never
appears in URLs or access logs. The web gateway supplies the header for REST edits and WebSocket telemetry. The
engine closes a socket from another website's page with code 4403, and the local gateway drops an upgrade that fails
its `Host` and `Origin` checks or that is not exactly `/api/ws`.

Writable sockets are bound to the switch session present when the socket opens.
After login or session replacement, reopen the socket before sending changes.
A stale target returns `workspace_changed`. Optional `X-EE-Workspace` handshake
and `workspace_id` message values must match the original bound target; they do
not retarget an existing socket. Ping and session telemetry remain available. A command while no switch is connected
or a handoff is pending returns `not_logged_in`.

State and frame payloads include `schedule_clock` with `local_iso` (an ISO local
timestamp with UTC offset) and `zone_name` (the host OS display name). Use this
clock for schedule and Daylight previews. Re-anchor to each received sample;
`zone_name` is not an IANA timezone identifier. Clock metadata is not saved in
scene or profile snapshots.

Messages from the server:

| `type` | |
|---|---|
| `session` | `{"type": "session", "session": {...}}` on connect, and on every login, logout or connection change |
| `state` | `{"type": "state", "state": {...}}` on connect, and after every change from any client |
| `frame` | `{"type": "frame", "mode": "running", "connected": true, "ports": [...], "sfp": [...], ...}` every frame |
| `fans` | `{"type": "fans", "fans": {...}}` every 5 seconds, every 1.5 seconds for 30 seconds after a fan change, and after each change; see [Fans](#fans) |
| `ack` | `{"type": "ack", "id": 7}` when a message with an `id` has been applied |
| `pong` | `{"type": "pong", "id": 7}`, the reply to `ping` |
| `error` | `{"type": "error", "id": 7, "code": "invalid", "message": "..."}` |

The engine sends frames at its frame rate (`fps` in the state, 25 by default, set with `PUT /api/fps`), at up to 50
per second while SFP indicators animate, and soon after each change, at most 30 per second unless the frame rate is
higher. A frame carries `mode`, `connected`, `error`, `reconnect_required`, `stats`, `ports`, `sfp`, `sfp_sync`,
`uplink_bandwidth`, `schedule_clock`, `identify`, `telemetry`, `poe_summary`, `history_error` and `controller`, as in
the state. `error` codes are `invalid` (also for a message that is not an
object), `not_logged_in`, `workspace_changed`, `not_found` and `output_unconfirmed`.

Messages to the server (add an `id` to any of them to get an `ack` or `error` back):

```json
{ "type": "set_port", "port": 3, "color": "#bf5af2", "brightness": 80 }
{ "type": "set_ports", "ports": { "1": { "color": "#ff0000" }, "2": { "color": "#00ff00" } } }
{ "type": "clear_port", "port": 3 }
{ "type": "clear_port" }
{ "type": "set_effect", "id": "rainbow", "params": { "speed": 0.5 }, "zone": "desk" }
{ "type": "set_params", "params": { "speed": 0.8 }, "zone": "desk" }
{ "type": "update_rule", "id": "0ee7bc65", "changes": { "look": { "color": "#ff375f" } } }
{ "type": "set_brightness", "value": 40 }
{ "type": "release" }
{ "type": "resume" }
{ "type": "ping" }
```

A message with any other `type` gets an `error` with code `invalid`. Every command is available to devices on
Tailscale and the LAN. An action that must stay on the host computer needs a REST route. A `release` is acknowledged
after the switch has taken the released output.

`zone` is optional; without it the first zone is used. `clear_port` without a port clears all ports. Mutation
messages apply immediately; the browser sends lighting changes only after Apply. Actual switch writes follow
the configured output settings and adaptive command budget. If a client reads slowly, it gets the newest state,
frame and fan reading and skips older ones; acks and errors are never dropped.

## Simulator controls

These only work after a demo login.

| | |
|---|---|
| `PUT /api/simulator/links/{port}` | `{"up": false}` unplugs a cable, `{"up": true}` plugs it back in |
| `PUT /api/simulator/anomaly/{port}` | `{"code": 4}` raises an anomaly, `{"code": 0}` clears it |
| `PUT /api/simulator/poe/{port}` | `{"class": 4}` connects a PoE device of that class, `{"class": null}` removes it |

Each returns its port and new value, for example `{"port": 3, "up": false}`, `{"port": 3, "code": "0x4"}` or
`{"port": 2, "class": 4}`. `class` is 0 to 8, and a port without PoE returns `400 invalid`. Without a demo login the
answer is `409 not_simulated`.

The simulator starts with all 8 ports linked at mixed speeds, port 5 as the uplink, and PoE devices on ports 2
(class 4, PoE+), 4 (class 3, PoE), 6 (class 6, PoE++) and 7 (class 8, PoE+++). Link changes appear within about a
second.

## Port display names

`GET /api/port-names` returns `{revision, use_unifi, names}` for the authenticated switch identity.
`names` maps physical port numbers (string keys) to saved manual display names. The default `use_unifi` is true.
`PUT /api/port-names` accepts `{expected_revision, use_unifi, names}`. Each supplied name updates that port;
an empty value clears it. Other port names are retained. Names accept up to 64 characters on one line.
The current revision is required; concurrent edits return `409 port_names_changed` and require reloading. Send
`X-EE-Workspace` to guard against a switch change. Storage failures, and names saved by a newer version of EE, return
`503 port_names_unavailable` without accepting an in-memory-only edit. An invalid name returns 400 with only
`message`.

Port details → Edit names opens the naming editor. Manual names are saved per switch on the host, shared
with other workspace clients, and retained when UniFi naming is selected. Clearing a manual name displays
the physical port number. With UniFi naming selected, configured port names take priority, followed by the
existing connected-device names. Existing controller refreshes update automatic names.

Port arrays from REST and WebSocket frames include `name_mode` (`manual` or `unifi`) and `display_name`
(a resolved custom name or null). Controller metadata retains client information and adds `port_name`
when a configured name is reported. Naming preferences have their own atomic storage and revision;
they do not change the lighting scene or send switch/controller configuration commands.
