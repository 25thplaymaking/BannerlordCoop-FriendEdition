using Common;
using Common.Logging;
using GameInterface.Services;
using HarmonyLib;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace GameInterface.Services.WorkshopMods.Diplomacy;

internal interface IDiplomacyClientUiLifecycle : IGameAbstraction
{
    bool IsReady { get; }
    void ResetForCampaign();
    bool TryMarkSnapshotReady(out string failure);
}

/// <summary>
/// Owns the client visibility of every Diplomacy UIExtender surface that reads authoritative state.
/// The surfaces are disabled before Diplomacy starts a campaign and are enabled as one fail-closed
/// group only after Coop has applied and committed the trusted host snapshot.
/// </summary>
internal sealed class DiplomacyClientUiLifecycle : IDiplomacyClientUiLifecycle
{
    private static readonly ILogger Logger = LogManager.GetLogger<DiplomacyClientUiLifecycle>();
    internal static readonly IReadOnlyList<string> SnapshotGatedUiTypeNames = new[]
    {
        "Diplomacy.ViewModelMixin.DiplomacyPanelPrefabExtension",
        "Diplomacy.ViewModelMixin.KingdomDiplomacyVMMixin",
        "Diplomacy.ViewModelMixin.KingdomWarItemVMMixin",
        "Diplomacy.ViewModelMixin.KingdomTruceItemVMMixin",
        "Diplomacy.ViewModelMixin.KingdomClanVMMixin",
        "Diplomacy.ViewModelMixin.EncyclopediaHeroPagePrefabExtension",
        "Diplomacy.ViewModelMixin.EncyclopediaHeroPageVMMixin",
        "Diplomacy.ViewModelMixin.PartyNameplateVMMixin",
        "Diplomacy.ViewModelMixin.PlayerPartyNameplateVMMixin",
        "Diplomacy.ViewModelMixin.SettlementNameplatesVMMixin",
    };

    internal static readonly IReadOnlyList<string> PermanentlyRetiredUiTypeNames = new[]
    {
        "Diplomacy.ViewModelMixin.KingdomManagementPrefabExtension",
        "Diplomacy.ViewModelMixin.KingdomManagementScalingPatch",
        "Diplomacy.ViewModelMixin.KingdomManagementVMMixin",
        "Diplomacy.ViewModelMixin.FactionsButtonExtension",
        "Diplomacy.ViewModelMixin.EncyclopediaFactionPagePrefabExtension",
        "Diplomacy.ViewModelMixin.EncyclopediaFactionPageVMMixin",
    };

    private readonly Func<string, Type> resolveUiType;
    private readonly Action<Type, bool> applyUiType;
    private readonly Action<bool> applyWholeUi;
    private IReadOnlyDictionary<string, Type> resolvedUiTypes;

    public bool IsReady { get; private set; }

    public DiplomacyClientUiLifecycle()
        : this(DiplomacyCompatibilityPolicy.ResolveType, ApplyUiType, ApplyWholeUi)
    {
    }

    internal DiplomacyClientUiLifecycle(
        Func<string, Type> resolveUiType,
        Action<Type, bool> applyUiType,
        Action<bool> applyWholeUi)
    {
        this.resolveUiType = resolveUiType ?? throw new ArgumentNullException(nameof(resolveUiType));
        this.applyUiType = applyUiType ?? throw new ArgumentNullException(nameof(applyUiType));
        this.applyWholeUi = applyWholeUi ?? throw new ArgumentNullException(nameof(applyWholeUi));
    }

    public void ResetForCampaign()
    {
        IsReady = ModInformation.IsServer;
        if (ModInformation.IsServer) return;

        // Disable the registered extension as one operation before resolving any implementation
        // type. This is the fail-closed boundary: even a loader/candidate failure below cannot leave
        // a Diplomacy mixin active while campaign state is still waiting on the host snapshot.
        applyWholeUi(false);

        var failures = new List<Exception>();
        try
        {
            ResolveUiTypes();
            Logger.Information(
                "Diplomacy UI lifecycle reset: disabled all extensions and resolved {Count} exact UI types from {Assembly}",
                resolvedUiTypes.Count,
                resolvedUiTypes.Values.First().Assembly.FullName);
        }
        catch (Exception ex)
        {
            failures.Add(ex);
        }
        if (failures.Count > 0)
            throw new AggregateException("Diplomacy UI could not be disabled before campaign startup.", failures);
    }

    public bool TryMarkSnapshotReady(out string failure)
    {
        failure = null;
        if (ModInformation.IsServer)
        {
            IsReady = true;
            return true;
        }

        if (IsReady) return true;

        try
        {
            ResolveUiTypes();
            foreach (string typeName in PermanentlyRetiredUiTypeNames)
                applyUiType(resolvedUiTypes[typeName], false);
            foreach (string typeName in SnapshotGatedUiTypeNames)
                applyUiType(resolvedUiTypes[typeName], true);

            IsReady = true;
            Logger.Information(
                "Diplomacy UI lifecycle ready: enabled {EnabledCount} snapshot-backed extensions; " +
                "kept {RetiredCount} retired extensions disabled",
                SnapshotGatedUiTypeNames.Count,
                PermanentlyRetiredUiTypeNames.Count);
            return true;
        }
        catch (Exception ex)
        {
            Exception rollbackFailure = null;
            try
            {
                applyWholeUi(false);
            }
            catch (Exception rollback)
            {
                rollbackFailure = rollback;
            }
            IsReady = false;
            failure = ex.GetBaseException().Message;
            if (rollbackFailure != null)
                failure += " UI rollback also failed: " + rollbackFailure.GetBaseException().Message;
            Logger.Error(
                "Diplomacy UI lifecycle activation failed and was rolled back: {Failure}",
                failure);
            return false;
        }
    }

    private void ResolveUiTypes()
    {
        if (resolvedUiTypes != null) return;

        var resolved = new Dictionary<string, Type>(StringComparer.Ordinal);
        foreach (string typeName in SnapshotGatedUiTypeNames.Concat(PermanentlyRetiredUiTypeNames))
        {
            Type type = resolveUiType(typeName) ?? throw new TypeLoadException(
                typeName + ". " + DiplomacyCompatibilityPolicy.DescribeResolutionFailure());
            resolved[typeName] = type;
        }

        resolvedUiTypes = resolved;
    }

    private static object ResolveExtender(out Type extenderType)
    {
        extenderType = AccessTools.TypeByName("Bannerlord.UIExtenderEx.UIExtender") ??
                       throw new TypeLoadException("Bannerlord.UIExtenderEx.UIExtender");
        MethodInfo getExtender = AccessTools.Method(
            extenderType,
            "GetUIExtenderFor",
            new[] { typeof(string) }) ??
                                 throw new MissingMethodException(extenderType.FullName, "GetUIExtenderFor");
        return getExtender.Invoke(null, new object[] { "Diplomacy" }) ??
               throw new InvalidOperationException("Diplomacy UIExtender runtime was not registered.");
    }

    private static void ApplyWholeUi(bool enabled)
    {
        object extender = ResolveExtender(out Type extenderType);
        MethodInfo transition = AccessTools.Method(
            extenderType,
            enabled ? "Enable" : "Disable",
            Type.EmptyTypes) ??
                                throw new MissingMethodException(extenderType.FullName, enabled ? "Enable()" : "Disable()");
        transition.Invoke(extender, Array.Empty<object>());
    }

    private static void ApplyUiType(Type uiType, bool enabled)
    {
        object extender = ResolveExtender(out Type extenderType);
        MethodInfo transition = AccessTools.Method(
            extenderType,
            enabled ? "Enable" : "Disable",
            new[] { typeof(Type) }) ??
                                throw new MissingMethodException(extenderType.FullName, enabled ? "Enable(Type)" : "Disable(Type)");
        transition.Invoke(extender, new object[] { uiType });
    }
}
