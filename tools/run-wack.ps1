<#
.SYNOPSIS
Runs the Windows App Certification Kit (WACK) on an MSIX package. Must run elevated.

.DESCRIPTION
Without -Elevated this script relaunches itself through a UAC prompt, so the user approves the
elevation. The XML report is written next to the package. Exit code 0 = overall PASS.

.EXAMPLE
pwsh tools\run-wack.ps1 -Package artifacts\msix\RBG-CAN-Insight_3.0.0_win-x64.msix
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Package,
    [string]$ReportPath = "",
    [switch]$Elevated
)

$ErrorActionPreference = "Stop"
$Package = (Resolve-Path $Package).Path
if ([string]::IsNullOrWhiteSpace($ReportPath)) {
    $ReportPath = [IO.Path]::ChangeExtension($Package, ".wack.xml")
}
$logPath = [IO.Path]::ChangeExtension($Package, ".wack.log")

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).
    IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdmin) {
    if ($Elevated) { throw "Could not obtain an elevated process." }
    Write-Host "WACK needs Administrator rights. Approve the UAC prompt to continue..."
    $arguments = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "`"$PSCommandPath`"",
        "-Package", "`"$Package`"", "-ReportPath", "`"$ReportPath`"", "-Elevated")
    $process = Start-Process pwsh -ArgumentList $arguments -Verb RunAs -Wait -PassThru
    if (Test-Path $logPath) { Get-Content $logPath }
    exit $process.ExitCode
}

$appCert = Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\App Certification Kit\appcert.exe"
if (-not (Test-Path $appCert)) { throw "appcert.exe not found. Install the Windows SDK App Certification Kit." }

Start-Transcript -Path $logPath -Force | Out-Null
try {
    if (Test-Path $ReportPath) { Remove-Item $ReportPath -Force }
    & $appCert reset | Out-Null
    & $appCert test -appxpackagepath $Package -reportoutputpath $ReportPath
    $code = $LASTEXITCODE
    if (Test-Path $ReportPath) {
        [xml]$report = Get-Content $ReportPath
        $overall = $report.REPORT.OVERALL_RESULT
        Write-Host "WACK overall result: $overall"
        foreach ($test in $report.SelectNodes("//TEST[@RESULT='FAIL']")) {
            Write-Host "FAILED: $($test.NAME)"
        }
        if ($overall -ne "PASS") { $code = 1 } else { $code = 0 }
    } else {
        Write-Host "No report produced (appcert exit code $code)."
        if ($code -eq 0) { $code = 1 }
    }
    exit $code
} finally {
    Stop-Transcript | Out-Null
}
