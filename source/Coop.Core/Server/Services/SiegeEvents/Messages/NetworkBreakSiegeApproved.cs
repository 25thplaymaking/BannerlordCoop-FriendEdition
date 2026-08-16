using Common.Messaging;
using ProtoBuf;

namespace Coop.Core.Server.Services.SiegeEvents.Messages;

public enum SiegeBreakOutcome
{
    Rejected,
    Applied,
    AlreadyLeft,
}

/// <summary>
/// Server result for a request to leave a siege camp.
/// </summary>
[ProtoContract(SkipConstructor = true)]
public record NetworkBreakSiegeApproved : IEvent
{
    [ProtoMember(1)]
    public SiegeBreakOutcome Outcome { get; }

    /// <summary>
    /// Echo of the request's local-continuation flag.
    /// </summary>
    [ProtoMember(2)]
    public bool FinishLocalMenus { get; }

    /// <summary>
    /// True when an active siege assault owns the leave.
    /// Its replicated battle-leave path removes both battle and camp state and performs the client cleanup.
    /// </summary>
    [ProtoMember(3)]
    public bool BattleLeaveApplied { get; }

    /// <summary>Correlation and status for the canonical authority route.</summary>
    [ProtoMember(4)]
    public AuthorityResultHeader Header { get; }

    /// <summary>The party whose camp/battle membership was authoritatively removed.</summary>
    [ProtoMember(5)]
    public string PartyId { get; }

    /// <summary>Whether another besieging party still owns the siege after this leave.</summary>
    [ProtoMember(6)]
    public bool SiegeContinues { get; }

    public NetworkBreakSiegeApproved(
        SiegeBreakOutcome outcome,
        bool finishLocalMenus = true,
        bool battleLeaveApplied = false,
        AuthorityResultHeader header = default,
        string partyId = null,
        bool siegeContinues = false)
    {
        Outcome = outcome;
        FinishLocalMenus = finishLocalMenus;
        BattleLeaveApplied = battleLeaveApplied;
        Header = header;
        PartyId = partyId;
        SiegeContinues = siegeContinues;
    }
}
