# Hardware notes

## Tested

USW-Pro-XG-8-PoE: eight 10 GbE RJ45 ports with Etherlighting (ports 1-8) and two SFP+ ports without LEDs.

On this switch:

- `ubus list` includes `etherlight` and `etherlight.mcu`, and `ubus -v list etherlight.mcu` shows a `set` method
  that accepts `port_rgb`.
- `ubus call etherlight.mcu set '{"port_rgb":"<port> <rrggbb> <level>"}'` sets the color of one port
  immediately. `<level>` is 0-100. Port numbers match the front panel.
- The switch's `etherlightd` service reapplies its own colors every couple of seconds. EE leaves it running and
  re-sends every port at least every 1.5 seconds (`keepalive`), and changed ports in between. When EE stops, the
  switch's colors return.
- `swctrl port show` lists each port's link state (`U/U` linked, `U/D` enabled without a link, `D/D` disabled),
  speed and duplex (`10000F`), packet and byte counters, STP state, an anomaly bit field (`0x0` when there are no
  anomalies) and LAG membership. A `U` before the port number marks the uplink.

## Not yet verified

PoE status: EE reads `swctrl poe show` and expects the table format used by other UniFi switches (port, op mode,
PoE mode, power limit, class, on/off, power good, watts, volts, milliamps). This hasn't been checked on the
USW-Pro-XG-8-PoE. If the output is different, EE reads no PoE data and PoE rules don't match; everything else
works normally. `eectl probe` reports when no PoE data was read. In that case, please open an issue with the output
of `swctrl poe show` and the switch model.

The PoE type is based on the class requested by the powered device:

| Class | Standard | EE value |
|---|---|---|
| 0-3 | 802.3af | `poe` |
| 4 | 802.3at | `poe+` |
| 5-6 | 802.3bt type 3 | `poe++` |
| 7-8 | 802.3bt type 4 | `poe+++` |

Other Etherlighting switches may use the same `etherlight.mcu` interface, but EE hasn't been tested on them. To
check a switch:

```bash
eectl probe <switch-ip> -u <ssh-user> --ports <number of LED ports>
```

This logs in, checks for `port_rgb` and prints the status of each port without changing anything. `eectl set` can
then light a single port to confirm the numbering. If it works, set `num_ports` in `ee.toml`, and please report the
model so it can be added here.

## Commands EE never sends

`etherlight.mcu` accepts other `set` options. These have been reported to corrupt the LED controller's state or
break LED output (see [unifi-lightshow](https://github.com/Ozark-Connect/unifi-lightshow)), so EE never uses them,
and they should be avoided when testing by hand:

`led_stop`, `reset`, `mode_color`, `led_mode`, `port_pwm`

## Speed

All commands go through one persistent SSH shell, so there is no new login per command, and changed ports are sent
in a single batch. `eectl benchmark` measures the time for one command, for a batch, and the highest full-frame
rate. Animated effects run at 10 frames per second by default; adjust `fps` based on the benchmark. Only ports that
change are sent, so a static color uses very little bandwidth.
