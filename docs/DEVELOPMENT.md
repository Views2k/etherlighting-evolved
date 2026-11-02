# Development

Run commands from the repository root. The current application uses the browser workspace in `preview/`, the Python engine in `src/etherlighting_evolved/`, and the desktop host in `workspace_desktop.py`. Build and test paths resolve within this checkout.

Follow [WORKFLOW.md](WORKFLOW.md) for branches, review, CI, and delivery. The pinned Windows verification environment is recorded in `requirements-ci.txt`.

## Existing tools

- Node.js 20 or newer. The JavaScript project has no package dependencies.
- Python 3.11 or newer with the project dependencies in `pyproject.toml`.
- The `dev` Python extras for tests and linting.
- The `desktop` and `build` Python extras, plus Inno Setup 6, for Windows packaging.

Use an existing environment containing these dependencies. Build scripts check for tools and dependencies; they do not install them. The conventional local Python path is `.venv/Scripts/python.exe` on Windows or `.venv/bin/python` on other systems.

## Workspace

Start the isolated simulator:

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

The packaging entry point delegates to `tools/build-workspace.ps1`. Supply the existing Python environment and Node executable:

[WINDOWS POWERSHELL]

```powershell
& .\packaging\build.ps1 `
    -PythonExe (Resolve-Path .\.venv\Scripts\python.exe).Path `
    -NodeExe (Get-Command node).Source
```

The selected Node distribution must include its `LICENSE` beside `node.exe`; that notice is bundled with the application. Use `-NodeExe` to select a distribution containing both files.

Use `-CompilerExe` to select an existing Inno Setup compiler, `-OutputRoot` for a new output directory, or `-SkipInstaller` to package the application without compiling Setup. The default output is `outputs/releases/<version>/`; a nonempty output directory is rejected to protect existing artifacts.

The build packages the desktop host and engine, copies the current workspace assets and Node runtime, collects third-party notices, and runs `tools/verify-package.py`. It records verification results and hashes in the output directory. Version values in `package.json`, `pyproject.toml`, and the Python package must agree. It does not install or launch the result.

Before delivery, verify the package against this checkout, exercise the packaged simulator, and retain the installer hash. Installed upgrade and hardware acceptance are separate checks.

## Shared browser code and fixtures

The current UI lives in `preview/`. It imports shared effect and color modules from `src/*.mjs`. Change a shared module at its owner and test both the workspace and any retained consumer.

`src/catalog.mjs` is generated from backend metadata. Regenerate it when effect definitions change:

[WINDOWS POWERSHELL]

```powershell
& .\.venv\Scripts\python.exe tools/extract_catalog.py src/etherlighting_evolved/effects/library.py src/catalog.mjs
```

Keep deterministic JSON fixtures under `tests/fixtures/`. Their generators accept the engine checkout or source directory explicitly; use this checkout when updating them. Review fixture changes together with their assertions.

## Retained interfaces

The standalone frontend rooted at `index.html` and `src/app.mjs` is retained for existing integrations. See [web/README.md](../web/README.md) for its build. It is separate from the current packaged workspace.

`native/` contains the alternative C# WPF interface. It shares the Python service and has its own build and verification entry points. The current Windows installer is produced by `packaging/build.ps1`, not the WPF build. Changes to shared engine contracts need to preserve the retained clients or explicitly migrate them.
