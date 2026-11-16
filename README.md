# Etherlighting Evolved

Etherlighting Evolved (EE) controls the port lights (Etherlighting) of UniFi switches over SSH and, on a USW-Pro-XG-8-PoE, the switch fans. Select ports, preview colors and effects, then apply them to the switch. Connect UniFi Network to add device names and port telemetry.

## Unofficial software

EE is an unofficial project. It is not affiliated with, endorsed by or sponsored by Ubiquiti Inc. UniFi and Etherlighting are trademarks of Ubiquiti Inc.

EE logs in to the switch over SSH and uses interfaces on the switch that Ubiquiti does not document or support. Use it at your own risk. It is provided under the MIT license, without warranty.

Only the USW-Pro-XG-8-PoE has been tested on a physical switch. The other models in the [hardware list](docs/HARDWARE.md) were checked against their firmware, not on hardware. At every login EE checks the model, the firmware build and the lighting interface, and it refuses lighting output on a model or firmware build that has not been reviewed. A firmware update can therefore block output until the new build is reviewed.

## Features

The desktop app has Lighting, Effects, Automation, Profiles, Fans, Settings and Help pages.

- Set colors and brightness for single ports or groups, and preview animated effects before applying them.
- Color ports by link status, traffic, PoE usage, anomalies or a schedule, and test rules against a port and time.
- Run effects on the SFP indicators, or set them one by one, together, or to follow an Ethernet port.
- Save lighting as a profile, apply it later, or undo the last apply. Weekly schedules can apply a profile, change brightness or hand lighting control to UniFi.
- Rename ports or use their names from UniFi Network, and see uplink download and upload speeds.
- On a USW-Pro-XG-8-PoE, set the fans to Switch control, a Low, Medium, High or Max curve, Manual (one fixed speed) or Thermal target, and see the CPU, SFP and port chip temperatures. Fan control is in the desktop app only.
- Open the workspace from a phone or another computer through Tailscale.

## Requirements

- A computer with 64-bit Windows 10 or later. EE controls switches from Windows only.
- A switch from the [hardware list](docs/HARDWARE.md), its IP address, and the SSH username and password set for your devices in UniFi Network.
- UniFi Network, if you want device names and port telemetry.
- Tailscale on the Windows computer and on each other device, if you want to open EE from elsewhere.

The macOS client (macOS 12 or later, Apple silicon or Intel) and the Linux client (x64 or arm64) do not connect to a switch themselves. They open the workspace of a Windows computer that runs EE, through its private Tailscale address.

## Install

Download the files from the [releases page](https://github.com/Views2k/etherlighting-evolved/releases). Each release has these files:

| File | Contents |
| --- | --- |
| `EtherlightingEvolved-Setup-<version>.exe` | Windows installer: the desktop app, the workspace service and the lighting engine |
| `EtherlightingEvolved-<version>-osx-arm64.zip`, `EtherlightingEvolved-<version>-osx-x64.zip` | macOS client for Apple silicon and for Intel |
| `EtherlightingEvolved-<version>-linux-x64.tar.gz`, `EtherlightingEvolved-<version>-linux-arm64.tar.gz` | Linux client |
| `SHA256SUMS` | SHA-256 checksums of the installer and the client archives |
| `package-verification.json` | Record of the checks the release build ran on the Windows package |

GitHub Actions builds each release from a version tag. The Windows installer has no Authenticode signature yet, so Windows SmartScreen can warn before Setup runs. The macOS app has an ad hoc signature but is not signed with a Developer ID or notarized, so macOS blocks it the first time it opens; choose **Open Anyway** in System Settings → Privacy & Security (System Preferences → Security & Privacy on macOS 12). Verify a download before you run it.

### Verify a download

Compare the file's SHA-256 checksum with its line in `SHA256SUMS`.

[WINDOWS POWERSHELL]

```powershell
(Get-FileHash .\EtherlightingEvolved-Setup-<version>.exe -Algorithm SHA256).Hash.ToLower()
```

[MACOS TERMINAL]

```sh
shasum -a 256 EtherlightingEvolved-<version>-osx-arm64.zip
```

[LINUX TERMINAL]

```sh
sha256sum EtherlightingEvolved-<version>-linux-x64.tar.gz
```

The installer and each client archive also have a build provenance attestation that ties the file to the commit and workflow run that built it. Check it with the [GitHub CLI](https://cli.github.com/):

```sh
gh attestation verify EtherlightingEvolved-Setup-<version>.exe --repo Views2k/etherlighting-evolved
```

### Windows

Run Setup. It asks whether to install EE for you only or for all users, and it can start EE when you sign in to Windows. Updating EE keeps your data and the Start with Windows setting.

### macOS and Linux

Extract the archive. On macOS, open `EtherlightingEvolved.app`. On Linux, run `./EtherlightingEvolved` in the extracted folder. Choose **Change workspace address** and enter the private address shown in Settings → Mobile & remote access on the Windows computer. The [desktop guide](desktop/README.md) has the details, including approving a tagged Tailscale device.

## First run

1. Start Etherlighting Evolved from the Start menu. The app starts its workspace service. Choose **Continue with demo** to try the controls on a simulated switch.
2. To connect a switch, enter its address, SSH username and password. The first time EE connects to a switch, it shows the switch's SSH host key fingerprint and sends the password only after you accept the key. After a switch is reset or replaced, EE shows the saved and the new fingerprints, and the new key can be accepted only in EE on the computer that runs it.
3. Select **Save login on this PC** to let EE reconnect after a restart. Windows protects the saved login for your Windows account.
4. In Settings → UniFi Network, select your controller and sign-in method to add device names and port telemetry.
5. In Settings → Mobile & remote access, select **Use Tailscale** to open the workspace from other devices at a private Tailscale address.

Closing the window keeps EE running in the notification area. Choose **Quit** in the tray menu to stop it. To use the workspace in a browser on the same computer, choose **Open in browser** in the desktop app. A browser tab opened another way shows the Mobile & remote access settings read-only and cannot close the workspace.

Devices that open EE through Tailscale can change lighting, effects, automation, profiles, schedules, port names and fans, can reconnect to the saved switch, and can choose Handoff to UniFi. Logging in to a new switch, forgetting the saved login, UniFi Network setup, Tailscale settings and accepting a switch's key work only on the Windows computer that runs EE.

## What EE changes on the switch

While it is connected, EE sets the port lights through the switch's own lighting service and leaves that service running. EE does not install firmware, load kernel modules or change the switch's network configuration.

Two features leave a program running on a USW-Pro-XG-8-PoE after EE closes. Both run from the switch's memory, and a switch restart removes them.

When the switch login is saved and the firmware passes EE's playback checks, EE copies a lighting player to `/tmp/ee-lighting-playback-v1`. The switch keeps showing your lighting, including traffic effects, while EE is closed.

A fan mode other than Switch control runs from `/tmp/ee-fan-control-v1`. While it runs, the switch's own fan service is paused. The service takes the fans back when EE's fan control stops or fails. EE saves the fan mode for each switch, and a switch that EE has not controlled before starts in Switch control.

### Return the switch to UniFi

Choose **Handoff to UniFi** in Settings. EE stops its lighting and the lighting player, returns the fans to the switch's own controller, closes the SSH connection, and waits until you connect again. Saved logins, profiles, the lighting setup and the fan mode of each switch are kept. To return only the fans, choose **Switch control** on the Fans page.

Uninstalling EE does not connect to the switch. Before you uninstall, connect to each switch EE controls and choose **Handoff to UniFi**. Otherwise the lighting player and fan control keep running on the switch until it restarts.

## Data and logs

On Windows, EE keeps its data in two folders. The uninstaller removes the program and leaves both folders in place.

| Folder | Contents |
| --- | --- |
| `%USERPROFILE%\.etherlighting-evolved` | Saved switch and UniFi Network logins (encrypted with Windows DPAPI for your account), pinned SSH host keys, lighting, profiles, schedules, port names, fan settings, the switch playback record and the output ownership records in `output-ownership` |
| `%LOCALAPPDATA%\Etherlighting Evolved` | Desktop app settings and `desktop.log`. `workspace` holds the private access settings, approved Tailscale devices and `service-diagnostics.log`. `demo` holds the demo's data. |

The macOS and Linux clients keep their settings and `desktop.log` in `~/Library/Application Support/Etherlighting Evolved` and `~/.config/etherlighting-evolved`. To remove a client, quit it and delete the app or its extracted folder and that data folder. If you turned on **Start at login** in its tray menu, turn it off first, or delete `~/Library/LaunchAgents/com.views2k.etherlighting-evolved.plist` on macOS or `~/.config/autostart/etherlighting-evolved.desktop` on Linux. On Linux, also delete the files that the [desktop guide](desktop/README.md#add-ee-to-the-linux-applications-menu) adds for the applications menu: `~/.local/bin/EtherlightingEvolved`, `~/.local/share/applications/etherlighting-evolved.desktop` and `~/.local/share/icons/hicolor/256x256/apps/etherlighting-evolved.png`.

To remove EE and its data from Windows, choose **Handoff to UniFi** for each switch, uninstall EE in Windows Settings → Apps, then delete both folders. Delete `output-ownership` only after Handoff to UniFi has finished: it records the switches whose lighting EE has to return at the next login. EE removes its Tailscale Serve route when it quits, and uninstalling removes it as well when EE was installed for you only. The uninstaller of an installation for all users skips that step. In that case, or when the uninstaller reports that it could not confirm the removal, run `tailscale serve status`; if the port of EE's private address (8444 by default) still forwards to `http://127.0.0.1`, remove it with `tailscale serve --https=<port> off`.

## Reporting a problem

Open an issue on the [issue tracker](https://github.com/Views2k/etherlighting-evolved/issues) with the bug report or hardware report form. Include the EE version from Settings → About, your Windows version, and the switch model and firmware.

The workspace service writes `%LOCALAPPDATA%\Etherlighting Evolved\workspace\service-diagnostics.log`. It records the time, the event, the EE version, error types, code locations, EE's fixed error codes and EE's log message templates without their values. It records no hosts, names or passwords, so you can attach it to an issue. `desktop.log` records the desktop app's errors and the last output lines of a service that stopped. Those lines can include file paths and addresses, so read the file before you attach it.

Report security vulnerabilities privately as described in [SECURITY.md](SECURITY.md), not in a public issue. Outside pull requests are not accepted; see [CONTRIBUTING.md](CONTRIBUTING.md).

## Build from source

Builds use Python 3.14 (`.python-version`), Node.js 24.18.0 (`.node-version`; running from source needs Node.js 22 or later), the .NET 10 SDK (`global.json`) and, for the installer, Inno Setup 6.7. To try the simulator from a Windows checkout, with Python 3.14 and Node.js on the path:

[WINDOWS POWERSHELL]

```powershell
python -m venv .venv
.\.venv\Scripts\python.exe -m pip install --require-hashes --only-binary=:all: -r requirements-ci.txt
.\.venv\Scripts\python.exe -m pip install --no-deps --no-build-isolation -e .
$env:EE_PREVIEW_LIVE = '0'
npm start
```

Open [the local workspace](http://127.0.0.1:4180). The simulator keeps its own data in `outputs/browser-preview-data/`. Set `EE_PREVIEW_LIVE` to `1` to log in to a switch instead; live mode runs on Windows only. The [development guide](docs/DEVELOPMENT.md) covers macOS and Linux, tests and the [Windows installer build](docs/DEVELOPMENT.md#windows-installer). The [desktop guide](desktop/README.md#build-and-test) covers the desktop app.

## Repository layout

| Directory | Contents |
| --- | --- |
| `desktop/` | Desktop app, its checks and the macOS and Linux client packaging |
| `preview/` | Workspace gateway, private access gateway and browser workspace |
| `src/etherlighting_evolved/` | Python lighting engine, API, switch adapters and the programs EE runs on the switch |
| `src/*.mjs` | Effect catalog, color, rule and icon modules shared by the browser workspace |
| `assets/` | Logo, favicon and web app icons |
| `tests/` | Python and JavaScript tests and their fixtures |
| `packaging/` | Engine entry point, PyInstaller specification, Windows installer script and build script |
| `tools/` | Build, verification, version and fixture tools |
| `docs/` | API contract, hardware list, development and workflow guides |
| `.github/` | CI and release workflows, issue forms and the pull request template |

## Documentation

The [hardware list](docs/HARDWARE.md) describes the supported models, the firmware review and the programs EE runs on the switch. The [desktop guide](desktop/README.md) covers the desktop app and the macOS and Linux clients. The [development guide](docs/DEVELOPMENT.md) covers the source setup, tests, data locations and the installer build. [INTEGRATION.md](docs/INTEGRATION.md) describes how the clients, the gateway and the engine work together, and [API.md](docs/API.md) is the API contract. [WORKFLOW.md](docs/WORKFLOW.md) describes branches, pull request checks and releases. Changes are listed in the [changelog](CHANGELOG.md).

## License

[MIT](LICENSE), copyright Views2k. Packaged third-party components keep their own notices and licenses.
