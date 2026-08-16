#Requires -Version 5.1

[CmdletBinding()]
param(
    [string]$RepoRoot = '',
    [string[]]$AssemblyPaths = @(
        'source\GameInterface\bin\Release\netstandard2.0\GameInterface.dll',
        'source\Coop.Core\bin\Release\netstandard2.0\Coop.Core.dll'
    ),
    [string]$AuthorityPolicyPath = 'tools\WorkshopIntegration\authority-dispositions.json',
    [string]$RoutePolicyPath = 'tools\WorkshopIntegration\authority-route-audit-policy.json',
    [string]$InspectorProjectPath = 'tools\WorkshopIntegration\AssemblyInspector\FriendEdition.WorkshopAssemblyInspector.csproj',
    [string]$OutputPath = 'doc\generated\authority-route-catalog.json',
    [switch]$Release
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($RepoRoot)) { $RepoRoot = Join-Path $PSScriptRoot '..\..' }

function Resolve-RepoPath {
    param([string]$Repo, [string]$Path)
    if ([IO.Path]::IsPathRooted($Path)) { return [IO.Path]::GetFullPath($Path) }
    return [IO.Path]::GetFullPath((Join-Path $Repo $Path))
}

function Get-RelativeRepoPath {
    param([string]$Repo, [string]$Path)
    $rootUri = [Uri](([IO.Path]::GetFullPath($Repo).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar))
    $pathUri = [Uri][IO.Path]::GetFullPath($Path)
    return [Uri]::UnescapeDataString($rootUri.MakeRelativeUri($pathUri).ToString()).Replace('\', '/')
}

function Add-Issue {
    param([Collections.Generic.List[string]]$Issues, [string]$Value)
    if (-not $Issues.Contains($Value)) { $Issues.Add($Value) }
}

$repo = [IO.Path]::GetFullPath($RepoRoot)
$resolvedOutput = Resolve-RepoPath $repo $OutputPath
$resolvedAuthorityPolicy = Resolve-RepoPath $repo $AuthorityPolicyPath
$resolvedRoutePolicy = Resolve-RepoPath $repo $RoutePolicyPath
if (-not (Test-Path -LiteralPath $resolvedAuthorityPolicy -PathType Leaf)) { throw "Authority policy missing: $resolvedAuthorityPolicy" }
if (-not (Test-Path -LiteralPath $resolvedRoutePolicy -PathType Leaf)) { throw "Route audit policy missing: $resolvedRoutePolicy" }

$authorityPolicy = Get-Content -LiteralPath $resolvedAuthorityPolicy -Raw | ConvertFrom-Json
$routePolicy = Get-Content -LiteralPath $resolvedRoutePolicy -Raw | ConvertFrom-Json
if ([int]$routePolicy.schemaVersion -ne 1) { throw 'Route audit policy schemaVersion must be 1.' }
$allowedMessageDispositions = @($routePolicy.allowedMessageDispositions)
if (($allowedMessageDispositions -join ',') -cne 'Command,BootstrapQuery,Replication,Internal') {
    throw 'Route audit policy must declare the exact request taxonomy Command/BootstrapQuery/Replication/Internal.'
}

$inspectionFiles = New-Object Collections.Generic.List[object]
foreach ($assemblyPath in $AssemblyPaths) {
    $resolved = Resolve-RepoPath $repo $assemblyPath
    if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) { throw "Route catalog assembly missing: $resolved" }
    $inspectionFiles.Add([ordered]@{
        moduleId = [IO.Path]::GetFileNameWithoutExtension($resolved)
        relativePath = Get-RelativeRepoPath $repo $resolved
        path = $resolved
        included = $true
        platform = 'production'
    })
}

$tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('friend-edition-route-catalog-' + [guid]::NewGuid().ToString('N'))
try {
    New-Item -ItemType Directory -Path $tempRoot | Out-Null
    $requestPath = Join-Path $tempRoot 'request.json'
    $inspectionPath = Join-Path $tempRoot 'inspection.json'
    [IO.File]::WriteAllText($requestPath, (@{ files = $inspectionFiles.ToArray() } | ConvertTo-Json -Depth 6), (New-Object Text.UTF8Encoding($false)))
    $dotnet = 'C:\Program Files\dotnet\dotnet.exe'
    if (-not (Test-Path -LiteralPath $dotnet)) { throw "Required x64 .NET SDK missing: $dotnet" }
    $inspector = Resolve-RepoPath $repo $InspectorProjectPath
    if (-not (Test-Path -LiteralPath $inspector -PathType Leaf)) { throw "Assembly inspector project missing: $inspector" }
    & $dotnet run --project $inspector -c Release -- $requestPath $inspectionPath
    if ($LASTEXITCODE -ne 0) { throw 'Assembly inspector failed while generating the authority route catalog.' }
    $inspections = @((Get-Content -LiteralPath $inspectionPath -Raw | ConvertFrom-Json).files)
}
finally {
    if (Test-Path -LiteralPath $tempRoot) {
        $safeTemp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
        $safeTarget = [IO.Path]::GetFullPath($tempRoot)
        if (-not $safeTarget.StartsWith($safeTemp, [StringComparison]::OrdinalIgnoreCase) -or
            (Split-Path -Leaf $safeTarget) -notlike 'friend-edition-route-catalog-*') {
            throw "Refusing unsafe route catalog cleanup: $safeTarget"
        }
        Remove-Item -LiteralPath $safeTarget -Recurse -Force
    }
}

$sourceRoots = @((Join-Path $repo 'source\GameInterface'), (Join-Path $repo 'source\Coop.Core'))
$productionSources = @(foreach ($root in $sourceRoots) {
    Get-ChildItem -LiteralPath $root -Recurse -File -Filter '*.cs' |
        Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' -and $_.FullName -notmatch '\.Tests[\\/]' }
})
$testSources = @(Get-ChildItem -LiteralPath (Join-Path $repo 'source') -Recurse -File -Filter '*.cs' |
    Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' -and $_.FullName -match '(Tests|Test)[\\/]' })
$sourceContent = @{}
foreach ($file in @($productionSources + $testSources)) { $sourceContent[$file.FullName] = Get-Content -LiteralPath $file.FullName -Raw }

$issues = New-Object Collections.Generic.List[string]
$routes = New-Object Collections.Generic.List[object]
$routeById = @{}
$routeByRequest = @{}
foreach ($assembly in $inspections) {
    if (-not [bool]$assembly.managed) { throw "Route catalog input is not managed: $($assembly.relativePath)" }
    foreach ($type in @($assembly.types | Where-Object { $null -ne $_.authorityRoute })) {
        $routeId = [string]$type.authorityRoute.routeId
        $requestType = [string]$type.name
        $requestName = ($requestType -split '[.+]')[-1]
        $kind = switch ([int]$type.authorityRoute.kind) { 0 { 'Command' } 1 { 'BootstrapQuery' } default { "Unknown($([int]$type.authorityRoute.kind))" } }
        $implementsCommand = @($type.interfaces | Where-Object { [string]$_ -ceq 'Common.Messaging.ICommand' }).Count -eq 1

        $definitionFiles = New-Object Collections.Generic.List[string]
        $resultTypes = New-Object Collections.Generic.List[string]
        foreach ($file in $productionSources) {
            $content = [string]$sourceContent[$file.FullName]
            $definitionPattern = 'AuthorityRoute\s*<(?<args>[^>]+)>\s*\.Define\s*\('
            foreach ($match in [regex]::Matches($content, $definitionPattern, [Text.RegularExpressions.RegexOptions]::Singleline)) {
                $arguments = @($match.Groups['args'].Value.Split(',') | ForEach-Object { $_.Trim() })
                if ($arguments.Count -ne 3) { continue }
                $candidateRequest = ($arguments[1] -split '\.')[-1]
                if ($candidateRequest -cne $requestName) { continue }
                $relative = Get-RelativeRepoPath $repo $file.FullName
                if (-not $definitionFiles.Contains($relative)) { $definitionFiles.Add($relative) }
                if (-not $resultTypes.Contains($arguments[2])) { $resultTypes.Add($arguments[2]) }
            }
        }
        $tests = New-Object Collections.Generic.List[string]
        foreach ($file in $testSources) {
            $content = [string]$sourceContent[$file.FullName]
            if ($content.Contains($routeId) -or [regex]::IsMatch($content, "\b$([regex]::Escape($requestName))\b")) {
                $relative = Get-RelativeRepoPath $repo $file.FullName
                if (-not $tests.Contains($relative)) { $tests.Add($relative) }
            }
        }

        $record = [ordered]@{
            module = [string]$assembly.moduleId
            assembly = [string]$assembly.relativePath
            routeId = $routeId
            kind = $kind
            requestType = $requestType
            implementsCommand = $implementsCommand
            resultTypes = @($resultTypes | Sort-Object)
            owners = @($definitionFiles | Sort-Object)
            tests = @($tests | Sort-Object)
        }
        if ($routeById.ContainsKey($routeId)) {
            Add-Issue $issues "module=$($assembly.moduleId) message=$requestType route=$routeId owner=$($definitionFiles -join ',') test=$($tests -join ','): duplicate route id also declared by $($routeById[$routeId].requestType)"
        }
        else { $routeById.Add($routeId, $record) }
        if ($routeByRequest.ContainsKey($requestType)) {
            Add-Issue $issues "module=$($assembly.moduleId) message=$requestType route=$routeId owner=$($definitionFiles -join ',') test=$($tests -join ','): duplicate request type"
        }
        else { $routeByRequest.Add($requestType, $record) }
        if (-not $implementsCommand) { Add-Issue $issues "module=$($assembly.moduleId) message=$requestType route=$routeId owner=$($definitionFiles -join ',') test=$($tests -join ','): routed request does not implement ICommand" }
        if ($kind.StartsWith('Unknown', [StringComparison]::Ordinal)) { Add-Issue $issues "module=$($assembly.moduleId) message=$requestType route=$routeId owner=$($definitionFiles -join ',') test=$($tests -join ','): unknown route kind" }
        if ($definitionFiles.Count -eq 0) { Add-Issue $issues "module=$($assembly.moduleId) message=$requestType route=$routeId owner=<missing> test=$($tests -join ','): no typed route definition owner" }
        if ($resultTypes.Count -eq 0) { Add-Issue $issues "module=$($assembly.moduleId) message=$requestType route=$routeId owner=$($definitionFiles -join ',') test=$($tests -join ','): no typed result type" }
        elseif ($resultTypes.Count -ne 1) { Add-Issue $issues "module=$($assembly.moduleId) message=$requestType route=$routeId owner=$($definitionFiles -join ',') test=$($tests -join ','): ambiguous typed result contracts $($resultTypes -join ',')" }
        if ($tests.Count -eq 0) { Add-Issue $issues "module=$($assembly.moduleId) message=$requestType route=$routeId owner=$($definitionFiles -join ',') test=<missing>: no focused route contract test" }
        $routes.Add($record)
    }
}

# Every exact Workshop method disposition claiming ServerCommand must name a compiled route.
foreach ($rule in @($authorityPolicy.rules | Where-Object { [string]$_.disposition -ceq 'ServerCommand' })) {
    $routeProperty = $rule.PSObject.Properties['routeId']
    $routeValue = if ($null -eq $routeProperty) { '' } else { [string]$routeProperty.Value }
    $label = "module=$([string]$rule.moduleId) message=<workshop-methods:$(@($rule.metadataTokens).Count)> route=$routeValue owner=$([string]$rule.owner) test=$(@($rule.tests) -join ',')"
    if ($null -eq $routeProperty -or [string]::IsNullOrWhiteSpace([string]$routeProperty.Value)) {
        Add-Issue $issues "$label`: ServerCommand has no routeId"
    }
    elseif (-not $routeById.ContainsKey($routeValue)) {
        Add-Issue $issues "$label`: ServerCommand references an unregistered route"
    }
}

$messageRuleByType = @{}
foreach ($rule in @($routePolicy.messageDispositions)) {
    $typeName = [string]$rule.messageType
    if ($messageRuleByType.ContainsKey($typeName)) { throw "Duplicate message disposition: $typeName" }
    if ([string]$rule.disposition -notin $allowedMessageDispositions) { throw "Unknown message disposition '$($rule.disposition)' for $typeName" }
    $reason = if ($null -eq $rule.PSObject.Properties['reason']) { '' } else { [string]$rule.reason }
    $owner = if ($null -eq $rule.PSObject.Properties['owner']) { '' } else { [string]$rule.owner }
    [array]$policyTests = if ($null -eq $rule.PSObject.Properties['tests']) { @() } else { @($rule.tests) }
    if ([string]::IsNullOrWhiteSpace($reason) -or [string]::IsNullOrWhiteSpace($owner) -or $policyTests.Count -eq 0) {
        throw "Incomplete message disposition: module=<policy> message=$typeName route=<none> owner=$owner test=$($policyTests -join ',')"
    }
    $ownerSymbol = ($owner -split '\.')[-1]
    [array]$ownerMatches = @($productionSources | Where-Object { [regex]::IsMatch([string]$sourceContent[$_.FullName], "\b$([regex]::Escape($ownerSymbol))\b") })
    if ($ownerMatches.Count -eq 0) {
        throw "Unknown message disposition owner: module=<policy> message=$typeName route=<none> owner=$owner test=$($policyTests -join ',')"
    }
    foreach ($test in $policyTests) {
        $testSymbol = ([string]$test -split '\.')[-1]
        [array]$testMatches = @($testSources | Where-Object { [regex]::IsMatch([string]$sourceContent[$_.FullName], "\b$([regex]::Escape($testSymbol))\b") })
        if ($testMatches.Count -eq 0) {
            throw "Unknown message disposition test: module=<policy> message=$typeName route=<none> owner=$owner test=$test"
        }
    }
    $messageRuleByType.Add($typeName, $rule)
}
$messages = New-Object Collections.Generic.List[object]
foreach ($assembly in $inspections) {
    foreach ($type in @($assembly.types)) {
        $typeName = [string]$type.name
        $isCommand = @($type.interfaces | Where-Object { [string]$_ -ceq 'Common.Messaging.ICommand' }).Count -eq 1
        if (-not $isCommand) { continue }
        $leafName = ($typeName -split '[.+]')[-1]
        $isRequestLike = $null -ne $type.authorityRoute -or $leafName -match '(?i)(Request|Requested|Attempt|Submit|Submission)'
        if (-not $isRequestLike) { continue }
        $messageSources = @($productionSources | Where-Object {
            [regex]::IsMatch([string]$sourceContent[$_.FullName], "(?m)\b(?:class|record|struct)\s+$([regex]::Escape($leafName))\b")
        } | ForEach-Object { Get-RelativeRepoPath $repo $_.FullName } | Sort-Object -Unique)
        $messageTests = @($testSources | Where-Object {
            [regex]::IsMatch([string]$sourceContent[$_.FullName], "\b$([regex]::Escape($leafName))\b")
        } | ForEach-Object { Get-RelativeRepoPath $repo $_.FullName } | Sort-Object -Unique)
        $disposition = if ($null -ne $type.authorityRoute) {
            if ([int]$type.authorityRoute.kind -eq 1) { 'BootstrapQuery' } else { 'Command' }
        }
        elseif ($messageRuleByType.ContainsKey($typeName)) { [string]$messageRuleByType[$typeName].disposition }
        else { 'Unclassified' }
        $messages.Add([ordered]@{ module = [string]$assembly.moduleId; messageType = $typeName; disposition = $disposition; sourcePaths = $messageSources; tests = $messageTests })
        if ($disposition -ceq 'Unclassified') {
            $ownerLabel = if ($messageSources.Count -eq 0) { '<missing>' } else { $messageSources -join ',' }
            $testLabel = if ($messageTests.Count -eq 0) { '<missing>' } else { $messageTests -join ',' }
            Add-Issue $issues "module=$($assembly.moduleId) message=$typeName route=<none> owner=$ownerLabel test=$testLabel`: request-like ICommand has no Command/BootstrapQuery/Replication/Internal disposition"
        }
        elseif ($null -eq $type.authorityRoute -and $disposition -ceq 'Command') {
            $ownerLabel = if ($messageSources.Count -eq 0) { '<missing>' } else { $messageSources -join ',' }
            $testLabel = if ($messageTests.Count -eq 0) { '<missing>' } else { $messageTests -join ',' }
            Add-Issue $issues "module=$($assembly.moduleId) message=$typeName route=<none> owner=$ownerLabel test=$testLabel`: Command disposition has no compiled AuthorityRoute"
        }
    }
}
foreach ($typeName in $messageRuleByType.Keys) {
    if (@($messages | Where-Object { [string]$_.messageType -ceq $typeName }).Count -eq 0) { Add-Issue $issues "module=<policy> message=$typeName route=<none> owner=$([string]$messageRuleByType[$typeName].owner) test=$(@($messageRuleByType[$typeName].tests) -join ','): stale message disposition" }
}

# A routed request may only be subscribed by the router. Direct feature subscriptions and obvious
# direct sends are bypasses; exceptional framework bridges must be exact, named and test-owned.
$bypasses = New-Object Collections.Generic.List[object]
foreach ($route in $routes) {
    $requestName = (([string]$route.requestType) -split '[.+]')[-1]
    foreach ($file in $productionSources | Where-Object { $_.FullName -notlike '*\Services\AuthorityRequests\AuthorityRequestRouter.cs' }) {
        $content = [string]$sourceContent[$file.FullName]
        $operation = $null
        if ([regex]::IsMatch($content, "Subscribe\s*<\s*$([regex]::Escape($requestName))\s*>")) { $operation = 'Subscribe' }
        $sendThenNew = [regex]::IsMatch($content, "(?s)\.Send(?:Immediate|All|AllBut)?\s*\(.{0,500}?\bnew\s+$([regex]::Escape($requestName))\b")
        if ($sendThenNew) { $operation = if ($null -eq $operation) { 'Send' } else { 'Send+Subscribe' } }
        if ($null -eq $operation) { continue }
        $bypasses.Add([ordered]@{ module = [string]$route.module; sourcePath = Get-RelativeRepoPath $repo $file.FullName; messageType = [string]$route.requestType; routeId = [string]$route.routeId; operation = $operation })
    }
}
$exemptions = @($routePolicy.bypassExemptions)
foreach ($bypass in $bypasses) {
    $matches = @($exemptions | Where-Object {
        [string]$_.sourcePath -ceq [string]$bypass.sourcePath -and
        [string]$_.messageType -ceq [string]$bypass.messageType -and
        [string]$_.operation -ceq [string]$bypass.operation
    })
    if ($matches.Count -ne 1) {
        Add-Issue $issues "module=$($bypass.module) message=$($bypass.messageType) route=$($bypass.routeId) owner=$($bypass.sourcePath) test=<missing>: direct client Command $($bypass.operation) bypasses AuthorityRequestRouter"
    }
    elseif ([string]::IsNullOrWhiteSpace([string]$matches[0].reason) -or [string]::IsNullOrWhiteSpace([string]$matches[0].owner) -or @($matches[0].tests).Count -eq 0) {
        Add-Issue $issues "module=$($bypass.module) message=$($bypass.messageType) route=$($bypass.routeId) owner=$($bypass.sourcePath) test=<missing>: bypass exemption is not reason/owner/test complete"
    }
}
foreach ($exemption in $exemptions) {
    $matches = @($bypasses | Where-Object { [string]$_.sourcePath -ceq [string]$exemption.sourcePath -and [string]$_.messageType -ceq [string]$exemption.messageType -and [string]$_.operation -ceq [string]$exemption.operation })
    if ($matches.Count -ne 1) { Add-Issue $issues "module=<policy> message=$([string]$exemption.messageType) route=<unknown> owner=$([string]$exemption.owner) test=$(@($exemption.tests) -join ','): stale bypass exemption" }
}

# Workshop capability declarations must gate on a catalogued route, an accepted-session snapshot,
# and Ready state. This is deliberately a source contract: the route IDs are stable literals.
$routeConstantByName = @{}
foreach ($file in $productionSources) {
    foreach ($match in [regex]::Matches([string]$sourceContent[$file.FullName], '(?:const|static\s+readonly)\s+string\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*=\s*"(?<route>[a-z0-9.-]+)"')) {
        $name = $match.Groups['name'].Value
        $route = $match.Groups['route'].Value
        if (-not $routeConstantByName.ContainsKey($name)) { $routeConstantByName.Add($name, $route) }
        elseif ([string]$routeConstantByName[$name] -cne $route) { $routeConstantByName[$name] = $null }
    }
}
$inactiveHoldProperty = $authorityPolicy.PSObject.Properties['inactiveModuleHolds']
$inactiveModuleHolds = if ($null -eq $inactiveHoldProperty) { @() } else { @($inactiveHoldProperty.Value) }
$capabilities = New-Object Collections.Generic.List[object]
foreach ($file in $productionSources | Where-Object { [regex]::IsMatch([string]$sourceContent[$_.FullName], 'class\s+[A-Za-z_][A-Za-z0-9_]*\s*:\s*(?:Core\.)?IWorkshopCapabilitySource') }) {
    $content = [string]$sourceContent[$file.FullName]
    $relative = Get-RelativeRepoPath $repo $file.FullName
    $moduleMatch = [regex]::Match($content, 'const\s+string\s+ModuleId\s*=\s*"(?<module>[^"]+)"')
    $moduleId = if ($moduleMatch.Success) { $moduleMatch.Groups['module'].Value } else { '' }
    $hold = @($inactiveModuleHolds | Where-Object { [string]$_.moduleId -ceq $moduleId })
    $heldUnavailable = $hold.Count -eq 1 -and [string]$hold[0].disposition -in @('Unsupported', 'GuardedFeatureBlocked') -and
        $content.Contains('false') -and $content.Contains([string]$hold[0].reason)
    $routeIds = New-Object Collections.Generic.List[string]
    foreach ($match in [regex]::Matches($content, 'IsRegistered\s*\(\s*(?<route>[^,\r\n]+)')) {
        $expression = $match.Groups['route'].Value.Trim()
        $routeId = if ($expression -match '^"(?<literal>[a-z0-9.-]+)"$') { $Matches['literal'] }
            else {
                $name = ($expression -split '\.')[-1]
                if ($routeConstantByName.ContainsKey($name)) { [string]$routeConstantByName[$name] } else { '' }
            }
        if (-not [string]::IsNullOrWhiteSpace($routeId) -and -not $routeIds.Contains($routeId)) { $routeIds.Add($routeId) }
    }
    $routeIds = @($routeIds | Sort-Object -Unique)
    $ready = $content.Contains('WorkshopSnapshotReadiness.Ready') -or $content.Contains('configAuthority.TryGetCurrent')
    $currentSession = $content.Contains('SessionId') -or $content.Contains('configAuthority.TryGetCurrent')
    $capabilities.Add([ordered]@{ moduleId = $moduleId; sourcePath = $relative; routeIds = $routeIds; ready = $ready; currentSession = $currentSession; heldUnavailable = $heldUnavailable })
    if (-not $heldUnavailable -and $routeIds.Count -eq 0) { Add-Issue $issues "module=GameInterface message=WorkshopCapability route=<missing> owner=$relative test=<missing>: capability declares no literal registered route" }
    foreach ($routeId in $routeIds) { if (-not $routeById.ContainsKey($routeId)) { Add-Issue $issues "module=GameInterface message=WorkshopCapability route=$routeId owner=$relative test=<missing>: capability references an unregistered route" } }
    if (-not $heldUnavailable -and (-not $ready -or -not $currentSession)) { Add-Issue $issues "module=GameInterface message=WorkshopCapability route=$($routeIds -join ',') owner=$relative test=<missing>: capability is not gated by current-session Ready" }
}

$catalog = [ordered]@{
    schemaVersion = 1
    assemblies = @($inspections | ForEach-Object { [ordered]@{ module = [string]$_.moduleId; path = [string]$_.relativePath; sha256 = [string]$_.sha256 } })
    summary = [ordered]@{ routeCount = $routes.Count; requestLikeMessageCount = $messages.Count; bypassCount = $bypasses.Count; capabilitySourceCount = $capabilities.Count; issueCount = $issues.Count }
    routes = @($routes | Sort-Object routeId)
    messageDispositions = @($messages | Sort-Object module,messageType)
    bypasses = @($bypasses | Sort-Object sourcePath,messageType)
    capabilities = @($capabilities | Sort-Object sourcePath)
    issues = $issues.ToArray()
}
$outputParent = Split-Path -Parent $resolvedOutput
if (-not (Test-Path -LiteralPath $outputParent -PathType Container)) { New-Item -ItemType Directory -Path $outputParent -Force | Out-Null }
[IO.File]::WriteAllText($resolvedOutput, ($catalog | ConvertTo-Json -Depth 20 -Compress) + [Environment]::NewLine, (New-Object Text.UTF8Encoding($false)))
Write-Host "WROTE $resolvedOutput"
Write-Host "ROUTES=$($routes.Count) REQUEST_MESSAGES=$($messages.Count) BYPASSES=$($bypasses.Count) CAPABILITIES=$($capabilities.Count) ISSUES=$($issues.Count)"
if ($Release -and $issues.Count -gt 0) { throw "AUTHORITY ROUTE AUDIT RELEASE FAILED: $($issues.Count) issue(s). $($issues -join '; ')" }
exit 0
