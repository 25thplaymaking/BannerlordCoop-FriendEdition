using Common.Messaging;
using GameInterface.Services.TroopRosters.Data;
using ProtoBuf;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Roster;
using TaleWorlds.CampaignSystem.Settlements;

namespace GameInterface.Services.Alleys.Messages;

/// <summary>Server-authoritative alley-management commands.</summary>
public enum AlleyManagementOperation
{
    Acquire, Clear, Abandon, ChangeOverseer, SetGarrison, RecruitTroops,
}

// --- Local events: published on the requesting client from the menu/screen patches ---

/// <summary>
/// Player asked to abandon an owned alley. <see cref="FromClanScreen"/> mirrors vanilla
/// AbandonTheAlley(fromClanScreen): a clan-screen abandon forfeits the garrison, a menu/dialog
/// abandon returns the garrison troops to the owner's party.
/// </summary>
public readonly struct AbandonAlleyRequested : IEvent
{
    public readonly Alley Alley;
    public readonly bool FromClanScreen;
    public AbandonAlleyRequested(Alley alley, bool fromClanScreen)
    {
        Alley = alley;
        FromClanScreen = fromClanScreen;
    }
}

/// <summary>Player picked a new overseer for an owned alley.</summary>
public readonly struct ChangeAlleyOverseerRequested : IEvent
{
    public readonly Alley Alley;
    public readonly Hero NewOverseer;
    public ChangeAlleyOverseerRequested(Alley alley, Hero newOverseer)
    {
        Alley = alley;
        NewOverseer = newOverseer;
    }
}

/// <summary>Player edited the alley garrison in the manage-troops party screen.</summary>
public readonly struct SetAlleyGarrisonRequested : IEvent
{
    public readonly Alley Alley;
    public readonly TroopRoster NewGarrison;
    public SetAlleyGarrisonRequested(Alley alley, TroopRoster newGarrison)
    {
        Alley = alley;
        NewGarrison = newGarrison;
    }
}

/// <summary>The player accepted the troop offer from their alley overseer.</summary>
public readonly struct RecruitAlleyTroopsRequested : IEvent
{
    public readonly Alley Alley;
    public readonly TroopRoster Troops;
    public RecruitAlleyTroopsRequested(Alley alley, TroopRoster troops)
    {
        Alley = alley;
        Troops = troops;
    }
}

/// <summary>
/// Player won the alley fight and took over the alley. The alley is owned by the acquiring player
/// (<see cref="Owner"/>) and run by the chosen clan member (<see cref="Overseer"/>) - these are
/// distinct, matching vanilla (owner = Hero.MainHero, overseer = AssignedClanMember). The fight is
/// a local solo mission, so only this authoritative result is sent to the server.
/// </summary>
public readonly struct AlleyAcquiredRequested : IEvent
{
    public readonly Alley Alley;
    public readonly Hero Owner;
    public readonly Hero Overseer;
    public readonly TroopRoster Garrison;
    public AlleyAcquiredRequested(Alley alley, Hero owner, Hero overseer, TroopRoster garrison)
    {
        Alley = alley;
        Owner = owner;
        Overseer = overseer;
        Garrison = garrison;
    }
}

/// <summary>Player won a local alley fight and chose to leave the defeated alley empty.</summary>
public readonly struct AlleyClearedRequested : IEvent
{
    public readonly Alley Alley;
    public AlleyClearedRequested(Alley alley) { Alley = alley; }
}

// --- Networked client -> server requests ---

[AuthorityRoute("alley.acquire", AuthorityRouteKind.Command)]
[ProtoContract(SkipConstructor = true)]
public readonly struct RequestAcquireAlley : ICommand
{
    [ProtoMember(1)]
    public readonly string AlleyId;
    [ProtoMember(2)]
    public readonly string OwnerId;
    [ProtoMember(3)]
    public readonly string OverseerId;
    [ProtoMember(4)]
    public readonly TroopRosterElementData[] Garrison;
    [ProtoMember(5)] public readonly AuthorityRequestHeader Header;
    public RequestAcquireAlley(string alleyId, string ownerId, string overseerId, TroopRosterElementData[] garrison)
    {
        AlleyId = alleyId;
        OwnerId = ownerId;
        OverseerId = overseerId;
        Garrison = garrison;
        Header = default;
    }
    public RequestAcquireAlley(string alleyId, string ownerId, string overseerId, TroopRosterElementData[] garrison, AuthorityRequestHeader header)
        : this(alleyId, ownerId, overseerId, garrison) => Header = header;
}

[AuthorityRoute("alley.clear", AuthorityRouteKind.Command)]
[ProtoContract(SkipConstructor = true)]
public readonly struct RequestClearAlley : ICommand
{
    [ProtoMember(1)]
    public readonly string AlleyId;
    [ProtoMember(2)] public readonly AuthorityRequestHeader Header;
    public RequestClearAlley(string alleyId) { AlleyId = alleyId; Header = default; }
    public RequestClearAlley(string alleyId, AuthorityRequestHeader header) : this(alleyId) => Header = header;
}

[AuthorityRoute("alley.abandon", AuthorityRouteKind.Command)]
[ProtoContract(SkipConstructor = true)]
public readonly struct RequestAbandonAlley : ICommand
{
    [ProtoMember(1)]
    public readonly string AlleyId;
    [ProtoMember(2)]
    public readonly bool FromClanScreen;
    [ProtoMember(3)] public readonly AuthorityRequestHeader Header;
    public RequestAbandonAlley(string alleyId, bool fromClanScreen)
    {
        AlleyId = alleyId;
        FromClanScreen = fromClanScreen;
        Header = default;
    }
    public RequestAbandonAlley(string alleyId, bool fromClanScreen, AuthorityRequestHeader header) : this(alleyId, fromClanScreen) => Header = header;
}

[AuthorityRoute("alley.overseer.change", AuthorityRouteKind.Command)]
[ProtoContract(SkipConstructor = true)]
public readonly struct RequestChangeAlleyOverseer : ICommand
{
    [ProtoMember(1)]
    public readonly string AlleyId;
    [ProtoMember(2)]
    public readonly string NewOverseerId;
    [ProtoMember(3)] public readonly AuthorityRequestHeader Header;
    public RequestChangeAlleyOverseer(string alleyId, string newOverseerId)
    {
        AlleyId = alleyId;
        NewOverseerId = newOverseerId;
        Header = default;
    }
    public RequestChangeAlleyOverseer(string alleyId, string newOverseerId, AuthorityRequestHeader header) : this(alleyId, newOverseerId) => Header = header;
}

[AuthorityRoute("alley.garrison.transfer", AuthorityRouteKind.Command)]
[ProtoContract(SkipConstructor = true)]
public readonly struct RequestSetAlleyGarrison : ICommand
{
    [ProtoMember(1)]
    public readonly string AlleyId;
    [ProtoMember(2)]
    public readonly TroopRosterElementData[] Garrison;
    [ProtoMember(3)] public readonly AuthorityRequestHeader Header;
    public RequestSetAlleyGarrison(string alleyId, TroopRosterElementData[] garrison)
    {
        AlleyId = alleyId;
        Garrison = garrison;
        Header = default;
    }
    public RequestSetAlleyGarrison(string alleyId, TroopRosterElementData[] garrison, AuthorityRequestHeader header) : this(alleyId, garrison) => Header = header;
}

[AuthorityRoute("alley.recruit", AuthorityRouteKind.Command)]
[ProtoContract(SkipConstructor = true)]
public readonly struct RequestRecruitAlleyTroops : ICommand
{
    [ProtoMember(1)]
    public readonly string AlleyId;
    [ProtoMember(2)]
    public readonly TroopRosterElementData[] Troops;
    [ProtoMember(3)] public readonly AuthorityRequestHeader Header;
    public RequestRecruitAlleyTroops(string alleyId, TroopRosterElementData[] troops)
    {
        AlleyId = alleyId;
        Troops = troops;
        Header = default;
    }
    public RequestRecruitAlleyTroops(string alleyId, TroopRosterElementData[] troops, AuthorityRequestHeader header) : this(alleyId, troops) => Header = header;
}

[ProtoContract(SkipConstructor = true)]
public readonly struct NetworkAlleyManagementResult : ICommand
{
    [ProtoMember(1)] public readonly AuthorityResultHeader Header;
    [ProtoMember(2)] public readonly AlleyManagementOperation Operation;
    [ProtoMember(3)] public readonly string AlleyId;
    [ProtoMember(4)] public readonly string OverseerId;
    [ProtoMember(5)] public readonly bool FromClanScreen;
    [ProtoMember(6)] public readonly string RosterKey;
    public NetworkAlleyManagementResult(AuthorityResultHeader header, AlleyManagementOperation operation, string alleyId, string overseerId, bool fromClanScreen, string rosterKey)
    { Header = header; Operation = operation; AlleyId = alleyId; OverseerId = overseerId; FromClanScreen = fromClanScreen; RosterKey = rosterKey; }
}

// --- Networked server -> clients broadcasts ---

[ProtoContract(SkipConstructor = true)]
public readonly struct NetworkAlleyManagementUpdated : ICommand
{
    [ProtoMember(1)]
    public readonly string AlleyId;
    [ProtoMember(2)]
    public readonly string OverseerId;
    [ProtoMember(3)]
    public readonly TroopRosterElementData[] Garrison;
    [ProtoMember(4)]
    public readonly long LastRecruitTimeTicks;
    [ProtoMember(5)] public readonly string SessionId;
    [ProtoMember(6)] public readonly long AuthorityRequestId;
    [ProtoMember(7)] public readonly long CommittedRevision;
    public NetworkAlleyManagementUpdated(
        string alleyId,
        string overseerId,
        TroopRosterElementData[] garrison,
        long lastRecruitTimeTicks)
    {
        AlleyId = alleyId;
        OverseerId = overseerId;
        Garrison = garrison;
        LastRecruitTimeTicks = lastRecruitTimeTicks;
        SessionId = null;
        AuthorityRequestId = 0;
        CommittedRevision = 0;
    }
    public NetworkAlleyManagementUpdated(string alleyId, string overseerId, TroopRosterElementData[] garrison, long lastRecruitTimeTicks, AuthorityRequestHeader header)
        : this(alleyId, overseerId, garrison, lastRecruitTimeTicks)
    { SessionId = header.SessionId; AuthorityRequestId = header.RequestId; CommittedRevision = header.ExpectedRevision; }
}

[ProtoContract(SkipConstructor = true)]
public readonly struct NetworkAlleyManagementRemoved : ICommand
{
    [ProtoMember(1)]
    public readonly string AlleyId;
    [ProtoMember(2)] public readonly string SessionId;
    [ProtoMember(3)] public readonly long AuthorityRequestId;
    [ProtoMember(4)] public readonly long CommittedRevision;
    public NetworkAlleyManagementRemoved(string alleyId) { AlleyId = alleyId; SessionId = null; AuthorityRequestId = 0; CommittedRevision = 0; }
    public NetworkAlleyManagementRemoved(string alleyId, AuthorityRequestHeader header) : this(alleyId)
    { SessionId = header.SessionId; AuthorityRequestId = header.RequestId; CommittedRevision = header.ExpectedRevision; }
}
