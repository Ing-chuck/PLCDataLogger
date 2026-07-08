<#
.SYNOPSIS
    Builds a versioned, self-contained distribution zip for field deployment.

.DESCRIPTION
    Publishes a single-file, self-contained win-x64 build and bundles it with the install/uninstall
    scripts and the deployment guide into PLCDataLogger-v<version>-win-x64.zip. Copy that one zip to
    a site, unzip, and run scripts\install-service.ps1 -Publish:$false (the exe is already built).

    Does not require elevation.

.EXAMPLE
    .\package.ps1
    .\package.ps1 -Version 1.1.0 -OutputDir C:\dist
#>
param(
    [string]$Version,
    [string]$OutputDir = (Join-Path $PSScriptRoot '..\dist'),
    [string]$ProjectPath = (Join-Path $PSScriptRoot '..\PLCDataLogger.csproj')
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')

# Resolve version from the .csproj if not supplied.
if (-not $Version) {
    $csproj = Get-Content $ProjectPath -Raw
    if ($csproj -match '<Version>([^<]+)</Version>') { $Version = $Matches[1] } else { $Version = '1.0.0' }
}

$stageName = "PLCDataLogger-v$Version-win-x64"
$stage = Join-Path $OutputDir $stageName
Write-Host "Packaging $stageName ..." -ForegroundColor Cyan

if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stage | Out-Null

# 1) Publish the self-contained single-file app into the stage folder.
dotnet publish $ProjectPath -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:Version=$Version -o $stage
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)." }

# 2) Bundle the operator-facing scripts and docs alongside the binary.
New-Item -ItemType Directory -Force -Path (Join-Path $stage 'scripts') | Out-Null
Copy-Item (Join-Path $PSScriptRoot 'install-service.ps1')   (Join-Path $stage 'scripts') -Force
Copy-Item (Join-Path $PSScriptRoot 'uninstall-service.ps1') (Join-Path $stage 'scripts') -Force
foreach ($doc in @('COMMISSIONING.md', 'DEPLOYMENT.md', 'README.md')) {
    $src = Join-Path $root $doc
    if (Test-Path $src) { Copy-Item $src $stage -Force }
}

# 3) Defensively scrub any per-site secrets or runtime data that must never ship, in case the
#    publish step ever picks one up. Credentials are placed on-site after install, never bundled.
foreach ($secret in @('secrets', 'pki', 'data', 'logs', 'exports', 'google_token',
                       'google_client.json', 'config.local.json', 'dashboards', 'dist')) {
    $target = Join-Path $stage $secret
    if (Test-Path $target) {
        Write-Host "Scrubbing '$secret' from the package." -ForegroundColor Yellow
        Remove-Item $target -Recurse -Force
    }
}
foreach ($enc in (Get-ChildItem $stage -Recurse -Filter '*.dpapi' -File -ErrorAction SilentlyContinue)) {
    Remove-Item -LiteralPath $enc.FullName -Force
}

# 4) Zip it up; the zip is the single artifact to copy to a site.
$zip = Join-Path $OutputDir "$stageName.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path "$stage\*" -DestinationPath $zip

$sizeMb = [math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Host "Created $zip ($sizeMb MB)" -ForegroundColor Green
Write-Host "Deploy: unzip on the target PC, edit appsettings.json, then run (elevated):" -ForegroundColor Green
Write-Host "  scripts\install-service.ps1 -InstallDir <unzipped folder>" -ForegroundColor Green
