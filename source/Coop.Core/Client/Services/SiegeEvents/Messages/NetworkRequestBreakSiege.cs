using Common.Messaging;
using ProtoBuf;

namespace Coop.Core.Client.Services.SiegeEvents.Messages;

/// <summary>
/// Client asks the server to remove its party from its siege camp.
/// </summary>
[AuthorityRoute("siege.break", AuthorityRouteKind.Command)]
[ProtoContract(SkipConstructor = true)]
public record NetworkRequestBreakSiege : ICommand
{
    [ProtoMember(1)]
    public string PartyId { get; }

    /// <summary>
    /// Whether the approval should finish the requester's local encounter and menu. Native flows
    /// that continue after publishing the request set this false so their own continuation remains.
    /// </summary>
    [ProtoMember(2)]
    public bool FinishLocalMenus { get; }

    [ProtoMember(3)]
    public AuthorityRequestHeader Header { get; }

    public NetworkRequestBreakSiege(
        string partyId,
        bool finishLocalMenus = true,
        AuthorityRequestHeader header = default)
    {
        PartyId = partyId;
        FinishLocalMenus = finishLocalMenus;
        Header = header;
    }
}
