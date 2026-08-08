#Requires -RunAsAdministrator
<#
    Removes the LLM Router Windows Service installed by install-service.ps1.
#>
param(
    [string]$ServiceName = "LLMRouter"
)

$ErrorActionPreference = "Stop"

$svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if (-not $svc) {
    Write-Host "Service '$ServiceName' is not installed."
    exit 0
}

if ($svc.Status -ne "Stopped") {
    Stop-Service -Name $ServiceName
    $svc.WaitForStatus("Stopped", (New-TimeSpan -Seconds 30))
}

sc.exe delete $ServiceName | Out-Null

Write-Host "Service '$ServiceName' removed."
