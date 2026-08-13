#Requires -Version 5.1

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw "ASSERTION FAILED: $Message" }
}

$kitRoot = Split-Path -Parent $PSScriptRoot
$syncScript = Join-Path $kitRoot 'Sync-ServerModuleBins.ps1'
$verifyScript = Join-Path $kitRoot 'Verify-ServerModuleBins.py'
$testRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('coop-server-bin-tests-' + [guid]::NewGuid().ToString('N'))

try {
    $moduleRoot = Join-Path $testRoot 'Modules\Example.Mod'
    $clientBin = Join-Path $moduleRoot 'bin\Win64_Shipping_Client'
    New-Item -ItemType Directory -Path $clientBin -Force | Out-Null
    [System.IO.File]::WriteAllText((Join-Path $moduleRoot 'SubModule.xml'), @'
<Module><SubModules><SubModule>
  <DLLName value="Example.Mod.dll" />
  <SubModuleClassType value="Example.Mod.Entry" />
</SubModule></SubModules></Module>
'@)
    [System.IO.File]::WriteAllText((Join-Path $clientBin 'Example.Mod.dll'), 'runtime')
    [System.IO.File]::WriteAllText((Join-Path $clientBin 'Dependency.dll'), 'dependency')
    [System.IO.File]::WriteAllText((Join-Path $clientBin 'Example.Mod.pdb'), 'symbols')

    $presentationRoot = Join-Path $testRoot 'Modules\Presentation.Mod'
    $presentationClientBin = Join-Path $presentationRoot 'bin\Win64_Shipping_Client'
    New-Item -ItemType Directory -Path $presentationClientBin -Force | Out-Null
    [System.IO.File]::WriteAllText((Join-Path $presentationRoot 'SubModule.xml'), @'
<Module><SubModules><SubModule>
  <DLLName value="Presentation.Mod.dll" />
  <SubModuleClassType value="Presentation.Mod.Entry" />
</SubModule></SubModules></Module>
'@)
    [System.IO.File]::WriteAllText((Join-Path $presentationClientBin 'Presentation.Mod.dll'), 'presentation')

    $roleManifest = Join-Path $testRoot 'server-module-roles.json'
    [System.IO.File]::WriteAllText($roleManifest, (@{
        serverRuntimeModules = @('Example.Mod')
        clientPresentationModules = @('Presentation.Mod')
    } | ConvertTo-Json -Depth 4))

    & python $verifyScript --modules-root (Join-Path $testRoot 'Modules') --role-manifest $roleManifest 2>$null
    Assert-True ($LASTEXITCODE -ne 0) 'verification must reject an advertised module without a server bin'

    & $syncScript -ModulesRoot (Join-Path $testRoot 'Modules') -ModuleIds @('Example.Mod')
    Assert-True (Test-Path -LiteralPath (Join-Path $moduleRoot 'bin\Win64_Shipping_Server\Example.Mod.dll')) 'submodule DLL was not mirrored'
    Assert-True (Test-Path -LiteralPath (Join-Path $moduleRoot 'bin\Win64_Shipping_Server\Dependency.dll')) 'runtime dependency was not mirrored'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $moduleRoot 'bin\Win64_Shipping_Server\Example.Mod.pdb'))) 'PDB should not be copied into the runtime overlay'

    & python $verifyScript --modules-root (Join-Path $testRoot 'Modules') --role-manifest $roleManifest
    Assert-True ($LASTEXITCODE -eq 0) 'verification rejected an exact mirrored runtime'

    $presentationServerBin = Join-Path $presentationRoot 'bin\Win64_Shipping_Server'
    New-Item -ItemType Directory -Path $presentationServerBin -Force | Out-Null
    [System.IO.File]::WriteAllText((Join-Path $presentationServerBin 'Presentation.Mod.dll'), 'presentation')
    & python $verifyScript --modules-root (Join-Path $testRoot 'Modules') --role-manifest $roleManifest 2>$null
    Assert-True ($LASTEXITCODE -ne 0) 'verification must reject a client-presentation submodule in the server bin'
    Remove-Item -LiteralPath $presentationServerBin -Recurse -Force

    $supportManifest = Join-Path $testRoot 'server-support.json'
    $supportRelativePath = 'Coop/bin/Win64_Shipping_Server/ClientOnly.Support.dll'
    $supportBytes = [System.Text.Encoding]::UTF8.GetBytes('support-runtime')
    $supportHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($supportBytes)).ToLowerInvariant()
    [System.IO.File]::WriteAllText($supportManifest, (@{
        assemblies = @(@{ relativePath = $supportRelativePath; sha256 = $supportHash })
    } | ConvertTo-Json -Depth 4))

    & python $verifyScript --modules-root (Join-Path $testRoot 'Modules') --role-manifest $roleManifest --support-manifest $supportManifest 2>$null
    Assert-True ($LASTEXITCODE -ne 0) 'verification must reject a missing dedicated support assembly'

    $supportPath = Join-Path (Join-Path $testRoot 'Modules') $supportRelativePath
    New-Item -ItemType Directory -Path (Split-Path -Parent $supportPath) -Force | Out-Null
    [System.IO.File]::WriteAllBytes($supportPath, $supportBytes)
    & python $verifyScript --modules-root (Join-Path $testRoot 'Modules') --role-manifest $roleManifest --support-manifest $supportManifest
    Assert-True ($LASTEXITCODE -eq 0) 'verification rejected the exact dedicated support closure'

    [System.IO.File]::WriteAllText($supportPath, 'support-drift')
    & python $verifyScript --modules-root (Join-Path $testRoot 'Modules') --role-manifest $roleManifest --support-manifest $supportManifest 2>$null
    Assert-True ($LASTEXITCODE -ne 0) 'verification must reject dedicated support assembly drift'

    [System.IO.File]::WriteAllText((Join-Path $moduleRoot 'bin\Win64_Shipping_Server\Dependency.dll'), 'drift')
    & python $verifyScript --modules-root (Join-Path $testRoot 'Modules') --role-manifest $roleManifest 2>$null
    Assert-True ($LASTEXITCODE -ne 0) 'verification must reject server/client runtime drift'

    Write-Host 'PASS: server module roles are explicit, exact, and fail closed on absence or drift'
}
finally {
    if (Test-Path -LiteralPath $testRoot) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}

exit 0
