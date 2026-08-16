using GameInterface.Services.WorkshopMods.Diplomacy;
using Common.Messaging;
using ProtoBuf;
using System;
using System.IO;
using System.Linq;
using Xunit;

namespace GameInterface.Tests.Services.WorkshopMods.Diplomacy;

public sealed class DiplomacyOperationTests
{
    private static readonly string SessionId = Guid.NewGuid().ToString("N");

    [Fact]
    public void GameplayOperationCatalog_CoversEveryAuditedPlayerAction()
    {
        Assert.Equal(
            new[]
            {
                DiplomacyOperation.DonateGold,
                DiplomacyOperation.GrantFief,
                DiplomacyOperation.SendMessenger,
                DiplomacyOperation.MakePeace,
                DiplomacyOperation.DeclareWar,
                DiplomacyOperation.EndAlliance,
                DiplomacyOperation.FormNonAggressionPact,
                DiplomacyOperation.AcceptKeepFief,
                DiplomacyOperation.DeclineKeepFief,
                DiplomacyOperation.CompleteMessenger,
                DiplomacyOperation.CancelMessenger,
                DiplomacyOperation.AcknowledgeMessengerAccident,
            },
            Enum.GetValues<DiplomacyOperation>());
    }

    [Fact]
    public void Request_RoundTripsEveryAuthorityField()
    {
        var request = Request(
            DiplomacyOperation.GrantFief,
            targetId: "clan.target",
            secondaryTargetId: "settlement.source",
            intValue: 17);

        using var stream = new MemoryStream();
        Serializer.Serialize(stream, request);
        stream.Position = 0;
        var roundTrip = Serializer.Deserialize<NetworkRequestDiplomacyOperation>(stream);

        Assert.Equal(SessionId, roundTrip.SessionId);
        Assert.Equal(73, roundTrip.RequestId);
        Assert.Equal(11, roundTrip.ExpectedRevision);
        Assert.Equal(DiplomacyOperation.GrantFief, roundTrip.Operation);
        Assert.Equal("clan.target", roundTrip.TargetId);
        Assert.Equal("settlement.source", roundTrip.SecondaryTargetId);
        Assert.Equal(17, roundTrip.IntValue);
        Assert.Equal(7, roundTrip.Header.ProtocolVersion);
    }

    [Theory]
    [InlineData((int)DiplomacyOperation.DonateGold, "clan.target", "", 1, true)]
    [InlineData((int)DiplomacyOperation.GrantFief, "clan.target", "settlement.source", 0, true)]
    [InlineData((int)DiplomacyOperation.SendMessenger, "hero.target", "", 0, true)]
    [InlineData((int)DiplomacyOperation.MakePeace, "kingdom.a", "kingdom.b", 0, true)]
    [InlineData((int)DiplomacyOperation.DeclareWar, "kingdom.a", "kingdom.b", 0, true)]
    [InlineData((int)DiplomacyOperation.EndAlliance, "kingdom.a", "kingdom.b", 0, true)]
    [InlineData((int)DiplomacyOperation.FormNonAggressionPact, "kingdom.a", "kingdom.b", 0, true)]
    [InlineData((int)DiplomacyOperation.AcceptKeepFief, "settlement.source", "", 0, true)]
    [InlineData((int)DiplomacyOperation.DeclineKeepFief, "settlement.source", "", 0, true)]
    [InlineData((int)DiplomacyOperation.CompleteMessenger, "hero.target", "", 7, true)]
    [InlineData((int)DiplomacyOperation.CancelMessenger, "hero.target", "", 7, true)]
    [InlineData((int)DiplomacyOperation.AcknowledgeMessengerAccident, "hero.target", "", 7, true)]
    [InlineData((int)DiplomacyOperation.DonateGold, "clan.target", "", 0, false)]
    [InlineData((int)DiplomacyOperation.GrantFief, "", "settlement.source", 0, false)]
    [InlineData((int)DiplomacyOperation.SendMessenger, "", "", 0, false)]
    [InlineData((int)DiplomacyOperation.MakePeace, "kingdom.a", "", 0, false)]
    [InlineData((int)DiplomacyOperation.AcceptKeepFief, "", "", 0, false)]
    [InlineData((int)DiplomacyOperation.CompleteMessenger, "hero.target", "", 0, false)]
    public void Protocol_RequiresTheExactShapeForEachOperation(
        int operationValue,
        string targetId,
        string secondaryTargetId,
        int intValue,
        bool expected)
    {
        Assert.Equal(expected, DiplomacyOperationProtocol.IsRequestShapeValid(
            Request((DiplomacyOperation)operationValue, targetId, secondaryTargetId, intValue)));
    }

    [Fact]
    public void Protocol_RejectsMalformedEnvelopeAndControlCharacters()
    {
        Assert.False(DiplomacyOperationProtocol.IsRequestShapeValid(new NetworkRequestDiplomacyOperation(
            "not-a-session", 1, 0, DiplomacyOperation.SendMessenger, "hero", "", 0)));
        Assert.False(DiplomacyOperationProtocol.IsRequestShapeValid(new NetworkRequestDiplomacyOperation(
            SessionId, 0, 0, DiplomacyOperation.SendMessenger, "hero", "", 0)));
        Assert.False(DiplomacyOperationProtocol.IsRequestShapeValid(new NetworkRequestDiplomacyOperation(
            SessionId, 1, -1, DiplomacyOperation.SendMessenger, "hero", "", 0)));
        Assert.False(DiplomacyOperationProtocol.IsRequestShapeValid(new NetworkRequestDiplomacyOperation(
            SessionId, 1, 0, DiplomacyOperation.SendMessenger, "hero\nforged", "", 0)));
    }

    [Fact]
    public void Protocol_ValidatesServerResultsAndKeepFiefPrompts()
    {
        Assert.True(DiplomacyOperationProtocol.IsResultShapeValid(
            new NetworkDiplomacyOperationResult(
                SessionId, 1, DiplomacyOperation.SendMessenger,
                DiplomacyOperationStatus.Accepted, 3, "hero.target")));
        Assert.False(DiplomacyOperationProtocol.IsResultShapeValid(
            new NetworkDiplomacyOperationResult(
                SessionId, 0, DiplomacyOperation.SendMessenger,
                DiplomacyOperationStatus.Accepted, 3, "hero.target")));
        Assert.False(DiplomacyOperationProtocol.IsResultShapeValid(
            new NetworkDiplomacyOperationResult(
                SessionId, 1, DiplomacyOperation.SendMessenger,
                (DiplomacyOperationStatus)99, 3, "hero.target")));
        Assert.False(DiplomacyOperationProtocol.IsResultShapeValid(
            new NetworkDiplomacyOperationResult(
                SessionId, 1, DiplomacyOperation.SendMessenger,
                DiplomacyOperationStatus.Accepted, -1, "hero.target")));

        Assert.True(DiplomacyOperationProtocol.IsKeepFiefPromptShapeValid(
            new NetworkDiplomacyKeepFiefPrompt(SessionId, "settlement.source", 3)));
        Assert.False(DiplomacyOperationProtocol.IsKeepFiefPromptShapeValid(
            new NetworkDiplomacyKeepFiefPrompt(SessionId, "", 3)));
        Assert.False(DiplomacyOperationProtocol.IsKeepFiefPromptShapeValid(
            new NetworkDiplomacyKeepFiefPrompt("bad-session", "settlement.source", 3)));

        Assert.True(DiplomacyOperationProtocol.IsMessengerArrivalPromptShapeValid(
            new NetworkDiplomacyMessengerArrivalPrompt(SessionId, 7, "hero.target", 3)));
        Assert.False(DiplomacyOperationProtocol.IsMessengerArrivalPromptShapeValid(
            new NetworkDiplomacyMessengerArrivalPrompt(SessionId, 0, "hero.target", 3)));
        Assert.True(DiplomacyOperationProtocol.IsMessengerAccidentShapeValid(
            new NetworkDiplomacyMessengerAccident(SessionId, 7, "hero.target", 6)));
        Assert.False(DiplomacyOperationProtocol.IsMessengerAccidentShapeValid(
            new NetworkDiplomacyMessengerAccident(SessionId, 7, "hero.target", 7)));
    }

    [Fact]
    public void CommandKey_IsStableAndIncludesEveryIntentField()
    {
        var baseline = Request(DiplomacyOperation.MakePeace, "kingdom.a", "kingdom.b", 0);
        Assert.Equal(
            DiplomacyOperationProtocol.CommandKey(baseline),
            DiplomacyOperationProtocol.CommandKey(Request(
                DiplomacyOperation.MakePeace, "kingdom.a", "kingdom.b", 0)));

        Assert.Equal(4, new[]
        {
            baseline,
            Request(DiplomacyOperation.DeclareWar, "kingdom.a", "kingdom.b", 0),
            Request(DiplomacyOperation.MakePeace, "kingdom.b", "kingdom.a", 0),
            Request(DiplomacyOperation.MakePeace, "kingdom.a", "kingdom.b", 1),
        }.Select(DiplomacyOperationProtocol.CommandKey).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void GameplayEnvelope_DeclaresTheTypedAuthorityRouteAndEchoesTheFullCommandDigest()
    {
        var attribute = (AuthorityRouteAttribute)Attribute.GetCustomAttribute(
            typeof(NetworkRequestDiplomacyOperation), typeof(AuthorityRouteAttribute));
        Assert.NotNull(attribute);
        Assert.Equal("workshop.diplomacy.gameplay", attribute.RouteId);
        Assert.Equal(AuthorityRouteKind.Command, attribute.Kind);

        var request = Request(DiplomacyOperation.MakePeace, "kingdom.a", "kingdom.b");
        var result = new NetworkDiplomacyOperationResult(request.Header, request.Operation,
            DiplomacyOperationStatus.Accepted, AuthorityResultStatus.Accepted, null,
            DiplomacyOperationProtocol.CommandKey(request), 12, new string('a', 64),
            request.TargetId, request.SecondaryTargetId, request.IntValue);
        Assert.Equal(DiplomacyOperationProtocol.CommandKey(request), result.CommandDigest);
        Assert.Equal(request.Header.RequestId, result.Header.RequestId);
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    public void Capability_RequiresOperatorEnablementAndLiveRoute(
        bool optionEnabled,
        bool routeReady,
        bool expected)
    {
        Assert.Equal(expected, DiplomacyCapabilityPolicy.IsEnabled(optionEnabled, routeReady));
    }

    [Fact]
    public void ActorAuthority_AllowsEmbeddedClanMemberToSendMessengerOnly()
    {
        Assert.True(DiplomacyActorAuthority.CanExecute(
            DiplomacyOperation.SendMessenger,
            actorBelongsToRegisteredParty: true,
            actorLeadsRegisteredParty: false,
            actorHasClan: true));

        Assert.False(DiplomacyActorAuthority.CanExecute(
            DiplomacyOperation.GrantFief,
            actorBelongsToRegisteredParty: true,
            actorLeadsRegisteredParty: false,
            actorHasClan: true));

        Assert.False(DiplomacyActorAuthority.CanExecute(
            DiplomacyOperation.SendMessenger,
            actorBelongsToRegisteredParty: false,
            actorLeadsRegisteredParty: false,
            actorHasClan: true));
    }

    [Fact]
    public void MessengerAuthorityStore_PreservesTravelAcrossRestartAndRejectsWrongController()
    {
        var store = new DiplomacyMessengerAuthorityStore();
        Assert.True(store.TryDispatch("controller.a", "hero.target", arrivalTicks: 100, out int id));
        Assert.False(store.TryGetArrived("controller.a", id, "hero.target", nowTicks: 99, out _));
        Assert.True(store.TryGetArrived("controller.a", id, "hero.target", nowTicks: 100, out var arrived));
        Assert.False(store.TryGetArrived("controller.b", id, "hero.target", nowTicks: 100, out _));

        var restored = new DiplomacyMessengerAuthorityStore(
            store.Export(),
            store.NextId);
        Assert.True(restored.TryGetArrived(
            "controller.a", id, "hero.target", nowTicks: 100, out var restoredRecord));
        Assert.Equal(arrived.ArrivalTicks, restoredRecord.ArrivalTicks);
        Assert.False(restored.TryRemoveArrived(
            "controller.a", id, "hero.target", nowTicks: 99));
        Assert.True(restored.TryRemoveArrived(
            "controller.a", id, "hero.target", nowTicks: 100));
        Assert.Empty(restored.Export());
    }

    [Fact]
    public void MessengerAuthorityStore_MarksOneAuthoritativeAccidentUntilAcknowledged()
    {
        var store = new DiplomacyMessengerAuthorityStore();
        Assert.True(store.TryDispatch("controller.a", "hero.one", arrivalTicks: 100, out int first));
        Assert.True(store.TryDispatch("controller.a", "hero.two", arrivalTicks: 100, out int second));

        Assert.True(store.TryMarkAccident(first, accidentIndex: 4));
        Assert.False(store.TryGetArrived("controller.a", first, "hero.one", nowTicks: 200, out _));
        Assert.Single(store.AccidentsFor("controller.a"));
        Assert.False(store.TryRemoveArrived(
            "controller.a", first, "hero.one", nowTicks: 200));
        Assert.True(store.TryRemoveAccident("controller.a", first, "hero.one"));
        Assert.Equal(second, Assert.Single(store.Export()).MessengerId);
    }

    private static NetworkRequestDiplomacyOperation Request(
        DiplomacyOperation operation,
        string targetId = "",
        string secondaryTargetId = "",
        int intValue = 0) => new(
        new AuthorityRequestHeader(protocolVersion: 7, sessionId: SessionId, requestId: 73, expectedRevision: 11),
        operation, targetId, secondaryTargetId, intValue);
}
