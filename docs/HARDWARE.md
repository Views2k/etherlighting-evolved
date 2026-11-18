# Hardware qualification

EE identifies the switch during login and uses its physical port layout. Output requires a matching model
and system ID, the exact running lighting-daemon hash, the corresponding MCU image hash, and the required
method schema on the current SSH lighting connection. A different firmware build requires another review.
`eectl probe` performs read-only discovery and returns success only when this output gate passes.

The [current-lineup qualification record](HARDWARE-QUALIFICATION.md) tracks the November 2026 release
inventory, firmware evidence, separate playback qualification and remaining public-launch gates.

## Reviewed firmware coverage

The following profiles have been traced through the official firmware's board dispatch, per-port RGB16
parser, MCU output mapping, native fade code, and scoped optical ownership operations. Offline fixtures
exercise their port addresses, acknowledgment handling, acquisition, and cleanup. These checks establish
the reviewed command path; physical appearance and sustained load on additional models still need testing.

| Models | Copper ports | Optical indicators | Firmware family / release |
| --- | --- | --- | --- |
| Pro XG 8 PoE | 1–8 | 9–10 | USWED72 / 7.5.15+17146 |
| Pro XG 10 PoE | 1–10 | 11–12 | USWED72 / 7.5.15+17146 |
| Pro HD 24, Pro HD 24 PoE | 1–24 | 25–28 | USWED72 / 7.5.15+17146 |
| Pro XG 24, Pro XG 24 PoE | 1–24 | 25–26 | USWED42 / 7.5.15+17146, 7.6.11+17272 |
| Pro XG 48, Pro XG 48 PoE | 1–48 | 49–52 | USWED42 / 7.5.15+17146, 7.6.11+17272 |
| Pro XG Aggregation | — | 1–32 | USWED42 / 7.5.15+17146, 7.6.11+17272 |
| ECS-24-PoE | 1–24 | 25–26 | USWED42 / 7.5.15+17146, 7.6.11+17272 |
| ECS-48-PoE | 1–48 | 49–52 | USWED42 / 7.5.15+17146, 7.6.11+17272 |
| ECS-24S, ECS-24S-PoE | 1–24 | 25–28 | USWF004 / 7.4.119+17363 |
| ECS-48S, ECS-48S-PoE | 1–48 | 49–52 | USWF004 / 7.4.119+17363 |
| EAV-XG-24-PoE | 1–24 | 25–28 | USWF001 / 7.5.15+17146, 7.6.11+17272 |
| EAV-Fiber | — | 1–22 | USWF001 / 7.5.15+17146, 7.6.11+17272 |

The Pro XG 8 PoE has been tested on hardware for its copper ports and both SFP indicators. The other
profiles are firmware-qualified candidates that have not been tested on a physical switch. Stacking ports and extra internal MCU records are excluded from output.
Optical-only models use indicator controls without creating a fictitious copper bank.

The six Pro Max models, ECS-Aggregation, and ECS-Core remain output-blocked. The reviewed Pro Max helper
commits all staged MCU records and hides commit errors. The SONiC models require a separate ownership and
cleanup adapter. Detection of a device or a similarly named command does not grant output permission.

## Port map layout

The map uses the Ethernet and optical ports reported by the connected switch. Larger layouts fill rows
of up to ten ports on desktop, laptop, and rack screens, with additional rows reached by vertical scrolling.
Optical-only switches use the same layout. Narrow screens retain the compact two- or four-column map.
The eight-Ethernet/two-optical layout stays on one row when the canvas has room. These display rules apply
independently of the model and firmware qualification above.

## Port controls and saved profiles

Copper ports use their group effect, matching rules, or an individual color. Qualified optical indicators
can join these effect groups or form their own. Each member uses its group position and its own port telemetry.
Outside a group, optical indicators have
independent Steady, Gentle pulse, Color cycle, Two-color fade, Off, and Link status controls. Together uses
the first indicator's settings; Follow Ethernet uses a selected copper port's composed color.

Scenes record the model and exact copper/optical membership. A scene from a different topology is rejected
before changing output. Existing Pro XG 8 device-specific saved data retains the same identity. Legacy
port-specific profiles keep their original layout rather than reinterpreting their port numbers.

## Output and cleanup

The adapter leaves the vendor lighting service running. It uses the existing ubus socket over SSH and
keeps one acknowledged request in flight. RGB16 fields are limited to three integers from 0 through 65280,
with white fixed at zero. Positive port IDs must belong to the qualified profile.

Optical acquisition and release use explicit one-record masks with `soc_port_map` and `blink_port_map`.
Masks use the reviewed high-24/low-32 word encoding. Release applies the status-controlled baseline after
draining writes; it does not claim to recover a previous controller color policy. Failed cleanup suspends
output. Reconnect rechecks firmware, identity, topology, and ownership before resuming a retained scene.

EE does not flash firmware, load modules, or change network configuration. Fan control pauses the switch's
fan daemon (`ubnt-fanctrl`) while it runs; see the next section. EE's typed writer excludes reset, LED-stop,
global port-zero writes, unqualified masks, and arbitrary shell input.
The transport starts unbound and rejects every lighting setter until qualification. Reprobing or closing
revokes that binding, including already queued writes.

## Programs EE runs on the switch

Two features on the Pro XG 8 PoE copy a program to the switch and leave it running after EE closes. Each
uses its own directory under `/tmp`, created with mode 700 for the SSH user, in the switch's RAM. A switch
restart removes both.

The lighting player runs from `/tmp/ee-lighting-playback-v1` when the switch login is saved and the switch
passes the playback checks (the qualified Pro XG 8 PoE profile with native breathing and precise SFP
output). It keeps the saved lighting and traffic-driven effects on the switch while EE is closed, and EE
updates its scene as the lighting changes. **Handoff to UniFi** stops it from the computer that saved it.
Accepting a changed SSH key for the switch deletes the player record of the previous key, because that player
ran in the previous switch's memory.
Another EE installation cannot connect to a switch while a player it did not start is running; return
playback to UniFi from the computer that saved it, or restart the switch.

Fan control runs from `/tmp/ee-fan-control-v1` while a fan mode other than Switch control is chosen. It
requires system ID `ed76` and an `adt7475` fan controller. While it runs, it pauses `ubnt-fanctrl` with
SIGSTOP and sets both fan outputs. A guard process resumes `ubnt-fanctrl` with SIGCONT when fan control
stops, fails or disappears. In every mode the fans speed up as the CPU passes 85 °C (full speed at 95 °C),
the SFP sensor passes 65 °C (75 °C) or a port chip passes 100 °C (110 °C). A failed sensor reading runs
the fans at full speed for that step, and after six failed readings in a row fan control stops and the
switch's own fan controller takes over. Choosing **Switch control** on the Fans page stops fan control, and
**Handoff to UniFi** stops it as well and confirms that `ubnt-fanctrl` runs again.

EE saves the fan mode for each switch on the computer that runs it, and a switch that EE has not controlled
before starts in Switch control. While connected, EE checks fan control every 5 seconds and starts it again
after a switch restart or a handoff. Fan control that EE on another computer started with release 0.6.0 or
later keeps running until a mode is chosen here. The mode saved by release 0.5.1 moves to the first Pro XG 8
PoE that EE connects to after the update. EE's fan program changed after 0.5.1, so the first check on that
switch restarts the earlier program once with the same mode; `ubnt-fanctrl` runs the fans while it restarts.
On every other switch, the check treats fan control that release 0.5.1 started, on this or another computer,
as EE's own: it replaces it with the mode saved for that switch, or stops it when that mode is Switch control,
as it is for a switch that EE has not controlled before.

## Timing and physical acceptance

Synchronized copper Breathing uses the MCU's shared phase. Optical fades use finite native segments and
separately acknowledged per-port targets. Grouped target submission is not an atomic frame operation.
At the initial 30-command/s budget, enrolling 32 optical indicators takes at least 1.033 seconds from the
first target to the last. Large-group synchronization and visible smoothness therefore remain acceptance
gates; firmware parsing and simulated output do not establish those properties.

EE starts at at most 30 commands per second. Direct output can increase only with fresh CPU-idle and
acknowledgment headroom, up to the configured ceiling. Stale readings, slow responses, or CPU pressure
reduce the rate. Each setup, target, keepalive, and cleanup write shares that budget. Larger port counts
do not multiply the ceiling or remove the one-request acknowledgment boundary.

Link, traffic, PoE, and health readings use the existing read-only parsers. Missing readings remain missing
rather than becoming invented values. Controller metadata supplies device names and topology when connected.
