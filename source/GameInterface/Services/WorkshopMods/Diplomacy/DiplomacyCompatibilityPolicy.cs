using Common;
using GameInterface.Configuration;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;

namespace GameInterface.Services.WorkshopMods.Diplomacy;

/// <summary>
/// Names from Diplomacy 1.4.7 are kept here instead of taking a binary reference on the mod.
/// The compatibility layer therefore remains inert when Diplomacy is not installed and does not
/// make the Coop module loader depend on a particular Diplomacy build.
/// </summary>
internal static class DiplomacyCompatibilityPolicy
{
    internal const string SupportedAssemblyName = "Bannerlord.Diplomacy.1.4.7";
    internal const string SupportedAssemblyVersion = "1.4.7.0";
    internal const string SupportedAssemblySha256 =
        "90930a1dfb48c8cf040b8bd2c89156a69838a8dc86b8ed97e0cd8475f2081257";
    internal const string SubModuleTypeName = "Diplomacy.SubModule";
    internal const string SettingsTypeName = "Diplomacy.Settings";

    private static readonly object AssemblyGate = new();
    private static Assembly cachedCandidate;
    private static bool cachedCandidateSupported;
    private static string cachedFailure = "Diplomacy 1.4.7 is not loaded.";

    private static readonly IReadOnlyDictionary<string, (string Method, int Arity)[]> RequiredMethodShapes =
        new Dictionary<string, (string Method, int Arity)[]>(StringComparer.Ordinal)
        {
            [SubModuleTypeName] = new[] { ("OnGameStart", 2) },
            ["Diplomacy.CampaignBehaviors.CooldownBehavior"] = new[]
            {
                ("RegisterEvents", 0), ("RegisterAllianceFormedCooldown", 2),
                ("RegisterDeclareWarCooldown", 3), ("RegisterPeaceProposalCooldown", 1), ("SyncData", 1),
            },
            ["Diplomacy.CampaignBehaviors.DiplomaticAgreementBehavior"] = new[]
            {
                ("RegisterEvents", 0), ("ConsiderDiplomaticAgreements", 1),
                ("UpdateDiplomaticAgreements", 0), ("ExpireNonAggressionPact", 2), ("SyncData", 1),
            },
            ["Diplomacy.CampaignBehaviors.ExpansionismBehavior"] = new[]
            {
                ("RegisterEvents", 0), ("OnDailyTickClan", 1), ("OnSettlementOwnerChanged", 6), ("SyncData", 1),
            },
            ["Diplomacy.CampaignBehaviors.MaintainInfluenceBehavior"] = new[]
            {
                ("RegisterEvents", 0), ("ReduceCorruption", 1),
            },
            ["Diplomacy.CampaignBehaviors.WarExhaustionBehavior"] = new[]
            {
                ("RegisterEvents", 0), ("OnDailyTick", 0), ("OnRaidCompleted", 2),
                ("OnMapEventEnded", 1), ("OnPrisonerTaken", 2), ("OnHeroKilled", 4),
                ("RegisterWarExhaustion", 3), ("ClearWarExhaustion", 3),
                ("OnGameLoadFinished", 0), ("SyncData", 1),
            },
            ["Diplomacy.CampaignBehaviors.CivilWarBehavior"] = new[]
            {
                ("RegisterEvents", 0), ("DailyTickClan", 1),
                ("RemoveClanFromRebelFaction", 3), ("ResolveCivilWar", 3),
                ("NewKing", 3), ("DailyTick", 0), ("OnClanDesroyed", 1),
                ("OnKingdomDestroyed", 1), ("OnGameLoadFinished", 0), ("SyncData", 1),
            },
            ["Diplomacy.CampaignBehaviors.MessengerBehavior"] = new[]
            {
                ("RegisterEvents", 0), ("OnMessengerSent", 1), ("OnAfterSessionLaunched", 1),
                ("OnHourlyTick", 0), ("OnDailyTick", 0), ("SyncData", 1),
            },
            ["Diplomacy.CampaignBehaviors.UIBehavior"] = new[] { ("RegisterEvents", 0), ("AddUIElements", 1) },
            ["Diplomacy.CampaignBehaviors.KeepFiefAfterSiegeBehavior"] = new[]
            {
                ("RegisterEvents", 0), ("OnPlayerSettlementTaken", 1),
            },
            ["Diplomacy.Models.DiplomacyKingdomDecisionPermissionModel"] = new[]
            {
                ("IsWarDecisionAllowedBetweenKingdoms", 3),
                ("IsPeaceDecisionAllowedBetweenKingdoms", 3),
            },
            ["Diplomacy.CivilWar.Actions.CreateFactionAction"] = new[] { ("Apply", 1) },
            ["Diplomacy.CivilWar.Actions.JoinFactionAction"] = new[] { ("Apply", 2) },
            ["Diplomacy.CivilWar.Actions.LeaveFactionAction"] = new[] { ("Apply", 2) },
            ["Diplomacy.CivilWar.Actions.StartRebellionAction"] = new[] { ("Apply", 1) },
            ["Diplomacy.CivilWar.Factions.RebelFaction"] = new[]
            {
                ("StartRebellion", 1), ("AddClan", 1), ("RemoveClan", 1),
                ("EnforceSuccess", 0), ("EnforceFailure", 0),
            },
            ["Diplomacy.ViewModel.RebelFactionsVM"] = new[] { ("OnCreateFaction", 0), ("HandleCreateFaction", 1) },
            ["Diplomacy.ViewModel.RebelFactionItemVM"] = new[]
            {
                ("OnJoin", 0), ("OnLeave", 0), ("OnStartRebellion", 0),
            },
            ["Diplomacy.ViewModel.GrantFiefVM"] = new[] { ("OnGrantFief", 0) },
            ["Diplomacy.ViewModel.DonateGoldVM"] = new[] { ("ExecutePropose", 0) },
            ["Diplomacy.Actions.GiveGoldToClanAction"] = new[] { ("ApplyFromHeroToClan", 3) },
            ["Diplomacy.Actions.GrantFiefAction"] = new[] { ("Apply", 2) },
            ["Diplomacy.Costs.DiplomacyCostCalculator"] = new[]
            {
                ("DetermineCostForSendingMessenger", 1),
                ("DetermineCostForDeclaringWar", 2),
            },
            ["Diplomacy.Messengers.MessengerManager"] = new[]
            {
                ("SendMessenger", 1),
                ("CanSendMessengerWithCost", 2),
            },
            ["Diplomacy.Character.PlayerCharacterTraitHelper"] = new[] { ("UpdateTrait", 4) },
            ["Diplomacy.Character.PlayerCharacterTraitEventExperience"] = new[] { ("Apply", 0) },
            ["Diplomacy.ViewModelMixin.EncyclopediaHeroPageVMMixin"] = new[] { ("SendMessenger", 0) },
            ["Diplomacy.ViewModelMixin.KingdomWarItemVMMixin"] = new[] { ("ExecuteDirectAction", 0) },
            ["Diplomacy.ViewModelMixin.KingdomTruceItemVMMixin"] = new[]
            {
                ("ExecuteDirectAction", 0), ("ProposeNonAggressionPact", 0),
            },
            ["Diplomacy.DiplomaticAction.AbstractDiplomaticAction`1"] = new[] { ("TryApply", 6) },
            ["Diplomacy.DiplomaticAction.NonAggressionPact.FormNonAggressionPactAction"] = new[]
            {
                ("PassesConditions", 4),
                ("ApplyInternal", 3),
            },
            ["Diplomacy.DiplomaticAction.NonAggressionPactAgreement"] = new[] { ("NotifyExpired", 0) },
            ["Diplomacy.DiplomaticAction.WarPeace.KingdomPeaceAction"] = new[]
            {
                ("ApplyPeace", 6),
                ("ApplyPeaceInternal", 9),
                ("AcceptPeace", 6),
            },
        };

    private static readonly string[] RequiredTypes =
    {
        SettingsTypeName,
        "Diplomacy.ExpansionismManager",
        "Diplomacy.CooldownManager",
        "Diplomacy.DiplomaticAction.DiplomaticAgreementManager",
        "Diplomacy.DiplomaticAction.NonAggressionPactAgreement",
        "Diplomacy.WarExhaustion.WarExhaustionManager",
        "Diplomacy.WarExhaustion.WarExhaustionRecord",
        "Diplomacy.ViewModelMixin.KingdomManagementPrefabExtension",
        "Diplomacy.ViewModelMixin.KingdomManagementScalingPatch",
        "Diplomacy.ViewModelMixin.KingdomManagementVMMixin",
    };

    private static readonly IReadOnlyDictionary<string, string[]> SharedMutationMethods =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["Diplomacy.CampaignBehaviors.CooldownBehavior"] = new[]
            {
                "RegisterEvents",
                "RegisterAllianceFormedCooldown",
                "RegisterDeclareWarCooldown",
                "RegisterPeaceProposalCooldown",
                "SyncData",
            },
            ["Diplomacy.CampaignBehaviors.DiplomaticAgreementBehavior"] = new[]
            {
                "RegisterEvents",
                "ConsiderDiplomaticAgreements",
                "UpdateDiplomaticAgreements",
                "ExpireNonAggressionPact",
                "SyncData",
            },
            ["Diplomacy.CampaignBehaviors.ExpansionismBehavior"] = new[]
            {
                "RegisterEvents",
                "OnDailyTickClan",
                "OnSettlementOwnerChanged",
                "SyncData",
            },
            ["Diplomacy.CampaignBehaviors.MaintainInfluenceBehavior"] = new[]
            {
                "RegisterEvents",
                "ReduceCorruption",
            },
            ["Diplomacy.CampaignBehaviors.WarExhaustionBehavior"] = new[]
            {
                "RegisterEvents",
                "OnDailyTick",
                "OnRaidCompleted",
                "OnMapEventEnded",
                "OnPrisonerTaken",
                "OnHeroKilled",
                "RegisterWarExhaustion",
                "ClearWarExhaustion",
                "OnGameLoadFinished",
                "SyncData",
            },
        };

    private static readonly IReadOnlyDictionary<string, string[]> CivilWarEntryPoints =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["Diplomacy.CampaignBehaviors.CivilWarBehavior"] = new[]
            {
                "RegisterEvents",
                "DailyTickClan",
                "RemoveClanFromRebelFaction",
                "ResolveCivilWar",
                "NewKing",
                "DailyTick",
                "OnClanDesroyed",
                "OnKingdomDestroyed",
                "OnGameLoadFinished",
                "SyncData",
            },
            ["Diplomacy.CivilWar.Actions.CreateFactionAction"] = new[] { "Apply" },
            ["Diplomacy.CivilWar.Actions.JoinFactionAction"] = new[] { "Apply" },
            ["Diplomacy.CivilWar.Actions.LeaveFactionAction"] = new[] { "Apply" },
            ["Diplomacy.CivilWar.Actions.StartRebellionAction"] = new[] { "Apply" },
            ["Diplomacy.CivilWar.Factions.RebelFaction"] = new[]
            {
                "StartRebellion",
                "AddClan",
                "RemoveClan",
                "EnforceSuccess",
                "EnforceFailure",
            },
            ["Diplomacy.ViewModel.RebelFactionsVM"] = new[] { "OnCreateFaction", "HandleCreateFaction" },
            ["Diplomacy.ViewModel.RebelFactionItemVM"] = new[] { "OnJoin", "OnLeave", "OnStartRebellion" },
        };

    /// <summary>
    /// The Friend Edition Separatism service is the sole rebellion authority whenever enabled.
    /// Diplomacy's civil-war behavior creates its own rebel kingdoms and would otherwise race the
    /// same daily campaign ticks.
    /// </summary>
    internal static bool FriendSeparatismOwnsRebellions => ModConfigProvider.ModOptions.Separatism.Enabled;

    internal static bool ShouldRunSharedMutation() => ModInformation.IsServer;

    internal static bool ShouldRunSharedMutation(string typeName, string methodName) =>
        ModInformation.IsServer;

    internal static bool ShouldRunOriginalMessengerBehavior() => false;

    // Diplomacy's CivilWarBehavior reads Friend Edition configuration while behaviors are being
    // registered, before the host config is guaranteed to be loaded. More importantly, its
    // RebelFactionManager has no controller-safe UI or complete late-join representation here.
    // Blocking every entry point on both roles is the only deterministic policy until that entire
    // manager and its player actions have an authoritative protocol. Friend Separatism remains the
    // supported rebellion implementation when enabled.
    internal static bool ShouldRunCivilWarEntryPoint() => false;

    internal static bool IsSharedMutation(string typeName, string methodName) =>
        SharedMutationMethods.TryGetValue(typeName, out var methods) && methods.Contains(methodName, StringComparer.Ordinal);

    internal static bool IsCivilWarEntryPoint(string typeName, string methodName) =>
        CivilWarEntryPoints.TryGetValue(typeName, out var methods) && methods.Contains(methodName, StringComparer.Ordinal);

    internal static IEnumerable<MethodBase> ResolveSharedMutationMethods() => ResolveMethods(SharedMutationMethods);

    internal static IEnumerable<MethodBase> ResolveCivilWarEntryPoints() => ResolveMethods(CivilWarEntryPoints);

    internal static Type ResolveType(string fullName)
    {
        var assembly = ResolveAssembly();
        return assembly?.GetType(fullName, throwOnError: false, ignoreCase: false);
    }

    internal static Assembly ResolveAssembly()
    {
        return TryResolveSupportedAssembly(out var assembly, out _) ? assembly : null;
    }

    internal static IReadOnlyList<Assembly> ResolveCandidateAssemblies()
    {
        var candidates = new List<Assembly>();
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            bool hasSubModule = false;
            try
            {
                hasSubModule = assembly.GetType(
                    SubModuleTypeName,
                    throwOnError: false,
                    ignoreCase: false) != null;
            }
            catch (Exception)
            {
                // An assembly carrying the Diplomacy name but failing type resolution is still a
                // candidate. Treating a loader failure as absence would let an unverified update
                // bypass the startup gate.
            }

            if (IsPotentialDiplomacyAssembly(assembly.GetName().Name, hasSubModule))
                candidates.Add(assembly);
        }

        return candidates;
    }

    internal static Assembly ResolveCandidateAssembly() => ResolveCandidateAssemblies()
        .OrderByDescending(candidate => string.Equals(
            candidate.GetName().Name,
            SupportedAssemblyName,
            StringComparison.Ordinal))
        .FirstOrDefault();

    internal static bool IsPotentialDiplomacyAssembly(string assemblyName, bool hasSubModule) =>
        hasSubModule ||
        string.Equals(assemblyName, "Bannerlord.Diplomacy", StringComparison.Ordinal) ||
        (assemblyName?.StartsWith("Bannerlord.Diplomacy.", StringComparison.Ordinal) ?? false);

    internal static bool TryResolveSupportedAssembly(out Assembly assembly, out string failure)
    {
        var candidates = ResolveCandidateAssemblies();
        if (candidates.Count == 0)
        {
            assembly = null;
            failure = "Diplomacy 1.4.7 is not loaded.";
            return false;
        }
        if (candidates.Count != 1)
        {
            assembly = null;
            failure = $"Found {candidates.Count} Diplomacy implementation candidates; exactly one audited DLL is required.";
            return false;
        }

        var candidate = candidates[0];

        lock (AssemblyGate)
        {
            if (!ReferenceEquals(candidate, cachedCandidate))
            {
                cachedCandidate = candidate;
                cachedCandidateSupported = ValidateImplementation(candidate, out cachedFailure);
            }

            assembly = cachedCandidateSupported ? candidate : null;
            failure = cachedFailure;
            return cachedCandidateSupported;
        }
    }

    internal static string CompatibilityFailure
    {
        get
        {
            TryResolveSupportedAssembly(out _, out var failure);
            return failure;
        }
    }

    internal static bool IsExpectedAssemblyIdentity(string name, string version, string sha256) =>
        string.Equals(name, SupportedAssemblyName, StringComparison.Ordinal) &&
        string.Equals(version, SupportedAssemblyVersion, StringComparison.Ordinal) &&
        FixedTimeEqualsHex(sha256, SupportedAssemblySha256);

    internal static bool IsRequiredMethodShape(string typeName, string methodName, int arity) =>
        RequiredMethodShapes.TryGetValue(typeName, out var shapes) &&
        shapes.Any(shape => shape.Method == methodName && shape.Arity == arity);

    private static bool ValidateImplementation(Assembly assembly, out string failure)
    {
        var name = assembly.GetName();
        string version = name.Version?.ToString() ?? string.Empty;
        string hash;
        try
        {
            if (string.IsNullOrWhiteSpace(assembly.Location) || !File.Exists(assembly.Location))
            {
                failure = "Diplomacy implementation has no verifiable DLL location.";
                return false;
            }

            using var stream = File.OpenRead(assembly.Location);
            using var sha = SHA256.Create();
            hash = ToHex(sha.ComputeHash(stream));
        }
        catch (Exception ex)
        {
            failure = $"Diplomacy DLL fingerprint could not be read: {ex.Message}";
            return false;
        }

        if (!IsExpectedAssemblyIdentity(name.Name, version, hash))
        {
            failure = $"Unsupported Diplomacy implementation {name.Name} {version} ({hash}).";
            return false;
        }

        foreach (var typeName in RequiredTypes)
        {
            if (assembly.GetType(typeName, throwOnError: false, ignoreCase: false) != null) continue;
            failure = $"Diplomacy 1.4.7 is missing required type {typeName}.";
            return false;
        }

        foreach (var pair in RequiredMethodShapes)
        {
            var type = assembly.GetType(pair.Key, throwOnError: false, ignoreCase: false);
            if (type == null)
            {
                failure = $"Diplomacy 1.4.7 is missing patched type {pair.Key}.";
                return false;
            }

            var methods = type.GetMethods(
                BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public |
                BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            foreach (var shape in pair.Value)
            {
                if (methods.Count(method => method.Name == shape.Method &&
                                            method.GetParameters().Length == shape.Arity) == 1)
                {
                    continue;
                }

                failure = $"Diplomacy 1.4.7 method shape mismatch: {pair.Key}.{shape.Method}/{shape.Arity}.";
                return false;
            }
        }

        failure = null;
        return true;
    }

    private static string ToHex(byte[] bytes)
    {
        var chars = new char[bytes.Length * 2];
        const string alphabet = "0123456789abcdef";
        for (int index = 0; index < bytes.Length; index++)
        {
            chars[index * 2] = alphabet[bytes[index] >> 4];
            chars[index * 2 + 1] = alphabet[bytes[index] & 0x0f];
        }
        return new string(chars);
    }

    private static bool FixedTimeEqualsHex(string left, string right)
    {
        if (left == null || right == null || left.Length != right.Length) return false;
        int difference = 0;
        for (int index = 0; index < left.Length; index++)
            difference |= char.ToLowerInvariant(left[index]) ^ char.ToLowerInvariant(right[index]);
        return difference == 0;
    }

    private static IEnumerable<MethodBase> ResolveMethods(IReadOnlyDictionary<string, string[]> catalog)
    {
        foreach (var pair in catalog)
        {
            var type = ResolveType(pair.Key);
            if (type == null) continue;

            foreach (var methodName in pair.Value)
            {
                foreach (var method in AccessTools.GetDeclaredMethods(type).Where(method =>
                             method.Name == methodName &&
                             IsRequiredMethodShape(pair.Key, method.Name, method.GetParameters().Length)))
                    yield return method;
            }
        }
    }
}
