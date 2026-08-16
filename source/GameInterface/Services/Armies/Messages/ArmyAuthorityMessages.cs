using Common.Messaging;
using ProtoBuf;
using System.Collections.Generic;

namespace GameInterface.Services.Armies.Messages;

[ProtoContract(SkipConstructor = true)]
[AuthorityRoute("army.create", AuthorityRouteKind.Command)]
internal readonly struct RequestCreateArmy : ICommand
{
    [ProtoMember(1)] public readonly string KingdomId;
    [ProtoMember(2)] public readonly string TargetSettlementId;
    [ProtoMember(3)] public readonly string ArmyTypeId;
    [ProtoMember(4)] public readonly List<string> PartyIds;
    [ProtoMember(5)] public readonly AuthorityRequestHeader Header;
    public RequestCreateArmy(string kingdomId, string targetSettlementId, string armyTypeId, List<string> partyIds, AuthorityRequestHeader header)
    { KingdomId = kingdomId; TargetSettlementId = targetSettlementId; ArmyTypeId = armyTypeId; PartyIds = partyIds; Header = header; }
}

[ProtoContract(SkipConstructor = true)]
[AuthorityRoute("army.invite", AuthorityRouteKind.Command)]
internal readonly struct RequestArmyInvite : ICommand
{
    [ProtoMember(1)] public readonly string ArmyId;
    [ProtoMember(2)] public readonly string PartyId;
    [ProtoMember(3)] public readonly AuthorityRequestHeader Header;
    public RequestArmyInvite(string armyId, string partyId, AuthorityRequestHeader header) { ArmyId = armyId; PartyId = partyId; Header = header; }
}

[ProtoContract(SkipConstructor = true)]
[AuthorityRoute("army.invite.respond", AuthorityRouteKind.Command)]
internal readonly struct RequestArmyInviteResponse : ICommand
{
    [ProtoMember(1)] public readonly string ArmyId;
    [ProtoMember(2)] public readonly bool Accept;
    [ProtoMember(3)] public readonly AuthorityRequestHeader Header;
    public RequestArmyInviteResponse(string armyId, bool accept, AuthorityRequestHeader header) { ArmyId = armyId; Accept = accept; Header = header; }
}

[ProtoContract(SkipConstructor = true)]
[AuthorityRoute("army.leave", AuthorityRouteKind.Command)]
internal readonly struct RequestLeaveArmy : ICommand
{
    [ProtoMember(1)] public readonly string ArmyId;
    [ProtoMember(2)] public readonly AuthorityRequestHeader Header;
    public RequestLeaveArmy(string armyId, AuthorityRequestHeader header) { ArmyId = armyId; Header = header; }
}

[ProtoContract(SkipConstructor = true)]
[AuthorityRoute("army.kick", AuthorityRouteKind.Command)]
internal readonly struct RequestKickArmyMember : ICommand
{
    [ProtoMember(1)] public readonly string ArmyId;
    [ProtoMember(2)] public readonly string PartyId;
    [ProtoMember(3)] public readonly AuthorityRequestHeader Header;
    public RequestKickArmyMember(string armyId, string partyId, AuthorityRequestHeader header) { ArmyId = armyId; PartyId = partyId; Header = header; }
}

[ProtoContract(SkipConstructor = true)]
[AuthorityRoute("army.boost-cohesion", AuthorityRouteKind.Command)]
internal readonly struct RequestBoostArmyCohesion : ICommand
{
    [ProtoMember(1)] public readonly string ArmyId;
    [ProtoMember(2)] public readonly float RequestedCohesion;
    [ProtoMember(3)] public readonly AuthorityRequestHeader Header;
    public RequestBoostArmyCohesion(string armyId, float requestedCohesion, AuthorityRequestHeader header) { ArmyId = armyId; RequestedCohesion = requestedCohesion; Header = header; }
}

[ProtoContract(SkipConstructor = true)]
[AuthorityRoute("army.objective.change", AuthorityRouteKind.Command)]
internal readonly struct RequestChangeArmyObjective : ICommand
{
    [ProtoMember(1)] public readonly string ArmyId;
    [ProtoMember(2)] public readonly string ObjectiveId;
    [ProtoMember(3)] public readonly bool IsSettlement;
    [ProtoMember(4)] public readonly AuthorityRequestHeader Header;
    public RequestChangeArmyObjective(string armyId, string objectiveId, bool isSettlement, AuthorityRequestHeader header) { ArmyId = armyId; ObjectiveId = objectiveId; IsSettlement = isSettlement; Header = header; }
}

[ProtoContract(SkipConstructor = true)]
internal readonly struct ArmyAuthorityResult : ICommand
{
    [ProtoMember(1)] public readonly string ArmyId;
    [ProtoMember(2)] public readonly string PartyId;
    [ProtoMember(3)] public readonly AuthorityResultHeader Header;
    public ArmyAuthorityResult(string armyId, string partyId, AuthorityResultHeader header) { ArmyId = armyId; PartyId = partyId; Header = header; }
}
