# SPDX-License-Identifier: GPL-3.0-only
[CmdletBinding()]
param([string]$PowerShellDirectory, [switch]$Publish)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$output = Join-Path $root ('artifacts/lhdc-runtime/' + [guid]::NewGuid().ToString('N'))
$archive = Join-Path $output 'EncoDeck-LHDC-runtime-win-x64.zip'
& (Join-Path $PSScriptRoot 'build-lhdc.ps1') -Destination (Join-Path $output 'runtime') -ArchivePath $archive -PowerShellDirectory $PowerShellDirectory
if (-not $?) { throw 'LHDC runtime build failed.' }
if ($Publish) {
    # The application consumes this dedicated release, independent of the EncoDeck version.
    & gh release view lhdc-runtime --repo TIANLI0/EncoDeck *> $null
    if ($LASTEXITCODE -ne 0) {
        & gh release create lhdc-runtime --repo TIANLI0/EncoDeck --title 'EncoDeck LHDC runtime' --notes 'Optional source-built LHDC backend and test-signed drivers. Upstream source revision is recorded in release.json.' --latest=false
        if ($LASTEXITCODE -ne 0) { throw 'Could not create runtime release.' }
    }
    & gh release upload lhdc-runtime $archive ($archive+'.sha256') --repo TIANLI0/EncoDeck --clobber
    if ($LASTEXITCODE -ne 0) { throw 'Could not publish runtime assets.' }
}
Write-Host "Source-built runtime archive: $archive"
