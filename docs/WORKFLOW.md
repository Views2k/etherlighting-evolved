# Repository workflow

This repository is the source of truth for EE. All application changes, tests, assets, build scripts, and developer documentation belong here. Each update is committed and pushed before its installer is delivered.

## Change process

1. Start from the current `main` revision. Use a `codex/` work branch and an isolated worktree when another change is in progress.
2. Record the problem, intended behavior, affected files, and acceptance checks in the pull request. Keep unrelated work separate.
3. Implement the change in this checkout. Update the API and workflow documentation when behavior or contracts change.
4. Run the relevant checks locally. Run `tools/verify.ps1` for a complete source, backend, contract, and browser check. Reuse evidence only when its recorded source inputs are unchanged.
5. Push the branch and open a pull request. Review the diff and the EE Quality result for that revision before merging.
6. The `main` run verifies the merged revision, builds the Windows installer, compares its contents with the source, and exercises the packaged simulator. Deliver that verified artifact with its revision and SHA256.

Keep the configured repository author. Public releases and live deployments require a separate owner instruction. A successful simulator check establishes the tested software behavior; physical lighting and rack-screen acceptance are recorded separately.

## Verification

With the existing tools installed, run from the repository root:

[WINDOWS POWERSHELL]

```powershell
& .\tools\verify.ps1 -BrowserExe 'C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe'
```

The script uses `.venv/Scripts/python.exe` by default. Use `-PythonExe` for another existing environment and `-OutputRoot` for an empty results directory. It does not install dependencies or connect to a switch. It records an exact source manifest, step results, logs, browser captures, and simulator results.

EE Quality runs on pull requests and `main`. The GitHub-hosted Windows runner uses the versions in `requirements-ci.txt`, Python 3.12.10, and Node 22.23.1. Workflow actions are pinned to commit IDs. Browser checks use a separate simulator data directory and browser profile.

The `main` job prepares the official pinned Inno Setup compiler on the hosted runner, verifies its SHA256 and publisher signature, and runs `packaging/build.ps1`. Local builds use existing tools. Updating a tool or dependency requires updating its pin and checking the affected build and tests together.

## Artifacts and records

| Location | Contents |
| --- | --- |
| `preview/`, `src/`, `assets/` | Application source and artwork |
| `tests/` | Automated checks and deterministic fixtures |
| `packaging/`, `tools/` | Installer and verification entry points |
| `docs/` | API, hardware, development, and workflow documentation |
| `outputs/releases/` | Local installers and package provenance |
| `outputs/verification/` | Local verification results |
| `outputs/audits/` | Local engineering evidence and historical reports |
| GitHub Actions artifacts | Revision-specific verification and verified installers |

Generated output, credentials, live application state, browser profiles, firmware images, and private diagnostics stay outside version control. Preserve existing releases and data. Do not move saved logins or scenes merely because a checkout changes. Historical working copies are archives; current work uses this repository.

## Optional staging host

A staging VM runs the repository's simulator from a recorded revision, with its own data directory. It can provide a persistent review URL. Live switch access remains an explicit handoff to one EE instance. A staging environment does not receive saved hardware credentials by default.
