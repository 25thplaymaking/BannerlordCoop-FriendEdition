using Common.Messaging;
using GameInterface.Services.Inventory.Data;
using GameInterface.Services.TroopRosters.Data;
using ProtoBuf;
using System;

namespace GameInterface.Services.Bandits.Messages;

[ProtoContract(SkipConstructor = true)]
[AuthorityRoute("barter.bandit.safe-passage", AuthorityRouteKind.Command)]
internal readonly struct NetworkRequestBanditBarter : ICommand
{
    [ProtoMember(1)] public readonly string BanditPartyId;
    [ProtoMember(2)] public readonly int PlayerGold;
    [ProtoMember(3)] public readonly ItemRosterElementData[] PlayerItems;
    [ProtoMember(4)] public readonly TroopRosterElementData[] PlayerPrisoners;
    [ProtoMember(5)] public readonly AuthorityRequestHeader Header;
    [ProtoMember(6)] public readonly string RequestId;

    public NetworkRequestBanditBarter(string banditPartyId, int playerGold,
        ItemRosterElementData[] playerItems, TroopRosterElementData[] playerPrisoners,
        AuthorityRequestHeader header = default)
    {
        BanditPartyId = banditPartyId;
        PlayerGold = playerGold;
        PlayerItems = playerItems ?? Array.Empty<ItemRosterElementData>();
        PlayerPrisoners = playerPrisoners ?? Array.Empty<TroopRosterElementData>();
        Header = header;
        RequestId = null;
    }

    // Read compatibility only for pre-authority saves/tests; the router rejects its headerless form.
    public NetworkRequestBanditBarter(string banditPartyId, int playerGold,
        ItemRosterElementData[] playerItems, TroopRosterElementData[] playerPrisoners, string requestId)
        : this(banditPartyId, playerGold, playerItems, playerPrisoners)
    { RequestId = requestId; }
}

/// <summary>Canonical post-transaction state, sent before its terminal result.</summary>
[ProtoContract(SkipConstructor = true)]
internal readonly struct NetworkBanditSafePassageDelta : ICommand
{
    [ProtoMember(1)] public readonly string SessionId;
    [ProtoMember(2)] public readonly long AuthorityRequestId;
    [ProtoMember(3)] public readonly long CommittedRevision;
    [ProtoMember(4)] public readonly string PlayerPartyId;
    [ProtoMember(5)] public readonly string BanditPartyId;
    [ProtoMember(6)] public readonly int PlayerGold;
    [ProtoMember(7)] public readonly int BanditGold;
    [ProtoMember(8)] public readonly string PlayerItemRosterId;
    [ProtoMember(9)] public readonly string PlayerPrisonRosterId;
    [ProtoMember(10)] public readonly string BanditItemRosterId;
    [ProtoMember(11)] public readonly string BanditPrisonRosterId;
    [ProtoMember(12)] public readonly long PlayerItemRosterHash;
    [ProtoMember(13)] public readonly long PlayerPrisonRosterHash;
    [ProtoMember(14)] public readonly long BanditItemRosterHash;
    [ProtoMember(15)] public readonly long BanditPrisonRosterHash;
    [ProtoMember(16)] public readonly ItemRosterElementData[] PlayerItems;
    [ProtoMember(17)] public readonly TroopRosterElementData[] PlayerPrisoners;
    [ProtoMember(18)] public readonly ItemRosterElementData[] BanditItems;
    [ProtoMember(19)] public readonly TroopRosterElementData[] BanditPrisoners;
    [ProtoMember(20)] public readonly BanditSafePassageProtectionData[] Protections;
    [ProtoMember(21)] public readonly BanditSafePassageInteractionData Interaction;

    public NetworkBanditSafePassageDelta(AuthorityRequestHeader header, string playerPartyId,
        string banditPartyId, int playerGold, int banditGold, string playerItemRosterId,
        string playerPrisonRosterId, string banditItemRosterId, string banditPrisonRosterId,
        long playerItemRosterHash, long playerPrisonRosterHash, long banditItemRosterHash,
        long banditPrisonRosterHash, ItemRosterElementData[] playerItems,
        TroopRosterElementData[] playerPrisoners, ItemRosterElementData[] banditItems,
        TroopRosterElementData[] banditPrisoners, BanditSafePassageProtectionData[] protections,
        BanditSafePassageInteractionData interaction)
    {
        SessionId = header.SessionId; AuthorityRequestId = header.RequestId; CommittedRevision = header.ExpectedRevision;
        PlayerPartyId = playerPartyId; BanditPartyId = banditPartyId; PlayerGold = playerGold; BanditGold = banditGold;
        PlayerItemRosterId = playerItemRosterId; PlayerPrisonRosterId = playerPrisonRosterId;
        BanditItemRosterId = banditItemRosterId; BanditPrisonRosterId = banditPrisonRosterId;
        PlayerItemRosterHash = playerItemRosterHash; PlayerPrisonRosterHash = playerPrisonRosterHash;
        BanditItemRosterHash = banditItemRosterHash; BanditPrisonRosterHash = banditPrisonRosterHash;
        PlayerItems = playerItems ?? Array.Empty<ItemRosterElementData>();
        PlayerPrisoners = playerPrisoners ?? Array.Empty<TroopRosterElementData>();
        BanditItems = banditItems ?? Array.Empty<ItemRosterElementData>();
        BanditPrisoners = banditPrisoners ?? Array.Empty<TroopRosterElementData>();
        Protections = protections ?? Array.Empty<BanditSafePassageProtectionData>(); Interaction = interaction;
    }
}

[ProtoContract(SkipConstructor = true)]
internal readonly struct BanditSafePassageProtectionData
{
    [ProtoMember(1)] public readonly string EnemyPartyId;
    [ProtoMember(2)] public readonly long ProtectedUntilTicks;
    public BanditSafePassageProtectionData(string enemyPartyId, long protectedUntilTicks)
    { EnemyPartyId = enemyPartyId; ProtectedUntilTicks = protectedUntilTicks; }
}

[ProtoContract(SkipConstructor = true)]
internal readonly struct BanditSafePassageInteractionData
{
    [ProtoMember(1)] public readonly string PlayerHeroId;
    [ProtoMember(2)] public readonly string BanditPartyId;
    [ProtoMember(3)] public readonly int Interaction;
    [ProtoMember(4)] public readonly bool EngagementEnded;
    public BanditSafePassageInteractionData(string playerHeroId, string banditPartyId, int interaction, bool engagementEnded)
    { PlayerHeroId = playerHeroId; BanditPartyId = banditPartyId; Interaction = interaction; EngagementEnded = engagementEnded; }
}

[ProtoContract(SkipConstructor = true)]
internal readonly struct NetworkBanditBarterResult : ICommand
{
    [ProtoMember(1)] public readonly string BanditPartyId;
    [ProtoMember(2)] public readonly AuthorityResultHeader Header;
    [ProtoMember(3)] public readonly int PlayerGold;
    [ProtoMember(4)] public readonly string RequestId;
    [ProtoMember(5)] public readonly string Reason;

    public NetworkBanditBarterResult(string banditPartyId, AuthorityResultHeader header, int playerGold)
    { BanditPartyId = banditPartyId; Header = header; PlayerGold = playerGold; RequestId = null; Reason = header.ReasonCode; }

    public NetworkBanditBarterResult(string banditPartyId, bool accepted, int playerGold,
        string reason = null, string requestId = null)
    {
        BanditPartyId = banditPartyId;
        Header = default;
        PlayerGold = playerGold;
        RequestId = requestId;
        Reason = reason;
    }

    public bool Accepted => Header.Status == AuthorityResultStatus.Accepted ||
        (Header.RequestId == 0 && string.IsNullOrEmpty(Reason));
}
