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

The Pro XG 8 PoE has prior owner-confirmed copper and two-indicator visual testing. The other profiles
are firmware-qualified candidates. Stacking ports and extra internal MCU records are excluded from output.
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

EE does not flash firmware, load modules, stop vendor services, or change network configuration. Its typed
writer excludes reset, LED-stop, global port-zero writes, unqualified masks, and arbitrary shell input.
The transport starts unbound and rejects every lighting setter until qualification. Reprobing or closing
revokes that binding, including already queued writes.

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
