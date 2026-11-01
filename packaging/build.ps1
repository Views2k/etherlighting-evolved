# Build the current desktop application with existing local tools.
param(
    [string]$PythonExe,
    [string]$NodeExe,
    [Alias('InstallerCompiler')][string]$CompilerExe,
    [string]$OutputRoot,
    [switch]$SkipInstaller
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (-not $PythonExe) { $PythonExe = Join-Path $root '.venv\Scripts\python.exe' }
if (-not $NodeExe) {
    $nodeCommand = Get-Command node.exe -CommandType Application -ErrorAction SilentlyContinue
    if (-not $nodeCommand) { throw 'Provide -NodeExe pointing to an existing Node.js installation.' }
    $NodeExe = $nodeCommand.Source
}
$options = @{PythonExe=$PythonExe;NodeExe=$NodeExe;SkipInstaller=$SkipInstaller}
if ($CompilerExe) { $options.CompilerExe = $CompilerExe }
if ($OutputRoot) { $options.OutputRoot = $OutputRoot }
& (Join-Path $root 'tools\build-workspace.ps1') @options
