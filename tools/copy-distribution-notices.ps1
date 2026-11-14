param([Parameter(Mandatory)] [string]$Destination,
      [Parameter(Mandatory)] [string]$Configuration,
      [Parameter(Mandatory)] [string]$Runtime,
      [Parameter(Mandatory)] [string]$PublishDirectory)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$assetsPath = Join-Path $root 'src\RbgCanInsight.App\obj\project.assets.json'
$assets = Get-Content -LiteralPath $assetsPath -Raw | ConvertFrom-Json
$frameworks = $assets.project.frameworks.PSObject.Properties.Value
$downloads = @($frameworks | ForEach-Object { $_.downloadDependencies })
foreach ($name in @('Microsoft.NETCore.App', 'Microsoft.WindowsDesktop.App')) {
    $packEntries = @($downloads | Where-Object { $_.name -eq "$name.Runtime.$Runtime" })
    if ($packEntries.Count -ne 1 -or $packEntries[0].version -ne '[10.0.15, 10.0.15]') {
        throw "Runtime $name does not match the reviewed 10.0.15 notices. Review and update notices before packaging."
    }
}
$runtimeConfig = Join-Path $PublishDirectory 'RbgCanInsight.runtimeconfig.json'
if (Test-Path -LiteralPath $runtimeConfig) {
    $included = (Get-Content -LiteralPath $runtimeConfig -Raw | ConvertFrom-Json).runtimeOptions.includedFrameworks
    foreach ($name in @('Microsoft.NETCore.App', 'Microsoft.WindowsDesktop.App')) {
        if (@($included | Where-Object { $_.name -eq $name -and $_.version -eq '10.0.15' }).Count -ne 1) { throw "Unexpected published runtime: $name" }
    }
}
$target = Join-Path $Destination 'licenses'
New-Item -ItemType Directory -Force $target | Out-Null
foreach ($file in @('APPLICATION-TERMS.md', 'THIRD-PARTY-NOTICES.md')) {
    Copy-Item -LiteralPath (Join-Path $root $file) -Destination $target
}
Copy-Item -LiteralPath (Join-Path $root 'third-party\dotnet-10.0.15') -Destination $target -Recurse
