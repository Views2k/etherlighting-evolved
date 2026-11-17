# Development

Run commands from the repository root. The desktop workspace is in `desktop/EtherlightingEvolved.Native/`, the gateway and browser workspace are in `preview/`, and the Python lighting engine is in `src/etherlighting_evolved/`. The native and browser clients use the same workspace API.

Follow [WORKFLOW.md](WORKFLOW.md) for branches, pull request checks and releases. The CI toolchain is pinned in `.python-version`, `.node-version`, `global.json` and `requirements-ci.txt`.

## Existing tools

- Node.js 22 or newer to run from source. Builds and CI use the version in `.node-version` (24.18.0). The JavaScript project has no package dependencies.
- Python 3.14 (`.python-version`) with the project dependencies in `pyproject.toml`.
- The `dev` Python extras for tests and linting.
- The .NET 10 SDK (`global.json`) and the packages pinned by the desktop projects’ lock files. EE Quality restores the check project with `dotnet restore desktop/EtherlightingEvolved.Native.Check/EtherlightingEvolved.Native.Check.csproj --locked-mode`.
- The packaging dependencies in `requirements-ci.txt`, including PyInstaller and Pillow, and Inno Setup 6.7 for Windows Setup.

Build scripts check for tools and dependencies; they do not install them. Create the Python environment once from the repository root:

[WINDOWS POWERSHELL]

```powershell
python -m venv .venv
.\.venv\Scripts\python.exe -m pip install --require-hashes --only-binary=:all: -r requirements-ci.txt
.\.venv\Scripts\python.exe -m pip install --no-deps --no-build-isolation -e .
```

`requirements-ci.txt` pins every package CI installs, with the hashes of the wheels for Windows x64, macOS arm64 and Linux x64. On macOS arm64 and Linux x64, run the same commands with `.venv/bin/python`. On other systems, install the project with its `dev` extras instead: `.venv/bin/python -m pip install -e ".[dev]"`. The gateway looks for `.venv/Scripts/python.exe` on Windows and `.venv/bin/python` on other systems.

## Workspace

For the native desktop app, build the native check project, then start the application with `--demo` to open an isolated simulator. For a separate source gateway and its browser workspace, use the repository’s start script. See the [desktop guide](../desktop/README.md#build-and-test) for native commands.

[WINDOWS POWERSHELL]

```powershell
$env:EE_PREVIEW_LIVE = '0'
npm start
```

Open [the workspace](http://127.0.0.1:4180). The gateway starts its own Python service, exchanges its session token through the child process's private input, and forwards API and WebSocket requests. The service port is assigned at startup. The gateway also prints an administration link that works once. A browser that opens it can change the Tailscale and LAN access settings and close the web session until the browser session ends or `npm start` stops; other tabs show those settings read-only.

To open another administration link, start the gateway with your own key and request a link with it from a second PowerShell window that has the same `$env:EE_ADMIN_KEY`:

[WINDOWS POWERSHELL]

```powershell
$bytes = New-Object byte[] 32
[Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
$env:EE_ADMIN_KEY = -join ($bytes | ForEach-Object { $_.ToString('x2') })
npm start
```

[WINDOWS POWERSHELL]

```powershell
$code = (Invoke-RestMethod -Method Post -Uri http://127.0.0.1:4180/api/workspace/admin-link -Headers @{ 'X-EE-Admin-Key' = $env:EE_ADMIN_KEY }).code
Start-Process "http://127.0.0.1:4180/?admin_code=$code"
```

To use another existing Python environment, set `EE_PREVIEW_PYTHON` to its absolute executable path before starting. The runtime also searches the checkout's `.venv` and the existing `PATH`. The desktop app, run from a source checkout, passes `EE_PREVIEW_PYTHON` to the gateway it starts; a packaged app ignores it.

For a live workspace, set `EE_PREVIEW_LIVE` to `1` and start the same command. Enter credentials through the login screen. Close another live EE instance first; the live workspace takes the Windows application instance lock, so live mode runs on Windows only. Source startup does not automatically log in to the switch.

| Setting | Purpose |
| --- | --- |
| `EE_PREVIEW_LIVE` | `0` for simulator, `1` for live login; source mode defaults to simulator |
| `EE_PREVIEW_PORT` | Local workspace port; defaults to `4180` |
| `EE_PREVIEW_PYTHON` | Absolute path to an existing Python executable; also read by the desktop app run from source |
| `EE_WORKSPACE_DATA_DIR` | Absolute directory for gateway settings and simulator data |
| `EE_ENGINE_SOURCE_ROOT` | Explicit alternate engine checkout for integration work |
| `EE_ADMIN_KEY` | Optional 64-character lowercase hexadecimal administration key; generated at each start when absent; never passed to the engine or to Tailscale |

Keep credentials out of command arguments, environment examples, and committed configuration. The workspace login forms provide the credential path.

## Data and generated files

Source workspace data defaults to `outputs/browser-preview-data/`. Simulator data is isolated there. Live engine settings, profiles, saved SSH host keys (`known_switches.json`), the fan mode saved for each switch (`device-fans/`) and encrypted logins use the existing engine data directory, normally `~/.etherlighting-evolved/`. `EE_DATA_DIR` moves that directory for `etherlighting-evolved serve` and `eectl`; the gateway removes it from the engine's environment.

The packaged Windows gateway stores its workspace settings under `%LOCALAPPDATA%/Etherlighting Evolved/workspace/`: `private-access.json`, `approved-device.json`, `lan-access.json`, `last-session.json` and `service-diagnostics.log`. A source gateway keeps the same files in its workspace data directory. Its live engine uses the same engine data directory as source mode. Changing the checkout or rebuilding an installer does not require copying or deleting either data directory.

`outputs/`, `dist/`, build caches, virtual environments, runtime configuration, and local working notes are excluded from version control. Firmware images, extracted vendor files, private diagnostics, and saved credentials do not belong in the source repository.

## Tests

Run the JavaScript suite and the Python suite from the same checkout:

[WINDOWS POWERSHELL]

```powershell
npm test
& .\.venv\Scripts\python.exe -m pytest
```

The JavaScript command runs `tests/*.test.mjs`. Python uses the `tests/` directory and `src/` import path configured in `pyproject.toml`.

For the browser/backend contract harness:

[WINDOWS POWERSHELL]

```powershell
& .\.venv\Scripts\python.exe tests/backend_contract.py .
```

The harness uses an in-process simulator with temporary data. Keep simulator and fixture results distinct from physical switch testing. Device output, cleanup, and visual acceptance require the qualified model and firmware documented in [HARDWARE.md](HARDWARE.md).

Run the checks that a change affects before you build a new installer from it. `tools/verify.ps1` runs the local checks together (see [WORKFLOW.md](WORKFLOW.md#local-verification)).

## Command line

Installing the project into the environment adds the commands `etherlighting-evolved` and `eectl`. Both run the same program:

| Subcommand | Purpose |
| --- | --- |
| `serve` | Runs the API server on `127.0.0.1:8787`; the default when no subcommand is given. `--demo` logs in to the simulator. The page at `/` links to the API documentation at `/docs`. |
| `probe <host> -u <user>` | Reads the switch's model, ports and lighting interface and reports whether EE allows output on it. It does not change the switch. Like the app, the first connection saves the switch's SSH key. `--accept-host-key <fingerprint>` accepts the changed key that the previous run printed, unless switch playback is saved for that switch. |
| `set <host> -u <user> <port> <color>` | Sets one port's color once. |
| `benchmark <host> -u <user>` | Measures LED command timing with paced writes to one port. |
| `effects` | Lists the effects and their parameters. |
| `init-config [path]` | Writes an example `ee.toml`. |
| `forget-host <host>` | Removes the saved SSH host key of a switch; the next connection saves the key it presents. |

`set` and `benchmark` write to the switch and apply the same model and firmware check as the app. `serve`, `set` and `benchmark` take the application instance lock, so they run on Windows only and not while EE runs. The switch commands ask for the SSH password unless `EE_PASSWORD` or a key file (`-i`) provides it. The `-p` option also accepts it, but leaves it in the shell history.

## Windows installer

Build the Windows installer from a committed checkout with the pinned tools.

[WINDOWS POWERSHELL]

```powershell
& .\packaging\build.ps1
```

Open the printed path to `EtherlightingEvolved-Setup-<version>.exe`. The default destination is `outputs/releases/<version>/dist/`. Use a new `-OutputRoot` for a second build of the same version to preserve the earlier installer.

The build publishes the native `win-x64` app, packages the Python engine and adds the gateway, browser workspace, Node runtime and component licenses. Restore the locked desktop project before packaging. The build uses the checkout's Python environment and the Node distribution under `outputs/build-tools/node/`, falling back to Node on the system path. Use `-PythonExe`, `-NodeExe`, or `-CompilerExe` to select an existing tool. Node needs its license beside the executable. Use `-SkipInstaller` to produce the app folder alone.

Standalone macOS and Linux desktop clients connect to an existing EE workspace. See the [archive build and launch instructions](../desktop/README.md).

Without `-AllowUnpinned`, the build stops when the source tree has uncommitted changes, when `tools/check-toolchain.py` finds a Python other than `.python-version`, a Node.js other than `.node-version`, or a package that the engine bundles or is built with at a version other than its `requirements-ci.txt` pin, or when the compiler is not Inno Setup 6.7. Test and lint tools may differ. `-AllowUnpinned` accepts uncommitted changes and other tool versions for a local test build only.

Every build compares the package with the checkout (`tools/verify-package.py`): required application files, third-party notices and the exclusion of private data. It writes `package-verification.json` next to `dist/`, with the source revision, the tree state and, when Setup is built, the Inno Setup release that compiled it. CI then exercises the packaged simulator and installs and uninstalls the package; see [WORKFLOW.md](WORKFLOW.md#pull-request-checks).

## Shared browser code and fixtures

The browser workspace in `preview/` imports shared effect and color modules from `src/*.mjs`. The native client uses their C# counterparts in `desktop/EtherlightingEvolved.Native/Core/Model/`. For a changed scene, lighting or scheduling contract, verify the native and browser workflows against the same engine and fixtures.

`src/catalog.mjs` is generated from backend metadata. Regenerate it when effect definitions change:

[WINDOWS POWERSHELL]

```powershell
& .\.venv\Scripts\python.exe tools/extract_catalog.py src/etherlighting_evolved/effects/library.py src/catalog.mjs
```

Keep deterministic JSON fixtures under `tests/fixtures/`. Their generators accept the engine checkout or source directory explicitly; use this checkout when updating them. Review fixture changes together with their assertions.

[WINDOWS POWERSHELL]

```powershell
& .\.venv\Scripts\python.exe tools/preview_fixtures.py . tests/fixtures
& .\.venv\Scripts\python.exe tools/live_traffic_fixtures.py . tests/fixtures
& .\.venv\Scripts\python.exe tools/profile_parity_fixtures.py . tests/fixtures
```

`tools/check-fixtures.py`, the fixtures step of `tools/verify.ps1`, runs these generators and the catalog command above in a temporary folder and fails when a result differs from the checked-in file. Rerun them after changing effect metadata, rendered frames or profile colors. The checked-in files are produced on Windows; rounding can differ on other platforms. `tests/fixtures/fixed_effects.json` and `pre_color_brightness.json` record earlier engine output and have no generator.

After changing `assets/logo.png`, run `python packaging/make_icons.py --icons` and commit the regenerated icons. Builds run the script without arguments, which writes only the installer images that Git ignores.
