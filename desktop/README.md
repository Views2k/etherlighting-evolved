# Etherlighting Evolved desktop

The desktop workspace uses Avalonia 11.3 and .NET 8. Lighting, Effects, Automation, Profiles, Settings and Help use native controls. The window exchanges scene changes and live port readings with the EE gateway.

## Run the app

The Windows installer includes the desktop app, gateway and lighting engine. Start Etherlighting Evolved from the Start menu, then connect to your switch or choose the demo. Closing the window hides it in the notification area. Use the tray menu to open it again or quit.

For macOS, extract the archive for your processor and open `EtherlightingEvolved.app`. On Linux, extract the archive into a folder and run `./EtherlightingEvolved` in that folder. Keep the application and its libraries together. These archives contain the desktop client and the .NET runtime. Start an EE workspace service on the same computer, or connect to an existing one using its private HTTPS workspace address. Choose **Change workspace address** on the connection error screen or in Settings to save the address. An explicit `--url` argument takes precedence over the saved address. The default local address is `http://127.0.0.1:4180/`.

The macOS packaging script produces an application bundle with the executable permission set. Signing and notarization are separate distribution steps.

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

Use the .NET 8 SDK and the locked packages for the two projects in this folder. Source mode uses the existing Node and Python environments described in the [development guide](../docs/DEVELOPMENT.md). Run the following from the repository root after dependency restore.

[WINDOWS POWERSHELL]

```powershell
dotnet build desktop/EtherlightingEvolved.Native.Check/EtherlightingEvolved.Native.Check.csproj -c Release --no-restore
dotnet desktop/EtherlightingEvolved.Native.Check/bin/Release/net8.0/EtherlightingEvolved.Native.Check.dll --service --output outputs/native-check
```

The check program renders windows off screen. Its service checks use a simulator with isolated data. To use the app interactively from the same build, launch the source app in demo mode.

[WINDOWS POWERSHELL]

```powershell
dotnet run --project desktop/EtherlightingEvolved.Native -c Release --no-build -- --demo
```

## Package

Use `packaging/build.ps1` for the Windows installer with the gateway and engine. Use `desktop/publish.ps1` to create standalone clients for selected runtime identifiers. Both scripts use restored packages and an empty output directory. The macOS ZIP includes the application bundle. The Linux tarball includes a desktop entry and icon alongside the executable.

[WINDOWS POWERSHELL]

```powershell
& .\desktop\publish.ps1 -Runtime osx-arm64,osx-x64,linux-x64 -OutputRoot .\outputs\desktop-clients
```

Follow the [repository workflow](../docs/WORKFLOW.md) for review, checks and release approval.
