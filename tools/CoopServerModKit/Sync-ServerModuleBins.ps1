#Requires -Version 5.1

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ModulesRoot,

    [string[]]$ModuleIds = @(),

    [string]$RoleManifest = (Join-Path $PSScriptRoot 'server-module-roles.json'),

    [switch]$Overwrite
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$resolvedRoot = [System.IO.Path]::GetFullPath($ModulesRoot)
if (-not (Test-Path -LiteralPath $resolvedRoot -PathType Container)) {
    throw "Modules root does not exist: $resolvedRoot"
}

if ($ModuleIds.Count -eq 0) {
    if (-not (Test-Path -LiteralPath $RoleManifest -PathType Leaf)) {
        throw "Server module role manifest does not exist: $RoleManifest"
    }
    $roles = Get-Content -LiteralPath $RoleManifest -Raw | ConvertFrom-Json
    $ModuleIds = @($roles.serverRuntimeModules)
    if ($ModuleIds.Count -eq 0) {
        throw "Server module role manifest declares no serverRuntimeModules: $RoleManifest"
    }
}

foreach ($moduleId in $ModuleIds) {
    if ([string]::IsNullOrWhiteSpace($moduleId) -or
        $moduleId.IndexOfAny([System.IO.Path]::GetInvalidFileNameChars()) -ge 0 -or
        $moduleId.Contains([System.IO.Path]::DirectorySeparatorChar) -or
        $moduleId.Contains([System.IO.Path]::AltDirectorySeparatorChar)) {
        throw "Invalid module id: $moduleId"
    }

    $moduleRoot = Join-Path $resolvedRoot $moduleId
    $clientBin = Join-Path $moduleRoot 'bin\Win64_Shipping_Client'
    $serverBin = Join-Path $moduleRoot 'bin\Win64_Shipping_Server'
    if (-not (Test-Path -LiteralPath $clientBin -PathType Container)) {
        throw "Client runtime is missing for $moduleId at $clientBin"
    }

    $runtimeFiles = @(Get-ChildItem -LiteralPath $clientBin -Recurse -File | Where-Object {
        -not $_.Extension.Equals('.pdb', [System.StringComparison]::OrdinalIgnoreCase)
    })
    if ($runtimeFiles.Count -eq 0) {
        throw "Client runtime contains no deployable files for $moduleId"
    }

    New-Item -ItemType Directory -Path $serverBin -Force | Out-Null
    foreach ($source in $runtimeFiles) {
        $relative = [System.IO.Path]::GetRelativePath($clientBin, $source.FullName)
        $destination = Join-Path $serverBin $relative
        $destinationParent = Split-Path -Parent $destination
        New-Item -ItemType Directory -Path $destinationParent -Force | Out-Null

        if (Test-Path -LiteralPath $destination -PathType Leaf) {
            $sourceHash = (Get-FileHash -LiteralPath $source.FullName -Algorithm SHA256).Hash
            $destinationHash = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash
            if ($sourceHash -eq $destinationHash) { continue }
            if (-not $Overwrite) {
                throw "Refusing to overwrite drifted server runtime file without -Overwrite: $destination"
            }
        }

        Copy-Item -LiteralPath $source.FullName -Destination $destination -Force:$Overwrite
    }

    Write-Host "STAGED SERVER MODULE BIN: $moduleId ($($runtimeFiles.Count) files)"
}
