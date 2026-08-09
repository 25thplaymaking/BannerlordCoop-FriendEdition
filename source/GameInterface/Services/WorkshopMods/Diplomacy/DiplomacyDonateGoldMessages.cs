using Common.Messaging;
using ProtoBuf;
using TaleWorlds.CampaignSystem;

namespace GameInterface.Services.WorkshopMods.Diplomacy;

/// <summary>
/// The local player accepted Diplomacy's "Donate Gold" dialog; ask the server to apply it.
/// </summary>
/// <remarks>
/// Carries the acting hero explicitly so nothing downstream re-reads Hero.MainHero — the server
/// has none, and on a client it would silently mean "whoever is local" rather than the requester.
/// </remarks>
internal readonly struct DiplomacyGoldDonationAttempted : IEvent
{
    public readonly Hero Giver;
    public readonly Clan Clan;
    public readonly int Amount;

    public DiplomacyGoldDonationAttempted(Hero giver, Clan clan, int amount)
    {
        Giver = giver;
        Clan = clan;
        Amount = amount;
    }
}

/// <summary>
/// Client asks the server to apply a Diplomacy gold donation from its own hero to a clan.
/// </summary>
/// <remarks>
/// Carries ids and the player's chosen amount — never outcomes. The amount is intent (the slider
/// value the dialog asked for), not a cost the client computed: the server re-validates it against
/// the giver's authoritative gold and derives the relation gain itself, so a client cannot dictate
/// what the donation is worth.
/// </remarks>
[ProtoContract(SkipConstructor = true)]
internal record NetworkRequestDiplomacyDonateGold : ICommand
{
    [ProtoMember(1)]
    public string GiverHeroId { get; }

    [ProtoMember(2)]
    public string ClanId { get; }

    [ProtoMember(3)]
    public int Amount { get; }

    public NetworkRequestDiplomacyDonateGold(string giverHeroId, string clanId, int amount)
    {
        GiverHeroId = giverHeroId;
        ClanId = clanId;
        Amount = amount;
    }
}
