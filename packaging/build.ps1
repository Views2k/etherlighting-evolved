# Build the current desktop application with existing local tools.
param(
    [string]$PythonExe,
    [string]$NodeExe,
    [Alias('InstallerCompiler')][string]$CompilerExe,
    [string]$OutputRoot,
    [switch]$SkipInstaller,
    # Build from uncommitted changes or with tools other than the pinned versions, for a local test only.
    [switch]$AllowUnpinned
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (-not $PythonExe) { $PythonExe = Join-Path $root '.venv\Scripts\python.exe' }
if (-not $NodeExe) {
    $repoNode = Join-Path $root 'outputs\build-tools\node\node.exe'
    if (Test-Path -LiteralPath $repoNode -PathType Leaf) { $NodeExe = $repoNode }
}
if (-not $NodeExe) {
    $nodeCommand = Get-Command node.exe -CommandType Application -ErrorAction SilentlyContinue
    if (-not $nodeCommand) { throw 'Provide -NodeExe pointing to an existing Node.js installation.' }
    $NodeExe = @($nodeCommand)[0].Source
}
$options = @{PythonExe=$PythonExe;NodeExe=$NodeExe;SkipInstaller=$SkipInstaller;AllowUnpinned=$AllowUnpinned}
if ($CompilerExe) { $options.CompilerExe = $CompilerExe }
if ($OutputRoot) { $options.OutputRoot = $OutputRoot }
& (Join-Path $root 'tools\build-workspace.ps1') @options
