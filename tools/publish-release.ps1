# Build the same standalone single-file assets as .github/workflows/release.yml.
#
# Usage:
#   .\tools\publish-release.ps1
#   .\tools\publish-release.ps1 -Version 0.2.0 -Rids win-x64
#
# The output is intentionally ignored by git. Publishing linux-x64 from Windows is supported
# by the .NET SDK; CI still builds each RID on its native runner.
param(
    [string]$Version = "0.1.0",
    [string]$Out = "artifacts/release",
    [string[]]$Rids = @("win-x64", "linux-x64")
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$output = if ([IO.Path]::IsPathRooted($Out)) { $Out } else { Join-Path $root $Out }

if ($Version -notmatch '^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?$') {
    throw "Version must be semver without the leading v, got '$Version'"
}

New-Item -ItemType Directory -Force -Path $output | Out-Null

# Use the in-tree restore caches when present; this checkout may run on a host whose home
# directory is read-only. An explicitly set environment variable always wins.
if (-not $env:NUGET_PACKAGES -and (Test-Path (Join-Path $root ".packages"))) {
    $env:NUGET_PACKAGES = Join-Path $root ".packages"
}
if (-not $env:NUGET_HTTP_CACHE_PATH -and (Test-Path (Join-Path $root ".httpcache"))) {
    $env:NUGET_HTTP_CACHE_PATH = Join-Path $root ".httpcache"
}

foreach ($rid in $Rids) {
    $ext = ""
    if ($rid -like "win-*") {
        $ext = ".exe"
    } elseif ($rid -notlike "linux-*") {
        throw "Unsupported RID '$rid' (expected linux-* or win-*)"
    }
    $publishDir = Join-Path $output "publish-$rid"
    if (Test-Path $publishDir) { Remove-Item -Recurse -Force $publishDir }
    New-Item -ItemType Directory -Force -Path $publishDir | Out-Null

    $publishArgs = @(
        "publish",
        (Join-Path $root "VSSaveEditor/VSSaveEditor.csproj"),
        "-c", "Release",
        "-r", $rid,
        "--self-contained", "true",
        "-p:PublishSingleFile=true",
        "-p:IncludeNativeLibrariesForSelfExtract=true",
        "-p:EnableCompressionInSingleFile=true",
        "-p:PublishTrimmed=true",
        "-p:DebugType=None",
        "-p:DebugSymbols=false",
        "-p:Version=$Version",
        "-p:ContinuousIntegrationBuild=true",
        "-o", $publishDir
    )
    & dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $rid" }

    $files = @(Get-ChildItem -LiteralPath $publishDir -File)
    if ($files.Count -ne 1) {
        throw "Expected exactly one published file in $publishDir, got $($files.Count): $(($files | ForEach-Object Name) -join ', ')"
    }

    $expected = "VSSaveEditor$ext"
    $binary = $files[0]
    if ($binary.Name -ne $expected) {
        throw "Unexpected published file '$($binary.Name)' (expected '$expected')"
    }
    if ($binary.Length -lt (20 * 1024 * 1024)) {
        throw "Published file is unexpectedly small: $($binary.Length) bytes"
    }

    $bytes = [IO.File]::ReadAllBytes($binary.FullName)
    if ($rid -like "linux-*") {
        if ($bytes[0] -ne 0x7f -or $bytes[1] -ne 0x45 -or $bytes[2] -ne 0x4c -or $bytes[3] -ne 0x46) {
            throw "Not an ELF executable: $($binary.FullName)"
        }
    } elseif ($bytes[0] -ne 0x4d -or $bytes[1] -ne 0x5a) {
        throw "Not a PE executable: $($binary.FullName)"
    }

    $assetName = "VSSaveEditor-$Version-$rid$ext"
    $asset = Join-Path $output $assetName
    Copy-Item -LiteralPath $binary.FullName -Destination $asset -Force
    if ($rid -like "linux-*" -and $IsLinux) {
        & chmod +x $asset
    }

    $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $asset).Hash.ToLowerInvariant()
    Set-Content -LiteralPath "$asset.sha256" -Value "$hash  $assetName" -NoNewline
    Write-Output "Published $assetName ($($binary.Length) bytes, sha256 $hash)"
}
