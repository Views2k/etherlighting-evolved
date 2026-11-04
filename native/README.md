# Etherlighting Evolved WPF interface

This alternative Windows interface uses C# and WPF with the shared Python lighting
service. The current desktop release uses `packaging/build.ps1` from the repository
root. Both build paths read the engine and assets from this checkout.

`--demo` opens an isolated simulator. `--verify <directory>` exercises simulator
workflows and captures native renders, then exits. Normal launches reuse the
application data and encrypted login store.

## Build

Use an installed .NET 8 SDK with the 8.0.30 Windows runtime packs, an existing Python
build environment, and Inno Setup. The checked-in NuGet configuration has no remote
package sources. Build uses `--no-restore` and requires the project to be restored
first from existing local packs.

[WINDOWS POWERSHELL]

```powershell
dotnet restore native/EtherlightingEvolved.csproj --configfile native/NuGet.Config -r win-x64 -p:SelfContained=true
./native/build.ps1 -PythonExe .venv/Scripts/python.exe
```

The output goes to `outputs/native-release-<version>/`. Use `-OutputRoot` for a
separate build directory; an existing nonempty directory is rejected. `-SkipInstaller`
builds the application without requiring Inno Setup. The build does not install or
launch the application.

[WINDOWS POWERSHELL]

```powershell
.venv/Scripts/python.exe native/verify_package.py outputs/native-release-0.4.19
```

For source launches, the service is resolved from this checkout and Python is read
from its `.venv/Scripts/python.exe`. Packaged launches use `engine/EEEngine.exe`.
