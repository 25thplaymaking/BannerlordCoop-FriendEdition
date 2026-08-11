using Common.Messaging;
using ProtoBuf;
using TaleWorlds.CampaignSystem;

namespace GameInterface.Services.WorkshopMods.Fourberie;

/// <summary>
/// The local player triggered a Fourberie action that creates campaign parties/troops
/// (saboteur/bandit recruiting, scam bandit spawns); ask the server to run it.
/// </summary>
/// <remarks>
/// These actions are <c>static void M(int)</c> routines that call vanilla
/// <c>MobileParty.CreateParty</c> + <c>TroopRoster</c> mutators. Run on a client they author
/// objects Coop forbids (the "Failed to get TroopRoster using Created_####" storm). Routed to the
/// server they pass through Coop's create funnels and replicate to every client. The requesting
/// hero is carried explicitly so the server never re-reads <c>Hero.MainHero</c>.
/// </remarks>
internal readonly struct FourberieCreateActionAttempted : IEvent
{
    public readonly Hero Requester;
    public readonly string DeclaringTypeName;
    public readonly string MethodName;
    public readonly int Arg;

    public FourberieCreateActionAttempted(Hero requester, string declaringTypeName, string methodName, int arg)
    {
        Requester = requester;
        DeclaringTypeName = declaringTypeName;
        MethodName = methodName;
        Arg = arg;
    }
}

/// <summary>
/// Client asks the server to run one of Fourberie's whitelisted static create-actions
/// authoritatively. Carries the requesting hero id, the method identity, and its single int intent
/// — never outcomes. The server validates ownership and that the method is on the routed
/// allow-list before invoking it.
/// </summary>
[ProtoContract(SkipConstructor = true)]
internal record NetworkRequestFourberieCreateAction : ICommand
{
    [ProtoMember(1)]
    public string RequesterHeroId { get; }

    [ProtoMember(2)]
    public string DeclaringTypeName { get; }

    [ProtoMember(3)]
    public string MethodName { get; }

    [ProtoMember(4)]
    public int Arg { get; }

    public NetworkRequestFourberieCreateAction(string requesterHeroId, string declaringTypeName, string methodName, int arg)
    {
        RequesterHeroId = requesterHeroId;
        DeclaringTypeName = declaringTypeName;
        MethodName = methodName;
        Arg = arg;
    }
}
