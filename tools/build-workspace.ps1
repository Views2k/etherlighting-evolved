param(
    [Parameter(Mandatory=$true)][string]$PythonExe,
    [Parameter(Mandatory=$true)][string]$NodeExe,
    [string]$CompilerExe,
    [string]$OutputRoot,
    [switch]$SkipInstaller,
    [switch]$ReleaseCandidate
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
& $PythonExe -c "import PyInstaller, fastapi, uvicorn, asyncssh, webview, pystray, PIL"
if ($LASTEXITCODE) { throw 'A build dependency is missing. No dependencies were downloaded.' }
$version = & $PythonExe -c "import runpy,sys; print(runpy.run_path(sys.argv[1])['__version__'])" (Join-Path $backend 'src\etherlighting_evolved\__init__.py')
if ($LASTEXITCODE -or $version -notmatch '^\d+\.\d+\.\d+$') { throw 'The application version could not be read.' }
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
& $PythonExe -m PyInstaller --noconfirm --distpath $dist --workpath (Join-Path $OutputRoot 'desktop-build') (Join-Path $backend 'packaging\workspace-desktop.spec') *> (Join-Path $OutputRoot 'desktop-build.log')
if ($LASTEXITCODE) { throw 'Desktop packaging failed. See desktop-build.log.' }
& $PythonExe -m PyInstaller --noconfirm --distpath (Join-Path $OutputRoot 'engine-dist') --workpath (Join-Path $OutputRoot 'engine-build') (Join-Path $backend 'packaging\native-service.spec') *> (Join-Path $OutputRoot 'engine-build.log')
if ($LASTEXITCODE) { throw 'Engine packaging failed. See engine-build.log.' }
New-Item -ItemType Directory -Path (Join-Path $app 'engine') -Force | Out-Null
Get-ChildItem -LiteralPath (Join-Path $OutputRoot 'engine-dist\EEEngine') | Copy-Item -Destination (Join-Path $app 'engine') -Recurse -Force
foreach ($folder in @('preview','src','assets')) {
    New-Item -ItemType Directory -Path (Join-Path $app $folder) -Force | Out-Null
    Get-ChildItem -LiteralPath (Join-Path $workspace $folder) -File | Where-Object { $_.Extension -in @('.mjs','.css','.html','.png','.svg','.ico','.webmanifest') } | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $app $folder)
    }
}
New-Item -ItemType Directory -Path (Join-Path $app 'runtime'),(Join-Path $app 'licenses') -Force | Out-Null
Copy-Item -LiteralPath $node -Destination (Join-Path $app 'runtime\node.exe')
Copy-Item -LiteralPath (Join-Path $backend 'LICENSE') -Destination (Join-Path $app 'LICENSE.txt')
Copy-Item -LiteralPath $nodeLicense -Destination (Join-Path $app 'licenses\Node-LICENSE.txt')
& $PythonExe (Join-Path $PSScriptRoot 'collect-package-notices.py') $app
if ($LASTEXITCODE) { throw 'Dependency notice collection failed.' }
& $node -p 'process.versions.node' | Set-Content -LiteralPath (Join-Path $OutputRoot 'node-version.txt')
if ($LASTEXITCODE) { throw 'The Node.js version could not be read.' }
if (-not $ReleaseCandidate) {
    & $PythonExe (Join-Path $PSScriptRoot 'verify-package.py') --app $app --check-contents
    if ($LASTEXITCODE) { throw 'Package content check failed.' }
}
if (-not $SkipInstaller) {
    & $compiler /Q "/DAppVersion=$version" "/DAppSourceDir=$app" "/O$dist" (Join-Path $backend 'packaging\installer.iss') *> (Join-Path $OutputRoot 'installer-build.log')
    if ($LASTEXITCODE) { throw 'Installer compilation failed. See installer-build.log.' }
}
if ($ReleaseCandidate) {
    $verificationArgs = @('--app', $app, '--node', $node, '--output', (Join-Path $OutputRoot 'package-verification.json'))
    if (-not $SkipInstaller) { $verificationArgs += @('--installer', (Join-Path $dist "EtherlightingEvolved-Setup-$version.exe")) }
    & $PythonExe (Join-Path $PSScriptRoot 'verify-package.py') @verificationArgs *> (Join-Path $OutputRoot 'package-verification.log')
    if ($LASTEXITCODE) { throw 'Package verification failed. See package-verification.log.' }
}
if ($SkipInstaller) { Write-Output (Join-Path $app 'EtherlightingEvolved.exe') }
else { Write-Output (Join-Path $dist "EtherlightingEvolved-Setup-$version.exe") }
