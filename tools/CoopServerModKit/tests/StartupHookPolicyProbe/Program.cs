using System.Reflection;

if (args.Length != 1)
    throw new ArgumentException("Usage: StartupHookPolicyProbe <coophook.dll>");

Assembly hookAssembly = Assembly.LoadFrom(Path.GetFullPath(args[0]));
Type hookType = hookAssembly.GetType("StartupHook", throwOnError: true)!;
MethodInfo policy = hookType.GetMethod(
    "ShouldProbeModuleBins",
    BindingFlags.NonPublic | BindingFlags.Static)
    ?? throw new MissingMethodException(hookType.FullName, "ShouldProbeModuleBins");

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

Console.WriteLine("PASS: startup hook probes the exact client-only support closure and preserves canonical engine ownership");
