param(
    [string]$Version = "1.3.17"
)

$ErrorActionPreference = "Stop"
$overlayRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$releaseRoot = (Resolve-Path (Join-Path $overlayRoot "release")).Path
$package = (Resolve-Path (Join-Path $releaseRoot "MalodyVReworkOverlay-$Version")).Path
$work = Join-Path $releaseRoot "verification-installer-$Version-$([Guid]::NewGuid().ToString('N'))"
$target = Join-Path $work "Game\BepInEx\plugins"
$hostDirectory = Join-Path $target "MalodyVReworkOverlay"
New-Item -ItemType Directory -Path $hostDirectory -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $target "OtherPlugin") -Force | Out-Null
Set-Content -LiteralPath (Join-Path $hostDirectory "old-version.txt") -Value "old host"
Set-Content -LiteralPath (Join-Path $target "MalodyV.ReworkOverlay.Plugin.dll") -Value "old plugin"
Set-Content -LiteralPath (Join-Path $target "manifest.json") -Value "old manifest"
Set-Content -LiteralPath (Join-Path $target "OtherPlugin\keep.dll") -Value "unrelated plugin"
$manifest = Get-Content -LiteralPath (Join-Path $package "manifest.json") -Raw | ConvertFrom-Json
$plan = @{ PluginRoot = $target; Manifest = $manifest }
$planPath = Join-Path $work "plan.json"
$plan | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $planPath -Encoding utf8NoBOM
$updater = Join-Path $package "MalodyVReworkOverlay\MalodyV.ReworkOverlay.exe"
$renamedUpdater = Join-Path $package "MalodyVReworkOverlay\MalodyV.ReworkOverlay.Update.exe"
Copy-Item -LiteralPath $updater -Destination $renamedUpdater -Force
$process = Start-Process -FilePath $renamedUpdater -ArgumentList @("--apply-update", ('"{0}"' -f $planPath)) -PassThru -WindowStyle Hidden
if (-not $process.WaitForExit(60000)) { throw "Staged updater did not exit within 60 seconds." }
if ($process.ExitCode -ne 0) { throw "Staged updater exited with code $($process.ExitCode)." }
Remove-Item -LiteralPath $renamedUpdater -Force
$installedHash = (Get-FileHash -LiteralPath (Join-Path $target "MalodyV.ReworkOverlay.Plugin.dll") -Algorithm SHA256).Hash
if ($installedHash -ne $manifest.pluginSha256) { throw "Installed plugin hash mismatch." }
if ((Get-FileHash -LiteralPath (Join-Path $hostDirectory "MalodyV.ReworkOverlay.dll") -Algorithm SHA256).Hash -ne $manifest.hostAssemblySha256) {
    throw "Installed managed host hash mismatch."
}
if (Test-Path -LiteralPath (Join-Path $hostDirectory "old-version.txt")) { throw "Old host was not replaced." }
if ((Get-Content -LiteralPath (Join-Path $target "OtherPlugin\keep.dll")).Trim() -ne "unrelated plugin") { throw "Other plugin was modified." }
Start-Sleep -Seconds 3
$hostExe = Join-Path $hostDirectory "MalodyV.ReworkOverlay.exe"
$restarted = @(Get-Process -Name "MalodyV.ReworkOverlay" -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $hostExe })
if ($restarted.Count -ne 1) { throw "Updated companion was not restarted." }
$restarted | Stop-Process -Force
$restarted | Wait-Process -Timeout 15 -ErrorAction SilentlyContinue
Write-Host "PASS: real staged updater replaced old files, preserved another plugin, verified hashes, and restarted the companion."
Write-Host "Fixture: $work"
