using Common.Messaging;
using E2E.Tests.Environment.Instance;
using GameInterface.Configuration;
using GameInterface.Services.MapEvents.Messages.Leave;
using GameInterface.Services.MapEvents.Messages.Start;
using System;
using System.Threading;
using TaleWorlds.Core;

namespace E2E.Tests.Util;

/// <summary>Builds canonical join/leave route requests for transport-level authority tests.</summary>
internal static class BattleJoinLeaveTestRequest
{
    private static long nextRequestId;

    public static NetworkRequestJoinBattle Join(EnvironmentInstance client, string mapEventId, string partyId,
        BattleSideEnum side) => new(Header(client), mapEventId, partyId, side);

    public static NetworkRequestLeaveBattle Leave(EnvironmentInstance client, string partyId, string mapEventId,
        bool finishLocalMenus = true) => new(Header(client), partyId, mapEventId, finishLocalMenus);

    private static AuthorityRequestHeader Header(EnvironmentInstance client)
    {
        IModConfigAuthority authority = client.Resolve<IModConfigAuthority>();
        if (!authority.TryGetCurrent(out ModConfigSnapshot snapshot))
            throw new InvalidOperationException("Client has no authoritative mod-config snapshot.");

        return new AuthorityRequestHeader(snapshot.ProtocolVersion, snapshot.SessionId,
            Interlocked.Increment(ref nextRequestId), snapshot.Revision);
    }
}
