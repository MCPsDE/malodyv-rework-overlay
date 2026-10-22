param(
    [string]$Version = "1.3.17"
)

$ErrorActionPreference = "Stop"
$overlayRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$releaseRoot = (Resolve-Path (Join-Path $overlayRoot "release")).Path
$package = (Resolve-Path (Join-Path $releaseRoot "MalodyVReworkOverlay-$Version")).Path
$manifest = Get-Content -LiteralPath (Join-Path $package "manifest.json") -Raw | ConvertFrom-Json
$work = Join-Path $releaseRoot "verification-auto-$Version-$([Guid]::NewGuid().ToString('N'))"
$target = Join-Path $work "Game\BepInEx\plugins"
New-Item -ItemType Directory -Path $target -Force | Out-Null
Get-ChildItem -LiteralPath $package -Force | Copy-Item -Destination $target -Recurse -Force
$pluginPath = Join-Path $target "MalodyV.ReworkOverlay.Plugin.dll"
Set-Content -LiteralPath $pluginPath -Value "damaged plugin for automatic repair test"
$hostExe = Join-Path $target "MalodyVReworkOverlay\MalodyV.ReworkOverlay.exe"
$watcher = [IO.FileSystemWatcher]::new($target, "MalodyV.ReworkOverlay.Plugin.dll")
$watcher.NotifyFilter = [IO.NotifyFilters]::FileName
$watcher.EnableRaisingEvents = $true
$original = Start-Process -FilePath $hostExe -PassThru -WindowStyle Hidden
try {
    $changed = $watcher.WaitForChanged([IO.WatcherChangeTypes]::Created, 180000)
    if ($changed.TimedOut) { throw "Automatic repair did not install the published package within 180 seconds." }
    if ((Get-FileHash -LiteralPath $pluginPath -Algorithm SHA256).Hash -ne $manifest.pluginSha256) {
        throw "Automatically repaired plugin failed hash verification."
    }
    if (-not $original.WaitForExit(15000)) { throw "The original companion did not exit for replacement." }
    Start-Sleep -Seconds 3
    $restarted = @(Get-Process -Name "MalodyV.ReworkOverlay" -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $hostExe })
    if ($restarted.Count -ne 1 -or $restarted[0].Id -eq $original.Id) { throw "The repaired companion was not restarted." }
    Write-Host "PASS: startup detected corruption, downloaded the GitHub release, verified and installed it, and restarted without confirmation."
    Write-Host "Fixture: $work"
}
finally {
    $watcher.Dispose()
    Get-Process -Name "MalodyV.ReworkOverlay" -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -eq $hostExe } | Stop-Process -Force
}
