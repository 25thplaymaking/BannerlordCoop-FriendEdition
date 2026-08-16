using Common.Messaging;
using ProtoBuf;

namespace Coop.Core.Server.Services.Kingdoms.Messages;

/// <summary>
/// Notifies all clients that a kingdom rename was applied
/// </summary>
[ProtoContract(SkipConstructor = true)]
public class NetworkKingdomNameChanged : ICommand
{
    [ProtoMember(1)]
    public string KingdomId { get; }
    [ProtoMember(2)]
    public string Name { get; }
    [ProtoMember(3)]
    public string SessionId { get; }
    [ProtoMember(4)]
    public long AuthorityRequestId { get; }
    [ProtoMember(5)]
    public long CommittedRevision { get; }
    [ProtoMember(6)]
    public string AuthorityControllerId { get; }
    [ProtoMember(7)]
    public string FullName { get; }
    [ProtoMember(8)]
    public string InformalName { get; }

    public NetworkKingdomNameChanged(string kingdomId)
        : this(kingdomId, null, null, null, null, default)
    {
    }

    public NetworkKingdomNameChanged(
        string kingdomId,
        string name,
        string fullName,
        string informalName,
        string authorityControllerId,
        AuthorityRequestHeader correlation = default)
    {
        KingdomId = kingdomId;
        Name = name;
        FullName = fullName;
        InformalName = informalName;
        SessionId = correlation.SessionId;
        AuthorityRequestId = correlation.RequestId;
        CommittedRevision = correlation.ExpectedRevision;
        AuthorityControllerId = authorityControllerId;
    }
}
