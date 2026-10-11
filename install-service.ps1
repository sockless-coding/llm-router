#Requires -RunAsAdministrator
<#
    Installs LLM Router as a Windows Service.

    Run it from the extracted GitHub release folder, next to LR.Application.exe.
    For a local test build, run publish.bat first and pass -PublishDir .\publish.

    Usage:
        .\install-service.ps1
        .\install-service.ps1 -PublishDir .\publish -StartupType Manual
#>
param(
    [string]$ServiceName = "LLMRouter",
    [string]$DisplayName = "LLM Router",
    [string]$Description = "Routes and load-balances requests across local LLM inference servers.",
    [string]$PublishDir = "$PSScriptRoot",
    [ValidateSet("Automatic", "Manual", "Disabled")]
    [string]$StartupType = "Automatic"
)

$ErrorActionPreference = "Stop"

$exePath = Join-Path $PublishDir "LR.Application.exe"

if (-not (Test-Path $exePath)) {
    Write-Error "Executable not found at '$exePath'.`nRun this script from the extracted release folder, or pass -PublishDir to point at the folder containing LR.Application.exe."
    exit 1
}

if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
    Write-Error "A service named '$ServiceName' already exists. Run uninstall-service.ps1 first."
    exit 1
}

New-Service -Name $ServiceName `
    -BinaryPathName "`"$exePath`"" `
    -DisplayName $DisplayName `
    -Description $Description `
    -StartupType $StartupType | Out-Null

Write-Host "Service '$ServiceName' installed (binary: $exePath)."
Write-Host "Start it with: Start-Service $ServiceName"
