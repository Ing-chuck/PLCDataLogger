#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Stops and removes the PLC Data Logger Windows Service.

.DESCRIPTION
    Removes the service registration only. The install directory (binaries,
    appsettings.json, data/, logs/) is left in place; delete it manually if desired.

    Must be run from an elevated PowerShell prompt.
#>
param(
    [string]$ServiceName = 'PLCDataLogger'
)

$ErrorActionPreference = 'Stop'

$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if (-not $existing) {
    Write-Host "Service '$ServiceName' is not installed; nothing to do." -ForegroundColor Yellow
    return
}

if ($existing.Status -ne 'Stopped') {
    Write-Host "Stopping service '$ServiceName'..." -ForegroundColor Cyan
    Stop-Service -Name $ServiceName -Force
}

Write-Host "Removing service '$ServiceName'..." -ForegroundColor Cyan
sc.exe delete $ServiceName | Out-Null
if ($LASTEXITCODE -ne 0) { throw "sc.exe delete failed (exit $LASTEXITCODE)." }

Write-Host "Service removed. Install directory and data were left untouched." -ForegroundColor Green
