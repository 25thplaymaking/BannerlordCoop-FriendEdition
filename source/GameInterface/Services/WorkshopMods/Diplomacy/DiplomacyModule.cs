using GameInterface.AutoSync;
using GameInterface.Services.WorkshopMods.Core;

namespace GameInterface.Services.WorkshopMods.Diplomacy;

/// <summary>
/// Diplomacy's declaration — the first module expressed through <see cref="IWorkshopModule"/>, and
/// the shape every following mod copies. Fingerprint values come from
/// <see cref="DiplomacyCompatibilityPolicy"/> so the pin has exactly one source of truth.
/// </summary>
internal sealed class DiplomacyModule : IWorkshopModule
{
    // Built once, at type initialization, so the pin is validated at a single deterministic point
    // during container build rather than on whichever access happens to come first.
    private static readonly ModuleFingerprint PinnedFingerprint = new ModuleFingerprint(
        DiplomacyCompatibilityPolicy.SupportedAssemblyName,
        DiplomacyCompatibilityPolicy.SupportedAssemblyVersion,
        DiplomacyCompatibilityPolicy.SupportedAssemblySha256);

    public string ModuleId => "Bannerlord.Diplomacy";

    public ulong WorkshopId => 2881380744UL;

    public ModuleFingerprint Fingerprint => PinnedFingerprint;

    public string PatchCategory => WorkshopPatchCategories.Diplomacy;

    /// <summary>
    /// Delegated to the compatibility policy rather than measured independently. ResolveAssembly()
    /// returns non-null only after it has hashed the loaded DLL and compared it, in fixed time,
    /// against this same constant — and after confirming every type and method shape the adapters
    /// patch. So the constant IS the measured digest here, and the answer this module gives the
    /// registrar is exactly the answer the adapters' own [HarmonyPrepare] guards will give.
    /// Deriving it from a second, weaker probe is how the two drift apart and a category gets
    /// registered for patches that cannot bind.
    /// </summary>
    public string ResolveInstalledSha256() =>
        DiplomacyCompatibilityPolicy.ResolveAssembly() == null
            ? null
            : DiplomacyCompatibilityPolicy.SupportedAssemblySha256;

    /// <summary>
    /// Deliberately empty — because the state already has an owner, NOT because AutoSync could not
    /// carry it. The distinction matters for the mods that follow this one.
    /// <para>
    /// <b>The reason.</b> <see cref="DiplomacyRuntime"/> already captures and applies every candidate
    /// — expansionism, all three cooldown dictionaries, non-aggression agreements and the
    /// war-exhaustion score/rate tables — as one server-authoritative, revisioned, validated snapshot
    /// with its own join handshake. Registering the same members here would put a second independent
    /// writer on the same campaign state, and two replication paths over one dictionary diverge
    /// nondeterministically. Server-authoritative ownership already held is the disqualifier.
    /// </para>
    /// <para>
    /// <b>Not the reason: "AutoSync cannot do dictionaries."</b> It can, and it would accept
    /// <c>CooldownManager</c>'s three: <c>Dictionary&lt;string, CampaignTime&gt; _lastWarTime</c>,
    /// <c>Dictionary&lt;Kingdom, CampaignTime&gt; _lastPeaceProposalTime</c> and
    /// <c>Dictionary&lt;string, CampaignTime&gt; _lastAllianceFormedTime</c>.
    /// <c>AutoSyncDictionaryBuilderBase.ValidateSyncable</c> admits a type that is protobuf
    /// serializable by value or managed by a registry: <c>string</c> is the former,
    /// <c>CampaignTime</c> has a surrogate in <c>SurrogateCollection</c>, and <c>Kingdom</c> is
    /// registry-managed. Do not read this empty method as evidence that mod dictionaries are out of
    /// reach.
    /// </para>
    /// <para>
    /// <b>Where AutoSync genuinely stops.</b> On the composite state, which sits on different types:
    /// <c>WarExhaustionManager</c>'s <c>Dictionary&lt;string, WarExhaustionRecord&gt;</c> and
    /// <c>Dictionary&lt;string, List&lt;WarExhaustionEventRecord&gt;&gt;</c>, and
    /// <c>DiplomaticAgreementManager</c>'s
    /// <c>Dictionary&lt;FactionPair, List&lt;DiplomaticAgreement&gt;&gt;</c>. Those key and value
    /// types are Diplomacy's own, with no surrogate and no registry, so ValidateSyncable rejects them
    /// by design — which is exactly why those managers have a snapshot codec.
    /// </para>
    /// <para>
    /// <b>And there is no spare member on the behaviour anyway.</b> Decompiling
    /// Bannerlord.Diplomacy 1.4.7 shows <c>CooldownBehavior</c> holds one field,
    /// <c>_cooldownManager</c>, assigned in the constructor and again while a save loads, never
    /// during play. The plan's illustrative <c>_lastWarDeclaredTime</c> /
    /// <c>_lastPeaceProposalTime</c> fields do not exist on that type.
    /// </para>
    /// <para>
    /// An honest empty registration is the correct declaration here; it is not a gap left for later.
    /// </para>
    /// </summary>
    public void RegisterSync(AutoSyncRegistry registry)
    {
    }
}
