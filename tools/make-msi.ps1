<#
.SYNOPSIS
Builds a local MSI installer for RBG CAN Insight.

.DESCRIPTION
Publishes the WPF app as a self-contained win-x64 application, generates a WiX
source file for the publish layout, and builds a per-machine MSI installer.

The WiX command-line tool is installed locally under artifacts\.tools\wix if it
is not already present, so no global machine setup is required.

.EXAMPLE
pwsh tools\make-msi.ps1
#>
[CmdletBinding()]
param(
    [string]$DotnetExe = "dotnet",
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$OutputRoot = "",
    [string]$WixVersion = "7.0.0",
    # Use an already installed WiX CLI (e.g. WiX 6) instead of installing WiX 7 locally.
    [string]$WixCli = "",
    # Thumbprint of a code-signing certificate in Cert:\CurrentUser\My; signs the MSI when set.
    [string]$SignThumbprint = "",
    # Script that Authenticode-signs files (e.g. Documents\FirmaApp\Firma-file.ps1); signs our binaries after publish.
    [string]$SignScript = ""
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot "src\RbgCanInsight.App\RbgCanInsight.App.csproj"

[xml]$props = Get-Content (Join-Path $repoRoot "Directory.Build.props")
$version = $props.Project.PropertyGroup.Version | Select-Object -First 1
if (-not $version) { $version = "0.0.0" }

$artifactRoot = if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    Join-Path $repoRoot "artifacts\msi"
} else {
    $OutputRoot
}

$publishDir = Join-Path $artifactRoot "publish"
$buildDir = Join-Path $artifactRoot "build"
$wixSource = Join-Path $buildDir "RbgCanInsight.wxs"
$msiPath = Join-Path $artifactRoot "RBG-CAN-Insight_${version}_Setup.msi"
$shaPath = "$msiPath.sha256"
$wixDir = Join-Path $repoRoot "artifacts\.tools\wix"
$wixExe = Join-Path $wixDir "wix.exe"
$appIcon = Join-Path $repoRoot "src\RbgCanInsight.App\Assets\RbgCanInsight.ico"

New-Item -ItemType Directory -Force $artifactRoot, $buildDir | Out-Null

if (-not [string]::IsNullOrWhiteSpace($WixCli)) {
    # An explicitly supplied WiX CLI is used as-is; no EULA is accepted on the user's behalf.
    if (-not (Test-Path $WixCli)) { throw "WiX CLI not found: $WixCli" }
    $wixExe = $WixCli
} else {
    if (-not (Test-Path $wixExe)) {
        Write-Host "Installing WiX Toolset $WixVersion locally..."
        & $DotnetExe tool install wix --version $WixVersion --tool-path $wixDir
        if ($LASTEXITCODE -ne 0) { throw "dotnet tool install wix failed with exit code $LASTEXITCODE" }
    }

    Write-Host "Review and accept any applicable WiX license terms yourself before running this build."
}

Write-Host "Publishing $project ($Configuration, $Runtime, self-contained)..."
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
& $DotnetExe publish $project `
    -c $Configuration -r $Runtime --self-contained true `
    -p:PublishSingleFile=false `
    -p:DebugType=embedded `
    -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

if (-not [string]::IsNullOrWhiteSpace($SignScript)) {
    # Authenticode-sign our own binaries (not the Microsoft runtime files) before they are packaged.
    $own = Get-ChildItem $publishDir -Recurse -File | Where-Object { $_.Name -like "RbgCanInsight*" -and $_.Extension -in ".exe", ".dll" }
    foreach ($file in $own) { & pwsh -NoProfile -File $SignScript -FilePath $file.FullName; if ($LASTEXITCODE -ne 0) { throw "Signing failed for $($file.Name) ($LASTEXITCODE)" } }
}

function ConvertTo-WixId {
    param([string]$Text)
    $clean = [Regex]::Replace($Text, '[^A-Za-z0-9_\.]', '_')
    if ($clean.Length -eq 0 -or $clean[0] -notmatch '[A-Za-z_]') {
        $clean = "I_$clean"
    }
    if ($clean.Length -gt 60) {
        $hash = [BitConverter]::ToString(
            [System.Security.Cryptography.SHA1]::HashData([Text.Encoding]::UTF8.GetBytes($Text))
        ).Replace("-", "").Substring(0, 10)
        $clean = $clean.Substring(0, 49) + "_" + $hash
    }
    return $clean
}

function ConvertTo-XmlAttribute {
    param([string]$Text)
    return [Security.SecurityElement]::Escape($Text)
}

$directories = [ordered]@{
    "" = "INSTALLFOLDER"
}
$directoryBuilder = [Text.StringBuilder]::new()
$componentBuilder = [Text.StringBuilder]::new()
$componentRefs = [Text.StringBuilder]::new()

Get-ChildItem $publishDir -Directory -Recurse | Sort-Object FullName | ForEach-Object {
    $relative = [IO.Path]::GetRelativePath($publishDir, $_.FullName).Replace('\', '/')
    $parentRelative = Split-Path $relative -Parent
    if ($parentRelative -eq ".") { $parentRelative = "" }
    $id = "DIR_" + (ConvertTo-WixId $relative)
    $directories[$relative] = $id
}

function Write-DirectoryNode {
    param(
        [string]$RelativePath,
        [int]$Indent
    )

    $children = @($directories.Keys | Where-Object {
        $_ -ne "" -and ((Split-Path $_ -Parent) -replace '\\','/') -eq $RelativePath
    } | Sort-Object)

    foreach ($child in $children) {
        $dirId = $directories[$child]
        $name = Split-Path $child -Leaf
        [void]$directoryBuilder.AppendLine((" " * $Indent) + "<Directory Id=`"$dirId`" Name=`"$(ConvertTo-XmlAttribute $name)`">")
        Write-DirectoryNode -RelativePath $child -Indent ($Indent + 2)
        [void]$directoryBuilder.AppendLine((" " * $Indent) + "</Directory>")
    }
}

Write-DirectoryNode -RelativePath "" -Indent 10

$fileIndex = 0
Get-ChildItem $publishDir -File -Recurse | Sort-Object FullName | ForEach-Object {
    $fileIndex++
    $relative = [IO.Path]::GetRelativePath($publishDir, $_.FullName).Replace('\', '/')
    $dirRelative = Split-Path $relative -Parent
    if ($dirRelative -eq ".") { $dirRelative = "" }
    $dirId = $directories[$dirRelative]
    $componentId = "CMP_$fileIndex"
    $fileId = "FIL_$fileIndex"
    $source = ConvertTo-XmlAttribute $_.FullName
    $name = ConvertTo-XmlAttribute $_.Name

    [void]$componentBuilder.AppendLine("      <Component Id=`"$componentId`" Directory=`"$dirId`" Guid=`"*`">")
    [void]$componentBuilder.AppendLine("        <File Id=`"$fileId`" Source=`"$source`" Name=`"$name`" KeyPath=`"yes`" />")
    [void]$componentBuilder.AppendLine("      </Component>")
    [void]$componentRefs.AppendLine("      <ComponentRef Id=`"$componentId`" />")
}

$upgradeCode = "8f08d72e-880b-4e9a-b2b9-8ac0aa70b09a"
$productName = "RBG CAN Insight"
$manufacturer = "RBG"
$description = "Offline automotive CAN trace analysis studio."

$wxs = @"
<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs">
  <Package
      Name="$productName"
      Manufacturer="$manufacturer"
      Version="$version"
      UpgradeCode="$upgradeCode"
      Scope="perMachine">
    <SummaryInformation Description="$description" Manufacturer="$manufacturer" />
    <MajorUpgrade DowngradeErrorMessage="A newer version of RBG CAN Insight is already installed." />
    <MediaTemplate EmbedCab="yes" />
    <Icon Id="AppIcon" SourceFile="$(ConvertTo-XmlAttribute $appIcon)" />
    <Property Id="ARPPRODUCTICON" Value="AppIcon" />

    <StandardDirectory Id="ProgramFiles64Folder">
      <Directory Id="ManufacturerFolder" Name="RBG">
        <Directory Id="INSTALLFOLDER" Name="RBG CAN Insight">
$directoryBuilder        </Directory>
      </Directory>
    </StandardDirectory>

    <StandardDirectory Id="ProgramMenuFolder">
      <Directory Id="ApplicationProgramsFolder" Name="RBG CAN Insight" />
    </StandardDirectory>

    <Component Id="StartMenuShortcut" Directory="ApplicationProgramsFolder" Guid="*">
      <Shortcut
          Id="ApplicationStartMenuShortcut"
          Name="RBG CAN Insight"
          Description="$description"
          Target="[INSTALLFOLDER]RbgCanInsight.exe"
          WorkingDirectory="INSTALLFOLDER"
          Icon="AppIcon" />
      <RemoveFolder Id="ApplicationProgramsFolder" On="uninstall" />
      <RegistryValue Root="HKCU" Key="Software\RBG\RBG CAN Insight" Name="Installed" Type="integer" Value="1" KeyPath="yes" />
    </Component>

    <StandardDirectory Id="DesktopFolder" />

    <Component Id="DesktopShortcut" Directory="DesktopFolder" Guid="*">
      <Shortcut
          Id="ApplicationDesktopShortcut"
          Name="RBG CAN Insight"
          Description="$description"
          Target="[INSTALLFOLDER]RbgCanInsight.exe"
          WorkingDirectory="INSTALLFOLDER"
          Icon="AppIcon" />
      <RegistryValue Root="HKCU" Key="Software\RBG\RBG CAN Insight" Name="DesktopShortcut" Type="integer" Value="1" KeyPath="yes" />
    </Component>

    <Feature Id="MainFeature" Title="RBG CAN Insight" Level="1">
      <ComponentRef Id="StartMenuShortcut" />
      <ComponentRef Id="DesktopShortcut" />
$componentRefs    </Feature>

    <ComponentGroup Id="PublishedFiles">
$componentBuilder    </ComponentGroup>
  </Package>
</Wix>
"@

$wxs | Set-Content $wixSource -Encoding UTF8

Write-Host "Building MSI..."
if (Test-Path $msiPath) { Remove-Item $msiPath -Force }
& $wixExe build $wixSource -arch x64 -out $msiPath -pdbtype none
if ($LASTEXITCODE -ne 0) { throw "wix build failed with exit code $LASTEXITCODE" }

if (-not [string]::IsNullOrWhiteSpace($SignThumbprint)) {
    $cert = Get-Item "Cert:\CurrentUser\My\$SignThumbprint" -ErrorAction Stop
    if (-not $cert.HasPrivateKey) { throw "Certificate $SignThumbprint has no private key." }
    $signTool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -like "*\x64\*" } | Sort-Object FullName -Descending | Select-Object -First 1
    if (-not $signTool) { throw "signtool.exe not found. Install the Windows SDK." }
    Write-Host "Signing MSI with $($cert.Subject)..."
    & $signTool.FullName sign /fd SHA256 /sha1 $SignThumbprint /d "RBG CAN Insight" $msiPath
    if ($LASTEXITCODE -ne 0) { throw "signtool sign failed with exit code $LASTEXITCODE" }
}

$hash = (Get-FileHash $msiPath -Algorithm SHA256).Hash
"$hash  $(Split-Path $msiPath -Leaf)" | Set-Content $shaPath -Encoding ASCII

Write-Host "Done."
Write-Host "  MSI:    $msiPath"
Write-Host "  SHA256: $hash"
