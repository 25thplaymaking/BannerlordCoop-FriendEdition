namespace GameInterface.Services.MapEvents.PlayerPartyInteractions;

public enum PlayerPartyInteractionPhase
{
    None,
    InitialOptions,
    OfferServices,
    WaitingForProposal,
    WaitingForResponse,
    ProposalPending,
    TradeActive,
    HostileDemandConfirm,
    HostileDemandPending
}

public enum PlayerPartyInteractionOption
{
    None,
    TradeProposal,
    OfferServices,
    JoinClan,
    Vassal,
    AcceptProposal,
    DeclineProposal,
    Leave,
    HostileDemand,
    ConfirmHostileDemand,
    CancelHostileDemand,
    RefuseHostileDemand,
    YieldHostileDemand,
    TravelTogether
}

public enum PlayerPartyInteractionVassalUnavailableReason
{
    None,
    TargetIsNotKingdomLeader,
    InitiatorHasNoClan,
    InitiatorIsInKingdom,
    InitiatorClanTierTooLow
}

public enum PlayerPartyInteractionProposal
{
    None,
    Trade,
    JoinClan,
    Vassal,
    HostileDemand,
    TravelTogether
}

public enum PlayerPartyInteractionOutcomeType
{
    None,
    Left,
    TradeAccepted,
    TradeDeclined,
    ClanJoinAccepted,
    ClanJoinDeclined,
    VassalAccepted,
    VassalDeclined,
    Rejected,
    Disconnected,
    HostileDemandAccepted,
    HostileDemandYielded,
    TravelTogetherAccepted,
    TravelTogetherDeclined
}

public enum PlayerPartyInteractionDeniedReason
{
    None,
    Busy,
    Hostile
}
