#Requires -RunAsAdministrator
<#
    Installs LLM Router as a Windows Service.

    The app must be published first, e.g.:
        dotnet publish LR.Application -c Release -r win-x64 --self-contained false -o publish

    Usage:
        .\install-service.ps1
        .\install-service.ps1 -PublishDir .\publish -StartupType Manual
#>
param(
    [string]$ServiceName = "LLMRouter",
    [string]$DisplayName = "LLM Router",
    [string]$Description = "Routes and load-balances requests across local LLM inference servers.",
    [string]$PublishDir = "$PSScriptRoot\LR.Application\bin\Release\net10.0\win-x64\publish",
    [ValidateSet("Automatic", "Manual", "Disabled")]
    [string]$StartupType = "Automatic"
)

$ErrorActionPreference = "Stop"

$exePath = Join-Path $PublishDir "LR.Application.exe"

if (-not (Test-Path $exePath)) {
    Write-Error "Executable not found at '$exePath'.`nPublish the app first, e.g.:`n  dotnet publish LR.Application -c Release -r win-x64 --self-contained false -o `"$PublishDir`""
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
