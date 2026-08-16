using Common.Messaging;
using ProtoBuf;

namespace GameInterface.Services.Players.Messages;

public readonly struct IndependentPlayerPartySelected : IEvent { }
public readonly struct LeavePlayerClanSelected : IEvent { }

[ProtoContract]
public readonly struct ClanLeaderReturnedNotification : ICommand
{
    [ProtoMember(1)] public readonly bool Delivered;
    public ClanLeaderReturnedNotification(bool delivered) => Delivered = delivered;
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
