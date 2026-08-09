#Requires -Version 5.1

[CmdletBinding()]
param(
    [string]$ServerRoot,
    [string]$RuntimeModConfigPath,
    [string[]]$ActiveModuleIds,
    [switch]$NonInteractive
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-NormalizedModuleIds {
    param([string[]]$Values)

    $result = New-Object System.Collections.Generic.List[string]
    foreach ($value in @($Values)) {
        foreach ($part in @(([string]$value) -split '[,;*]')) {
            $trimmed = $part.Trim()
            if (-not [string]::IsNullOrWhiteSpace($trimmed) -and $trimmed -notin @('_MODULES_', 'MODULES')) {
                $result.Add($trimmed)
            }
        }
    }
    return $result.ToArray()
}

function Get-RequiredFullPath {
    param([string]$Path, [string]$Description)

    if ([string]::IsNullOrWhiteSpace($Path)) { throw "$Description is required." }
    $full = [System.IO.Path]::GetFullPath($Path.Trim().Trim('"'))
    if (-not (Test-Path -LiteralPath $full -PathType Container)) {
        throw "$Description does not exist: $full"
    }
    return $full.TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)
}

function Remove-JsonComments {
    param([string]$Text)

    $builder = New-Object System.Text.StringBuilder
    $inString = $false
    $escaped = $false
    $lineComment = $false
    $blockComment = $false
    for ($index = 0; $index -lt $Text.Length; $index++) {
        $current = $Text[$index]
        $next = if ($index + 1 -lt $Text.Length) { $Text[$index + 1] } else { [char]0 }
        if ($lineComment) {
            if ($current -eq "`n" -or $current -eq "`r") {
                $lineComment = $false
                [void]$builder.Append($current)
            }
            continue
        }
        if ($blockComment) {
            if ($current -eq '*' -and $next -eq '/') {
                $blockComment = $false
                $index++
            }
            elseif ($current -eq "`n" -or $current -eq "`r") {
                [void]$builder.Append($current)
            }
            continue
        }
        if ($inString) {
            [void]$builder.Append($current)
            if ($escaped) { $escaped = $false; continue }
            if ($current -eq '\') { $escaped = $true; continue }
            if ($current -eq '"') { $inString = $false }
            continue
        }
        if ($current -eq '"') {
            $inString = $true
            [void]$builder.Append($current)
        }
        elseif ($current -eq '/' -and $next -eq '/') {
            $lineComment = $true
            $index++
        }
        elseif ($current -eq '/' -and $next -eq '*') {
            $blockComment = $true
            $index++
        }
        else {
            [void]$builder.Append($current)
        }
    }
    if ($inString -or $blockComment) { throw 'Runtime mod-config.json contains an unterminated string or block comment.' }
    return $builder.ToString()
}

function Remove-TrailingJsonCommas {
    param([string]$Text)

    $builder = New-Object System.Text.StringBuilder
    $inString = $false
    $escaped = $false
    for ($index = 0; $index -lt $Text.Length; $index++) {
        $current = $Text[$index]
        if ($inString) {
            [void]$builder.Append($current)
            if ($escaped) { $escaped = $false; continue }
            if ($current -eq '\') { $escaped = $true; continue }
            if ($current -eq '"') { $inString = $false }
            continue
        }
        if ($current -eq '"') {
            $inString = $true
            [void]$builder.Append($current)
            continue
        }
        if ($current -eq ',') {
            $lookAhead = $index + 1
            while ($lookAhead -lt $Text.Length -and [char]::IsWhiteSpace($Text[$lookAhead])) { $lookAhead++ }
            if ($lookAhead -lt $Text.Length -and $Text[$lookAhead] -in @('}', ']')) { continue }
        }
        [void]$builder.Append($current)
    }
    return $builder.ToString()
}

try {
    if ([string]::IsNullOrWhiteSpace($ServerRoot)) {
        if ($NonInteractive) { throw 'Pass -ServerRoot when using -NonInteractive.' }
        $ServerRoot = Read-Host 'Enter the dedicated-server engine root (the directory containing Modules)'
    }
    $resolvedServerRoot = Get-RequiredFullPath -Path $ServerRoot -Description 'Dedicated-server engine root'
    $modulesRoot = Join-Path $resolvedServerRoot 'Modules'
    if (-not (Test-Path -LiteralPath $modulesRoot -PathType Container)) {
        throw "Dedicated-server Modules directory is missing: $modulesRoot"
    }

    $expectationPath = Join-Path $PSScriptRoot 'SERVER-HARMONY.json'
    if (-not (Test-Path -LiteralPath $expectationPath -PathType Leaf)) {
        throw "Pinned server Harmony expectation is missing: $expectationPath"
    }
    $expectation = Get-Content -LiteralPath $expectationPath -Raw | ConvertFrom-Json

    if ([string]::IsNullOrWhiteSpace($RuntimeModConfigPath)) {
        foreach ($environmentName in @($expectation.runtimeConfig.environmentDirectoryPrecedence)) {
            $directory = [Environment]::GetEnvironmentVariable([string]$environmentName)
            if (-not [string]::IsNullOrWhiteSpace($directory)) {
                $RuntimeModConfigPath = Join-Path $directory ([string]$expectation.runtimeConfig.fileName)
                break
            }
        }
    }
    if ([string]::IsNullOrWhiteSpace($RuntimeModConfigPath)) {
        if ($NonInteractive) {
            throw 'Pass -RuntimeModConfigPath or set COOP_DATA_DIR/BANNERLORD_USER_DIR when using -NonInteractive.'
        }
        $RuntimeModConfigPath = Read-Host 'Enter the resolved runtime CoopData mod-config.json path'
    }
    $resolvedRuntimeConfig = [System.IO.Path]::GetFullPath($RuntimeModConfigPath.Trim().Trim('"'))
    if (Test-Path -LiteralPath $resolvedRuntimeConfig -PathType Container) {
        $resolvedRuntimeConfig = Join-Path $resolvedRuntimeConfig ([string]$expectation.runtimeConfig.fileName)
    }
    if (-not (Test-Path -LiteralPath $resolvedRuntimeConfig -PathType Leaf)) {
        throw "Resolved runtime CoopData mod-config.json is missing: $resolvedRuntimeConfig"
    }
    $runtimeConfigHash = (Get-FileHash -LiteralPath $resolvedRuntimeConfig -Algorithm SHA256).Hash.ToLowerInvariant()
    $jsonc = [System.IO.File]::ReadAllText($resolvedRuntimeConfig)
    $runtimeConfig = Remove-TrailingJsonCommas -Text (Remove-JsonComments -Text $jsonc) | ConvertFrom-Json
    $difficultyProperty = $runtimeConfig.PSObject.Properties['difficulty']
    $birthProperty = if ($null -ne $difficultyProperty -and $null -ne $difficultyProperty.Value) {
        $difficultyProperty.Value.PSObject.Properties['birthAndDeath']
    } else { $null }
    if ($null -eq $birthProperty -or $birthProperty.Value -isnot [bool] -or -not [bool]$birthProperty.Value) {
        throw "Resolved runtime config must set difficulty.birthAndDeath=true as a Boolean: $resolvedRuntimeConfig"
    }

    $normalizedIds = @(Get-NormalizedModuleIds -Values $ActiveModuleIds)
    if ($normalizedIds.Count -eq 0) {
        if ($NonInteractive) {
            throw 'Pass the launch module list through -ActiveModuleIds when using -NonInteractive.'
        }
        $answer = Read-Host 'Enter the server launch module IDs in activation order (comma-separated)'
        $normalizedIds = @(Get-NormalizedModuleIds -Values @($answer))
    }
    if (@($normalizedIds | Sort-Object -Unique).Count -ne $normalizedIds.Count) {
        throw "The launch module list contains duplicate IDs: $($normalizedIds -join ', ')"
    }
    foreach ($required in @($expectation.activation.requiredModuleIds)) {
        if ([string]$required -notin $normalizedIds) {
            throw "Required server module '$required' is absent from the launch module list."
        }
    }
    $blockedServerModules = @(
        @($expectation.activation.neverActivateModuleIds) +
        @($expectation.activation.guardedModuleIds) |
            ForEach-Object { [string]$_ } |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
            Sort-Object -Unique
    )
    foreach ($blocked in $blockedServerModules) {
        if ([string]$blocked -in $normalizedIds) {
            throw "Production server preflight blocks staged-inactive module '$blocked'; remove it from the headless launch module list."
        }
    }
    $expectedOrder = @($expectation.activation.exactActiveModuleOrder | ForEach-Object { [string]$_ })
    if ($expectedOrder.Count -eq 0 -or ($normalizedIds -join "`n") -cne ($expectedOrder -join "`n")) {
        throw "Production server launch modules must exactly match the pinned order: $($expectedOrder -join ' -> '). Received: $($normalizedIds -join ' -> ')."
    }
    foreach ($successor in @($expectation.activation.harmonyMustPrecede)) {
        $harmonyIndex = [array]::IndexOf($normalizedIds, [string]$expectation.moduleId)
        $successorIndex = [array]::IndexOf($normalizedIds, [string]$successor)
        if ($harmonyIndex -lt 0 -or $successorIndex -lt 0 -or $harmonyIndex -ge $successorIndex) {
            throw "Server activation order must place '$($expectation.moduleId)' before '$successor'."
        }
    }

    $moduleRoot = Join-Path $modulesRoot ([string]$expectation.moduleId)
    $descriptorPath = Join-Path $moduleRoot 'SubModule.xml'
    if (-not (Test-Path -LiteralPath $descriptorPath -PathType Leaf)) {
        throw "Pinned Harmony module descriptor is missing: $descriptorPath"
    }
    [xml]$descriptor = Get-Content -LiteralPath $descriptorPath -Raw
    if ([string]$descriptor.Module.Id.value -cne [string]$expectation.moduleId -or
        [string]$descriptor.Module.Version.value -cne [string]$expectation.moduleVersion) {
        throw "Harmony descriptor is not the pinned module/version '$($expectation.moduleId)' '$($expectation.moduleVersion)'."
    }

    if (-not [bool]$expectation.metadataAudit.buildTimeVerified) {
        throw 'The package does not attest build-time AssemblyName/version verification for the pinned Harmony payloads.'
    }
    foreach ($payload in @($expectation.payloads)) {
        $payloadPath = Join-Path $moduleRoot ([string]$payload.path).Replace('/', [System.IO.Path]::DirectorySeparatorChar)
        if (-not (Test-Path -LiteralPath $payloadPath -PathType Leaf)) {
            throw "Pinned server Harmony payload is missing: $payloadPath"
        }
        $payloadItem = Get-Item -LiteralPath $payloadPath
        $payloadHash = (Get-FileHash -LiteralPath $payloadPath -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($payloadItem.Length -ne [long]$payload.size -or $payloadHash -cne [string]$payload.sha256) {
            throw "Server Harmony payload failed its pinned size/SHA-256 check: $payloadPath"
        }
        if ([string]::IsNullOrWhiteSpace([string]$payload.assemblyFullName)) {
            throw "Pinned build-time AssemblyName/version record is missing for: $($payload.path)"
        }
    }

    $unexpectedProviders = @(
        Get-ChildItem -LiteralPath $modulesRoot -Filter '0Harmony.dll' -Recurse -File -ErrorAction Stop |
            Where-Object { -not $_.FullName.StartsWith(($moduleRoot.TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar), [System.StringComparison]::OrdinalIgnoreCase) }
    )
    if ($unexpectedProviders.Count -gt 0) {
        throw "More than one packaged Harmony provider is present. Remove stale noncanonical copies before launch: $(@($unexpectedProviders.FullName) -join '; ')"
    }

    foreach ($payload in @($expectation.payloads)) {
        $payloadPath = Join-Path $moduleRoot ([string]$payload.path).Replace('/', [System.IO.Path]::DirectorySeparatorChar)
        if ((Get-FileHash -LiteralPath $payloadPath -Algorithm SHA256).Hash.ToLowerInvariant() -cne [string]$payload.sha256) {
            throw "Server Harmony payload changed during read-only preflight: $payloadPath"
        }
    }
    if ((Get-FileHash -LiteralPath $resolvedRuntimeConfig -Algorithm SHA256).Hash.ToLowerInvariant() -cne $runtimeConfigHash) {
        throw "Resolved runtime mod-config changed during read-only preflight: $resolvedRuntimeConfig"
    }

    Write-Host "PASS: exact build-time-audited AssemblyName/version byte pins for $($expectation.moduleId) $($expectation.moduleVersion) are the only packaged Harmony provider and precede Native/Coop." -ForegroundColor Green
    Write-Host "CONFIG: resolvedRuntimePath=$resolvedRuntimeConfig sha256=$runtimeConfigHash difficulty.birthAndDeath=true" -ForegroundColor Green
    Write-Host 'This preflight validates only; it does not install, delete, overwrite, bootstrap, or launch the server.'
}
catch {
    Write-Error $_
    exit 1
}
