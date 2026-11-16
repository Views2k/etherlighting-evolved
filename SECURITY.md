# Security policy

## Reporting a vulnerability

Report vulnerabilities privately through GitHub private vulnerability reporting: open the **Security** tab of this repository and choose **Report a vulnerability**. Do not report a vulnerability in a public issue, discussion or pull request.

Include the EE version (Settings → About in the desktop app), the operating system, the switch model and firmware if a switch is involved, the steps that show the problem, and what an attacker could do with it. Leave out real passwords, API keys, Tailscale names and public IP addresses.

EE has a single maintainer and no fixed response time. Vulnerabilities in UniFi firmware, UniFi Network or Tailscale themselves belong with Ubiquiti or Tailscale.

## Supported versions

Only the latest release receives security fixes.

## Scope

The report channel covers the Windows app and its installer, the workspace gateway (`preview/`), the lighting engine and its API (`src/etherlighting_evolved/`), the macOS and Linux desktop clients, the release workflow, and the programs EE runs on the switch.

## What EE stores

On Windows, the engine keeps its data in `%USERPROFILE%\.etherlighting-evolved`:

- The switch SSH login (`login.json`) and the UniFi Network login or API key (`controller-login.json`). EE saves them only when you choose to save a login. Windows DPAPI encrypts them for your Windows account, so any program that runs under your account can decrypt them. Saving a login is not available on macOS or Linux.
- The pinned SSH host key fingerprint of each switch (`known_switches.json`). EE shows a new switch's fingerprint and sends the password only after you accept it, and it stops a login when a switch presents a different key. A replaced key can be accepted only in EE on the computer that runs it.
- The SHA-256 fingerprint of a UniFi Network certificate that the system does not trust, saved with the controller login after you approve it.
- Lighting, profiles, schedules, port names, fan settings, the switch playback record and output ownership records. These files hold no passwords.
- An optional `ee.toml` with advanced settings, which you create yourself. A switch password written there is stored in plain text.

The desktop app and the workspace gateway keep their settings in `%LOCALAPPDATA%\Etherlighting Evolved`: the saved workspace address and preferences, `desktop.log`, and in `workspace` the private access setting, the approved Tailscale devices and `service-diagnostics.log`. The macOS and Linux clients keep their settings and `desktop.log` in `~/Library/Application Support/Etherlighting Evolved` and `~/.config/etherlighting-evolved`.

`service-diagnostics.log` records error types, code locations, fixed error codes and log message templates without their values. It holds no hosts, names or passwords. `desktop.log` keeps the last output lines of a service that stopped, which can include file paths and addresses; user names and passwords in addresses are removed.

The engine token is created again at every start. The gateway makes 32 random bytes and passes them to the engine on its standard input. Neither writes the token to disk, and the app passes neither the token nor credentials in command-line arguments.

## What EE leaves on the switch

EE logs in over SSH with the login you give it and does not install SSH keys on the switch. It sets the port lights through the switch's own lighting service. It does not install firmware, load kernel modules or change the switch's network configuration. EE only reads from UniFi Network; it signs in and does not change its settings.

On a USW-Pro-XG-8-PoE, two programs can keep running after EE closes:

- A lighting player in `/tmp/ee-lighting-playback-v1`, installed when the switch login is saved and the firmware passes EE's playback checks, so the switch keeps showing the saved lighting.
- Fan control in `/tmp/ee-fan-control-v1`, while a fan mode other than Switch control is chosen. While it runs, the switch's fan service `ubnt-fanctrl` is paused with SIGSTOP. A guard process resumes it with SIGCONT when fan control stops, fails or disappears.

Both directories are readable only by the SSH user and live in the switch's memory, so a switch restart removes them. **Handoff to UniFi** in Settings stops both, and **Switch control** on the Fans page stops fan control.

## Access model

The engine listens on `127.0.0.1` on a port chosen at each start and accepts only requests that carry the engine token. The workspace gateway listens on `127.0.0.1:4180`. When that port is taken, the desktop app uses the next free port up to 4199; its demo uses 4190. The gateway adds the token to the requests it forwards. It accepts only requests addressed to `127.0.0.1` or `localhost` on its own port, refuses API and WebSocket requests that a browser marks as coming from another website (`Origin` and `Sec-Fetch-Site`), and refuses request bodies over 1 MiB.

Other programs on the same computer, including programs of other signed-in users, can use the lighting, fan and session requests through the gateway. Do not run EE on a computer shared with people you do not trust. Changing Tailscale settings and closing the workspace also need a secret that the gateway creates at each start. The desktop app holds it, and **Open in browser** passes it to a browser tab, which keeps it in a cookie that is sent only with those requests. Browsers do not separate cookies by port on `127.0.0.1`, so a page served by another local web server could have the browser send that cookie to that server. The cookie stops working when EE stops.

Other devices reach EE only through Tailscale Serve, which keeps the address inside your tailnet. EE never uses Tailscale Funnel: it refuses a Serve port that is open to Funnel and refuses requests that arrive through Funnel. Private access admits the Tailscale account that set it up, or every account in the tailnet if you choose that, and tagged devices that the owner approves one by one on the Windows computer. Remote devices can call only the requests listed for them. Logging in to a new switch, forgetting the saved login, UniFi Network setup, Tailscale settings and accepting a switch's key work only on the computer that runs EE. EE removes its own Tailscale Serve route when it stops and when it is uninstalled.

The separate command-line server, `etherlighting-evolved serve`, listens on `127.0.0.1:8787` and needs no token there. Listening on any other address, or allowing every website with `cors_origins = ["*"]`, requires a token of at least 32 characters in `EE_API_TOKEN` or `api.token`. The desktop app and the workspace do not use this server.

EE verifies the UniFi Network certificate. A certificate that the system does not trust is used only after you approve its fingerprint, and a changed certificate stops the connection until you approve it again.
