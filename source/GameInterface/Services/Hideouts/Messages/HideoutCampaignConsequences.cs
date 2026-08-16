using Common.Messaging;
using ProtoBuf;
using TaleWorlds.CampaignSystem.Settlements;

namespace GameInterface.Services.Hideouts.Messages;

internal enum HideoutCampaignConsequence
{
    PrepareMission,
    SetAttackCooldown,
    GrantClearRewards,
    PrepareDirectAssaultMission,
}

internal readonly struct HideoutCampaignConsequenceRequested : IEvent
{
    public readonly Settlement Settlement;
    public readonly HideoutCampaignConsequence Consequence;

    public HideoutCampaignConsequenceRequested(
        Settlement settlement,
        HideoutCampaignConsequence consequence)
    {
        Settlement = settlement;
        Consequence = consequence;
    }
}

[ProtoContract(SkipConstructor = true)]
[AuthorityRoute("hideout.campaign-consequence", AuthorityRouteKind.Command)]
internal readonly struct NetworkHideoutCampaignConsequenceRequested : ICommand
{
    [ProtoMember(1)]
    public readonly string SettlementId;

    [ProtoMember(2)]
    public readonly HideoutCampaignConsequence Consequence;

    [ProtoMember(3)]
    public readonly int ProtocolVersion;

    [ProtoMember(4)]
    public readonly string SessionId;

    [ProtoMember(5)]
    public readonly long AuthorityRequestId;

    [ProtoMember(6)]
    public readonly long ExpectedRevision;

    [ProtoMember(7)]
    public readonly string AssaultSessionId;

    [ProtoMember(8)]
    public readonly long ExpectedHideoutRevision;

    public NetworkHideoutCampaignConsequenceRequested(
        AuthorityRequestHeader header,
        string settlementId,
        HideoutCampaignConsequence consequence,
        string assaultSessionId,
        long expectedHideoutRevision)
    {
        SettlementId = settlementId;
        Consequence = consequence;
        ProtocolVersion = header.ProtocolVersion;
        SessionId = header.SessionId;
        AuthorityRequestId = header.RequestId;
        ExpectedRevision = header.ExpectedRevision;
        AssaultSessionId = assaultSessionId;
        ExpectedHideoutRevision = expectedHideoutRevision;
    }

    public AuthorityRequestHeader Header => new(
        ProtocolVersion,
        SessionId,
        AuthorityRequestId,
        ExpectedRevision);
}

internal enum HideoutAssaultStage
{
    None,
    Prepared,
    CooldownCommitted,
    Closed,
}

[ProtoContract(SkipConstructor = true)]
internal readonly struct NetworkHideoutCampaignConsequenceResult : ICommand
{
    [ProtoMember(1)]
    public readonly AuthorityResultHeader Header;

    [ProtoMember(2)]
    public readonly string SettlementId;

    [ProtoMember(3)]
    public readonly HideoutCampaignConsequence Consequence;

    [ProtoMember(4)]
    public readonly int ExpectedHealthyDefenderCount;

    [ProtoMember(5)]
    public readonly bool ExpectedCooldownActive;

    [ProtoMember(6)]
    public readonly string AssaultSessionId;

    [ProtoMember(7)]
    public readonly HideoutAssaultStage Stage;

    [ProtoMember(8)]
    public readonly long HideoutRevision;

    [ProtoMember(9)]
    public readonly bool ExpectedInfested;

    public NetworkHideoutCampaignConsequenceResult(
        AuthorityResultHeader header,
        string settlementId,
        HideoutCampaignConsequence consequence,
        int expectedHealthyDefenderCount,
        bool expectedCooldownActive,
        string assaultSessionId,
        HideoutAssaultStage stage,
        long hideoutRevision,
        bool expectedInfested)
    {
        Header = header;
        SettlementId = settlementId;
        Consequence = consequence;
        ExpectedHealthyDefenderCount = expectedHealthyDefenderCount;
        ExpectedCooldownActive = expectedCooldownActive;
        AssaultSessionId = assaultSessionId;
        Stage = stage;
        HideoutRevision = hideoutRevision;
        ExpectedInfested = expectedInfested;
    }
}

/// <summary>Targeted canonical state for the peer-owned, non-persistent hideout assault session.</summary>
[ProtoContract(SkipConstructor = true)]
internal readonly struct NetworkHideoutAssaultSessionState : ICommand
{
    [ProtoMember(1)] public readonly string ConfigSessionId;
    [ProtoMember(2)] public readonly string AssaultSessionId;
    [ProtoMember(3)] public readonly string SettlementId;
    [ProtoMember(4)] public readonly HideoutAssaultStage Stage;
    [ProtoMember(5)] public readonly long HideoutRevision;
    [ProtoMember(6)] public readonly int ExpectedHealthyDefenderCount;
    [ProtoMember(7)] public readonly bool CooldownActive;
    [ProtoMember(8)] public readonly bool Infested;

    public NetworkHideoutAssaultSessionState(
        string configSessionId, string assaultSessionId, string settlementId, HideoutAssaultStage stage,
        long hideoutRevision, int expectedHealthyDefenderCount, bool cooldownActive, bool infested)
    {
        ConfigSessionId = configSessionId;
        AssaultSessionId = assaultSessionId;
        SettlementId = settlementId;
        Stage = stage;
        HideoutRevision = hideoutRevision;
        ExpectedHealthyDefenderCount = expectedHealthyDefenderCount;
        CooldownActive = cooldownActive;
        Infested = infested;
    }
}
