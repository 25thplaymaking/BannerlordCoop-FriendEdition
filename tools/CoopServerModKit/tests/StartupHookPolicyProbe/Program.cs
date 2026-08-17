using System.Reflection;

if (args.Length != 1)
    throw new ArgumentException("Usage: StartupHookPolicyProbe <coophook.dll>");

Assembly hookAssembly = Assembly.LoadFrom(Path.GetFullPath(args[0]));
Type hookType = hookAssembly.GetType("StartupHook", throwOnError: true)!;
MethodInfo policy = hookType.GetMethod(
    "ShouldProbeModuleBins",
    BindingFlags.NonPublic | BindingFlags.Static)
    ?? throw new MissingMethodException(hookType.FullName, "ShouldProbeModuleBins");
MethodInfo modulePolicy = hookType.GetMethod(
    "ShouldForceDedicatedWorkshopSubModule",
    BindingFlags.NonPublic | BindingFlags.Static)
    ?? throw new MissingMethodException(hookType.FullName, "ShouldForceDedicatedWorkshopSubModule");
MethodInfo saveDefinitionPolicy = hookType.GetMethod(
    "ShouldRunContainerDefinitionOriginal",
    BindingFlags.NonPublic | BindingFlags.Static)
    ?? throw new MissingMethodException(hookType.FullName, "ShouldRunContainerDefinitionOriginal");
MethodInfo presentationPolicy = hookType.GetMethod(
    "ShouldBlockDedicatedPresentationSubModule",
    BindingFlags.NonPublic | BindingFlags.Static)
    ?? throw new MissingMethodException(hookType.FullName, "ShouldBlockDedicatedPresentationSubModule");

string[] requiredClientOnlyAssemblies =
[
    "SandBox.GauntletUI",
    "SandBox.View",
    "SandBox.ViewModelCollection",
    "StoryMode",
    "TaleWorlds.CampaignSystem.ViewModelCollection",
    "TaleWorlds.Core.ViewModelCollection",
    "TaleWorlds.Engine.GauntletUI",
    "TaleWorlds.GauntletUI",
    "TaleWorlds.GauntletUI.Data",
    "TaleWorlds.GauntletUI.ExtraWidgets",
    "TaleWorlds.GauntletUI.PrefabSystem",
    "TaleWorlds.MountAndBlade.GauntletUI",
    "TaleWorlds.MountAndBlade.GauntletUI.Widgets",
    "TaleWorlds.MountAndBlade.View",
    "TaleWorlds.MountAndBlade.ViewModelCollection",
    "TaleWorlds.TwoDimension",
];

string[] engineOwnedAssemblies =
[
    "SandBox",
    "StoryMode.View",
    "TaleWorlds.CampaignSystem",
    "TaleWorlds.Core",
    "TaleWorlds.Engine",
    "TaleWorlds.MountAndBlade",
    "Coop",
    "GameInterface",
    "Missions",
    "Common",
];

foreach (string assemblyName in requiredClientOnlyAssemblies)
{
    if (!(bool)policy.Invoke(null, [assemblyName])!)
        throw new InvalidOperationException($"Required client-only support assembly is blocked: {assemblyName}");
}

foreach (string assemblyName in engineOwnedAssemblies)
{
    if ((bool)policy.Invoke(null, [assemblyName])!)
        throw new InvalidOperationException($"Canonical engine/Coop assembly is redirectable: {assemblyName}");
}

string[] dedicatedWorkshopSubModules =
[
    "ImprovedGarrisons.Main",
    "Fourberie.Main",
    "UnblockableThrust.UnblockableThrustSubmodule",
    "RebellionsAndDemographics.SubModule",
];
foreach (string classType in dedicatedWorkshopSubModules)
{
    if (!(bool)modulePolicy.Invoke(null, [classType])!)
        throw new InvalidOperationException($"Audited dedicated Workshop submodule is blocked: {classType}");
}
foreach (string? classType in new string?[]
         {
             "DismembermentPlus.Main",
             "Unrelated.Mod.Entry",
             "SandBox.View.SandBoxViewSubModule",
             null,
         })
{
    if ((bool)modulePolicy.Invoke(null, [classType])!)
        throw new InvalidOperationException($"Unaudited dedicated Workshop submodule is enabled: {classType ?? "<null>"}");
}

string[] dedicatedPresentationSubModules =
[
    "Bannerlord.UIExtenderEx.SubModule",
    "MCM.MCMSubModule",
    "MCM.Internal.MCMImplementationSubModule",
    "Bannerlord.ModuleLoader.Bannerlord_MBOptionScreen",
];
foreach (string classType in dedicatedPresentationSubModules)
{
    if (!(bool)presentationPolicy.Invoke(null, [classType])!)
        throw new InvalidOperationException($"Audited client-presentation submodule is not blocked: {classType}");
}
foreach (string? classType in new string?[] { "ImprovedGarrisons.Main", "Unrelated.Mod.Entry", null })
{
    if ((bool)presentationPolicy.Invoke(null, [classType])!)
        throw new InvalidOperationException($"Gameplay or unknown submodule is presentation-blocked: {classType ?? "<null>"}");
}

if (!(bool)saveDefinitionPolicy.Invoke(null, [false])!)
    throw new InvalidOperationException("The first save-container definition would be suppressed.");
if ((bool)saveDefinitionPolicy.Invoke(null, [true])!)
    throw new InvalidOperationException("A duplicate save-container definition would reach the v1.4.8 fatal assert.");

Console.WriteLine("PASS: startup hook preserves assembly ownership, enables four gameplay submodules, blocks four presentation submodules, and suppresses only duplicate save-container registrations");
