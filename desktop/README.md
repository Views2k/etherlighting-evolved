# Etherlighting Evolved desktop

The desktop workspace uses Avalonia 11.3 and .NET 10. Lighting, Effects, Automation, Profiles, Fans, Settings and Help use native controls. The window exchanges scene changes and live port readings with the EE gateway.

## Run the app

The Windows installer includes the desktop app, gateway and lighting engine. Start Etherlighting Evolved from the Start menu, then connect to your switch or choose the demo. Closing the window hides it in the notification area. Use the tray menu to open it again or quit.

To use the workspace in a browser on the same computer, choose **Open in browser** in the tray menu or in Settings > Connection. The tab it opens can change the Tailscale and LAN access settings and close the web session; a tab opened another way, such as a bookmark of `http://127.0.0.1:4180`, shows those settings read-only.

Fans controls the fans of a USW-Pro-XG-8-PoE. EE saves the fan mode for each switch, and a switch that EE has not controlled before starts in Switch control. Fan control runs on the switch and keeps running after you quit EE; **Switch control** returns the fans to the switch's own controller, and **Handoff to UniFi** in Settings returns them as well. The uninstaller does not connect to the switch, so before you uninstall, connect to each switch EE controls and choose **Handoff to UniFi**, which also stops the lighting player. The [README](../README.md#what-ee-changes-on-the-switch) lists what EE leaves on the switch.

The first connection to a switch that has no saved SSH key signs in with the username and password and saves the switch's key. Settings > Connection shows the connected switch's SSH key fingerprint and key type as selectable text. If a switch presents a different key, the Connect page shows the saved and the new fingerprints, and **Accept new key** appears only in the window on the computer that runs the workspace service. A window connected through a Tailscale or LAN address shows a message to review the key on that computer instead. If the key changes while EE is connected, choose **Quit** from the tray menu and open EE again to review it.

For macOS, extract the archive for your processor and open `EtherlightingEvolved.app`. On Linux, extract the archive into a folder and run `./EtherlightingEvolved` in that folder. Keep the application and its libraries together. These archives contain the desktop client and the .NET runtime, but no workspace service, and only the Windows app connects to a switch. Connect to the workspace of a Windows computer that runs EE through its Tailscale private HTTPS address, through its LAN address with a pairing code (see [Connect over your network without Tailscale](#connect-over-your-network-without-tailscale)), or to a simulator started from a source checkout on the same computer. Choose **Change workspace address** on the connection error screen or in Settings to save the address. An explicit `--url` argument takes precedence over the saved address, and the `EE_WORKSPACE_URL` environment variable sets the same address with lower precedence than `--url`. Each source accepts only the addresses the dialog accepts: an HTTPS origin, an HTTP origin on this computer, or an HTTP origin at a private IPv4 address, one that starts with 10, 172.16 to 172.31 or 192.168, with a port other than 80, without account details, a path or a query. Host names such as `.local` names and IPv6 addresses are not accepted for LAN access. A `--url` value that does not qualify opens the connection error screen with **Change workspace address**, and an environment value that does not qualify is ignored. The default local address is `http://127.0.0.1:4180/`.

A window connected through a Tailscale or LAN address can use lighting, effects, automation, profiles, schedules, port names and fans, and can reconnect to the switch whose login is saved on the Windows computer. Its Connect page shows **Connect with saved login**, **Retry handoff** when a handoff waits, and **Change workspace address** instead of the login form, and Settings leaves out **Forget saved switch login** and UniFi Network setup. **Change workspace address** stays available on the Connect page while a connection or handoff is pending. A client connected by address, including the Windows app opened with `--url` or `EE_WORKSPACE_URL`, cannot change the Tailscale or LAN access settings or close a workspace it did not start. Use the Windows computer for those.

On macOS, the packaging script signs the graphics libraries and completed application with an ad hoc signature and verifies the bundle before archiving it. A macOS host produces the signature-verified ZIPs used by CI. Developer ID signing and notarization are separate distribution steps.

### Desktop log

The app keeps `desktop.log` in its data folder: `%LOCALAPPDATA%\Etherlighting Evolved` on Windows, `~/Library/Application Support/Etherlighting Evolved` on macOS and `~/.config/etherlighting-evolved` on Linux. It records errors the window could not report in full and why the bundled service did not start or stopped. Each entry has the time, the app version, the error type and the code locations involved, or the service's exit code and its last 64 error output lines. The app does not record its own error messages, screen text or request contents. The service's output lines can include file paths and addresses; the user name and password in an address are removed. The file stays on the computer. When it passes 256 KB it is renamed to `desktop.log.1`, replacing the previous one.

### Tailscale private access

Tailscale is optional. **Tailscale private access** in Settings > Remote stays off until you select **Use Tailscale**, and EE runs no Tailscale command while it is off. The one exception is a route that EE added earlier and has not yet seen removed, for example because Tailscale was not running when you turned private access off: EE then reads `tailscale serve status` at each start, refresh and quit until a start or refresh sees the route gone. When it is on, EE adds one Tailscale Serve route on its HTTPS port (8444 by default) when it starts and removes it when it quits. Turning private access off removes the route, and EE never changes other Serve routes. The port can be changed under **Connection options** while private access is off.

An uninstall for one user removes EE's route and names the port if it cannot confirm the removal. To remove the route by hand, quit EE and run the cleanup tool as the user who runs EE. An installation for all users skips the automatic step; for it, set `$ee` to `Join-Path $env:ProgramFiles 'Etherlighting Evolved'` instead.

[WINDOWS POWERSHELL]

```powershell
$ee = Join-Path $env:LOCALAPPDATA 'Programs\Etherlighting Evolved'
& "$ee\runtime\node.exe" "$ee\tools\private-access-cleanup.mjs"
```

The tool exits with 0 when the route is gone or there is none, with the HTTPS port number when it cannot confirm the removal, and with 2 when it cannot read the setting or is run with arguments.

### Connect a tagged Tailscale device

Devices signed in to the workspace owner’s Tailscale account can use the private workspace address directly. For a tagged device, enable **Use Tailscale** in **Tailscale private access** on the Windows host. Open PowerShell as the user who runs EE and enter the device’s full Tailscale DNS name when prompted.

[WINDOWS POWERSHELL]

```powershell
$ee = Join-Path $env:LOCALAPPDATA 'Programs\Etherlighting Evolved'
$device = Read-Host 'Full Tailscale DNS name of the device to approve'
& "$ee\runtime\node.exe" "$ee\tools\approve-workspace-device.mjs" approve $device
```

On the approved device, choose **Change workspace address** and enter the private address shown by the Windows host. Repeat the approval command for each tagged device. The host can approve an offline device when Tailscale still confirms its identity and assigned addresses.

Use `status $device` to check that device, `status` to count saved approvals, or `revoke $device` to remove its access and keep the others. Use `revoke --all` to clear all device approvals.

### Connect over your network without Tailscale

LAN access lets phones and other computers on your network open EE at the Windows computer's address, without Tailscale. It is off by default. Once turned on, it runs whenever EE's service runs on the Windows computer, including before a switch is connected and while you use **Continue with demo**, but not in the demo that the app opens with `--demo`. Paired devices have the same limits as devices on Tailscale: they can use lighting, effects, automation, profiles, schedules and port names, control fans in the desktop app and reconnect to the saved switch, but logging in to a new switch, forgetting the saved login, UniFi Network setup, the LAN and Tailscale settings and accepting a changed SSH key work only on the computer running EE.

LAN access uses plain HTTP, so traffic between your devices and the computer running EE is not encrypted. Anyone who can watch or change traffic on your network can read workspace data, such as the switch address and SSH username, port names, profiles and schedules, and can copy a device's access key. With that key they can do anything a paired device can, including changing lighting and fans and handing lighting back to UniFi, until you remove that device or turn off LAN access. EE does not send switch or UniFi passwords over LAN access, because switch login and UniFi Network setup work only on the computer running EE. EE on another device never asks for switch or UniFi passwords; if it does, do not enter them. Use Tailscale for an encrypted connection.

On the Windows computer:

1. Make sure Windows lists the network as private. In Windows Settings, open **Network & internet**, choose the connection and set **Network profile type** to **Private**. Windows marks new networks as public.
2. In EE, open **Settings**, go to **LAN access**, select **Allow devices on my network** and choose **Apply setting**.
3. The first time EE's service listens on the network, Windows shows a Windows Security Alert for Node.js JavaScript Runtime, the program that runs EE's service. Allow it on private networks only. Allowing it needs administrator approval.
4. LAN access lists the address for other devices, such as `http://192.168.1.2:4280`. Choose **Pair a device** to show a pairing code. A code works once and ends after 5 minutes, after 5 wrong attempts, or when you show a new one.

On the phone or other computer, open the address in a browser, or in the macOS or Linux client choose **Change workspace address** and enter it. Enter the pairing code and a name for the device. The desktop client offers the computer's name. Each device needs its own code.

The Windows firewall rule belongs to the program file, not to EE:

- If you chose Cancel in the alert, Windows blocks Node.js JavaScript Runtime at that path and does not ask again. Open Windows Security, choose **Firewall & network protection**, then **Allow an app through firewall**, choose **Change settings** and select **Private** for Node.js JavaScript Runtime.
- If no alert appears, for example because notifications for blocked apps are turned off or a policy manages the firewall, add the program on the same page with **Allow another app**.
- Any script that EE's `node.exe` runs can accept connections on the networks you allow. In an installation for all users the file is `C:\Program Files\Etherlighting Evolved\runtime\node.exe`, which only administrators can change. In an installation for one user it is `%LOCALAPPDATA%\Programs\Etherlighting Evolved\runtime\node.exe`, in that user's folder, and each Windows user who installs EE has a separate file and decision.
- A rule for private networks does not apply while Windows lists the current network as public.
- Upgrades keep the path, so the decision stays. Turning LAN access off and uninstalling EE leave the rule in place; remove it on **Allow an app through firewall**.
- EE does not check the firewall. If a device cannot open the address, check the firewall settings on the computer running EE.

Do not forward the LAN port on your router. LAN access uses port 4280 unless you choose another port under **Connection options** while LAN access is off. It uses IPv4 only. It accepts connections from private IPv4 addresses, those that start with 10, 172.16 to 172.31 or 192.168, including other VLANs that your router routes to the computer running EE. It refuses Tailscale addresses; devices on Tailscale use the encrypted Tailscale address instead.

Give the computer running EE a fixed address, for example with a DHCP reservation in your router or in UniFi Network. When its address changes, each device opens the new address and pairs again, and the old entries stay listed until you remove them.

LAN access lists each paired device with how it paired, when and from which address, and when it was last used. **Remove** ends that device's access at once, including an open workspace. Turning LAN access off removes and disconnects every paired device, and each one needs a new code after you turn it on again. At most 16 devices can be paired. A browser keeps its access key for one year and then needs a new code; its old entry stays listed until you remove it. The desktop apps keep their keys with no time limit.

On an iPhone or iPad, a Home Screen copy keeps its own cookies and pairs separately from Safari. Android cannot install the workspace as an app from a plain-HTTP address. Browsers with an HTTPS-only mode show a warning before they open the address.

On macOS 15 or later, the app asks before it connects to devices on your local network. Allow it, or later turn on Etherlighting Evolved under System Settings > Privacy & Security > Local Network.

The macOS and Linux clients, and the Windows app opened with `--url`, keep the key for each LAN address in `workspace-keys.json` in the data folder listed under [Desktop log](#desktop-log): one key per address, at most 16. A key is replaced only by a new pairing with that address. A file saved by a newer version of EE is left as it is, and a pairing then does not save its key.

An uninstall for one user deletes the LAN access setting and the paired devices, so a later installation starts with LAN access off. An uninstall for all users runs as an administrator and cannot reliably reach your data folder, so the setting and the paired devices stay until LAN access is turned off, and a later installation opens LAN access again when it starts. Turn LAN access off before you uninstall to remove them in either case.

### Add EE to the Linux applications menu

Keep the extracted folder in a permanent location. To add EE to your own applications menu, open a terminal in that folder and run the following.

[LINUX TERMINAL]

```sh
app_dir="$(pwd -P)"
data_dir="${XDG_DATA_HOME:-$HOME/.local/share}"
mkdir -p "$HOME/.local/bin"
ln -sfn "$app_dir/EtherlightingEvolved" "$HOME/.local/bin/EtherlightingEvolved"
install -Dm644 "$app_dir/etherlighting-evolved.png" "$data_dir/icons/hicolor/256x256/apps/etherlighting-evolved.png"
install -Dm644 "$app_dir/etherlighting-evolved.desktop" "$data_dir/applications/etherlighting-evolved.desktop"
```

The menu entry runs `EtherlightingEvolved` by name. Make sure `$HOME/.local/bin` is on your graphical session's `PATH`. If you add it to your login profile, sign out and back in, then open **Etherlighting Evolved** from the applications menu.

## Build and test

Use the .NET 10 SDK and the locked packages for the two projects in this folder. Source mode uses the existing Node and Python environments described in the [development guide](../docs/DEVELOPMENT.md); set `EE_PREVIEW_PYTHON` to use a Python other than the checkout's `.venv`. The app run from a source checkout passes that variable to the gateway it starts, and `tools/verify.ps1` sets it to its `-PythonExe` for the desktop check; a packaged app ignores it. Run the following from the repository root. The restore downloads the locked packages into `outputs/nuget-packages`, as `desktop/NuGet.Config` sets, and restores the app project too.

[WINDOWS POWERSHELL]

```powershell
dotnet restore desktop/EtherlightingEvolved.Native.Check/EtherlightingEvolved.Native.Check.csproj --configfile desktop/NuGet.Config --locked-mode
dotnet build desktop/EtherlightingEvolved.Native.Check/EtherlightingEvolved.Native.Check.csproj -c Release --no-restore
dotnet desktop/EtherlightingEvolved.Native.Check/bin/Release/net10.0/EtherlightingEvolved.Native.Check.dll --service --output outputs/native-check
```

The check program renders windows off screen. Its service checks use a simulator with isolated data. To use the app interactively from the same build, launch the source app in demo mode. A demo copy can run beside the installed app; it keeps its workspace data and its preferences, such as the theme and saved palettes, in the `demo` folder of the app's data folder.

[WINDOWS POWERSHELL]

```powershell
dotnet run --project desktop/EtherlightingEvolved.Native -c Release --no-build -- --demo
```

### Program options

| Option | Effect |
| --- | --- |
| `--url <address>` | Opens the workspace at this address instead of starting the bundled service or using the saved address |
| `--demo` | Opens the demo with its own data, beside an installed copy |
| `--background` | Starts in the notification area without showing the window; Start with Windows uses it |
| `--quit` | Asks a running copy to quit; the installer uses it |
| `--shot <folder> --page <name>` | Renders one page in a window off screen, saves `shot.png` in the folder and exits. `--theme <id>` and `--size <width>x<height>` (default 1440x900) set the look. The run keeps its data in the folder. |
| `EE_WORKSPACE_URL` | Environment variable with the same effect as `--url`, used when `--url` is not given |
| `EE_GPU_RENDERING=1` | Environment variable that selects GPU composition on Windows instead of the default software rendering, for comparison |

## Package

Use `packaging/build.ps1` for the Windows installer with the gateway and engine. Use `desktop/publish.ps1` to create standalone clients for selected runtime identifiers: `win-x64`, `win-arm64`, `osx-arm64`, `osx-x64`, `linux-x64` and `linux-arm64`. Without `-Runtime` it builds `win-x64`, `osx-arm64`, `osx-x64` and `linux-x64`, and without `-OutputRoot` it writes to `outputs/native`. Both scripts use restored packages and an empty output directory. The macOS ZIP includes the application bundle. The Linux tarball includes a desktop entry and icon alongside the executable. Releases include the macOS and Linux clients for x64 and arm64.

[WINDOWS POWERSHELL]

```powershell
& .\desktop\publish.ps1 -Runtime linux-x64 -OutputRoot .\outputs\desktop-clients
```

Build the macOS bundles on a Mac, where the script signs them; Apple Silicon does not open unsigned programs. On Windows or Linux the script refuses macOS runtimes unless `-AllowUnsignedMac` is given, and each bundle built that way needs `codesign --force --deep --sign - EtherlightingEvolved.app` on a Mac before it opens.

Follow the [repository workflow](../docs/WORKFLOW.md) for branches, checks and releases.
