# Local-only real-desktop FlaUI acceptance suite. No CI workflow is created.
[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',
    [string]$AppPath = '',
    [string]$Filter = '',
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw 'FlaUI tests require an interactive Windows desktop.'
}
if (-not [Environment]::UserInteractive) {
    throw 'FlaUI tests cannot execute in a headless or noninteractive session.'
}
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'Install the .NET 10 SDK and Windows Desktop Runtime before running the GUI suite.'
}

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$project = Join-Path $repo 'tests\RbgCanInsight.GuiTests\RbgCanInsight.GuiTests.csproj'
$results = Join-Path $repo 'artifacts\flaui'
New-Item -ItemType Directory -Path $results -Force | Out-Null
if ($AppPath) {
    $resolved = (Resolve-Path -Path $AppPath -ErrorAction Stop).Path
    $env:RBG_CAN_INSIGHT_GUI_APP_PATH = $resolved
}
Push-Location $repo
try {
    if (-not $SkipBuild) {
        & dotnet build $project -c $Configuration
        if ($LASTEXITCODE -ne 0) { throw "FlaUI project build failed with exit code $LASTEXITCODE." }
    }
    $arguments = @(
        'test', $project, '-c', $Configuration,
        '--no-build',
        '--logger', 'trx;LogFileName=flaui-results.trx',
        '--results-directory', $results
    )
    if ($Filter) { $arguments += @('--filter', $Filter) }
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "FlaUI tests failed with exit code $LASTEXITCODE. Review $results\flaui-results.trx"
    }
    Write-Host "FlaUI tests passed. Result file: $results\flaui-results.trx"
}
finally {
    Pop-Location
}
