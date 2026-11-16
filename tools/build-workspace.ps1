param(
    [Parameter(Mandatory=$true)][string]$PythonExe,
    [Parameter(Mandatory=$true)][string]$NodeExe,
    [string]$CompilerExe,
    [string]$DotnetExe = 'dotnet',
    [string]$OutputRoot,
    [switch]$SkipInstaller,
    [switch]$AllowUnpinned
)
$ErrorActionPreference = 'Stop'
$workspace = Split-Path $PSScriptRoot -Parent
$backend = $workspace
$PythonExe = [IO.Path]::GetFullPath($PythonExe)
$node = [IO.Path]::GetFullPath($NodeExe)
$nodeLicense = Join-Path (Split-Path $node -Parent) 'LICENSE'
foreach ($required in @($PythonExe,$node,$nodeLicense)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw 'An existing build tool or its license is missing.' }
}
if (-not $SkipInstaller) {
    if (-not $CompilerExe) { $CompilerExe = Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe' }
    $compiler = [IO.Path]::GetFullPath($CompilerExe)
    if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) { throw 'An existing Inno Setup compiler is required.' }
}
& $PythonExe -c "import PyInstaller, fastapi, uvicorn, asyncssh, PIL"
if ($LASTEXITCODE) { throw 'A build dependency is missing. No dependencies were downloaded.' }
# A package is reproducible only from a committed tree and the tool versions CI uses. -AllowUnpinned accepts other
# tools and uncommitted changes for a local test build; package-verification.json still records the tree state.
if (-not $AllowUnpinned) {
    $changes = & git -C $workspace status --porcelain --untracked-files=all
    if ($LASTEXITCODE) { throw 'The source tree state could not be read.' }
    if ($changes) { throw 'Commit or remove the changes in the source tree, or pass -AllowUnpinned for a local test build.' }
    & $PythonExe (Join-Path $PSScriptRoot 'check-toolchain.py') $node
    if ($LASTEXITCODE) { throw 'Use the pinned build tools, or pass -AllowUnpinned for a local test build.' }
}
$version = & $PythonExe -c "import pathlib,runpy,sys; print(runpy.run_path(sys.argv[1])['check_versions'](pathlib.Path(sys.argv[2])))" (Join-Path $PSScriptRoot 'check-repository.py') $workspace
if ($LASTEXITCODE -or $version -notmatch '^\d+\.\d+\.\d+$') { throw 'The application version values could not be read or do not agree.' }
if (-not $OutputRoot) { $OutputRoot = Join-Path $workspace "outputs\releases\$version" }
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
if (Test-Path -LiteralPath $OutputRoot) {
    if (-not (Test-Path -LiteralPath $OutputRoot -PathType Container) -or
        @(Get-ChildItem -LiteralPath $OutputRoot -Force).Count) {
        throw 'The output directory is not empty. Choose a new -OutputRoot to preserve previous builds.'
    }
}
New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null
& $PythonExe (Join-Path $backend 'packaging\make_icons.py') *> (Join-Path $OutputRoot 'brand-assets.log')
if ($LASTEXITCODE) { throw 'Brand asset generation failed. See brand-assets.log.' }
$dist = Join-Path $OutputRoot 'dist'
$app = Join-Path $dist 'EtherlightingEvolved'
# The desktop program is the native window (desktop/EtherlightingEvolved.Native). Its packages are restored ahead
# of time from the lock file; the build itself never downloads anything.
$native = Join-Path $backend 'desktop\EtherlightingEvolved.Native\EtherlightingEvolved.Native.csproj'
if (-not (Test-Path -LiteralPath (Join-Path (Split-Path $native -Parent) 'obj\project.assets.json') -PathType Leaf)) {
    throw 'Restore the desktop packages first (see desktop/README.md). No packages were downloaded.'
}
$env:AVALONIA_TELEMETRY_OPTOUT = '1'
& $DotnetExe publish $native -c Release -r win-x64 --no-restore -o $app *> (Join-Path $OutputRoot 'desktop-build.log')
if ($LASTEXITCODE) { throw 'Desktop build failed. See desktop-build.log.' }
& $PythonExe -m PyInstaller --noconfirm --distpath (Join-Path $OutputRoot 'engine-dist') --workpath (Join-Path $OutputRoot 'engine-build') (Join-Path $backend 'packaging\native-service.spec') *> (Join-Path $OutputRoot 'engine-build.log')
if ($LASTEXITCODE) { throw 'Engine packaging failed. See engine-build.log.' }
New-Item -ItemType Directory -Path (Join-Path $app 'engine') -Force | Out-Null
Get-ChildItem -LiteralPath (Join-Path $OutputRoot 'engine-dist\EEEngine') | Copy-Item -Destination (Join-Path $app 'engine') -Recurse -Force
foreach ($folder in @('preview','src','assets')) {
    New-Item -ItemType Directory -Path (Join-Path $app $folder) -Force | Out-Null
    Get-ChildItem -LiteralPath (Join-Path $workspace $folder) -File | Where-Object { $_.Extension -in @('.mjs','.css','.html','.png','.svg','.ico','.webmanifest') -and $_.Name -notlike '*.test.mjs' } | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $app $folder)
    }
}
New-Item -ItemType Directory -Path (Join-Path $app 'runtime'),(Join-Path $app 'licenses'),(Join-Path $app 'tools') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'approve-workspace-device.mjs') -Destination (Join-Path $app 'tools')
Copy-Item -LiteralPath $node -Destination (Join-Path $app 'runtime\node.exe')
Copy-Item -LiteralPath (Join-Path $backend 'LICENSE') -Destination (Join-Path $app 'LICENSE.txt')
Copy-Item -LiteralPath $nodeLicense -Destination (Join-Path $app 'licenses\Node-LICENSE.txt')
& $PythonExe (Join-Path $PSScriptRoot 'collect-package-notices.py') $app (Join-Path (Split-Path $native -Parent) 'obj\project.assets.json')
if ($LASTEXITCODE) { throw 'Dependency notice collection failed.' }
& $node -p 'process.versions.node' | Set-Content -LiteralPath (Join-Path $OutputRoot 'node-version.txt')
if ($LASTEXITCODE) { throw 'The Node.js version could not be read.' }
if (-not $SkipInstaller) {
    $compilerOptions = @('/Q', "/DAppVersion=$version", "/DAppSourceDir=$app", "/O$dist")
    if ($AllowUnpinned) { $compilerOptions += '/DAnyCompiler' }
    & $compiler @compilerOptions (Join-Path $backend 'packaging\installer.iss') *> (Join-Path $OutputRoot 'installer-build.log')
    if ($LASTEXITCODE) { throw 'Installer compilation failed. See installer-build.log.' }
}
$verificationArgs = @('--app', $app, '--node', $node, '--output', (Join-Path $OutputRoot 'package-verification.json'))
if (-not $SkipInstaller) { $verificationArgs += @('--installer', (Join-Path $dist "EtherlightingEvolved-Setup-$version.exe")) }
& $PythonExe (Join-Path $PSScriptRoot 'verify-package.py') @verificationArgs *> (Join-Path $OutputRoot 'package-verification.log')
if ($LASTEXITCODE) { throw 'Package verification failed. See package-verification.log.' }
if ($SkipInstaller) { Write-Output (Join-Path $app 'EtherlightingEvolved.exe') }
else { Write-Output (Join-Path $dist "EtherlightingEvolved-Setup-$version.exe") }
