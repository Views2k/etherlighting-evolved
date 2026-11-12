param(
    [string]$PythonExe,
    [Parameter(Mandatory=$true)][string]$BrowserExe,
    [string]$OutputRoot
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (-not $PythonExe) { $PythonExe = Join-Path $root '.venv\Scripts\python.exe' }
if (-not $OutputRoot) { $OutputRoot = Join-Path $root ('outputs\verification\' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
$PythonExe = [IO.Path]::GetFullPath($PythonExe)
$BrowserExe = [IO.Path]::GetFullPath($BrowserExe)
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
foreach ($tool in @($PythonExe, $BrowserExe)) {
    if (-not (Test-Path -LiteralPath $tool -PathType Leaf)) { throw 'Provide existing Python and browser executables.' }
}
if ((Test-Path -LiteralPath $OutputRoot) -and @(Get-ChildItem -LiteralPath $OutputRoot -Force).Count) {
    throw 'Choose an empty output directory to preserve earlier results.'
}
New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null
$node = @(Get-Command node -CommandType Application)[0].Source
$steps = [Collections.Generic.List[object]]::new()
function Invoke-Check([string]$Name, [string]$Executable, [string[]]$Arguments) {
    $clock = [Diagnostics.Stopwatch]::StartNew()
    & $Executable @Arguments *> (Join-Path $OutputRoot "$Name.log")
    $code = $LASTEXITCODE
    $clock.Stop()
    $steps.Add(@{name=$Name;exit_code=$code;seconds=[Math]::Round($clock.Elapsed.TotalSeconds,2)})
    if ($code) { throw "$Name failed. See $OutputRoot\$Name.log" }
    Write-Output "$Name passed."
}
Push-Location $root
$passed = $false
try {
    Invoke-Check 'repository' $PythonExe @('tools/check-repository.py','--output',(Join-Path $OutputRoot 'source-manifest.json'))
    Invoke-Check 'syntax' $node @('tools/check.mjs')
    Invoke-Check 'javascript' $node @('--test','tests/*.test.mjs','preview/*.test.mjs')
    Invoke-Check 'lint' $PythonExe @('-m','ruff','check','src/etherlighting_evolved','tests')
    Invoke-Check 'python' $PythonExe @('-m','pytest','-q','--junitxml',(Join-Path $OutputRoot 'python.xml'))
    Invoke-Check 'contract' $PythonExe @('tests/backend_contract.py','.')
    # The desktop program: offscreen rendering checks and a lighting session against the simulator.
    $check = Join-Path $root 'desktop\EtherlightingEvolved.Native.Check'
    if (-not (Test-Path -LiteralPath (Join-Path $check 'obj\project.assets.json') -PathType Leaf)) {
        throw 'Restore the desktop packages first (see desktop/README.md).'
    }
    $env:AVALONIA_TELEMETRY_OPTOUT = '1'
    Invoke-Check 'desktop-build' 'dotnet' @('build',(Join-Path $check 'EtherlightingEvolved.Native.Check.csproj'),'-c','Release','--no-restore')
    Invoke-Check 'desktop' 'dotnet' @((Join-Path $check 'bin\Release\net8.0\EtherlightingEvolved.Native.Check.dll'),'--output',(Join-Path $OutputRoot 'desktop'),'--service')
    Invoke-Check 'browser' $node @('tools/smoke-workspace.mjs','--root',$root,'--python',$PythonExe,'--browser',$BrowserExe,'--output',(Join-Path $OutputRoot 'browser'),'--port','4198')
    $passed = $true
} finally {
    @{passed=$passed;steps=$steps.ToArray()} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $OutputRoot 'result.json') -Encoding utf8
    Pop-Location
}
