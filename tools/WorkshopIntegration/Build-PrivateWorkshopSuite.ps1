#Requires -Version 5.1

[CmdletBinding(DefaultParameterSetName = 'Stage')]
param(
    [string]$ManifestPath,
    [string]$SteamRoot,
    [string]$WorkshopRoot,
    [string]$CoopModuleRoot,
    [string]$OutputRoot,
    [string]$ArchivePath,
    [Parameter(ParameterSetName = 'Validate')][switch]$ValidateOnly,
    [Parameter(ParameterSetName = 'DryRun')][switch]$DryRun,
    [switch]$NonInteractive,
    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($ManifestPath)) {
    $ManifestPath = Join-Path $PSScriptRoot '..\..\deploy\workshop-mods.json'
}
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $PSScriptRoot '..\..\artifacts\FriendEdition-PrivateWorkshopSuite'
}

Import-Module (Join-Path $PSScriptRoot 'WorkshopIntegration.psm1') -Force

try {
    Write-Host 'Friend Edition private Workshop suite builder' -ForegroundColor Cyan
    Write-Host 'Source Workshop content is read-only to this builder.'

    $manifest = Read-WorkshopSuiteManifest -Path $ManifestPath
    $workshop = Find-BannerlordWorkshop `
        -AppId ([string]$manifest.suite.appId) `
        -SteamRoot $SteamRoot `
        -WorkshopRoot $WorkshopRoot `
        -NonInteractive:$NonInteractive

    Write-Host "Workshop ACF: $($workshop.AcfPath)"
    Write-Host "Workshop content: $($workshop.ContentRoot)"
    $resolvedCoop = Resolve-CoopModuleRoot -Workshop $workshop -CoopModuleRoot $CoopModuleRoot -NonInteractive:$NonInteractive
    Write-Host "Friend Edition Coop input: $resolvedCoop"
    Write-Host 'Validating subscriptions, pinned Steam manifests, module metadata, exclusions, and SHA-256 hashes...'
    $plan = New-WorkshopSuitePlan -Manifest $manifest -Workshop $workshop -CoopModuleRoot $resolvedCoop

    $fileCount = @($plan.Modules | ForEach-Object { $_.IncludedFiles }).Count
    $excludedCount = @($plan.Modules | ForEach-Object { $_.ExcludedFiles }).Count
    $bytes = ($plan.Modules | ForEach-Object { ($_.IncludedFiles | Measure-Object -Property Length -Sum).Sum } | Measure-Object -Sum).Sum
    $coopBytes = ($plan.Coop.IncludedFiles | Measure-Object -Property Length -Sum).Sum
    Write-Host ("Validated {0} managed Workshop modules plus Coop, {1} Workshop files ({2:N2} MiB), {3} Coop files ({4:N2} MiB), {5} Workshop exclusions, and {6} Coop exclusions." -f $plan.Modules.Count, $fileCount, ($bytes / 1MB), $plan.Coop.IncludedFiles.Count, ($coopBytes / 1MB), $excludedCount, $plan.Coop.ExcludedFiles.Count) -ForegroundColor Green
    Write-Host ("Assembly audit: {0} DLL payloads inspected, {1} active managed assemblies, {2} provider/orphan closure proofs, {3} explicit strong-named side-by-side allowances, zero unresolved same-identity duplicates." -f $plan.AssemblyAudit.InspectedDllCount, $plan.AssemblyAudit.ManagedDllCount, $plan.AssemblyAudit.ClosureProofs.Count, $plan.AssemblyAudit.SideBySideAllowances.Count) -ForegroundColor Green

    if ($ValidateOnly) {
        Write-Host 'Validation completed. No package output or Workshop source files were written. The ignored local AssemblyInspector build cache may be regenerated.' -ForegroundColor Green
        exit 0
    }
    if ($DryRun) {
        Write-Host ''
        Write-Host "Dry run target: $([System.IO.Path]::GetFullPath($OutputRoot))"
        foreach ($module in $plan.Modules) {
            Write-Host ("  {0:D3} {1} {2}: {3} files, {4} excluded, source digest {5}" -f [int]$module.Config.loadOrder, $module.Descriptor.ModuleId, $module.Descriptor.Version, $module.IncludedFiles.Count, $module.ExcludedFiles.Count, $module.SourceSnapshot.Digest)
            foreach ($excluded in $module.ExcludedFiles) {
                Write-Host "      omit $($excluded.RelativePath) -- $($excluded.Reason)"
            }
        }
        Write-Host ("  Coop {0}: {1} files, {2} excluded (managed receipt will be installed at Modules/Coop/WorkshopSuite/MANIFEST.json)" -f $plan.Coop.Descriptor.Version, $plan.Coop.IncludedFiles.Count, $plan.Coop.ExcludedFiles.Count)
        foreach ($excluded in $plan.Coop.ExcludedFiles) {
            Write-Host "      omit Coop/$($excluded.RelativePath) -- $($excluded.Reason)"
        }
        Write-Host 'Dry run completed. No package output or Workshop source files were written; the ignored local AssemblyInspector build cache may be regenerated. Stale, unsubscribed Workshop directories were ignored.' -ForegroundColor Green
        exit 0
    }

    $result = Invoke-WorkshopSuiteStage -Plan $plan -OutputRoot $OutputRoot -ArchivePath $ArchivePath -Force:$Force
    Write-Host "Suite staged at: $($result.OutputRoot)" -ForegroundColor Green
    Write-Host "Verified files: $($result.VerifiedFiles); source Workshop snapshots unchanged: $($result.SourceUnchanged)"
    if ($null -ne $result.Archive) {
        Write-Host "Archive: $($result.Archive.Path)" -ForegroundColor Green
        Write-Host "Archive SHA-256: $($result.Archive.Sha256)"
    }
}
catch {
    Write-Error $_
    exit 1
}
