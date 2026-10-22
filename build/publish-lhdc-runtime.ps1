# SPDX-License-Identifier: GPL-3.0-only
[CmdletBinding()]
param([string]$PowerShellDirectory, [string]$Version, [string]$OutputDirectory, [switch]$Publish)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $Version) { $Version = [regex]::Match((Get-Content (Join-Path $root 'Directory.Build.props') -Raw), '<VersionPrefix>([^<]+)</VersionPrefix>').Groups[1].Value }
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'A stable EncoDeck version is required.' }
$output = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $root ('artifacts/lhdc-runtime/' + [guid]::NewGuid().ToString('N')) }
$archive = Join-Path $output "EncoDeck-LHDC-$Version-win-x64.zip"
& (Join-Path $PSScriptRoot 'build-lhdc.ps1') -Destination (Join-Path $output 'runtime') -ArchivePath $archive -PowerShellDirectory $PowerShellDirectory -Version $Version
if (-not $?) { throw 'LHDC runtime build failed.' }
if ($Publish) {
    # Require the app release; never create an independent component release or overwrite existing assets.
    & gh release view "v$Version" --repo TIANLI0/EncoDeck *> $null
    if ($LASTEXITCODE -ne 0) { throw "Create the application release v$Version first." }
    & gh release upload "v$Version" $archive ($archive+'.sha256') --repo TIANLI0/EncoDeck
    if ($LASTEXITCODE -ne 0) { throw 'Could not publish versioned runtime assets.' }
}
Write-Host "Source-built runtime archive: $archive"
