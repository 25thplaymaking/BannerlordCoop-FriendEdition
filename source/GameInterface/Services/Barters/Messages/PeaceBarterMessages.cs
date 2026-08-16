using Common.Messaging;
using ProtoBuf;
using GameInterface.Services.Inventory.Data;
using GameInterface.Services.TroopRosters.Data;
using System;

namespace GameInterface.Services.Barters.Messages;

internal enum PeaceBarterTermType
{
    Gold,
    Item,
    Fief,
    TransferPrisoner,
    ReleasePrisoner,
}

internal enum PeaceConversationContext
{
    MapParty,
    Location,

    /// <summary>
    /// A conversation held inside a settlement without a location mission - the settlement menu.
    /// </summary>
    /// <remarks>
    /// Talking to a lord from a castle or town menu creates no agent interaction and no
    /// CampaignMission.Current.Location, so neither the map-party hold nor the location lock is ever
    /// acquired and every barter from such a conversation was refused. There is nothing to lock here;
    /// the server instead verifies both parties are in the settlement it was told about.
    /// Appended, never reordered: the value travels as an int on the wire.
    /// </remarks>
    Settlement,
}

[ProtoContract(SkipConstructor = true)]
internal readonly struct PeaceBarterTerm
{
    [ProtoMember(1)]
    public readonly int Type;
    [ProtoMember(2)]
    public readonly string OwnerHeroId;
    [ProtoMember(3)]
    public readonly string ObjectId;
    [ProtoMember(4)]
    public readonly string ItemModifierId;
    [ProtoMember(5)]
    public readonly bool ItemModifierNull;
    [ProtoMember(6)]
    public readonly int Amount;

    public PeaceBarterTerm(
        PeaceBarterTermType type,
        string ownerHeroId,
        string objectId,
        string itemModifierId,
        bool itemModifierNull,
        int amount)
    {
        Type = (int)type;
        OwnerHeroId = ownerHeroId;
        ObjectId = objectId;
        ItemModifierId = itemModifierId;
        ItemModifierNull = itemModifierNull;
        Amount = amount;
    }
}

[ProtoContract(SkipConstructor = true)]
[AuthorityRoute("barter.peace.commit", AuthorityRouteKind.Command)]
internal readonly struct NetworkRequestPeaceBarter : ICommand
{
    [ProtoMember(1)]
    public readonly string TargetHeroId;
    [ProtoMember(2)]
    public readonly string ContextId;
    [ProtoMember(3)]
    public readonly PeaceBarterTerm[] Terms;
    [ProtoMember(4)]
    public readonly int Context;
    [ProtoMember(5)]
    public readonly string RequestId;
    [ProtoMember(6)]
    public readonly AuthorityRequestHeader Header;

    public NetworkRequestPeaceBarter(
        string targetHeroId,
        PeaceConversationContext context,
        string contextId,
        PeaceBarterTerm[] terms,
        string requestId = null)
    {
        TargetHeroId = targetHeroId;
        ContextId = contextId;
        Terms = terms ?? Array.Empty<PeaceBarterTerm>();
        Context = (int)context;
        RequestId = requestId;
        Header = default;
    }

    public NetworkRequestPeaceBarter(
        string targetHeroId,
        PeaceConversationContext context,
        string contextId,
        PeaceBarterTerm[] terms,
        string requestId,
        AuthorityRequestHeader header)
        : this(targetHeroId, context, contextId, terms, requestId)
    {
        Header = header;
    }
}

[ProtoContract(SkipConstructor = true)]
internal readonly struct NetworkPeaceBarterResult : ICommand
{
    [ProtoMember(1)]
    public readonly string ContextId;
    [ProtoMember(2)]
    public readonly bool Accepted;
    [ProtoMember(3)]
    public readonly int PlayerGold;
    [ProtoMember(4)]
    public readonly string Reason;
    [ProtoMember(5)]
    public readonly string RequestId;
    [ProtoMember(6)]
    public readonly AuthorityResultHeader Header;

    public NetworkPeaceBarterResult(
        string contextId,
        bool accepted,
        int playerGold,
        string reason = null,
        string requestId = null)
    {
        ContextId = contextId;
        Accepted = accepted;
        PlayerGold = playerGold;
        Reason = reason;
        RequestId = requestId;
        Header = default;
    }

    public NetworkPeaceBarterResult(
        string contextId,
        AuthorityResultHeader header,
        int playerGold,
        string requestId)
    {
        ContextId = contextId;
        Accepted = header.Status == AuthorityResultStatus.Accepted;
        PlayerGold = playerGold;
        Reason = header.ReasonCode;
        RequestId = requestId;
        Header = header;
    }
}

/// <summary>
/// Correlated commit witness for a peace barter. Regular campaign replication applies the state;
/// this bounded envelope lets the requesting UI wait for exactly that state before it closes.
/// </summary>
[ProtoContract(SkipConstructor = true)]
internal readonly struct NetworkPeaceBarterDelta : ICommand
{
    [ProtoMember(1)] public readonly string SessionId;
    [ProtoMember(2)] public readonly long AuthorityRequestId;
    [ProtoMember(3)] public readonly long CommittedRevision;
    [ProtoMember(4)] public readonly string PlayerHeroId;
    [ProtoMember(5)] public readonly string TargetHeroId;
    [ProtoMember(6)] public readonly string PlayerPartyId;
    [ProtoMember(7)] public readonly string TargetPartyId;
    [ProtoMember(8)] public readonly string PlayerFactionId;
    [ProtoMember(9)] public readonly string TargetFactionId;
    [ProtoMember(10)] public readonly int PlayerGold;
    [ProtoMember(11)] public readonly int TargetGold;
    [ProtoMember(12)] public readonly int PlayerToTargetRelation;
    [ProtoMember(13)] public readonly ItemRosterElementData[] PlayerItems;
    [ProtoMember(14)] public readonly TroopRosterElementData[] PlayerPrisoners;
    [ProtoMember(15)] public readonly ItemRosterElementData[] TargetItems;
    [ProtoMember(16)] public readonly TroopRosterElementData[] TargetPrisoners;
    [ProtoMember(17)] public readonly long PlayerItemRosterHash;
    [ProtoMember(18)] public readonly long PlayerPrisonRosterHash;
    [ProtoMember(19)] public readonly long TargetItemRosterHash;
    [ProtoMember(20)] public readonly long TargetPrisonRosterHash;
    [ProtoMember(21)] public readonly PeaceBarterFiefStateData[] Fiefs;
    [ProtoMember(22)] public readonly PeaceBarterPrisonerStateData[] Prisoners;
    [ProtoMember(23)] public readonly bool EngagementEnded;

    public NetworkPeaceBarterDelta(
        AuthorityRequestHeader header,
        string playerHeroId, string targetHeroId, string playerPartyId, string targetPartyId,
        string playerFactionId, string targetFactionId, int playerGold, int targetGold,
        int playerToTargetRelation, ItemRosterElementData[] playerItems,
        TroopRosterElementData[] playerPrisoners, ItemRosterElementData[] targetItems,
        TroopRosterElementData[] targetPrisoners, long playerItemRosterHash,
        long playerPrisonRosterHash, long targetItemRosterHash, long targetPrisonRosterHash,
        PeaceBarterFiefStateData[] fiefs, PeaceBarterPrisonerStateData[] prisoners,
        bool engagementEnded)
    {
        SessionId = header.SessionId;
        AuthorityRequestId = header.RequestId;
        CommittedRevision = header.ExpectedRevision;
        PlayerHeroId = playerHeroId;
        TargetHeroId = targetHeroId;
        PlayerPartyId = playerPartyId;
        TargetPartyId = targetPartyId;
        PlayerFactionId = playerFactionId;
        TargetFactionId = targetFactionId;
        PlayerGold = playerGold;
        TargetGold = targetGold;
        PlayerToTargetRelation = playerToTargetRelation;
        PlayerItems = playerItems ?? Array.Empty<ItemRosterElementData>();
        PlayerPrisoners = playerPrisoners ?? Array.Empty<TroopRosterElementData>();
        TargetItems = targetItems ?? Array.Empty<ItemRosterElementData>();
        TargetPrisoners = targetPrisoners ?? Array.Empty<TroopRosterElementData>();
        PlayerItemRosterHash = playerItemRosterHash;
        PlayerPrisonRosterHash = playerPrisonRosterHash;
        TargetItemRosterHash = targetItemRosterHash;
        TargetPrisonRosterHash = targetPrisonRosterHash;
        Fiefs = fiefs ?? Array.Empty<PeaceBarterFiefStateData>();
        Prisoners = prisoners ?? Array.Empty<PeaceBarterPrisonerStateData>();
        EngagementEnded = engagementEnded;
    }
}

[ProtoContract(SkipConstructor = true)]
internal readonly struct PeaceBarterFiefStateData
{
    [ProtoMember(1)] public readonly string SettlementId;
    [ProtoMember(2)] public readonly string OwnerClanId;

    public PeaceBarterFiefStateData(string settlementId, string ownerClanId)
    {
        SettlementId = settlementId;
        OwnerClanId = ownerClanId;
    }
}

[ProtoContract(SkipConstructor = true)]
internal readonly struct PeaceBarterPrisonerStateData
{
    [ProtoMember(1)] public readonly string HeroId;
    [ProtoMember(2)] public readonly bool IsPrisoner;
    [ProtoMember(3)] public readonly string CaptorPartyId;

    public PeaceBarterPrisonerStateData(string heroId, bool isPrisoner, string captorPartyId)
    {
        HeroId = heroId;
        IsPrisoner = isPrisoner;
        CaptorPartyId = captorPartyId;
    }
}
