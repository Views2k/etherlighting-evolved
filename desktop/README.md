# Etherlighting Evolved desktop

The desktop workspace uses Avalonia 11.3 and .NET 10. Lighting, Effects, Automation, Profiles, Settings and Help use native controls. The window exchanges scene changes and live port readings with the EE gateway.

## Run the app

The Windows installer includes the desktop app, gateway and lighting engine. Start Etherlighting Evolved from the Start menu, then connect to your switch or choose the demo. Closing the window hides it in the notification area. Use the tray menu to open it again or quit.

For macOS, extract the archive for your processor and open `EtherlightingEvolved.app`. On Linux, extract the archive into a folder and run `./EtherlightingEvolved` in that folder. Keep the application and its libraries together. These archives contain the desktop client and the .NET runtime. Start an EE workspace service on the same computer, or connect to an existing one using its private HTTPS workspace address. Choose **Change workspace address** on the connection error screen or in Settings to save the address. An explicit `--url` argument takes precedence over the saved address, and the `EE_WORKSPACE_URL` environment variable sets the same address with lower precedence than `--url`. Each source accepts only the addresses the dialog accepts: an HTTPS origin, or an HTTP origin on this computer, without account details, a path or a query. A `--url` value that does not qualify opens the connection error screen with **Change workspace address**, and an environment value that does not qualify is ignored. The default local address is `http://127.0.0.1:4180/`.

On macOS, the packaging script signs the graphics libraries and completed application with an ad hoc signature and verifies the bundle before archiving it. A macOS host produces the signature-verified ZIPs used by CI. Developer ID signing and notarization are separate distribution steps.

### Desktop log

The app keeps `desktop.log` in its data folder: `%LOCALAPPDATA%\Etherlighting Evolved` on Windows, `~/Library/Application Support/Etherlighting Evolved` on macOS and `~/.config/etherlighting-evolved` on Linux. It records errors the window could not report in full and why the bundled service did not start or stopped. Each entry has the time, the app version, the error type and the code locations involved, or the service's exit code and its last 64 error output lines. The app does not record its own error messages, screen text or request contents. The service's output lines can include file paths and addresses; the user name and password in an address are removed. The file stays on the computer. When it passes 256 KB it is renamed to `desktop.log.1`, replacing the previous one.

### Connect a tagged Tailscale device

Devices signed in to the workspace owner’s Tailscale account can use the private workspace address directly. For a tagged device, enable **Use Tailscale** in **Mobile & remote access** on the Windows host. Open PowerShell as the user who runs EE and enter the device’s full Tailscale DNS name when prompted.

[WINDOWS POWERSHELL]

```powershell
$ee = Join-Path $env:LOCALAPPDATA 'Programs\Etherlighting Evolved'
$device = Read-Host 'Full Tailscale DNS name of the device to approve'
& "$ee\runtime\node.exe" "$ee\tools\approve-workspace-device.mjs" approve $device
```

On the approved device, choose **Change workspace address** and enter the private address shown by the Windows host. Repeat the approval command for each tagged device. The host can approve an offline device when Tailscale still confirms its identity and assigned addresses.

Use `status $device` to check that device, `status` to count saved approvals, or `revoke $device` to remove its access and keep the others. Use `revoke --all` to clear all device approvals.

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

Use the .NET 10 SDK and the locked packages for the two projects in this folder. Source mode uses the existing Node and Python environments described in the [development guide](../docs/DEVELOPMENT.md). Run the following from the repository root after dependency restore.

[WINDOWS POWERSHELL]

```powershell
dotnet build desktop/EtherlightingEvolved.Native.Check/EtherlightingEvolved.Native.Check.csproj -c Release --no-restore
dotnet desktop/EtherlightingEvolved.Native.Check/bin/Release/net10.0/EtherlightingEvolved.Native.Check.dll --service --output outputs/native-check
```

The check program renders windows off screen. Its service checks use a simulator with isolated data. To use the app interactively from the same build, launch the source app in demo mode. A demo copy can run beside the installed app; it keeps its workspace data and its preferences, such as the theme and saved palettes, in the `demo` folder of the app's data folder.

[WINDOWS POWERSHELL]

```powershell
dotnet run --project desktop/EtherlightingEvolved.Native -c Release --no-build -- --demo
```

## Package

Use `packaging/build.ps1` for the Windows installer with the gateway and engine. Use `desktop/publish.ps1` to create standalone clients for selected runtime identifiers. Both scripts use restored packages and an empty output directory. The macOS ZIP includes the application bundle. The Linux tarball includes a desktop entry and icon alongside the executable.

[WINDOWS POWERSHELL]

```powershell
& .\desktop\publish.ps1 -Runtime linux-x64 -OutputRoot .\outputs\desktop-clients
```

Build the macOS bundles on a Mac, where the script signs them; Apple Silicon does not open unsigned programs. On Windows or Linux the script refuses macOS runtimes unless `-AllowUnsignedMac` is given, and each bundle built that way needs `codesign --force --deep --sign - EtherlightingEvolved.app` on a Mac before it opens.

Follow the [repository workflow](../docs/WORKFLOW.md) for review, checks and release approval.
