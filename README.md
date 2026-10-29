# Etherlighting Evolved

The Etherlighting UniFi couldn't be bothered to build.

Etherlighting Evolved (EE) is an open-source controller for the RGB port LEDs on UniFi Etherlighting switches. It
connects to the switch over SSH and sets each port's color directly, so you can use any color, run animated
effects, and color ports based on their status (link, speed, PoE, anomalies) or the time of day.

> [!IMPORTANT]
> EE is an unofficial community project. It is not affiliated with, endorsed by or supported by Ubiquiti Inc. It
> uses undocumented interfaces on the switch; see [Safety](#safety) for exactly what it sends. Use it at your own
> risk. UniFi and Etherlighting are trademarks of Ubiquiti Inc.

## Features

- Set any color and brightness on any port. Changes reach the switch immediately.
- Rules that color ports by link state, speed, duplex, uplink, anomaly, spanning tree state, LAG membership,
  traffic, PoE type and power draw, time of day and day of week. The default is white for ports that are up and red
  for ports that are down.
- 15 lighting effects and a traffic meter.
- Zones, so different groups of ports can run different effects.
- Presets for saving and loading a complete setup.
- Schedules that load a preset, change brightness or hand the LEDs back to the switch at set times.
- The current setup is saved automatically and restored when EE restarts.
- A REST and WebSocket API for the web UI or your own scripts.
- A built-in switch simulator for trying EE without hardware.

## Getting started

Requirements: Python 3.11 or newer, and SSH access to the switch. To enable SSH, turn on *Device SSH
Authentication* in the UniFi Network system settings. EE logs in with the username and password set there.

```bash
git clone https://github.com/Views2k/etherlighting-evolved.git
cd etherlighting-evolved
pip install .
etherlighting-evolved
```

Then open <http://localhost:8787> and log in with the switch's IP address, SSH username and password.

To try EE without a switch, log in with `demo` as the host, or start it with `etherlighting-evolved --demo`. The
simulator acts like a USW-Pro-XG-8-PoE, and the API can unplug its links, raise anomalies and change its PoE
devices.

This repository contains the backend: the engine, the switch connection and the API. The web UI is built separately
and served from [`web/`](web/). Until it's there, interactive API docs are at `/docs`, and the API is described in
[docs/API.md](docs/API.md).

## How port colors are decided

Each frame is worked out in this order, and each step can override the one before it:

1. Zones: every port belongs to at most one zone, and each zone runs an effect.
2. Rules: rules are checked from the top, and the first one that matches a port sets its color.
3. Manual colors: a color set by hand on a port overrides everything above.
4. Brightness: the global brightness applies unless a rule or manual color sets its own.

EE then sends only the ports whose color changed, plus a full refresh every 1.5 seconds, because the switch's own
Etherlighting service keeps reapplying its colors.

A new install has one zone running static white and two rules: Anomaly (amber, blinking) and Offline (red). The API
reports which zone, rule or manual color set each port.

## Effects

| Effect | Description |
|---|---|
| Static | One color on every port. |
| Breathing | Fades in and out. Can be staggered across the ports. |
| Color Cycle | All ports move through the color wheel together. |
| Rainbow Wave | A rainbow that moves along the ports. |
| Comet | A bright point with a fading tail that moves along the ports or bounces between the ends. |
| Scanner | A light that sweeps back and forth. |
| Marquee | Every second, third or fourth port lit, stepping along. |
| Starry Night | Random ports flash and fade out, in one color or random colors. |
| Fire | Each port flickers on its own. Fire, ice, toxic and plasma palettes. |
| Police | Alternating flashes in two colors, with three patterns. |
| Strobe | Fast flashes, optionally in a new color each time. |
| Gradient | A blend between two colors, static or moving. |
| Ripple | Rings that move outward from one port. |
| Lightning | A dark background with random double flashes. |
| Daylight | Follows the clock through sunrise, day, sunset and night colors. |
| Traffic Meter | Each port moves from an idle color to a busy color as its throughput rises. |

Each effect lists its settings (type, range, step and unit) at `GET /api/effects`, so the UI generates its
controls from that list. Effects are small classes in
[`effects/library.py`](src/etherlighting_evolved/effects/library.py).

## Rules

A rule has conditions and a look:

```json
{
  "name": "PoE++ devices",
  "conditions": [{ "field": "poe", "op": "in", "value": ["poe++", "poe+++"] }],
  "look": { "color": "#bf5af2", "style": "pulse", "rate": 0.5 }
}
```

| Field | Meaning |
|---|---|
| `link` | Link is up |
| `speed` | Negotiated speed in Mbps |
| `full_duplex` | Full duplex |
| `uplink` | The port is the uplink |
| `anomaly` | The switch reports an anomaly on the port |
| `stp_state` | Spanning tree state: forwarding, blocking, learning, ... |
| `lag` | The port is in a link aggregation group |
| `traffic` | Receive plus transmit, in Mbps |
| `poe` | `off`, `poe` (802.3af), `poe+` (802.3at), `poe++` (802.3bt type 3), `poe+++` (802.3bt type 4) |
| `poe_power` | Power delivered, in watts |
| `time` | Time of day, e.g. between 22:00 and 07:00 (a range can cross midnight) |
| `weekday` | Day of the week |

The look's style is `solid`, `blink`, `pulse`, `flash` or `effect`. `effect` keeps the zone's color and only
changes brightness, for example to dim all ports at night. A rule can apply to all ports or a list of them, and
can require all of its conditions or any of them. `GET /api/rules/templates` has ready-made rules for the fields
above.

## Other controls

- Manual colors stay on a port until they are cleared.
- Presets store zones, rules, brightness and manual colors under a name.
- Schedules run at a set time on selected days.
- Release hands the LEDs back to the switch's own Etherlighting until you resume.
- The WebSocket at `/api/ws` sends every frame, for a live view of the ports.

## Safety

EE only uses one LED command, which sets the color of a single port:

```sh
ubus call etherlight.mcu set '{"port_rgb":"<port> <rrggbb> <brightness>"}'
```

It never sends `led_stop`, `reset`, `mode_color`, `led_mode` or `port_pwm`, which have been reported to corrupt the
LED controller's state or break LED output. A test fails if any of them appear in the code that talks to the
switch.

EE doesn't change the switch's configuration. The switch's Etherlighting service keeps running, and EE re-sends its
colors often enough to stay in front of it. When EE stops, logs out or releases the LEDs, the switch's own colors
return within about two seconds.

The SSH host key is saved on the first login, and EE refuses to connect if it changes. If you tick *Remember me*,
the password is stored in `~/.etherlighting-evolved` with permissions limited to your user account.

The server listens on `127.0.0.1` by default. If you make it reachable from your network with `--host 0.0.0.0`,
also set an API token (`EE_API_TOKEN`), since anyone who can reach the API can control the LEDs.

## Hardware

EE is developed and tested on a USW-Pro-XG-8-PoE, which has Etherlighting on ports 1-8 (the SFP+ ports have no
LEDs). Other Etherlighting switches haven't been tested. `eectl probe` checks whether a switch supports the
command EE uses without changing anything, and `num_ports` in `ee.toml` sets the number of LED ports. See
[docs/HARDWARE.md](docs/HARDWARE.md) for details. Reports from other models are welcome.

## Command line

```bash
etherlighting-evolved                    # run the server (same as `eectl serve`)
etherlighting-evolved --demo             # start logged in to the simulator
eectl probe 192.168.1.2 -u admin         # check whether a switch works with EE
eectl set 192.168.1.2 -u admin 3 ff00ff  # set one port's color once
eectl benchmark 192.168.1.2 -u admin     # measure how fast the switch accepts commands
eectl effects                            # list effects and their settings
eectl init-config                        # write an example ee.toml
eectl forget-host 192.168.1.2            # forget a saved host key after replacing or resetting a switch
```

No configuration file is needed. `ee.toml` (created by `eectl init-config`) sets the frame rate, keepalive, listen
address and API token. Setting `EE_HOST`, `EE_USERNAME`, `EE_PASSWORD` or `EE_KEY_FILE` logs in at startup without
the login screen, which is useful when running EE as a service.

## Development

```bash
python -m venv .venv
.venv/bin/pip install -e ".[dev]"     # Windows: .venv\Scripts\pip
.venv/bin/pytest
```

The tests use the simulator, so no switch is needed.

```
src/etherlighting_evolved/
  transport.py   SSH connection, host key pinning, simulator
  switch.py      LED command, change detection, keepalive, port and PoE status
  links.py       `swctrl port show` parser, traffic meter
  poe.py         `swctrl poe show` parser, PoE types
  effects/       effects and their settings
  rules.py       conditions, looks, templates
  engine.py      zones, rules, manual colors and brightness, sent to the switch at up to 30 fps
  schedules.py   scheduled actions
  session.py     login, reconnecting, saving the current setup
  mailbox.py     per-client WebSocket message queue
  api.py         REST and WebSocket API
  cli.py         etherlighting-evolved and eectl commands
```

## Prior work

These projects documented parts of the Etherlighting interface before EE:

- [Ozark-Connect/unifi-lightshow](https://github.com/Ozark-Connect/unifi-lightshow): controlling Etherlighting
  through `ubus`, and which commands to avoid.
- [adamjezek98/ubnt-etherlighting](https://github.com/adamjezek98/ubnt-etherlighting): early work on the
  Etherlighting controller.
- [dr-stefan-krank/unifi-etherlight-profiles](https://github.com/dr-stefan-krank/unifi-etherlight-profiles):
  keeping port colors visible when the link is down.

## License

[MIT](LICENSE). Not affiliated with Ubiquiti Inc.
