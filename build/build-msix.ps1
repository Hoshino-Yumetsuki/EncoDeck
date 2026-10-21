# SPDX-License-Identifier: GPL-3.0-only
<#
.SYNOPSIS
  Builds the Microsoft Store package (artifacts\EncoDeck-<version>.msixupload, x64 + ARM64).

.DESCRIPTION
  1. Runs the unit tests (unless -SkipTests).
  2. Publishes the WinUI app self-contained for each architecture.
  3. Adds packaging\Package.appxmanifest and the tile images, packs one .msix per architecture,
     bundles them and wraps the bundle as .msixupload for Partner Center.
  The Store signs the package; the local bundle is unsigned.

.EXAMPLE
  pwsh build\build-msix.ps1
#>
[CmdletBinding()]
param(
    [string] $Configuration = "Release",
    [string] $Version,
    [string[]] $Architectures = @("x64", "arm64"),
    [switch] $SkipTests
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

if (-not $Version) {
    $props = Get-Content (Join-Path $root "Directory.Build.props") -Raw
    $Version = [regex]::Match($props, "<VersionPrefix>([^<]+)</VersionPrefix>").Groups[1].Value
}
$numeric = ($Version -split "[-+]")[0]
# Store packages require the fourth (revision) field to be 0.
$parts = @($numeric -split "\.") + @("0", "0", "0")
$packageVersion = "$($parts[0]).$($parts[1]).$($parts[2]).0"
Write-Host "EncoDeck $Version (package $packageVersion)"

if (-not $SkipTests) {
    dotnet test tests/EncoDeck.Tests -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) { throw "Unit tests failed." }
}

$nugetRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE ".nuget\packages" }
$buildToolsVersion = [regex]::Match((Get-Content Directory.Packages.props -Raw), 'Microsoft\.Windows\.SDK\.BuildTools" Version="([^"]+)"').Groups[1].Value
$makeappx = Get-ChildItem (Join-Path $nugetRoot "microsoft.windows.sdk.buildtools\$buildToolsVersion\bin") -Recurse -Filter makeappx.exe |
    Where-Object { $_.DirectoryName -like "*\x64" } | Select-Object -First 1
if (-not $makeappx) { throw "makeappx.exe not found; run dotnet restore first." }

$work = [IO.Path]::GetFullPath((Join-Path $root "artifacts\msix"))
if (Test-Path -LiteralPath $work) { Remove-Item -LiteralPath $work -Recurse -Force }
New-Item -ItemType Directory -Force (Join-Path $work "bundle") | Out-Null

# Tile and Store images from the 256 px app icon.
Add-Type -AssemblyName System.Drawing
$icon = [System.Drawing.Image]::FromFile((Join-Path $root "src\EncoDeck.App\Assets\EncoDeck-256.png"))
function Save-Logo([string] $path, [int] $width, [int] $height, [double] $scale) {
    $bitmap = New-Object System.Drawing.Bitmap $width, $height
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $size = [int]([Math]::Min($width, $height) * $scale)
    $g.DrawImage($icon, [int](($width - $size) / 2), [int](($height - $size) / 2), $size, $size)
    $g.Dispose()
    $bitmap.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
}

foreach ($arch in $Architectures) {
    $rid = "win-$arch"
    $platform = if ($arch -eq "arm64") { "ARM64" } else { "x64" }
    $layout = Join-Path $work $arch
    dotnet publish src/EncoDeck.App -c $Configuration -r $rid -p:Platform=$platform `
        -p:VersionPrefix=$numeric -p:VersionSuffix="$(if ($Version -match '-(.+)$') { $Matches[1] } else { '' })" `
        -o $layout --nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish ($arch) failed." }

    $images = New-Item -ItemType Directory -Force (Join-Path $layout "Images")
    Save-Logo (Join-Path $images "StoreLogo.png") 50 50 1.0
    Save-Logo (Join-Path $images "Square44x44Logo.png") 44 44 1.0
    Save-Logo (Join-Path $images "Square150x150Logo.png") 150 150 0.66
    Save-Logo (Join-Path $images "Wide310x150Logo.png") 310 150 0.66

    (Get-Content packaging\Package.appxmanifest -Raw).Replace('$VERSION$', $packageVersion).Replace('$ARCH$', $arch) |
        Set-Content (Join-Path $layout "AppxManifest.xml") -Encoding utf8

    & $makeappx.FullName pack /o /d $layout /p (Join-Path $work "bundle\EncoDeck_${packageVersion}_$arch.msix")
    if ($LASTEXITCODE -ne 0) { throw "makeappx pack ($arch) failed." }
}
$icon.Dispose()

$bundle = Join-Path $work "EncoDeck_$packageVersion.msixbundle"
& $makeappx.FullName bundle /o /bv $packageVersion /d (Join-Path $work "bundle") /p $bundle
if ($LASTEXITCODE -ne 0) { throw "makeappx bundle failed." }

$upload = Join-Path $root "artifacts\EncoDeck-$Version.msixupload"
$zip = "$upload.zip"
Remove-Item -LiteralPath $upload, $zip -ErrorAction SilentlyContinue
Compress-Archive -LiteralPath $bundle -DestinationPath $zip
Move-Item -LiteralPath $zip -Destination $upload
Write-Host "Store upload: $upload"
