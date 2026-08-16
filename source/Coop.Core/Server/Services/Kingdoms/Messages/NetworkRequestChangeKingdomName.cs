using Common.Messaging;
using ProtoBuf;

namespace Coop.Core.Server.Services.Kingdoms.Messages;

/// <summary>
/// Requests that the server rename a kingdom
/// The server must validate the originating peer's ability.
/// </summary>
[ProtoContract(SkipConstructor = true)]
[AuthorityRoute("kingdom.rename", AuthorityRouteKind.Command)]
public class NetworkRequestChangeKingdomName : ICommand
{
    [ProtoMember(1)]
    public string KingdomId { get; }
    
    [ProtoMember(2)]
    public string Name { get; }
    [ProtoMember(3)]
    public AuthorityRequestHeader Header { get; }

    public NetworkRequestChangeKingdomName(
        string kingdomId,
        string name,
        AuthorityRequestHeader header = default)
    {
        KingdomId = kingdomId;
        Name = name;
        Header = header;
    }
}
