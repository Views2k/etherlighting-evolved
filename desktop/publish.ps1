param(
    [string[]]$Runtime = @('win-x64', 'osx-arm64', 'osx-x64', 'linux-x64'),
    [string]$PythonExe,
    [string]$OutputRoot
)
# Standalone builds of the desktop program for Windows, macOS and Linux. Each build includes the .NET runtime.
# macOS builds become an application bundle (EtherlightingEvolved.app, zipped) and Linux builds a tar.gz with a
# desktop entry and icon. Packages must already be restored (see README.md); nothing is downloaded.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $PSScriptRoot 'EtherlightingEvolved.Native\EtherlightingEvolved.Native.csproj'
if (-not $PythonExe) { $PythonExe = Join-Path $root '.venv\Scripts\python.exe' }
if (-not $OutputRoot) { $OutputRoot = Join-Path $root 'outputs\native' }
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
if (-not (Test-Path -LiteralPath (Join-Path (Split-Path $project -Parent) 'obj\project.assets.json') -PathType Leaf)) {
    throw 'Restore the desktop packages first (see README.md). No packages were downloaded.'
}
$version = ([xml](Get-Content -LiteralPath $project -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$icon = Join-Path $root 'src\etherlighting_evolved\assets\ee.png'
$env:AVALONIA_TELEMETRY_OPTOUT = '1'
New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null

foreach ($rid in $Runtime) {
    $build = Join-Path $OutputRoot $rid
    if (Test-Path -LiteralPath $build) { Remove-Item -LiteralPath $build -Recurse -Force }
    dotnet publish $project -c Release -r $rid --no-restore -o $build *> (Join-Path $OutputRoot "$rid.log")
    if ($LASTEXITCODE) { throw "The $rid build failed. See $rid.log." }
    $licenses = Join-Path $build 'licenses'
    New-Item -ItemType Directory -Path $licenses -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination (Join-Path $build 'LICENSE.txt')

    if ($rid -like 'osx-*') {
        # EtherlightingEvolved.app/Contents/{Info.plist, MacOS/<program>, Resources/ee.icns}
        $bundle = Join-Path $OutputRoot "$rid-app\EtherlightingEvolved.app"
        if (Test-Path -LiteralPath (Split-Path $bundle -Parent)) { Remove-Item -LiteralPath (Split-Path $bundle -Parent) -Recurse -Force }
        $macos = Join-Path $bundle 'Contents\MacOS'
        $resources = Join-Path $bundle 'Contents\Resources'
        New-Item -ItemType Directory -Path $macos, $resources -Force | Out-Null
        Get-ChildItem -LiteralPath $build | Copy-Item -Destination $macos -Recurse -Force
        & $PythonExe -I -c "import sys; from PIL import Image; Image.open(sys.argv[1]).convert('RGBA').save(sys.argv[2], sizes=[(16,16),(32,32),(64,64),(128,128),(256,256)])" $icon (Join-Path $resources 'ee.icns')
        if ($LASTEXITCODE) { throw 'The macOS icon could not be made.' }
        $plist = @"
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>Etherlighting Evolved</string>
  <key>CFBundleDisplayName</key><string>Etherlighting Evolved</string>
  <key>CFBundleIdentifier</key><string>com.views2k.etherlighting-evolved</string>
  <key>CFBundleVersion</key><string>$version</string>
  <key>CFBundleShortVersionString</key><string>$version</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleExecutable</key><string>EtherlightingEvolved</string>
  <key>CFBundleIconFile</key><string>ee.icns</string>
  <key>LSMinimumSystemVersion</key><string>11.0</string>
  <key>NSHighResolutionCapable</key><true/>
</dict>
</plist>
"@
        [IO.File]::WriteAllText((Join-Path $bundle 'Contents\Info.plist'), $plist.Replace("`r`n", "`n"))
        $archive = Join-Path $OutputRoot "EtherlightingEvolved-$version-$rid.zip"
        & $PythonExe -I (Join-Path $PSScriptRoot 'make_archive.py') zip $bundle $archive EtherlightingEvolved
        if ($LASTEXITCODE) { throw 'The macOS archive could not be made.' }
    }
    elseif ($rid -like 'linux-*') {
        Copy-Item -LiteralPath $icon -Destination (Join-Path $build 'etherlighting-evolved.png')
        $entry = "[Desktop Entry]`nType=Application`nName=Etherlighting Evolved`nComment=Lighting controls for UniFi switches`nExec=EtherlightingEvolved`nIcon=etherlighting-evolved`nCategories=Network;Utility;`nTerminal=false`n"
        [IO.File]::WriteAllText((Join-Path $build 'etherlighting-evolved.desktop'), $entry)
        $archive = Join-Path $OutputRoot "EtherlightingEvolved-$version-$rid.tar.gz"
        & $PythonExe -I (Join-Path $PSScriptRoot 'make_archive.py') tgz $build $archive EtherlightingEvolved
        if ($LASTEXITCODE) { throw 'The Linux archive could not be made.' }
    }
    else {
        Write-Output (Join-Path $build 'EtherlightingEvolved.exe')
    }
}
