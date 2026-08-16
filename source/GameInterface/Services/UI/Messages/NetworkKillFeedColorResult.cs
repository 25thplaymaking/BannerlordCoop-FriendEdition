using Common.Messaging;
using ProtoBuf;

namespace GameInterface.Services.UI.Messages;

/// <summary>Terminal response for an authoritative kill-feed colour update.</summary>
[ProtoContract(SkipConstructor = true)]
public readonly struct NetworkKillFeedColorResult : ICommand
{
    [ProtoMember(1)] public readonly string ControllerId;
    [ProtoMember(2)] public readonly int Red;
    [ProtoMember(3)] public readonly int Green;
    [ProtoMember(4)] public readonly int Blue;
    [ProtoMember(5)] public readonly AuthorityResultHeader Header;

    public NetworkKillFeedColorResult(string controllerId, int red, int green, int blue, AuthorityResultHeader header)
    {
        ControllerId = controllerId;
        Red = red;
        Green = green;
        Blue = blue;
        Header = header;
    }
}
