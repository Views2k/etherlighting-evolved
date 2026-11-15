# Current-lineup qualification

This engineering record tracks the current Etherlighting lineup for public-launch preparation. Catalog membership, firmware qualification, simulator checks, and physical-device acceptance are separate results. A public launch covering the entire lineup remains gated on the unresolved items below.

## Scope and release evidence

The November 14, 2026 inventory covers all 25 Etherlighting models in the current [U.S.](https://store.ui.com/us/en/category/all-switching) and [European](https://eu.store.ui.com/eu/en/category/all-switching) switching collections. Regional availability does not determine qualification. Vintage Enterprise models are outside this scope. The former EAV-24-PoE and EAV-Aggregation product names refer to the current EAV-XG-24-PoE and EAV-Fiber products; firmware identity is checked independently of store naming.

Ubiquiti's model-specific release API was checked for all 25 platforms on November 14 at 23:51:59–23:52:00 UTC. Nine platforms point to two updated 7.6.11+17272 images. The other sixteen point to images already examined in the November 1 review. Release-channel metadata describes the available artifact, not an update observed on every controller.

All six image hashes were compared with the official release metadata. The two newly downloaded images matched their official lengths and SHA-256 hashes. Microsoft Defender found no threats in those archives or their extracted files. Firmware was inspected as data, without running its programs or connecting to a switch.

## Model matrix

`Reviewed` means the current firmware's EE command path has static evidence and the adapter has deterministic fixtures. It is not physical acceptance. `Adapter open` means identification and topology are implemented, but output remains blocked pending the separate protocol and ownership implementation.

| Model | Family | Release | Ethernet / optical / stacking | Current firmware |
| --- | --- | --- | --- | --- |
| USW-Pro-Max-16 | USPM16 | 7.5.15+17146 | 16 / 2 / 0 | Adapter open |
| USW-Pro-Max-16-PoE | USPM16 | 7.5.15+17146 | 16 / 2 / 0 | Adapter open |
| USW-Pro-Max-24 | USPM16 | 7.5.15+17146 | 24 / 2 / 0 | Adapter open |
| USW-Pro-Max-24-PoE | USPM16 | 7.5.15+17146 | 24 / 2 / 0 | Adapter open |
| USW-Pro-Max-48 | USPM16 | 7.5.15+17146 | 48 / 4 / 0 | Adapter open |
| USW-Pro-Max-48-PoE | USPM16 | 7.5.15+17146 | 48 / 4 / 0 | Adapter open |
| USW-Pro-HD-24 | USWED72 | 7.5.15+17146 | 24 / 4 / 0 | Reviewed |
| USW-Pro-HD-24-PoE | USWED72 | 7.5.15+17146 | 24 / 4 / 0 | Reviewed |
| USW-Pro-XG-8-PoE | USWED72 | 7.5.15+17146 | 8 / 2 / 0 | Reviewed |
| USW-Pro-XG-10-PoE | USWED72 | 7.5.15+17146 | 10 / 2 / 0 | Reviewed |
| USW-Pro-XG-24 | USWED42 | 7.6.11+17272 | 24 / 2 / 0 | Reviewed |
| USW-Pro-XG-24-PoE | USWED42 | 7.6.11+17272 | 24 / 2 / 0 | Reviewed |
| USW-Pro-XG-48 | USWED42 | 7.6.11+17272 | 48 / 4 / 0 | Reviewed |
| USW-Pro-XG-48-PoE | USWED42 | 7.6.11+17272 | 48 / 4 / 0 | Reviewed |
| USW-Pro-XG-Aggregation | USWED42 | 7.6.11+17272 | 0 / 32 / 0 | Reviewed |
| ECS-24-PoE | USWED42 | 7.6.11+17272 | 24 / 2 / 0 | Reviewed |
| ECS-48-PoE | USWED42 | 7.6.11+17272 | 48 / 4 / 0 | Reviewed |
| ECS-24S | USWF004 | 7.4.119+17363 | 24 / 4 / 2 | Reviewed |
| ECS-24S-PoE | USWF004 | 7.4.119+17363 | 24 / 4 / 2 | Reviewed |
| ECS-48S | USWF004 | 7.4.119+17363 | 48 / 4 / 2 | Reviewed |
| ECS-48S-PoE | USWF004 | 7.4.119+17363 | 48 / 4 / 2 | Reviewed |
| EAV-XG-24-PoE | USWF001 | 7.6.11+17272 | 24 / 4 / 0 | Reviewed |
| EAV-Fiber | USWF001 | 7.6.11+17272 | 0 / 22 / 0 | Reviewed |
| ECS-Aggregation | USWF066 | 4.0.1+995 | 0 / 54 / 0 | Adapter open |
| ECS-Core | USWF066 | 4.0.1+995 | 0 / 32 / 0 | Adapter open |

The Pro XG 8 PoE has previous owner-observed lighting acceptance. The other 24 models still need independent physical acceptance. Stacking connectors and internal MCU sentinel records are excluded from lighting writes.

## Updated firmware

The release images are available from Ubiquiti's metadata records:

- [USWED42 release](https://fw-update.ui.com/api/firmware/b5cfb8d3-ae65-4b6f-9e5a-9fe08349fe7c): 26,981,723 bytes, SHA-256 `6a42bab5c5a54fcecc71809cae0348f15c22beba4c98a15877267c1d9aea4e34`.
- [USWF001 release](https://fw-update.ui.com/api/firmware/39509992-77d6-4fd2-887e-15f60b315c13): 29,416,687 bytes, SHA-256 `4dce222ddc769fa4a67012b7d23aec562d4e1a4df1d540eeaf20a108349e1f9c`.

The running daemon changes in both families. Their MCU images remain byte-identical to the previously reviewed releases. The model identities, physical port records and lighting schemas also match. The new library changes board-record stride, so static-analysis offsets cannot be reused blindly. EE reads the board identity through the existing text interface.

The scoped comparison reached a stable set of 59 functions in each family: 6,972 instructions for USWED42 and 7,000 for USWF001. The final data-reference passes covered 402 and 400 references, respectively. The selected get/set/dump handlers, model-selected probe and cleanup callbacks, reachable internal callees, direct import targets and branch relationships match after relocation. Referenced data was checked separately, including manually resolved stack-loaded pointer bases and fixed-width constants. This establishes equivalence of the examined lighting path, not of the whole executable or every firmware subsystem.

EE admits the two new daemon hashes only with their corresponding MCU hash. The older reviewed pairs remain accepted. Original profile identity fields remain unchanged so saved scenes are retained across the reviewed firmware change. Regression coverage exercises every admitted model/firmware pair, qualification revocation and recovery, cross-family and wrong-path rejection, synthetic crossed-pair rejection, and saved-scene loading through upgrade and return to the older pair.

| Family | New running daemon SHA-256 | Corresponding MCU SHA-256 |
| --- | --- | --- |
| USWED42 | `9de90875f77a65c5d3006308ab4e707a79d420cca8f4c4994a3de2896df59109` | `8c79c0050ba28fa933f3b1a71d530665ef560775793f133fae17a85482508322` |
| USWF001 | `4599de216f558d4425782518a7cfd8fe234bc2d4eba3967c9802951debdb4220` | `8c79c0050ba28fa933f3b1a71d530665ef560775793f133fae17a85482508322` |

## Pro Max adapter work

All six current models resolve lighting device 33 to I2C bus 104 and address 0x66. Hash-pinned kernel tables and instruction paths establish this discovery; it does not grant output access.

The existing proc helper stages values and commits shared MCU records, and it can hide transfer failures. Its kernel helper also returns zero when the adapter is absent. Built-in BusyBox checks block-write errors, but its optional readback failure or mismatch can return success. The legacy ioctl operation it uses still needs translation verified against the kernel path.

The vendor host mapping is now traced across all 192 connector rows in the six models: ordered board configuration becomes the logical port, and the host sends the zero-based connector index in opcode 0x20. All six system-ID-to-MCU-board selections are resolved. Pro Max 16 uses host records 0–17, Pro Max 24 uses 0–25, and Pro Max 48 uses 0–51.

This does not finish physical output qualification. Pro Max 16's default MCU table has status-map holes and extra aliased records; Pro Max 24 has two extra output records. Status bits cannot be treated as connector indices, and extra records must not enter a commit mask. Physical/PCB routing or an identified runtime map writer must close that discrepancy. The next adapter also needs bounded packet encoding and independent completion checks: acknowledgment occurs before execution, so successful submission is not confirmation that a queued command completed.

Ownership also requires proof. Mode 3 suppresses ordinary link/PoE writers, but locate and controller device-type changes have separate paths. A controller change can retain the same mode. Restoring a cached mode would risk overwriting newer controller intent. No output adapter should use broad configuration locks, stop vendor services or infer exclusive ownership from this mode alone.

## Campus aggregation and core adapter work

The SONiC daemon and MCU use a separate protocol. The current MCU's Aggregation table contains 54 physical output records plus two sentinel records; Core has 32 physical records plus two sentinels. Those internal records do not expand the public port map.

Daemon `get` returns cached mode. `dump` logs configuration and calibration rather than reporting active output. The `off` value is parsed but follows the invalid-mode branch. Returning to speed mode does not propagate each MCU write result. These responses cannot substitute for confirmed cleanup.

MCU selector register 0x10 can snapshot the selected port's active channels; registers 0x11–0x18 return four 16-bit values. This is useful evidence beyond the daemon cache, but the ubus interface does not expose it. A production adapter needs an atomic selector/read route sharing vendor bus serialization. A separate raw helper would bypass the daemon's process-local mutex. Physical light output remains a separate measurement.

Mode 3 suppresses normal link events, but direct color writes bypass it. Controller-policy reconciliation and completion-aware cleanup remain required before output admission.

## Launch acceptance

Every enabled model/firmware pair must pass identity, schema, exact hash-pair, connector mapping, pacing, stale-connection revocation, interrupted apply, reconnect and cleanup checks. Malformed or cross-family evidence must leave the writer unbound. A firmware update must retain saved settings while requiring fresh qualification on its new connection.

The ordinary Lighting, Effects, Automation and Profiles workspace must remain usable. No playback gate or separate effect editor is part of this work. Browser and native topology fixtures cover the model layouts; physical labeling and visible behavior need device acceptance.

Switch-resident playback is currently specific to Pro XG 8 PoE. It uses a RAM-resident player. Other models need their own telemetry, port-map and resource-budget validation before that path is enabled. Isolated reboot recovery and direct UniFi-edit precedence remain open, including on Pro XG 8 PoE. Firmware analysis alone cannot establish those behaviors.

Before enabling a new family, a controlled device acceptance run must verify every physical port at bounded brightness, an unchanged nonselected port, link/PoE stability, sustained CPU/memory and command latency, loss of the desktop/SSH connection, explicit handoff and recovery. Reboot testing requires a supported persistent startup mechanism and separate authorization for the network interruption. Public release notes must describe only the model/firmware behavior actually accepted.

## Evidence handling

Firmware images, extracted binaries, raw disassembly, private diagnostics and tool packages remain outside the source repository. The private evidence bundle records official metadata, file hashes, Defender results, reproducible table/code checks and their outputs. Repository tests cover software behavior without contacting a switch. Neither a clean scan nor static equivalence is a guarantee of zero physical risk.
