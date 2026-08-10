using System;
using System.Collections.Generic;

namespace GameInterface.Services.WorkshopMods.Frameworks;

internal enum FrameworkActivationState
{
    StagedInactive,
    ActiveExact,
}

internal sealed class FrameworkAssemblyExpectation
{
    internal FrameworkAssemblyExpectation(
        string assemblyName,
        string version,
        string sha256,
        bool requiredBeforeModuleLoad,
        bool optionalFramework)
    {
        AssemblyName = assemblyName;
        Version = System.Version.Parse(version);
        Sha256 = sha256;
        RequiredBeforeModuleLoad = requiredBeforeModuleLoad;
        OptionalFramework = optionalFramework;
    }

    internal string AssemblyName { get; }
    internal Version Version { get; }
    internal string Sha256 { get; }
    internal bool RequiredBeforeModuleLoad { get; }
    internal bool OptionalFramework { get; }
}

internal sealed class FrameworkMethodExpectation
{
    internal FrameworkMethodExpectation(
        string assemblyName,
        string typeName,
        string methodName,
        string returnTypeName,
        params string[] parameterTypeNames)
    {
        AssemblyName = assemblyName;
        TypeName = typeName;
        MethodName = methodName;
        ReturnTypeName = returnTypeName;
        ParameterTypeNames = parameterTypeNames ?? Array.Empty<string>();
    }

    internal string AssemblyName { get; }
    internal string TypeName { get; }
    internal string MethodName { get; }
    internal string ReturnTypeName { get; }
    internal IReadOnlyList<string> ParameterTypeNames { get; }

    internal string Identity =>
        $"{TypeName}.{MethodName}({string.Join(",", ParameterTypeNames)}):{ReturnTypeName}";
}

/// <summary>
/// Exact, source-audited executable surface for the Friend Edition dependency-framework bundle.
/// The distributable activates ButterLib/UIExtenderEx/MCM on every peer — the group runs the
/// full modded experience — so the boundary's job is byte-exact admission: every framework
/// assembly must match this manifest before hardened Coop lets it run. Partial cohorts are
/// still refused; the audited binaries then run unmodified.
/// </summary>
internal static class FrameworkCompatibilityManifest
{
    internal const string AdapterHarmonyId = "Bannerlord.Coop.Workshop.FrameworkBoundary";
    internal const string PolicyRevision = "framework-boundary-v2-active";
    internal const bool OptionalFrameworkActivationAllowed = true;

    internal static IReadOnlyList<FrameworkAssemblyExpectation> Assemblies { get; } =
        new[]
        {
            // Bannerlord.Harmony v2.4.2.248 is the one active canonical Harmony provider.
            new FrameworkAssemblyExpectation(
                "0Harmony", "2.4.2.0",
                "2edda13a18954b79795bac0d7e0e8fdf0bfa05b96552d6056d90f446cd0a2ab2",
                requiredBeforeModuleLoad: true, optionalFramework: false),
            new FrameworkAssemblyExpectation(
                "Bannerlord.Harmony", "2.4.2.248",
                "4bc2f22e63cfcd677b9de2a9834b103eaacfd8ec0ea4735f4edf66342a6fd01b",
                requiredBeforeModuleLoad: true, optionalFramework: false),

            new FrameworkAssemblyExpectation(
                "Bannerlord.ButterLib", "2.11.1.0",
                "d820692e0c02377f53804e7ec14bb35bd524644613dcdd013c5873729b262172",
                requiredBeforeModuleLoad: true, optionalFramework: true),
            new FrameworkAssemblyExpectation(
                "Bannerlord.UIExtenderEx", "2.13.3.0",
                "4f2782840e51391d66b7d701962cb6202969d34c0949b8ec1591964f9c479f99",
                requiredBeforeModuleLoad: true, optionalFramework: true),
            new FrameworkAssemblyExpectation(
                "MCMv5", "5.12.2.0",
                "7aef3e20ce73eb4409a670f9c2778e2894ed5897a0433bdbe9475e13cac67638",
                requiredBeforeModuleLoad: true, optionalFramework: true),
            new FrameworkAssemblyExpectation(
                "Bannerlord.MBOptionScreen", "1.0.1.50",
                "a8fec162853602402b34a942c690e5890716a6a080493a0daae174b4a185884f",
                requiredBeforeModuleLoad: true, optionalFramework: true),

            // Loaders select these exact v1.4.7 implementations before the end of the module-load pass.
            new FrameworkAssemblyExpectation(
                "Bannerlord.ButterLib.Implementation.1.4.7", "2.11.1.0",
                "52e2b6e0314fe2840703d39485a28dedeb7066b9a106ba26f14908e3b26b9209",
                requiredBeforeModuleLoad: false, optionalFramework: true),
            new FrameworkAssemblyExpectation(
                "MCM.UI.Adapter.MCMv5", "5.12.2.0",
                "33ea4ec2e6046f7b9e66179666671eaf1bbb0d979cbee8c97a4c84ee2d3ca6a9",
                requiredBeforeModuleLoad: false, optionalFramework: true),
            new FrameworkAssemblyExpectation(
                "Bannerlord.MBOptionScreen.v1.4.7", "5.12.2.0",
                "f65e954d9e034b2161fe105dad430d4dd7e14c68f05239be057f01e9349056b7",
                requiredBeforeModuleLoad: false, optionalFramework: true),
        };

    internal static IReadOnlyList<FrameworkMethodExpectation> GuardedMethods { get; } =
        new[]
        {
            // MCM's implementation load performs filesystem migrations; deny it before its load pass.
            Method("MCMv5", "MCM.Internal.MCMImplementationSubModule", "OnSubModuleLoad", "System.Void"),

            // ButterLib's base and selected implementation lifecycle hooks. Its OnSubModuleLoad
            // already ran before Coop can be constructed and is explicitly unwound by the bootstrap.
            Method("Bannerlord.ButterLib", "Bannerlord.ButterLib.ButterLibSubModule",
                "OnBeforeInitialModuleScreenSetAsRoot", "System.Void"),
            Method("Bannerlord.ButterLib", "Bannerlord.ButterLib.ButterLibSubModule",
                "OnApplicationTick", "System.Void", "System.Single"),
            Method("Bannerlord.ButterLib", "Bannerlord.ButterLib.ButterLibSubModule", "OnGameStart", "System.Void",
                "TaleWorlds.Core.Game", "TaleWorlds.Core.IGameStarter"),
            Method("Bannerlord.ButterLib", "Bannerlord.ButterLib.ButterLibSubModule", "OnGameEnd", "System.Void",
                "TaleWorlds.Core.Game"),
            Method("Bannerlord.ButterLib.Implementation.1.4.7",
                "Bannerlord.ButterLib.Implementation.SubModule", "OnBeforeInitialModuleScreenSetAsRoot", "System.Void"),
            Method("Bannerlord.ButterLib.Implementation.1.4.7",
                "Bannerlord.ButterLib.Implementation.SubModule", "OnGameStart", "System.Void",
                "TaleWorlds.Core.Game", "TaleWorlds.Core.IGameStarter"),
            Method("Bannerlord.ButterLib.Implementation.1.4.7",
                "Bannerlord.ButterLib.Implementation.SubModule", "OnGameEnd", "System.Void",
                "TaleWorlds.Core.Game"),

            // UIExtender remains a binary dependency only; no module can add dynamic UI transpilers.
            Method("Bannerlord.UIExtenderEx", "Bannerlord.UIExtenderEx.UIExtender", "Enable", "System.Void"),
            Method("Bannerlord.UIExtenderEx", "Bannerlord.UIExtenderEx.UIExtender", "Enable", "System.Void", "System.Type"),

            // No local campaign/mission settings providers or per-save setting serialization.
            Method("MCMv5", "MCM.MCMSubModule", "OnBeforeInitialModuleScreenSetAsRoot", "System.Void"),
            Method("MCMv5", "MCM.MCMSubModule", "OnCampaignStart", "System.Void",
                "TaleWorlds.Core.Game", "System.Object"),
            Method("MCMv5", "MCM.MCMSubModule", "OnMissionBehaviorInitialize", "System.Void",
                "TaleWorlds.MountAndBlade.Mission"),
            Method("MCMv5", "MCM.MCMSubModule", "OnGameStart", "System.Void",
                "TaleWorlds.Core.Game", "TaleWorlds.Core.IGameStarter"),
            Method("MCMv5", "MCM.MCMSubModule", "OnGameEnd", "System.Void",
                "TaleWorlds.Core.Game"),
            Method("MCMv5", "MCM.Internal.MCMImplementationSubModule", "OnGameStart", "System.Void",
                "TaleWorlds.Core.Game", "TaleWorlds.Core.IGameStarter"),
            Method("MCMv5", "MCM.Internal.MCMImplementationSubModule", "OnNewGameCreated", "System.Void",
                "TaleWorlds.Core.Game", "System.Object"),
            Method("MCMv5", "MCM.Internal.MCMImplementationSubModule", "OnGameLoaded", "System.Void",
                "TaleWorlds.Core.Game", "System.Object"),
            Method("MCMv5", "MCM.Internal.MCMImplementationSubModule", "OnGameEnd", "System.Void",
                "TaleWorlds.Core.Game"),
            Method("MCMv5", "MCM.Internal.GameFeatures.PerSaveCampaignBehavior", "RegisterEvents", "System.Void"),
            Method("MCMv5", "MCM.Internal.GameFeatures.PerSaveCampaignBehavior", "SyncData", "System.Void",
                "TaleWorlds.CampaignSystem.IDataStore"),
            Method("MCMv5", "MCM.Internal.GameFeatures.PerSaveCampaignBehavior", "SaveSettings", "System.Boolean",
                "MCM.Abstractions.Base.PerSave.PerSaveSettings"),
            Method("MCMv5", "MCM.Internal.GameFeatures.PerSaveCampaignBehavior", "LoadSettings", "System.Void",
                "MCM.Abstractions.Base.PerSave.PerSaveSettings"),
            Method("MCMv5", "MCM.Implementation.DefaultSettingsProvider", "SaveSettings", "System.Void",
                "MCM.Abstractions.Base.BaseSettings"),
            Method("MCMv5", "MCM.Implementation.DefaultSettingsProvider", "ResetSettings", "System.Void",
                "MCM.Abstractions.Base.BaseSettings"),
            Method("MCMv5", "MCM.Implementation.DefaultSettingsProvider", "OverrideSettings", "System.Void",
                "MCM.Abstractions.Base.BaseSettings"),

            // MCM's UI never initializes in Coop; the lower-level mutators are guarded as defense in depth.
            Method("Bannerlord.MBOptionScreen.v1.4.7", "MCM.UI.MCMUISubModule",
                "OnBeforeInitialModuleScreenSetAsRoot", "System.Void"),
            Method("Bannerlord.MBOptionScreen.v1.4.7", "MCM.UI.GUI.ViewModels.SettingsVM",
                "ChangePreset", "System.Void", "System.String"),
            Method("Bannerlord.MBOptionScreen.v1.4.7", "MCM.UI.GUI.ViewModels.SettingsVM",
                "ChangePresetValue", "System.Void", "System.String", "System.String"),
            Method("Bannerlord.MBOptionScreen.v1.4.7", "MCM.UI.GUI.ViewModels.SettingsVM",
                "ResetSettings", "System.Void"),
            Method("Bannerlord.MBOptionScreen.v1.4.7", "MCM.UI.GUI.ViewModels.SettingsVM",
                "SaveSettings", "System.Void"),
            Method("Bannerlord.MBOptionScreen.v1.4.7", "MCM.UI.GUI.ViewModels.SettingsVM",
                "ResetSettingsValue", "System.Void", "System.String"),
            Method("Bannerlord.MBOptionScreen.v1.4.7", "MCM.UI.GUI.ViewModels.SettingsPropertyVM",
                "set_BoolValue", "System.Void", "System.Boolean"),
            Method("Bannerlord.MBOptionScreen.v1.4.7", "MCM.UI.GUI.ViewModels.SettingsPropertyVM",
                "set_FloatValue", "System.Void", "System.Single"),
            Method("Bannerlord.MBOptionScreen.v1.4.7", "MCM.UI.GUI.ViewModels.SettingsPropertyVM",
                "set_IntValue", "System.Void", "System.Int32"),
            Method("Bannerlord.MBOptionScreen.v1.4.7", "MCM.UI.GUI.ViewModels.SettingsPropertyVM",
                "set_StringValue", "System.Void", "System.String"),
            Method("Bannerlord.MBOptionScreen.v1.4.7", "MCM.UI.GUI.ViewModels.SettingsPropertyVM",
                "ResetValueToDefaultOnReleasedEvent", "System.Void"),
            Method("Bannerlord.MBOptionScreen.v1.4.7", "MCM.UI.GUI.ViewModels.SettingsPropertyVM",
                "OnValueClick", "System.Void"),
        };

    internal static IReadOnlyList<string> ButterSubsystemTypes { get; } =
        new[]
        {
            "Bannerlord.ButterLib.DelayedSubModule.DelayedSubModuleSubSystem",
            "Bannerlord.ButterLib.ExceptionHandler.ExceptionHandlerSubSystem",
            "Bannerlord.ButterLib.CrashUploader.CrashUploaderSubSystem",
            "Bannerlord.ButterLib.SubModuleWrappers2.SubModuleWrappers2SubSystem",
            "Bannerlord.ButterLib.Implementation.DistanceMatrix.DistanceMatrixSubSystem",
            "Bannerlord.ButterLib.Implementation.HotKeys.HotKeySubSystem",
            "Bannerlord.ButterLib.Implementation.MBSubModuleBaseExtended.MBSubModuleBaseExSubSystem",
            "Bannerlord.ButterLib.Implementation.ObjectSystem.ObjectSystemSubSystem",
            "Bannerlord.ButterLib.Implementation.SaveSystem.SaveSystemSubSystem",
        };

    private static FrameworkMethodExpectation Method(
        string assemblyName,
        string typeName,
        string methodName,
        string returnTypeName,
        params string[] parameterTypeNames) =>
        new(assemblyName, typeName, methodName, returnTypeName, parameterTypeNames);
}
