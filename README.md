# Etherlighting Evolved

Etherlighting Evolved (EE) controls UniFi port lighting over SSH. Select ports, preview colors and effects, then apply them to the switch. Connect UniFi Network to add device names and port telemetry.

EE is an unofficial community project, not affiliated with, endorsed by, or sponsored by Ubiquiti. UniFi and Etherlighting are trademarks of Ubiquiti Inc.

## Desktop app

EE 0.5.0 brings Lighting, Effects, Automation, Profiles, Settings and Help into a native desktop window. On Windows, the installer includes the local workspace service. Open EE and choose the demo to try the controls, or connect to your switch to manage its lighting.

The macOS and Linux desktop clients connect to a running EE workspace. Open the app on macOS or run `./EtherlightingEvolved` from the extracted folder on Linux. Choose **Change workspace address** to enter the private HTTPS address of your EE workspace. See the [desktop guide](desktop/README.md) for platform packages and startup options.

## Lighting controls

- Set colors and brightness for individual ports or groups.
- Preview animated effects using the selected colors and settings.
- Color ports by link status, traffic, PoE usage, anomalies, or a schedule.
- Select SFP lights for regular effects, including Activity Glow, Live Traffic, and Traffic Trails. In Effects, select **Include SFP lights** to add them to your Ethernet selection.
- Configure SFP indicator lighting individually, together, or to follow an Ethernet port.
- Save the current lighting or a preview as a profile. Review a profile's settings, apply it, or use **Undo last apply** to return to the previous lighting.
- Test rules against a port and time before applying them. Set weekly schedules to choose a profile, adjust brightness, or hand lighting control to UniFi.
- Rename ports in Port details or use their names from UniFi Network.
- View uplink upload and download rates, updated every five seconds.
- Use Settings to connect UniFi Network, configure private access through Tailscale, or hand lighting control back to UniFi.

Review the [hardware qualification table](docs/HARDWARE.md) for the model and firmware you plan to use. EE checks device identity, firmware, and lighting capabilities before permitting output.

## Run from source

The source gateway and lighting engine use Node.js 20 or newer and Python 3.11 or newer. Building the desktop app also requires the .NET 8 SDK and the locked desktop dependencies. See the [development guide](docs/DEVELOPMENT.md).

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

Build the Windows installer from the repository root with the existing local tools. Setup packages the native app together with the gateway, engine and notices under `outputs/releases/`. See the [Windows build instructions](docs/DEVELOPMENT.md#windows-installer).

## Development

Use the [repository workflow](docs/WORKFLOW.md) for changes, verification, and revision-specific installers. See the [changelog](CHANGELOG.md) for recent updates.

The application, engine, tests, assets, and packaging scripts share this repository:

| Directory | Contents |
| --- | --- |
| `desktop/` | Native desktop app, simulator checks, and platform packaging |
| `preview/` | Browser workspace, private gateway, and browser rendering |
| `src/etherlighting_evolved/` | Python lighting engine, device adapters, and API |
| `src/*.mjs` | Shared browser modules and effect catalog |
| `assets/` | Browser artwork and styles |
| `tests/` | Python and JavaScript tests with deterministic fixtures |
| `packaging/` | Service entry points, executable specifications, and installer |
| `tools/` | Build, verification, and fixture-generation tools |
| `docs/` | API contract, hardware qualification, and development instructions |

See [DEVELOPMENT.md](docs/DEVELOPMENT.md) for commands and data locations, [INTEGRATION.md](docs/INTEGRATION.md) for the workspace-to-engine flow, and [API.md](docs/API.md) for the API contract.

## License

[MIT](LICENSE), copyright Views2k. Packaged third-party components retain their notices and licenses.
