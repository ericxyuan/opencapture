param(
    [switch]$OwnershipOnly,
    [switch]$ReviewOnly,
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
if ($OwnershipOnly -and $ReviewOnly) {
    throw 'Choose either OwnershipOnly or ReviewOnly.'
}
$workspace = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $env:LOCALAPPDATA ('OpenCaptureBuild\tests\' + [Guid]::NewGuid().ToString('N'))
}
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$runner = Join-Path ([System.IO.Path]::GetFullPath($OutputDirectory)) 'OpenCaptureTests.exe'
& (Join-Path $workspace 'build.ps1') -TestBuild -OutputPath $runner
$testArguments = @()
if ($OwnershipOnly) { $testArguments += '--ownership-only' }
if ($ReviewOnly) { $testArguments += '--review-only' }
& $runner @testArguments
$testExitCode = $LASTEXITCODE
Write-Host "EXIT=$testExitCode"
if ($testExitCode -ne 0) {
    throw "OpenCapture tests failed with exit code $testExitCode. Runner: $runner"
}
