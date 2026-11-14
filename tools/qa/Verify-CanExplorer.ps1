#requires -Version 5.1
<#
.SYNOPSIS
  Local Windows quality gate for the CAN Explorer / source-backed signal map.
.DESCRIPTION
  This script runs on a Windows development machine with .NET 10 SDK.
  No GitHub Actions, release, signing, tags, or network writes to GitHub.
  It compiles WPF, runs every xUnit suite test, then optionally runs the
  real interactive FlaUI acceptance tests. It stops at the first failure.
.PARAMETER IncludeGui
  Run the FULL interactive Windows FlaUI project after a successful build.
  Requires an unlocked Windows desktop; remote headless sessions are invalid.
.PARAMETER Configuration
  Default: Release.
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\tools\qa\Verify-CanExplorer.ps1
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\tools\qa\Verify-CanExplorer.ps1 -IncludeGui
#>
[CmdletBinding()]
param(
    [switch]$IncludeGui,
    [ValidateSet('Debug','Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($env:OS -ne 'Windows_NT') {
    throw 'WPF / FlaUI verification must be run on the Windows development PC.'
}

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$solution = Join-Path $repo 'RbgCanInsight.sln'
$unit = Join-Path $repo 'tests\RbgCanInsight.Tests\RbgCanInsight.Tests.csproj'
$gui = Join-Path $repo 'tests\RbgCanInsight.GuiTests\RbgCanInsight.GuiTests.csproj'
$output = Join-Path $repo 'artifacts\qa\can-explorer'
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw '.NET 10 SDK missing: install dotnet 10 SDK on this Windows development PC.'
}

New-Item -ItemType Directory -Force -Path $output | Out-Null
$sessionId = (Get-Date -Format 'yyyyMMdd-HHmmss')
$logFile = Join-Path $output "verification-$sessionId.log"
$transcriptStarted = $false
$allPassed = $false

function Invoke-DotnetChecked {
    param(
        [Parameter(Mandatory=$true)][string]$Step,
        [Parameter(Mandatory=$true)][string[]]$Arguments
    )
    Write-Host ""
    Write-Host "=== $Step ===" -ForegroundColor Cyan
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Step FAILED (dotnet exit code $LASTEXITCODE). Stop: PR must stay draft."
    }
}

try {
    Start-Transcript -Path $logFile -ErrorAction Stop | Out-Null
    $transcriptStarted = $true
    Set-Location $repo

    Write-Host "Repository: $repo"
    Write-Host "Quality gate: CAN Explorer / relationship map / DTC regression"
    Write-Host "GUI FlaUI acceptance: $([bool]$IncludeGui)"
    Write-Host "This process never merges, signs or publishes any release."
    Invoke-DotnetChecked -Step 'SDK info' -Arguments @('--info')
    $sdkVersion = (& dotnet --version).Trim()
    if ($sdkVersion -notmatch '^10\.') {
        throw "This solution targets .NET 10. Current SDK: $sdkVersion"
    }

    Invoke-DotnetChecked -Step 'Restore all solution projects' -Arguments @(
        'restore', $solution
    )
    Invoke-DotnetChecked -Step 'Compile entire Windows WPF solution' -Arguments @(
        'build', $solution, '--configuration', $Configuration, '--no-restore',
        '/warnaserror:NU1901,NU1902,NU1903,NU1904'
    )
    Invoke-DotnetChecked -Step 'Execute entire xUnit test project' -Arguments @(
        'test', $unit, '--configuration', $Configuration, '--no-build',
        '--no-restore', '--logger', "trx;LogFileName=unit-$sessionId.trx",
        '--results-directory', $output
    )

    if ($IncludeGui) {
        if ([System.Environment]::UserInteractive -ne $true) {
            throw 'FlaUI needs an interactive and unlocked Windows desktop.'
        }
        Write-Host 'FlaUI will start and control the actual WPF executable.' -ForegroundColor Yellow
        Invoke-DotnetChecked -Step 'Execute entire Windows FlaUI test project' -Arguments @(
            'test', $gui, '--configuration', $Configuration, '--no-build',
            '--no-restore', '--logger', "trx;LogFileName=flaui-$sessionId.trx",
            '--results-directory', $output
        )
    }

    $allPassed = $true
    Write-Host ""
    if ($IncludeGui) {
        Write-Host 'PASS: restore, WPF build, xUnit and FlaUI.' -ForegroundColor Green
    } else {
        Write-Host 'PARTIAL PASS: restore, WPF build and xUnit; FlaUI NOT RUN.' -ForegroundColor Yellow
        Write-Host 'Run again with -IncludeGui on an unlocked Windows desktop.'
    }
    Write-Host 'Sign/release gates and independent Vector parity are outside this script.'
}
finally {
    if ($transcriptStarted) {
        try { Stop-Transcript | Out-Null } catch { }
    }
    Write-Host "Verification transcript: $logFile"
}
if (-not $allPassed) { exit 1 }
