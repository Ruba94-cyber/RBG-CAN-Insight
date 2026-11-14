# Local Windows engineering gate for RBG CAN Insight.
# Developer desktop + .NET 10; never creates a release or uploads OEM data.
[CmdletBinding()]
param([switch]$SkipGui, [switch]$SkipRestore)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
    [System.Runtime.InteropServices.OSPlatform]::Windows)) {
    throw 'Run this gate on Windows with .NET 10 and WPF.'
}

$repository = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

function Invoke-DotNet {
    param([Parameter(Mandatory)][string]$Label,
          [Parameter(Mandatory)][string[]]$Arguments)
    Write-Host ">>> $Label" -ForegroundColor Cyan
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Label failed (dotnet exit code $LASTEXITCODE)."
    }
}

Push-Location $repository
try {
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        throw 'Install the .NET 10 SDK or add dotnet to PATH.'
    }
    Invoke-DotNet -Label 'SDK version' -Arguments @('--version')
    if (-not $SkipRestore) {
        Invoke-DotNet -Label 'Restore solution' -Arguments @('restore', 'RbgCanInsight.sln')
    }

    Invoke-DotNet -Label 'Release solution build' -Arguments @(
        'build', 'RbgCanInsight.sln', '--configuration', 'Release', '--no-restore')
    Invoke-DotNet -Label 'Entire xUnit suite' -Arguments @(
        'test', 'tests/RbgCanInsight.Tests/RbgCanInsight.Tests.csproj',
        '--configuration', 'Release', '--no-restore')

    if ($SkipGui) {
        Write-Warning 'GUI intentionally skipped. This is NOT a Windows-verified release.'
        return
    }
    if (-not [Environment]::UserInteractive) {
        throw 'WPF GUI verification requires a logged-in Windows desktop.'
    }

    Invoke-DotNet -Label 'WPF GUI verification and screenshots' -Arguments @(
        'run', '--project', 'tools/RbgCanInsight.GuiVerify/RbgCanInsight.GuiVerify.csproj',
        '--configuration', 'Release', '--no-restore')

    $reportPath = Join-Path $repository 'artifacts/gui-verification/gui-verification.json'
    if (-not (Test-Path -LiteralPath $reportPath)) {
        throw "Missing GUI verification report: $reportPath"
    }
    $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
    $required = @(
        'DbcEditControlsVisible',
        'DbcEditStagesWithoutMutatingSource',
        'DbcEditSaveAsWorks',
        'DbcEditRevertWorks',
        'DbcNamedValueGridVisible',
        'DbcNamedValueDuplicateRejected',
        'DbcNamedValueStageWorks',
        'DbcNamedValueSaveAsWorks',
        'DbcNamedValueRevertWorks',
        'DefinitionWorkbookExportsWork',
        'DiagnosticCatalogExplorerWorks'
    )
    foreach ($key in $required) {
        $property = $report.PSObject.Properties[$key]
        if ($null -eq $property -or $property.Value -ne $true) {
            throw "GUI acceptance failed: $key (see $reportPath)"
        }
        Write-Host "PASS $key" -ForegroundColor Green
    }
    foreach ($artifact in @('dbc-working-copy-editor.png', 'dbc-value-table-editor.png',
                             'gui-dbc-definitions.xlsx', 'gui-diagnostic-definitions.xlsx')) {
        $file = Join-Path $repository ('artifacts/gui-verification/' + $artifact)
        if (-not (Test-Path -LiteralPath $file)) {
            throw "Missing artifact: $artifact"
        }
    }
    Write-Host 'Local build, xUnit, GUI gates and evidence completed.' -ForegroundColor Green
    Write-Warning 'Manual Excel/Vector/CANdela oracle checks, small-screen DPI and packages still need validation.'
}
finally {
    Pop-Location
}
