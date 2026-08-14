using ProtoBuf;

namespace GameInterface.Services.Players.Data;

[ProtoContract(SkipConstructor = true)]
public class Player
{
    [ProtoMember(1)]
    public readonly string ControllerId;
    [ProtoMember(2)]
    public readonly string HeroId;
    [ProtoMember(3)]
    public readonly string MobilePartyId;
    [ProtoMember(4)]
    public readonly string ClanId;
    [ProtoMember(5)]
    public readonly string CharacterObjectId;
    [ProtoMember(6)]
    private readonly string personalClanId;
    [ProtoMember(7)]
    public readonly PlayerClanMembershipMode ClanMembershipMode;
    [ProtoMember(8)]
    public readonly bool EmergencyDetached;

    public string PersonalClanId => personalClanId ?? ClanId;

    public Player(
        string controllerId,
        string heroId,
        string mobilePartyId,
        string clanId,
        string characterObjectId,
        string personalClanId = null,
        PlayerClanMembershipMode clanMembershipMode = PlayerClanMembershipMode.PersonalClan,
        bool emergencyDetached = false)
    {
        ControllerId = controllerId;
        HeroId = heroId;
        MobilePartyId = mobilePartyId;
        ClanId = clanId;
        CharacterObjectId = characterObjectId;
        this.personalClanId = personalClanId ?? clanId;
        ClanMembershipMode = clanMembershipMode;
        EmergencyDetached = emergencyDetached;
    }
}
