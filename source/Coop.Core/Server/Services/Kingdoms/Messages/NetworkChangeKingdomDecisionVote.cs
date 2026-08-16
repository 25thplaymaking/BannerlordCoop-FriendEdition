using Common.Messaging;
using GameInterface.Services.Kingdoms.Data;
using ProtoBuf;

namespace Coop.Core.Server.Services.Kingdoms.Messages
{
    [ProtoContract(SkipConstructor = true)]
    public class NetworkChangeKingdomDecisionVote : ICommand
    {
        [ProtoMember(1)]
        public string ClanId { get; }
        [ProtoMember(2)]
        public KingdomDecisionVoteData VoteData { get; }
        [ProtoMember(3)]
        public string SessionId { get; }
        [ProtoMember(4)]
        public long AuthorityRequestId { get; }
        [ProtoMember(5)]
        public long CommittedRevision { get; }
        [ProtoMember(6)]
        public string AuthorityControllerId { get; }

        public NetworkChangeKingdomDecisionVote(
            string clanId,
            KingdomDecisionVoteData voteData,
            string authorityControllerId = null,
            AuthorityRequestHeader correlation = default)
        {
            ClanId = clanId;
            VoteData = voteData;
            SessionId = correlation.SessionId;
            AuthorityRequestId = correlation.RequestId;
            CommittedRevision = correlation.ExpectedRevision;
            AuthorityControllerId = authorityControllerId;
        }
    }
}
