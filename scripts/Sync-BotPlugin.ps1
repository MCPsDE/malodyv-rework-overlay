param(
    [Parameter(Mandatory = $true)]
    [string]$SourcePath
)

$ErrorActionPreference = "Stop"
$overlayRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$source = (Resolve-Path $SourcePath).Path
if (-not (Test-Path -LiteralPath (Join-Path $source "__init__.py"))) {
    throw "SourcePath must point to the nonebot_plugin_rework directory."
}

$destination = Join-Path $overlayRoot "nonebot_plugin_rework"
$stage = Join-Path $overlayRoot ".nonebot_plugin_rework-stage-$([Guid]::NewGuid().ToString('N'))"
$backup = Join-Path $overlayRoot ".nonebot_plugin_rework-backup-$([Guid]::NewGuid().ToString('N'))"
$destinationResolved = [IO.Path]::GetFullPath($destination)
$allowedPrefix = $overlayRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $destinationResolved.StartsWith($allowedPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Bundled Bot directory escapes the overlay repository."
}

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

try {
    New-Item -ItemType Directory -Path $stage | Out-Null
    robocopy $source $stage /E /XD __pycache__ /XF *.pyc /NFL /NDL /NJH /NJS /NC /NS
    if ($LASTEXITCODE -ge 8) {
        throw "Bot plugin copy failed with robocopy exit code $LASTEXITCODE."
    }

    $sourceHash = Get-DirectorySha256 $source
    $stageHash = Get-DirectorySha256 $stage
    if ($sourceHash -ne $stageHash) {
        throw "Staged Bot plugin hash differs from source."
    }

    $movedExisting = $false
    try {
        if (Test-Path -LiteralPath $destinationResolved) {
            Move-Item -LiteralPath $destinationResolved -Destination $backup
            $movedExisting = $true
        }
        Move-Item -LiteralPath $stage -Destination $destinationResolved
        if ($movedExisting) {
            Remove-Item -LiteralPath $backup -Recurse -Force
        }
    }
    catch {
        if (Test-Path -LiteralPath $destinationResolved) {
            Remove-Item -LiteralPath $destinationResolved -Recurse -Force
        }
        if ($movedExisting -and (Test-Path -LiteralPath $backup)) {
            Move-Item -LiteralPath $backup -Destination $destinationResolved
        }
        throw
    }

    Write-Host "Bundled nonebot_plugin_rework synchronized. SHA-256: $sourceHash"
}
finally {
    if (Test-Path -LiteralPath $stage) {
        Remove-Item -LiteralPath $stage -Recurse -Force
    }
}
