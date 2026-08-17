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
    "DismembermentPlus.Main",
    "Fourberie.Main",
    "UnblockableThrust.UnblockableThrustSubmodule",
    "RebellionsAndDemographics.SubModule",
];
foreach (string classType in dedicatedWorkshopSubModules)
{
    if (!(bool)modulePolicy.Invoke(null, [classType])!)
        throw new InvalidOperationException($"Audited dedicated Workshop submodule is blocked: {classType}");
}
foreach (string? classType in new string?[] { "Unrelated.Mod.Entry", "SandBox.View.SandBoxViewSubModule", null })
{
    if ((bool)modulePolicy.Invoke(null, [classType])!)
        throw new InvalidOperationException($"Unaudited dedicated Workshop submodule is enabled: {classType ?? "<null>"}");
}

Console.WriteLine("PASS: startup hook preserves assembly ownership and enables only the five audited dedicated Workshop submodules");
