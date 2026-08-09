#Requires -Version 5.1

[CmdletBinding()]
param(
    [string]$GamePath,
    [string]$LauncherDataPath,
    [string]$BackupRoot,
    [string]$SuiteRoot,
    [switch]$ValidateOnly,
    [switch]$NonInteractive
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($SuiteRoot)) { $SuiteRoot = $PSScriptRoot }

function Get-FullPathInput {
    param([Parameter(Mandatory = $true)][string]$Path)

    $expanded = [Environment]::ExpandEnvironmentVariables($Path.Trim().Trim('"').Trim("'"))
    if ([string]::IsNullOrWhiteSpace($expanded)) { throw 'The supplied path is empty.' }
    return [System.IO.Path]::GetFullPath($expanded).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar
    )
}

function Test-PathInside {
    param(
        [Parameter(Mandatory = $true)][string]$Candidate,
        [Parameter(Mandatory = $true)][string]$Parent
    )

    $candidatePath = (Get-FullPathInput $Candidate).TrimEnd('\', '/')
    $parentPath = (Get-FullPathInput $Parent).TrimEnd('\', '/')
    if ($candidatePath.Equals($parentPath, [System.StringComparison]::OrdinalIgnoreCase)) { return $true }
    return $candidatePath.StartsWith(
        $parentPath + [System.IO.Path]::DirectorySeparatorChar,
        [System.StringComparison]::OrdinalIgnoreCase
    )
}

function Resolve-SafeChildPath {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$RelativePath,
        [Parameter(Mandatory = $true)][string]$Description
    )

    if ([System.IO.Path]::IsPathRooted($RelativePath) -or $RelativePath.IndexOf([char]0) -ge 0) {
        throw "$Description contains an invalid rooted path: $RelativePath"
    }
    $resolved = [System.IO.Path]::GetFullPath((Join-Path $Root $RelativePath.Replace('/', '\')))
    if (-not (Test-PathInside -Candidate $resolved -Parent $Root)) {
        throw "$Description escapes its expected root: $RelativePath"
    }
    return $resolved
}

function Test-ExactContains {
    param([string[]]$Values, [string]$Value)

    foreach ($candidate in @($Values)) {
        if ([string]$candidate -ceq $Value) { return $true }
    }
    return $false
}

function Get-SteamLibraryRoots {
    $roots = New-Object System.Collections.Generic.List[string]
    $steamRoots = New-Object System.Collections.Generic.List[string]
    foreach ($key in @(
        'HKCU:\Software\Valve\Steam',
        'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam',
        'HKLM:\SOFTWARE\Valve\Steam'
    )) {
        try {
            $properties = Get-ItemProperty -LiteralPath $key -ErrorAction Stop
            foreach ($name in @('SteamPath', 'InstallPath')) {
                $property = $properties.PSObject.Properties[$name]
                if ($null -ne $property -and -not [string]::IsNullOrWhiteSpace([string]$property.Value)) {
                    $steamRoots.Add((Get-FullPathInput ([string]$property.Value)))
                }
            }
        }
        catch { }
    }
    if (${env:ProgramFiles(x86)}) { $steamRoots.Add((Join-Path ${env:ProgramFiles(x86)} 'Steam')) }
    if ($env:ProgramFiles) { $steamRoots.Add((Join-Path $env:ProgramFiles 'Steam')) }

    foreach ($steamRoot in @($steamRoots | Sort-Object -Unique)) {
        if (-not (Test-Path -LiteralPath $steamRoot -PathType Container)) { continue }
        $roots.Add((Get-FullPathInput $steamRoot))
        $libraryFile = Join-Path $steamRoot 'steamapps\libraryfolders.vdf'
        if (-not (Test-Path -LiteralPath $libraryFile -PathType Leaf)) { continue }
        foreach ($line in [System.IO.File]::ReadAllLines($libraryFile)) {
            if ($line -match '^\s*"path"\s+"([^"]+)"') {
                $library = $matches[1].Replace('\\', '\')
                if (Test-Path -LiteralPath $library -PathType Container) {
                    $roots.Add((Get-FullPathInput $library))
                }
            }
        }
    }
    return @($roots | Sort-Object -Unique)
}

function Test-BannerlordGamePath {
    param([Parameter(Mandatory = $true)][string]$RequestedPath)

    $candidate = Get-FullPathInput $RequestedPath
    $leaf = [System.IO.Path]::GetFileName($candidate)
    if ($leaf -ieq 'Modules') {
        $candidate = Split-Path -Parent $candidate
    }
    elseif ($leaf -ieq 'Coop' -and (Split-Path -Leaf (Split-Path -Parent $candidate)) -ieq 'Modules') {
        $candidate = Split-Path -Parent (Split-Path -Parent $candidate)
    }
    $nativeDescriptor = Join-Path $candidate 'Modules\Native\SubModule.xml'
    if (-not (Test-Path -LiteralPath $nativeDescriptor -PathType Leaf)) {
        throw "Bannerlord was not found at '$candidate'. Expected Modules\Native\SubModule.xml."
    }
    [xml]$native = [System.IO.File]::ReadAllText($nativeDescriptor)
    if ([string]$native.Module.Id.value -cne 'Native') {
        throw "The selected folder has an invalid Native module descriptor: $nativeDescriptor"
    }
    return $candidate
}

function Find-BannerlordGamePath {
    param([string]$RequestedPath)

    if (-not [string]::IsNullOrWhiteSpace($RequestedPath)) {
        return Test-BannerlordGamePath -RequestedPath $RequestedPath
    }
    foreach ($library in Get-SteamLibraryRoots) {
        $candidate = Join-Path $library 'steamapps\common\Mount & Blade II Bannerlord'
        if (Test-Path -LiteralPath (Join-Path $candidate 'Modules\Native\SubModule.xml') -PathType Leaf) {
            return Test-BannerlordGamePath -RequestedPath $candidate
        }
    }
    return $null
}

function Resolve-BannerlordGamePath {
    param([string]$RequestedPath, [switch]$NonInteractive)

    if (-not [string]::IsNullOrWhiteSpace($RequestedPath)) {
        return Test-BannerlordGamePath -RequestedPath $RequestedPath
    }
    $detected = Find-BannerlordGamePath
    if ($null -ne $detected) {
        if ($NonInteractive) { return $detected }
        Write-Host 'Detected Bannerlord here:' -ForegroundColor Cyan
        Write-Host "  $detected"
        $answer = Read-Host 'Press Enter to use it, or paste a different Bannerlord folder'
        if ([string]::IsNullOrWhiteSpace($answer)) { return $detected }
        $RequestedPath = $answer
    }
    elseif ($NonInteractive) {
        throw "Bannerlord was not found automatically. Pass -GamePath, for example -GamePath 'G:\Steam\steamapps\common\Mount & Blade II Bannerlord'."
    }

    while ($true) {
        if ([string]::IsNullOrWhiteSpace($RequestedPath)) {
            Write-Host 'Bannerlord was not found automatically.' -ForegroundColor Yellow
            Write-Host 'In Steam, choose Bannerlord > Manage > Browse local files, then copy the folder address.'
            Write-Host 'Example: G:\Steam\steamapps\common\Mount & Blade II Bannerlord' -ForegroundColor DarkGray
            $RequestedPath = Read-Host 'Paste the Bannerlord game folder'
        }
        if ([string]::IsNullOrWhiteSpace($RequestedPath)) { throw 'No Bannerlord folder was entered. Setup was cancelled.' }
        try { return Test-BannerlordGamePath -RequestedPath $RequestedPath }
        catch {
            Write-Host $_.Exception.Message -ForegroundColor Yellow
            Write-Host 'Please paste the folder opened by Steam, not its Modules or bin subfolder.' -ForegroundColor Yellow
            $RequestedPath = $null
        }
    }
}

function Read-ModuleDescriptor {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $null }
    [xml]$xml = [System.IO.File]::ReadAllText($Path)
    if ($null -eq $xml.Module -or [string]::IsNullOrWhiteSpace([string]$xml.Module.Id.value)) {
        throw "Invalid Bannerlord module descriptor: $Path"
    }
    return [pscustomobject]@{
        Id = [string]$xml.Module.Id.value
        Version = [string]$xml.Module.Version.value
        Name = [string]$xml.Module.Name.value
        Path = [System.IO.Path]::GetFullPath($Path)
    }
}

function Get-PackageModuleRecords {
    param([Parameter(Mandatory = $true)][pscustomobject]$Manifest)

    $records = New-Object System.Collections.Generic.List[object]
    foreach ($module in @($Manifest.modules)) {
        $records.Add([pscustomobject]@{
            ModuleId = [string]$module.moduleId
            Version = [string]$module.version
            Files = @($module.files)
            ExcludedFiles = @($module.excludedFiles)
            IsCoop = $false
        })
    }
    $coopFiles = New-Object System.Collections.Generic.List[object]
    foreach ($file in @($Manifest.coop.files)) { $coopFiles.Add($file) }
    $receipt = $Manifest.coop.managedSuiteManifest
    $coopFiles.Add([pscustomobject]@{
        path = [string]$receipt.path
        size = [long]$receipt.size
        sha256 = [string]$receipt.sha256
    })
    $records.Add([pscustomobject]@{
        ModuleId = [string]$Manifest.coop.moduleId
        Version = [string]$Manifest.coop.version
        Files = $coopFiles.ToArray()
        ExcludedFiles = @($Manifest.coop.excludedFiles)
        IsCoop = $true
    })
    return $records.ToArray()
}

function Test-ModulePayload {
    param(
        [Parameter(Mandatory = $true)][string]$ModuleRoot,
        [Parameter(Mandatory = $true)][pscustomobject]$Record,
        [switch]$RejectUnknownFiles
    )

    if (-not (Test-Path -LiteralPath $ModuleRoot -PathType Container)) {
        throw "Package module '$($Record.ModuleId)' is missing. Extract the complete distribution before running setup."
    }
    $moduleRootPath = Get-FullPathInput $ModuleRoot
    $links = @(Get-ChildItem -LiteralPath $moduleRootPath -Force -Recurse | Where-Object {
        ($_.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0
    })
    if ($links.Count -gt 0) { throw "Package module '$($Record.ModuleId)' contains an unsupported link: $($links[0].FullName)" }

    $expected = @{}
    foreach ($file in @($Record.Files)) {
        $relative = ([string]$file.path).Replace('\', '/')
        $key = $relative.ToLowerInvariant()
        if ($expected.ContainsKey($key)) { throw "Package manifest repeats '$relative' in '$($Record.ModuleId)'." }
        $expected[$key] = $true
        $path = Resolve-SafeChildPath -Root $moduleRootPath -RelativePath $relative -Description "Package file for $($Record.ModuleId)"
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Package file is missing: $path" }
        $item = Get-Item -LiteralPath $path
        $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($item.Length -ne [long]$file.size -or $hash -cne [string]$file.sha256) {
            throw "Package file failed its pinned size/SHA-256 check: $path"
        }
    }
    foreach ($excluded in @($Record.ExcludedFiles)) {
        $path = Resolve-SafeChildPath -Root $moduleRootPath -RelativePath ([string]$excluded.path) -Description "Excluded file for $($Record.ModuleId)"
        if (Test-Path -LiteralPath $path) { throw "Excluded runtime-dangerous file is present in the package: $path" }
    }
    if ($RejectUnknownFiles) {
        foreach ($file in @(Get-ChildItem -LiteralPath $moduleRootPath -File -Force -Recurse)) {
            $relative = $file.FullName.Substring($moduleRootPath.Length).TrimStart('\', '/').Replace('\', '/')
            if (-not $expected.ContainsKey($relative.ToLowerInvariant())) {
                throw "Unmanifested file exists in package module '$($Record.ModuleId)': $relative"
            }
        }
    }
    $descriptor = Read-ModuleDescriptor -Path (Join-Path $moduleRootPath 'SubModule.xml')
    if ($null -eq $descriptor -or $descriptor.Id -cne $Record.ModuleId -or $descriptor.Version -cne $Record.Version) {
        throw "Package module descriptor does not match pinned '$($Record.ModuleId)' '$($Record.Version)'."
    }
    return $descriptor
}

function Read-AndValidateSuite {
    param([Parameter(Mandatory = $true)][string]$Root)

    $resolvedRoot = Get-FullPathInput $Root
    $manifestPath = Join-Path $resolvedRoot 'MANIFEST.json'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw "MANIFEST.json is missing beside this installer. Extract the complete Friend Edition suite first: $resolvedRoot"
    }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ([int]$manifest.schemaVersion -ne 1 -or [string]::IsNullOrWhiteSpace([string]$manifest.suiteId)) {
        throw "Unsupported or invalid suite MANIFEST.json: $manifestPath"
    }
    if ([int]$manifest.moduleCount -ne @($manifest.modules).Count) {
        throw 'Suite MANIFEST.json has an inconsistent managed module count.'
    }
    $exactOrder = @($manifest.activationPolicy.client.exactModuleOrder | ForEach-Object { [string]$_ })
    $activeOrder = @($manifest.activationPolicy.client.activeModuleOrder | ForEach-Object { [string]$_ })
    if ($exactOrder.Count -eq 0 -or $activeOrder.Count -eq 0 -or [bool]$manifest.activationPolicy.client.activateAllManagedModules) {
        throw 'Suite client activation policy is missing or unsafe.'
    }
    if (@($exactOrder | Sort-Object -Unique).Count -ne $exactOrder.Count -or
        @($activeOrder | Sort-Object -Unique).Count -ne $activeOrder.Count) {
        throw 'Suite client activation policy contains duplicate module IDs.'
    }
    $lastPosition = -1
    foreach ($id in $activeOrder) {
        $position = [array]::IndexOf($exactOrder, $id)
        if ($position -lt 0 -or $position -le $lastPosition) {
            throw "Client active module '$id' is absent from or out of order in exactModuleOrder."
        }
        $lastPosition = $position
    }
    if (Test-ExactContains -Values $activeOrder -Value 'BirthAndDeath') {
        throw 'Unsafe suite policy: the optional TaleWorlds BirthAndDeath module must stay disabled.'
    }

    $installer = $manifest.clientInstaller
    if ($null -eq $installer -or [string]$installer.scriptPath -cne 'Setup-ManagedSuiteClient.ps1') {
        throw 'Suite MANIFEST.json does not contain the expected managed client-installer record.'
    }
    foreach ($file in @($installer.files)) {
        $path = Resolve-SafeChildPath -Root $resolvedRoot -RelativePath ([string]$file.path) -Description 'Client installer file'
        if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or
            (Get-Item -LiteralPath $path).Length -ne [long]$file.size -or
            (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -cne [string]$file.sha256) {
            throw "Client installer file failed its pinned size/SHA-256 check: $path"
        }
    }

    $records = Get-PackageModuleRecords -Manifest $manifest
    $seen = @{}
    $descriptors = @{}
    foreach ($record in $records) {
        if ($record.ModuleId -notmatch '^[A-Za-z0-9_.-]+$' -or $seen.ContainsKey($record.ModuleId)) {
            throw "Invalid or duplicate package module ID '$($record.ModuleId)'."
        }
        $seen[$record.ModuleId] = $true
        $moduleRoot = Join-Path (Join-Path $resolvedRoot 'Modules') $record.ModuleId
        $descriptors[$record.ModuleId] = Test-ModulePayload -ModuleRoot $moduleRoot -Record $record -RejectUnknownFiles
    }
    foreach ($record in $records) {
        if (-not (Test-ExactContains -Values $exactOrder -Value $record.ModuleId)) {
            throw "Client exactModuleOrder omits packaged module '$($record.ModuleId)'."
        }
    }
    return [pscustomobject]@{
        Root = $resolvedRoot
        Manifest = $manifest
        Records = $records
        PackageDescriptors = $descriptors
        ExactOrder = $exactOrder
        ActiveOrder = $activeOrder
    }
}

function Get-DirectModuleInventory {
    param([Parameter(Mandatory = $true)][string]$ModulesRoot)

    $entries = New-Object System.Collections.Generic.List[object]
    $byId = @{}
    foreach ($directory in @(Get-ChildItem -LiteralPath $ModulesRoot -Directory -Force -ErrorAction Stop | Sort-Object FullName)) {
        if (($directory.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Bannerlord Modules contains an unsupported linked directory: $($directory.FullName)"
        }
        $descriptorPath = Join-Path $directory.FullName 'SubModule.xml'
        if (-not (Test-Path -LiteralPath $descriptorPath -PathType Leaf)) { continue }
        try { $descriptor = Read-ModuleDescriptor -Path $descriptorPath } catch { continue }
        if ($null -eq $descriptor) { continue }
        $entry = [pscustomobject]@{
            ModuleId = [string]$descriptor.Id
            ModuleRoot = $directory.FullName
            FolderName = $directory.Name
            Descriptor = $descriptor
        }
        $entries.Add($entry)
        if (-not $byId.ContainsKey($entry.ModuleId)) {
            $byId[$entry.ModuleId] = New-Object System.Collections.Generic.List[object]
        }
        $byId[$entry.ModuleId].Add($entry)
    }
    return [pscustomobject]@{ Entries = $entries.ToArray(); ById = $byId }
}

function Get-UniqueDescriptorMap {
    param([Parameter(Mandatory = $true)][pscustomobject]$Inventory)

    $descriptors = @{}
    foreach ($id in $Inventory.ById.Keys) {
        $matches = $Inventory.ById[$id].ToArray()
        if ($matches.Count -eq 1) { $descriptors[$id] = $matches[0].Descriptor }
    }
    return $descriptors
}

function Get-DirectoryCopySnapshot {
    param([Parameter(Mandatory = $true)][string]$Root)

    $resolved = Get-FullPathInput $Root
    $files = New-Object System.Collections.Generic.List[object]
    $links = @(Get-ChildItem -LiteralPath $resolved -Force -Recurse | Where-Object {
        ($_.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0
    })
    if ($links.Count -gt 0) { throw "Refusing to back up linked content: $($links[0].FullName)" }
    foreach ($file in @(Get-ChildItem -LiteralPath $resolved -File -Force -Recurse | Sort-Object FullName)) {
        $relative = $file.FullName.Substring($resolved.Length).TrimStart('\', '/').Replace('\', '/')
        $files.Add([pscustomobject]@{
            RelativePath = $relative
            Length = [long]$file.Length
            Sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        })
    }
    $lines = @($files | ForEach-Object { "$($_.RelativePath)|$($_.Length)|$($_.Sha256)" }) -join "`n"
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($lines)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try { $digest = ([System.BitConverter]::ToString($sha.ComputeHash($bytes))).Replace('-', '').ToLowerInvariant() }
    finally { $sha.Dispose() }
    return [pscustomobject]@{ Root = $resolved; Files = $files.ToArray(); Digest = $digest }
}

function Copy-DirectoryWithVerification {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Destination
    )

    if (Test-Path -LiteralPath $Destination) { throw "Backup destination already exists: $Destination" }
    $before = Get-DirectoryCopySnapshot -Root $Source
    Copy-Item -LiteralPath $Source -Destination $Destination -Recurse
    $after = Get-DirectoryCopySnapshot -Root $Destination
    if ($before.Digest -cne $after.Digest -or $before.Files.Count -ne $after.Files.Count) {
        throw "Recoverable backup copy failed verification: $Source -> $Destination"
    }
    return $before
}

function Get-LauncherPath {
    param([string]$RequestedPath)

    if (-not [string]::IsNullOrWhiteSpace($RequestedPath)) { return Get-FullPathInput $RequestedPath }
    $documents = [Environment]::GetFolderPath('MyDocuments')
    if ([string]::IsNullOrWhiteSpace($documents)) { throw 'Windows Documents folder could not be resolved. Pass -LauncherDataPath.' }
    return Join-Path $documents 'Mount and Blade II Bannerlord\Configs\LauncherData.xml'
}

function Get-LauncherModsNode {
    param([Parameter(Mandatory = $true)][xml]$Launcher, [Parameter(Mandatory = $true)][string]$Path)

    $node = $Launcher.SelectSingleNode('/UserData/SingleplayerData/ModDatas')
    if ($null -eq $node) { $node = $Launcher.SelectSingleNode('//SingleplayerData/ModDatas') }
    if ($null -eq $node) {
        throw "LauncherData.xml does not contain SingleplayerData/ModDatas. Start and close the Bannerlord launcher once, then retry: $Path"
    }
    return $node
}

function Save-XmlAtomically {
    param([Parameter(Mandatory = $true)][xml]$Xml, [Parameter(Mandatory = $true)][string]$Path)

    $temporary = Join-Path (Split-Path -Parent $Path) ('.' + [System.IO.Path]::GetFileName($Path) + '.friend-edition.' + [guid]::NewGuid().ToString('N') + '.tmp')
    $settings = New-Object System.Xml.XmlWriterSettings
    $settings.Indent = $true
    $settings.Encoding = New-Object System.Text.UTF8Encoding($false)
    $writer = [System.Xml.XmlWriter]::Create($temporary, $settings)
    try { $Xml.Save($writer) } finally { $writer.Dispose() }
    $replaceBackup = Join-Path (Split-Path -Parent $Path) ('.' + [System.IO.Path]::GetFileName($Path) + '.friend-edition-replace-backup.' + [guid]::NewGuid().ToString('N'))
    try {
        [System.IO.File]::Replace($temporary, $Path, $replaceBackup)
        if (Test-Path -LiteralPath $replaceBackup) { Remove-Item -LiteralPath $replaceBackup -Force }
    }
    finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force } }
}

function Set-LauncherActivation {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string[]]$ExactOrder,
        [Parameter(Mandatory = $true)][string[]]$ActiveOrder,
        [Parameter(Mandatory = $true)][hashtable]$Descriptors
    )

    [xml]$launcher = [System.IO.File]::ReadAllText($Path)
    $modsNode = Get-LauncherModsNode -Launcher $launcher -Path $Path
    foreach ($node in @($modsNode.SelectNodes('UserModData'))) {
        $idNode = $node.SelectSingleNode('Id')
        $id = if ($null -ne $idNode) { [string]$idNode.InnerText } else { '' }
        if (Test-ExactContains -Values $ExactOrder -Value $id) {
            [void]$modsNode.RemoveChild($node)
            continue
        }
        $selectedNode = $node.SelectSingleNode('IsSelected')
        if ($null -eq $selectedNode) {
            $selectedNode = $launcher.CreateElement('IsSelected')
            [void]$node.AppendChild($selectedNode)
        }
        $selectedNode.InnerText = 'false'
    }
    foreach ($id in $ExactOrder) {
        if (-not $Descriptors.ContainsKey($id)) { throw "Cannot configure launcher: module '$id' has no validated SubModule.xml." }
        $descriptor = $Descriptors[$id]
        $entry = $launcher.CreateElement('UserModData')
        foreach ($pair in @(
            @('Id', $id),
            @('LastKnownVersion', [string]$descriptor.Version),
            @('IsSelected', $(if (Test-ExactContains -Values $ActiveOrder -Value $id) { 'true' } else { 'false' }))
        )) {
            $child = $launcher.CreateElement([string]$pair[0])
            $child.InnerText = [string]$pair[1]
            [void]$entry.AppendChild($child)
        }
        [void]$modsNode.AppendChild($entry)
    }
    Save-XmlAtomically -Xml $launcher -Path $Path
}

function Test-LauncherActivation {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string[]]$ExactOrder,
        [Parameter(Mandatory = $true)][string[]]$ActiveOrder
    )

    [xml]$launcher = [System.IO.File]::ReadAllText($Path)
    $modsNode = Get-LauncherModsNode -Launcher $launcher -Path $Path
    $targetNodes = @($modsNode.SelectNodes('UserModData') | Where-Object {
        $idNode = $_.SelectSingleNode('Id')
        $null -ne $idNode -and (Test-ExactContains -Values $ExactOrder -Value ([string]$idNode.InnerText))
    })
    $targetIds = @($targetNodes | ForEach-Object { [string]$_.SelectSingleNode('Id').InnerText })
    if ($targetIds.Count -ne $ExactOrder.Count -or (($targetIds -join "`n") -cne ($ExactOrder -join "`n"))) {
        throw 'LauncherData.xml does not preserve the exact managed module order.'
    }
    $selected = @($modsNode.SelectNodes('UserModData') | Where-Object {
        $node = $_.SelectSingleNode('IsSelected')
        $null -ne $node -and [string]$node.InnerText -ieq 'true'
    } | ForEach-Object { [string]$_.SelectSingleNode('Id').InnerText })
    if (($selected -join "`n") -cne ($ActiveOrder -join "`n")) {
        throw "Launcher active modules do not match the approved activeModuleOrder. Found: $($selected -join ', ')"
    }
}

$transactionRootForError = $null
$backupRunRootForError = $null

try {
    Write-Host ''
    Write-Host 'Bannerlord Coop Friend Edition - Managed Client Setup' -ForegroundColor Cyan
    Write-Host 'This verifies the private suite, installs every managed module separately, and enables only the approved client modules.'
    Write-Host ''

    $suite = Read-AndValidateSuite -Root $SuiteRoot
    $resolvedGamePath = Resolve-BannerlordGamePath -RequestedPath $GamePath -NonInteractive:$NonInteractive
    $modulesRoot = Join-Path $resolvedGamePath 'Modules'
    if ((Test-PathInside -Candidate $suite.Root -Parent $modulesRoot) -or
        (Test-PathInside -Candidate $modulesRoot -Parent $suite.Root)) {
        throw 'The extracted suite must be outside the live Bannerlord Modules directory. Move the complete package to Downloads or another normal folder and retry.'
    }
    $launcherPath = Get-LauncherPath -RequestedPath $LauncherDataPath
    if (-not (Test-Path -LiteralPath $launcherPath -PathType Leaf)) {
        throw "LauncherData.xml is missing. Start and close the Bannerlord launcher once, then rerun setup: $launcherPath"
    }
    [xml]$launcherProbe = [System.IO.File]::ReadAllText($launcherPath)
    Get-LauncherModsNode -Launcher $launcherProbe -Path $launcherPath | Out-Null

    $inventory = Get-DirectModuleInventory -ModulesRoot $modulesRoot
    $available = Get-UniqueDescriptorMap -Inventory $inventory
    foreach ($record in $suite.Records) { $available[$record.ModuleId] = $suite.PackageDescriptors[$record.ModuleId] }
    foreach ($id in $suite.ExactOrder) {
        if (-not $available.ContainsKey($id)) {
            throw "Required base/managed module '$id' is missing. Verify this Bannerlord installation before setup."
        }
    }
    Write-Host "Package: $($suite.Manifest.displayName)" -ForegroundColor Green
    Write-Host "Game folder: $resolvedGamePath" -ForegroundColor Green
    Write-Host "Managed modules to stage: $($suite.Records.Count); modules to activate: $($suite.ActiveOrder.Count)"

    $noncanonicalDuplicates = New-Object System.Collections.Generic.List[object]
    foreach ($record in $suite.Records) {
        $canonicalTarget = Get-FullPathInput (Join-Path $modulesRoot $record.ModuleId)
        if ($inventory.ById.ContainsKey($record.ModuleId)) {
            foreach ($entry in $inventory.ById[$record.ModuleId].ToArray()) {
                if (-not (Get-FullPathInput $entry.ModuleRoot).Equals($canonicalTarget, [System.StringComparison]::OrdinalIgnoreCase)) {
                    $noncanonicalDuplicates.Add($entry)
                }
            }
        }
    }

    if ($ValidateOnly) {
        if ($noncanonicalDuplicates.Count -gt 0) {
            $details = @($noncanonicalDuplicates | ForEach-Object { "$($_.ModuleId): $($_.ModuleRoot)" }) -join '; '
            throw "Duplicate/noncanonical managed module roots would collide with this suite: $details. Run normal setup to move these old copies into the recoverable install backup."
        }
        Write-Host 'VALIDATION PASSED: package hashes, module descriptors, game path, launcher data, and active-only policy are valid. No files were written.' -ForegroundColor Green
        Write-Host "Approved active order: $($suite.ActiveOrder -join ' -> ')"
        exit 0
    }
    if (Get-Process -Name 'Bannerlord', 'Bannerlord.Native', 'TaleWorlds.MountAndBlade.Launcher' -ErrorAction SilentlyContinue) {
        throw 'Close Bannerlord and its launcher before installing the suite.'
    }

    if ([string]::IsNullOrWhiteSpace($BackupRoot)) {
        $documents = [Environment]::GetFolderPath('MyDocuments')
        if ([string]::IsNullOrWhiteSpace($documents)) { throw 'Windows Documents folder could not be resolved. Pass -BackupRoot.' }
        $BackupRoot = Join-Path $documents 'Mount and Blade II Bannerlord\CoopFriendEditionBackups'
    }
    $resolvedBackupRoot = Get-FullPathInput $BackupRoot
    $backupDriveRoot = [System.IO.Path]::GetPathRoot($resolvedBackupRoot).TrimEnd('\', '/')
    if ($resolvedBackupRoot.Length -lt 12 -or $resolvedBackupRoot.TrimEnd('\', '/') -ieq $backupDriveRoot -or
        (Test-PathInside -Candidate $resolvedBackupRoot -Parent $modulesRoot) -or
        (Test-PathInside -Candidate $resolvedBackupRoot -Parent $suite.Root) -or
        (Test-PathInside -Candidate $suite.Root -Parent $resolvedBackupRoot)) {
        throw "BackupRoot overlaps a protected package/game location or is too broad: $resolvedBackupRoot"
    }
    New-Item -ItemType Directory -Path $resolvedBackupRoot -Force | Out-Null
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $backupRunRoot = Join-Path $resolvedBackupRoot ("ManagedSuite-$stamp-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
    $backupRunRootForError = $backupRunRoot
    $durableOriginalModules = Join-Path $backupRunRoot 'OriginalModules'
    New-Item -ItemType Directory -Path $durableOriginalModules -Force | Out-Null

    # All rename/swap operations remain on the game volume. The Documents backup is copy-only
    # and fully hash-verified before the first live module directory is moved.
    $transactionName = '.friend-edition-transaction-' + [guid]::NewGuid().ToString('N')
    $transactionRoot = Join-Path $modulesRoot $transactionName
    if ((Split-Path -Parent (Get-FullPathInput $transactionRoot)) -ine (Get-FullPathInput $modulesRoot) -or
        (Split-Path -Leaf $transactionRoot) -notlike '.friend-edition-transaction-*') {
        throw "Refusing unsafe game-volume transaction root: $transactionRoot"
    }
    $transactionRootForError = $transactionRoot
    $installStage = Join-Path $transactionRoot 'NewModules'
    $rollbackModules = Join-Path $transactionRoot 'OriginalModules'
    $failedModules = Join-Path $transactionRoot 'FailedNewModules'
    New-Item -ItemType Directory -Path $installStage -Force | Out-Null
    New-Item -ItemType Directory -Path $rollbackModules -Force | Out-Null

    foreach ($record in $suite.Records) {
        $source = Join-Path (Join-Path $suite.Root 'Modules') $record.ModuleId
        $target = Join-Path $installStage $record.ModuleId
        Copy-Item -LiteralPath $source -Destination $target -Recurse
        Get-ChildItem -LiteralPath $target -File -Recurse | Unblock-File -ErrorAction SilentlyContinue
        Test-ModulePayload -ModuleRoot $target -Record $record -RejectUnknownFiles | Out-Null
    }

    $launcherBackup = Join-Path $backupRunRoot 'LauncherData.xml.before-install.bak'
    Copy-Item -LiteralPath $launcherPath -Destination $launcherBackup
    if ((Get-FileHash -LiteralPath $launcherBackup -Algorithm SHA256).Hash -cne
        (Get-FileHash -LiteralPath $launcherPath -Algorithm SHA256).Hash) {
        throw 'LauncherData.xml backup failed verification; no live module was changed.'
    }

    $relocations = New-Object System.Collections.Generic.List[object]
    $seenRelocationPaths = @{}
    foreach ($record in $suite.Records) {
        $canonicalTarget = Get-FullPathInput (Join-Path $modulesRoot $record.ModuleId)
        if (Test-Path -LiteralPath $canonicalTarget) {
            $key = $canonicalTarget.ToLowerInvariant()
            $seenRelocationPaths[$key] = $true
            $relocations.Add([pscustomobject]@{
                ModuleId = $record.ModuleId; Kind = 'canonical-replacement'; SourcePath = $canonicalTarget
                FolderName = (Split-Path -Leaf $canonicalTarget); HoldingPath = (Join-Path $rollbackModules (Split-Path -Leaf $canonicalTarget))
                DurableBackupPath = $null; SourceDigest = $null; Moved = $false
            })
        }
    }
    foreach ($entry in $noncanonicalDuplicates) {
        $source = Get-FullPathInput $entry.ModuleRoot
        $key = $source.ToLowerInvariant()
        if ($seenRelocationPaths.ContainsKey($key)) { continue }
        if ((Split-Path -Parent $source) -ine (Get-FullPathInput $modulesRoot)) {
            throw "Refusing to relocate a module that is not a direct child of Modules: $source"
        }
        $seenRelocationPaths[$key] = $true
        $relocations.Add([pscustomobject]@{
            ModuleId = $entry.ModuleId; Kind = 'noncanonical-duplicate'; SourcePath = $source
            FolderName = $entry.FolderName; HoldingPath = (Join-Path $rollbackModules $entry.FolderName)
            DurableBackupPath = $null; SourceDigest = $null; Moved = $false
        })
    }

    foreach ($relocation in $relocations) {
        $category = if ($relocation.Kind -ceq 'noncanonical-duplicate') { 'Duplicates' } else { 'Canonical' }
        $durableParent = Join-Path $durableOriginalModules $category
        New-Item -ItemType Directory -Path $durableParent -Force | Out-Null
        $durablePath = Join-Path $durableParent $relocation.FolderName
        $snapshot = Copy-DirectoryWithVerification -Source $relocation.SourcePath -Destination $durablePath
        $relocation.DurableBackupPath = $durablePath
        $relocation.SourceDigest = $snapshot.Digest
    }

    $installedSwaps = New-Object System.Collections.Generic.List[object]
    $launcherChanged = $false
    try {
        foreach ($relocation in $relocations) {
            if (Test-Path -LiteralPath $relocation.HoldingPath) { throw "Transaction holding path already exists: $($relocation.HoldingPath)" }
            Move-Item -LiteralPath $relocation.SourcePath -Destination $relocation.HoldingPath
            $relocation.Moved = $true
        }

        $swapNumber = 0
        foreach ($record in $suite.Records) {
            $target = Join-Path $modulesRoot $record.ModuleId
            if (-not (Test-PathInside -Candidate $target -Parent $modulesRoot) -or
                (Split-Path -Parent (Get-FullPathInput $target)) -ine (Get-FullPathInput $modulesRoot)) {
                throw "Refusing unsafe module target: $target"
            }
            if (Test-Path -LiteralPath $target) { throw "Module target remained occupied after the same-volume backup rename: $target" }
            $swap = [pscustomobject]@{ ModuleId = $record.ModuleId; TargetPath = $target; Moved = $false }
            $installedSwaps.Add($swap)
            Move-Item -LiteralPath (Join-Path $installStage $record.ModuleId) -Destination $target
            $swap.Moved = $true
            Test-ModulePayload -ModuleRoot $target -Record $record -RejectUnknownFiles | Out-Null
            $swapNumber++
            if ($swapNumber -eq 1 -and [Environment]::GetEnvironmentVariable('FRIEND_EDITION_TEST_FAIL_AFTER_FIRST_SWAP') -ceq '1') {
                throw 'Injected client-installer rollback test failure after the first same-volume module swap.'
            }
        }

        $installedInventory = Get-DirectModuleInventory -ModulesRoot $modulesRoot
        foreach ($record in $suite.Records) {
            $canonicalTarget = Get-FullPathInput (Join-Path $modulesRoot $record.ModuleId)
            if (-not $installedInventory.ById.ContainsKey($record.ModuleId)) {
                throw "Managed module '$($record.ModuleId)' is absent after installation."
            }
            $matches = $installedInventory.ById[$record.ModuleId]
            if ($matches.Count -ne 1 -or -not (Get-FullPathInput $matches[0].ModuleRoot).Equals($canonicalTarget, [System.StringComparison]::OrdinalIgnoreCase)) {
                throw "Managed module '$($record.ModuleId)' is not unique at its canonical installed path after cleanup."
            }
        }
        $installedDescriptors = Get-UniqueDescriptorMap -Inventory $installedInventory
        foreach ($id in $suite.ExactOrder) {
            if (-not $installedDescriptors.ContainsKey($id)) { throw "Installed module '$id' could not be resolved from SubModule.xml." }
        }
        $launcherChanged = $true
        Set-LauncherActivation -Path $launcherPath -ExactOrder $suite.ExactOrder -ActiveOrder $suite.ActiveOrder -Descriptors $installedDescriptors
        Test-LauncherActivation -Path $launcherPath -ExactOrder $suite.ExactOrder -ActiveOrder $suite.ActiveOrder

        $localRecoveryParent = Join-Path $resolvedGamePath 'FriendEditionBackups'
        New-Item -ItemType Directory -Path $localRecoveryParent -Force | Out-Null
        $localRecoveryPath = Join-Path $localRecoveryParent (Split-Path -Leaf $backupRunRoot)
        if (Test-Path -LiteralPath $localRecoveryPath) { throw "Local recovery path already exists: $localRecoveryPath" }
        Move-Item -LiteralPath $transactionRoot -Destination $localRecoveryPath
        foreach ($relocation in $relocations) {
            $relativeHolding = $relocation.HoldingPath.Substring($transactionRoot.Length).TrimStart('\', '/')
            $relocation.HoldingPath = Join-Path $localRecoveryPath $relativeHolding
        }
        $transactionRoot = $localRecoveryPath
        $transactionRootForError = $localRecoveryPath

        $recordPath = Join-Path $backupRunRoot 'INSTALL-RECORD.json'
        $installRecord = [ordered]@{
            schemaVersion = 1
            installedAtUtc = [DateTime]::UtcNow.ToString('o')
            suiteId = [string]$suite.Manifest.suiteId
            receiptSha256 = [string]$suite.Manifest.coop.managedSuiteManifest.receiptSha256
            gamePath = $resolvedGamePath
            launcherDataPath = $launcherPath
            activeModuleOrder = $suite.ActiveOrder
            stagedModuleIds = @($suite.Records | ForEach-Object { [string]$_.ModuleId })
            gameVolumeRecoveryPath = $localRecoveryPath
            originalModuleBackups = @($relocations | Sort-Object ModuleId, SourcePath | ForEach-Object {
                [ordered]@{
                    moduleId = $_.ModuleId; kind = $_.Kind; originalPath = $_.SourcePath
                    durableBackupPath = $_.DurableBackupPath; sourceDigest = $_.SourceDigest
                }
            })
            relocatedDuplicateModules = @($relocations | Where-Object { $_.Kind -ceq 'noncanonical-duplicate' } | ForEach-Object {
                [ordered]@{ moduleId = $_.ModuleId; originalPath = $_.SourcePath; durableBackupPath = $_.DurableBackupPath }
            })
        }
        [System.IO.File]::WriteAllText($recordPath, (($installRecord | ConvertTo-Json -Depth 10) + "`n"), (New-Object System.Text.UTF8Encoding($false)))
    }
    catch {
        $installError = $_
        $rollbackErrors = New-Object System.Collections.Generic.List[string]
        if ($launcherChanged -and (Test-Path -LiteralPath $launcherBackup -PathType Leaf)) {
            try {
                $restoreTemp = Join-Path (Split-Path -Parent $launcherPath) ('.LauncherData.friend-restore-' + [guid]::NewGuid().ToString('N') + '.tmp')
                Copy-Item -LiteralPath $launcherBackup -Destination $restoreTemp
                $failedLauncherBackup = Join-Path $backupRunRoot 'LauncherData.xml.failed-install.bak'
                [System.IO.File]::Replace($restoreTemp, $launcherPath, $failedLauncherBackup)
            }
            catch { $rollbackErrors.Add("Launcher restore failed: $($_.Exception.Message)") }
        }
        try { New-Item -ItemType Directory -Path $failedModules -Force | Out-Null }
        catch { $rollbackErrors.Add("Could not create failed-module recovery folder: $($_.Exception.Message)") }
        foreach ($swap in @($installedSwaps.ToArray() | Sort-Object ModuleId -Descending)) {
            if ($swap.Moved -and (Test-Path -LiteralPath $swap.TargetPath)) {
                try {
                    Move-Item -LiteralPath $swap.TargetPath -Destination (Join-Path $failedModules (Split-Path -Leaf $swap.TargetPath))
                }
                catch { $rollbackErrors.Add("Could not move failed new module '$($swap.TargetPath)' aside: $($_.Exception.Message)") }
            }
        }
        foreach ($relocation in @($relocations.ToArray() | Sort-Object SourcePath -Descending)) {
            if ($relocation.Moved -and (Test-Path -LiteralPath $relocation.HoldingPath)) {
                try {
                    if (Test-Path -LiteralPath $relocation.SourcePath) { throw "original path is occupied: $($relocation.SourcePath)" }
                    Move-Item -LiteralPath $relocation.HoldingPath -Destination $relocation.SourcePath
                    $relocation.Moved = $false
                }
                catch { $rollbackErrors.Add("Could not restore '$($relocation.SourcePath)': $($_.Exception.Message)") }
            }
        }
        if ($rollbackErrors.Count -gt 0) {
            throw "Setup failed: $($installError.Exception.Message). Automatic rollback also needs attention: $($rollbackErrors -join '; '). Recovery data remains at '$transactionRoot' and '$backupRunRoot'."
        }
        throw $installError
    }

    Write-Host ''
    Write-Host 'INSTALLATION PASSED' -ForegroundColor Green
    Write-Host "Every managed module was installed separately under: $modulesRoot"
    Write-Host "Only these modules were enabled: $($suite.ActiveOrder -join ' -> ')"
    Write-Host "Backup and install record: $backupRunRoot" -ForegroundColor Yellow
    Write-Host 'TaleWorlds BirthAndDeath and all guarded Workshop gameplay/framework modules remain disabled.'
    exit 0
}
catch {
    Write-Host ''
    Write-Host 'SETUP COULD NOT FINISH' -ForegroundColor Red
    Write-Host $_.Exception.Message -ForegroundColor Yellow
    if ([Environment]::GetEnvironmentVariable('FRIEND_EDITION_SETUP_DEBUG') -ceq '1') {
        Write-Host $_.ScriptStackTrace -ForegroundColor DarkGray
    }
    if (-not [string]::IsNullOrWhiteSpace([string]$transactionRootForError) -and (Test-Path -LiteralPath $transactionRootForError)) {
        Write-Host "Game-volume recovery data was retained at: $transactionRootForError" -ForegroundColor Yellow
    }
    if (-not [string]::IsNullOrWhiteSpace([string]$backupRunRootForError) -and (Test-Path -LiteralPath $backupRunRootForError)) {
        Write-Host "Durable backup data was retained at: $backupRunRootForError" -ForegroundColor Yellow
    }
    Write-Host ''
    Write-Host 'Correct the item above, then run Run-ClientSetup.cmd again.'
    exit 1
}
