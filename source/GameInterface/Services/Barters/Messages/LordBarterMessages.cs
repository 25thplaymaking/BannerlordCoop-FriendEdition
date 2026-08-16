using Common.Messaging;
using GameInterface.Services.AuthorityRequests;
using GameInterface.Services.Inventory.Data;
using GameInterface.Services.TroopRosters.Data;
using ProtoBuf;
using System;

namespace GameInterface.Services.Barters.Messages;

internal enum LordBarterKind
{
    Generic,
    SafePassage,
    JoinKingdomAsClan,
}

[ProtoContract(SkipConstructor = true)]
internal readonly struct NetworkAuthorizeLordBarter : ICommand
{
    [ProtoMember(1)] public readonly string RequestId;
    [ProtoMember(2)] public readonly string TargetHeroId;
    [ProtoMember(3)] public readonly string ContextId;
    [ProtoMember(4)] public readonly int Context;
    [ProtoMember(5)] public readonly int Kind;
    [ProtoMember(6)] public readonly string TargetKingdomId;

    public NetworkAuthorizeLordBarter(
        string requestId,
        string targetHeroId,
        PeaceConversationContext context,
        string contextId,
        LordBarterKind kind,
        string targetKingdomId = null)
    {
        RequestId = requestId;
        TargetHeroId = targetHeroId;
        ContextId = contextId;
        Context = (int)context;
        Kind = (int)kind;
        TargetKingdomId = targetKingdomId;
    }
}

[ProtoContract(SkipConstructor = true)]
internal readonly struct NetworkCancelLordBarterAuthorization : ICommand
{
    [ProtoMember(1)] public readonly string RequestId;

    public NetworkCancelLordBarterAuthorization(string requestId)
    {
        RequestId = requestId;
    }
}

[ProtoContract(SkipConstructor = true)]
[AuthorityRoute("barter.lord.commit", AuthorityRouteKind.Command)]
internal readonly struct NetworkRequestLordBarter : ICommand
{
    [ProtoMember(1)] public readonly string TargetHeroId;
    [ProtoMember(2)] public readonly string ContextId;
    [ProtoMember(3)] public readonly PeaceBarterTerm[] Terms;
    [ProtoMember(4)] public readonly int Context;
    [ProtoMember(5)] public readonly int Kind;
    [ProtoMember(6)] public readonly string RequestId;
    [ProtoMember(7)] public readonly DefectionPersuasionOutcome[] PersuasionOutcomes;
    [ProtoMember(8)] public readonly AuthorityRequestHeader Header;

    public NetworkRequestLordBarter(
        string targetHeroId,
        PeaceConversationContext context,
        string contextId,
        LordBarterKind kind,
        PeaceBarterTerm[] terms,
        string requestId,
        DefectionPersuasionOutcome[] persuasionOutcomes = null)
    {
        TargetHeroId = targetHeroId;
        ContextId = contextId;
        Terms = terms ?? Array.Empty<PeaceBarterTerm>();
        Context = (int)context;
        Kind = (int)kind;
        RequestId = requestId;
        PersuasionOutcomes = persuasionOutcomes ?? Array.Empty<DefectionPersuasionOutcome>();
        Header = default;
    }

    public NetworkRequestLordBarter(
        string targetHeroId,
        PeaceConversationContext context,
        string contextId,
        LordBarterKind kind,
        PeaceBarterTerm[] terms,
        string requestId,
        DefectionPersuasionOutcome[] persuasionOutcomes,
        AuthorityRequestHeader header)
        : this(targetHeroId, context, contextId, kind, terms, requestId, persuasionOutcomes)
    {
        Header = header;
    }
}

/// <summary>
/// One successful persuasion attempt behind a lord defection.
/// </summary>
/// <remarks>
/// Only the outcome enums travel. The server derives the XP itself: the skill
/// (<c>DefaultSkills.Charm</c>) and difficulty (<c>PersuasionDifficulty.Medium</c>) are hardcoded in
/// vanilla's defection_successful_on_consequence, so no number a client sends is ever trusted.
/// The mini-game cannot be moved server-side - its rolls run inside the client's ConversationManager
/// and vanilla's option table reads Hero.MainHero / Hero.OneToOneConversationHero - so the outcomes
/// themselves are the irreducible trust surface.
/// </remarks>
[ProtoContract(SkipConstructor = true)]
internal readonly struct DefectionPersuasionOutcome
{
    [ProtoMember(1)] public readonly int Result;           // PersuasionOptionResult
    [ProtoMember(2)] public readonly int ArgumentStrength; // PersuasionArgumentStrength

    public DefectionPersuasionOutcome(int result, int argumentStrength)
    {
        Result = result;
        ArgumentStrength = argumentStrength;
    }
}

[ProtoContract(SkipConstructor = true)]
internal readonly struct NetworkLordBarterResult : ICommand
{
    [ProtoMember(1)] public readonly string ContextId;
    [ProtoMember(2)] public readonly bool Accepted;
    [ProtoMember(3)] public readonly int PlayerGold;
    [ProtoMember(4)] public readonly string Reason;
    [ProtoMember(5)] public readonly string RequestId;
    [ProtoMember(6)] public readonly AuthorityResultHeader Header;

    public NetworkLordBarterResult(string contextId, bool accepted, int playerGold, string reason, string requestId)
    {
        ContextId = contextId;
        Accepted = accepted;
        PlayerGold = playerGold;
        Reason = reason;
        RequestId = requestId;
        Header = default;
    }

    public NetworkLordBarterResult(string contextId, AuthorityResultHeader header, int playerGold, string requestId)
    {
        ContextId = contextId;
        Accepted = header.Status == AuthorityResultStatus.Accepted;
        PlayerGold = playerGold;
        Reason = header.ReasonCode;
        RequestId = requestId;
        Header = header;
    }
}

/// <summary>Correlated post-mutation witness for a lord barter commit.</summary>
[ProtoContract(SkipConstructor = true)]
internal readonly struct NetworkLordBarterDelta : ICommand
{
    [ProtoMember(1)] public readonly string SessionId;
    [ProtoMember(2)] public readonly long AuthorityRequestId;
    [ProtoMember(3)] public readonly long CommittedRevision;
    [ProtoMember(4)] public readonly int Kind;
    [ProtoMember(5)] public readonly string PlayerHeroId;
    [ProtoMember(6)] public readonly string TargetHeroId;
    [ProtoMember(7)] public readonly string PlayerPartyId;
    [ProtoMember(8)] public readonly string TargetPartyId;
    [ProtoMember(9)] public readonly int PlayerGold;
    [ProtoMember(10)] public readonly int TargetGold;
    [ProtoMember(11)] public readonly ItemRosterElementData[] PlayerItems;
    [ProtoMember(12)] public readonly TroopRosterElementData[] PlayerPrisoners;
    [ProtoMember(13)] public readonly ItemRosterElementData[] TargetItems;
    [ProtoMember(14)] public readonly TroopRosterElementData[] TargetPrisoners;
    [ProtoMember(15)] public readonly long PlayerItemRosterHash;
    [ProtoMember(16)] public readonly long PlayerPrisonRosterHash;
    [ProtoMember(17)] public readonly long TargetItemRosterHash;
    [ProtoMember(18)] public readonly long TargetPrisonRosterHash;
    [ProtoMember(19)] public readonly PeaceBarterFiefStateData[] Fiefs;
    [ProtoMember(20)] public readonly PeaceBarterPrisonerStateData[] Prisoners;
    [ProtoMember(21)] public readonly string DefectingClanId;
    [ProtoMember(22)] public readonly string DefectingClanKingdomId;
    [ProtoMember(23)] public readonly bool EngagementEnded;

    public NetworkLordBarterDelta(AuthorityRequestHeader header, LordBarterKind kind,
        string playerHeroId, string targetHeroId, string playerPartyId, string targetPartyId,
        int playerGold, int targetGold, ItemRosterElementData[] playerItems,
        TroopRosterElementData[] playerPrisoners, ItemRosterElementData[] targetItems,
        TroopRosterElementData[] targetPrisoners, long playerItemRosterHash, long playerPrisonRosterHash,
        long targetItemRosterHash, long targetPrisonRosterHash, PeaceBarterFiefStateData[] fiefs,
        PeaceBarterPrisonerStateData[] prisoners, string defectingClanId, string defectingClanKingdomId,
        bool engagementEnded)
    {
        SessionId = header.SessionId;
        AuthorityRequestId = header.RequestId;
        CommittedRevision = header.ExpectedRevision;
        Kind = (int)kind;
        PlayerHeroId = playerHeroId;
        TargetHeroId = targetHeroId;
        PlayerPartyId = playerPartyId;
        TargetPartyId = targetPartyId;
        PlayerGold = playerGold;
        TargetGold = targetGold;
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
        DefectingClanId = defectingClanId;
        DefectingClanKingdomId = defectingClanKingdomId;
        EngagementEnded = engagementEnded;
    }
}
