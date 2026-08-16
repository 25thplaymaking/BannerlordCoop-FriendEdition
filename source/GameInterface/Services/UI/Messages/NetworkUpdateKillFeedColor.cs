using Common.Messaging;
using ProtoBuf;

namespace GameInterface.Services.UI.Messages;

[ProtoContract(SkipConstructor = true)]
public readonly struct NetworkUpdateKillFeedColor : IEvent
{
    [ProtoMember(1)]
    public readonly string ControllerId;

    [ProtoMember(2)]
    public readonly int Red;

    [ProtoMember(3)]
    public readonly int Green;

    [ProtoMember(4)]
    public readonly int Blue;

    // When this update completes an authority command, it binds the replica state to the
    // accepted request. Snapshot/rejoin updates intentionally retain the default header.
    [ProtoMember(5)]
    public readonly AuthorityResultHeader Header;

    public NetworkUpdateKillFeedColor(string controllerId, int red, int green, int blue)
        : this(controllerId, red, green, blue, default)
    {
    }

    public NetworkUpdateKillFeedColor(string controllerId, int red, int green, int blue, AuthorityResultHeader header)
    {
        ControllerId = controllerId;
        Red = red;
        Green = green;
        Blue = blue;
        Header = header;
    }
}
