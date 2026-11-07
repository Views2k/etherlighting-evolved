# EE API

This document describes the API used by the EE workspace and other clients.

- `npm start` serves the workspace at `http://127.0.0.1:4180` and forwards `/api` and `/api/ws` to its private
  engine service. See [INTEGRATION.md](INTEGRATION.md) for the gateway and session flow.
- The separate CLI server defaults to `http://localhost:8787` and serves interactive OpenAPI documentation
  at `/docs`. That documentation route belongs to the CLI server, not the workspace gateway.
- Request and response bodies are JSON. Colors are `"#rrggbb"` strings. The server also accepts `"f0f"`, names
  such as `"white"`, `[r, g, b]` and `{"r":..,"g":..,"b":..}`, but always returns `#rrggbb`.
- Ports are numbered from 1, matching the labels on the switch. Use the state `ports` and `sfp` arrays for
  controllable port membership. `hardware_profile` describes the detected physical layout; its stacking
  ports are not lighting targets. Optical-only models have an empty copper `ports` array.
- Times use the local time of the machine running EE, in 24-hour `"HH:MM"` format.

## Switch playback

On a qualified Pro XG 8 PoE, `POST /api/session/switch-playback` transfers the current scene to switch RAM
using the saved SSH login. The desktop writer drains before the switch starts. A successful response requires
an acknowledged LED frame. Activity Glow uses local port counters. Decorative effects use a 30-second loop.
Enabled rules and other desktop time/data effects must be removed from the scene before transfer.

Session info includes `can_save_to_switch` and `switch_playback`. Use `POST /api/session/switch-playback/check`
for a fresh reading. Quit and logout leave the player running. The existing handoff endpoint stops the player
and restores the lighting controller. An unreachable switch leaves handoff pending for retry when EE is open.
A switch reboot clears the scene from RAM.

## UI flow

State and frame messages include `uplink_bandwidth`, measured from the existing physical-port byte counters.
Upload is transmitted traffic and download is received traffic across the detected active uplink ports.
New measurements publish on five-second deadlines using the latest captured counters. Rates use the actual
elapsed time between counter samples; `sample_period_ms` records that interval, which can differ from the
publication interval. A missing counter sample does not produce a new measurement.
Each physical uplink is counted once, including optical uplinks. Virtual aggregation rows are excluded.

| Field | Meaning |
| --- | --- |
| `status` | `waiting`, `ready`, `stale`, `no_uplink`, or `unavailable` |
| `ports` | Physical uplink port IDs included in the sample |
| `upload_bps`, `download_bps` | Directional bits per second, or `null` when a reading is unavailable |
| `history` | Measured intervals from the last minute as `[offset_ms, upload_bps, download_bps]`, relative to the newest sample. Clears when counters or uplink membership reset. |
| `sample_period_ms`, `sample_age_ms` | Measurement duration and age in milliseconds |
| `sequence` | Increases for each completed measurement |
| `refresh_interval_ms` | Publication interval, currently `5000` |

Counter resets, uplink membership changes, and reconnection start a new measurement window. Clients must expire
readings when their age reaches ten seconds, including when frame delivery stops. Visual number transitions
interpolate between measured results; they do not create additional traffic measurements.

`POST /api/session/recover` revalidates the current switch and cleans up its owned output before resuming
the retained scene after an interrupted connection. It preserves saved credentials and rejects changed
device identity, changed capabilities, or unconfirmed helper cleanup. State/frame `reconnect_required`
indicates that explicit recovery is needed. Pending handoff must finish before recovery can resume lighting.

1. Subscribe to session messages at `/api/ws` and call `GET /api/session`. If `logged_in` is true, load the
   main screen. Otherwise show the login screen, filled in from `remembered` if present. While `connecting`
   is true, show saved-login progress; a later session message reports completion or an actionable error.
2. The login screen has fields for IP address, SSH username and password, plus a *Save login* option.
   It submits to `POST /api/session/login`. Use saved login calls `POST /api/session/restore` without sending
   credentials from the browser. Connection options contains the optional API token.
3. Keep the same WebSocket after entering the workspace. It sends the session, the full state, and then a
   frame with every port about 10 times per second. Draw the ports from the frames.
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

| Status | Code | Meaning |
|---|---|---|
| 400 | `missing_host`, `missing_username`, `bad_port` | Login form incomplete |
| 400 | `invalid` | Invalid request; the message gives the reason |
| 401 | `auth_failed` | Wrong SSH username or password |
| 401 | `bad_token` | API token missing or wrong (only when the server has one set) |
| 404 | `not_found` | No such zone, rule, preset or schedule |
| 409 | `not_logged_in` | Not logged in to a switch |
| 409 | `host_key_changed` | The switch's SSH host key doesn't match the saved one |
| 409 | `not_simulated` | Simulator controls require a demo login |
| 422 | `no_etherlighting` | Logged in, but the device has no Etherlighting |
| 500 | `saved_login_failed` | Saving failed; sign in with Save login turned off |
| 500 | `saved_login_forget_failed` | The saved login could not be removed |
| 502 | `unreachable`, `ssh_error` | The switch couldn't be reached, or SSH failed |

`host_key_changed` means the switch's SSH host key differs from the one saved at the first login. This happens
when the switch is replaced or reset, or when a different device answers at that address. To accept the new key,
call `POST /api/session/forget-host-key` with `{"host": "...", "port": 22}` and log in again.

If the server has an API token, send `Authorization: Bearer <token>` with every request. The browser UI uses
authenticated REST polling for these connections so the token stays out of URLs and access logs.

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
  "error": null,
  "remembered": { "host": "192.168.1.2", "port": 22, "username": "admin" },
  "has_saved_login": true,
  "saved_login_error": null
}
```

`connected` can become false while logged in, for example when the switch reboots. EE reconnects automatically,
and `error` holds the last connection error until it does.

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

### `POST /api/session/restore`

Takes no request body and reconnects using the server's protected saved switch login. Returns the session.
If a real switch session already exists, returns it unchanged. Concurrent restore requests share one connection
attempt. A demo session remains active until the saved real-switch login succeeds.

The server publishes `connecting: true` and the final session transition. Missing saved credentials return
HTTP 409 with `no_saved_login`; an unreadable saved record returns HTTP 409 with `saved_login_unavailable`.
Connection errors use the normal login error contract. The saved record is retained, and its password is never
returned to the browser.

### `POST /api/session/forget`

Deletes the saved login, returns the updated session and publishes a session message. The current switch
connection keeps running. This also removes an unreadable record when the user explicitly chooses Forget.

### `POST /api/session/logout`

Stops EE from controlling the LEDs. The switch's own colors return within about two seconds. `?forget=true` also
deletes the remembered login.

### `GET /api/about`

Returns the name, version, tagline and the unofficial-project disclaimer. The disclaimer should be visible in the
UI, for example on the login screen and an About page.

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

By default, credentials and session cookies remain in server memory for this process. Saving requires the
separate explicit preferences action below; connecting does not save or replace a saved login.
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
It saves that session's canonical HTTPS origin, controller type, username, password, site and approved certificate pin under
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
  value to use when drawing the port.
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

To save a preview without applying it, `POST /api/presets` accepts `scene` and
`expected_revision` alongside `name`. The revision must match the current scene;
the supplied scene uses the same topology, capability, and setting validation as
Apply. Saving leaves engine output and the current scene unchanged. This endpoint creates
profiles only. An existing name returns HTTP 409, including when an older client sends
`overwrite: true`. Omitting `scene` saves the applied setup.

Rename and duplicate return the updated preset mapping. An existing target name returns
409; a missing source returns 404; an invalid name returns 400. Names contain 1–64 letters,
numbers, spaces, periods, underscores, or hyphens. A failed save preserves the original file.

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
`profile_missing`, and `conflicts`. Save binds the schedule to the connected switch. Browser edits,
Delete, and Run now send `expected_revision`; a changed schedule returns 409 `schedule_changed`.

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
Each item contains `id`, `revision`, `name`, and `value`. Effect settings are shared across switches.
Selections are bound to the connected switch and retain its port topology.

`POST /api/library/{kind}` creates an item with `{"name":"...","value":{...}}`.
An effect value is `{"id":"activity_glow","params":{...}}` and uses the existing effect validator.
A selection value is `{"ports":[1,3,9]}` and must use available ports. The stored selection also includes
its topology. Neither operation changes a lighting scene or selection in another client.

`PUT /api/library/{kind}/{id}` replaces or renames an item with `name`, `value`, and
`expected_revision`. `DELETE` on the same path requires `{"expected_revision":"..."}`.
Missing, changed, or out-of-scope items return 409 `library_changed`. Duplicate names in one kind/scope
are rejected. The host bounds the library to 256 items and preserves unreadable storage for review.
Recalling an effect creates a local lighting preview. Recalling a selection only selects ports.

## Port identification

`POST /api/identify` accepts `{"port": 3}` and starts a ten-second identification on one qualified
lighting port. It returns `id`, `port`, and `seconds_remaining`. Send that exact ID with
`DELETE /api/identify`, for example `{"id": "<returned-id>"}`, to stop early. A stale ID cannot stop a
newer identification. An overlapping start is rejected until the current identification finishes.

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

These endpoints work without logging in.

## WebSocket: `/api/ws`

When an API token is configured, desktop clients authenticate with an `Authorization: Bearer <token>`
header. The web gateway supplies authentication for REST edits and WebSocket telemetry so the token stays out of browser URLs. The protocol also accepts
a `token` query parameter; a supplied Authorization header takes precedence and an invalid header cannot be
overridden by a query token.

Writable sockets are bound to the switch session present when the socket opens.
After login or session replacement, reopen the socket before sending changes.
A stale target returns `workspace_changed`. Optional `X-EE-Workspace` handshake
and `workspace_id` message values must match the original bound target; they do
not retarget an existing socket. Ping and session telemetry remain available.

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
| `frame` | `{"type": "frame", "mode": "running", "connected": true, "ports": [...]}` every frame (about 10 per second) |
| `ack` | `{"type": "ack", "id": 7}` when a message with an `id` has been applied |
| `pong` | Reply to `ping` |
| `error` | `{"type": "error", "id": 7, "code": "invalid", "message": "..."}` |

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

`zone` is optional; without it the first zone is used. `clear_port` without a port clears all ports. Mutation
messages apply immediately; the browser sends lighting changes only after Apply. Actual switch writes follow
the configured output settings and adaptive command budget. If a client reads slowly, it gets the newest state
and frame and skips older ones; acks and errors are never dropped.

## Simulator controls

These only work after a demo login.

| | |
|---|---|
| `PUT /api/simulator/links/{port}` | `{"up": false}` unplugs a cable, `{"up": true}` plugs it back in |
| `PUT /api/simulator/anomaly/{port}` | `{"code": 4}` raises an anomaly, `{"code": 0}` clears it |
| `PUT /api/simulator/poe/{port}` | `{"class": 4}` connects a PoE device of that class, `{"class": null}` removes it |

The simulator starts with all 8 ports linked at mixed speeds, port 5 as the uplink, and PoE devices on ports 2
(class 4, PoE+), 4 (class 3, PoE), 6 (class 6, PoE++) and 7 (class 8, PoE+++). Link changes appear within about a
second.

## Port display names

`GET /api/port-names` returns `{revision, use_unifi, names}` for the authenticated switch identity.
`names` maps physical port numbers (string keys) to saved manual display names. The default `use_unifi` is true.
`PUT /api/port-names` accepts `{expected_revision, use_unifi, names}`. Each supplied name updates that port;
an empty value clears it. Other port names are retained. Names accept up to 64 characters on one line.
The current revision is required; concurrent edits return 409 and require reloading. Send `X-EE-Workspace`
to guard against a switch change. Storage failures return 503 without accepting an in-memory-only edit.

Port details → Edit names opens the naming editor. Manual names are saved per switch on the host, shared
with other workspace clients, and retained when UniFi naming is selected. Clearing a manual name displays
the physical port number. With UniFi naming selected, configured port names take priority, followed by the
existing connected-device names. Existing controller refreshes update automatic names.

Port arrays from REST and WebSocket frames include `name_mode` (`manual` or `unifi`) and `display_name`
(a resolved custom name or null). Controller metadata retains client information and adds `port_name`
when a configured name is reported. Naming preferences have their own atomic storage and revision;
they do not change the lighting scene or send switch/controller configuration commands.
