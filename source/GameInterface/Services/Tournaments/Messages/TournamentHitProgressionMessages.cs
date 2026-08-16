using Common.Messaging;
using GameInterface.Configuration;
using GameInterface.Services.Tournaments.Data;
using ProtoBuf;
using System;

namespace GameInterface.Services.Tournaments.Messages;

[ProtoContract(SkipConstructor = true)]
public sealed class TournamentHitProgressionData
{
    [ProtoMember(1)] public readonly string SessionId;
    [ProtoMember(2)] public readonly string MatchId;
    [ProtoMember(3)] public readonly long Revision;
    [ProtoMember(4)] public readonly long BracketRevision;
    [ProtoMember(5)] public readonly string DamageOriginControllerId;
    [ProtoMember(6)] public readonly long DamageSequence;
    [ProtoMember(7)] public readonly Guid AttackerAgentId;
    [ProtoMember(8)] public readonly Guid VictimAgentId;
    [ProtoMember(9)] public readonly string WeaponItemId;
    [ProtoMember(10)] public readonly int WeaponUsageIndex;
    [ProtoMember(11)] public readonly float MovementSpeedModifier;
    [ProtoMember(12)] public readonly float ShotDifficulty;
    [ProtoMember(13)] public readonly float HitpointRatio;
    [ProtoMember(14)] public readonly float DamageAmount;
    [ProtoMember(15)] public readonly int AttackType;
    [ProtoMember(16)] public readonly bool AttackerMounted;
    [ProtoMember(17)] public readonly bool SameTeam;
    [ProtoMember(18)] public readonly bool Fatal;
    [ProtoMember(19)] public readonly bool Charging;
    [ProtoMember(20)] public readonly bool SneakAttack;

    public TournamentHitProgressionData(
        string sessionId,
        string matchId,
        long revision,
        long bracketRevision,
        string damageOriginControllerId,
        long damageSequence,
        Guid attackerAgentId,
        Guid victimAgentId,
        string weaponItemId,
        int weaponUsageIndex,
        float movementSpeedModifier,
        float shotDifficulty,
        float hitpointRatio,
        float damageAmount,
        int attackType,
        bool attackerMounted,
        bool sameTeam,
        bool fatal,
        bool charging,
        bool sneakAttack)
    {
        SessionId = sessionId;
        MatchId = matchId;
        Revision = revision;
        BracketRevision = bracketRevision;
        DamageOriginControllerId = damageOriginControllerId;
        DamageSequence = damageSequence;
        AttackerAgentId = attackerAgentId;
        VictimAgentId = victimAgentId;
        WeaponItemId = weaponItemId;
        WeaponUsageIndex = weaponUsageIndex;
        MovementSpeedModifier = movementSpeedModifier;
        ShotDifficulty = shotDifficulty;
        HitpointRatio = hitpointRatio;
        DamageAmount = damageAmount;
        AttackType = attackType;
        AttackerMounted = attackerMounted;
        SameTeam = sameTeam;
        Fatal = fatal;
        Charging = charging;
        SneakAttack = sneakAttack;
    }
}

[AuthorityRoute("tournament.hit-progression", AuthorityRouteKind.Command)]
[ProtoContract(SkipConstructor = true)]
public readonly struct NetworkSubmitTournamentHitProgression : ICommand
{
    [ProtoMember(1)] public readonly TournamentHitProgressionData Data;
    [ProtoMember(2)] public readonly int ProtocolVersion;
    [ProtoMember(3)] public readonly string ConfigSessionId;
    [ProtoMember(4)] public readonly long AuthorityRequestId;
    [ProtoMember(5)] public readonly long ConfigRevision;
    [ProtoMember(6)] public readonly string MissionInstanceId;

    public NetworkSubmitTournamentHitProgression(AuthorityRequestHeader header, TournamentHitProgressionData data, string missionInstanceId)
    {
        Data = data;
        ProtocolVersion = header.ProtocolVersion;
        ConfigSessionId = header.SessionId;
        AuthorityRequestId = header.RequestId;
        ConfigRevision = header.ExpectedRevision;
        MissionInstanceId = missionInstanceId;
    }
    public AuthorityRequestHeader Header => new(ProtocolVersion, ConfigSessionId, AuthorityRequestId, ConfigRevision);
}

[ProtoContract(SkipConstructor = true)]
public readonly struct NetworkTournamentHitProgressionApplied : IEvent
{
    [ProtoMember(1)] public readonly string SessionId;
    [ProtoMember(2)] public readonly long AuthorityRequestId;
    [ProtoMember(3)] public readonly AuthorityResultStatus Status;
    [ProtoMember(4)] public readonly long CommittedRevision;
    [ProtoMember(5)] public readonly string ReasonCode;
    [ProtoMember(6)] public readonly string TournamentSessionId;
    [ProtoMember(7)] public readonly string MatchId;
    [ProtoMember(8)] public readonly long CommittedBracketRevision;
    [ProtoMember(9)] public readonly string DamageOriginControllerId;
    [ProtoMember(10)] public readonly long DamageSequence;
    [ProtoMember(11)] public readonly Guid AttackerAgentId;
    [ProtoMember(12)] public readonly string AttackerCharacterId;
    [ProtoMember(13)] public readonly string MissionInstanceId;

    public NetworkTournamentHitProgressionApplied(AuthorityRequestHeader request, AuthorityResultStatus status,
        TournamentSessionSnapshot snapshot, TournamentHitProgressionData data, string attackerCharacterId, string reasonCode)
    {
        SessionId = request.SessionId; AuthorityRequestId = request.RequestId; Status = status;
        CommittedRevision = snapshot?.Revision ?? data?.Revision ?? request.ExpectedRevision; ReasonCode = reasonCode;
        TournamentSessionId = data?.SessionId; MatchId = data?.MatchId;
        CommittedBracketRevision = snapshot?.BracketRevision ?? data?.BracketRevision ?? 0;
        DamageOriginControllerId = data?.DamageOriginControllerId; DamageSequence = data?.DamageSequence ?? 0;
        AttackerAgentId = data?.AttackerAgentId ?? Guid.Empty; AttackerCharacterId = attackerCharacterId;
        MissionInstanceId = snapshot?.MissionInstanceId;
    }
    public AuthorityResultHeader Header => new(SessionId, AuthorityRequestId, Status, CommittedRevision, ReasonCode);
}
