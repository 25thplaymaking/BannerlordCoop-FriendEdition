using Common.Messaging;
using ProtoBuf;

namespace GameInterface.Services.Players.Messages;

public readonly struct IndependentPlayerPartySelected : IEvent { }
public readonly struct LeavePlayerClanSelected : IEvent { }

[ProtoContract]
internal readonly struct RequestIndependentPlayerParty : ICommand
{
    [ProtoMember(1)] public readonly bool Requested;
    public RequestIndependentPlayerParty(bool requested) => Requested = requested;
}

[ProtoContract]
internal readonly struct RequestLeavePlayerClan : ICommand
{
    [ProtoMember(1)] public readonly bool Requested;
    public RequestLeavePlayerClan(bool requested) => Requested = requested;
}

[ProtoContract]
public readonly struct ClanLeaderReturnedNotification : ICommand
{
    [ProtoMember(1)] public readonly bool Delivered;
    public ClanLeaderReturnedNotification(bool delivered) => Delivered = delivered;
}

[ProtoContract(SkipConstructor = true)]
internal readonly struct IndependentPlayerPartyApprovalRequested : ICommand
{
    [ProtoMember(1)] public readonly string RequestId;
    [ProtoMember(2)] public readonly string PlayerName;

    public IndependentPlayerPartyApprovalRequested(string requestId, string playerName)
    {
        RequestId = requestId;
        PlayerName = playerName;
    }
}

[ProtoContract(SkipConstructor = true)]
internal readonly struct RespondIndependentPlayerParty : ICommand
{
    [ProtoMember(1)] public readonly string RequestId;
    [ProtoMember(2)] public readonly bool Approved;

    public RespondIndependentPlayerParty(string requestId, bool approved)
    {
        RequestId = requestId;
        Approved = approved;
    }
}
