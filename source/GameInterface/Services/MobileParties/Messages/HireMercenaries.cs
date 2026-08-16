using Common.Messaging;
using GameInterface.Services.AuthorityRequests;
using ProtoBuf;

namespace GameInterface.Services.MobileParties.Messages;

/// <summary>
/// [Client -> Server] Requests the server apply a tavern mercenary hire: add the mercenary troops
/// to the player's party member roster and deduct the gold cost. The server applies both with
/// patches live, so the troop add (TroopRoster patches) and gold change (Hero.Gold sync) replicate
/// to every client. The count is the client's requested hire amount; the server validates it against
/// authoritative stock and current server hero gold. HeroGold is the client's snapshot for reject
/// diagnostics only.
/// </summary>
[ProtoContract(SkipConstructor = true)]
[AuthorityRoute("mercenary.hire", AuthorityRouteKind.Command)]
internal readonly struct HireMercenaries : ICommand
{
    [ProtoMember(1)] public readonly string TownId;
    [ProtoMember(2)] public readonly int Count;
    [ProtoMember(3)] public readonly AuthorityRequestHeader Header;

    public HireMercenaries(string townId, int count, AuthorityRequestHeader header)
    { TownId = townId; Count = count; Header = header; }
}

[ProtoContract(SkipConstructor = true)]
internal readonly struct MercenaryHireResult : ICommand
{
    [ProtoMember(1)] public readonly string TownId;
    [ProtoMember(2)] public readonly string TroopId;
    [ProtoMember(3)] public readonly int Count;
    [ProtoMember(4)] public readonly int ExpectedPartyTroopCount;
    [ProtoMember(5)] public readonly int ExpectedHeroGold;
    [ProtoMember(6)] public readonly int ExpectedStock;
    [ProtoMember(7)] public readonly AuthorityResultHeader Header;

    public MercenaryHireResult(string townId, string troopId, int count, int expectedPartyTroopCount,
        int expectedHeroGold, int expectedStock, AuthorityResultHeader header)
    { TownId = townId; TroopId = troopId; Count = count; ExpectedPartyTroopCount = expectedPartyTroopCount;
      ExpectedHeroGold = expectedHeroGold; ExpectedStock = expectedStock; Header = header; }
}
