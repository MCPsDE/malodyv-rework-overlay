param(
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version = "1.3.17"
)

$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$releaseRoot = (Resolve-Path (Join-Path $root "release")).Path
$sourceDir = (Resolve-Path (Join-Path $releaseRoot "MalodyVReworkOverlay-$Version")).Path
$hostDir = (Resolve-Path (Join-Path $sourceDir "MalodyVReworkOverlay")).Path
$pluginPath = Join-Path $sourceDir "MalodyV.ReworkOverlay.Plugin.dll"
$manifestPath = Join-Path $sourceDir "manifest.json"
$zipPath = Join-Path $releaseRoot "MalodyVReworkOverlay-$Version.zip"
$sumPath = Join-Path $releaseRoot "SHA256SUMS-Overlay-$Version.txt"
$stageRoot = Join-Path $releaseRoot "_stage-overlay-$Version"
$stagePackage = Join-Path $stageRoot "MalodyVReworkOverlay"
$releasePrefix = $releaseRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$stageRoot = [IO.Path]::GetFullPath($stageRoot)
if (-not $stageRoot.StartsWith($releasePrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Staging path escapes the release directory: $stageRoot"
}

if (Test-Path -LiteralPath $stageRoot) {
    Remove-Item -LiteralPath $stageRoot -Recurse -Force
}
if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}

New-Item -ItemType Directory -Force -Path $stagePackage | Out-Null
Get-ChildItem -LiteralPath $hostDir -Force | Copy-Item -Destination $stagePackage -Recurse -Force
Copy-Item -LiteralPath $pluginPath -Destination $stageRoot -Force
Copy-Item -LiteralPath $manifestPath -Destination $stageRoot -Force
$packagedManifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -AsHashtable
$packagedManifest.Remove('packageSha256') | Out-Null
[IO.File]::WriteAllText((Join-Path $stageRoot "manifest.json"), ($packagedManifest | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
Compress-Archive -Path (Join-Path $stageRoot "*") -DestinationPath $zipPath -CompressionLevel Optimal

$hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToUpperInvariant()
[IO.File]::WriteAllText($sumPath, "$hash  MalodyVReworkOverlay-$Version.zip`n", [Text.UTF8Encoding]::new($false))
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -AsHashtable
$manifest.packageSha256 = $hash
$manifestJson = $manifest | ConvertTo-Json
[IO.File]::WriteAllText((Join-Path $root "manifest.json"), $manifestJson, [Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText($manifestPath, $manifestJson, [Text.UTF8Encoding]::new($false))

$releasePrefix = $releaseRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$stageResolved = (Resolve-Path $stageRoot).Path
if (-not $stageResolved.StartsWith($releasePrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Staging path escapes the release directory: $stageResolved"
}
Remove-Item -LiteralPath $stageResolved -Recurse -Force

Write-Host "ZIP: $zipPath"
Write-Host "SHA256: $hash"
