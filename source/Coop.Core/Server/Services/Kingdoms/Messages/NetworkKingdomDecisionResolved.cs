using Common.Messaging;
using ProtoBuf;

namespace Coop.Core.Server.Services.Kingdoms.Messages
{
    [ProtoContract(SkipConstructor = true)]
    public class NetworkKingdomDecisionResolved : ICommand
    {
        [ProtoMember(1)]
        public string KingdomId { get; }
        [ProtoMember(2)]
        public int DecisionIndex { get; }
        [ProtoMember(3)]
        public int OutcomeIndex { get; }
        [ProtoMember(4)]
        public bool IsPlayerDecision { get; }
        [ProtoMember(5)]
        public string OutcomeKey { get; }
        [ProtoMember(6)]
        public string NotificationText { get; }
        [ProtoMember(7)]
        public string SessionId { get; }
        [ProtoMember(8)]
        public long AuthorityRequestId { get; }
        [ProtoMember(9)]
        public long CommittedRevision { get; }
        [ProtoMember(10)]
        public string AuthorityControllerId { get; }

        public NetworkKingdomDecisionResolved(string kingdomId, int decisionIndex, int outcomeIndex, bool isPlayerDecision)
            : this(kingdomId, decisionIndex, outcomeIndex, isPlayerDecision, null)
        {
        }

        public NetworkKingdomDecisionResolved(string kingdomId, int decisionIndex, int outcomeIndex, bool isPlayerDecision, string outcomeKey)
            : this(kingdomId, decisionIndex, outcomeIndex, isPlayerDecision, outcomeKey, null, null, default)
        {
        }

        public NetworkKingdomDecisionResolved(
            string kingdomId,
            int decisionIndex,
            int outcomeIndex,
            bool isPlayerDecision,
            string outcomeKey,
            string notificationText,
            string authorityControllerId = null,
            AuthorityRequestHeader correlation = default)
        {
            KingdomId = kingdomId;
            DecisionIndex = decisionIndex;
            OutcomeIndex = outcomeIndex;
            IsPlayerDecision = isPlayerDecision;
            OutcomeKey = outcomeKey;
            NotificationText = notificationText;
            SessionId = correlation.SessionId;
            AuthorityRequestId = correlation.RequestId;
            CommittedRevision = correlation.ExpectedRevision;
            AuthorityControllerId = authorityControllerId;
        }
    }
}
