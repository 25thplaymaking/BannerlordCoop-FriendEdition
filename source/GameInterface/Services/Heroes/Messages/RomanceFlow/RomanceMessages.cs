using Common.Messaging;
using ProtoBuf;
using TaleWorlds.CampaignSystem;
using Romance = TaleWorlds.CampaignSystem.Romance;

namespace GameInterface.Services.Heroes.Messages.RomanceFlow;

internal readonly struct RomanticStateChangeRequested : IEvent
{
    public readonly Hero Person1;
    public readonly Hero Person2;
    public readonly Romance.RomanceLevelEnum RequestedLevel;
    public readonly int ProgressToNextLevel;
    public readonly float LastVisit;
    public readonly float ScoreFromPersuasion;

    public RomanticStateChangeRequested(
        Hero person1,
        Hero person2,
        Romance.RomanceLevelEnum requestedLevel,
        int progressToNextLevel,
        float lastVisit,
        float scoreFromPersuasion)
    {
        Person1 = person1;
        Person2 = person2;
        RequestedLevel = requestedLevel;
        ProgressToNextLevel = progressToNextLevel;
        LastVisit = lastVisit;
        ScoreFromPersuasion = scoreFromPersuasion;
    }
}

internal readonly struct RomanceStatesChanged : IEvent
{
}

[AuthorityRoute("romance.transition", AuthorityRouteKind.Command)]
[ProtoContract(SkipConstructor = true)]
internal readonly struct NetworkRequestRomanceStateChange : ICommand
{
    [ProtoMember(1)]
    public readonly string TargetHeroId;
    [ProtoMember(2)]
    public readonly int RequestedLevel;
    [ProtoMember(3)]
    public readonly int ProgressToNextLevel;
    [ProtoMember(4)]
    public readonly float LastVisit;
    [ProtoMember(5)]
    public readonly float ScoreFromPersuasion;

    /// <summary>
    /// Set only for an ARRANGED change: the requesting player's clan member being promised to
    /// <see cref="TargetHeroId"/>. Empty/null means the player hero itself is the courting party.
    /// </summary>
    [ProtoMember(6)]
    public readonly string ClanMemberHeroId;
    [ProtoMember(7)]
    public readonly AuthorityRequestHeader Header;

    public NetworkRequestRomanceStateChange(
        string targetHeroId,
        Romance.RomanceLevelEnum requestedLevel,
        int progressToNextLevel,
        float lastVisit,
        float scoreFromPersuasion,
        string clanMemberHeroId = null,
        AuthorityRequestHeader header = default)
    {
        TargetHeroId = targetHeroId;
        RequestedLevel = (int)requestedLevel;
        ProgressToNextLevel = progressToNextLevel;
        LastVisit = lastVisit;
        ScoreFromPersuasion = scoreFromPersuasion;
        ClanMemberHeroId = clanMemberHeroId;
        Header = header;
    }
}

[AuthorityRoute("romance.snapshot", AuthorityRouteKind.BootstrapQuery)]
[ProtoContract(SkipConstructor = true)]
internal readonly struct NetworkRequestRomanceStateSync : ICommand
{
    [ProtoMember(1)]
    public readonly AuthorityRequestHeader Header;

    public NetworkRequestRomanceStateSync(AuthorityRequestHeader header = default)
    {
        Header = header;
    }
}

[ProtoContract(SkipConstructor = true)]
internal readonly struct NetworkSyncRomanceStates : ICommand
{
    [ProtoMember(1)]
    public readonly RomanceStateData[] States;

    public NetworkSyncRomanceStates(RomanceStateData[] states)
    {
        States = states;
    }
}

[ProtoContract(SkipConstructor = true)]
internal readonly struct NetworkRomanceStateChangeResult : IEvent
{
    [ProtoMember(1)] public readonly string Person1Id;
    [ProtoMember(2)] public readonly string Person2Id;
    [ProtoMember(3)] public readonly int Level;
    [ProtoMember(4)] public readonly string SessionId;
    [ProtoMember(5)] public readonly long AuthorityRequestId;
    [ProtoMember(6)] public readonly AuthorityResultStatus Status;
    [ProtoMember(7)] public readonly long CommittedRevision;
    [ProtoMember(8)] public readonly string ReasonCode;

    public NetworkRomanceStateChangeResult(
        AuthorityRequestHeader request,
        string person1Id,
        string person2Id,
        Romance.RomanceLevelEnum level,
        AuthorityResultStatus status,
        string reasonCode = null)
    {
        Person1Id = person1Id;
        Person2Id = person2Id;
        Level = (int)level;
        SessionId = request.SessionId;
        AuthorityRequestId = request.RequestId;
        Status = status;
        CommittedRevision = request.ExpectedRevision;
        ReasonCode = reasonCode;
    }

    public AuthorityResultHeader Header =>
        new AuthorityResultHeader(SessionId, AuthorityRequestId, Status, CommittedRevision, ReasonCode);
}

[ProtoContract(SkipConstructor = true)]
internal readonly struct NetworkRomanceStateSyncResult : IEvent
{
    [ProtoMember(1)] public readonly RomanceStateData[] States;
    [ProtoMember(2)] public readonly string SessionId;
    [ProtoMember(3)] public readonly long AuthorityRequestId;
    [ProtoMember(4)] public readonly AuthorityResultStatus Status;
    [ProtoMember(5)] public readonly long CommittedRevision;
    [ProtoMember(6)] public readonly string ReasonCode;

    public NetworkRomanceStateSyncResult(
        AuthorityRequestHeader request,
        AuthorityResultStatus status,
        RomanceStateData[] states,
        string reasonCode = null)
    {
        States = states ?? System.Array.Empty<RomanceStateData>();
        SessionId = request.SessionId;
        AuthorityRequestId = request.RequestId;
        Status = status;
        CommittedRevision = request.ExpectedRevision;
        ReasonCode = reasonCode;
    }

    public AuthorityResultHeader Header =>
        new AuthorityResultHeader(SessionId, AuthorityRequestId, Status, CommittedRevision, ReasonCode);
}

[ProtoContract(SkipConstructor = true)]
internal readonly struct RomanceStateData
{
    [ProtoMember(1)]
    public readonly string Person1Id;
    [ProtoMember(2)]
    public readonly string Person2Id;
    [ProtoMember(3)]
    public readonly int Level;
    [ProtoMember(4)]
    public readonly int ProgressToNextLevel;
    [ProtoMember(5)]
    public readonly float LastVisit;
    [ProtoMember(6)]
    public readonly float ScoreFromPersuasion;

    public RomanceStateData(
        string person1Id,
        string person2Id,
        Romance.RomanceLevelEnum level,
        int progressToNextLevel,
        float lastVisit,
        float scoreFromPersuasion)
    {
        Person1Id = person1Id;
        Person2Id = person2Id;
        Level = (int)level;
        ProgressToNextLevel = progressToNextLevel;
        LastVisit = lastVisit;
        ScoreFromPersuasion = scoreFromPersuasion;
    }
}

[ProtoContract(SkipConstructor = true)]
internal readonly struct NetworkRomanceRequestRejected : ICommand
{
    [ProtoMember(1)]
    public readonly string Reason;

    public NetworkRomanceRequestRejected(string reason)
    {
        Reason = reason;
    }
}
