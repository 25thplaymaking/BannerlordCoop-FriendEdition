using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;

namespace GameInterface.Services.WorkshopMods.PlayerSettlement;

internal enum PlayerSettlementPatchKind
{
    BootstrapPersistenceBehavior,
    GuardedObjectRegistration,
    ServerPersistence,
    ServerLifecycle,
    RoleLifecycle,
    ClientPresentation,
    BlockedPlayerAction,
    BlockedSharedMutation,
    BlockedSaveMutation,
}

internal sealed class PlayerSettlementMethodSpec
{
    public PlayerSettlementMethodSpec(
        string typeName,
        string methodName,
        bool isStatic,
        string returnTypeName,
        PlayerSettlementPatchKind kind,
        params string[] parameterTypeNames)
    {
        TypeName = typeName;
        MethodName = methodName;
        IsStatic = isStatic;
        ReturnTypeName = returnTypeName;
        Kind = kind;
        ParameterTypeNames = parameterTypeNames ?? Array.Empty<string>();
    }

    public string TypeName { get; }
    public string MethodName { get; }
    public bool IsStatic { get; }
    public string ReturnTypeName { get; }
    public PlayerSettlementPatchKind Kind { get; }
    public string[] ParameterTypeNames { get; }

    public string Key =>
        $"{TypeName}::{MethodName}({string.Join(",", ParameterTypeNames)})->{ReturnTypeName}";
}

/// <summary>
/// Exact compatibility boundary for the creator-approved Player Settlement 7.5.0 Workshop
/// package. This adapter is deliberately reflection-only: Coop can still run without the
/// optional module, while a loaded but different build stops session initialization before its
/// single-player campaign code can execute unguarded.
/// </summary>
internal static class PlayerSettlementCompatibilityManifest
{
    internal const string AdapterVersion = "2";
    internal const string ModuleVersion = "v7.5.0";
    internal const string AssemblyName = "PlayerSettlement";
    internal const string FixesAssemblyName = "PlayerSettlementFixes";
    internal const string AssemblyVersion = "7.5.0.0";
    internal const string FixesAssemblyVersion = "1.0.0.0";

    // Both binaries are shipped in the same approved Workshop item. Bannerlord loads the Win64
    // build on Steam/Windows and the Gaming.Desktop build on the corresponding platform target.
    internal static readonly IReadOnlyCollection<string> SupportedAssemblySha256 = new HashSet<string>(
        new[]
        {
            "74f9ab2ebc82bdc755886c6ad0802500c2df89015d7c65cd5018c543dbf18119",
            "7862692efcac86a08d24f09670d74ee9bd72c549a9849652a7c08d491c7a7314",
        },
        StringComparer.OrdinalIgnoreCase);

    internal static readonly IReadOnlyCollection<string> SupportedFixesSha256 = new HashSet<string>(
        new[]
        {
            "a74dd0ed13470240074dfe2a52295a33cb6235a1b6a0ae739fcd0b0d8dd8d1ac",
        },
        StringComparer.OrdinalIgnoreCase);

    internal static readonly IReadOnlyList<PlayerSettlementMethodSpec> Methods =
        new List<PlayerSettlementMethodSpec>
        {
            Spec("BannerlordPlayerSettlement.Main", "OnSubModuleLoad", false, "System.Void",
                PlayerSettlementPatchKind.BlockedSharedMutation),
            Spec("BannerlordPlayerSettlement.Main", "OnBeforeInitialModuleScreenSetAsRoot", false,
                "System.Void", PlayerSettlementPatchKind.ClientPresentation),
            // Add the behavior on both roles. Its event handlers are independently role-routed
            // below: clients own placement/menu presentation while the host owns persistence,
            // construction completion, and campaign mutation.
            Spec("BannerlordPlayerSettlement.Main", "AddBehaviors", false, "System.Void",
                PlayerSettlementPatchKind.BootstrapPersistenceBehavior,
                "TaleWorlds.CampaignSystem.CampaignGameStarter"),
            Spec("BannerlordPlayerSettlement.Main", "RegisterSubModuleObjects", false, "System.Void",
                PlayerSettlementPatchKind.GuardedObjectRegistration, "System.Boolean"),
            Spec("BannerlordPlayerSettlement.Main", "UpdateBlacklist", false, "System.Void",
                PlayerSettlementPatchKind.BlockedSaveMutation, "System.String[]"),

            Spec("BannerlordPlayerSettlement.Behaviours.PlayerSettlementBehaviour", "RegisterEvents", false,
                "System.Void", PlayerSettlementPatchKind.RoleLifecycle),
            Spec("BannerlordPlayerSettlement.Behaviours.PlayerSettlementBehaviour", "SyncData", false,
                "System.Void", PlayerSettlementPatchKind.ServerPersistence,
                "TaleWorlds.CampaignSystem.IDataStore"),
            Spec("BannerlordPlayerSettlement.Behaviours.PlayerSettlementBehaviour", "LoadEarlySync", false,
                "System.Void", PlayerSettlementPatchKind.ServerPersistence,
                "TaleWorlds.CampaignSystem.IDataStore"),
            Spec("BannerlordPlayerSettlement.Behaviours.PlayerSettlementBehaviour", "OnLoad", false,
                "System.Void", PlayerSettlementPatchKind.RoleLifecycle),
            Spec("BannerlordPlayerSettlement.Behaviours.PlayerSettlementBehaviour", "DailyTick", false,
                "System.Void", PlayerSettlementPatchKind.ServerLifecycle),
            Spec("BannerlordPlayerSettlement.Behaviours.PlayerSettlementBehaviour", "Tick", false,
                "System.Void", PlayerSettlementPatchKind.ClientPresentation, "System.Single"),
            Spec("BannerlordPlayerSettlement.Behaviours.PlayerSettlementBehaviour", "OnBeforeTick", false,
                "System.Void", PlayerSettlementPatchKind.ClientPresentation,
                "SandBox.View.Map.MapCameraView+InputInformation&"),
            Spec("BannerlordPlayerSettlement.Behaviours.PlayerSettlementBehaviour", "SetupGameMenus", false,
                "System.Void", PlayerSettlementPatchKind.ClientPresentation,
                "TaleWorlds.CampaignSystem.CampaignGameStarter"),
            Spec("BannerlordPlayerSettlement.Behaviours.PlayerSettlementBehaviour", "StartPortPlacement", false,
                "System.Void", PlayerSettlementPatchKind.ClientPresentation),
            Spec("BannerlordPlayerSettlement.Behaviours.PlayerSettlementBehaviour", "StartGatePlacement", false,
                "System.Void", PlayerSettlementPatchKind.ClientPresentation),
            Spec("BannerlordPlayerSettlement.Behaviours.PlayerSettlementBehaviour", "ApplyNow", false,
                "System.Void", PlayerSettlementPatchKind.ClientPresentation),
            Spec("BannerlordPlayerSettlement.Behaviours.PlayerSettlementBehaviour", "Reset", false,
                "System.Void", PlayerSettlementPatchKind.ClientPresentation),
            Spec("BannerlordPlayerSettlement.Behaviours.PlayerSettlementBehaviour", "RefreshVisualSelection", false,
                "System.Void", PlayerSettlementPatchKind.ClientPresentation),
            Spec("BannerlordPlayerSettlement.Behaviours.PlayerSettlementBehaviour", "NotifyComplete", false,
                "System.Void", PlayerSettlementPatchKind.ServerLifecycle,
                "BannerlordPlayerSettlement.Saves.ISettlementItem"),
            Spec("BannerlordPlayerSettlement.Behaviours.PlayerSettlementBehaviour", "Overwrite", false,
                "System.Void", PlayerSettlementPatchKind.BlockedPlayerAction,
                "TaleWorlds.CampaignSystem.Settlements.Settlement"),
            Spec("BannerlordPlayerSettlement.Behaviours.PlayerSettlementBehaviour", "Rebuild", false,
                "System.Void", PlayerSettlementPatchKind.BlockedPlayerAction,
                "BannerlordPlayerSettlement.Saves.PlayerSettlementItem"),
            Spec("BannerlordPlayerSettlement.Behaviours.PlayerSettlementBehaviour", "BuildCastle", false,
                "System.Void", PlayerSettlementPatchKind.BlockedPlayerAction),
            Spec("BannerlordPlayerSettlement.Behaviours.PlayerSettlementBehaviour", "BuildVillage", false,
                "System.Void", PlayerSettlementPatchKind.BlockedPlayerAction),
            Spec("BannerlordPlayerSettlement.Behaviours.PlayerSettlementBehaviour", "BuildVillageFor", false,
                "System.Void", PlayerSettlementPatchKind.BlockedPlayerAction,
                "TaleWorlds.CampaignSystem.Settlements.Settlement"),
            Spec("BannerlordPlayerSettlement.Behaviours.PlayerSettlementBehaviour", "BuildTown", false,
                "System.Void", PlayerSettlementPatchKind.BlockedPlayerAction),
            Spec("BannerlordPlayerSettlement.Behaviours.PlayerSettlementBehaviour", "CalculateVillageOwner", false,
                "TaleWorlds.CampaignSystem.Settlements.Settlement",
                PlayerSettlementPatchKind.BlockedPlayerAction),

            Spec("BannerlordPlayerSettlement.UI.Viewmodels.PlayerSettlementBuildVM",
                "ExecuteCreatePlayerSettlement", false, "System.Void",
                PlayerSettlementPatchKind.BlockedPlayerAction),

            Spec("BannerlordPlayerSettlement.SaveHandler", "SaveLoad", true, "System.Void",
                PlayerSettlementPatchKind.BlockedSaveMutation,
                "BannerlordPlayerSettlement.SaveHandler+SaveMechanism",
                "System.Action`2<BannerlordPlayerSettlement.SaveHandler+SaveMechanism,System.String>"),
            Spec("BannerlordPlayerSettlement.SaveHandler", "SaveOnly", true, "System.Void",
                PlayerSettlementPatchKind.BlockedSaveMutation, "System.Boolean"),
            Spec("BannerlordPlayerSettlement.SaveHandler", "SaveAndLoad", false, "System.Void",
                PlayerSettlementPatchKind.BlockedSaveMutation,
                "BannerlordPlayerSettlement.SaveHandler+SaveMechanism",
                "System.Action`2<BannerlordPlayerSettlement.SaveHandler+SaveMechanism,System.String>"),
            Spec("BannerlordPlayerSettlement.SaveHandler", "Save", false, "System.Void",
                PlayerSettlementPatchKind.BlockedSaveMutation, "System.Boolean"),
            Spec("BannerlordPlayerSettlement.SaveHandler", "StartGame", false, "System.Void",
                PlayerSettlementPatchKind.BlockedSaveMutation,
                "TaleWorlds.SaveSystem.Load.LoadResult"),

            Spec("BannerlordPlayerSettlement.Saves.PlayerSettlementInfo", "set_Instance", true,
                "System.Void", PlayerSettlementPatchKind.BlockedSharedMutation,
                "BannerlordPlayerSettlement.Saves.PlayerSettlementInfo"),
            Spec("BannerlordPlayerSettlement.Saves.PlayerSettlementInfo", "OnLoad", false,
                "System.Void", PlayerSettlementPatchKind.BlockedSharedMutation),
        };

    // The companion fixes DLL has already run once before Coop starts. These exact re-entry points
    // are denied after its installed Harmony surface is purged, preventing a later callback or
    // compatibility hook from restoring direct vanilla campaign patches.
    internal static readonly IReadOnlyList<PlayerSettlementMethodSpec> FixesMethods =
        new List<PlayerSettlementMethodSpec>
        {
            Spec("PlayerSettlementFixes.PlayerSettlementFixesSubModule", "OnSubModuleLoad", false,
                "System.Void", PlayerSettlementPatchKind.BlockedSharedMutation),
            Spec("PlayerSettlementFixes.PlayerSettlementFixesLoader", "Initialize", true,
                "System.Void", PlayerSettlementPatchKind.BlockedSharedMutation,
                "HarmonyLib.Harmony"),
            Spec("PlayerSettlementFixes.PlayerSettlementFixesLoader", "EnsureSiegeFixApplied", true,
                "System.Void", PlayerSettlementPatchKind.BlockedSharedMutation),
            Spec("PlayerSettlementFixes.SiegeEnginesContainerRuntimeFix", "Apply", true,
                "System.Void", PlayerSettlementPatchKind.BlockedSharedMutation,
                "HarmonyLib.Harmony"),
        };

    internal static bool IsSupportedIdentity(string moduleVersion, string sha256) =>
        string.Equals(moduleVersion, ModuleVersion, StringComparison.Ordinal) &&
        sha256 != null && SupportedAssemblySha256.Contains(sha256);

    internal static bool TryValidate(
        Assembly assembly,
        Assembly fixesAssembly,
        out IReadOnlyDictionary<PlayerSettlementMethodSpec, MethodInfo> methods,
        out string failure)
    {
        methods = null;
        failure = null;

        if (!TryValidateAssembly(
                assembly,
                AssemblyName,
                AssemblyVersion,
                SupportedAssemblySha256,
                out failure) ||
            !TryValidateAssembly(
                fixesAssembly,
                FixesAssemblyName,
                FixesAssemblyVersion,
                SupportedFixesSha256,
                out failure))
        {
            return false;
        }

        if (!TryResolveMethods(assembly, Methods, out var mainMethods, out failure) ||
            !TryResolveMethods(fixesAssembly, FixesMethods, out var fixesMethods, out failure))
        {
            return false;
        }

        methods = mainMethods
            .Concat(fixesMethods)
            .ToDictionary(pair => pair.Key, pair => pair.Value);
        return true;
    }

    internal static bool TryResolveMethods(
        Assembly assembly,
        IEnumerable<PlayerSettlementMethodSpec> specs,
        out IReadOnlyDictionary<PlayerSettlementMethodSpec, MethodInfo> methods,
        out string failure)
    {
        var result = new Dictionary<PlayerSettlementMethodSpec, MethodInfo>();
        foreach (var spec in specs ?? Array.Empty<PlayerSettlementMethodSpec>())
        {
            var type = assembly?.GetType(spec.TypeName, throwOnError: false, ignoreCase: false);
            if (type == null)
            {
                methods = null;
                failure = $"missing required type {spec.TypeName}";
                return false;
            }

            var matches = type
                .GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public |
                            BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .Where(method => MethodMatchesSpec(method, spec))
                .ToArray();
            if (matches.Length != 1)
            {
                methods = null;
                failure = $"required method shape {spec.Key} resolved {matches.Length} times";
                return false;
            }

            result.Add(spec, matches[0]);
        }

        methods = result;
        failure = null;
        return true;
    }

    internal static bool MethodMatchesSpec(MethodInfo method, PlayerSettlementMethodSpec spec)
    {
        if (method == null || spec == null ||
            !string.Equals(method.Name, spec.MethodName, StringComparison.Ordinal) ||
            method.IsStatic != spec.IsStatic ||
            !string.Equals(DisplayName(method.ReturnType), spec.ReturnTypeName, StringComparison.Ordinal))
        {
            return false;
        }

        var parameters = method.GetParameters();
        if (parameters.Length != spec.ParameterTypeNames.Length) return false;
        for (var index = 0; index < parameters.Length; index++)
        {
            if (!string.Equals(
                    DisplayName(parameters[index].ParameterType),
                    spec.ParameterTypeNames[index],
                    StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    internal static string DisplayName(Type type)
    {
        if (type == null) return string.Empty;
        if (type.IsByRef) return DisplayName(type.GetElementType()) + "&";
        if (type.IsArray) return DisplayName(type.GetElementType()) + "[]";
        if (!type.IsGenericType) return type.FullName ?? type.Name;

        var definition = type.GetGenericTypeDefinition();
        return (definition.FullName ?? definition.Name) + "<" +
               string.Join(",", type.GetGenericArguments().Select(DisplayName)) + ">";
    }

    private static PlayerSettlementMethodSpec Spec(
        string typeName,
        string methodName,
        bool isStatic,
        string returnTypeName,
        PlayerSettlementPatchKind kind,
        params string[] parameterTypeNames) =>
        new PlayerSettlementMethodSpec(
            typeName, methodName, isStatic, returnTypeName, kind, parameterTypeNames);

    private static bool TryValidateAssembly(
        Assembly assembly,
        string expectedName,
        string expectedVersion,
        IReadOnlyCollection<string> hashes,
        out string failure)
    {
        if (assembly == null)
        {
            failure = $"required assembly {expectedName} is not loaded";
            return false;
        }

        var name = assembly.GetName();
        if (!string.Equals(name.Name, expectedName, StringComparison.Ordinal) ||
            !string.Equals(name.Version?.ToString(), expectedVersion, StringComparison.Ordinal))
        {
            failure = $"unsupported {expectedName} identity {name.Name} {name.Version}";
            return false;
        }

        if (string.IsNullOrEmpty(assembly.Location) || !File.Exists(assembly.Location))
        {
            failure = $"cannot hash loaded {expectedName} assembly";
            return false;
        }

        string hash;
        using (var stream = File.OpenRead(assembly.Location))
        using (var sha = SHA256.Create())
            hash = ToHex(sha.ComputeHash(stream));

        if (!hashes.Contains(hash))
        {
            failure = $"unsupported {expectedName} SHA-256 {hash}";
            return false;
        }

        failure = null;
        return true;
    }

    private static string ToHex(byte[] bytes)
    {
        const string digits = "0123456789abcdef";
        var result = new char[bytes.Length * 2];
        for (var index = 0; index < bytes.Length; index++)
        {
            result[index * 2] = digits[bytes[index] >> 4];
            result[(index * 2) + 1] = digits[bytes[index] & 15];
        }
        return new string(result);
    }
}
