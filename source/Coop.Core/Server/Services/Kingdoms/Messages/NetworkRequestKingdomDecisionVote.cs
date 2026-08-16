using Common.Messaging;
using GameInterface.Services.Kingdoms.Data;
using ProtoBuf;

namespace Coop.Core.Server.Services.Kingdoms.Messages
{
    [ProtoContract(SkipConstructor = true)]
    [AuthorityRoute("kingdom.decision.vote", AuthorityRouteKind.Command)]
    public class NetworkRequestKingdomDecisionVote : ICommand
    {
        [ProtoMember(1)]
        public string ControllerId { get; }
        [ProtoMember(2)]
        public KingdomDecisionVoteData VoteData { get; }
        [ProtoMember(3)]
        public AuthorityRequestHeader Header { get; }

        public NetworkRequestKingdomDecisionVote(
            string controllerId,
            KingdomDecisionVoteData voteData,
            AuthorityRequestHeader header = default)
        {
            ControllerId = controllerId;
            VoteData = voteData;
            Header = header;
        }
    }
}
