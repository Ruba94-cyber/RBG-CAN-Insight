<#
.SYNOPSIS
Builds the RBG CAN Insight portable release with a clean, professional layout:
the executable sits alone in the root, everything else lives in dedicated folders.

.DESCRIPTION
Publishes the WPF app as a self-contained single-file executable, then arranges:

  RBG-CAN-Insight_<version>_Portable/
  ├── RbgCanInsight.exe      ← launch this
  ├── README.txt
  ├── config/                ← recent files and future user settings
  ├── logs/                  ← application logs
  ├── docs/                  ← user guide
  └── runtime/               ← support files (debug symbols, native leftovers)

.EXAMPLE
pwsh tools/make-portable.ps1
pwsh tools/make-portable.ps1 -DotnetExe C:\Users\me\.dotnet\dotnet.exe
#>
[CmdletBinding()]
param(
    [string]$DotnetExe = "dotnet",
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$OutputRoot = "",
    [string]$SignScript = ""
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot "src\RbgCanInsight.App\RbgCanInsight.App.csproj"

# Version from Directory.Build.props keeps the artifact name in sync with the assembly.
[xml]$props = Get-Content (Join-Path $repoRoot "Directory.Build.props")
$version = $props.Project.PropertyGroup.Version | Select-Object -First 1
if (-not $version) { $version = "0.0.0" }

$publishDir = Join-Path $repoRoot "artifacts\publish-tmp"
$releaseName = "RBG-CAN-Insight_${version}_Portable"
$artifactRoot = if ([string]::IsNullOrWhiteSpace($OutputRoot)) { Join-Path $repoRoot "artifacts" } else { $OutputRoot }
$releaseDir = Join-Path $artifactRoot $releaseName
$zipPath = Join-Path $artifactRoot "$releaseName.zip"

New-Item -ItemType Directory -Force $artifactRoot | Out-Null

Write-Host "Publishing $project ($Configuration, $Runtime, single-file self-contained)..."
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
& $DotnetExe publish $project `
    -c $Configuration -r $Runtime --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=embedded `
    -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

if (-not [string]::IsNullOrWhiteSpace($SignScript)) {
    # Authenticode-sign our own binaries (not the Microsoft runtime files) before they are packaged.
    $own = Get-ChildItem $publishDir -Recurse -File | Where-Object { $_.Name -like "RbgCanInsight*" -and $_.Extension -in ".exe", ".dll" }
    foreach ($file in $own) { & pwsh -NoProfile -File $SignScript -FilePath $file.FullName; if ($LASTEXITCODE -ne 0) { throw "Signing failed for $($file.Name) ($LASTEXITCODE)" } }
}

Write-Host "Arranging portable layout in $releaseDir..."
if (Test-Path $releaseDir) { Remove-Item $releaseDir -Recurse -Force }
New-Item -ItemType Directory -Force $releaseDir | Out-Null
foreach ($sub in "config", "logs", "docs", "runtime") {
    New-Item -ItemType Directory -Force (Join-Path $releaseDir $sub) | Out-Null
}

# Root: only the executable and the readme.
Copy-Item (Join-Path $publishDir "RbgCanInsight.exe") $releaseDir

# Everything else from the publish output goes to runtime/ (usually only symbols leftovers;
# native libraries are embedded in the single-file exe and self-extract at first run).
Get-ChildItem $publishDir -File | Where-Object { $_.Name -ne "RbgCanInsight.exe" } |
    Copy-Item -Destination (Join-Path $releaseDir "runtime")

# Docs.
foreach ($doc in "USER_GUIDE.md", "ARCHITECTURE.md") {
    $source = Join-Path $repoRoot "docs\$doc"
    if (Test-Path $source) { Copy-Item $source (Join-Path $releaseDir "docs") }
}

@"
RBG CAN Insight $version — Portable
Designed by Paolo Rubagotti

Start:  double-click RbgCanInsight.exe (no installation needed).

Folders
  config/   recent files and user settings (kept next to the app, so the
            folder can be copied to another PC with your preferences)
  logs/     application log files, one per day
  docs/     user guide
  runtime/  support files — nothing to launch in here

Requirements: Windows 10/11 x64. The .NET runtime is bundled; nothing to install.
"@ | Set-Content (Join-Path $releaseDir "README.txt") -Encoding UTF8

# Placeholders so the empty folders survive zip round-trips.
"RBG CAN Insight writes its settings here." | Set-Content (Join-Path $releaseDir "config\README.txt") -Encoding UTF8
"RBG CAN Insight writes its log files here." | Set-Content (Join-Path $releaseDir "logs\README.txt") -Encoding UTF8

Write-Host "Creating $zipPath..."
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path $releaseDir -DestinationPath $zipPath

Remove-Item $publishDir -Recurse -Force
Write-Host "Done."
Write-Host "  Folder: $releaseDir"
Write-Host "  Zip:    $zipPath"
Get-ChildItem $releaseDir | Select-Object Name, Length | Format-Table -AutoSize
