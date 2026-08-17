using Common.Messaging;
using ProtoBuf;
using TaleWorlds.CampaignSystem.Actions;

namespace GameInterface.Services.PlayerCaptivityService.Messages;

/// <summary>
/// [Server → the captive's client] The terms on which this captivity can be ended by choice.
/// </summary>
/// <remarks>
/// The server prices the ransom when it records the capture and keeps it. The client is told the number
/// only so its menu quotes the same figure it will be charged; the client never sends a price back, it
/// sends <see cref="NetworkPlayerCaptivityReleaseRequest.OfferId"/>. That is the whole point of the
/// offer: nothing about the transaction is client-chosen.
/// </remarks>
[ProtoContract]
internal readonly struct NetworkPlayerCaptivityReleaseOffer : IEvent
{
    [ProtoMember(1)]
    public readonly string OfferId;
    [ProtoMember(2)]
    public readonly int RansomAmount;

    public NetworkPlayerCaptivityReleaseOffer(string offerId, int ransomAmount)
    {
        OfferId = offerId;
        RansomAmount = ransomAmount;
    }
}

/// <summary>
/// [Client → server] The captive player chose to end their captivity, quoting a server-issued offer.
/// </summary>
/// <remarks>
/// Carries an offer id and the flavour of release, and nothing else. The hero, the captor, the price and
/// the release position all come from the server's own record of the offer — the previous message let a
/// client name its own ransom and its own reappearance position, which is why it was never wired up.
/// </remarks>
[ProtoContract]
internal readonly struct NetworkPlayerCaptivityReleaseRequest : IEvent
{
    [ProtoMember(1)]
    public readonly string OfferId;
    [ProtoMember(2)]
    public readonly EndCaptivityDetail Detail;

    public NetworkPlayerCaptivityReleaseRequest(string offerId, EndCaptivityDetail detail)
    {
        OfferId = offerId;
        Detail = detail;
    }
}
