# Engineering status

Update this record when a tracked issue is fixed, disproved, or deferred. Record the affected revision and verification result in its pull request. Passing simulator tests do not close physical-device acceptance.

## Current delivery

Version 0.4.19 includes the Pro XG temperature and UniFi Network sign-in changes described in [the changelog](../CHANGELOG.md). Source, tests, assets, documentation, and build tools are maintained in this repository. [WORKFLOW.md](WORKFLOW.md) defines verification and delivery.

## Remaining work

| Area | Required result | Status |
| --- | --- | --- |
| Interrupted output ownership | In-app recovery after crash or restart, with pinned device identity, exclusive ownership, and confirmed scoped cleanup | Open |
| Interrupted handoff | Retry completes retained cleanup without deleting evidence or bypassing ownership checks | Open |
| Abrupt SSH loss | Automatic reconnect uses the verified recovery path; transient qualification failures remain retryable | Open |
| Desktop shutdown | A dead gateway does not indefinitely block Tray Quit or installer upgrade | Open |
| Parent-loss cleanup | Retries are bounded and expose actionable recovery state | Open |
| Autostart upgrade | An unselected installer task preserves an existing tray-enabled preference | Open |
| Profile layout validation | A cross-switch profile identifies incompatible topology before Apply | Open |
| Touch and accessibility | Physical rack, mobile, installed WebView, keyboard, and virtual-cursor picker acceptance | Awaiting device verification |
| Subset effect previews | Spatial-group geometry remains accurate when previewing a subset | Open |
| Pro Max and SONiC | Verified output adapter and model-specific acceptance before enabling lighting | Blocked pending qualification |
| Brightness comparison | Compare the same fixed color against the earlier accepted build | Deferred by owner |

Preserve current admission, pacing, identity, and cleanup gates while resolving these issues. See [HARDWARE.md](HARDWARE.md) for model and firmware qualification. Network failover and Plex work are separate from EE.

## Evidence locations

CI results are attached to the tested revision. Release candidates also include installer provenance. Local verification is under `outputs/verification/`, delivered packages under `outputs/releases/`, and historical audit evidence under `outputs/audits/`. These generated records are excluded from Git. Credentials and live application state are not build inputs.
