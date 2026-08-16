using Common.Messaging;
using GameInterface.Services.AuthorityRequests;
using ProtoBuf;
using System;

namespace GameInterface.Services.Barters.Messages;

internal enum MarriageBarterTermType
{
    Gold,
    Item,
    Fief,
    Prisoner,
}

internal enum MarriageConversationContext
{
    MapParty,
    Location,
    Settlement,
}

[ProtoContract(SkipConstructor = true)]
[AuthorityRoute("barter.marriage.authorize", AuthorityRouteKind.Command)]
internal readonly struct NetworkAuthorizeMarriageBarter : ICommand
{
    [ProtoMember(1)]
    public readonly string RequestId;
    [ProtoMember(2)]
    public readonly string CounterpartyHeroId;
    [ProtoMember(3)]
    public readonly int Context;
    [ProtoMember(4)]
    public readonly string ContextId;
    [ProtoMember(5)]
    public readonly string HeroBeingProposedToId;
    [ProtoMember(6)]
    public readonly string ProposingHeroId;
    [ProtoMember(7)]
    public readonly AuthorityRequestHeader Header;

    public NetworkAuthorizeMarriageBarter(
        string requestId,
        string counterpartyHeroId,
        MarriageConversationContext context,
        string contextId,
        string heroBeingProposedToId,
        string proposingHeroId)
    {
        RequestId = requestId;
        CounterpartyHeroId = counterpartyHeroId;
        Context = (int)context;
        ContextId = contextId;
        HeroBeingProposedToId = heroBeingProposedToId;
        ProposingHeroId = proposingHeroId;
        Header = default;
    }

    public NetworkAuthorizeMarriageBarter(
        string requestId, string counterpartyHeroId, MarriageConversationContext context, string contextId,
        string heroBeingProposedToId, string proposingHeroId, AuthorityRequestHeader header)
        : this(requestId, counterpartyHeroId, context, contextId, heroBeingProposedToId, proposingHeroId)
    {
        Header = header;
    }
}

[ProtoContract(SkipConstructor = true)]
internal readonly struct NetworkMarriageBarterAuthorizationResult : ICommand
{
    [ProtoMember(1)]
    public readonly string RequestId;
    [ProtoMember(2)]
    public readonly string LeaseId;
    [ProtoMember(3)]
    public readonly AuthorityResultHeader Header;

    public NetworkMarriageBarterAuthorizationResult(string requestId, string leaseId, AuthorityResultHeader header)
    {
        RequestId = requestId;
        LeaseId = leaseId;
        Header = header;
    }
}

[ProtoContract(SkipConstructor = true)]
internal readonly struct MarriageBarterTerm
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

    public MarriageBarterTerm(
        MarriageBarterTermType type,
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
[AuthorityRoute("barter.marriage.commit", AuthorityRouteKind.Command)]
internal readonly struct NetworkRequestMarriageBarter : ICommand
{
    [ProtoMember(1)]
    public readonly string CounterpartyHeroId;
    [ProtoMember(2)]
    public readonly int Context;
    [ProtoMember(3)]
    public readonly string ContextId;
    [ProtoMember(4)]
    public readonly string HeroBeingProposedToId;
    [ProtoMember(5)]
    public readonly string ProposingHeroId;
    [ProtoMember(6)]
    public readonly MarriageBarterTerm[] Terms;
    [ProtoMember(7)]
    public readonly string RequestId;
    [ProtoMember(8)]
    public readonly string LeaseId;
    [ProtoMember(9)]
    public readonly AuthorityRequestHeader Header;

    public NetworkRequestMarriageBarter(
        string counterpartyHeroId,
        MarriageConversationContext context,
        string contextId,
        string heroBeingProposedToId,
        string proposingHeroId,
        MarriageBarterTerm[] terms,
        string requestId = null)
    {
        CounterpartyHeroId = counterpartyHeroId;
        Context = (int)context;
        ContextId = contextId;
        HeroBeingProposedToId = heroBeingProposedToId;
        ProposingHeroId = proposingHeroId;
        Terms = terms ?? Array.Empty<MarriageBarterTerm>();
        RequestId = requestId;
        LeaseId = null;
        Header = default;
    }

    public NetworkRequestMarriageBarter(
        string counterpartyHeroId, MarriageConversationContext context, string contextId,
        string heroBeingProposedToId, string proposingHeroId, MarriageBarterTerm[] terms, string requestId,
        string leaseId, AuthorityRequestHeader header)
        : this(counterpartyHeroId, context, contextId, heroBeingProposedToId, proposingHeroId, terms, requestId)
    {
        LeaseId = leaseId;
        Header = header;
    }
}

[ProtoContract(SkipConstructor = true)]
internal readonly struct NetworkMarriageBarterResult : ICommand
{
    [ProtoMember(1)]
    public readonly string CounterpartyHeroId;
    [ProtoMember(2)]
    public readonly string HeroBeingProposedToId;
    [ProtoMember(3)]
    public readonly string ProposingHeroId;
    [ProtoMember(4)]
    public readonly bool Accepted;
    [ProtoMember(5)]
    public readonly int PlayerGold;
    [ProtoMember(6)]
    public readonly string Reason;
    [ProtoMember(7)]
    public readonly string RequestId;
    [ProtoMember(8)]
    public readonly AuthorityResultHeader Header;

    public NetworkMarriageBarterResult(
        string counterpartyHeroId,
        string heroBeingProposedToId,
        string proposingHeroId,
        bool accepted,
        int playerGold,
        string reason = null,
        string requestId = null)
    {
        CounterpartyHeroId = counterpartyHeroId;
        HeroBeingProposedToId = heroBeingProposedToId;
        ProposingHeroId = proposingHeroId;
        Accepted = accepted;
        PlayerGold = playerGold;
        Reason = reason;
        RequestId = requestId;
        Header = default;
    }

    public NetworkMarriageBarterResult(
        string counterpartyHeroId, string heroBeingProposedToId, string proposingHeroId,
        AuthorityResultHeader header, int playerGold, string requestId)
    {
        CounterpartyHeroId = counterpartyHeroId;
        HeroBeingProposedToId = heroBeingProposedToId;
        ProposingHeroId = proposingHeroId;
        Accepted = header.Status == AuthorityResultStatus.Accepted;
        PlayerGold = playerGold;
        Reason = header.ReasonCode;
        RequestId = requestId;
        Header = header;
    }
}

[ProtoContract(SkipConstructor = true)]
internal readonly struct NetworkMarriageBarterDelta : ICommand
{
    [ProtoMember(1)] public readonly string SessionId;
    [ProtoMember(2)] public readonly long AuthorityRequestId;
    [ProtoMember(3)] public readonly long CommittedRevision;
    [ProtoMember(4)] public readonly string PlayerHeroId;
    [ProtoMember(5)] public readonly string CounterpartyHeroId;
    [ProtoMember(6)] public readonly string HeroBeingProposedToId;
    [ProtoMember(7)] public readonly string ProposingHeroId;
    [ProtoMember(8)] public readonly string HeroBeingProposedToSpouseId;
    [ProtoMember(9)] public readonly string ProposingHeroSpouseId;
    [ProtoMember(10)] public readonly int RomanceLevel;
    [ProtoMember(11)] public readonly int PlayerGold;
    [ProtoMember(12)] public readonly int CounterpartyGold;
    [ProtoMember(13)] public readonly int HeroBeingProposedToGold;
    [ProtoMember(14)] public readonly int ProposingHeroGold;
    [ProtoMember(15)] public readonly int Context;
    [ProtoMember(16)] public readonly string ContextId;

    public NetworkMarriageBarterDelta(AuthorityRequestHeader header, string playerHeroId, string counterpartyHeroId,
        string heroBeingProposedToId, string proposingHeroId, string heroBeingProposedToSpouseId,
        string proposingHeroSpouseId, int romanceLevel, int playerGold, int counterpartyGold,
        int heroBeingProposedToGold, int proposingHeroGold, int context, string contextId)
    {
        SessionId = header.SessionId;
        AuthorityRequestId = header.RequestId;
        CommittedRevision = header.ExpectedRevision;
        PlayerHeroId = playerHeroId;
        CounterpartyHeroId = counterpartyHeroId;
        HeroBeingProposedToId = heroBeingProposedToId;
        ProposingHeroId = proposingHeroId;
        HeroBeingProposedToSpouseId = heroBeingProposedToSpouseId;
        ProposingHeroSpouseId = proposingHeroSpouseId;
        RomanceLevel = romanceLevel;
        PlayerGold = playerGold;
        CounterpartyGold = counterpartyGold;
        HeroBeingProposedToGold = heroBeingProposedToGold;
        ProposingHeroGold = proposingHeroGold;
        Context = context;
        ContextId = contextId;
    }
}
