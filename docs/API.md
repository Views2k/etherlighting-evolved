# EE API

This document describes the API used by the EE web UI. A running server also has interactive OpenAPI docs at
`/docs`.

- The server runs at `http://localhost:8787` by default. The UI is served from `/` and the API from `/api`.
- Request and response bodies are JSON. Colors are `"#rrggbb"` strings. The server also accepts `"f0f"`, names
  such as `"white"`, `[r, g, b]` and `{"r":..,"g":..,"b":..}`, but always returns `#rrggbb`.
- Ports are numbered from 1, matching the labels on the switch. A USW-Pro-XG-8-PoE has 8 LED ports.
- Times use the local time of the machine running EE, in 24-hour `"HH:MM"` format.

## UI flow

1. Call `GET /api/session`. If `logged_in` is true, show the main screen. Otherwise show the login screen,
   filled in from `remembered` if present.
2. The login screen has fields for IP address, SSH username and password, a *Remember me* option, and the SSH
   port under advanced settings. It submits to `POST /api/session/login`. A login takes a second or two.
3. Open the WebSocket at `/api/ws`. It sends the session, the full state, and then a frame with every port about
   10 times per second. Draw the ports from the frames.
4. Continuous controls (color pickers, brightness and speed sliders) send WebSocket messages on every change.
   Everything else uses REST. Every change, from any client, is also sent to all open WebSockets as a `state`
   message.

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
| 502 | `unreachable`, `ssh_error` | The switch couldn't be reached, or SSH failed |

`host_key_changed` means the switch's SSH host key differs from the one saved at the first login. This happens
when the switch is replaced or reset, or when a different device answers at that address. To accept the new key,
call `POST /api/session/forget-host-key` with `{"host": "...", "port": 22}` and log in again.

If the server has an API token, send `Authorization: Bearer <token>` with every request and add `?token=<token>`
to the WebSocket URL.

## Session

### `GET /api/session`

```json
{
  "logged_in": true,
  "connected": true,
  "host": "192.168.1.2",
  "port": 22,
  "username": "admin",
  "simulated": false,
  "switch": { "hostname": "USW-Pro-XG-8-PoE", "etherlight_mcu": true, "port_rgb": true },
  "error": null,
  "remembered": { "host": "192.168.1.2", "port": 22, "username": "admin" }
}
```

`connected` can become false while logged in, for example when the switch reboots. EE reconnects automatically,
and `error` holds the last connection error until it does.

### `POST /api/session/login`

```json
{ "host": "192.168.1.2", "username": "admin", "password": "...", "port": 22, "remember": true }
```

Returns the session as above, or an error. When switching to a different switch, the current session keeps
running until the new login succeeds.

### `POST /api/session/logout`

Stops EE from controlling the LEDs. The switch's own colors return within about two seconds. `?forget=true` also
deletes the remembered login.

### `GET /api/about`

Returns the name, version, tagline and the unofficial-project disclaimer. The disclaimer should be visible in the
UI, for example on the login screen and an About page.

## State

### `GET /api/state`

```json
{
  "mode": "running",
  "connected": true,
  "error": null,
  "switch": { "hostname": "USWProXG8PoE-SIM", "etherlight_mcu": true, "port_rgb": true },
  "num_ports": 8,
  "fps": 10.0,
  "brightness": 100,
  "dimming": "level",
  "zones": [
    { "id": "all", "name": "All ports", "ports": [1, 2, 3, 4, 5, 6, 7, 8],
      "effect": { "id": "static", "name": "Static", "params": { "color": "#ffffff" } } }
  ],
  "rules": [
    { "id": "e00e3872", "name": "Anomaly", "enabled": true, "match": "all", "ports": null,
      "conditions": [{ "field": "anomaly", "op": "is", "value": true }],
      "look": { "color": "#ff9f0a", "style": "blink", "rate": 2.0, "brightness": null } },
    { "id": "0ee7bc65", "name": "Offline", "enabled": true, "match": "all", "ports": null,
      "conditions": [{ "field": "link", "op": "is", "value": false }],
      "look": { "color": "#ff0000", "style": "solid", "rate": 1.0, "brightness": null } }
  ],
  "overrides": { "3": { "color": "#bf5af2", "brightness": null } },
  "revision": 12,
  "stats": { "frames": 1830, "commands": 2401, "last_write_ms": 6.1, "avg_write_ms": 6.4 }
}
```

- `mode` is `running`, or `released` when the LEDs have been handed back to the switch.
- `revision` increases with every change.
- `stats` shows how long writes to the switch take, for a diagnostics view.

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

### `GET /api/effects`

Lists every effect and its settings. The UI should generate effect controls from this list.

```json
{
  "id": "comet",
  "name": "Comet",
  "description": "A bright point with a fading tail that moves along the ports.",
  "category": "animated",
  "params": [
    { "key": "color", "label": "Color", "type": "color", "default": "#64d2ff" },
    { "key": "background", "label": "Background", "type": "color", "default": "#000000" },
    { "key": "speed", "label": "Speed", "type": "float", "default": 6.0, "min": 0.5, "max": 30.0, "step": 0.5,
      "unit": "ports/s" },
    { "key": "tail", "label": "Tail", "type": "int", "default": 3, "min": 0, "max": 7, "step": 1, "unit": "ports" },
    { "key": "bounce", "label": "Bounce", "type": "bool", "default": false }
  ]
}
```

| Param type | Control |
|---|---|
| `color` | Color picker |
| `float`, `int` | Slider from `min` to `max` in steps of `step`, with `unit` shown next to the value |
| `bool` | Toggle |
| `choice` | Segmented control or menu of `choices` |

`category` is `static`, `animated`, `time` (Daylight follows the clock) or `data` (Traffic Meter uses port
traffic).

## Zones

A zone is a group of ports running one effect. By default there is one zone containing every port.

| | |
|---|---|
| `GET /api/zones` | The zones, as in the state |
| `PUT /api/effect` | `{"id": "rainbow", "params": {...}}`: one effect on every port (replaces all zones) |
| `PUT /api/zones` | `{"zones": [{"name": "Desk", "ports": [1,2,3,4], "effect": {"id": "fire", "params": {}}}, ...]}` |
| `PUT /api/zones/{id}/effect` | `{"id": "comet", "params": {...}}`: change one zone's effect |
| `PATCH /api/zones/{id}/params` | `{"params": {"speed": 12}}`: change some settings and keep the rest |

A port can be in only one zone. Ports that aren't in a zone stay off unless a rule or manual color sets them. Zone
ids are made from the name (`"Desk"` becomes `"desk"`) unless an id is given.

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
  "look": { "color": "#bf5af2", "style": "pulse", "rate": 0.5, "brightness": null }
}
```

- `match`: `all` conditions must be true, or `any` of them. A rule without conditions always matches.
- `ports`: a list of ports the rule applies to, or `null` for all ports.
- `look.style`: `solid`, `blink`, `pulse`, `flash` or `effect`. `effect` keeps the zone's color and only applies
  `brightness`, for example to dim ports at night.
- `look.rate`: cycles per second for blink, pulse and flash (0.05-10).
- `look.brightness`: 0-100 to override the global brightness on matching ports, or `null`.

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

While a color picker is being dragged, send WebSocket `set_port` messages instead. They do the same thing with less
overhead.

## Global controls

| | |
|---|---|
| `PUT /api/brightness` | `{"value": 0-100}` |
| `PUT /api/fps` | `{"value": 1-30}`: animation frame rate |
| `POST /api/release` | Hand the LEDs back to the switch's own Etherlighting |
| `POST /api/resume` | Resume control of the LEDs |

## Presets

A preset stores zones, rules, brightness and manual colors.

| | |
|---|---|
| `GET /api/presets` | `{"Movie night": {...}, ...}` |
| `POST /api/presets` | `{"name": "Movie night"}` saves the current setup, replacing a preset with the same name |
| `POST /api/presets/{name}/apply` | Load a preset |
| `DELETE /api/presets/{name}` | Delete a preset |

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

Actions: `{"type": "preset", "preset": "Evening"}`, `{"type": "brightness", "value": 40}`, `{"type": "release"}`
and `{"type": "resume"}`. `days` defaults to every day. `history` lists the last 20 runs, each with a `result`
of `"ok"` or the reason it failed.

## Catalogs

| | |
|---|---|
| `GET /api/effects` | Effects and their settings |
| `GET /api/rules/fields` | Rule fields, operators and styles |
| `GET /api/rules/templates` | Ready-made rules |
| `GET /api/colors` | Named colors (`{"white": "#ffffff", ...}`) for swatches |

These endpoints work without logging in.

## WebSocket: `/api/ws`

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

`zone` is optional; without it the first zone is used. `clear_port` without a port clears all ports. Picker and
slider changes can be sent as they happen without throttling. The server applies each one immediately and writes
to the switch at most 30 times per second. If a client reads slowly, it gets the newest state and frame and skips
older ones; acks and errors are never dropped.

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
