# Security policy

## Reporting a vulnerability

Report vulnerabilities privately through GitHub private vulnerability reporting: open the **Security** tab of this repository and choose **Report a vulnerability**. Do not report a vulnerability in a public issue, discussion or pull request.

Include the EE version (Settings → About in the desktop app), the operating system, the switch model and firmware if a switch is involved, the steps that show the problem, and what an attacker could do with it. Leave out real passwords, API keys, Tailscale names and public IP addresses.

EE has a single maintainer and no fixed response time. Vulnerabilities in UniFi firmware, UniFi Network or Tailscale themselves belong with Ubiquiti or Tailscale.

## Supported versions

Only the latest release receives security fixes.

## Scope

The report channel covers the Windows app and its installer, the workspace gateway and the Tailscale and LAN gateways (`preview/`), the lighting engine and its API (`src/etherlighting_evolved/`), the macOS and Linux desktop clients, the release workflow, and the programs EE runs on the switch.

## What EE stores

On Windows, the engine keeps its data in `%USERPROFILE%\.etherlighting-evolved`:

- The switch SSH login (`login.json`) and the UniFi Network login or API key (`controller-login.json`). EE saves them only when you choose to save a login. Windows DPAPI encrypts them for your Windows account, so any program that runs under your account can decrypt them. Saving a login is not available on macOS or Linux.
- The saved SSH host key fingerprint of each switch address (`known_switches.json`), described under [SSH host keys](#ssh-host-keys).
- The SHA-256 fingerprint of a UniFi Network certificate that the system does not trust, saved with the controller login after you approve it.
- Lighting, profiles, schedules, port names, the fan mode of each switch, the switch playback record and output ownership records. These files hold no passwords.
- An optional `ee.toml` with advanced settings, which you create yourself. A switch password written there is stored in plain text.

The desktop app and the workspace gateway keep their settings in `%LOCALAPPDATA%\Etherlighting Evolved`: the saved workspace address and preferences, `desktop.log`, and in `workspace` the Tailscale private access setting (`private-access.json`), the approved Tailscale devices (`approved-device.json`), the LAN access setting with the paired devices (`lan-access.json`) and `service-diagnostics.log`. `lan-access.json` holds a SHA-256 hash of each paired device's key, not the key. An uninstall for one user deletes it; an uninstall for all users cannot reach it. The macOS and Linux clients keep their settings and `desktop.log` in `~/Library/Application Support/Etherlighting Evolved` and `~/.config/etherlighting-evolved`. A client that paired over LAN access keeps the key for each LAN address there in `workspace-keys.json`, readable by any program that runs under that account. The Windows app opened with `--url` that pairs with another computer keeps the same file in `%LOCALAPPDATA%\Etherlighting Evolved`, also readable by programs that run under that account.

`service-diagnostics.log` records error types, code locations, fixed error codes and log message templates without their values. It holds no hosts, names or passwords. `desktop.log` keeps the last output lines of a service that stopped, which can include file paths and addresses; user names and passwords in addresses are removed.

The engine token is created again at every start. The gateway makes 32 random bytes and passes them to the engine on its standard input. Neither writes the token to disk, and the app passes neither the token nor credentials in command-line arguments.

## SSH host keys

EE signs in to a switch with the SSH username and password you give it and requires no SSH key on the switch. A first login, a reconnect with the saved login or the connection at startup to an address that has no saved key signs in and then saves the key that the switch presented (trust on first use). EE therefore sends the password to whatever device answers at that address the first time. To check the first key, compare the fingerprint in Settings → Connection with what `ssh-keygen -l` or an SSH client shows over a path you trust.

After that, EE checks the key during the SSH key exchange, before it sends the username or password; GSS key exchange is off, so the check cannot be skipped. A key that differs from the saved one ends the connection, and the Connect page shows the saved and the new fingerprints. The new key can be accepted only in EE on the computer that runs it: the Tailscale and LAN gateways refuse `POST /api/session/host-key/replace`, and the engine refuses that request from an address that is not a loopback address. EE accepts only a key it saw at that address itself; it keeps the presented keys in memory for at most 32 addresses. Accepting a key deletes the switch playback record of the previous key. `eectl forget-host` removes a saved key, and the next connection saves whatever key is presented.

## What EE leaves on the switch

EE logs in over SSH with the login you give it and does not install SSH keys on the switch. It sets the port lights through the switch's own lighting service. It does not install firmware, load kernel modules or change the switch's network configuration. EE only reads from UniFi Network; it signs in and does not change its settings.

On a USW-Pro-XG-8-PoE, two programs can keep running after EE closes:

- A lighting player in `/tmp/ee-lighting-playback-v1`, installed when the switch login is saved and the firmware passes EE's playback checks, so the switch keeps showing the saved lighting.
- Fan control in `/tmp/ee-fan-control-v1`, while a fan mode other than Switch control is chosen. While it runs, the switch's fan service `ubnt-fanctrl` is paused with SIGSTOP. A guard process resumes it with SIGCONT when fan control stops, fails or disappears.

Both directories are readable only by the SSH user and live in the switch's memory, so a switch restart removes them. **Handoff to UniFi** in Settings stops both, and **Switch control** on the Fans page stops fan control.

## Access model

The engine listens on `127.0.0.1` on a port chosen at each start and accepts only requests that carry the engine token. The workspace gateway listens on `127.0.0.1:4180`. When that port is taken, the desktop app uses the next free port up to 4199; its demo starts at port 4190. The gateway adds the token to the requests it forwards. It accepts only requests addressed to `127.0.0.1` or `localhost` on its own port, refuses API and WebSocket requests that a browser marks as coming from another website (`Origin` and `Sec-Fetch-Site`), and refuses request bodies over 1 MiB.

Other programs on the same computer, including programs of other signed-in users, can use the lighting, fan and session requests through the gateway. Do not run EE on a computer shared with people you do not trust. Changing the Tailscale and LAN access settings and closing the workspace also need an administration key that EE creates at each start. The desktop app holds the key and sends it with those requests. **Open in browser** opens a browser tab with a one-use code, and the gateway exchanges that code for a cookie whose value is a separate secret, also created at each start. The browser sends that cookie only with the administration requests, and the key itself never reaches the browser. Browsers do not separate cookies by port on `127.0.0.1`, so a page served by another local web server could have the browser send that cookie to that server. The cookie stops working when EE stops.

### Remote devices

Other devices reach EE only through the Tailscale gateway or the LAN gateway. Both are optional and off until the owner turns them on. Once on, they run whenever EE runs its normal service on Windows, including before a switch is connected and while the app uses **Continue with demo**; the separate demo started with `--demo` and a simulator run from source have neither. They forward only the requests listed as remote in `preview/remote-routes.mjs` and refuse every other request with 403 `local_access_required`, including a request added to EE later without an entry. Logging in to a new switch, forgetting the saved login, UniFi Network setup, the Tailscale and LAN access settings and accepting a changed SSH key work only on the computer that runs EE. The gateways are the only place where this is enforced: the engine does not tell remote requests from local ones, and every WebSocket command is available to remote devices. Neither gateway forwards cookies, the `Authorization` header or the administration key, and neither passes `Set-Cookie` back.

Tailscale is not required. While private access is off, EE runs no Tailscale command. The one exception is a route that EE added earlier and has not yet seen removed, for example because Tailscale was not running when private access was turned off, or because release 0.5.1 turned it off without recording the removal: EE reads `tailscale serve status` at each start, refresh, quit and uninstall for one user until a start or refresh sees the route gone and records that in `private-access.json`. When it is on, devices reach EE through Tailscale Serve, which keeps the address inside your tailnet and encrypts the connection. EE never uses Tailscale Funnel: it refuses a Serve port that is open to Funnel and refuses requests that arrive through Funnel. Private access admits the Tailscale account that set it up, or every account in the tailnet if you choose that, and tagged devices that the owner approves one by one on the Windows computer. EE removes its own Tailscale Serve route when it stops. Uninstalling removes the route as well when EE was installed for you only. The uninstaller of an installation for all users skips that step; run `tailscale serve status` afterwards and remove EE's port with `tailscale serve --https=<port> off`.

LAN access is off by default. When it is on, EE listens on port 4280 (or the port the owner chose) on every IPv4 interface, IPv4 only. Before it reads any HTTP, it closes every connection that does not arrive at one of the computer's private IPv4 addresses from a private IPv4 address, so Tailscale, public, link-local and IPv6 addresses are refused. The `Host` header must be the address the connection arrived on, which stops a page on another name that resolves to the computer from using a device's cookie. A device pairs with a 6-digit code shown on the computer running EE; a code works once and ends after 5 minutes or 5 wrong attempts. Pairing gives the device a random key in a cookie, and EE saves only a hash of it. Paired devices stay paired until the owner removes them or turns LAN access off, and at most 16 can be paired. A browser keeps the cookie for one year and then needs a new code; the desktop clients keep their keys with no time limit.

LAN traffic is plain HTTP and is not encrypted. Anyone who can watch or change traffic on the network can read the commands and workspace data that cross it, including the switch address, SSH port and SSH username, port names, profiles, schedules, UniFi Network device names and telemetry, fan readings and the Tailscale private address, and can copy a device's key from any request or the pairing code once. With a copied key they can do anything a paired device can until that device is removed or LAN access is turned off. Switch passwords, saved logins, UniFi Network passwords and API keys and the administration key never cross LAN access, because the requests that carry them are local-only. Browsers send the LAN cookie to every port of the same address, so another web server on the computer running EE receives it for requests under `/api`. Use Tailscale when the network is not trusted. The installer adds no firewall rule; Windows asks the first time EE's `node.exe` listens on the network.

## Command-line server

The separate command-line server, `etherlighting-evolved serve`, listens on `127.0.0.1:8787` and needs no token there. Listening on any other address, or allowing every website with `cors_origins = ["*"]`, requires a token of at least 32 characters in `EE_API_TOKEN` or `api.token`. The desktop app and the workspace do not use this server.

## UniFi Network

EE verifies the UniFi Network certificate. A certificate that the system does not trust is used only after you approve its fingerprint, and a changed certificate stops the connection until you approve it again.
