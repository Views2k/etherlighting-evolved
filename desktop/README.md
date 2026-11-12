# Etherlighting Evolved native window

A native desktop window for EE built with [Avalonia](https://avaloniaui.net/) 11.3 on .NET 8. It runs on Windows, macOS and Linux and draws with Skia on the GPU. It follows a running EE workspace and shows the live port faceplate.

## Status

This is the first part of the native client. It shows the switch name, the connection state, and every Ethernet port and SFP cage with its live lighting color, device name, link speed and PoE draw. Lighting is changed through the full EE controls, which the window opens in the default browser.

The window only reads from EE. It sends no commands, so it cannot change the switch.

## How it keeps CPU use low

- The port map redraws only when a port's color or text changes. Steady lighting causes no redraws.
- Stream messages are applied at most once per UI turn; older ones are dropped.
- A minimized window disconnects from the live stream and reconnects with a fresh state when restored.
- There are no timers or animation loops.

Measured on a Windows PC while following a workspace for 40 seconds, including a software render of every change for the measurement:

| Source | Frames received | Redraws | Process CPU | Memory |
| --- | --- | --- | --- | --- |
| Real switch, steady lighting | 1.5 per second | 53 | 3.5% of one core | 34 MB |
| Simulator, Rainbow on every port | 3.9 per second | 147 | 4.5% of one core | 36 MB |

## Requirements

- An EE workspace running on the same computer, at `http://127.0.0.1:4180/` by default. Set another address with `--url` or the `EE_WORKSPACE_URL` environment variable.
- The .NET 8 SDK to build. The published builds include the .NET runtime and need nothing installed.

## Build

Run from the repository root. `desktop/NuGet.Config` restores from nuget.org into `outputs/nuget-packages`, which is ignored by Git. The package lock files pin every version.

[WINDOWS POWERSHELL]

```powershell
$env:AVALONIA_TELEMETRY_OPTOUT = '1'
dotnet restore desktop/EtherlightingEvolved.Native.Check/EtherlightingEvolved.Native.Check.csproj --configfile desktop/NuGet.Config -p:SelfContained=true
dotnet build desktop/EtherlightingEvolved.Native.Check/EtherlightingEvolved.Native.Check.csproj -c Release --no-restore
```

Avalonia's build tools report build statistics to Avalonia by default. `desktop/Directory.Build.targets` replaces that step with an empty one, and the environment variable above disables it as well.

## Check

The check program runs without opening a window. It reads synthetic payloads, confirms that unchanged frames cause no redraw, and renders 8 + 2, 48 + 4 and 0 + 32 port layouts to PNG files, comparing drawn colors with the reported ones.

[WINDOWS POWERSHELL]

```powershell
desktop/EtherlightingEvolved.Native.Check/bin/Release/net8.0/EtherlightingEvolved.Native.Check.exe --output outputs/native-check
```

Add `--live http://127.0.0.1:4180 --seconds 40` to follow a running workspace, read only, and report the frame rate, redraws, CPU and memory of the check process.

## Publish

Each command produces a standalone build in `outputs/native/<runtime>`.

[WINDOWS POWERSHELL]

```powershell
foreach ($rid in 'win-x64', 'osx-arm64', 'osx-x64', 'linux-x64') {
  dotnet publish desktop/EtherlightingEvolved.Native/EtherlightingEvolved.Native.csproj -c Release -r $rid --self-contained --no-restore -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o outputs/native/$rid
}
```

The Windows and Linux builds are one executable file. The macOS builds are an executable with three Avalonia and Skia libraries beside it. The macOS and Linux builds have not been started on those systems yet. macOS builds are not signed by a developer certificate, so the first launch needs Open from the Finder context menu.

## Not yet included

- Editing lighting, effects, automation, profiles and settings in the native window.
- A notification area icon and start with the computer.
- A macOS app bundle and Linux packages.
- Trimming the published builds, which are 85 to 98 MB.
