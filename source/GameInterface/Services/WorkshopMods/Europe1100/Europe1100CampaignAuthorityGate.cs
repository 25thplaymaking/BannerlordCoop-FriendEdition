using Common;
using Common.Logging;
using HarmonyLib;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TaleWorlds.CampaignSystem;

namespace GameInterface.Services.WorkshopMods.Europe1100;

/// <summary>
/// Confines the Empires of Europe 1100 conversion's third-party campaign behaviours to the
/// authoritative host.
/// </summary>
/// <remarks>
/// Coop owns campaign simulation on the server and replicates the result. It has no blanket
/// suppression for campaign behaviours: every native AI behaviour that must not run on a replica
/// carries its own <c>Prefix() =&gt; ModInformation.IsServer</c> on <c>RegisterEvents</c> (see the
/// 145 classes under <c>Services/*/Patches/Disable</c>). A behaviour nobody gated ticks on BOTH
/// peers, so each side independently decides kingdom wars, clan wealth or party spawns and the
/// host's authoritative state then overwrites the replica's — churn, and the divergence class
/// behind most desyncs.
/// <para>
/// The EoE modules are deliberately absent from <see cref="Core.FriendEditionWorkshopModuleCatalog"/>:
/// <c>WorkshopSuiteReceipt.TryValidate</c> locks the catalog and the packaging receipt 1:1, so a
/// catalogued conversion would add eight fatal join gates each and force a 4.9 GB suite payload.
/// That keeps them invisible to the join handshake, which is exactly why their authority has to be
/// enforced here instead.
/// </para>
/// <para>
/// This gate therefore patches by TYPE NAME through reflection — Coop references none of these
/// assemblies — and is installed imperatively rather than through a Harmony category, the same
/// route Improved Garrisons, Fourberie and Player Settlement take. Nothing resolves when the
/// conversion is not installed, and the gate simply installs no patches.
/// </para>
/// <para>
/// A resolution failure is logged and skipped rather than thrown. These modules are optional
/// content that the handshake does not police; refusing to build the session because an unpinned
/// content mod changed shape would be a worse failure than the churn the gate prevents. Every skip
/// is logged at Error so a silently ungated behaviour is still visible in the host log.
/// </para>
/// </remarks>
internal sealed class Europe1100CampaignAuthorityGate
{
    internal const string AdapterHarmonyId = "Bannerlord.Coop.Workshop.Europe1100";
    private const string RegisterEventsMethod = "RegisterEvents";

    private static readonly ILogger Logger = LogManager.GetLogger<Europe1100CampaignAuthorityGate>();

    /// <summary>
    /// Campaign behaviours the replica must not tick, keyed by the assembly that declares them.
    /// Verified against the shipped CLI metadata of the Europe1100 v1.4.7.3 and
    /// SnowballingKingdoms v1.0.21 packages.
    /// </summary>
    /// <remarks>
    /// Re-verified against the shipped SubModule.xml and binaries on 2026-08-19, which corrected two
    /// earlier assumptions:
    /// <list type="bullet">
    /// <item><c>WhileThyCome</c> IS declared as a submodule — an earlier note claimed it was not and
    /// that its behaviours were listed only defensively. It loads. Listing them was not optional.</item>
    /// <item><c>EOE.CustomBattlePatch</c> is now gated too. It was excluded on the grounds that its
    /// submodule is tagged <c>DedicatedServerType="none"</c> and kept off a dedicated allowlist —
    /// but the SubModule.xml carries no <c>DedicatedServerType</c> attribute on any of its nine
    /// submodules, and no such allowlist exists. Its <c>EoeCustomBattleCampaignBehavior</c> derives
    /// from <c>CampaignBehaviorBase</c> like the rest, so it is confined to the host like the rest.
    /// If it really is inert in a campaign then gating it costs nothing; the exclusion rested on an
    /// assumption that turned out not to hold.</item>
    /// </list>
    /// <para>
    /// The four assemblies here are exactly those that reference <c>CampaignBehaviorBase</c> across the
    /// Europe1100 and SnowballingKingdoms runtimes, and every assembly referencing
    /// <c>CampaignEvents</c> also declares one — so no behaviour subscribes to campaign events outside
    /// this list.
    /// </para>
    /// </remarks>
    internal static readonly IReadOnlyDictionary<string, string[]> HostOnlyCampaignBehaviors =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["SnowballingKingdoms"] = new[]
            {
                "SnowballingKingdoms.SnowballEvents",
                "SnowballingKingdoms.SnowballFixesBehavior",
            },
            ["ClansResourceAdder"] = new[]
            {
                "ClansResourceAdder.ResourcesAdderEvents",
            },
            ["BattleArtilleryReworked"] = new[]
            {
                "BattleArtilleryReworked.BACampaignBehavior",
            },
            ["EOE.CustomBattlePatch"] = new[]
            {
                "EOE.CustomBattlePatch.SinglePlayer.EoeCustomBattleCampaignBehavior",
            },
            ["WhileThyCome"] = new[]
            {
                "WhileThyCome.Patches.WTCDiplomaticBartersBehavior",
                "WhileThyCome.BehaviorBase.AggresiveBehaviour",
                "WhileThyCome.BehaviorBase.PartyBehaviour",
                "WhileThyCome.BehaviorBase.ProvisionBehaviour",
                "WhileThyCome.BehaviorBase.SharedDataAmongBehaviours",
                "WhileThyCome.BehaviorBase.SpawnBehaviour",
            },
        };

    private readonly Harmony harmony;

    public Europe1100CampaignAuthorityGate()
        : this(new Harmony(AdapterHarmonyId))
    {
    }

    internal Europe1100CampaignAuthorityGate(Harmony harmony)
    {
        this.harmony = harmony ?? throw new ArgumentNullException(nameof(harmony));
        Install();
    }

    /// <summary>Behaviours whose RegisterEvents this gate actually took over, for diagnostics and tests.</summary>
    internal List<string> GatedBehaviors { get; } = new List<string>();

    private void Install()
    {
        HarmonyMethod prefix = new HarmonyMethod(AccessTools.Method(
            typeof(Europe1100CampaignAuthorityGate), nameof(HostOnlyRegisterEventsPrefix)));

        foreach (KeyValuePair<string, string[]> declaration in HostOnlyCampaignBehaviors)
        {
            Assembly assembly = ResolveLoadedAssembly(declaration.Key);
            if (assembly == null) continue;

            foreach (string typeName in declaration.Value)
                GateOne(assembly, typeName, prefix);
        }

        if (GatedBehaviors.Count > 0)
        {
            Logger.Information(
                "[EoE1100] Confined {Count} conversion campaign behaviours to the host: {Behaviors}",
                GatedBehaviors.Count,
                string.Join(", ", GatedBehaviors));
        }
    }

    private void GateOne(Assembly assembly, string typeName, HarmonyMethod prefix)
    {
        try
        {
            Type behavior = assembly.GetType(typeName, throwOnError: false, ignoreCase: false);
            if (behavior == null)
            {
                // Absent is normal: the package ships assemblies whose submodules it never declares.
                Logger.Debug("[EoE1100] '{TypeName}' is not present in {Assembly}; nothing to gate.",
                    typeName, assembly.GetName().Name);
                return;
            }

            // Shape check before patching. A same-named type that is not a campaign behaviour would
            // mean the package was replaced by something this gate has never been read against, and
            // patching it blind could suppress an unrelated method.
            if (!typeof(CampaignBehaviorBase).IsAssignableFrom(behavior))
            {
                Logger.Error(
                    "[EoE1100] '{TypeName}' is not a CampaignBehaviorBase; refusing to gate it. " +
                    "The conversion package does not match the audited build and this behaviour " +
                    "will run on every peer.",
                    typeName);
                return;
            }

            MethodInfo registerEvents = AccessTools.DeclaredMethod(behavior, RegisterEventsMethod);
            if (registerEvents == null)
            {
                Logger.Error(
                    "[EoE1100] '{TypeName}' declares no {Method} override; it cannot be gated and " +
                    "will run on every peer.",
                    typeName, RegisterEventsMethod);
                return;
            }

            harmony.Patch(registerEvents, prefix: prefix);
            GatedBehaviors.Add(typeName);
        }
        catch (Exception exception)
        {
            // Never abort the container build for optional content. See the class remarks.
            Logger.Error(exception,
                "[EoE1100] Failed to gate '{TypeName}'; it will run on every peer.", typeName);
        }
    }

    private static Assembly ResolveLoadedAssembly(string simpleName) =>
        AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(candidate =>
            string.Equals(candidate.GetName().Name, simpleName, StringComparison.Ordinal));

    /// <summary>
    /// The same shape the 145 native <c>Disable*</c> patches use: the host subscribes its campaign
    /// events, the replica skips subscription and receives the resulting state over the wire.
    /// </summary>
    private static bool HostOnlyRegisterEventsPrefix() => ModInformation.IsServer;
}
