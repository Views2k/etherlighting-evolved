$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') {
    throw 'Compiler setup is restricted to the GitHub-hosted build runner.'
}
$setup = Join-Path $env:RUNNER_TEMP 'innosetup-6.7.4.exe'
$compilerDir = Join-Path $env:RUNNER_TEMP 'ee-inno-6.7.4'
$url = 'https://github.com/jrsoftware/issrc/releases/download/is-6_7_4/innosetup-6.7.4.exe'
Invoke-WebRequest -Uri $url -OutFile $setup
if ((Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash.ToLowerInvariant() -ne 'a549929319f9120716677c685535ca4671e4b937b5fdc87e2585f32d8eb30e8c') {
    throw 'The installer compiler hash does not match the pinned official release.'
}
$signature = Get-AuthenticodeSignature -LiteralPath $setup
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'Pyrsys B\.V\.') {
    throw 'The installer compiler publisher could not be verified.'
}
$process = Start-Process -FilePath $setup -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-',('/DIR="' + $compilerDir + '"')) -PassThru -Wait -WindowStyle Hidden
if ($process.ExitCode -ne 0) { throw 'Installer compiler setup failed.' }
$compiler = Join-Path $compilerDir 'ISCC.exe'
if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) { throw 'The installer compiler is missing.' }
"EE_CI_COMPILER=$compiler" | Out-File -FilePath $env:GITHUB_ENV -Encoding utf8 -Append
