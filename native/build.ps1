param(
    [string]$PythonExe,
    [string]$CompilerExe,
    [string]$OutputRoot,
    [switch]$SkipInstaller
)
$ErrorActionPreference = 'Stop'
$workspace = Split-Path $PSScriptRoot -Parent
$backend = $workspace
if (-not $PythonExe) { $PythonExe = Join-Path $workspace '.venv\Scripts\python.exe' }
$PythonExe = [IO.Path]::GetFullPath($PythonExe)
if (-not (Test-Path -LiteralPath $PythonExe -PathType Leaf)) { throw 'An existing Python build environment is required.' }
$version = & $PythonExe -c "import runpy,sys; print(runpy.run_path(sys.argv[1])['__version__'])" (Join-Path $backend 'src\etherlighting_evolved\__init__.py')
if ($LASTEXITCODE -or $version -notmatch '^\d+\.\d+\.\d+$') { throw 'The application version could not be read.' }
if (-not $OutputRoot) { $OutputRoot = Join-Path $workspace "outputs\native-release-$version" }
$release = [IO.Path]::GetFullPath($OutputRoot)
if (Test-Path -LiteralPath $release) {
    if (-not (Test-Path -LiteralPath $release -PathType Container) -or
        @(Get-ChildItem -LiteralPath $release -Force).Count) {
        throw 'The output directory is not empty. Choose a new -OutputRoot to preserve previous builds.'
    }
}
if (-not $SkipInstaller) {
    if (-not $CompilerExe) { $CompilerExe = Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe' }
    $compiler = [IO.Path]::GetFullPath($CompilerExe)
    if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) { throw 'An existing Inno Setup compiler is required.' }
}
if (-not (Get-Command dotnet -CommandType Application -ErrorAction SilentlyContinue)) { throw 'An existing .NET SDK is required.' }
& $PythonExe -c "import PyInstaller, fastapi, uvicorn, asyncssh, PIL"
if ($LASTEXITCODE) { throw 'A build dependency is missing. No dependencies were downloaded.' }
if (-not (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'obj\project.assets.json') -PathType Leaf)) {
    throw 'Restore the native project with native/NuGet.Config and the installed runtime packs before building.'
}
$destination = Join-Path $release 'dist\EtherlightingEvolved'
$env:DOTNET_CLI_HOME = Join-Path $workspace 'outputs\dotnet-home'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
if (-not $env:NUGET_PACKAGES) { $env:NUGET_PACKAGES = Join-Path ([Environment]::GetFolderPath('UserProfile')) '.nuget\packages' }
New-Item -ItemType Directory -Path $release -Force | Out-Null
& $PythonExe (Join-Path $backend 'packaging\make_icons.py')
if ($LASTEXITCODE) { throw 'Brand assets could not be prepared.' }
& dotnet publish (Join-Path $PSScriptRoot 'EtherlightingEvolved.csproj') -c Release -r win-x64 --self-contained true --no-restore -o $destination "-p:Version=$version" -p:DebugType=None -p:DebugSymbols=false --nologo
if ($LASTEXITCODE) { throw 'Native build failed.' }
& $PythonExe -m PyInstaller --noconfirm --distpath (Join-Path $release 'engine-dist') --workpath (Join-Path $release 'engine-build') (Join-Path $backend 'packaging\native-service.spec')
if ($LASTEXITCODE) { throw 'Service build failed.' }
New-Item -ItemType Directory -Path (Join-Path $destination 'engine') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $release 'engine-dist\EEEngine\EEEngine.exe') -Destination (Join-Path $destination 'engine\EEEngine.exe') -Force
Copy-Item -LiteralPath (Join-Path $release 'engine-dist\EEEngine\_internal') -Destination (Join-Path $destination 'engine') -Recurse -Force
Copy-Item -LiteralPath (Join-Path $backend 'LICENSE') -Destination (Join-Path $destination 'LICENSE.txt') -Force
$licenses = Join-Path $destination 'licenses'
New-Item -ItemType Directory -Path $licenses -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $env:NUGET_PACKAGES 'microsoft.netcore.app.runtime.win-x64\8.0.30\LICENSE.TXT') -Destination (Join-Path $licenses 'dotnet-LICENSE.txt') -Force
Copy-Item -LiteralPath (Join-Path $env:NUGET_PACKAGES 'microsoft.netcore.app.runtime.win-x64\8.0.30\THIRD-PARTY-NOTICES.TXT') -Destination (Join-Path $licenses 'dotnet-NOTICES.txt') -Force
Copy-Item -LiteralPath (Join-Path $env:NUGET_PACKAGES 'microsoft.windowsdesktop.app.runtime.win-x64\8.0.30\LICENSE') -Destination (Join-Path $licenses 'desktop-LICENSE.txt') -Force
if (-not $SkipInstaller) {
    & $compiler /Q "/DAppVersion=$version" "/DAppSourceDir=$destination" "/O$(Join-Path $release 'dist')" (Join-Path $backend 'packaging\installer.iss')
    if ($LASTEXITCODE) { throw 'Installer build failed.' }
    Write-Output (Join-Path $release "dist\EtherlightingEvolved-Setup-$version.exe")
} else { Write-Output (Join-Path $destination 'EtherlightingEvolved.exe') }
