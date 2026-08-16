using Common.Messaging;
using Coop.Core.Client.Services.Settlements.Messages;
using GameInterface.Services.Armies.Audit;
using GameInterface.Services.Heroes.Audit;
using GameInterface.Services.Heroes.Messages.RomanceFlow;
using GameInterface.Services.MapEventParties.Messages;
using GameInterface.Services.MobileParties.Audit;
using GameInterface.Services.MobileParties.Messages;
using GameInterface.Services.SettlementComponents.Audit;
using GameInterface.Services.Settlements.Audit;
using GameInterface.Services.Tournaments.Messages;
using GameInterface.Services.UI.Messages;
using ProtoBuf;
using System;
using System.Net;
using Xunit;

namespace Coop.Tests.Authority;

public sealed class LegacyRequestDispositionContractTests
{
    [Fact]
    public void AuditRequests_AreReadOnlyQueryContractsWithoutAuthorityRoutes()
    {
        AssertReadOnlyQuery<RequestArmyAudit>();
        AssertReadOnlyQuery<RequestHeroAudit>();
        AssertReadOnlyQuery<RequestMobilePartyAudit>();
        AssertReadOnlyQuery<NetworkRequestMercenaryStockAudit>();
        AssertReadOnlyQuery<RequestSettlementComponentAudit>();
        AssertReadOnlyQuery<RequestSettlementAudit>();
        AssertReadOnlyQuery<NetworkRequestSettlementAudit>();
    }

    [Fact]
    public void RejectionMessages_AreServerResultReplicationContracts()
    {
        var romance = new NetworkRomanceRequestRejected("romance rejected");
        var tournament = new NetworkTournamentRequestRejected("town", "tournament rejected");

        Assert.Equal("romance rejected", romance.Reason);
        Assert.Equal("town", tournament.TownId);
        Assert.Equal("tournament rejected", tournament.Reason);
        AssertReplication<NetworkRomanceRequestRejected>();
        AssertReplication<NetworkTournamentRequestRejected>();
    }

    [Fact]
    public void HostJoinAndMapEventUpdateRequests_AreLocalBrokerControls()
    {
        var host = new AttemptHost("save", "password");
        var join = new AttemptJoin(IPAddress.Loopback, 7210, "password");

        Assert.Equal("save", host.SaveName);
        Assert.Equal(IPAddress.Loopback, join.Address);
        AssertLocalControl<AttemptHost>();
        AssertLocalControl<AttemptJoin>();
        AssertLocalControl<RequestMapEventPartyUpdate>();
    }

    private static void AssertReadOnlyQuery<T>() where T : ICommand
    {
        Assert.NotNull(Attribute.GetCustomAttribute(typeof(T), typeof(ProtoContractAttribute)));
        Assert.Null(Attribute.GetCustomAttribute(typeof(T), typeof(AuthorityRouteAttribute)));
    }

    private static void AssertReplication<T>() where T : ICommand
    {
        Assert.NotNull(Attribute.GetCustomAttribute(typeof(T), typeof(ProtoContractAttribute)));
        Assert.Null(Attribute.GetCustomAttribute(typeof(T), typeof(AuthorityRouteAttribute)));
    }

    private static void AssertLocalControl<T>() where T : ICommand
    {
        Assert.Null(Attribute.GetCustomAttribute(typeof(T), typeof(ProtoContractAttribute)));
        Assert.Null(Attribute.GetCustomAttribute(typeof(T), typeof(AuthorityRouteAttribute)));
    }
}
