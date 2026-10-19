param(
    [string]$BotPluginSource = ""
)

$ErrorActionPreference = "Stop"

$root = (Resolve-Path $PSScriptRoot).Path
$release = Join-Path $root "release\MalodyVReworkOverlay"
$releaseParent = [IO.Path]::GetDirectoryName($release)
$hostProject = Join-Path $root "host\MalodyV.ReworkOverlay.Host.csproj"
$pluginProject = Join-Path $root "plugin\MalodyV.ReworkOverlay.Plugin.csproj"
$bundledBotPlugin = Join-Path $root "nonebot_plugin_rework"

if ([string]::IsNullOrWhiteSpace($BotPluginSource)) {
    $candidate = Join-Path $root "..\..\nonebot_plugin_rework"
    if (Test-Path -LiteralPath $candidate) {
        $BotPluginSource = (Resolve-Path $candidate).Path
    }
}

if (-not [string]::IsNullOrWhiteSpace($BotPluginSource)) {
    & (Join-Path $root "scripts\Sync-BotPlugin.ps1") -SourcePath $BotPluginSource
}

if (-not (Test-Path -LiteralPath (Join-Path $bundledBotPlugin "tosu_plugin\index.html"))) {
    throw "The bundled nonebot_plugin_rework/tosu_plugin directory is missing."
}

$releaseResolved = [IO.Path]::GetFullPath($release)
$allowedReleaseParent = [IO.Path]::GetFullPath($releaseParent) + [IO.Path]::DirectorySeparatorChar
if (-not $releaseResolved.StartsWith($allowedReleaseParent, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Release directory escapes the overlay repository: $releaseResolved"
}
if (Test-Path -LiteralPath $releaseResolved) {
    Remove-Item -LiteralPath $releaseResolved -Recurse -Force
}
New-Item -ItemType Directory -Force $releaseResolved | Out-Null

dotnet publish $hostProject --configuration Release --runtime win-x64 --self-contained true `
    -p:PublishSingleFile=false -p:PublishTrimmed=false -o $releaseResolved
if ($LASTEXITCODE -ne 0) {
    throw "Host publish failed with exit code $LASTEXITCODE."
}

dotnet build $pluginProject --configuration Release
if ($LASTEXITCODE -ne 0) {
    throw "BepInEx plugin build failed with exit code $LASTEXITCODE."
}

$pluginDll = Join-Path $root "plugin\bin\Release\net6.0\MalodyV.ReworkOverlay.Plugin.dll"
Copy-Item -LiteralPath $pluginDll -Destination (Join-Path $releaseResolved "MalodyV.ReworkOverlay.Plugin.dll") -Force

function Get-DirectorySha256([string]$Directory) {
    $basePath = [IO.Path]::GetFullPath($Directory).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $relativePaths = [string[]]@(Get-ChildItem -LiteralPath $Directory -File -Recurse |
        Where-Object { $_.FullName -notmatch '[\\/]__pycache__([\\/]|$)' -and $_.Extension -ne ".pyc" } |
        ForEach-Object { $_.FullName.Substring($basePath.Length).Replace('\', '/') })
    [Array]::Sort($relativePaths, [StringComparer]::Ordinal)

    $hash = [Security.Cryptography.IncrementalHash]::CreateHash([Security.Cryptography.HashAlgorithmName]::SHA256)
    $separator = [byte[]]@(0)
    $buffer = [byte[]]::new(131072)
    try {
        foreach ($relativePath in $relativePaths) {
            $hash.AppendData([Text.Encoding]::UTF8.GetBytes($relativePath))
            $hash.AppendData($separator)
            $filePath = Join-Path $Directory $relativePath.Replace('/', [IO.Path]::DirectorySeparatorChar)
            $stream = [IO.File]::OpenRead($filePath)
            try {
                while (($read = $stream.Read($buffer, 0, $buffer.Length)) -gt 0) {
                    $hash.AppendData($buffer, 0, $read)
                }
            }
            finally {
                $stream.Dispose()
            }
            $hash.AppendData($separator)
        }
        return [Convert]::ToHexString($hash.GetHashAndReset())
    }
    finally {
        $hash.Dispose()
    }
}

$localBotHash = Get-DirectorySha256 $bundledBotPlugin
$publishedBotPlugin = Join-Path $releaseResolved "nonebot_plugin_rework"
if (-not (Test-Path -LiteralPath (Join-Path $publishedBotPlugin "tosu_plugin\index.html"))) {
    throw "Publish output does not contain the complete nonebot_plugin_rework directory."
}
$publishedBotHash = Get-DirectorySha256 $publishedBotPlugin
if ($publishedBotHash -ne $localBotHash) {
    throw "Published Bot plugin directory does not match its bundled source."
}

$pluginHash = (Get-FileHash -LiteralPath (Join-Path $releaseResolved "MalodyV.ReworkOverlay.Plugin.dll") -Algorithm SHA256).Hash
$hostHash = (Get-FileHash -LiteralPath (Join-Path $releaseResolved "MalodyV.ReworkOverlay.exe") -Algorithm SHA256).Hash
$hostVersion = ([xml](Get-Content -LiteralPath $hostProject -Raw)).Project.PropertyGroup.Version | Select-Object -First 1
$manifest = [ordered]@{
    version = $hostVersion
    pluginSha256 = $pluginHash
    hostSha256 = $hostHash
    botDirectorySha256 = $localBotHash
    downloadUrl = "https://github.com/MCPsDE/malodyv-rework-overlay"
}
$manifestPath = Join-Path $root "manifest.json"
[IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
Copy-Item -LiteralPath $manifestPath -Destination (Join-Path $releaseResolved "manifest.json") -Force

Write-Host "Release: $releaseResolved"
Write-Host "Bot directory SHA-256: $localBotHash"
Write-Host "Plugin SHA-256: $pluginHash"
Write-Host "Host SHA-256: $hostHash"
