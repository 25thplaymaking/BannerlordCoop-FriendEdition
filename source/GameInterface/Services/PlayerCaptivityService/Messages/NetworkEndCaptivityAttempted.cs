using Common.Messaging;
using ProtoBuf;
using TaleWorlds.CampaignSystem.Actions;

namespace GameInterface.Services.PlayerCaptivityService.Messages;

/// <summary>
/// A client asks the server to release a hero it does not control — the party-screen "release prisoner"
/// action and the other native flows that reach <c>EndCaptivityAction.ApplyInternal</c> for a hero that
/// is not the local main hero.
/// </summary>
/// <remarks>
/// Only the prisoner is named. The captor is not, because the server reads it from the prisoner itself
/// and requires it to be the requesting player's own party — the client has no say in whose prisoner it
/// is releasing. The facilitator is likewise resolved server-side rather than trusted, and carries only
/// a relation change.
/// </remarks>
[ProtoContract]
internal readonly struct NetworkEndCaptivityAttempted : IEvent
{
    [ProtoMember(1)]
    public readonly string PrisonerId;
    [ProtoMember(2)]
    public readonly EndCaptivityDetail Detail;
    [ProtoMember(3)]
    public readonly string FacilitatorId;

    public NetworkEndCaptivityAttempted(string prisonerId, EndCaptivityDetail detail, string facilitatorId)
    {
        PrisonerId = prisonerId;
        Detail = detail;
        FacilitatorId = facilitatorId;
    }
}
