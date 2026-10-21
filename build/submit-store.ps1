# SPDX-License-Identifier: GPL-3.0-only
<#
.SYNOPSIS
  Submits a .msixupload to the Microsoft Store through the Partner Center submission API.

.DESCRIPTION
  Creates a new submission from the published one (listing, pricing, age rating are kept), replaces the
  packages with -Package, uploads it and commits the submission for certification. Credentials come from
  the STORE_TENANT_ID, STORE_CLIENT_ID and STORE_CLIENT_SECRET environment variables (a Microsoft Entra
  app with the Manager role in Partner Center).

.EXAMPLE
  pwsh build\submit-store.ps1 -Package artifacts\store\EncoDeck-0.7.2.msixupload
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Package,
    [string] $AppId = "9PN6D8G1K9WZ",
    [string] $ReleaseNotes
)

$ErrorActionPreference = "Stop"
foreach ($name in "STORE_TENANT_ID", "STORE_CLIENT_ID", "STORE_CLIENT_SECRET") {
    if (-not [Environment]::GetEnvironmentVariable($name)) { throw "$name is not set." }
}
$Package = (Resolve-Path $Package).Path
$api = "https://manage.devcenter.microsoft.com/v1.0/my/applications/$AppId"

$token = (Invoke-RestMethod -Method Post "https://login.microsoftonline.com/$env:STORE_TENANT_ID/oauth2/token" -Body @{
    grant_type    = "client_credentials"
    client_id     = $env:STORE_CLIENT_ID
    client_secret = $env:STORE_CLIENT_SECRET
    resource      = "https://manage.devcenter.microsoft.com"
}).access_token
$headers = @{ Authorization = "Bearer $token" }

$app = Invoke-RestMethod "$api" -Headers $headers
if ($app.pendingApplicationSubmission) {
    throw "Submission $($app.pendingApplicationSubmission.id) is still pending in Partner Center; finish or delete it first."
}

$submission = Invoke-RestMethod -Method Post "$api/submissions" -Headers $headers -ContentType "application/json" -Body "{}"
Write-Host "Created submission $($submission.id)"

foreach ($existing in $submission.applicationPackages) { $existing.fileStatus = "PendingDelete" }
$fileName = Split-Path $Package -Leaf
$submission.applicationPackages = @($submission.applicationPackages) + [pscustomobject]@{
    fileName = $fileName; fileStatus = "PendingUpload"; minimumDirectXVersion = "None"; minimumSystemRam = "None"
}
if ($ReleaseNotes) {
    foreach ($listing in $submission.listings.PSObject.Properties) { $listing.Value.baseListing.releaseNotes = $ReleaseNotes }
}
$body = $submission | ConvertTo-Json -Depth 20
$submission = Invoke-RestMethod -Method Put "$api/submissions/$($submission.id)" -Headers $headers -ContentType "application/json; charset=utf-8" -Body ([Text.Encoding]::UTF8.GetBytes($body))

# The upload URL takes a zip whose entries match the package file names.
$zip = Join-Path ([IO.Path]::GetTempPath()) "encodeck-store-$($submission.id).zip"
Remove-Item $zip -ErrorAction SilentlyContinue
Compress-Archive -LiteralPath $Package -DestinationPath $zip
Invoke-RestMethod -Method Put $submission.fileUploadUrl -InFile $zip -Headers @{ "x-ms-blob-type" = "BlockBlob" } -ContentType "application/zip"
Write-Host "Uploaded $fileName"

Invoke-RestMethod -Method Post "$api/submissions/$($submission.id)/commit" -Headers $headers -ContentType "application/json" -Body "{}" | Out-Null
for ($i = 0; $i -lt 60; $i++) {
    $status = Invoke-RestMethod "$api/submissions/$($submission.id)/status" -Headers $headers
    if ($status.status -ne "CommitStarted") { break }
    Start-Sleep -Seconds 10
}
Write-Host "Submission status: $($status.status)"
if ($status.status -like "*Failed") {
    $status.statusDetails | ConvertTo-Json -Depth 10 | Write-Host
    throw "Store submission failed."
}
