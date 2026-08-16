using Common.Messaging;
using E2E.Tests.Environment.Instance;
using GameInterface.Configuration;
using GameInterface.Services.MapEvents.Handlers;
using GameInterface.Services.MapEvents.Messages.Start;
using System.Threading;

namespace E2E.Tests.Util;

/// <summary>Builds canonical battle-start requests for transport-level route tests; it never exercises legacy wire shape.</summary>
internal static class BattleStartTestRequest
{
    private static long nextRequestId;

    public static NetworkBattleStartRequest Create(EnvironmentInstance client, BattleStartMode mode,
        string mapEventId, string attackerPartyId)
    {
        IModConfigAuthority authority = client.Resolve<IModConfigAuthority>();
        if (!authority.TryGetCurrent(out ModConfigSnapshot snapshot))
            throw new InvalidOperationException("Client has no authoritative mod-config snapshot.");

        return new NetworkBattleStartRequest(
            new AuthorityRequestHeader(snapshot.ProtocolVersion, snapshot.SessionId,
                Interlocked.Increment(ref nextRequestId), snapshot.Revision),
            (int)mode, mapEventId, attackerPartyId);
    }
}
