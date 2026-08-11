using GameInterface.Services.WorkshopMods.Core;

namespace GameInterface.Services.WorkshopMods.Fourberie;

/// <summary>Why a routed Fourberie create-action was or was not applied.</summary>
public enum FourberieCreateActionVerdict
{
    Applied,

    /// <summary>The method is not on the routed allow-list — refused.</summary>
    NotAllowed,

    /// <summary>Fourberie / the target method is not loaded.</summary>
    ModuleNotInstalled,

    /// <summary>The mod routine threw; nothing may be assumed about resulting state.</summary>
    ApplyFailed,
}

/// <summary>
/// Legacy request boundary retained so older clients receive a deterministic refusal. The audited
/// static Fourberie APIs have no requester parameter and cannot safely execute for a remote peer.
/// </summary>
public interface IFourberieCreateActionInterface : IGameAbstraction
{
    FourberieCreateActionVerdict TryRun(string declaringTypeName, string methodName, int arg);
}

/// <summary>
/// Refuses the legacy routed create-actions. Although the server can authenticate the requesting
/// peer, Fourberie's original <c>static void M(int)</c> methods still select
/// <c>MainHero/MainParty/_agentsParty</c> and cannot receive that peer's explicit context.
/// </summary>
internal sealed class FourberieCreateActionInterface : IFourberieCreateActionInterface
{
    public FourberieCreateActionVerdict TryRun(string declaringTypeName, string methodName, int arg)
        => FourberieCreateActionVerdict.NotAllowed;
}
