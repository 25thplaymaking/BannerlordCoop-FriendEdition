using Common;
using Common.Logging;
using GameInterface.Services.WorkshopMods.Core;
using GameInterface.Services.Barters;
using HarmonyLib;
using System;
using System.Collections;
using Serilog;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TaleWorlds.Core;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
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

/// <summary>Client presentation entry points for the unified Diplomacy command route.</summary>
[HarmonyPatch]
[HarmonyPatchCategory(WorkshopPatchCategories.Diplomacy)]
internal static class DiplomacyPlayerActionRoutingPatch
{
    private static readonly IReadOnlyDictionary<string, string[]> EntryPoints =
        new Dictionary<string, string[]>(System.StringComparer.Ordinal)
        {
            ["Diplomacy.ViewModel.GrantFiefVM"] = new[] { "OnGrantFief" },
            ["Diplomacy.ViewModelMixin.EncyclopediaHeroPageVMMixin"] = new[] { "SendMessenger" },
            ["Diplomacy.ViewModelMixin.KingdomWarItemVMMixin"] = new[] { "ExecuteDirectAction" },
            ["Diplomacy.ViewModelMixin.KingdomTruceItemVMMixin"] = new[]
            {
                "ExecuteDirectAction",
                "ProposeNonAggressionPact",
            },
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
    private static bool Prefix(object __instance, MethodBase __originalMethod)
    {
        if (!ModInformation.IsClient || __instance == null) return false;
        string type = __originalMethod?.DeclaringType?.FullName ?? string.Empty;
        string method = __originalMethod?.Name ?? string.Empty;
        DiplomacyLocalOperation operation = null;

        if (type == "Diplomacy.ViewModel.GrantFiefVM")
        {
            object selected = AccessTools.Property(__instance.GetType(), "SelectedSettlementItem")?.GetValue(__instance);
            var settlement = selected == null
                ? null
                : AccessTools.Property(selected.GetType(), "Settlement")?.GetValue(selected) as Settlement;
            var targetHero = AccessTools.Field(__instance.GetType(), "_targetHero")?.GetValue(__instance) as Hero;
            if (targetHero?.Clan != null && settlement != null)
                operation = new DiplomacyLocalOperation(
                    DiplomacyOperation.GrantFief, targetHero.Clan, settlement);
            (AccessTools.Field(__instance.GetType(), "_onComplete")?.GetValue(__instance) as Action)?.Invoke();
        }
        else if (type == "Diplomacy.ViewModelMixin.EncyclopediaHeroPageVMMixin")
        {
            var hero = AccessTools.Field(__instance.GetType(), "_hero")?.GetValue(__instance) as Hero;
            if (hero != null)
                operation = new DiplomacyLocalOperation(DiplomacyOperation.SendMessenger, hero);
        }
        else
        {
            var first = AccessTools.Field(__instance.GetType(), "_faction1")?.GetValue(__instance) as Kingdom;
            var second = AccessTools.Field(__instance.GetType(), "_faction2")?.GetValue(__instance) as Kingdom;
            if (first != null && second != null)
            {
                DiplomacyOperation kind;
                if (type == "Diplomacy.ViewModelMixin.KingdomWarItemVMMixin")
                    kind = DiplomacyOperation.MakePeace;
                else if (method == "ProposeNonAggressionPact")
                    kind = DiplomacyOperation.FormNonAggressionPact;
                else
                    kind = first.IsAllyWith(second)
                        ? DiplomacyOperation.EndAlliance
                        : DiplomacyOperation.DeclareWar;
                operation = new DiplomacyLocalOperation(kind, first, second);
            }
        }

        if (operation == null || DiplomacyPatchRuntime.Current?.TrySubmit(operation) != true)
            InformationManager.DisplayMessage(new InformationMessage(
                "The Diplomacy action is not available until the co-op authority handshake is complete."));
        return false;
    }
}

/// <summary>
/// Converts the server's settlement-capture callback into a prompt for the controller who led the
/// final assault. Accept/decline returns through the same authenticated command protocol.
/// </summary>
[HarmonyPatch]
[HarmonyPatchCategory(WorkshopPatchCategories.Diplomacy)]
internal static class DiplomacyKeepFiefCallbackRoutingPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        var type = DiplomacyCompatibilityPolicy.ResolveType(
            "Diplomacy.CampaignBehaviors.KeepFiefAfterSiegeBehavior");
        var method = type == null ? null : AccessTools.Method(type, "OnPlayerSettlementTaken");
        if (method != null) yield return method;
    }

    [HarmonyPrepare]
    private static bool Prepare() => TargetMethods().Any();

    [HarmonyPrefix]
    private static bool Prefix(Settlement settlement)
    {
        if (ModInformation.IsServer) DiplomacyPatchRuntime.Current?.TryPromptKeepFief(settlement);
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
/// Coop owns messenger travel, persistence, and accidents. The pinned manager remains available
/// only as a presentation helper for the authorized client's inquiry and conversation mission.
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
    private static bool Prefix() =>
        DiplomacyCompatibilityPolicy.ShouldRunOriginalMessengerBehavior();
}

/// <summary>
/// Keep-fief capture detection is a server callback; its inquiry is routed to the winning
/// controller by <see cref="DiplomacyKeepFiefCallbackRoutingPatch"/>.
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
    private static bool Prefix() => ModInformation.IsServer;
}

/// <summary>
/// Diplomacy pacts and peace execute only on the campaign server. Explicit player commands enter
/// with a verified context; automated server callbacks receive the proposing kingdom leader as a
/// temporary player context so singleton reads cannot resolve to an arbitrary client.
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
    private static bool Prefix(
        MethodBase __originalMethod,
        object[] __args,
        ref DiplomacyAutomatedKingdomActionContext __state)
    {
        bool explicitOperation = DiplomacyExplicitOperationScope.IsAllowed;
        bool automatedOperation = DiplomacyAutomatedOperationScope.IsAllowed;
        if (!ModInformation.IsServer) return false;
        if (explicitOperation || automatedOperation) return true;

        var proposing = __args?.OfType<Kingdom>().FirstOrDefault();
        var actor = proposing?.Leader;
        if (!ShouldAllowKingdomAction(
                isServer: true,
                explicitOperation,
                automatedOperation,
                hasProposingLeader: actor != null && actor.Clan?.Kingdom == proposing))
        {
            string method = $"{__originalMethod?.DeclaringType?.FullName}.{__originalMethod?.Name}";
            if (LoggedMethods.Add(method))
                Logger.Warning("Refused Diplomacy server action {Method} without a proposing kingdom leader.", method);
            return false;
        }

        __state = new DiplomacyAutomatedKingdomActionContext(actor);
        return true;
    }

    [HarmonyFinalizer]
    private static Exception Finalizer(Exception __exception, DiplomacyAutomatedKingdomActionContext __state)
    {
        __state?.Dispose();
        return __exception;
    }

    internal static bool ShouldAllowKingdomAction(
        bool isServer,
        bool explicitOperation,
        bool automatedOperation,
        bool hasProposingLeader) =>
        isServer && (explicitOperation || automatedOperation || hasProposingLeader);
}

internal sealed class DiplomacyAutomatedKingdomActionContext : IDisposable
{
    private readonly BarterPlayerContext playerContext;
    private readonly IDisposable operationScope;

    public DiplomacyAutomatedKingdomActionContext(Hero actor)
    {
        playerContext = new BarterPlayerContext(actor, actor?.PartyBelongedTo);
        operationScope = DiplomacyAutomatedOperationScope.Enter();
    }

    public void Dispose()
    {
        operationScope.Dispose();
        playerContext.Dispose();
    }
}

/// <summary>
/// Headless authoritative peace must never wait on a process-local inquiry. The outer guarded
/// action computes the pinned mod's exact costs, tribute, returned fiefs, and elimination result;
/// this sink commits those values directly only while an authenticated command or derived server
/// callback scope is active.
/// </summary>
[HarmonyPatch]
[HarmonyPatchCategory(WorkshopPatchCategories.Diplomacy)]
internal static class DiplomacyPeaceInquiryAuthorityPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        var type = DiplomacyCompatibilityPolicy.ResolveType(
            "Diplomacy.DiplomaticAction.WarPeace.KingdomPeaceAction");
        var method = type == null
            ? null
            : AccessTools.Method(type, "ApplyPeaceInternal");
        if (method != null) yield return method;
    }

    [HarmonyPrepare]
    private static bool Prepare() => TargetMethods().Any();

    [HarmonyPrefix]
    private static bool Prefix(object[] __args)
    {
        if (!ShouldAcceptWithoutInquiry(
                ModInformation.IsServer,
                DiplomacyExplicitOperationScope.IsAllowed,
                DiplomacyAutomatedOperationScope.IsAllowed))
            return false;
        if (__args == null || __args.Length != 9)
            throw new InvalidOperationException("Diplomacy ApplyPeaceInternal shape changed after compatibility validation.");

        Type type = DiplomacyCompatibilityPolicy.ResolveType(
            "Diplomacy.DiplomaticAction.WarPeace.KingdomPeaceAction") ??
                    throw new TypeLoadException("Diplomacy KingdomPeaceAction is unavailable.");
        MethodInfo accept = type.GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Single(method => method.Name == "AcceptPeace" && method.GetParameters().Length == 6);
        accept.Invoke(null, new[]
        {
            __args[0], __args[1], __args[4], __args[5], __args[6], __args[8],
        });
        return false;
    }

    internal static bool ShouldAcceptWithoutInquiry(
        bool isServer,
        bool explicitOperation,
        bool automatedOperation) =>
        isServer && (explicitOperation || automatedOperation);
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
