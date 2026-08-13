using Common;
using GameInterface.Services;
using HarmonyLib;
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
    internal static readonly IReadOnlyList<string> SnapshotGatedUiTypeNames = new[]
    {
        "Diplomacy.ViewModelMixin.DiplomacyPanelPrefabExtension",
        "Diplomacy.ViewModelMixin.KingdomDiplomacyVMMixin",
        "Diplomacy.ViewModelMixin.KingdomWarItemVMMixin",
        "Diplomacy.ViewModelMixin.KingdomTruceItemVMMixin",
        "Diplomacy.ViewModelMixin.KingdomClanVMMixin",
        "Diplomacy.ViewModelMixin.EncyclopediaHeroPagePrefabExtension",
        "Diplomacy.ViewModelMixin.EncyclopediaHeroPageVMMixin",
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

    private readonly Action<string, bool> applyUiType;

    public bool IsReady { get; private set; }

    public DiplomacyClientUiLifecycle()
        : this(ApplyUiType)
    {
    }

    internal DiplomacyClientUiLifecycle(Action<string, bool> applyUiType)
    {
        this.applyUiType = applyUiType ?? throw new ArgumentNullException(nameof(applyUiType));
    }

    public void ResetForCampaign()
    {
        IsReady = ModInformation.IsServer;
        if (ModInformation.IsServer) return;

        var failures = new List<Exception>();
        DisableAll(SnapshotGatedUiTypeNames, failures);
        DisableAll(PermanentlyRetiredUiTypeNames, failures);
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
            foreach (string typeName in PermanentlyRetiredUiTypeNames)
                applyUiType(typeName, false);
            foreach (string typeName in SnapshotGatedUiTypeNames)
                applyUiType(typeName, true);

            IsReady = true;
            return true;
        }
        catch (Exception ex)
        {
            var rollbackFailures = new List<Exception>();
            DisableAll(SnapshotGatedUiTypeNames, rollbackFailures);
            IsReady = false;
            failure = ex.GetBaseException().Message;
            if (rollbackFailures.Count > 0)
                failure += " UI rollback also failed: " + string.Join("; ", rollbackFailures.Select(value => value.GetBaseException().Message));
            return false;
        }
    }

    private void DisableAll(IEnumerable<string> typeNames, ICollection<Exception> failures)
    {
        foreach (string typeName in typeNames)
        {
            try
            {
                applyUiType(typeName, false);
            }
            catch (Exception ex)
            {
                failures.Add(ex);
            }
        }
    }

    private static void ApplyUiType(string typeName, bool enabled)
    {
        Type extenderType = AccessTools.TypeByName("Bannerlord.UIExtenderEx.UIExtender") ??
                            throw new TypeLoadException("Bannerlord.UIExtenderEx.UIExtender");
        MethodInfo getExtender = AccessTools.Method(
            extenderType,
            "GetUIExtenderFor",
            new[] { typeof(string) }) ??
                                 throw new MissingMethodException(extenderType.FullName, "GetUIExtenderFor");
        object extender = getExtender.Invoke(null, new object[] { "Diplomacy" }) ??
                          throw new InvalidOperationException("Diplomacy UIExtender runtime was not registered.");
        MethodInfo transition = AccessTools.Method(
            extenderType,
            enabled ? "Enable" : "Disable",
            new[] { typeof(Type) }) ??
                                throw new MissingMethodException(extenderType.FullName, enabled ? "Enable(Type)" : "Disable(Type)");
        Type uiType = DiplomacyCompatibilityPolicy.ResolveType(typeName) ?? throw new TypeLoadException(typeName);
        transition.Invoke(extender, new object[] { uiType });
    }
}
