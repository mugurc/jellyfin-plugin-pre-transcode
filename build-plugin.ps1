<#
.SYNOPSIS
    Builds the plugin in Release, packages it into an installable, checksummed zip, and prints the
    manifest.json values to publish for the custom Jellyfin plugin repository.

.EXAMPLE
    ./build-plugin.ps1 -Version 0.1.0.0
#>
param(
    [string]$Version = "0.4.0.0"
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$guid = "8da245f6-7244-449b-9f32-46043f34b5f0"
$proj = Join-Path $root "Jellyfin.Plugin.PreTranscode\Jellyfin.Plugin.PreTranscode.csproj"
$artifacts = Join-Path $root "artifacts"
$stage = Join-Path $artifacts "Pre-Transcode_$Version"
$dll = Join-Path $root "Jellyfin.Plugin.PreTranscode\bin\Release\net9.0\Jellyfin.Plugin.PreTranscode.dll"

Write-Host "Building $Version..." -ForegroundColor Cyan
dotnet build $proj -c Release -p:Version=$Version -p:AssemblyVersion=$Version -p:FileVersion=$Version
if ($LASTEXITCODE -ne 0) { throw "Build failed." }

Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $stage | Out-Null
Copy-Item $dll $stage

$timestamp = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
$meta = [ordered]@{
    category   = "General"
    changelog  = "Release $Version"
    description = "Pre-transcode media to an admin-defined compatibility baseline."
    guid       = $guid
    name       = "Pre-Transcode"
    overview   = "Pre-transcode media to an admin-defined compatibility baseline."
    owner      = "mugurc"
    targetAbi  = "10.11.0.0"
    timestamp  = $timestamp
    version    = $Version
    status     = "Active"
    autoUpdate = $false
}
$meta | ConvertTo-Json | Set-Content -Path (Join-Path $stage "meta.json") -Encoding UTF8

$zip = Join-Path $artifacts "pre-transcode-$Version.zip"
Compress-Archive -Path (Join-Path $stage "*") -DestinationPath $zip -Force
$md5 = (Get-FileHash $zip -Algorithm MD5).Hash.ToLower()

Write-Host ""
Write-Host "Package : $zip" -ForegroundColor Green
Write-Host "MD5     : $md5" -ForegroundColor Green
Write-Host ""
Write-Host "For manifest.json, set this version's checksum to the MD5 above, the sourceUrl to the"
Write-Host "GitHub release download URL, and timestamp to: $timestamp"
