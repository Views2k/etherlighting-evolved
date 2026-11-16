# Development

Run commands from the repository root. The desktop workspace is in `desktop/EtherlightingEvolved.Native/`, the gateway and browser workspace are in `preview/`, and the Python lighting engine is in `src/etherlighting_evolved/`. The native and browser clients use the same workspace API.

Follow [WORKFLOW.md](WORKFLOW.md) for branches, review, CI, and delivery. The pinned Windows verification environment is recorded in `requirements-ci.txt`.

## Existing tools

- Node.js 20 or newer. The JavaScript project has no package dependencies.
- Python 3.11 or newer with the project dependencies in `pyproject.toml`.
- The `dev` Python extras for tests and linting.
- The .NET 8 SDK and the packages pinned by the desktop projects’ lock files. EE Quality restores the check project with `dotnet restore desktop/EtherlightingEvolved.Native.Check/EtherlightingEvolved.Native.Check.csproj --locked-mode`.
- The packaging dependencies in `requirements-ci.txt`, including PyInstaller and Pillow, and Inno Setup 6 for Windows Setup.

Use an existing environment containing these dependencies. Build scripts check for tools and dependencies; they do not install them. The conventional local Python path is `.venv/Scripts/python.exe` on Windows or `.venv/bin/python` on other systems.

## Workspace

For the native desktop app, build the native check project, then start the application with `--demo` to open an isolated simulator. For a separate source gateway and its browser workspace, use the repository’s start script. See the [desktop guide](../desktop/README.md#build-and-test) for native commands.

[WINDOWS POWERSHELL]

```powershell
$env:EE_PREVIEW_LIVE = '0'
npm start
```

Open [the workspace](http://127.0.0.1:4180). The gateway starts its own Python service, exchanges its session token through the child process's private input, and forwards API and WebSocket requests. The service port is assigned at startup.

To use another existing Python environment, set `EE_PREVIEW_PYTHON` to its absolute executable path before starting. The runtime also searches the checkout's `.venv` and the existing `PATH`.

For a live workspace, set `EE_PREVIEW_LIVE` to `1` and start the same command. Enter credentials through the login screen. Close another live EE instance first; the live workspace uses the application instance lock. Source startup does not automatically log in to the switch.

| Setting | Purpose |
| --- | --- |
| `EE_PREVIEW_LIVE` | `0` for simulator, `1` for live login; source mode defaults to simulator |
| `EE_PREVIEW_PORT` | Local workspace port; defaults to `4180` |
| `EE_PREVIEW_PYTHON` | Absolute path to an existing Python executable |
| `EE_WORKSPACE_DATA_DIR` | Absolute directory for gateway settings and simulator data |
| `EE_ENGINE_SOURCE_ROOT` | Explicit alternate engine checkout for integration work |

Keep credentials out of command arguments, environment examples, and committed configuration. The workspace login forms provide the credential path.

## Data and generated files

Source workspace data defaults to `outputs/browser-preview-data/`. Simulator data is isolated there. Live engine settings, profiles, host keys, and encrypted logins use the existing engine data directory, normally `~/.etherlighting-evolved/`.

The packaged Windows gateway stores its workspace settings under `%LOCALAPPDATA%/Etherlighting Evolved/workspace/`. Its live engine uses the same engine data directory as source mode. Changing the checkout or rebuilding an installer does not require copying or deleting either data directory.

`outputs/`, `dist/`, build caches, virtual environments, runtime configuration, and local working notes are excluded from version control. Preserve previously delivered installers when choosing a new output directory. Firmware images, extracted vendor files, private diagnostics, and saved credentials do not belong in the source repository.

## Tests

Run the JavaScript suite and the Python suite from the same checkout:

[WINDOWS POWERSHELL]

```powershell
npm test
& .\.venv\Scripts\python.exe -m pytest
```

The JavaScript command includes `tests/*.test.mjs` and `preview/*.test.mjs`. Python uses the `tests/` directory and `src/` import path configured in `pyproject.toml`.

For the browser/backend contract harness:

[WINDOWS POWERSHELL]

```powershell
& .\.venv\Scripts\python.exe tests/backend_contract.py .
```

The harness uses an in-process simulator with temporary data. Keep simulator and fixture results distinct from physical switch testing. Device output, cleanup, and visual acceptance require the qualified model and firmware documented in [HARDWARE.md](HARDWARE.md).

Run checks affected by a change and retain their command, source revision, result, and evidence location. A previously passing installer remains valid evidence for unchanged inputs; source changes need the relevant checks before the next package is delivered.

## Windows installer

Build the private Windows installer with the existing local tools.

[WINDOWS POWERSHELL]

```powershell
& .\packaging\build.ps1
```

Open the printed path to `EtherlightingEvolved-Setup-<version>.exe`. The default destination is `outputs/releases/<version>/dist/`. Use a new `-OutputRoot` for a second build of the same version to preserve the earlier installer.

The build publishes the native `win-x64` app, packages the Python engine and adds the gateway, browser workspace, Node runtime and component licenses. Restore the locked desktop project before packaging. The build uses the checkout's Python environment and the Node distribution under `outputs/build-tools/node/`, falling back to Node on the system path. Use `-PythonExe`, `-NodeExe`, or `-CompilerExe` to select an existing tool. Node needs its license beside the executable. Use `-SkipInstaller` to produce the app folder alone.

Standalone macOS and Linux desktop clients connect to an existing EE workspace. See the [archive build and launch instructions](../desktop/README.md).

Private builds check required application files, third-party notices, and exclusion of private data before compiling Setup. For a release candidate, run EE Quality manually on `main`. It passes `-ReleaseCandidate` for the full package comparison and then exercises the packaged simulator.

## Shared browser code and fixtures

The browser workspace in `preview/` imports shared effect and color modules from `src/*.mjs`. The native client uses their C# counterparts in `desktop/EtherlightingEvolved.Native/Core/Model/`. For a changed scene, lighting or scheduling contract, verify the native and browser workflows against the same engine and fixtures.

`src/catalog.mjs` is generated from backend metadata. Regenerate it when effect definitions change:

[WINDOWS POWERSHELL]

```powershell
& .\.venv\Scripts\python.exe tools/extract_catalog.py src/etherlighting_evolved/effects/library.py src/catalog.mjs
```

Keep deterministic JSON fixtures under `tests/fixtures/`. Their generators accept the engine checkout or source directory explicitly; use this checkout when updating them. Review fixture changes together with their assertions.
