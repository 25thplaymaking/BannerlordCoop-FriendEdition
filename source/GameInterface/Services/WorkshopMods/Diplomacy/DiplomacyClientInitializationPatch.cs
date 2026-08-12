using Common;
using Common.Logging;
using HarmonyLib;
using SandBox.View.Map;
using Serilog;
using System;
using System.Reflection;

namespace GameInterface.Services.WorkshopMods.Diplomacy;

/// <summary>
/// Ensures Diplomacy's client-side singletons are initialized before the co-op client builds
/// the campaign map.
///
/// Diplomacy creates its <c>DiplomacyEvents</c> singleton in <c>SubModule.OnGameStart</c>
/// (<c>DiplomacyEvents.Instance = new DiplomacyEvents()</c>), and its UIExtenderEx view-model
/// mixins register on that singleton in their constructors (e.g.
/// <c>PartyNameplateVMMixin</c> → <c>DiplomacyEvents.KingdomBannerChanged.AddNonSerializedListener</c>).
/// Coop's character-creation → map handoff can reach <c>MapScreen.OnInitialize</c> — which builds
/// the party nameplates, and therefore those mixins — before <c>OnGameStart</c> has created the
/// singleton, so the mixin constructor dereferences a null <c>Instance</c> and takes down map
/// initialization. Rather than swallow that, we do the initialization the mod expects: create the
/// singleton (idempotently) up front so every Diplomacy event listener wires correctly.
///
/// This initializes only Diplomacy's event plumbing — pure client-side notification objects with
/// no authoritative campaign state — so it is safe under Coop's server-authoritative model, which
/// continues to gate the mutating behaviours through <see cref="DiplomacySharedMutationAuthorityPatch"/>.
///
/// The Diplomacy type is resolved lazily inside the prefix (at map-init time, when Diplomacy is
/// definitely loaded) rather than in a static field or <c>[HarmonyPrepare]</c> gate: Coop loads
/// before Diplomacy in the module order, so any type lookup at patch-scan time would resolve to
/// null and make the patch inert. Patching the always-present <c>MapScreen.OnInitialize</c>
/// unconditionally avoids that ordering trap.
/// </summary>
[HarmonyPatch]
internal static class DiplomacyClientInitializationPatch
{
    private static readonly ILogger Logger = LogManager.GetLogger(typeof(DiplomacyClientInitializationPatch));

    private static Type _diplomacyEventsType;
    private static bool _resolved;

    [HarmonyPatch(typeof(MapScreen), nameof(MapScreen.OnInitialize))]
    [HarmonyPrefix]
    private static void EnsureDiplomacyClientSingletonsInitialized()
    {
        EnsureDiplomacyEvents();
        EnsureAgreementManager();
    }

    private static void EnsureDiplomacyEvents()
    {
        try
        {
            // Resolve once, lazily, at runtime — Diplomacy is loaded by the time a map initializes.
            if (!_resolved)
            {
                _diplomacyEventsType = AccessTools.TypeByName("Diplomacy.Events.DiplomacyEvents");
                _resolved = true;
            }

            Type type = _diplomacyEventsType;
            if (type == null) return; // Diplomacy not installed this session.

            PropertyInfo instanceProperty = AccessTools.Property(type, "Instance");
            if (instanceProperty == null || instanceProperty.GetValue(null) != null) return;

            // The public parameterless constructor assigns DiplomacyEvents.Instance = this and
            // builds all of its MbEvent<T> instances, so the mixins' listener registration works.
            Activator.CreateInstance(type);
            Logger.Debug("Initialized Diplomacy DiplomacyEvents singleton before co-op map build");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to pre-initialize Diplomacy DiplomacyEvents singleton");
        }
    }

    /// <summary>
    /// The co-op client only creates <c>DiplomaticAgreementManager</c> when the join-handshake Diplomacy
    /// snapshot is applied (<c>DiplomacyRuntime.ApplySnapshot</c> → <c>EnsureManager</c>). Until then its
    /// <c>Instance</c> is null, and Diplomacy's encyclopedia faction mixin dereferences it unguarded
    /// (<c>HasNonAggressionPact</c>) — clicking a kingdom link before the snapshot lands NREs out of the
    /// page VM and, via the screen-tick abort, bricks the map UI. Pre-create the manager (empty and
    /// idempotently, reusing the same path the snapshot uses) at map build on the client so the read
    /// always finds a live, empty manager; the subsequent snapshot repopulates its agreements onto that
    /// same instance. Server-side Diplomacy behaviours create it themselves, so this is client-only.
    /// The <c>EncyclopediaFactionPageReadinessPatch</c> finalizer remains as the belt-and-braces net.
    /// </summary>
    private static void EnsureAgreementManager()
    {
        if (!ModInformation.IsClient) return;
        try
        {
            DiplomacyRuntime.EnsureManager("Diplomacy.DiplomaticAction.DiplomaticAgreementManager");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to pre-initialize Diplomacy DiplomaticAgreementManager on client");
        }
    }
}
