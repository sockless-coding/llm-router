<#
.SYNOPSIS
    Builds the LLM Router agent packages the router serves to install scripts and self-updating agents.

.DESCRIPTION
    Publishes LR.Agent self-contained for each runtime, archives it (zip for Windows, tar.gz for
    Linux) and writes manifest.json (version, sha256, size per package) into the output folder.
    The router reads that folder (agent-packages/ next to LR.Application, or Agents:PackageFolder).

    Connected agents with auto-update on are moved to whatever version the router has packaged,
    so re-running this during development rolls the new build out to them.

.PARAMETER Version
    Version stamped on the agent. Defaults to a unique dev version so every run is picked up as an update.

.PARAMETER Output
    Folder to write the packages and manifest to. Defaults to the dev router's output folder.

.EXAMPLE
    .\publish-agents.ps1
    .\publish-agents.ps1 -Version 2026.11.0 -Output publish\agent-packages
    .\publish-agents.ps1 -Rids linux-x64
#>
param(
    [string]$Version = ('0.0.0-dev.' + (Get-Date -Format 'yyyyMMddHHmmss')),
    [string]$Output = (Join-Path $PSScriptRoot 'LR.Application\bin\Debug\net10.0\agent-packages'),
    [string[]]$Rids = @('win-x64', 'win-arm64', 'linux-x64', 'linux-arm64'),
    [string]$Configuration = 'Release'
)
$ErrorActionPreference = 'Stop'

$project = Join-Path $PSScriptRoot 'LR.Agent\LR.Agent.csproj'
$staging = Join-Path ([IO.Path]::GetTempPath()) ('lr-agent-publish-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $Output, $staging | Out-Null
$Output = (Resolve-Path $Output).Path

$packages = @()
try {
    foreach ($rid in $Rids) {
        Write-Host "==> Publishing LR.Agent $Version for $rid"
        $dir = Join-Path $staging $rid
        dotnet publish $project -c $Configuration -r $rid --self-contained true -o $dir `
            -p:Version=$Version -p:DebugType=None -p:GenerateDocumentationFile=false --nologo -v quiet
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $rid" }

        if ($rid -like 'win-*') {
            $file = "lr-agent-$rid.zip"
            $archive = Join-Path $Output $file
            if (Test-Path $archive) { Remove-Item $archive -Force }
            Compress-Archive -Path (Join-Path $dir '*') -DestinationPath $archive
        }
        else {
            $file = "lr-agent-$rid.tar.gz"
            $archive = Join-Path $Output $file
            if (Test-Path $archive) { Remove-Item $archive -Force }
            # bsdtar ships with Windows 10+ and every Linux; the installer/updater restore exec bits.
            tar -czf $archive -C $dir .
            if ($LASTEXITCODE -ne 0) { throw "tar failed for $rid" }
        }

        $hash = (Get-FileHash -Algorithm SHA256 $archive).Hash.ToLowerInvariant()
        $size = (Get-Item $archive).Length
        $packages += [ordered]@{ rid = $rid; file = $file; sha256 = $hash; size = $size }
        Write-Host ("    {0} ({1:N1} MB)" -f $file, ($size / 1MB))
    }

    # Only packages built at this version are listed: an agent updated to an older package would
    # report a version that differs from the manifest's and be told to update again, forever.
    $manifestPath = Join-Path $Output 'manifest.json'
    $manifest = [ordered]@{ version = $Version; builtAt = (Get-Date).ToUniversalTime().ToString('o'); packages = $packages }
    $manifest | ConvertTo-Json -Depth 4 | Set-Content -Encoding utf8 $manifestPath
    Write-Host "==> Wrote $manifestPath (version $Version)"
}
finally {
    Remove-Item -Recurse -Force $staging -ErrorAction SilentlyContinue
}
