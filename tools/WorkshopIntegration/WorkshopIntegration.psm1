Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Write-Utf8File {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][AllowEmptyString()][string]$Content
    )

    $parent = Split-Path -Parent $Path
    if ($parent -and -not (Test-Path -LiteralPath $parent -PathType Container)) {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
    }

    $encoding = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($Path, $Content, $encoding)
}

function Get-FullPath {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$Path)

    return [System.IO.Path]::GetFullPath($Path.Trim().Trim('"'))
}

function Test-PathIsInside {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Candidate,
        [Parameter(Mandatory = $true)][string]$Parent
    )

    $candidatePath = (Get-FullPath $Candidate).TrimEnd('\', '/')
    $parentPath = (Get-FullPath $Parent).TrimEnd('\', '/')
    if ($candidatePath.Equals($parentPath, [System.StringComparison]::OrdinalIgnoreCase)) {
        return $true
    }

    $prefix = $parentPath + [System.IO.Path]::DirectorySeparatorChar
    return $candidatePath.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)
}

function Assert-SafeOutputPath {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$OutputPath,
        [Parameter(Mandatory = $true)][string[]]$ProtectedPaths
    )

    $resolved = Get-FullPath $OutputPath
    $root = [System.IO.Path]::GetPathRoot($resolved)
    if ([string]::IsNullOrWhiteSpace($resolved) -or $resolved.Length -lt 12 -or $resolved -eq $root) {
        throw "Refusing suspicious output path '$resolved'."
    }

    foreach ($protected in $ProtectedPaths) {
        if ([string]::IsNullOrWhiteSpace($protected)) {
            continue
        }

        if ((Test-PathIsInside -Candidate $resolved -Parent $protected) -or
            (Test-PathIsInside -Candidate $protected -Parent $resolved)) {
            throw "Output path '$resolved' overlaps protected source path '$protected'."
        }
    }

    return $resolved
}

function ConvertFrom-ValveKeyValues {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$Text)

    $tokenMatches = [System.Text.RegularExpressions.Regex]::Matches(
        $Text,
        '"((?:\\.|[^"\\])*)"|([{}])',
        [System.Text.RegularExpressions.RegexOptions]::CultureInvariant
    )
    $tokens = New-Object System.Collections.Generic.List[string]
    foreach ($match in $tokenMatches) {
        if ($match.Groups[1].Success) {
            $value = $match.Groups[1].Value
            $value = $value.Replace('\"', '"').Replace('\\', '\')
            $tokens.Add($value)
        }
        else {
            $tokens.Add($match.Groups[2].Value)
        }
    }

    function Read-ValveObject {
        param(
            [Parameter(Mandatory = $true)][AllowEmptyString()][AllowEmptyCollection()][System.Collections.Generic.List[string]]$TokenList,
            [Parameter(Mandatory = $true)][ref]$Position,
            [switch]$StopAtBrace
        )

        $result = [ordered]@{}
        while ($Position.Value -lt $TokenList.Count) {
            $token = $TokenList[$Position.Value]
            if ($token -eq '}') {
                if (-not $StopAtBrace) {
                    throw 'Unexpected closing brace in Valve KeyValues document.'
                }
                $Position.Value++
                return $result
            }
            if ($token -eq '{') {
                throw 'Unexpected opening brace in Valve KeyValues document.'
            }

            $key = $token
            $Position.Value++
            if ($Position.Value -ge $TokenList.Count) {
                throw "Missing value for Valve KeyValues key '$key'."
            }

            $next = $TokenList[$Position.Value]
            if ($next -eq '{') {
                $Position.Value++
                $result[$key] = Read-ValveObject -TokenList $TokenList -Position $Position -StopAtBrace
            }
            elseif ($next -eq '}') {
                throw "Missing value for Valve KeyValues key '$key'."
            }
            else {
                $result[$key] = $next
                $Position.Value++
            }
        }

        if ($StopAtBrace) {
            throw 'Unclosed object in Valve KeyValues document.'
        }
        return $result
    }

    $index = 0
    return Read-ValveObject -TokenList $tokens -Position ([ref]$index)
}

function Read-ValveKeyValuesFile {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Valve KeyValues file does not exist: $Path"
    }
    return ConvertFrom-ValveKeyValues -Text ([System.IO.File]::ReadAllText((Get-FullPath $Path)))
}

function Get-SteamLibraryRoots {
    [CmdletBinding()]
    param([string]$SteamRoot)

    $candidates = New-Object System.Collections.Generic.List[string]
    if (-not [string]::IsNullOrWhiteSpace($SteamRoot)) {
        $candidates.Add((Get-FullPath $SteamRoot))
    }

    foreach ($registryPath in @(
        'HKCU:\Software\Valve\Steam',
        'HKLM:\Software\WOW6432Node\Valve\Steam',
        'HKLM:\Software\Valve\Steam'
    )) {
        try {
            $properties = Get-ItemProperty -Path $registryPath -ErrorAction Stop
            foreach ($propertyName in @('SteamPath', 'InstallPath')) {
                $value = $properties.$propertyName
                if (-not [string]::IsNullOrWhiteSpace($value)) {
                    $candidates.Add((Get-FullPath $value))
                }
            }
        }
        catch {
            Write-Verbose "Steam registry location unavailable: $registryPath"
        }
    }

    if (${env:ProgramFiles(x86)}) {
        $candidates.Add((Join-Path ${env:ProgramFiles(x86)} 'Steam'))
    }
    if ($env:ProgramFiles) {
        $candidates.Add((Join-Path $env:ProgramFiles 'Steam'))
    }

    $expanded = New-Object System.Collections.Generic.List[string]
    foreach ($candidate in $candidates) {
        if ([string]::IsNullOrWhiteSpace($candidate)) {
            continue
        }

        $candidatePath = Get-FullPath $candidate
        if ((Split-Path -Leaf $candidatePath) -ieq 'steamapps') {
            $candidatePath = Split-Path -Parent $candidatePath
        }
        $expanded.Add($candidatePath)

        $libraryFolders = Join-Path $candidatePath 'steamapps\libraryfolders.vdf'
        if (Test-Path -LiteralPath $libraryFolders -PathType Leaf) {
            try {
                $document = Read-ValveKeyValuesFile -Path $libraryFolders
                $folders = $document['libraryfolders']
                if ($null -ne $folders) {
                    foreach ($key in $folders.Keys) {
                        $entry = $folders[$key]
                        if ($entry -is [System.Collections.IDictionary] -and $entry.Contains('path')) {
                            $expanded.Add((Get-FullPath ([string]$entry['path'])))
                        }
                    }
                }
            }
            catch {
                Write-Warning "Could not parse '$libraryFolders': $($_.Exception.Message)"
            }
        }
    }

    return @($expanded |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) -and (Test-Path -LiteralPath $_ -PathType Container -ErrorAction SilentlyContinue) } |
        Sort-Object -Unique)
}

function Resolve-WorkshopRootInput {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$AppId
    )

    $resolved = Get-FullPath $Path
    if (Test-Path -LiteralPath $resolved -PathType Leaf) {
        if ((Split-Path -Leaf $resolved) -ieq "appworkshop_$AppId.acf") {
            return Split-Path -Parent $resolved
        }
        throw "Workshop path is a file but is not appworkshop_$AppId.acf: $resolved"
    }

    if (Test-Path -LiteralPath (Join-Path $resolved "appworkshop_$AppId.acf") -PathType Leaf) {
        return $resolved
    }
    if (Test-Path -LiteralPath (Join-Path $resolved "steamapps\workshop\appworkshop_$AppId.acf") -PathType Leaf) {
        return Join-Path $resolved 'steamapps\workshop'
    }
    if ((Split-Path -Leaf $resolved) -eq $AppId -and (Split-Path -Leaf (Split-Path -Parent $resolved)) -ieq 'content') {
        return Split-Path -Parent (Split-Path -Parent $resolved)
    }

    return $resolved
}

function Find-BannerlordWorkshop {
    [CmdletBinding()]
    param(
        [string]$AppId = '261550',
        [string]$SteamRoot,
        [string]$WorkshopRoot,
        [switch]$NonInteractive
    )

    $candidateWorkshopRoots = New-Object System.Collections.Generic.List[string]
    if (-not [string]::IsNullOrWhiteSpace($WorkshopRoot)) {
        $candidateWorkshopRoots.Add((Resolve-WorkshopRootInput -Path $WorkshopRoot -AppId $AppId))
    }
    foreach ($library in (Get-SteamLibraryRoots -SteamRoot $SteamRoot)) {
        $candidateWorkshopRoots.Add(([System.IO.Path]::Combine($library, 'steamapps', 'workshop')))
    }

    while ($true) {
        foreach ($candidate in @($candidateWorkshopRoots | Sort-Object -Unique)) {
            $acfPath = Join-Path $candidate "appworkshop_$AppId.acf"
            $contentRoot = Join-Path $candidate "content\$AppId"
            if ((Test-Path -LiteralPath $acfPath -PathType Leaf) -and
                (Test-Path -LiteralPath $contentRoot -PathType Container)) {
                return [pscustomobject]@{
                    AppId        = $AppId
                    WorkshopRoot = Get-FullPath $candidate
                    AcfPath      = Get-FullPath $acfPath
                    ContentRoot  = Get-FullPath $contentRoot
                }
            }
        }

        if ($NonInteractive) {
            break
        }
        $answer = Read-Host "Steam Workshop for Bannerlord was not found. Enter the Steam library root, workshop root, or appworkshop_$AppId.acf path"
        if ([string]::IsNullOrWhiteSpace($answer)) {
            break
        }
        $candidateWorkshopRoots.Clear()
        $candidateWorkshopRoots.Add((Resolve-WorkshopRootInput -Path $answer -AppId $AppId))
    }

    throw "Could not locate appworkshop_$AppId.acf and content\$AppId. Pass -SteamRoot or -WorkshopRoot."
}

function Read-WorkshopState {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][pscustomobject]$Workshop)

    $document = Read-ValveKeyValuesFile -Path $Workshop.AcfPath
    $app = $document['AppWorkshop']
    if ($null -eq $app) {
        throw "Invalid Workshop ACF: AppWorkshop root is missing from '$($Workshop.AcfPath)'."
    }
    if ([string]$app['appid'] -ne [string]$Workshop.AppId) {
        throw "Workshop ACF appid '$($app['appid'])' does not match expected '$($Workshop.AppId)'."
    }

    $installed = $app['WorkshopItemsInstalled']
    $details = $app['WorkshopItemDetails']
    if ($null -eq $installed -or $null -eq $details) {
        throw 'Workshop ACF is missing WorkshopItemsInstalled or WorkshopItemDetails.'
    }

    $items = [ordered]@{}
    foreach ($id in $installed.Keys) {
        $installedItem = $installed[$id]
        $detail = $details[$id]
        if ($null -eq $detail) {
            throw "Workshop item $id is installed but has no WorkshopItemDetails record."
        }
        $items[$id] = [pscustomobject]@{
            WorkshopId    = [string]$id
            ManifestId    = [string]$installedItem['manifest']
            LatestManifest = [string]$detail['latest_manifest']
            TimeUpdated   = [string]$installedItem['timeupdated']
            LatestTimeUpdated = [string]$detail['latest_timeupdated']
            DeclaredSize  = [long]$installedItem['size']
        }
    }

    return [pscustomobject]@{
        NeedsUpdate   = [string]$app['NeedsUpdate']
        NeedsDownload = [string]$app['NeedsDownload']
        Items         = $items
    }
}

function Read-WorkshopSuiteManifest {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$Path)

    $manifestPath = Get-FullPath $Path
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw "Workshop suite manifest does not exist: $manifestPath"
    }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    foreach ($property in @('schemaVersion', 'suite', 'modules')) {
        if ($null -eq $manifest.$property) {
            throw "Workshop suite manifest is missing '$property'."
        }
    }
    if ([int]$manifest.schemaVersion -ne 1) {
        throw "Unsupported workshop suite schemaVersion '$($manifest.schemaVersion)'."
    }
    if ([string]::IsNullOrWhiteSpace([string]$manifest.suite.id) -or
        [string]::IsNullOrWhiteSpace([string]$manifest.suite.appId)) {
        throw 'Workshop suite id and appId are required.'
    }

    $moduleIds = @($manifest.modules | ForEach-Object { [string]$_.moduleId })
    $workshopIds = @($manifest.modules | ForEach-Object { [string]$_.workshopId })
    if (@($moduleIds | Where-Object { [string]::IsNullOrWhiteSpace($_) }).Count -gt 0 -or
        @($workshopIds | Where-Object { [string]::IsNullOrWhiteSpace($_) }).Count -gt 0) {
        throw 'Every module requires moduleId and workshopId.'
    }
    if (@($moduleIds | Sort-Object -Unique).Count -ne $moduleIds.Count) {
        throw 'Duplicate moduleId values exist in the workshop suite manifest.'
    }
    if (@($workshopIds | Sort-Object -Unique).Count -ne $workshopIds.Count) {
        throw 'Duplicate workshopId values exist in the workshop suite manifest.'
    }
    if ([int]$manifest.suite.expectedModuleCount -ne $manifest.modules.Count) {
        throw "Suite expectedModuleCount is $($manifest.suite.expectedModuleCount), but $($manifest.modules.Count) modules are configured."
    }

    return $manifest
}

function Get-RelativePathSafe {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$Path
    )

    $rootPath = (Get-FullPath $Root).TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
    $filePath = Get-FullPath $Path
    if (-not $filePath.StartsWith($rootPath, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Path '$filePath' is outside root '$rootPath'."
    }
    return $filePath.Substring($rootPath.Length).Replace('\', '/')
}

function Get-StringSha256 {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$Text)

    $bytes = [System.Text.Encoding]::UTF8.GetBytes($Text)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        return ([System.BitConverter]::ToString($sha.ComputeHash($bytes))).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $sha.Dispose()
    }
}

function Get-DirectorySnapshot {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$Root)

    $rootPath = Get-FullPath $Root
    if (-not (Test-Path -LiteralPath $rootPath -PathType Container)) {
        throw "Snapshot root does not exist: $rootPath"
    }

    $files = New-Object System.Collections.Generic.List[object]
    foreach ($file in @(Get-ChildItem -LiteralPath $rootPath -File -Force -Recurse | Sort-Object FullName)) {
        if (($file.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Refusing reparse-point file in Workshop source: $($file.FullName)"
        }
        $relative = Get-RelativePathSafe -Root $rootPath -Path $file.FullName
        $files.Add([pscustomobject]@{
            RelativePath = $relative
            FullName     = $file.FullName
            Length       = [long]$file.Length
            Sha256       = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        })
    }

    $directoryLinks = @(Get-ChildItem -LiteralPath $rootPath -Directory -Force -Recurse |
        Where-Object { ($_.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0 })
    if ($directoryLinks.Count -gt 0) {
        throw "Refusing reparse-point directory in Workshop source: $($directoryLinks[0].FullName)"
    }

    $digestText = (@($files | ForEach-Object { "$($_.RelativePath)|$($_.Length)|$($_.Sha256)" }) -join "`n")
    return [pscustomobject]@{
        Root   = $rootPath
        Files  = $files.ToArray()
        Digest = Get-StringSha256 -Text $digestText
    }
}

function Get-SubModuleDescriptor {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$ModuleRoot)

    $path = Join-Path $ModuleRoot 'SubModule.xml'
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Workshop module has no root SubModule.xml: $ModuleRoot"
    }
    [xml]$xml = [System.IO.File]::ReadAllText($path)
    $module = $xml.Module
    if ($null -eq $module) {
        throw "SubModule.xml has no Module root: $path"
    }

    $dllNames = New-Object System.Collections.Generic.List[string]
    foreach ($submodule in @($xml.SelectNodes('/Module/SubModules/SubModule'))) {
        $dllName = $submodule.SelectSingleNode('DLLName')
        if ($null -ne $dllName -and -not [string]::IsNullOrWhiteSpace([string]$dllName.GetAttribute('value'))) {
            $dllNames.Add([string]$dllName.GetAttribute('value'))
        }
    }

    $dependencies = New-Object System.Collections.Generic.List[object]
    $dependencyNodes = @($xml.SelectNodes('/Module/DependedModules/DependedModule'))
    if ($dependencyNodes.Count -gt 0) {
        foreach ($dependency in $dependencyNodes) {
            $dependencyId = [string]$dependency.GetAttribute('Id')
            if ([string]::IsNullOrWhiteSpace($dependencyId)) {
                continue
            }
            $optionalText = [string]$dependency.GetAttribute('Optional')
            $dependencies.Add([pscustomobject]@{
                ModuleId = $dependencyId
                Version  = [string]$dependency.GetAttribute('DependentVersion')
                Optional = $optionalText.Equals('true', [System.StringComparison]::OrdinalIgnoreCase)
            })
        }
    }

    return [pscustomobject]@{
        Name             = [string]$module.Name.value
        ModuleId         = [string]$module.Id.value
        Version          = [string]$module.Version.value
        DeclaredDllNames = @($dllNames | Sort-Object -Unique)
        Dependencies     = $dependencies.ToArray()
        Path             = $path
    }
}

function Resolve-CoopModuleRoot {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][pscustomobject]$Workshop,
        [string]$CoopModuleRoot,
        [switch]$NonInteractive
    )

    $candidates = New-Object System.Collections.Generic.List[string]
    if (-not [string]::IsNullOrWhiteSpace($CoopModuleRoot)) {
        $candidates.Add((Get-FullPath $CoopModuleRoot))
    }
    $steamApps = Split-Path -Parent $Workshop.WorkshopRoot
    $candidates.Add(([System.IO.Path]::Combine($steamApps, 'common', 'Mount & Blade II Bannerlord', 'Modules', 'Coop')))

    while ($true) {
        foreach ($candidate in @($candidates | Sort-Object -Unique)) {
            if (Test-Path -LiteralPath (Join-Path $candidate 'SubModule.xml') -PathType Leaf) {
                return Get-FullPath $candidate
            }
        }
        if ($NonInteractive) {
            break
        }
        $answer = Read-Host 'Friend Edition Coop module was not found. Enter the built/staged Coop module root (the directory containing SubModule.xml)'
        if ([string]::IsNullOrWhiteSpace($answer)) {
            break
        }
        $candidates.Clear()
        $candidates.Add((Get-FullPath $answer))
    }

    throw 'A built Friend Edition Coop module is required so the output is one managed distribution. Pass -CoopModuleRoot.'
}

function New-CoopModulePlan {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][pscustomobject]$Manifest,
        [Parameter(Mandatory = $true)][string]$CoopModuleRoot
    )

    if ($null -eq $Manifest.coopModule) {
        throw 'Workshop suite manifest is missing coopModule composition policy.'
    }
    $root = Get-FullPath $CoopModuleRoot
    $descriptor = Get-SubModuleDescriptor -ModuleRoot $root
    if ($descriptor.ModuleId -cne [string]$Manifest.coopModule.moduleId) {
        throw "Coop composition root contains module '$($descriptor.ModuleId)', expected '$($Manifest.coopModule.moduleId)'."
    }
    $snapshot = Get-DirectorySnapshot -Root $root
    $birthAndDeathConfig = @($snapshot.Files | Where-Object { [string]$_.RelativePath -ieq 'mod-config.default.json' })
    if ($birthAndDeathConfig.Count -ne 1 -or
        [System.IO.File]::ReadAllText([string]$birthAndDeathConfig[0].FullName) -notmatch '"birthAndDeath"\s*:\s*true\b') {
        throw 'Coop composition must contain exactly one packaged mod-config.default.json with birthAndDeath=true. This validates only the seed template; deployment preflight must inspect the resolved runtime CoopData/mod-config.json. The optional TaleWorlds BirthAndDeath module remains disabled.'
    }
    $included = New-Object System.Collections.Generic.List[object]
    $excluded = New-Object System.Collections.Generic.List[object]
    $counts = @{}
    foreach ($rule in @($Manifest.coopModule.exclusions)) { $counts[[string]$rule.glob] = 0 }
    foreach ($file in $snapshot.Files) {
        if ([string]$file.RelativePath -ieq 'WorkshopSuite/MANIFEST.json') {
            continue
        }
        $matched = $null
        foreach ($rule in @($Manifest.coopModule.exclusions)) {
            if (Test-RelativeGlob -RelativePath $file.RelativePath -Glob ([string]$rule.glob)) { $matched = $rule; break }
        }
        if ($null -eq $matched) {
            $included.Add($file)
        }
        else {
            $counts[[string]$matched.glob] = [int]$counts[[string]$matched.glob] + 1
            $excluded.Add([pscustomobject]@{
                RelativePath = $file.RelativePath; Length = $file.Length; Sha256 = $file.Sha256
                Rule = [string]$matched.glob; Reason = [string]$matched.reason
            })
        }
    }
    foreach ($rule in @($Manifest.coopModule.exclusions)) {
        $minimum = if ($null -eq $rule.minimumMatches) { 0 } else { [int]$rule.minimumMatches }
        if ([int]$counts[[string]$rule.glob] -lt $minimum) {
            throw "Coop exclusion '$($rule.glob)' matched $($counts[[string]$rule.glob]) files; expected at least $minimum."
        }
    }
    foreach ($declaredDll in $descriptor.DeclaredDllNames) {
        if (@($included | Where-Object { [System.IO.Path]::GetFileName($_.RelativePath) -ceq $declaredDll }).Count -eq 0) {
            throw "Coop SubModule.xml declares '$declaredDll', but the composition policy would omit it."
        }
    }
    return [pscustomobject]@{
        ModuleRoot = $root; Descriptor = $descriptor; SourceSnapshot = $snapshot
        IncludedFiles = @($included | Sort-Object RelativePath)
        ExcludedFiles = @($excluded | Sort-Object RelativePath)
        ReplacedGeneratedFiles = @($snapshot.Files | Where-Object { [string]$_.RelativePath -ieq 'WorkshopSuite/MANIFEST.json' })
        BirthAndDeathConfig = [pscustomobject]@{
            RelativePath = [string]$birthAndDeathConfig[0].RelativePath
            Sha256 = [string]$birthAndDeathConfig[0].Sha256
            PackagedDefaultEnabled = $true
            RuntimeValueVerified = $false
            TaleWorldsModuleActive = $false
        }
    }
}

function Get-AssemblyInspectorPath {
    [CmdletBinding()]
    param()

    $project = Join-Path $PSScriptRoot 'AssemblyInspector\FriendEdition.WorkshopAssemblyInspector.csproj'
    $program = Join-Path $PSScriptRoot 'AssemblyInspector\Program.cs'
    $output = Join-Path $PSScriptRoot 'AssemblyInspector\bin\Release\net8.0\FriendEdition.WorkshopAssemblyInspector.dll'
    $mustBuild = -not (Test-Path -LiteralPath $output -PathType Leaf)
    if (-not $mustBuild) {
        $outputTime = (Get-Item -LiteralPath $output).LastWriteTimeUtc
        $mustBuild = (Get-Item -LiteralPath $project).LastWriteTimeUtc -gt $outputTime -or
            (Get-Item -LiteralPath $program).LastWriteTimeUtc -gt $outputTime
    }
    if ($mustBuild) {
        $dotnet = if (Test-Path -LiteralPath 'C:\Program Files\dotnet\dotnet.exe') {
            'C:\Program Files\dotnet\dotnet.exe'
        }
        else {
            (Get-Command dotnet -ErrorAction Stop).Source
        }
        $buildOutput = & $dotnet build $project -c Release --nologo -v:q 2>&1
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $output -PathType Leaf)) {
            throw "Assembly metadata inspector build failed:`n$($buildOutput -join "`n")"
        }
    }
    return $output
}

function Get-ServerPreflightFiles {
    [CmdletBinding()]
    param()

    $scriptPath = Join-Path $PSScriptRoot 'Verify-ServerHarmony.ps1'
    if (-not (Test-Path -LiteralPath $scriptPath -PathType Leaf)) {
        throw "Server Harmony preflight source is missing: $scriptPath"
    }
    $pythonPath = Join-Path $PSScriptRoot 'Verify-ServerHarmony.py'
    $sources = @(
        [pscustomobject]@{ FullName = $scriptPath; RelativePath = 'Verify-ServerHarmony.ps1' },
        [pscustomobject]@{ FullName = $pythonPath; RelativePath = 'Verify-ServerHarmony.py' }
    )
    $result = New-Object System.Collections.Generic.List[object]
    foreach ($source in $sources) {
        if (-not (Test-Path -LiteralPath $source.FullName -PathType Leaf)) {
            throw "Server Harmony preflight dependency is missing: $($source.FullName)"
        }
        $item = Get-Item -LiteralPath $source.FullName
        $result.Add([pscustomobject]@{
            FullName = $item.FullName
            RelativePath = [string]$source.RelativePath
            Length = $item.Length
            Sha256 = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        })
    }
    return $result.ToArray()
}

function Get-ClientInstallerFiles {
    [CmdletBinding()]
    param()

    $sources = @(
        [pscustomobject]@{
            FullName = (Join-Path $PSScriptRoot 'Setup-ManagedSuiteClient.ps1')
            RelativePath = 'Setup-ManagedSuiteClient.ps1'
        },
        [pscustomobject]@{
            FullName = (Join-Path $PSScriptRoot 'Run-ClientSetup.cmd')
            RelativePath = 'Run-ClientSetup.cmd'
        }
    )
    $result = New-Object System.Collections.Generic.List[object]
    foreach ($source in $sources) {
        if (-not (Test-Path -LiteralPath $source.FullName -PathType Leaf)) {
            throw "Managed client installer dependency is missing: $($source.FullName)"
        }
        $item = Get-Item -LiteralPath $source.FullName
        $result.Add([pscustomobject]@{
            FullName = $item.FullName
            RelativePath = [string]$source.RelativePath
            Length = [long]$item.Length
            Sha256 = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        })
    }
    return $result.ToArray()
}

function Invoke-AssemblyInspector {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][object[]]$Files,
        [ValidateRange(1, 128)][int]$BatchSize = 8
    )

    $inspector = Get-AssemblyInspectorPath
    $dotnet = if (Test-Path -LiteralPath 'C:\Program Files\dotnet\dotnet.exe') {
        'C:\Program Files\dotnet\dotnet.exe'
    }
    else {
        (Get-Command dotnet -ErrorAction Stop).Source
    }
    $temporaryRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('friend-edition-assembly-audit-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $temporaryRoot | Out-Null
    try {
        $inspections = New-Object System.Collections.Generic.List[object]
        for ($offset = 0; $offset -lt $Files.Count; $offset += $BatchSize) {
            $last = [Math]::Min($offset + $BatchSize - 1, $Files.Count - 1)
            $batch = @($Files[$offset..$last])
            $batchNumber = [int]($offset / $BatchSize)
            $requestPath = Join-Path $temporaryRoot ("request-{0:D4}.json" -f $batchNumber)
            $resultPath = Join-Path $temporaryRoot ("result-{0:D4}.json" -f $batchNumber)
            Write-Utf8File -Path $requestPath -Content (([ordered]@{ files = $batch } | ConvertTo-Json -Depth 10) + "`n")
            $toolOutput = & $dotnet $inspector $requestPath $resultPath 2>&1
            if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $resultPath -PathType Leaf)) {
                throw "Assembly metadata inspection batch $batchNumber failed:`n$($toolOutput -join "`n")"
            }
            $result = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
            $errors = @($result.files | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_.error) })
            if ($errors.Count -gt 0) {
                throw "Assembly metadata inspection errors: $(@($errors | ForEach-Object { "$($_.moduleId)/$($_.relativePath): $($_.error)" }) -join '; ')"
            }
            foreach ($inspection in @($result.files)) {
                $inspections.Add($inspection)
            }
        }
        return @($inspections.ToArray())
    }
    finally {
        if (Test-Path -LiteralPath $temporaryRoot) {
            Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
        }
    }
}

function Get-AssemblyPlatform {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$RelativePath)
    $normalized = $RelativePath.Replace('\', '/')
    if ($normalized -match '^bin/([^/]+)/') { return $Matches[1] }
    return 'unscoped'
}

function Test-AssemblyIdentitySatisfiesReference {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][pscustomobject]$Identity,
        [Parameter(Mandatory = $true)][pscustomobject]$Reference,
        [switch]$AllowUnsignedProviderUpgrade,
        [switch]$AllowStrongNamedFrameworkUpgrade
    )
    if ([string]$Identity.name -cne [string]$Reference.name -or
        [string]$Identity.culture -cne [string]$Reference.culture -or
        [string]$Identity.publicKeyToken -cne [string]$Reference.publicKeyToken) { return $false }
    if ([string]$Identity.version -ceq [string]$Reference.version) { return $true }
    if ($AllowUnsignedProviderUpgrade -and [string]$Identity.publicKeyToken -ceq 'null') {
        return ([version]$Identity.version) -ge ([version]$Reference.version)
    }
    if ($AllowStrongNamedFrameworkUpgrade -and [string]$Identity.publicKeyToken -cne 'null' -and
        ([string]$Identity.name).StartsWith('System.', [System.StringComparison]::Ordinal)) {
        return ([version]$Identity.version) -ge ([version]$Reference.version)
    }
    return $false
}

function Invoke-ManagedAssemblyAudit {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][pscustomobject]$Manifest,
        [Parameter(Mandatory = $true)][object[]]$Modules,
        [Parameter(Mandatory = $true)][pscustomobject]$Coop,
        [string]$GameRoot
    )

    $requests = New-Object System.Collections.Generic.List[object]
    foreach ($module in $Modules + @($Coop)) {
        $moduleId = [string]$module.Descriptor.ModuleId
        foreach ($included in @($module.IncludedFiles | Where-Object { $_.RelativePath -like '*.dll' })) {
            $requests.Add([ordered]@{
                moduleId = $moduleId; relativePath = $included.RelativePath; path = $included.FullName
                included = $true; platform = Get-AssemblyPlatform -RelativePath $included.RelativePath
            })
        }
        foreach ($excluded in @($module.ExcludedFiles | Where-Object { $_.RelativePath -like '*.dll' })) {
            $source = Join-Path $module.ModuleRoot $excluded.RelativePath.Replace('/', '\')
            $requests.Add([ordered]@{
                moduleId = $moduleId; relativePath = $excluded.RelativePath; path = $source
                included = $false; platform = Get-AssemblyPlatform -RelativePath $excluded.RelativePath
            })
        }
    }
    $gameProviderRules = @($Manifest.coopModule.exclusions | Where-Object {
        $closureProperty = $_.PSObject.Properties['assemblyClosure']
        if ($null -eq $closureProperty -or $null -eq $closureProperty.Value) { return $false }
        $providerProperty = $closureProperty.Value.PSObject.Properties['providerModuleId']
        $providersProperty = $closureProperty.Value.PSObject.Properties['providerModuleIds']
        ($null -ne $providerProperty -and [string]$providerProperty.Value -ceq '__InstalledGame') -or
        ($null -ne $providersProperty -and '__InstalledGame' -in @($providersProperty.Value))
    })
    if ($gameProviderRules.Count -gt 0) {
        $providerRoots = @(
            (Join-Path $GameRoot 'bin\Win64_Shipping_Client'),
            (Join-Path $GameRoot 'Modules\Native\bin\Win64_Shipping_Client'),
            (Join-Path $GameRoot 'Modules\SandBox\bin\Win64_Shipping_Client')
        )
        $seenProviderPaths = @{}
        foreach ($providerRoot in $providerRoots) {
            if (-not (Test-Path -LiteralPath $providerRoot -PathType Container)) { continue }
            foreach ($providerFile in @(Get-ChildItem -LiteralPath $providerRoot -Filter '*.dll' -File)) {
                if ($seenProviderPaths.ContainsKey($providerFile.FullName)) { continue }
                $seenProviderPaths[$providerFile.FullName] = $true
                $requests.Add([ordered]@{
                    moduleId = '__InstalledGame'; relativePath = $providerFile.Name; path = $providerFile.FullName
                    included = $false; platform = 'Win64_Shipping_Client'
                })
            }
        }
    }
    $inventory = Invoke-AssemblyInspector -Files $requests.ToArray()
    $includedManaged = @($inventory | Where-Object { $_.included -and $_.managed })
    $sameIdentityProofs = New-Object System.Collections.Generic.List[object]
    foreach ($group in @($includedManaged | Group-Object { ([string]$_.platform).ToLowerInvariant() + '|' + ([string]$_.identity.fullName).ToLowerInvariant() })) {
        $moduleIds = @($group.Group.moduleId | Sort-Object -Unique)
        if ($moduleIds.Count -lt 2) { continue }
        $hashes = @($group.Group.sha256 | Sort-Object -Unique)
        if ($hashes.Count -gt 1) {
            throw "Same managed assembly identity has different bytes in active modules ($($group.Name)): $(@($group.Group | ForEach-Object { "$($_.moduleId)/$($_.relativePath)=$($_.sha256)" }) -join '; ')"
        }
        throw "Duplicate managed assembly identity remains active across modules ($($group.Name)): $($moduleIds -join ', '). Remove the redundant provider explicitly."
    }

    $sideBySideProofs = New-Object System.Collections.Generic.List[object]
    $sideBySideErrors = New-Object System.Collections.Generic.List[string]
    foreach ($group in @($includedManaged | Group-Object { ([string]$_.platform).ToLowerInvariant() + '|' + ([string]$_.identity.name).ToLowerInvariant() })) {
        $moduleIds = @($group.Group.moduleId | Sort-Object -Unique)
        $identities = @($group.Group.identity.fullName | Sort-Object -Unique)
        if ($moduleIds.Count -lt 2 -or $identities.Count -lt 2) { continue }
        $allowance = @($Manifest.sideBySideAssemblyAllowances | Where-Object {
            $candidate = $_
            [string]$candidate.assemblyName -ieq [string]$group.Group[0].identity.name -and
            @($candidate.moduleIds | Where-Object { [string]$_ -notin $moduleIds }).Count -eq 0 -and
            @($moduleIds | Where-Object { [string]$_ -notin @($candidate.moduleIds) }).Count -eq 0
        })
        if ($allowance.Count -ne 1 -or [string]::IsNullOrWhiteSpace([string]$allowance[0].reason)) {
            $sideBySideErrors.Add("$($group.Name): modules $($moduleIds -join ', '), identities $($identities -join ' || ')")
            continue
        }
        if (@($group.Group | Where-Object { [string]$_.identity.publicKeyToken -eq 'null' }).Count -gt 0) {
            $sideBySideErrors.Add("$($group.Name): configured side-by-side versions are not strong-named")
            continue
        }
        $activeProviderProperty = $allowance[0].PSObject.Properties['activeProviderModuleId']
        $stagedInactiveProperty = $allowance[0].PSObject.Properties['stagedInactiveModuleIds']
        $coactiveProperty = $allowance[0].PSObject.Properties['coactiveModuleIds']
        $coactivationProperty = $allowance[0].PSObject.Properties['coactivationPolicy']
        $sideBySideProofs.Add([ordered]@{
            platform = [string]$group.Group[0].platform; assemblyName = [string]$group.Group[0].identity.name
            moduleIds = $moduleIds; identities = $identities; reason = [string]$allowance[0].reason
            activeProviderModuleId = if ($null -ne $activeProviderProperty) { [string]$activeProviderProperty.Value } else { $null }
            stagedInactiveModuleIds = if ($null -ne $stagedInactiveProperty) { @($stagedInactiveProperty.Value) } else { @() }
            coactiveModuleIds = if ($null -ne $coactiveProperty) { @($coactiveProperty.Value) } else { @() }
            coactivationPolicy = if ($null -ne $coactivationProperty) { [string]$coactivationProperty.Value } else { $null }
        })
    }
    if ($sideBySideErrors.Count -gt 0) {
        throw "Side-by-side assembly policy failures: $($sideBySideErrors.ToArray() -join '; ')"
    }

    $closureProofs = New-Object System.Collections.Generic.List[object]
    foreach ($excluded in @($inventory | Where-Object { $_.moduleId -eq $Coop.Descriptor.ModuleId -and -not $_.included -and $_.managed })) {
        $rule = @($Manifest.coopModule.exclusions | Where-Object { Test-RelativeGlob -RelativePath ([string]$excluded.relativePath) -Glob ([string]$_.glob) })[0]
        if ($null -eq $rule) { continue }
        $closureProperty = $rule.PSObject.Properties['assemblyClosure']
        if ($null -eq $closureProperty -or $null -eq $closureProperty.Value) { continue }
        $assemblyClosure = $closureProperty.Value
        $incoming = New-Object System.Collections.Generic.List[object]
        foreach ($consumer in @($includedManaged | Where-Object { $_.platform -eq $excluded.platform })) {
            foreach ($reference in @($consumer.references | Where-Object { [string]$_.name -ceq [string]$excluded.identity.name })) {
                $incoming.Add([pscustomobject]@{ Consumer = $consumer; Reference = $reference })
            }
        }
        $orphanProperty = $assemblyClosure.PSObject.Properties['orphan']
        $isOrphan = $null -ne $orphanProperty -and [bool]$orphanProperty.Value
        if ($isOrphan) {
            if ($incoming.Count -gt 0) {
                throw "Coop assembly '$($excluded.relativePath)' is marked orphan but is referenced by $(@($incoming | ForEach-Object { "$($_.Consumer.moduleId)/$($_.Consumer.relativePath) -> $($_.Reference.fullName)" }) -join '; ')."
            }
            $closureProofs.Add([ordered]@{
                path = [string]$excluded.relativePath; identity = [string]$excluded.identity.fullName
                disposition = 'excluded-orphan'; incomingReferenceCount = 0
            })
            continue
        }

        $providerIdsProperty = $assemblyClosure.PSObject.Properties['providerModuleIds']
        $providerIdProperty = $assemblyClosure.PSObject.Properties['providerModuleId']
        $providerIds = if ($null -ne $providerIdsProperty) {
            @($providerIdsProperty.Value | ForEach-Object { [string]$_ })
        }
        elseif ($null -ne $providerIdProperty) {
            @([string]$providerIdProperty.Value)
        }
        else { @() }
        $upgradeProperty = $assemblyClosure.PSObject.Properties['allowUnsignedProviderUpgrade']
        $allowUnsignedUpgrade = $null -ne $upgradeProperty -and [bool]$upgradeProperty.Value
        $frameworkUpgradeProperty = $assemblyClosure.PSObject.Properties['allowStrongNamedFrameworkUpgrade']
        $allowStrongNamedFrameworkUpgrade = $null -ne $frameworkUpgradeProperty -and [bool]$frameworkUpgradeProperty.Value
        $providerInventory = @($inventory | Where-Object {
            $_.managed -and [string]$_.moduleId -in $providerIds -and
            ($_.included -or [string]$_.moduleId -ceq '__InstalledGame')
        })
        $providers = @($providerInventory | Where-Object {
            [string]$_.moduleId -in $providerIds -and [string]$_.platform -ceq [string]$excluded.platform -and
            [string]$_.identity.name -ceq [string]$excluded.identity.name
        })
        if ($providers.Count -eq 0) {
            throw "No configured provider ($($providerIds -join ', ')) remains for excluded Coop assembly '$($excluded.identity.fullName)' on $($excluded.platform)."
        }
        foreach ($use in $incoming) {
            $satisfied = @($providers | Where-Object {
                Test-AssemblyIdentitySatisfiesReference -Identity $_.identity -Reference $use.Reference -AllowUnsignedProviderUpgrade:$allowUnsignedUpgrade -AllowStrongNamedFrameworkUpgrade:$allowStrongNamedFrameworkUpgrade
            })
            if ($satisfied.Count -eq 0) {
                throw "Provider closure failed: $($use.Consumer.moduleId)/$($use.Consumer.relativePath) requires '$($use.Reference.fullName)', but configured providers ($($providerIds -join ', ')) do not supply a compatible identity."
            }
        }
        $closureProofs.Add([ordered]@{
            path = [string]$excluded.relativePath; identity = [string]$excluded.identity.fullName
            disposition = 'excluded-provider-satisfied'; providerModuleIds = $providerIds
            providerIdentities = @($providers.identity.fullName | Sort-Object -Unique)
            incomingReferenceCount = $incoming.Count
            providerHashes = @($providers.sha256 | Sort-Object -Unique)
        })
    }

    $requiredCoopAssemblyPins = New-Object System.Collections.Generic.List[object]
    $requiredPinsProperty = $Manifest.coopModule.PSObject.Properties['requiredAssemblyPins']
    $requiredPins = if ($null -ne $requiredPinsProperty) { @($requiredPinsProperty.Value) } else { @() }
    foreach ($pin in $requiredPins) {
        $plannedFile = @($Coop.IncludedFiles | Where-Object { [string]$_.RelativePath -ceq [string]$pin.path })
        $providerMetadata = @($inventory | Where-Object {
            $_.included -and $_.managed -and [string]$_.moduleId -ceq [string]$Coop.Descriptor.ModuleId -and
            [string]$_.relativePath -ceq [string]$pin.path
        })
        if ($plannedFile.Count -ne 1 -or $providerMetadata.Count -ne 1 -or
            $plannedFile[0].Length -ne [long]$pin.size -or
            [string]$plannedFile[0].Sha256 -cne ([string]$pin.sha256).ToLowerInvariant() -or
            [string]$providerMetadata[0].identity.fullName -cne [string]$pin.assemblyFullName) {
            throw "Required Coop assembly pin failed path/size/hash/AssemblyName verification: $($pin.path)"
        }
        $consumerProofs = New-Object System.Collections.Generic.List[object]
        foreach ($expectedConsumer in @($pin.exactConsumerReferences)) {
            $consumer = @($inventory | Where-Object {
                $_.included -and $_.managed -and [string]$_.moduleId -ceq [string]$Coop.Descriptor.ModuleId -and
                [string]$_.relativePath -ceq [string]$expectedConsumer.path
            })
            if ($consumer.Count -ne 1) {
                throw "Required Coop assembly pin consumer is missing or ambiguous: $($expectedConsumer.path)"
            }
            $matchingReference = @($consumer[0].references | Where-Object {
                [string]$_.fullName -ceq [string]$expectedConsumer.referenceFullName
            })
            if ($matchingReference.Count -ne 1) {
                throw "Required Coop assembly pin lacks exact consumer AssemblyRef '$($expectedConsumer.referenceFullName)' from '$($expectedConsumer.path)'."
            }
            $consumerProofs.Add([ordered]@{
                path = [string]$expectedConsumer.path
                consumerAssemblyFullName = [string]$consumer[0].identity.fullName
                referenceFullName = [string]$matchingReference[0].fullName
            })
        }
        $stagedConflictProperty = $pin.PSObject.Properties['stagedInactiveConflictModuleIds']
        $coactiveAlternateProperty = $pin.PSObject.Properties['coactiveAlternateProviderModuleIds']
        $conflictIds = @()
        if ($null -ne $stagedConflictProperty) {
            $conflictIds = @($stagedConflictProperty.Value | ForEach-Object { [string]$_ })
        }
        $coactiveIds = @()
        if ($null -ne $coactiveAlternateProperty) {
            $coactiveIds = @($coactiveAlternateProperty.Value | ForEach-Object { [string]$_ })
        }
        if ($conflictIds.Count -gt 0 -and $coactiveIds.Count -gt 0) {
            throw "Required Coop assembly pin cannot define both staged-inactive conflicts and verified coactive alternate providers: $($pin.path)"
        }
        $configuredInactive = @($Manifest.activationPolicy.client.stagedInactiveModuleIds | ForEach-Object { [string]$_ })
        $configuredActive = @($Manifest.activationPolicy.client.activeModuleOrder | ForEach-Object { [string]$_ })
        if (@($conflictIds | Where-Object { [string]$_ -notin $configuredInactive }).Count -gt 0 -or
            @($conflictIds | Where-Object { [string]$_ -in $configuredActive }).Count -gt 0) {
            throw "Required Coop assembly pin has a conflict provider that is not staged-inactive: $($conflictIds -join ', ')."
        }
        if (@($coactiveIds | Where-Object { [string]$_ -notin $configuredActive -or [string]$_ -in $configuredInactive }).Count -gt 0) {
            throw "Required Coop assembly pin has an alternate provider that is not active: $($coactiveIds -join ', ')."
        }
        $alternateIds = @($conflictIds) + @($coactiveIds)
        $alternateProviders = @($includedManaged | Where-Object {
            [string]$_.moduleId -in $alternateIds -and
            [string]$_.identity.name -ceq [string]$providerMetadata[0].identity.name
        })
        foreach ($alternateId in $alternateIds) {
            if (@($alternateProviders | Where-Object { [string]$_.moduleId -ceq $alternateId }).Count -eq 0) {
                throw "Configured alternate provider module '$alternateId' has no audited '$($providerMetadata[0].identity.name)' payload."
            }
        }
        $expectedCoactivationPolicy = if ($coactiveIds.Count -gt 0) { 'verified-framework-load-context-e2e' } else { 'forbidden-until-framework-load-context-e2e' }
        $allowance = @($Manifest.sideBySideAssemblyAllowances | Where-Object {
            [string]$_.assemblyName -ceq [string]$providerMetadata[0].identity.name
        })
        if ($allowance.Count -ne 1 -or [string]$allowance[0].activeProviderModuleId -cne [string]$Coop.Descriptor.ModuleId -or
            [string]$allowance[0].coactivationPolicy -cne $expectedCoactivationPolicy) {
            throw "Required Coop assembly '$($providerMetadata[0].identity.name)' lacks an explicit active-provider/coactivation policy."
        }
        $installedProviders = @($inventory | Where-Object {
            $_.managed -and [string]$_.moduleId -ceq '__InstalledGame' -and
            [string]$_.identity.name -ceq [string]$providerMetadata[0].identity.name
        })
        $requiredCoopAssemblyPins.Add([ordered]@{
            path = [string]$pin.path
            size = [long]$pin.size
            sha256 = ([string]$pin.sha256).ToLowerInvariant()
            assemblyFullName = [string]$pin.assemblyFullName
            exactConsumerReferences = $consumerProofs.ToArray()
            installedProviderIdentities = @($installedProviders.identity.fullName | Sort-Object -Unique)
            stagedInactiveConflictProviders = @($alternateProviders | Where-Object { [string]$_.moduleId -in $conflictIds } | ForEach-Object {
                [ordered]@{ moduleId = [string]$_.moduleId; assemblyFullName = [string]$_.identity.fullName; sha256 = [string]$_.sha256 }
            })
            coactiveAlternateProviders = @($alternateProviders | Where-Object { [string]$_.moduleId -in $coactiveIds } | ForEach-Object {
                [ordered]@{ moduleId = [string]$_.moduleId; assemblyFullName = [string]$_.identity.fullName; sha256 = [string]$_.sha256 }
            })
            activeProviderModuleId = [string]$Coop.Descriptor.ModuleId
            coactivationPolicy = $expectedCoactivationPolicy
            reason = [string]$pin.reason
            buildTimeVerified = $true
        })
    }

    $serverHarmonyPins = New-Object System.Collections.Generic.List[object]
    $harmonyPolicy = $Manifest.activationPolicy.server.harmonyPreflight
    $harmonyModule = @($Modules | Where-Object { [string]$_.Descriptor.ModuleId -ceq [string]$harmonyPolicy.moduleId })
    if ($harmonyModule.Count -ne 1 -or [string]$harmonyModule[0].Descriptor.Version -cne [string]$harmonyPolicy.moduleVersion) {
        throw "Server Harmony preflight module/version is not present exactly once: $($harmonyPolicy.moduleId) $($harmonyPolicy.moduleVersion)."
    }
    foreach ($pin in @($harmonyPolicy.payloads)) {
        $plannedFile = @($harmonyModule[0].IncludedFiles | Where-Object { [string]$_.RelativePath -ceq [string]$pin.path })
        $metadata = @($inventory | Where-Object {
            $_.included -and [string]$_.moduleId -ceq [string]$harmonyPolicy.moduleId -and
            [string]$_.relativePath -ceq [string]$pin.path
        })
        if ($plannedFile.Count -ne 1 -or $metadata.Count -ne 1 -or -not [bool]$metadata[0].managed -or
            $plannedFile[0].Length -ne [long]$pin.size -or
            [string]$plannedFile[0].Sha256 -cne ([string]$pin.sha256).ToLowerInvariant() -or
            [string]$metadata[0].identity.fullName -cne [string]$pin.assemblyFullName) {
            throw "Pinned server Harmony payload failed build-time path/size/hash/AssemblyName verification: $($pin.path)"
        }
        $serverHarmonyPins.Add([ordered]@{
            path = [string]$pin.path
            size = [long]$pin.size
            sha256 = ([string]$pin.sha256).ToLowerInvariant()
            assemblyFullName = [string]$pin.assemblyFullName
            buildTimeVerified = $true
        })
    }

    return [pscustomobject]@{
        InspectedDllCount = $inventory.Count
        ManagedDllCount = $includedManaged.Count
        ClosureProofs = $closureProofs.ToArray()
        SideBySideAllowances = $sideBySideProofs.ToArray()
        SameIdentityDuplicates = $sameIdentityProofs.ToArray()
        RequiredCoopAssemblyPins = $requiredCoopAssemblyPins.ToArray()
        ServerHarmonyPins = $serverHarmonyPins.ToArray()
    }
}

function Test-RelativeGlob {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$RelativePath,
        [Parameter(Mandatory = $true)][string]$Glob
    )

    $path = $RelativePath.Replace('\', '/')
    $pattern = $Glob.Replace('\', '/')
    return $path -like $pattern
}

function New-WorkshopSuitePlan {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][pscustomobject]$Manifest,
        [Parameter(Mandatory = $true)][pscustomobject]$Workshop,
        [Parameter(Mandatory = $true)][string]$CoopModuleRoot
    )

    $state = Read-WorkshopState -Workshop $Workshop
    if ($state.NeedsDownload -ne '0') {
        # A download in flight means source files may be half-written; no hash check can make
        # reading them safe. This stays fatal.
        throw "Steam reports a Workshop download in progress (NeedsUpdate=$($state.NeedsUpdate), NeedsDownload=$($state.NeedsDownload)). Let Steam finish first."
    }
    if ($state.NeedsUpdate -ne '0') {
        # NeedsUpdate with no download queued means Steam merely KNOWS a newer manifest exists
        # (it fetches at next game launch); the on-disk content is stable. The real integrity
        # gates are the per-file SHA-256 pins validated before staging and the full source
        # re-hash after staging — if Steam does start rewriting content mid-build, those abort.
        # Blocking here would make the suite unbuildable for as long as any upstream author
        # keeps publishing updates we have not yet audited and re-pinned.
        Write-Warning ("Steam reports a newer manifest for at least one Workshop item " +
            "(NeedsUpdate=$($state.NeedsUpdate), NeedsDownload=0). Building against the installed, " +
            "pin-verified content; re-audit and re-pin before adopting the update.")
    }

    $expectedIds = @($Manifest.modules | ForEach-Object { [string]$_.workshopId } | Sort-Object)
    $installedIds = @($state.Items.Keys | ForEach-Object { [string]$_ } | Sort-Object)
    $missingIds = @($expectedIds | Where-Object { $_ -notin $installedIds })
    if ($missingIds.Count -gt 0) {
        throw "Configured Workshop subscriptions are not installed: $($missingIds -join ', ')."
    }
    if ([bool]$Manifest.suite.requireExactSubscriptionSet) {
        $unexpectedIds = @($installedIds | Where-Object { $_ -notin $expectedIds })
        if ($unexpectedIds.Count -gt 0) {
            throw "Installed Workshop subscriptions are missing from the suite manifest: $($unexpectedIds -join ', ')."
        }
    }

    $plannedModules = New-Object System.Collections.Generic.List[object]
    foreach ($configured in @($Manifest.modules | Sort-Object { [int]$_.loadOrder }, { [string]$_.moduleId })) {
        $workshopId = [string]$configured.workshopId
        $stateItem = $state.Items[$workshopId]
        if ($stateItem.ManifestId -ne $stateItem.LatestManifest -or
            $stateItem.TimeUpdated -ne $stateItem.LatestTimeUpdated) {
            # The upstream author published something newer than what is installed. The suite
            # deliberately ships the AUDITED, pinned build — the fatal check below proves the
            # installed manifest IS that pin, and the per-file SHA-256 pins prove the bytes.
            # Newer-upstream-exists is a re-audit reminder, not a packaging error.
            Write-Warning ("Workshop item $workshopId has a newer upstream manifest " +
                "($($stateItem.LatestManifest)) than the installed one ($($stateItem.ManifestId)). " +
                "Building the installed, audited pin; re-audit and re-pin to adopt the update.")
        }
        if ($stateItem.ManifestId -ne [string]$configured.steamManifestId) {
            throw "Workshop item $workshopId manifest is '$($stateItem.ManifestId)', expected '$($configured.steamManifestId)'. Review and repin deploy/workshop-mods.json before distributing an update."
        }

        $moduleRoot = Join-Path $Workshop.ContentRoot $workshopId
        if (-not (Test-Path -LiteralPath $moduleRoot -PathType Container)) {
            throw "Active Workshop item $workshopId has no content directory: $moduleRoot"
        }
        $descriptor = Get-SubModuleDescriptor -ModuleRoot $moduleRoot
        foreach ($comparison in @(
            @('moduleId', $descriptor.ModuleId, [string]$configured.moduleId),
            @('name', $descriptor.Name, [string]$configured.name),
            @('version', $descriptor.Version, [string]$configured.version)
        )) {
            if ($comparison[1] -cne $comparison[2]) {
                throw "Workshop item $workshopId $($comparison[0]) is '$($comparison[1])', expected '$($comparison[2])'."
            }
        }

        $snapshot = Get-DirectorySnapshot -Root $moduleRoot
        if ($snapshot.Files.Count -eq 0) {
            throw "Active Workshop item $workshopId is empty."
        }

        $included = New-Object System.Collections.Generic.List[object]
        $excluded = New-Object System.Collections.Generic.List[object]
        $exclusionCounts = @{}
        foreach ($rule in @($configured.exclusions)) {
            $exclusionCounts[[string]$rule.glob] = 0
        }

        foreach ($file in $snapshot.Files) {
            $matchedRule = $null
            foreach ($rule in @($configured.exclusions)) {
                if (Test-RelativeGlob -RelativePath $file.RelativePath -Glob ([string]$rule.glob)) {
                    $matchedRule = $rule
                    break
                }
            }
            if ($null -ne $matchedRule) {
                $glob = [string]$matchedRule.glob
                $exclusionCounts[$glob] = [int]$exclusionCounts[$glob] + 1
                $excluded.Add([pscustomobject]@{
                    RelativePath = $file.RelativePath
                    Length       = $file.Length
                    Sha256       = $file.Sha256
                    Rule         = $glob
                    Reason       = [string]$matchedRule.reason
                })
            }
            else {
                $included.Add($file)
            }
        }

        foreach ($rule in @($configured.exclusions)) {
            $minimumMatches = 0
            if ($null -ne $rule.minimumMatches) {
                $minimumMatches = [int]$rule.minimumMatches
            }
            if ([int]$exclusionCounts[[string]$rule.glob] -lt $minimumMatches) {
                throw "Exclusion '$($rule.glob)' for $($configured.moduleId) matched $($exclusionCounts[[string]$rule.glob]) files; expected at least $minimumMatches."
            }
        }

        foreach ($declaredDll in $descriptor.DeclaredDllNames) {
            $matchingIncluded = @($included | Where-Object { [System.IO.Path]::GetFileName($_.RelativePath) -ceq $declaredDll })
            if ($matchingIncluded.Count -eq 0) {
                throw "SubModule.xml for $($configured.moduleId) declares '$declaredDll', but the staged plan would omit it."
            }
        }

        $plannedModules.Add([pscustomobject]@{
            Config         = $configured
            WorkshopId     = $workshopId
            SteamManifestId = $stateItem.ManifestId
            ModuleRoot     = $moduleRoot
            Descriptor     = $descriptor
            SourceSnapshot = $snapshot
            IncludedFiles  = @($included | Sort-Object RelativePath)
            ExcludedFiles  = @($excluded | Sort-Object RelativePath)
        })
    }

    foreach ($module in $plannedModules) {
        foreach ($file in @($module.IncludedFiles | Where-Object { $_.RelativePath -like '*.dll' })) {
            $leaf = [System.IO.Path]::GetFileName($file.RelativePath)
            foreach ($policy in @($Manifest.assemblyPolicies)) {
                if ($leaf -like [string]$policy.fileNamePattern) {
                    $allowed = @($policy.allowedModuleIds | ForEach-Object { [string]$_ })
                    if ([string]$module.Descriptor.ModuleId -notin $allowed) {
                        throw "Runtime-dangerous assembly '$leaf' remains in $($module.Descriptor.ModuleId); allowed providers: $($allowed -join ', ')."
                    }
                }
            }
        }
    }

    $crossModuleAssemblies = @{}
    foreach ($module in $plannedModules) {
        foreach ($file in @($module.IncludedFiles | Where-Object { $_.RelativePath -like '*.dll' })) {
            $segments = $file.RelativePath.Split('/')
            $platform = if ($segments.Count -ge 3 -and $segments[0] -ieq 'bin') { $segments[1] } else { 'unscoped' }
            $key = ($platform + '/' + [System.IO.Path]::GetFileName($file.RelativePath)).ToLowerInvariant()
            if (-not $crossModuleAssemblies.ContainsKey($key)) {
                $crossModuleAssemblies[$key] = New-Object System.Collections.Generic.List[string]
            }
            if ([string]$module.Descriptor.ModuleId -notin $crossModuleAssemblies[$key]) {
                $crossModuleAssemblies[$key].Add([string]$module.Descriptor.ModuleId)
            }
        }
    }
    $duplicates = @($crossModuleAssemblies.GetEnumerator() | Where-Object { $_.Value.Count -gt 1 })
    if ($duplicates.Count -gt 0) {
        $descriptions = @($duplicates | ForEach-Object { "$($_.Key): $($_.Value -join ', ')" })
        throw "Cross-module duplicate runtime assemblies remain after exclusions: $($descriptions -join '; ')"
    }

    $coop = New-CoopModulePlan -Manifest $Manifest -CoopModuleRoot $CoopModuleRoot
    foreach ($file in @($coop.IncludedFiles | Where-Object { $_.RelativePath -like '*.dll' })) {
        $leaf = [System.IO.Path]::GetFileName($file.RelativePath)
        foreach ($policy in @($Manifest.assemblyPolicies)) {
            if ($leaf -like [string]$policy.fileNamePattern) {
                $allowed = @($policy.allowedModuleIds | ForEach-Object { [string]$_ })
                if ([string]$coop.Descriptor.ModuleId -notin $allowed) {
                    throw "Runtime-dangerous assembly '$leaf' remains in composed Coop; allowed staged providers: $($allowed -join ', ')."
                }
            }
        }
    }
    $coopHarmony = @($coop.IncludedFiles | Where-Object { [System.IO.Path]::GetFileName($_.RelativePath) -ieq '0Harmony.dll' })
    if ($coopHarmony.Count -gt 0) {
        throw 'Coop composition still contains 0Harmony.dll while Bannerlord.Harmony is active. Exactly one Harmony provider is required.'
    }

    $suiteIds = @($plannedModules | ForEach-Object { [string]$_.Descriptor.ModuleId })
    $providedIds = @($Manifest.suite.baseModuleIds | ForEach-Object { [string]$_ }) + $suiteIds + @([string]$coop.Descriptor.ModuleId)
    $loadOrders = @{}
    foreach ($module in $plannedModules) { $loadOrders[[string]$module.Descriptor.ModuleId] = [int]$module.Config.loadOrder }
    foreach ($module in $plannedModules.ToArray() + @($coop)) {
        foreach ($dependency in @($module.Descriptor.Dependencies)) {
            if ($dependency.Optional) { continue }
            if ([string]$dependency.ModuleId -notin $providedIds) {
                throw "Required dependency '$($dependency.ModuleId)' for '$($module.Descriptor.ModuleId)' is absent from the managed suite/base-module closure."
            }
            if ($loadOrders.ContainsKey([string]$dependency.ModuleId) -and $loadOrders.ContainsKey([string]$module.Descriptor.ModuleId) -and
                [int]$loadOrders[[string]$dependency.ModuleId] -ge [int]$loadOrders[[string]$module.Descriptor.ModuleId]) {
                throw "Load order is invalid: '$($dependency.ModuleId)' must load before '$($module.Descriptor.ModuleId)'."
            }
        }
    }

    $clientOrder = @($Manifest.activationPolicy.client.exactModuleOrder | ForEach-Object { [string]$_ })
    if (@($clientOrder | Sort-Object -Unique).Count -ne $clientOrder.Count) {
        throw 'Client exactModuleOrder contains duplicate module IDs.'
    }
    foreach ($requiredId in $providedIds) {
        if ([string]$requiredId -notin $clientOrder) {
            throw "Client exactModuleOrder is missing required managed/base module '$requiredId'."
        }
    }
    $clientIndexes = @{}
    for ($index = 0; $index -lt $clientOrder.Count; $index++) { $clientIndexes[$clientOrder[$index]] = $index }
    foreach ($module in $plannedModules.ToArray() + @($coop)) {
        foreach ($dependency in @($module.Descriptor.Dependencies)) {
            if ($clientIndexes.ContainsKey([string]$dependency.ModuleId) -and
                [int]$clientIndexes[[string]$dependency.ModuleId] -ge [int]$clientIndexes[[string]$module.Descriptor.ModuleId]) {
                throw "Client exactModuleOrder violates dependency: '$($dependency.ModuleId)' must precede '$($module.Descriptor.ModuleId)'."
            }
        }
    }
    if (-not (Test-ManagedLoadOrderMatchesActivationCohorts `
        -ClientOrder $clientOrder `
        -CoopModuleId ([string]$coop.Descriptor.ModuleId) `
        -ManagedModules $plannedModules.ToArray())) {
        throw 'Numeric managed loadOrder values do not match client exactModuleOrder within the before-Coop and after-Coop cohorts.'
    }

    $activeClientOrder = @($Manifest.activationPolicy.client.activeModuleOrder | ForEach-Object { [string]$_ })
    $stagedInactiveIds = @($Manifest.activationPolicy.client.stagedInactiveModuleIds | ForEach-Object { [string]$_ })
    if ([bool]$Manifest.activationPolicy.client.activateAllManagedModules) {
        throw 'Private-suite safety policy must not hardwire activateAllManagedModules=true before framework/adapter E2E gates close.'
    }
    if (@($activeClientOrder | Sort-Object -Unique).Count -ne $activeClientOrder.Count -or
        @($stagedInactiveIds | Sort-Object -Unique).Count -ne $stagedInactiveIds.Count) {
        throw 'Client activeModuleOrder/stagedInactiveModuleIds contains duplicate module IDs.'
    }
    if (@($activeClientOrder | Where-Object { [string]$_ -notin $providedIds }).Count -gt 0) {
        throw "Client activeModuleOrder contains an unknown module: $(@($activeClientOrder | Where-Object { [string]$_ -notin $providedIds }) -join ', ')."
    }
    if (@($stagedInactiveIds | Where-Object { [string]$_ -notin $suiteIds }).Count -gt 0) {
        throw "Client stagedInactiveModuleIds contains a non-managed module: $(@($stagedInactiveIds | Where-Object { [string]$_ -notin $suiteIds }) -join ', ')."
    }
    if (@($activeClientOrder | Where-Object { [string]$_ -in $stagedInactiveIds }).Count -gt 0) {
        throw 'A client module cannot be both active and staged-inactive.'
    }
    $managedActiveIds = @($activeClientOrder | Where-Object { [string]$_ -in $suiteIds })
    if (@($suiteIds | Where-Object { [string]$_ -notin ($managedActiveIds + $stagedInactiveIds) }).Count -gt 0) {
        throw "Every managed module must be explicitly active or staged-inactive: $(@($suiteIds | Where-Object { [string]$_ -notin ($managedActiveIds + $stagedInactiveIds) }) -join ', ')."
    }
    $activeProjection = @($clientOrder | Where-Object { [string]$_ -in $activeClientOrder })
    if (($activeProjection -join '|') -cne ($activeClientOrder -join '|')) {
        throw 'Client activeModuleOrder must preserve the dependency-safe exactModuleOrder sequence.'
    }

    $serverPolicy = $Manifest.activationPolicy.server
    $exactServerOrder = @($serverPolicy.exactActiveModuleOrder | ForEach-Object { [string]$_ })
    $blockedServerIds = @(
        @($serverPolicy.neverActivateModuleIds) + @($serverPolicy.guardedModuleIds) |
            ForEach-Object { [string]$_ } |
            Sort-Object -Unique
    )
    if ($exactServerOrder.Count -eq 0 -or @($exactServerOrder | Sort-Object -Unique).Count -ne $exactServerOrder.Count) {
        throw 'Server exactActiveModuleOrder is empty or contains duplicate module IDs.'
    }
    if (@($exactServerOrder | Where-Object { [string]$_ -notin $providedIds }).Count -gt 0) {
        throw "Server exactActiveModuleOrder contains an unknown module: $(@($exactServerOrder | Where-Object { [string]$_ -notin $providedIds }) -join ', ')."
    }
    if (@($exactServerOrder | Where-Object { [string]$_ -in $blockedServerIds }).Count -gt 0) {
        throw "Server exactActiveModuleOrder activates a guarded/blocked module: $(@($exactServerOrder | Where-Object { [string]$_ -in $blockedServerIds }) -join ', ')."
    }
    $requiredServerIds = @($serverPolicy.harmonyPreflight.requiredModuleIds | ForEach-Object { [string]$_ })
    if (($requiredServerIds -join '|') -cne ($exactServerOrder -join '|')) {
        throw 'Server harmonyPreflight.requiredModuleIds must exactly match exactActiveModuleOrder for this pinned private release.'
    }
    foreach ($successor in @($serverPolicy.harmonyPreflight.harmonyMustPrecede)) {
        if ([array]::IndexOf($exactServerOrder, [string]$serverPolicy.harmonyPreflight.moduleId) -ge
            [array]::IndexOf($exactServerOrder, [string]$successor)) {
            throw "Server exactActiveModuleOrder must place '$($serverPolicy.harmonyPreflight.moduleId)' before '$successor'."
        }
    }

    $requiresInstalledGameProvider = @($Manifest.coopModule.exclusions | Where-Object {
        $closureProperty = $_.PSObject.Properties['assemblyClosure']
        if ($null -eq $closureProperty -or $null -eq $closureProperty.Value) { return $false }
        $providerProperty = $closureProperty.Value.PSObject.Properties['providerModuleId']
        $providersProperty = $closureProperty.Value.PSObject.Properties['providerModuleIds']
        ($null -ne $providerProperty -and [string]$providerProperty.Value -ceq '__InstalledGame') -or
        ($null -ne $providersProperty -and '__InstalledGame' -in @($providersProperty.Value))
    }).Count -gt 0
    $gameRoot = Join-Path (Split-Path -Parent $Workshop.WorkshopRoot) 'common\Mount & Blade II Bannerlord'
    if ($requiresInstalledGameProvider -and -not (Test-Path -LiteralPath $gameRoot -PathType Container)) {
        throw "Bannerlord game root required for canonical API assembly comparison was not found: $gameRoot"
    }
    $assemblyAudit = Invoke-ManagedAssemblyAudit -Manifest $Manifest -Modules $plannedModules.ToArray() -Coop $coop -GameRoot $gameRoot
    $serverPreflightFiles = Get-ServerPreflightFiles
    $clientInstallerFiles = Get-ClientInstallerFiles

    return [pscustomobject]@{
        Manifest = $Manifest
        Workshop = $Workshop
        State    = $state
        Modules  = $plannedModules.ToArray()
        Coop     = $coop
        AssemblyAudit = $assemblyAudit
        ServerPreflightFiles = $serverPreflightFiles
        ClientInstallerFiles = $clientInstallerFiles
    }
}

function Test-ManagedLoadOrderMatchesActivationCohorts {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string[]]$ClientOrder,
        [Parameter(Mandatory = $true)][string]$CoopModuleId,
        [Parameter(Mandatory = $true)][object[]]$ManagedModules
    )

    $coopIndex = [array]::IndexOf($ClientOrder, $CoopModuleId)
    if ($coopIndex -lt 0) { return $false }

    $managedIds = @($ManagedModules | ForEach-Object { [string]$_.Descriptor.ModuleId })
    $orderedBeforeCoop = New-Object System.Collections.Generic.List[string]
    $orderedAfterCoop = New-Object System.Collections.Generic.List[string]
    for ($index = 0; $index -lt $ClientOrder.Count; $index++) {
        $moduleId = [string]$ClientOrder[$index]
        if ($moduleId -notin $managedIds) { continue }
        if ($index -lt $coopIndex) { $orderedBeforeCoop.Add($moduleId) }
        else { $orderedAfterCoop.Add($moduleId) }
    }

    $numericIds = @($ManagedModules |
        Sort-Object { [int]$_.Config.loadOrder } |
        ForEach-Object { [string]$_.Descriptor.ModuleId })
    $numericBeforeCoop = @($numericIds | Where-Object { $_ -in $orderedBeforeCoop })
    $numericAfterCoop = @($numericIds | Where-Object { $_ -in $orderedAfterCoop })
    return (($orderedBeforeCoop.ToArray() -join '|') -ceq ($numericBeforeCoop -join '|')) -and
        (($orderedAfterCoop.ToArray() -join '|') -ceq ($numericAfterCoop -join '|'))
}

function Get-ReceiptDigest {
    <#
        .SYNOPSIS
        Canonical receipt digest, byte-identical to WorkshopSuiteReceipt.ComputeDigest.

        .DESCRIPTION
        The runtime orders records by module id with StringComparer.OrdinalIgnoreCase. Each line
        already begins with the lowercased module id, so sorting the lines ordinally reproduces
        that order. Sort-Object -CaseSensitive must not be used here: it is culture-aware and can
        order differently from the runtime, producing a digest the game rejects.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][System.Collections.Generic.List[string]]$ReceiptLines)

    $ordered = [System.Collections.Generic.List[string]]::new($ReceiptLines)
    $ordered.Sort([System.StringComparer]::Ordinal)
    return Get-StringSha256 -Text ($ordered -join "`n")
}

function Get-ManagedModuleDigests {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][object[]]$Files)

    $ignoredExtensions = @('.log', '.pdb', '.md', '.bak', '.tmp')
    $configurationExtensions = @('.xml', '.json', '.config', '.ini', '.yaml', '.yml', '.csv', '.txt')
    $mutableNames = @('mod-config.json', 'coop-options.json', 'server-config.json')
    $contentLines = New-Object System.Collections.Generic.List[string]
    $configurationLines = New-Object System.Collections.Generic.List[string]
    foreach ($file in $Files) {
        $path = ([string]$file.RelativePath).Replace('\', '/').ToLowerInvariant()
        $name = [System.IO.Path]::GetFileName($path)
        $extension = [System.IO.Path]::GetExtension($name)
        if ($name -in $mutableNames -or $extension -in $ignoredExtensions) { continue }
        $line = "$path|$($file.Length)|$($file.Sha256.ToLowerInvariant())"
        if ($extension -in $configurationExtensions) { $configurationLines.Add($line) } else { $contentLines.Add($line) }
    }
    # Sort ORDINALLY, exactly like the runtime hasher (StringComparer.Ordinal). Sort-Object
    # -CaseSensitive is culture-aware: for most modules its order coincides with ordinal, but
    # PlayerSettlement's file names diverge, which produced a pinned configuration hash the
    # runtime could never reproduce and refused every join as an "unmanaged copy".
    $contentLines.Sort([System.StringComparer]::Ordinal)
    $configurationLines.Sort([System.StringComparer]::Ordinal)
    $content = $contentLines -join "`n"
    $configuration = $configurationLines -join "`n"
    return [pscustomobject]@{
        ContentSha256 = Get-StringSha256 -Text $content
        ConfigurationSha256 = Get-StringSha256 -Text $configuration
    }
}

function New-ManagedSuiteReceipt {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][pscustomobject]$Plan)

    $records = New-Object System.Collections.Generic.List[object]
    $receiptLines = New-Object System.Collections.Generic.List[string]
    foreach ($module in @($Plan.Modules | Sort-Object { $_.Descriptor.ModuleId.ToLowerInvariant() })) {
        $digests = Get-ManagedModuleDigests -Files $module.IncludedFiles
        $record = [ordered]@{
            moduleId = [string]$module.Descriptor.ModuleId
            workshopId = [string]$module.WorkshopId
            steamManifestId = [string]$module.SteamManifestId
            version = [string]$module.Descriptor.Version
            loadOrder = [int]$module.Config.loadOrder
            contentSha256 = $digests.ContentSha256
            configurationSha256 = $digests.ConfigurationSha256
        }
        $records.Add($record)
        $receiptLines.Add("$($record.moduleId.ToLowerInvariant())|$($record.workshopId)|$($record.steamManifestId)|$($record.version)|$($record.loadOrder)|$($record.contentSha256)|$($record.configurationSha256)")
    }
    $receipt = [ordered]@{
        schemaVersion = 1
        suiteId = [string]$Plan.Manifest.suite.id
        moduleCount = $records.Count
        receiptSha256 = Get-ReceiptDigest -ReceiptLines $receiptLines
        modules = $records.ToArray()
    }
    return [pscustomobject]@{ Record = $receipt; Json = ($receipt | ConvertTo-Json -Depth 20) }
}

function New-ManifestDocuments {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][pscustomobject]$Plan,
        [Parameter(Mandatory = $true)][pscustomobject]$ManagedReceipt
    )

    $moduleRecords = New-Object System.Collections.Generic.List[object]
    $checksumLines = New-Object System.Collections.Generic.List[string]
    $creditLines = New-Object System.Collections.Generic.List[string]
    $loadOrderLines = New-Object System.Collections.Generic.List[string]
    $creditLines.Add("# $($Plan.Manifest.suite.displayName) credits")
    $creditLines.Add('')
    $creditLines.Add('The original modules remain separate runtime modules. Credit and Workshop links must stay with every private copy.')
    $creditLines.Add('')

    $serverPolicy = $Plan.Manifest.activationPolicy.server
    $harmonyPolicy = $serverPolicy.harmonyPreflight
    if ($null -eq $harmonyPolicy) {
        throw 'Server activation policy is missing harmonyPreflight.'
    }
    $preflightFiles = New-Object System.Collections.Generic.List[object]
    foreach ($file in @($Plan.ServerPreflightFiles | Sort-Object RelativePath)) {
        $preflightFiles.Add([ordered]@{ path = $file.RelativePath; size = $file.Length; sha256 = $file.Sha256 })
        $checksumLines.Add("$($file.Sha256) *$($file.RelativePath)")
    }
    $clientInstallerFiles = New-Object System.Collections.Generic.List[object]
    foreach ($file in @($Plan.ClientInstallerFiles | Sort-Object RelativePath)) {
        $clientInstallerFiles.Add([ordered]@{ path = $file.RelativePath; size = $file.Length; sha256 = $file.Sha256 })
        $checksumLines.Add("$($file.Sha256) *$($file.RelativePath)")
    }
    $serverExpectation = [ordered]@{
        schemaVersion = 1
        selectedNightly = [string]$harmonyPolicy.selectedNightly
        platform = [string]$harmonyPolicy.platform
        moduleId = [string]$harmonyPolicy.moduleId
        moduleVersion = [string]$harmonyPolicy.moduleVersion
        activation = [ordered]@{
            exactActiveModuleOrder = @($serverPolicy.exactActiveModuleOrder | ForEach-Object { [string]$_ })
            requiredModuleIds = @($harmonyPolicy.requiredModuleIds | ForEach-Object { [string]$_ })
            harmonyMustPrecede = @($harmonyPolicy.harmonyMustPrecede | ForEach-Object { [string]$_ })
            neverActivateModuleIds = @($serverPolicy.neverActivateModuleIds | ForEach-Object { [string]$_ })
            guardedModuleIds = @($serverPolicy.guardedModuleIds | ForEach-Object { [string]$_ })
        }
        runtimeConfig = [ordered]@{
            fileName = 'mod-config.json'
            environmentDirectoryPrecedence = @('COOP_DATA_DIR', 'BANNERLORD_USER_DIR')
            semanticPath = 'difficulty.birthAndDeath'
            requiredBooleanValue = $true
            packagedDefaultIsNotRuntimeEvidence = $true
        }
        payloads = @($Plan.AssemblyAudit.ServerHarmonyPins | ForEach-Object {
            [ordered]@{
                path = [string]$_.path
                size = [long]$_.size
                sha256 = [string]$_.sha256
                assemblyFullName = [string]$_.assemblyFullName
            }
        })
        metadataAudit = [ordered]@{
            buildTimeVerified = $true
            method = 'PE metadata inspection without loading target assemblies; runtime preflight enforces the exact audited byte hashes.'
        }
    }
    $serverExpectationJson = $serverExpectation | ConvertTo-Json -Depth 20
    $serverExpectationContent = $serverExpectationJson + "`n"
    $serverExpectationHash = Get-StringSha256 -Text $serverExpectationContent
    $checksumLines.Add("$serverExpectationHash *SERVER-HARMONY.json")

    foreach ($module in $Plan.Modules) {
        $config = $module.Config
        $managedEntry = @($ManagedReceipt.Record.modules | Where-Object { [string]$_.moduleId -ceq [string]$module.Descriptor.ModuleId })[0]
        $includedRecords = New-Object System.Collections.Generic.List[object]
        foreach ($file in $module.IncludedFiles) {
            $suitePath = "Modules/$($module.Descriptor.ModuleId)/$($file.RelativePath)"
            $includedRecords.Add([ordered]@{
                path   = $file.RelativePath
                size   = $file.Length
                sha256 = $file.Sha256
            })
            $checksumLines.Add("$($file.Sha256) *$suitePath")
        }
        $excludedRecords = @($module.ExcludedFiles | ForEach-Object {
            [ordered]@{
                path   = $_.RelativePath
                size   = $_.Length
                sha256 = $_.Sha256
                rule   = $_.Rule
                reason = $_.Reason
            }
        })
        $licenseRecords = @($module.SourceSnapshot.Files |
            Where-Object { [System.IO.Path]::GetFileName($_.RelativePath) -match '^(?i:licen[cs]e|copying|notice|authors?)(\..*)?$' } |
            ForEach-Object { [ordered]@{ path = $_.RelativePath; sha256 = $_.Sha256 } })

        $moduleRecords.Add([ordered]@{
            loadOrder       = [int]$config.loadOrder
            placement       = [string]$config.placement
            workshopId      = $module.WorkshopId
            steamManifestId = $module.SteamManifestId
            workshopUrl     = [string]$config.workshopUrl
            moduleId        = $module.Descriptor.ModuleId
            name            = $module.Descriptor.Name
            version         = $module.Descriptor.Version
            creator         = [string]$config.creator
            sourceDigest    = $module.SourceSnapshot.Digest
            contentSha256   = [string]$managedEntry.contentSha256
            configurationSha256 = [string]$managedEntry.configurationSha256
            sourceFileCount = $module.SourceSnapshot.Files.Count
            stagedFileCount = $module.IncludedFiles.Count
            files           = $includedRecords.ToArray()
            excludedFiles   = $excludedRecords
            discoveredLicenseFiles = $licenseRecords
        })

        $creditLines.Add("## $($module.Descriptor.Name)")
        $creditLines.Add('')
        $creditLines.Add("- Creator: $($config.creator)")
        $creditLines.Add("- Module ID: $($module.Descriptor.ModuleId)")
        $creditLines.Add("- Version: $($module.Descriptor.Version)")
        $creditLines.Add("- Workshop: $($config.workshopUrl)")
        $creditLines.Add("- Permission record: $($config.permission.status); $($config.permission.scope)")
        $creditLines.Add('')
        $loadOrderLines.Add(('{0:D3}  {1}  ({2})  [{3}]' -f [int]$config.loadOrder, $module.Descriptor.ModuleId, $module.Descriptor.Name, [string]$config.placement))
    }

    $coopFiles = New-Object System.Collections.Generic.List[object]
    foreach ($file in $Plan.Coop.IncludedFiles) {
        $coopFiles.Add([ordered]@{ path = $file.RelativePath; size = $file.Length; sha256 = $file.Sha256 })
        $checksumLines.Add("$($file.Sha256) *Modules/$($Plan.Coop.Descriptor.ModuleId)/$($file.RelativePath)")
    }
    $receiptBytes = [System.Text.Encoding]::UTF8.GetBytes($ManagedReceipt.Json + "`n")
    $receiptHash = Get-StringSha256 -Text ($ManagedReceipt.Json + "`n")
    $checksumLines.Add("$receiptHash *Modules/$($Plan.Coop.Descriptor.ModuleId)/WorkshopSuite/MANIFEST.json")

    $manifestRecord = [ordered]@{
        schemaVersion = 1
        suiteId       = [string]$Plan.Manifest.suite.id
        displayName   = [string]$Plan.Manifest.suite.displayName
        appId         = [string]$Plan.Manifest.suite.appId
        distribution = [ordered]@{
            audience = [string]$Plan.Manifest.suite.audience
            memberCount = [int]$Plan.Manifest.suite.memberCount
            permissionAttestation = [string]$Plan.Manifest.suite.permissionAttestation
            permissionDate = [string]$Plan.Manifest.suite.permissionDate
        }
        moduleCount   = $Plan.Modules.Count
        modules       = $moduleRecords.ToArray()
        coop           = [ordered]@{
            moduleId = $Plan.Coop.Descriptor.ModuleId
            name = $Plan.Coop.Descriptor.Name
            version = $Plan.Coop.Descriptor.Version
            sourceDigest = $Plan.Coop.SourceSnapshot.Digest
            birthAndDeath = [ordered]@{
                packagedDefaultPath = [string]$Plan.Coop.BirthAndDeathConfig.RelativePath
                packagedDefaultSha256 = [string]$Plan.Coop.BirthAndDeathConfig.Sha256
                packagedDefaultEnabled = [bool]$Plan.Coop.BirthAndDeathConfig.PackagedDefaultEnabled
                runtimeValueVerified = [bool]$Plan.Coop.BirthAndDeathConfig.RuntimeValueVerified
                taleWorldsModuleActive = [bool]$Plan.Coop.BirthAndDeathConfig.TaleWorldsModuleActive
            }
            stagedFileCount = $Plan.Coop.IncludedFiles.Count + 1
            files = $coopFiles.ToArray()
            managedSuiteManifest = [ordered]@{
                path = 'WorkshopSuite/MANIFEST.json'
                size = $receiptBytes.Length
                sha256 = $receiptHash
                receiptSha256 = $ManagedReceipt.Record.receiptSha256
            }
            excludedFiles = @($Plan.Coop.ExcludedFiles | ForEach-Object {
                [ordered]@{ path = $_.RelativePath; size = $_.Length; sha256 = $_.Sha256; rule = $_.Rule; reason = $_.Reason }
            })
        }
        assemblyAudit  = [ordered]@{
            inspectedDllCount = $Plan.AssemblyAudit.InspectedDllCount
            activeManagedDllCount = $Plan.AssemblyAudit.ManagedDllCount
            closureProofs = $Plan.AssemblyAudit.ClosureProofs
            sideBySideAllowances = $Plan.AssemblyAudit.SideBySideAllowances
            sameIdentityDuplicates = $Plan.AssemblyAudit.SameIdentityDuplicates
            requiredCoopAssemblyPins = $Plan.AssemblyAudit.RequiredCoopAssemblyPins
            serverHarmonyPins = $Plan.AssemblyAudit.ServerHarmonyPins
        }
        assemblyPolicies = $Plan.Manifest.assemblyPolicies
        serverHarmonyPreflight = [ordered]@{
            scriptPath = [string]$harmonyPolicy.scriptPath
            expectationPath = [string]$harmonyPolicy.expectationPath
            expectationSha256 = $serverExpectationHash
            files = $preflightFiles.ToArray()
        }
        clientInstaller = [ordered]@{
            scriptPath = 'Setup-ManagedSuiteClient.ps1'
            launcherPath = 'Run-ClientSetup.cmd'
            validatesArbitraryGamePath = $true
            activatesOnlyActiveModuleOrder = $true
            files = $clientInstallerFiles.ToArray()
        }
        activationPolicy = $Plan.Manifest.activationPolicy
    }

    $permissionText = @(
        "# $($Plan.Manifest.suite.displayName) permission record",
        '',
        "Distribution scope: $($Plan.Manifest.suite.audience) ($($Plan.Manifest.suite.memberCount) people).",
        '',
        [string]$Plan.Manifest.suite.permissionAttestation,
        '',
        "Permission reported on: $($Plan.Manifest.suite.permissionDate)",
        '',
        'This record preserves the project owner''s permission attestation. Keep the original evidence and creator messages with the private project records.',
        'It does not replace upstream license text. Any license, notice, or author file supplied inside a module remains inside that separate module.'
    ) -join "`n"

    $installText = @(
        $Plan.Manifest.suite.displayName,
        ('=' * ([string]$Plan.Manifest.suite.displayName).Length),
        '',
        'Rendered Windows clients (recommended):',
        '1. Extract the complete distribution to a normal folder; do not run setup from inside the ZIP.',
        '2. Close Bannerlord and its launcher, then double-click Run-ClientSetup.cmd.',
        '3. Accept the detected game folder or paste any correct Bannerlord folder when prompted, for example G:\Steam\steamapps\common\Mount & Blade II Bannerlord.',
        '4. The installer verifies package hashes and SubModule.xml files, backs up existing same-ID module folders and LauncherData.xml, copies every managed module separately, and enables only activationPolicy.client.activeModuleOrder. Guarded modules and TaleWorlds BirthAndDeath remain disabled.',
        '',
        'Advanced/manual fallback:',
        '5. Copy every directory under Modules into the game''s Modules directory. This includes Coop; the managed receipt at Coop/WorkshopSuite/MANIFEST.json must stay with it. Keep each directory separate; do not merge bin or ModuleData folders.',
        '6. In the launcher, activate only entries marked ACTIVE in CLIENT-LOAD-ORDER.txt. Framework/gameplay originals marked STAGED-INACTIVE remain feature-blocked.',
        '',
        'Dedicated/headless server:',
        '7. Stage the complete package so its receipt and hashes are available, then follow SERVER-ACTIVATION.txt. Use the exact seven-entry production server order recorded there; do not add guarded, never-active, or otherwise unreviewed modules.',
        '8. Run the Windows or Linux server preflight against the staged server root, launch module order, and resolved runtime CoopData/mod-config.json. It must report the runtime path/fingerprint and semantic difficulty.birthAndDeath=true; the staged default alone is not runtime evidence. A failure blocks launch.',
        '9. Compare SHA256SUMS.txt when diagnosing a mismatch.',
        '',
        'The builder intentionally omits embedded copies of game assemblies and duplicate Harmony/MCM dependencies from gameplay modules and Coop. Rendered clients and the selected dedicated-server topology activate the exact pinned Bannerlord.Harmony payload as the sole packaged Harmony provider.',
        '',
        'Staging proves package identity, not that original single-player campaign adapters are safe on TaleWorlds official headless. Guarded/feature-blocked components remain blocked until their role-specific E2E gate passes.'
    ) -join "`n"

    $clientOrderLines = New-Object System.Collections.Generic.List[string]
    $clientOrderLines.Add('Friend Edition rendered-client staging and activation policy')
    $clientOrderLines.Add('===========================================================')
    $clientOrderLines.Add('')
    $clientPolicy = $Plan.Manifest.activationPolicy.client
    $activeClientIds = @($clientPolicy.activeModuleOrder | ForEach-Object { [string]$_ })
    $clientPosition = 1
    foreach ($id in @($clientPolicy.exactModuleOrder)) {
        $state = if ([string]$id -in $activeClientIds) { 'ACTIVE' } else { 'STAGED-INACTIVE' }
        $clientOrderLines.Add(('{0:D2}. [{1}] {2}' -f $clientPosition, $state, [string]$id))
        $clientPosition++
    }
    $clientOrderLines.Add('')
    $clientOrderLines.Add('All three clients stage this exact receipt-pinned order but activate only ACTIVE entries. STAGED-INACTIVE is not a claim of E2E compatibility; those components remain feature-blocked until their explicit gates pass. Keep TaleWorlds BirthAndDeath disabled. Birth/Death is accepted only from the authenticated host runtime config/effective-setting log, never from the packaged seed template.')
    $serverLines = New-Object System.Collections.Generic.List[string]
    $serverLines.Add('Friend Edition dedicated/headless activation policy')
    $serverLines.Add('===============================================')
    $serverLines.Add('')
    $serverLines.Add('Stage all Modules directories so Coop can read WorkshopSuite/MANIFEST.json and advertise trusted package hashes.')
    $serverLines.Add('Do not infer activation from staging. Packaging has not proven the original single-player Workshop campaign DLLs safe on TaleWorlds official headless.')
    $serverLines.Add('')
    $serverLines.Add("Canonical Harmony policy: $($serverPolicy.canonicalHarmonyPolicy)")
    $serverLines.Add("Exact production order: $(@($serverPolicy.exactActiveModuleOrder) -join ' -> ')")
    $serverLines.Add('Before launch, run:')
    $serverLines.Add("  Windows: .\Verify-ServerHarmony.ps1 -ServerRoot 'C:\path\to\server\engine' -RuntimeModConfigPath 'C:\path\to\CoopData\mod-config.json' -ActiveModuleIds @('Bannerlord.Harmony','Native','SandBoxCore','CustomBattle','Sandbox','StoryMode','Coop') -NonInteractive")
    $serverLines.Add("  Linux:   python3 ./Verify-ServerHarmony.py --server-root '/path/to/server/engine' --runtime-mod-config '/path/to/CoopData/mod-config.json' --active-modules 'Bannerlord.Harmony,Native,SandBoxCore,CustomBattle,Sandbox,StoryMode,Coop'")
    $serverLines.Add('The production launch list must exactly match the seven-entry order above. Bannerlord.Harmony precedes Native and Coop; no extra module is accepted. Preflight rejects stale Coop/other 0Harmony copies and does not modify or start the server.')
    $serverLines.Add('')
    $serverLines.Add('Never activate on headless:')
    foreach ($id in @($serverPolicy.neverActivateModuleIds)) { $serverLines.Add("- $id") }
    $serverLines.Add('')
    $serverLines.Add('Guarded/feature-blocked; leave original module inactive until its explicit headless + authority E2E gate passes:')
    foreach ($id in @($serverPolicy.guardedModuleIds)) { $serverLines.Add("- $id") }
    $serverLines.Add('')
    $serverLines.Add('Coop-integrated adapters must fail closed when the corresponding original module is inactive. DismembermentPlus is client presentation only and must never initialize on headless. TaleWorlds BirthAndDeath stays disabled on both roles. Preflight and runtime authority must both log the resolved runtime config fingerprint and effective difficulty.birthAndDeath=true; a true packaged default is not proof of the live value.')

    return [pscustomobject]@{
        ManifestJson = ($manifestRecord | ConvertTo-Json -Depth 100)
        Checksums    = ((@($checksumLines | Sort-Object) -join "`n") + "`n")
        Credits      = (($creditLines.ToArray() -join "`n") + "`n")
        Permissions  = ($permissionText + "`n")
        LoadOrder    = (($clientOrderLines.ToArray() -join "`n") + "`n")
        ServerActivation = (($serverLines.ToArray() -join "`n") + "`n")
        ServerHarmonyExpectation = $serverExpectationContent
        Install      = ($installText + "`n")
    }
}

function Test-WorkshopSuite {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$SuiteRoot,
        [string]$ExpectedSuiteId
    )

    $root = Get-FullPath $SuiteRoot
    $manifestPath = Join-Path $root 'MANIFEST.json'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw "Suite manifest is missing: $manifestPath"
    }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if (-not [string]::IsNullOrWhiteSpace($ExpectedSuiteId) -and [string]$manifest.suiteId -cne $ExpectedSuiteId) {
        throw "Suite id '$($manifest.suiteId)' does not match expected '$ExpectedSuiteId'."
    }
    if ([int]$manifest.moduleCount -ne @($manifest.modules).Count) {
        throw 'Suite moduleCount does not match its module records.'
    }

    $verified = 0
    foreach ($module in @($manifest.modules)) {
        $moduleRoot = Join-Path (Join-Path $root 'Modules') ([string]$module.moduleId)
        if (-not (Test-Path -LiteralPath (Join-Path $moduleRoot 'SubModule.xml') -PathType Leaf)) {
            throw "Staged module '$($module.moduleId)' has no SubModule.xml."
        }
        foreach ($file in @($module.files)) {
            $path = Join-Path $moduleRoot ([string]$file.path).Replace('/', '\')
            if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
                throw "Staged file is missing: $path"
            }
            $actualHash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
            if ($actualHash -cne [string]$file.sha256 -or (Get-Item -LiteralPath $path).Length -ne [long]$file.size) {
                throw "Staged file failed hash/size verification: $path"
            }
            $verified++
        }
        foreach ($excluded in @($module.excludedFiles)) {
            $path = Join-Path $moduleRoot ([string]$excluded.path).Replace('/', '\')
            if (Test-Path -LiteralPath $path) {
                throw "Excluded runtime file is present in the suite: $path"
            }
        }
    }

    $coopRoot = Join-Path (Join-Path $root 'Modules') ([string]$manifest.coop.moduleId)
    foreach ($file in @($manifest.coop.files)) {
        $path = Join-Path $coopRoot ([string]$file.path).Replace('/', '\')
        if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or
            (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -cne [string]$file.sha256) {
            throw "Composed Coop file failed verification: $path"
        }
        $verified++
    }
    $receipt = $manifest.coop.managedSuiteManifest
    $receiptPath = Join-Path $coopRoot ([string]$receipt.path).Replace('/', '\')
    if (-not (Test-Path -LiteralPath $receiptPath -PathType Leaf)) {
        throw "Managed Workshop receipt is missing from Coop: $receiptPath"
    }
    if ((Get-FileHash -LiteralPath $receiptPath -Algorithm SHA256).Hash.ToLowerInvariant() -cne [string]$receipt.sha256) {
        throw 'Managed Workshop receipt failed file hash verification.'
    }
    $managed = Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json
    if ([int]$managed.moduleCount -ne [int]$manifest.moduleCount -or [string]$managed.suiteId -cne [string]$manifest.suiteId -or
        [string]$managed.receiptSha256 -cne [string]$receipt.receiptSha256) {
        throw 'Managed Workshop receipt identity/count/digest does not match the suite manifest.'
    }
    $receiptLines = New-Object System.Collections.Generic.List[string]
    foreach ($entry in @($managed.modules | Sort-Object { ([string]$_.moduleId).ToLowerInvariant() })) {
        $rootEntry = @($manifest.modules | Where-Object { [string]$_.moduleId -ceq [string]$entry.moduleId })
        if ($rootEntry.Count -ne 1 -or [string]$rootEntry[0].workshopId -cne [string]$entry.workshopId -or
            [string]$rootEntry[0].steamManifestId -cne [string]$entry.steamManifestId -or
            [string]$rootEntry[0].version -cne [string]$entry.version -or
            [int]$rootEntry[0].loadOrder -ne [int]$entry.loadOrder -or
            [string]$rootEntry[0].contentSha256 -cne [string]$entry.contentSha256 -or
            [string]$rootEntry[0].configurationSha256 -cne [string]$entry.configurationSha256) {
            throw "Root and installed managed receipts diverge for '$($entry.moduleId)'."
        }
        $receiptLines.Add("$(([string]$entry.moduleId).ToLowerInvariant())|$($entry.workshopId)|$($entry.steamManifestId)|$($entry.version)|$($entry.loadOrder)|$($entry.contentSha256)|$($entry.configurationSha256)")
    }
    $calculatedReceipt = Get-ReceiptDigest -ReceiptLines $receiptLines
    if ($calculatedReceipt -cne [string]$managed.receiptSha256) {
        throw 'Managed Workshop receipt SHA-256 does not match its canonical module records.'
    }

    $preflight = $manifest.serverHarmonyPreflight
    foreach ($file in @($preflight.files)) {
        $path = Join-Path $root ([string]$file.path).Replace('/', '\')
        if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or
            (Get-Item -LiteralPath $path).Length -ne [long]$file.size -or
            (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -cne [string]$file.sha256) {
            throw "Server Harmony preflight tool failed staged hash/size verification: $path"
        }
        $verified++
    }
    $expectationPath = Join-Path $root ([string]$preflight.expectationPath).Replace('/', '\')
    if (-not (Test-Path -LiteralPath $expectationPath -PathType Leaf) -or
        (Get-FileHash -LiteralPath $expectationPath -Algorithm SHA256).Hash.ToLowerInvariant() -cne [string]$preflight.expectationSha256) {
        throw 'Pinned server Harmony expectation failed staged hash verification.'
    }
    $serverExpectation = Get-Content -LiteralPath $expectationPath -Raw | ConvertFrom-Json
    if ([string]$serverExpectation.moduleId -cne [string]$manifest.activationPolicy.server.harmonyPreflight.moduleId -or
        [string]$serverExpectation.moduleVersion -cne [string]$manifest.activationPolicy.server.harmonyPreflight.moduleVersion -or
        (@($serverExpectation.activation.exactActiveModuleOrder | ForEach-Object { [string]$_ }) -join '|') -cne
            (@($manifest.activationPolicy.server.exactActiveModuleOrder | ForEach-Object { [string]$_ }) -join '|') -or
        (@($serverExpectation.activation.requiredModuleIds | ForEach-Object { [string]$_ }) -join '|') -cne
            (@($manifest.activationPolicy.server.exactActiveModuleOrder | ForEach-Object { [string]$_ }) -join '|') -or
        (@($serverExpectation.activation.neverActivateModuleIds | ForEach-Object { [string]$_ } | Sort-Object) -join '|') -cne
            (@($manifest.activationPolicy.server.neverActivateModuleIds | ForEach-Object { [string]$_ } | Sort-Object) -join '|') -or
        (@($serverExpectation.activation.guardedModuleIds | ForEach-Object { [string]$_ } | Sort-Object) -join '|') -cne
            (@($manifest.activationPolicy.server.guardedModuleIds | ForEach-Object { [string]$_ } | Sort-Object) -join '|') -or
        -not [bool]$serverExpectation.metadataAudit.buildTimeVerified -or
        [string]$serverExpectation.runtimeConfig.semanticPath -cne 'difficulty.birthAndDeath' -or
        -not [bool]$serverExpectation.runtimeConfig.requiredBooleanValue -or
        -not [bool]$serverExpectation.runtimeConfig.packagedDefaultIsNotRuntimeEvidence -or
        [bool]$manifest.coop.birthAndDeath.runtimeValueVerified) {
        throw 'Pinned server Harmony expectation diverges from the activation policy.'
    }
    foreach ($payload in @($serverExpectation.payloads)) {
        $proof = @($manifest.assemblyAudit.serverHarmonyPins | Where-Object {
            [string]$_.path -ceq [string]$payload.path -and [string]$_.sha256 -ceq [string]$payload.sha256 -and
            [string]$_.assemblyFullName -ceq [string]$payload.assemblyFullName -and [long]$_.size -eq [long]$payload.size -and
            [bool]$_.buildTimeVerified
        })
        if ($proof.Count -ne 1) {
            throw "Server Harmony runtime expectation lacks a matching build-time AssemblyName/hash proof: $($payload.path)"
        }
    }

    $clientInstaller = $manifest.clientInstaller
    if ($null -eq $clientInstaller -or [string]$clientInstaller.scriptPath -cne 'Setup-ManagedSuiteClient.ps1' -or
        [string]$clientInstaller.launcherPath -cne 'Run-ClientSetup.cmd' -or
        -not [bool]$clientInstaller.validatesArbitraryGamePath -or
        -not [bool]$clientInstaller.activatesOnlyActiveModuleOrder) {
        throw 'Managed client-installer record is missing or does not enforce the approved active-only policy.'
    }
    foreach ($file in @($clientInstaller.files)) {
        $path = Join-Path $root ([string]$file.path).Replace('/', '\')
        if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or
            (Get-Item -LiteralPath $path).Length -ne [long]$file.size -or
            (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -cne [string]$file.sha256) {
            throw "Managed client installer failed staged hash/size verification: $path"
        }
        $verified++
    }

    $modulesRoot = Join-Path $root 'Modules'
    $harmonyProviderIds = New-Object System.Collections.Generic.List[string]
    foreach ($dll in @(Get-ChildItem -LiteralPath $modulesRoot -Filter '*.dll' -Recurse -File)) {
        $relative = $dll.FullName.Substring($modulesRoot.Length).TrimStart('\', '/')
        $segments = $relative -split '[\\/]'
        if ($segments.Count -lt 2) { throw "DLL is not inside a separate module root: $($dll.FullName)" }
        $moduleId = $segments[0]
        foreach ($policy in @($manifest.assemblyPolicies)) {
            if ($dll.Name -like [string]$policy.fileNamePattern) {
                $allowed = @($policy.allowedModuleIds | ForEach-Object { [string]$_ })
                if ([string]$moduleId -notin $allowed) {
                    throw "Staged assembly policy violation: '$($dll.Name)' remains in '$moduleId'; allowed providers: $($allowed -join ', ')."
                }
            }
        }
        if ($dll.Name -ieq '0Harmony.dll' -and [string]$moduleId -notin $harmonyProviderIds) {
            $harmonyProviderIds.Add([string]$moduleId)
        }
    }
    if ($harmonyProviderIds.Count -ne 1 -or
        [string]$harmonyProviderIds[0] -cne [string]$serverExpectation.moduleId) {
        throw "The staged suite must contain exactly one packaged Harmony provider module '$($serverExpectation.moduleId)'; found: $($harmonyProviderIds -join ', ')."
    }

    return [pscustomobject]@{
        SuiteId       = [string]$manifest.suiteId
        ModuleCount   = [int]$manifest.moduleCount
        VerifiedFiles = $verified
    }
}

function Remove-OwnedSuiteDirectory {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$ExpectedSuiteId,
        [Parameter(Mandatory = $true)][string[]]$ProtectedPaths
    )

    $safePath = Assert-SafeOutputPath -OutputPath $Path -ProtectedPaths $ProtectedPaths
    $manifestPath = Join-Path $safePath 'MANIFEST.json'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw "Refusing to remove unowned directory without MANIFEST.json: $safePath"
    }
    $existing = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ([string]$existing.suiteId -cne $ExpectedSuiteId) {
        throw "Refusing to remove suite '$($existing.suiteId)' while replacing '$ExpectedSuiteId'."
    }
    Remove-Item -LiteralPath $safePath -Recurse -Force
}

function New-DeterministicSuiteArchive {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$SuiteRoot,
        [Parameter(Mandatory = $true)][string]$ArchivePath,
        [Parameter(Mandatory = $true)][string[]]$ProtectedPaths,
        [switch]$Force
    )

    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $suitePath = Get-FullPath $SuiteRoot
    $archive = Assert-SafeOutputPath -OutputPath $ArchivePath -ProtectedPaths $ProtectedPaths
    if (Test-PathIsInside -Candidate $archive -Parent $suitePath) {
        throw 'ArchivePath must be outside the staged suite directory.'
    }
    if ([System.IO.Path]::GetExtension($archive) -ine '.zip') {
        throw "ArchivePath must end in .zip: $archive"
    }
    if ((Test-Path -LiteralPath $archive) -and -not $Force) {
        throw "Archive already exists; pass -Force to replace it: $archive"
    }

    $archiveParent = Split-Path -Parent $archive
    if (-not (Test-Path -LiteralPath $archiveParent -PathType Container)) {
        New-Item -ItemType Directory -Path $archiveParent -Force | Out-Null
    }
    $temporary = Join-Path $archiveParent ('.' + [System.IO.Path]::GetFileName($archive) + '.partial.' + [guid]::NewGuid().ToString('N'))
    $stream = [System.IO.File]::Open($temporary, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
    try {
        $zip = New-Object System.IO.Compression.ZipArchive($stream, [System.IO.Compression.ZipArchiveMode]::Create, $true)
        try {
            foreach ($file in @(Get-ChildItem -LiteralPath $suitePath -File -Force -Recurse | Sort-Object FullName)) {
                $relative = Get-RelativePathSafe -Root $suitePath -Path $file.FullName
                $entry = $zip.CreateEntry($relative, [System.IO.Compression.CompressionLevel]::Optimal)
                $entry.LastWriteTime = New-Object System.DateTimeOffset(1980, 1, 1, 0, 0, 0, [System.TimeSpan]::Zero)
                $input = [System.IO.File]::OpenRead($file.FullName)
                $output = $entry.Open()
                try {
                    $input.CopyTo($output)
                }
                finally {
                    $output.Dispose()
                    $input.Dispose()
                }
            }
        }
        finally {
            $zip.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }

    if (Test-Path -LiteralPath $archive) {
        Remove-Item -LiteralPath $archive -Force
    }
    Move-Item -LiteralPath $temporary -Destination $archive
    $hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    Write-Utf8File -Path ($archive + '.sha256') -Content ("$hash *$([System.IO.Path]::GetFileName($archive))`n")
    return [pscustomobject]@{ Path = $archive; Sha256 = $hash; Length = (Get-Item -LiteralPath $archive).Length }
}

function Invoke-WorkshopSuiteStage {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][pscustomobject]$Plan,
        [Parameter(Mandatory = $true)][string]$OutputRoot,
        [string]$ArchivePath,
        [switch]$Force
    )

    $protected = @($Plan.Workshop.WorkshopRoot, $Plan.Workshop.ContentRoot)
    $output = Assert-SafeOutputPath -OutputPath $OutputRoot -ProtectedPaths $protected
    if ((Test-Path -LiteralPath $output) -and -not $Force) {
        throw "Output already exists; pass -Force to replace this suite-owned directory: $output"
    }
    if (Test-Path -LiteralPath $output) {
        $existingManifest = Join-Path $output 'MANIFEST.json'
        if (-not (Test-Path -LiteralPath $existingManifest -PathType Leaf)) {
            throw "Refusing to replace an output directory not owned by this builder: $output"
        }
        $existing = Get-Content -LiteralPath $existingManifest -Raw | ConvertFrom-Json
        if ([string]$existing.suiteId -cne [string]$Plan.Manifest.suite.id) {
            throw "Refusing to replace suite '$($existing.suiteId)' with '$($Plan.Manifest.suite.id)'."
        }
    }

    $parent = Split-Path -Parent $output
    if (-not (Test-Path -LiteralPath $parent -PathType Container)) {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
    }
    $staging = Join-Path $parent ('.' + (Split-Path -Leaf $output) + '.staging.' + [guid]::NewGuid().ToString('N'))
    $backup = Join-Path $parent ('.' + (Split-Path -Leaf $output) + '.backup.' + [guid]::NewGuid().ToString('N'))
    Assert-SafeOutputPath -OutputPath $staging -ProtectedPaths $protected | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $staging 'Modules') -Force | Out-Null

    try {
        foreach ($module in $Plan.Modules) {
            $targetModule = Join-Path (Join-Path $staging 'Modules') $module.Descriptor.ModuleId
            New-Item -ItemType Directory -Path $targetModule -Force | Out-Null
            foreach ($file in $module.IncludedFiles) {
                $destination = Join-Path $targetModule $file.RelativePath.Replace('/', '\')
                $destinationFull = Get-FullPath $destination
                if (-not (Test-PathIsInside -Candidate $destinationFull -Parent $targetModule)) {
                    throw "Planned destination escapes module root: $destinationFull"
                }
                $destinationParent = Split-Path -Parent $destinationFull
                if (-not (Test-Path -LiteralPath $destinationParent -PathType Container)) {
                    New-Item -ItemType Directory -Path $destinationParent -Force | Out-Null
                }
                Copy-Item -LiteralPath $file.FullName -Destination $destinationFull
                $copiedHash = (Get-FileHash -LiteralPath $destinationFull -Algorithm SHA256).Hash.ToLowerInvariant()
                if ($copiedHash -cne $file.Sha256) {
                    throw "Copy verification failed for '$($file.FullName)'."
                }
            }
        }

        $targetCoop = Join-Path (Join-Path $staging 'Modules') $Plan.Coop.Descriptor.ModuleId
        New-Item -ItemType Directory -Path $targetCoop -Force | Out-Null
        foreach ($file in $Plan.Coop.IncludedFiles) {
            $destination = Join-Path $targetCoop $file.RelativePath.Replace('/', '\')
            if (-not (Test-PathIsInside -Candidate $destination -Parent $targetCoop)) {
                throw "Planned Coop destination escapes its module root: $destination"
            }
            $destinationParent = Split-Path -Parent $destination
            if (-not (Test-Path -LiteralPath $destinationParent -PathType Container)) {
                New-Item -ItemType Directory -Path $destinationParent -Force | Out-Null
            }
            Copy-Item -LiteralPath $file.FullName -Destination $destination
            if ((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant() -cne $file.Sha256) {
                throw "Copy verification failed for Coop file '$($file.FullName)'."
            }
        }

        $managedReceipt = New-ManagedSuiteReceipt -Plan $Plan
        Write-Utf8File -Path (Join-Path $targetCoop 'WorkshopSuite\MANIFEST.json') -Content ($managedReceipt.Json + "`n")

        foreach ($file in $Plan.ServerPreflightFiles) {
            $destination = Join-Path $staging ([string]$file.RelativePath).Replace('/', '\')
            if (-not (Test-PathIsInside -Candidate $destination -Parent $staging)) {
                throw "Planned server preflight destination escapes suite root: $destination"
            }
            $destinationParent = Split-Path -Parent $destination
            if (-not (Test-Path -LiteralPath $destinationParent -PathType Container)) {
                New-Item -ItemType Directory -Path $destinationParent -Force | Out-Null
            }
            Copy-Item -LiteralPath $file.FullName -Destination $destination
            if ((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant() -cne $file.Sha256) {
                throw "Copy verification failed for server preflight file '$($file.FullName)'."
            }
        }

        foreach ($file in $Plan.ClientInstallerFiles) {
            $destination = Join-Path $staging ([string]$file.RelativePath).Replace('/', '\')
            if (-not (Test-PathIsInside -Candidate $destination -Parent $staging)) {
                throw "Planned client installer destination escapes suite root: $destination"
            }
            Copy-Item -LiteralPath $file.FullName -Destination $destination
            if ((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant() -cne $file.Sha256) {
                throw "Copy verification failed for client installer file '$($file.FullName)'."
            }
        }

        $documents = New-ManifestDocuments -Plan $Plan -ManagedReceipt $managedReceipt
        Write-Utf8File -Path (Join-Path $staging 'MANIFEST.json') -Content ($documents.ManifestJson + "`n")
        Write-Utf8File -Path (Join-Path $staging 'SHA256SUMS.txt') -Content $documents.Checksums
        Write-Utf8File -Path (Join-Path $staging 'CREDITS.md') -Content $documents.Credits
        Write-Utf8File -Path (Join-Path $staging 'PERMISSIONS.md') -Content $documents.Permissions
        Write-Utf8File -Path (Join-Path $staging 'LOAD-ORDER.txt') -Content $documents.LoadOrder
        Write-Utf8File -Path (Join-Path $staging 'CLIENT-LOAD-ORDER.txt') -Content $documents.LoadOrder
        Write-Utf8File -Path (Join-Path $staging 'SERVER-ACTIVATION.txt') -Content $documents.ServerActivation
        Write-Utf8File -Path (Join-Path $staging 'SERVER-HARMONY.json') -Content $documents.ServerHarmonyExpectation
        Write-Utf8File -Path (Join-Path $staging 'INSTALL.txt') -Content $documents.Install

        $verification = Test-WorkshopSuite -SuiteRoot $staging -ExpectedSuiteId ([string]$Plan.Manifest.suite.id)

        foreach ($module in $Plan.Modules) {
            $after = Get-DirectorySnapshot -Root $module.ModuleRoot
            if ($after.Digest -cne $module.SourceSnapshot.Digest) {
                throw "Source Workshop content changed while staging $($module.Descriptor.ModuleId). The source was never an output target; stop and investigate Steam or another process."
            }
        }
        $coopAfter = Get-DirectorySnapshot -Root $Plan.Coop.ModuleRoot
        if ($coopAfter.Digest -cne $Plan.Coop.SourceSnapshot.Digest) {
            throw 'Source Friend Edition Coop module changed while staging.'
        }

        $hadExisting = Test-Path -LiteralPath $output
        if ($hadExisting) {
            Move-Item -LiteralPath $output -Destination $backup
        }
        try {
            Move-Item -LiteralPath $staging -Destination $output
        }
        catch {
            if ($hadExisting -and (Test-Path -LiteralPath $backup) -and -not (Test-Path -LiteralPath $output)) {
                Move-Item -LiteralPath $backup -Destination $output
            }
            throw
        }
        if ($hadExisting -and (Test-Path -LiteralPath $backup)) {
            Remove-OwnedSuiteDirectory -Path $backup -ExpectedSuiteId ([string]$Plan.Manifest.suite.id) -ProtectedPaths $protected
        }

        $archiveResult = $null
        if (-not [string]::IsNullOrWhiteSpace($ArchivePath)) {
            $archiveResult = New-DeterministicSuiteArchive -SuiteRoot $output -ArchivePath $ArchivePath -ProtectedPaths $protected -Force:$Force
        }
        return [pscustomobject]@{
            OutputRoot     = $output
            ModuleCount   = $verification.ModuleCount
            VerifiedFiles = $verification.VerifiedFiles
            Archive       = $archiveResult
            SourceUnchanged = $true
        }
    }
    catch {
        if (Test-Path -LiteralPath $staging) {
            Remove-Item -LiteralPath $staging -Recurse -Force
        }
        throw
    }
}

Export-ModuleMember -Function @(
    'ConvertFrom-ValveKeyValues',
    'Find-BannerlordWorkshop',
    'Get-DirectorySnapshot',
    'Get-SteamLibraryRoots',
    'Invoke-WorkshopSuiteStage',
    'New-WorkshopSuitePlan',
    'Read-WorkshopState',
    'Read-WorkshopSuiteManifest',
    'Resolve-CoopModuleRoot',
    'Test-WorkshopSuite'
)
