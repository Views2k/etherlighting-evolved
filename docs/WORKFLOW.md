# Development workflow

This document describes how changes reach `main` and how releases are built. EE has a single author; [CONTRIBUTING.md](../CONTRIBUTING.md) explains how others can report problems. [DEVELOPMENT.md](DEVELOPMENT.md) covers the source setup and the individual commands.

## Branches and pull requests

1. Create a topic branch from `main`, such as `fix/<topic>` or `feature/<topic>`. Keep unrelated changes on separate branches.
2. Make the change. Update `CHANGELOG.md` under `## Unreleased` for anything users notice, and `docs/API.md` when an API contract changes.
3. Run `tools/verify.ps1` (below) and the checks the change needs.
4. Open a pull request to `main` and fill in the template: the change, the checks and their results, and the effect on stored data and compatibility.
5. Merge after the EE Quality checks pass for the last commit.

Commits use the repository's configured author. EE Quality fails a pull request that contains commits by another author. Simulator checks do not replace a test on a physical switch; name the hardware checks that were run, or that are still needed, in the pull request.

## Local verification

With the dependencies from [DEVELOPMENT.md](DEVELOPMENT.md) installed and the desktop packages restored, run from the repository root:

[WINDOWS POWERSHELL]

```powershell
& .\tools\verify.ps1 -BrowserExe 'C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe'
```

The script uses `.venv\Scripts\python.exe` unless `-PythonExe` names another environment, and writes to `outputs\verification\<time>` unless `-OutputRoot` names an empty folder. It does not install anything or connect to a switch. It runs these steps in order and stops at the first failure:

| Step | Command |
| --- | --- |
| `repository` | `tools/check-repository.py`: source inventory, matching version numbers, and no private files, home folder paths, private network addresses, tailnet names or attribution text |
| `syntax` | `tools/check.mjs` |
| `javascript` | `node --test tests/*.test.mjs` |
| `lint` | `ruff check` |
| `python` | `pytest` |
| `contract` | `tests/backend_contract.py`, the browser and engine contract against an in-process simulator |
| `fixtures` | `tools/check-fixtures.py`: the generated fixtures and `src/catalog.mjs` match the current engine |
| `desktop-build`, `desktop` | Builds the desktop check program and runs its window and simulator checks |
| `browser` | `tools/smoke-workspace.mjs`: the browser workspace against the simulator |

The output folder holds a log for each step, `result.json` and `source-manifest.json`, which lists the hash of every source file.

## Pull request checks

The EE Quality workflow (`.github/workflows/quality.yml`) runs on every pull request to `main` and can be started by hand. Its actions are pinned to commit SHAs.

The `Verify EE` job runs on Windows. It checks the commit authors and messages, installs the hash-pinned Python packages, restores the locked desktop packages and runs `tools/verify.ps1`. It then builds the installer with `packaging/build.ps1` and the Inno Setup release that `tools/ci-inno.ps1` downloads and checks, runs the packaged simulator in a browser, and installs, checks and uninstalls the package. A run started by hand on `main` keeps the verified installer as a workflow artifact for 90 days.

The job runs fewer steps when the changed files allow it. A pull request that changes only Markdown files and the images in `docs/screenshots/` gets the author and message check and `tools/check-repository.py`, without the tests or the installer. `docs/API.md` is an exception, because a test reads it. The installer steps are skipped when every changed file is a test, documentation, an issue or pull request template, or `.github/dependabot.yml`. A run started by hand runs every step.

The `Native client (macos-15)` and `Native client (ubuntu-24.04)` jobs build the desktop check program and run it against the simulator, package the client for x64 and arm64, check both archives, and open the packaged client against a local simulator.

## Toolchain

| File | What it pins |
| --- | --- |
| `.python-version` | Python 3.14, used by CI and bundled in the Windows package |
| `.node-version` | The Node.js 24 release used by CI and bundled in the Windows package |
| `global.json` | The .NET 10 SDK (10.0.100 or a later feature band) |
| `requirements-ci.txt` | Python packages with the SHA-256 hashes of their wheels for the CI runners |
| `desktop/*/packages.lock.json` | The desktop NuGet packages, restored with `--locked-mode` |
| `tools/ci-inno.ps1` | Inno Setup 6.7.4 with its SHA-256 hash and publisher; `packaging/installer.iss` accepts any 6.7 release |

After changing a pin in `requirements-ci.txt`, run `python tools/hash-requirements.py` to write the new hashes from PyPI. The Windows package bundles the Python in `.python-version`. Move it to the next minor version when the current one stops receiving binary releases from python.org, then regenerate the hashes. The Windows package also bundles the Node.js in `.node-version`, so move that file to the newest Node.js 24 release after each Node.js security release.

Dependabot (`.github/dependabot.yml`) opens weekly update pull requests for the workflow actions, the Python packages and the NuGet packages. They fail the commit author check, so make each update again on your own branch. Dependabot does not update `.python-version`, `.node-version`, `global.json` or Inno Setup; check those by hand at each release.

`packaging/build.ps1` builds the installer from pinned tools only. Without `-AllowUnpinned`, it stops when the source tree has uncommitted changes, when `tools/check-toolchain.py` finds a Python, Node.js or bundled package version other than the pinned one, or when the compiler is not Inno Setup 6.7. `-AllowUnpinned` is for local test builds. Every build compares the package with the checkout and writes `package-verification.json`, which records the tree state and the Inno Setup release that compiled the installer.

## Releases

1. On a branch, run `python tools/set-version.py <major.minor.patch>`. It renames `## Unreleased` in `CHANGELOG.md` to the version and writes the version into `pyproject.toml`, `package.json`, `src/etherlighting_evolved/__init__.py`, the desktop project and `packaging/installer.iss`. Merge it through a pull request.
2. The EE Release workflow (`.github/workflows/release.yml`) runs on every push to `main`. When the application version has no `v<major.minor.patch>` tag yet, it tags the pushed commit, builds the installer and the macOS and Linux clients for x64 and arm64, writes `SHA256SUMS`, adds build provenance attestations and creates a draft GitHub Release with these files and `package-verification.json`. The release text is the version's section of `CHANGELOG.md`, followed by the commit it was built from and how to check a download. A version that already has its tag releases nothing, and an untagged version without a `CHANGELOG.md` section fails the run.
3. The workflow then downloads the files from the draft and checks them against `SHA256SUMS`, checks that `package-verification.json` passed for that version and commit, and verifies each file's attestation for that commit. Only then does it publish the release as the latest one.

These releases are attested from `refs/heads/main`, so check them with `--source-digest <commit>` rather than `--source-ref refs/tags/<tag>`. A version tag pushed by hand runs the same build but leaves the release as a draft to review and publish by hand.

The Windows installer has no Authenticode signature yet. The macOS clients have an ad hoc signature but no Developer ID signature or notarization. The [README](../README.md#verify-a-download) describes how to verify a download.

## Local output

`outputs/` is ignored by Git and holds local results: `outputs/verification/` for `tools/verify.ps1`, `outputs/releases/<version>/` for `packaging/build.ps1`, `outputs/native/` for `desktop/publish.ps1`, and `outputs/browser-preview-data/` for the simulator data of a source gateway. Credentials, application data, browser profiles, firmware images and diagnostics from a real switch stay out of the repository. `.gitignore` excludes the file names EE uses for them, and `tools/check-repository.py` checks that it does.
