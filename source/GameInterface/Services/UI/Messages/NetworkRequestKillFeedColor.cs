using Common.Messaging;
using GameInterface.Services.AuthorityRequests;
using ProtoBuf;

namespace GameInterface.Services.UI.Messages;

[ProtoContract(SkipConstructor = true)]
[AuthorityRoute("preference.killfeed-color", AuthorityRouteKind.Command)]
public readonly struct NetworkRequestKillFeedColor : ICommand
{
    [ProtoMember(1)]
    public readonly int Red;

    [ProtoMember(2)]
    public readonly int Green;

    [ProtoMember(3)]
    public readonly int Blue;

    [ProtoMember(4)]
    public readonly AuthorityRequestHeader Header;

    public NetworkRequestKillFeedColor(int red, int green, int blue)
        : this(red, green, blue, default)
    {
    }

    public NetworkRequestKillFeedColor(int red, int green, int blue, AuthorityRequestHeader header)
    {
        Red = red;
        Green = green;
        Blue = blue;
        Header = header;
    }
}
