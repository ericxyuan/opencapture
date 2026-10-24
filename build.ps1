param(
    [switch]$DebugBuild,
    [switch]$TestBuild,
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $MyInvocation.MyCommand.Path
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$wpfReferencePath = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\WPF'
$systemRuntimeReference = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\System.Runtime.dll'
$windowsInteropReference = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\System.Runtime.InteropServices.WindowsRuntime.dll'
$windowsRuntimeReference = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\System.Runtime.WindowsRuntime.dll'
$source = Join-Path $workspace 'src\OpenCapture'
$output = if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    Join-Path $workspace $(if ($TestBuild) { 'tests\OpenCaptureTests.exe' } else { 'OpenCapture.exe' })
} else {
    [System.IO.Path]::GetFullPath($OutputPath)
}
$fontResource = Join-Path $source 'Resources\Proxima Nova A.otf'
$startupImageResource = Join-Path $workspace 'OpenCapture Startup Window.png'
$trayIconResource = Join-Path $workspace 'OpenCapture Tray Icon.png'

if (-not (Test-Path -LiteralPath $compiler)) {
    throw 'The .NET Framework 4.x C# compiler was not found on this Windows installation.'
}

foreach ($requiredResource in @($fontResource, $startupImageResource, $trayIconResource)) {
    if (-not (Test-Path -LiteralPath $requiredResource)) {
        throw "Required embedded resource was not found: $requiredResource"
    }
}

$windowsContractsVersion = '10.0.26100.9169'
$windowsContractsRoot = Join-Path $env:LOCALAPPDATA "OpenCaptureBuild\Microsoft.Windows.SDK.Contracts\$windowsContractsVersion"
$windowsMetadataReference = Join-Path $windowsContractsRoot 'ref\netstandard2.0\Windows.WinMD'
$windowsFoundationReference = Join-Path $windowsContractsRoot 'ref\netstandard2.0\Windows.Foundation.FoundationContract.winmd'
$windowsUniversalReference = Join-Path $windowsContractsRoot 'ref\netstandard2.0\Windows.Foundation.UniversalApiContract.winmd'
if (-not (Test-Path -LiteralPath $windowsMetadataReference)) {
    $packageRoot = Split-Path -Parent $windowsContractsRoot
    New-Item -ItemType Directory -Force -Path $packageRoot | Out-Null
    $packagePath = Join-Path $packageRoot "microsoft.windows.sdk.contracts.$windowsContractsVersion.zip"
    if (-not (Test-Path -LiteralPath $packagePath)) {
        $packageUri = "https://api.nuget.org/v3-flatcontainer/microsoft.windows.sdk.contracts/$windowsContractsVersion/microsoft.windows.sdk.contracts.$windowsContractsVersion.nupkg"
        Invoke-WebRequest -UseBasicParsing -Uri $packageUri -OutFile $packagePath
    }
    Expand-Archive -LiteralPath $packagePath -DestinationPath $windowsContractsRoot -Force
}

foreach ($requiredReference in @($systemRuntimeReference, $windowsInteropReference, $windowsRuntimeReference, $windowsMetadataReference, $windowsFoundationReference, $windowsUniversalReference)) {
    if (-not (Test-Path -LiteralPath $requiredReference)) {
        throw "Required Windows OCR reference was not found: $requiredReference"
    }
}

$arguments = @(
    '/nologo',
    $(if ($TestBuild) { '/target:exe' } else { '/target:winexe' }),
    '/platform:x64',
    '/optimize+',
    '/unsafe',
    '/warn:4',
    "/out:$output",
    "/win32manifest:$(Join-Path $source 'app.manifest')",
    '/reference:System.dll',
    '/reference:System.Core.dll',
    '/reference:System.Drawing.dll',
    '/reference:System.Windows.Forms.dll',
    '/reference:Microsoft.CSharp.dll',
    '/reference:Microsoft.VisualBasic.dll',
    "/reference:$systemRuntimeReference",
    "/reference:$windowsInteropReference",
    "/reference:$windowsRuntimeReference",
    "/reference:$windowsMetadataReference",
    "/reference:$windowsFoundationReference",
    "/reference:$windowsUniversalReference",
    "/reference:$(Join-Path $wpfReferencePath 'UIAutomationClient.dll')",
    "/reference:$(Join-Path $wpfReferencePath 'UIAutomationTypes.dll')",
    "/reference:$(Join-Path $wpfReferencePath 'WindowsBase.dll')",
    "/resource:$fontResource,OpenCapture.Resources.ProximaNovaA.otf",
    "/resource:$startupImageResource,OpenCapture.Resources.StartupWindow.png",
    "/resource:$trayIconResource,OpenCapture.Resources.TrayIcon.png",
    "/recurse:$(Join-Path $source '*.cs')"
)

if ($TestBuild) {
    $arguments += '/main:OpenCapture.Tests.TestRunner'
    $arguments += (Join-Path $workspace 'tests\OpenCaptureTests.cs')
    $arguments += @(Get-ChildItem -LiteralPath (Join-Path $workspace 'tests') -Filter '*ReviewTests.cs' |
        Sort-Object Name | ForEach-Object { $_.FullName })
}

if ($DebugBuild) {
    $arguments += '/debug:pdbonly'
} else {
    $arguments += '/debug-'
}

New-Item -ItemType Directory -Force -Path (Split-Path -Parent $output) | Out-Null
& $compiler $arguments
if ($LASTEXITCODE -ne 0) {
    throw "OpenCapture build failed with exit code $LASTEXITCODE."
}

Write-Host "Built $output"
