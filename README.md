# Etherlighting Evolved

Etherlighting Evolved (EE) controls UniFi port lighting over SSH. Select ports, preview colors and effects, then apply them to the switch. Connect UniFi Network to add device names and port telemetry.

EE is an unofficial community project, not affiliated with, endorsed by, or sponsored by Ubiquiti. UniFi and Etherlighting are trademarks of Ubiquiti Inc.

## Lighting controls

- Set colors and brightness for individual ports or groups.
- Preview animated effects using the selected colors and settings.
- Color ports by link status, traffic, PoE usage, anomalies, or a schedule.
- Configure optical indicators individually, together, or to follow Ethernet lighting on qualified hardware.
- Save lighting profiles and apply them when needed.
- View uplink upload and download rates, updated every five seconds.
- Use Settings to connect UniFi Network, configure private access through Tailscale, or hand lighting control back to UniFi.

Review the [hardware qualification table](docs/HARDWARE.md) for the model and firmware you plan to use. EE checks device identity, firmware, and lighting capabilities before permitting output.

## Run from source

The workspace requires Node.js 20 or newer and Python 3.11 or newer with the dependencies declared in [pyproject.toml](pyproject.toml). Development and build instructions are in [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md).

With an existing Python environment in `.venv`, start the simulator from the repository root:

[WINDOWS POWERSHELL]

```powershell
$env:EE_PREVIEW_LIVE = '0'
npm start
```

Open [the local workspace](http://127.0.0.1:4180). The simulator has its own data directory and is suitable for trying the interface before connecting a switch.

To open a live workspace instead:

[WINDOWS POWERSHELL]

```powershell
$env:EE_PREVIEW_LIVE = '1'
npm start
```

Enter the switch's SSH credentials in the login form. **Save login on this PC** stores the login using Windows account protection. Use the UniFi Network section in Settings to connect the controller and retrieve device names and telemetry.

Use **Handoff to UniFi** to release EE's lighting control. Wait for its result before starting another lighting controller. Profiles and saved logins remain available for the next connection.

## Windows installer

The Windows app runs the same workspace in its own window. Build the installer from this repository using [the Windows build instructions](docs/DEVELOPMENT.md#windows-installer). The build uses existing local tools and writes its output under `outputs/releases/`.

## Development

Use the [repository workflow](docs/WORKFLOW.md) for changes, verification, and revision-specific installers. See the [changelog](CHANGELOG.md) for recent updates.

The application, engine, tests, assets, and packaging scripts share this repository:

| Directory | Contents |
| --- | --- |
| `preview/` | Current workspace, private gateway, and browser rendering |
| `src/etherlighting_evolved/` | Python engine, device adapters, API, and desktop host |
| `src/*.mjs` | Shared browser modules and effect catalog |
| `assets/` | Browser artwork and styles |
| `tests/` | Python and JavaScript tests with deterministic fixtures |
| `packaging/` | Service entry points, executable specifications, and installer |
| `tools/` | Build, verification, and fixture-generation tools |
| `docs/` | API contract, hardware qualification, and development instructions |

See [DEVELOPMENT.md](docs/DEVELOPMENT.md) for commands and data locations, [INTEGRATION.md](docs/INTEGRATION.md) for the workspace-to-engine flow, and [API.md](docs/API.md) for the API contract.

## License

[MIT](LICENSE), copyright Views2k. Packaged third-party components retain their notices and licenses.
