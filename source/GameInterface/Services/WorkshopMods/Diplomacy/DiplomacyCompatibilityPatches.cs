using Common;
using Common.Logging;
using GameInterface.Services.WorkshopMods.Core;
using HarmonyLib;
using System;
using System.Collections;
using Serilog;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TaleWorlds.Core;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.ComponentInterfaces;
using TaleWorlds.Library;
using TaleWorlds.Localization;

namespace GameInterface.Services.WorkshopMods.Diplomacy;

/// <summary>
/// Diplomacy registers its campaign listeners on every process. Coop's campaign model is
/// server-authoritative, so the listeners which mutate shared/save state are allowed to execute
/// only on the server. Native kingdom, stance, decision, gold and influence changes made by those
/// callbacks continue through Coop's existing synchronization funnels.
/// </summary>
[HarmonyPatch]
[HarmonyPatchCategory(WorkshopPatchCategories.Diplomacy)]
internal static class DiplomacySharedMutationAuthorityPatch
{
    private static IEnumerable<MethodBase> TargetMethods() =>
        DiplomacyCompatibilityPolicy.ResolveSharedMutationMethods();

    // The category gate keeps this class out of GameInterface.PatchAll when Diplomacy is absent,
    // but Harmony's blanket PatchAll(assembly) applies categorised classes too and throws
    // "Undefined target method" on an empty TargetMethods(). Prepare makes the class immune
    // regardless of how patching is invoked — the same defence the RBM/DismembermentPlus
    // adapters carry (see RbmPatchWaveCompatibilityPatch.Prepare).
    [HarmonyPrepare]
    private static bool Prepare() => TargetMethods().Any();

    [HarmonyPrefix]
    private static bool Prefix(MethodBase __originalMethod, object[] __args)
    {
        if (!DiplomacyCompatibilityPolicy.ShouldRunSharedMutation(
                __originalMethod?.DeclaringType?.FullName,
                __originalMethod?.Name))
        {
            return false;
        }

        // Diplomacy 1.4.7 dereferences both MapEvent sides, every MapEventParty.Party and
        // the winning/defeated side without null guards. Ended caravan encounters can retain
        // a removed party in that graph, so skip only that bookkeeping callback instead of
        // crashing the authoritative campaign process.
        if (__originalMethod?.DeclaringType?.FullName ==
                "Diplomacy.CampaignBehaviors.WarExhaustionBehavior" &&
            __originalMethod.Name == "OnMapEventEnded")
        {
            return __args != null &&
                   __args.Length == 1 &&
                   DiplomacyWarExhaustionSafety.IsSafeMapEvent(__args[0]);
        }

        return true;
    }

    [HarmonyPostfix]
    private static void Postfix()
    {
        if (!ModInformation.IsServer) return;
        if (ContainerProvider.TryResolve<IDiplomacySnapshotPublisher>(out var publisher))
            publisher.PublishIfChanged();
    }
}

/// <summary>
/// Structural validation for Diplomacy's null-unsafe war-exhaustion callback. Reflection keeps
/// this helper independent of the optional Diplomacy assembly and makes it possible to reject a
/// partially dismantled MapEvent graph before the original code touches it.
/// </summary>
internal static class DiplomacyWarExhaustionSafety
{
    internal static bool IsSafeMapEvent(object mapEvent)
    {
        if (mapEvent == null ||
            !TryReadMember(mapEvent, "AttackerSide", out var attackerSide) ||
            !TryReadMember(mapEvent, "DefenderSide", out var defenderSide) ||
            attackerSide == null ||
            defenderSide == null)
        {
            return false;
        }

        if (!IsSafeSide(attackerSide) || !IsSafeSide(defenderSide) ||
            !TryReadMember(mapEvent, "WinningSide", out var winningSide) ||
            !TryReadMember(mapEvent, "DefeatedSide", out var defeatedSide) ||
            !TryConvertSide(winningSide, out int winningValue) ||
            !TryConvertSide(defeatedSide, out int defeatedValue))
        {
            return false;
        }

        // None (-1) exits before GetMapEventSide in Diplomacy. Any other value must be one of the
        // two native sides and resolve to a structurally safe side object.
        if (winningValue == -1 || defeatedValue == -1) return true;
        if (winningValue is < 0 or > 1 || defeatedValue is < 0 or > 1) return false;

        try
        {
            var resolver = mapEvent.GetType()
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .SingleOrDefault(method => method.Name == "GetMapEventSide" && method.GetParameters().Length == 1);
            if (resolver == null) return false;
            return IsSafeSide(resolver.Invoke(mapEvent, new[] { winningSide })) &&
                   IsSafeSide(resolver.Invoke(mapEvent, new[] { defeatedSide }));
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool IsSafeSide(object side)
    {
        if (!TryReadMember(side, "LeaderParty", out var leaderParty) || leaderParty == null ||
            !TryReadMember(side, "Parties", out var parties) || parties is not IEnumerable entries)
        {
            return false;
        }

        foreach (var entry in entries)
        {
            if (entry == null ||
                !TryReadMember(entry, "Party", out var party) ||
                party == null)
            {
                return false;
            }

            if (!TryReadMember(party, "IsMobile", out var isMobile) || isMobile is not bool mobile)
                return false;
            if (mobile &&
                (!TryReadMember(party, "MobileParty", out var mobileParty) || mobileParty == null))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryReadMember(object instance, string memberName, out object value)
    {
        value = null;
        try
        {
            const BindingFlags flags =
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var type = instance.GetType();
            var property = type.GetProperty(memberName, flags);
            if (property != null)
            {
                value = property.GetValue(instance);
                return true;
            }

            var field = type.GetField(memberName, flags);
            if (field == null) return false;
            value = field.GetValue(instance);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool TryConvertSide(object value, out int side)
    {
        try
        {
            side = System.Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
            return true;
        }
        catch (Exception)
        {
            side = 0;
            return false;
        }
    }
}

/// <summary>
/// Friend Edition's Separatism service and Diplomacy's CivilWarBehavior both create rebel
/// kingdoms from daily ticks. Running both is not a supported blend: when Friend Separatism is
/// enabled, every Diplomacy rebellion entry point is stopped before it can alter manager or
/// campaign state. The feature is also blocked when Friend Separatism is disabled because its
/// manager and player controls do not yet have a complete late-join protocol.
/// </summary>
[HarmonyPatch]
[HarmonyPatchCategory(WorkshopPatchCategories.Diplomacy)]
internal static class DiplomacyCivilWarCollisionPatch
{
    private static readonly ILogger Logger = LogManager.GetLogger(typeof(DiplomacyCivilWarCollisionPatch));
    private static bool logged;

    private static IEnumerable<MethodBase> TargetMethods() =>
        DiplomacyCompatibilityPolicy.ResolveCivilWarEntryPoints();

    // See DiplomacySharedMutationAuthorityPatch.Prepare.
    [HarmonyPrepare]
    private static bool Prepare() => TargetMethods().Any();

    [HarmonyPrefix]
    private static bool Prefix(MethodBase __originalMethod)
    {
        if (DiplomacyCompatibilityPolicy.ShouldRunCivilWarEntryPoint()) return true;

        if (!logged)
        {
            logged = true;
            Logger.Information(
                "Diplomacy civil-war entry points are disabled in co-op; Friend Edition Separatism is the supported rebellion authority and Diplomacy rebel state is not replicated ({Method}).",
                __originalMethod?.Name);
        }

        return false;
    }
}

/// <summary>
/// These Diplomacy UI consequences assume the vanilla singleton player and directly mutate the
/// campaign. A dedicated server has no Hero.MainHero, while executing them on a client would make
/// that client diverge. Until a validated request/authorization flow exists, fail closed and tell
/// the player instead of silently applying the action on the wrong process.
/// </summary>
[HarmonyPatch]
[HarmonyPatchCategory(WorkshopPatchCategories.Diplomacy)]
internal static class DiplomacyUnsupportedPlayerActionPatch
{
    private static readonly ILogger Logger = LogManager.GetLogger(typeof(DiplomacyUnsupportedPlayerActionPatch));
    private static readonly HashSet<string> LoggedMethods = new();

    private static readonly IReadOnlyDictionary<string, string[]> EntryPoints =
        new Dictionary<string, string[]>(System.StringComparer.Ordinal)
        {
            ["Diplomacy.ViewModel.GrantFiefVM"] = new[] { "OnGrantFief" },
            ["Diplomacy.ViewModel.DonateGoldVM"] = new[] { "ExecutePropose" },
            ["Diplomacy.ViewModelMixin.EncyclopediaHeroPageVMMixin"] = new[] { "SendMessenger" },
            ["Diplomacy.ViewModelMixin.KingdomWarItemVMMixin"] = new[] { "ExecuteDirectAction" },
            ["Diplomacy.ViewModelMixin.KingdomTruceItemVMMixin"] = new[]
            {
                "ExecuteDirectAction",
                "ProposeNonAggressionPact",
            },
            ["Diplomacy.CampaignBehaviors.KeepFiefAfterSiegeBehavior"] = new[] { "OnPlayerSettlementTaken" },
        };

    private static IEnumerable<MethodBase> TargetMethods()
    {
        foreach (var pair in EntryPoints)
        {
            var type = DiplomacyCompatibilityPolicy.ResolveType(pair.Key);
            if (type == null) continue;

            foreach (var methodName in pair.Value)
            {
                foreach (var method in AccessTools.GetDeclaredMethods(type))
                {
                    if (method.Name == methodName) yield return method;
                }
            }
        }
    }

    // See DiplomacySharedMutationAuthorityPatch.Prepare.
    [HarmonyPrepare]
    private static bool Prepare() => TargetMethods().Any();

    [HarmonyPrefix]
    private static bool Prefix(MethodBase __originalMethod)
    {
        var method = $"{__originalMethod?.DeclaringType?.FullName}.{__originalMethod?.Name}";
        if (LoggedMethods.Add(method))
        {
            Logger.Warning(
                "Blocked unsupported Diplomacy player action {Method}; it requires a Coop server request/authorization path.",
                method);
        }

        if (ModInformation.IsClient)
        {
            InformationManager.DisplayMessage(new InformationMessage(
                "This Diplomacy action is disabled in co-op until it has a server-authorized request path."));
        }

        return false;
    }
}

/// <summary>Diplomacy's UI tick has no work on a headless campaign server.</summary>
[HarmonyPatch]
[HarmonyPatchCategory(WorkshopPatchCategories.Diplomacy)]
internal static class DiplomacyServerUiGuardPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        var type = DiplomacyCompatibilityPolicy.ResolveType("Diplomacy.CampaignBehaviors.UIBehavior");
        if (type == null) yield break;

        foreach (var method in AccessTools.GetDeclaredMethods(type))
        {
            if (method.Name is "RegisterEvents" or "AddUIElements") yield return method;
        }
    }

    // See DiplomacySharedMutationAuthorityPatch.Prepare.
    [HarmonyPrepare]
    private static bool Prepare() => TargetMethods().Any();

    [HarmonyPrefix]
    private static bool Prefix() => ModInformation.IsClient;
}

/// <summary>
/// Messenger state dereferences Hero.MainHero/MainParty and starts global PlayerEncounter state.
/// The initiating action has no server request path, so the feature and any messenger records
/// inherited from a single-player save are inert on every peer rather than mutating whichever
/// hero happens to be exposed through the local singleton.
/// </summary>
[HarmonyPatch]
[HarmonyPatchCategory(WorkshopPatchCategories.Diplomacy)]
internal static class DiplomacyMessengerFeatureGuardPatch
{
    private static readonly string[] UnsafeMethods =
    {
        "RegisterEvents",
        "OnMessengerSent",
        "OnAfterSessionLaunched",
        "OnHourlyTick",
        "OnDailyTick",
        "SyncData",
    };

    private static IEnumerable<MethodBase> TargetMethods()
    {
        var type = DiplomacyCompatibilityPolicy.ResolveType("Diplomacy.CampaignBehaviors.MessengerBehavior");
        if (type == null) yield break;

        foreach (var method in AccessTools.GetDeclaredMethods(type))
        {
            if (System.Array.IndexOf(UnsafeMethods, method.Name) >= 0) yield return method;
        }
    }

    // See DiplomacySharedMutationAuthorityPatch.Prepare.
    [HarmonyPrepare]
    private static bool Prepare() => TargetMethods().Any();

    [HarmonyPrefix]
    private static bool Prefix() => false;
}

/// <summary>
/// Keep-fief prompts capture Hero.MainHero inside a deferred inquiry callback. That cannot be
/// attributed to the requesting co-op player, so do not register the behavior on either role.
/// The callback itself is also in the unsupported-player-action guard as defense in depth.
/// </summary>
[HarmonyPatch]
[HarmonyPatchCategory(WorkshopPatchCategories.Diplomacy)]
internal static class DiplomacyKeepFiefBehaviorGuardPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        var type = DiplomacyCompatibilityPolicy.ResolveType(
            "Diplomacy.CampaignBehaviors.KeepFiefAfterSiegeBehavior");
        var method = type == null ? null : AccessTools.Method(type, "RegisterEvents");
        if (method != null) yield return method;
    }

    // See DiplomacySharedMutationAuthorityPatch.Prepare.
    [HarmonyPrepare]
    private static bool Prepare() => TargetMethods().Any();

    [HarmonyPrefix]
    private static bool Prefix() => false;
}

/// <summary>
/// Diplomacy routes pacts and exhaustion peace through Clan.PlayerClan and local inquiries. Those
/// methods dereference the vanilla singleton even for nominal AI-to-AI calls, so a headless server
/// cannot safely execute them. Coop's existing stance/decision services remain the sole peace
/// authority; NAP creation stays feature-blocked until it has a complete server request path.
/// </summary>
[HarmonyPatch]
[HarmonyPatchCategory(WorkshopPatchCategories.Diplomacy)]
internal static class DiplomacyPlayerKingdomActionGuardPatch
{
    private static readonly ILogger Logger = LogManager.GetLogger(typeof(DiplomacyPlayerKingdomActionGuardPatch));
    private static readonly HashSet<string> LoggedMethods = new();

    private static IEnumerable<MethodBase> TargetMethods()
    {
        var pactType = DiplomacyCompatibilityPolicy.ResolveType(
            "Diplomacy.DiplomaticAction.NonAggressionPact.FormNonAggressionPactAction");
        // TryApply is inherited from the closed generic action base and is the normal entry point.
        // ApplyInternal is the audited state-mutation sink; patching both prevents a cheat/plugin
        // or future internal caller from bypassing the singleton-dependent outer path.
        var pactTryApply = pactType == null ? null : AccessTools.Method(pactType, "TryApply");
        if (pactTryApply != null) yield return pactTryApply;
        var pactApplyInternal = pactType == null ? null : AccessTools.Method(pactType, "ApplyInternal");
        if (pactApplyInternal != null) yield return pactApplyInternal;

        var peaceType = DiplomacyCompatibilityPolicy.ResolveType(
            "Diplomacy.DiplomaticAction.WarPeace.KingdomPeaceAction");
        var peaceMethod = peaceType == null ? null : AccessTools.Method(peaceType, "ApplyPeace");
        if (peaceMethod != null) yield return peaceMethod;
    }

    // See DiplomacySharedMutationAuthorityPatch.Prepare.
    [HarmonyPrepare]
    private static bool Prepare() => TargetMethods().Any();

    [HarmonyPrefix]
    private static bool Prefix(MethodBase __originalMethod)
    {
        string method = $"{__originalMethod?.DeclaringType?.FullName}.{__originalMethod?.Name}";
        if (LoggedMethods.Add(method))
        {
            Logger.Warning(
                "Blocked Diplomacy action {Method}; it depends on Clan.PlayerClan and has no headless-safe Coop authority path.",
                method);
        }
        return false;
    }

    internal static bool ShouldAllowKingdomAction() => false;
}

/// <summary>Expiration notifications are local UI and must not execute on a headless server.</summary>
[HarmonyPatch]
[HarmonyPatchCategory(WorkshopPatchCategories.Diplomacy)]
internal static class DiplomacyServerNotificationGuardPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        var type = DiplomacyCompatibilityPolicy.ResolveType(
            "Diplomacy.DiplomaticAction.NonAggressionPactAgreement");
        var method = type == null ? null : AccessTools.Method(type, "NotifyExpired");
        if (method != null) yield return method;
    }

    // See DiplomacySharedMutationAuthorityPatch.Prepare.
    [HarmonyPrepare]
    private static bool Prepare() => TargetMethods().Any();

    [HarmonyPrefix]
    private static bool Prefix() => ModInformation.IsClient;
}

/// <summary>
/// Diplomacy installs a KingdomDecisionPermissionModel directly rather than through Harmony. Its
/// war/peace conditions consume mutable local MCM settings and one condition dereferences
/// Clan.PlayerClan, so removing the original client patches alone is not a complete authority
/// boundary. Delegate to the previously installed native/Coop model on every peer.
/// </summary>
[HarmonyPatch]
[HarmonyPatchCategory(WorkshopPatchCategories.Diplomacy)]
internal static class DiplomacyDecisionPermissionModelGuardPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        var type = DiplomacyCompatibilityPolicy.ResolveType(
            "Diplomacy.Models.DiplomacyKingdomDecisionPermissionModel");
        if (type == null) yield break;

        foreach (var method in AccessTools.GetDeclaredMethods(type))
        {
            if (method.Name is "IsWarDecisionAllowedBetweenKingdoms" or
                "IsPeaceDecisionAllowedBetweenKingdoms")
            {
                yield return method;
            }
        }
    }

    // See DiplomacySharedMutationAuthorityPatch.Prepare.
    [HarmonyPrepare]
    private static bool Prepare() => TargetMethods().Any();

    [HarmonyPrefix]
    private static bool Prefix(
        object __instance,
        MethodBase __originalMethod,
        Kingdom kingdom1,
        Kingdom kingdom2,
        ref TextObject reason,
        ref bool __result)
    {
        reason = new TextObject("Diplomacy's local decision model is disabled in co-op.");
        var previous = AccessTools.Field(__instance?.GetType(), "_previousModel")
            ?.GetValue(__instance) as KingdomDecisionPermissionModel;
        if (previous == null)
        {
            __result = false;
            return false;
        }

        if (__originalMethod?.Name == "IsWarDecisionAllowedBetweenKingdoms")
        {
            __result = previous.IsWarDecisionAllowedBetweenKingdoms(kingdom1, kingdom2, out reason);
            return false;
        }

        if (__originalMethod?.Name == "IsPeaceDecisionAllowedBetweenKingdoms")
        {
            __result = previous.IsPeaceDecisionAllowedBetweenKingdoms(kingdom1, kingdom2, out reason);
            return false;
        }

        __result = false;
        return false;
    }

    internal static bool ShouldUseDiplomacyDecisionFormula() => false;
}
