using Common.Messaging;
using GameInterface.Services.Inventory.Data;
using GameInterface.Services.TroopRosters.Data;
using ProtoBuf;

namespace GameInterface.Services.MapEvents.Messages.Conversation;

[ProtoContract(SkipConstructor = true)]
[AuthorityRoute("player-interaction.shown", AuthorityRouteKind.Command)]
internal readonly struct RequestPlayerPartyInteractionShown : ICommand
{
    [ProtoMember(1)] public readonly AuthorityRequestHeader Header;
    [ProtoMember(2)] public readonly string InteractionSessionId;
    [ProtoMember(3)] public readonly long ExpectedInteractionRevision;
    public RequestPlayerPartyInteractionShown(AuthorityRequestHeader header, string interactionSessionId, long expectedInteractionRevision)
    { Header = header; InteractionSessionId = interactionSessionId; ExpectedInteractionRevision = expectedInteractionRevision; }
}

[ProtoContract(SkipConstructor = true)]
[AuthorityRoute("player-interaction.option", AuthorityRouteKind.Command)]
internal readonly struct RequestPlayerPartyInteractionOption : ICommand
{
    [ProtoMember(1)] public readonly AuthorityRequestHeader Header;
    [ProtoMember(2)] public readonly string InteractionSessionId;
    [ProtoMember(3)] public readonly long ExpectedInteractionRevision;
    [ProtoMember(4)] public readonly int Option;
    public RequestPlayerPartyInteractionOption(AuthorityRequestHeader header, string interactionSessionId, long expectedInteractionRevision, int option)
    { Header = header; InteractionSessionId = interactionSessionId; ExpectedInteractionRevision = expectedInteractionRevision; Option = option; }
}

[ProtoContract(SkipConstructor = true)]
[AuthorityRoute("player-interaction.trade-offer", AuthorityRouteKind.Command)]
internal readonly struct RequestPlayerPartyTradeOffer : ICommand
{
    [ProtoMember(1)] public readonly AuthorityRequestHeader Header;
    [ProtoMember(2)] public readonly string InteractionSessionId;
    [ProtoMember(3)] public readonly long ExpectedInteractionRevision;
    [ProtoMember(4)] public readonly ItemRosterElementData[] OfferedItems;
    [ProtoMember(5)] public readonly TroopRosterElementData[] OfferedTroops;
    [ProtoMember(6)] public readonly int OfferedGold;
    [ProtoMember(7)] public readonly string[] OfferedFiefs;
    [ProtoMember(8)] public readonly TroopRosterElementData[] OfferedPrisoners;
    [ProtoMember(9)] public readonly bool OfferedPeace;
    public RequestPlayerPartyTradeOffer(AuthorityRequestHeader header, string interactionSessionId, long expectedInteractionRevision,
        ItemRosterElementData[] offeredItems, TroopRosterElementData[] offeredTroops, int offeredGold, string[] offeredFiefs,
        TroopRosterElementData[] offeredPrisoners, bool offeredPeace)
    { Header = header; InteractionSessionId = interactionSessionId; ExpectedInteractionRevision = expectedInteractionRevision; OfferedItems = offeredItems ?? new ItemRosterElementData[0]; OfferedTroops = offeredTroops ?? new TroopRosterElementData[0]; OfferedGold = offeredGold; OfferedFiefs = offeredFiefs ?? new string[0]; OfferedPrisoners = offeredPrisoners ?? new TroopRosterElementData[0]; OfferedPeace = offeredPeace; }
}

[ProtoContract(SkipConstructor = true)]
[AuthorityRoute("player-interaction.trade-accept", AuthorityRouteKind.Command)]
internal readonly struct RequestPlayerPartyTradeAccept : ICommand
{
    [ProtoMember(1)] public readonly AuthorityRequestHeader Header;
    [ProtoMember(2)] public readonly string InteractionSessionId;
    [ProtoMember(3)] public readonly long ExpectedInteractionRevision;
    [ProtoMember(4)] public readonly bool Accepted;
    public RequestPlayerPartyTradeAccept(AuthorityRequestHeader header, string interactionSessionId, long expectedInteractionRevision, bool accepted)
    { Header = header; InteractionSessionId = interactionSessionId; ExpectedInteractionRevision = expectedInteractionRevision; Accepted = accepted; }
}

// Each route has a distinct result type.  Keeping the compact, correlated post-state envelope identical
// makes route replay safe without turning interaction commands back into an untyped RPC surface.
[ProtoContract(SkipConstructor = true)]
internal readonly struct PlayerPartyInteractionShownResult : ICommand
{
    [ProtoMember(1)] public readonly AuthorityResultHeader Header;
    [ProtoMember(2)] public readonly string InteractionSessionId;
    [ProtoMember(3)] public readonly string PartyId;
    [ProtoMember(4)] public readonly long InteractionRevision;
    public PlayerPartyInteractionShownResult(AuthorityResultHeader header, string sessionId, string partyId, long revision) { Header = header; InteractionSessionId = sessionId; PartyId = partyId; InteractionRevision = revision; }
}
[ProtoContract(SkipConstructor = true)]
internal readonly struct PlayerPartyInteractionOptionResult : ICommand
{
    [ProtoMember(1)] public readonly AuthorityResultHeader Header;
    [ProtoMember(2)] public readonly string InteractionSessionId;
    [ProtoMember(3)] public readonly string PartyId;
    [ProtoMember(4)] public readonly long InteractionRevision;
    public PlayerPartyInteractionOptionResult(AuthorityResultHeader header, string sessionId, string partyId, long revision) { Header = header; InteractionSessionId = sessionId; PartyId = partyId; InteractionRevision = revision; }
}
[ProtoContract(SkipConstructor = true)]
internal readonly struct PlayerPartyTradeOfferResult : ICommand
{
    [ProtoMember(1)] public readonly AuthorityResultHeader Header;
    [ProtoMember(2)] public readonly string InteractionSessionId;
    [ProtoMember(3)] public readonly string PartyId;
    [ProtoMember(4)] public readonly long InteractionRevision;
    public PlayerPartyTradeOfferResult(AuthorityResultHeader header, string sessionId, string partyId, long revision) { Header = header; InteractionSessionId = sessionId; PartyId = partyId; InteractionRevision = revision; }
}
[ProtoContract(SkipConstructor = true)]
internal readonly struct PlayerPartyTradeAcceptResult : ICommand
{
    [ProtoMember(1)] public readonly AuthorityResultHeader Header;
    [ProtoMember(2)] public readonly string InteractionSessionId;
    [ProtoMember(3)] public readonly string PartyId;
    [ProtoMember(4)] public readonly long InteractionRevision;
    public PlayerPartyTradeAcceptResult(AuthorityResultHeader header, string sessionId, string partyId, long revision) { Header = header; InteractionSessionId = sessionId; PartyId = partyId; InteractionRevision = revision; }
}
