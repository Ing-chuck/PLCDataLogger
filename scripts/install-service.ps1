#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Installs the PLC Data Logger as a Windows Service with auto-start and
    auto-restart-on-failure recovery.

.DESCRIPTION
    Optionally publishes a self-contained single-file build into the install
    directory, then registers it as a Windows Service via sc.exe. Re-running the
    script updates an existing installation (the service is stopped and recreated).

    Must be run from an elevated PowerShell prompt.

.EXAMPLE
    # Publish a fresh build and install the service:
    .\install-service.ps1 -Publish

.EXAMPLE
    # Install from an already-published folder:
    .\install-service.ps1 -InstallDir 'D:\Apps\PLCDataLogger'
#>
param(
    [string]$ServiceName = 'PLCDataLogger',
    [string]$DisplayName = 'PLC Data Logger',
    [string]$Description = 'Logs Codesys/Eaton PLC tag data over OPC UA to local storage.',
    [string]$InstallDir  = 'C:\PLCDataLogger',
    [switch]$Publish,
    [string]$ProjectPath = (Join-Path $PSScriptRoot '..\PLCDataLogger.csproj')
)

$ErrorActionPreference = 'Stop'

if ($Publish) {
    Write-Host "Publishing self-contained build to $InstallDir ..." -ForegroundColor Cyan
    dotnet publish $ProjectPath -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -o $InstallDir
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)." }
}

$exe = Join-Path $InstallDir 'PLCDataLogger.exe'
if (-not (Test-Path $exe)) {
    throw "Executable not found at '$exe'. Run with -Publish, or publish into -InstallDir first."
}

# Remove any existing instance so settings (binPath, recovery) are applied cleanly.
$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host "Existing service found; stopping and removing..." -ForegroundColor Yellow
    if ($existing.Status -ne 'Stopped') { Stop-Service -Name $ServiceName -Force }
    sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 2
}

Write-Host "Creating service '$ServiceName'..." -ForegroundColor Cyan
# sc.exe requires a space after each '='.
sc.exe create $ServiceName binPath= "`"$exe`"" start= auto DisplayName= "$DisplayName" | Out-Null
if ($LASTEXITCODE -ne 0) { throw "sc.exe create failed (exit $LASTEXITCODE)." }

sc.exe description $ServiceName "$Description" | Out-Null

# Auto-restart on failure: 5s, then 10s, then every 30s; reset the failure counter daily.
sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/10000/restart/30000 | Out-Null
# Also trigger recovery when the process exits non-zero (not just on a hard crash).
sc.exe failureflag $ServiceName 1 | Out-Null

Write-Host "Starting service..." -ForegroundColor Cyan
Start-Service -Name $ServiceName

Write-Host "Done. Configuration: $(Join-Path $InstallDir 'appsettings.json')  Logs: $(Join-Path $InstallDir 'logs')" -ForegroundColor Green
Get-Service -Name $ServiceName | Format-Table -AutoSize
