<#
.SYNOPSIS
Builds a local MSIX package for RBG CAN Insight.

.DESCRIPTION
Creates a clean win-x64 self-contained publish layout, generates a Package.appxmanifest
and MSIX image assets, creates a local test certificate, then packages and signs an .msix.

For Microsoft Store submission, use the identity reserved in Partner Center before
generating the final package.

.EXAMPLE
pwsh tools/make-msix.ps1

.EXAMPLE
pwsh tools/make-msix.ps1 -PackageName "PaoloRubagotti.RBG-CAN-Insight" -Publisher "CN=10C6FB18-8DDB-4E06-BA21-3E1B0217DE01"
#>
[CmdletBinding()]
param(
    [string]$DotnetExe = "dotnet",
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$OutputRoot = "",
    [string]$PackageVersionOverride = "",
    [string]$PackageName = "PaoloRubagotti.RBG-CAN-Insight",
    [string]$Publisher = "CN=10C6FB18-8DDB-4E06-BA21-3E1B0217DE01",
    [string]$PublisherDisplayName = "PaoloRubagotti",
    [string]$CertPassword = "password",
    [switch]$SkipSigning,
    [switch]$SkipPackage,
    # Thumbprint of a code-signing certificate in Cert:\CurrentUser\My. Builds a sideload package whose
    # manifest Publisher equals the certificate subject (required for signing). Not for Store upload.
    [string]$SignThumbprint = "",
    # Script that Authenticode-signs files (e.g. Documents\FirmaApp\Firma-file.ps1); signs our binaries after publish.
    [string]$SignScript = ""
)

$ErrorActionPreference = "Stop"
$signCert = $null
if (-not [string]::IsNullOrWhiteSpace($SignThumbprint)) {
    $signCert = Get-Item "Cert:\CurrentUser\My\$SignThumbprint" -ErrorAction Stop
    if (-not $signCert.HasPrivateKey) { throw "Certificate $SignThumbprint has no private key." }
    $Publisher = $signCert.Subject
    $PublisherDisplayName = ($Publisher -replace '^CN=([^,]+).*$', '$1')
}
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot "src\RbgCanInsight.App\RbgCanInsight.App.csproj"

[xml]$props = Get-Content (Join-Path $repoRoot "Directory.Build.props")
$version = $props.Project.PropertyGroup.Version | Select-Object -First 1
if (-not $version) { $version = "0.0.0" }
$packageVersion = if (($version -split '\.').Count -eq 3) { "$version.0" } else { $version }
if (-not [string]::IsNullOrWhiteSpace($PackageVersionOverride)) {
    if ($PackageVersionOverride -notmatch '^\d+\.\d+\.\d+\.\d+$') { throw 'PackageVersionOverride must have four numeric components.' }
    $packageVersion = $PackageVersionOverride
    $version = $PackageVersionOverride
}

$artifactRoot = if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    Join-Path $repoRoot "artifacts\msix"
} else {
    $OutputRoot
}

$layoutDir = Join-Path $artifactRoot "layout"
$assetDir = Join-Path $artifactRoot "asset-source"
$certPath = Join-Path $artifactRoot "RbgCanInsight-TestCert.pfx"
$msixSuffix = if ($signCert) { "_signed" } else { "" }
$msixPath = Join-Path $artifactRoot "RBG-CAN-Insight_${version}_${Runtime}${msixSuffix}.msix"
$sourceLogo = Join-Path $repoRoot "src\RbgCanInsight.App\Assets\RbgCanInsight.png"
$assetOutputDir = Join-Path $layoutDir "Assets"

New-Item -ItemType Directory -Force $artifactRoot | Out-Null
if (Test-Path $layoutDir) { Remove-Item $layoutDir -Recurse -Force }
if (Test-Path $assetDir) { Remove-Item $assetDir -Recurse -Force }
New-Item -ItemType Directory -Force $layoutDir, $assetDir | Out-Null

function Resolve-FirstTool {
    param(
        [string]$Name,
        [string]$SearchRoot
    )

    $command = Get-Command $Name -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }

    $tool = Get-ChildItem -Path $SearchRoot -Recurse -Filter $Name -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -like "*\x64\*" } |
        Sort-Object FullName -Descending |
        Select-Object -First 1
    if ($tool) { return $tool.FullName }

    throw "$Name was not found. Install the Windows SDK with MSIX Packaging Tools."
}

function ConvertTo-XmlAttribute {
    param([string]$Text)
    return [Security.SecurityElement]::Escape($Text)
}

function Write-PngAsset {
    param(
        [System.Windows.Media.Imaging.BitmapSource]$Source,
        [string]$Path,
        [int]$Size
    )

    $visual = New-Object System.Windows.Media.DrawingVisual
    $context = $visual.RenderOpen()
    $context.DrawRectangle([System.Windows.Media.Brushes]::Transparent, $null, (New-Object System.Windows.Rect 0, 0, $Size, $Size))
    $context.DrawImage($Source, (New-Object System.Windows.Rect 0, 0, $Size, $Size))
    $context.Close()

    $target = New-Object System.Windows.Media.Imaging.RenderTargetBitmap $Size, $Size, 96, 96, ([System.Windows.Media.PixelFormats]::Pbgra32)
    $target.Render($visual)

    $encoder = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($target))
    $outStream = [System.IO.File]::Create($Path)
    try { $encoder.Save($outStream) } finally { $outStream.Dispose() }
}

Write-Host "Cleaning Release outputs before publishing..."
& $DotnetExe clean (Join-Path $repoRoot "RbgCanInsight.sln") -c $Configuration -v quiet
if ($LASTEXITCODE -ne 0) { throw "dotnet clean failed with exit code $LASTEXITCODE" }

Write-Host "Publishing app layout..."
& $DotnetExe publish $project `
    -c $Configuration -r $Runtime --self-contained true `
    -p:RuntimeFrameworkVersion=10.0.15 `
    "-target:Rebuild;Publish" `
    -p:PublishSingleFile=false `
    -p:DebugType=none `
    -p:DebugSymbols=false `
    -o $layoutDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }
& (Join-Path $PSScriptRoot 'copy-distribution-notices.ps1') -Destination $layoutDir -Configuration $Configuration -Runtime $Runtime -PublishDirectory $layoutDir

if (-not [string]::IsNullOrWhiteSpace($SignScript)) {
    # Authenticode-sign our own binaries (not the Microsoft runtime files) before they are packaged.
    $own = Get-ChildItem $layoutDir -Recurse -File | Where-Object { $_.Name -like "RbgCanInsight*" -and $_.Extension -in ".exe", ".dll" }
    foreach ($file in $own) { & pwsh -NoProfile -File $SignScript -FilePath $file.FullName; if ($LASTEXITCODE -ne 0) { throw "Signing failed for $($file.Name) ($LASTEXITCODE)" } }
}

Write-Host "Preparing MSIX assets..."
Add-Type -AssemblyName PresentationCore
Add-Type -AssemblyName WindowsBase
$stream = [System.IO.File]::OpenRead($sourceLogo)
try {
    $decoder = [System.Windows.Media.Imaging.BitmapDecoder]::Create(
        $stream,
        [System.Windows.Media.Imaging.BitmapCreateOptions]::PreservePixelFormat,
        [System.Windows.Media.Imaging.BitmapCacheOption]::OnLoad)
    $source = $decoder.Frames[0]
} finally {
    $stream.Dispose()
}

New-Item -ItemType Directory -Force $assetOutputDir | Out-Null
Write-PngAsset -Source $source -Path (Join-Path $assetOutputDir "Square44x44Logo.png") -Size 44
Write-PngAsset -Source $source -Path (Join-Path $assetOutputDir "Square150x150Logo.png") -Size 150
Write-PngAsset -Source $source -Path (Join-Path $assetOutputDir "StoreLogo.png") -Size 50
Copy-Item -LiteralPath (Join-Path $repoRoot "src\RbgCanInsight.App\Assets\RbgCanInsight.ico") `
    -Destination (Join-Path $assetOutputDir "RbgCanInsight.ico") -Force

Write-Host "Generating AppxManifest.xml..."
$architecture = switch -Regex ($Runtime) {
    "arm64$" { "arm64"; break }
    "x86$" { "x86"; break }
    default { "x64" }
}
$description = "Offline automotive CAN and UDS trace analysis for DBC decoding, ISO-TP reconstruction, signal plotting and time-aligned diagnostic correlation."
$manifestPath = Join-Path $layoutDir "AppxManifest.xml"
$manifest = @"
<?xml version="1.0" encoding="utf-8"?>
<Package
  xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
  xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
  xmlns:desktop7="http://schemas.microsoft.com/appx/manifest/desktop/windows10/7"
  xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities"
  IgnorableNamespaces="uap desktop7 rescap">
  <Identity Name="$(ConvertTo-XmlAttribute $PackageName)" Publisher="$(ConvertTo-XmlAttribute $Publisher)" Version="$(ConvertTo-XmlAttribute $packageVersion)" ProcessorArchitecture="$architecture" />
  <Properties>
    <DisplayName>RBG-CAN-Insight</DisplayName>
    <PublisherDisplayName>$(ConvertTo-XmlAttribute $PublisherDisplayName)</PublisherDisplayName>
    <Logo>Assets\StoreLogo.png</Logo>
  </Properties>
  <Dependencies>
    <TargetDeviceFamily Name="Windows.Desktop" MinVersion="10.0.17763.0" MaxVersionTested="10.0.26100.0" />
  </Dependencies>
  <Resources>
    <Resource Language="en-us" />
  </Resources>
  <Applications>
    <Application Id="App" Executable="RbgCanInsight.exe" EntryPoint="Windows.FullTrustApplication">
      <uap:VisualElements DisplayName="RBG-CAN-Insight" Description="$(ConvertTo-XmlAttribute $description)" BackgroundColor="transparent" Square150x150Logo="Assets\Square150x150Logo.png" Square44x44Logo="Assets\Square44x44Logo.png" />
      <Extensions>
        <desktop7:Extension Category="windows.shortcut" Executable="RbgCanInsight.exe" EntryPoint="Windows.FullTrustApplication">
          <desktop7:Shortcut File="`$(Desktop)\RBG CAN Insight.lnk" Icon="Assets\RbgCanInsight.ico" Description="$(ConvertTo-XmlAttribute $description)" />
        </desktop7:Extension>
      </Extensions>
    </Application>
  </Applications>
  <Capabilities>
    <rescap:Capability Name="runFullTrust" />
  </Capabilities>
</Package>
"@
$manifest | Set-Content $manifestPath -Encoding UTF8

if ($SkipPackage) {
    Write-Host "SkipPackage requested. Layout is ready:"
    Write-Host "  $layoutDir"
    return
}

Write-Host "Creating MSIX..."
if (Test-Path $msixPath) { Remove-Item $msixPath -Force }
$makeAppxExe = Resolve-FirstTool -Name "makeappx.exe" -SearchRoot "${env:ProgramFiles(x86)}\Windows Kits\10\bin"
& $makeAppxExe pack /d $layoutDir /p $msixPath /o
if ($LASTEXITCODE -ne 0) { throw "makeappx pack failed with exit code $LASTEXITCODE" }

if ($signCert) {
    Write-Host "Signing MSIX with $($signCert.Subject)..."
    $signToolExe = Resolve-FirstTool -Name "signtool.exe" -SearchRoot "${env:ProgramFiles(x86)}\Windows Kits\10\bin"
    & $signToolExe sign /fd SHA256 /sha1 $SignThumbprint $msixPath
    if ($LASTEXITCODE -ne 0) { throw "signtool sign failed with exit code $LASTEXITCODE" }
} elseif (-not $SkipSigning) {
    Write-Host "Generating local test certificate..."
    if (Test-Path $certPath) { Remove-Item $certPath -Force }
    $certificate = New-SelfSignedCertificate `
        -Type Custom `
        -Subject $Publisher `
        -KeyUsage DigitalSignature `
        -FriendlyName "RBG CAN Insight Test Certificate" `
        -CertStoreLocation "Cert:\CurrentUser\My" `
        -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3")
    $securePassword = ConvertTo-SecureString $CertPassword -AsPlainText -Force
    Export-PfxCertificate -Cert $certificate -FilePath $certPath -Password $securePassword | Out-Null

    Write-Host "Signing MSIX..."
    $signToolExe = Resolve-FirstTool -Name "signtool.exe" -SearchRoot "${env:ProgramFiles(x86)}\Windows Kits\10\bin"
    & $signToolExe sign /fd SHA256 /f $certPath /p $CertPassword $msixPath
    if ($LASTEXITCODE -ne 0) { throw "signtool sign failed with exit code $LASTEXITCODE" }
}

$hash = (Get-FileHash $msixPath -Algorithm SHA256).Hash
Write-Host "Done."
Write-Host "  MSIX: $msixPath"
if (-not $SkipSigning -and -not $signCert) { Write-Host "  Cert: $certPath" }
Write-Host "  SHA256: $hash"
