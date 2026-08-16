using Common.Messaging;
using ProtoBuf;
using System;
using System.Linq;

namespace GameInterface.Services.Separatism;

[ProtoContract(SkipConstructor = true)]
[AuthorityRoute(SeparatismRecruitmentProtocol.RouteId, AuthorityRouteKind.Command)]
internal readonly struct NetworkRequestSeparatismRecruitment : ICommand
{
    [ProtoMember(1)] public readonly int ProtocolVersion;
    [ProtoMember(2)] public readonly string SessionId;
    [ProtoMember(3)] public readonly long AuthorityRequestId;
    [ProtoMember(4)] public readonly long ExpectedConfigRevision;
    [ProtoMember(5)] public readonly long ExpectedFactionChangeTicks;
    [ProtoMember(6)] public readonly string ExpectedKingdomId;
    [ProtoMember(7)] public readonly string TargetClanId;
    [ProtoMember(8)] public readonly string TargetHeroId;
    [ProtoMember(9)] public readonly string CommandDigest;

    public NetworkRequestSeparatismRecruitment(AuthorityRequestHeader header, long expectedFactionChangeTicks,
        string expectedKingdomId, string targetClanId, string targetHeroId)
    {
        ProtocolVersion = header.ProtocolVersion;
        SessionId = header.SessionId;
        AuthorityRequestId = header.RequestId;
        ExpectedConfigRevision = header.ExpectedRevision;
        ExpectedFactionChangeTicks = expectedFactionChangeTicks;
        ExpectedKingdomId = expectedKingdomId;
        TargetClanId = targetClanId;
        TargetHeroId = targetHeroId;
        CommandDigest = SeparatismRecruitmentProtocol.CommandDigest(
            header, expectedFactionChangeTicks, expectedKingdomId, targetClanId, targetHeroId);
    }

    public AuthorityRequestHeader Header => new(
        ProtocolVersion, SessionId, AuthorityRequestId, ExpectedConfigRevision);
}

[ProtoContract(SkipConstructor = true)]
internal readonly struct NetworkSeparatismRecruitmentResult : IMessage
{
    [ProtoMember(1)] public readonly AuthorityResultHeader Header;
    [ProtoMember(2)] public readonly string CommandDigest;
    [ProtoMember(3)] public readonly string ExpectedKingdomId;
    [ProtoMember(4)] public readonly string CommittedKingdomId;
    [ProtoMember(5)] public readonly string TargetClanId;
    [ProtoMember(6)] public readonly string TargetHeroId;
    [ProtoMember(7)] public readonly long ExpectedFactionChangeTicks;
    [ProtoMember(8)] public readonly long CommittedFactionChangeTicks;

    public NetworkSeparatismRecruitmentResult(AuthorityResultHeader header, string commandDigest,
        string expectedKingdomId, string committedKingdomId, string targetClanId, string targetHeroId,
        long expectedFactionChangeTicks, long committedFactionChangeTicks)
    {
        Header = header;
        CommandDigest = commandDigest;
        ExpectedKingdomId = expectedKingdomId;
        CommittedKingdomId = committedKingdomId;
        TargetClanId = targetClanId;
        TargetHeroId = targetHeroId;
        ExpectedFactionChangeTicks = expectedFactionChangeTicks;
        CommittedFactionChangeTicks = committedFactionChangeTicks;
    }
}

internal static class SeparatismRecruitmentProtocol
{
    internal const string RouteId = "workshop.separatism.recruit-fallen-clan";
    internal const int MaximumObjectIdLength = 256;
    internal const int MaximumDigestLength = 2048;

    internal static string ValidateRequest(NetworkRequestSeparatismRecruitment request)
    {
        if (request.ExpectedFactionChangeTicks < 0 || !IsIdentifier(request.ExpectedKingdomId) ||
            !IsIdentifier(request.TargetClanId) || !IsIdentifier(request.TargetHeroId))
            return "invalid-recruitment-fields";

        return string.Equals(request.CommandDigest, CommandDigest(
                request.Header, request.ExpectedFactionChangeTicks, request.ExpectedKingdomId,
                request.TargetClanId, request.TargetHeroId), StringComparison.Ordinal)
            ? null : "invalid-command-digest";
    }

    internal static bool IsResultShapeValid(NetworkSeparatismRecruitmentResult result) =>
        result.Header.TryValidate(out _) && IsDigest(result.CommandDigest) &&
        IsOptionalIdentifier(result.ExpectedKingdomId) && IsOptionalIdentifier(result.CommittedKingdomId) &&
        IsOptionalIdentifier(result.TargetClanId) && IsOptionalIdentifier(result.TargetHeroId) &&
        result.ExpectedFactionChangeTicks >= 0 && result.CommittedFactionChangeTicks >= 0;

    internal static string CommandDigest(AuthorityRequestHeader header, long expectedFactionChangeTicks,
        string expectedKingdomId, string targetClanId, string targetHeroId) => string.Concat(
            "v1|", header.ProtocolVersion, "|", Part(header.SessionId), "|", header.ExpectedRevision, "|",
            expectedFactionChangeTicks, "|", Part(expectedKingdomId), "|", Part(targetClanId), "|", Part(targetHeroId));

    internal static string StructuralKey(NetworkRequestSeparatismRecruitment request) => string.Concat(
        Part(request.CommandDigest), "|", request.ProtocolVersion, "|", Part(request.SessionId), "|",
        request.ExpectedConfigRevision, "|", request.ExpectedFactionChangeTicks, "|",
        Part(request.ExpectedKingdomId), "|", Part(request.TargetClanId), "|", Part(request.TargetHeroId));

    private static string Part(string value) => string.Concat(value?.Length ?? -1, ":", value ?? string.Empty);
    private static bool IsDigest(string value) => !string.IsNullOrWhiteSpace(value) &&
        value.Length <= MaximumDigestLength && !value.Any(char.IsControl);
    private static bool IsOptionalIdentifier(string value) => value == null || IsIdentifier(value);
    private static bool IsIdentifier(string value) => !string.IsNullOrWhiteSpace(value) &&
        value.Length <= MaximumObjectIdLength && !value.Any(char.IsControl);
}

internal static class SeparatismConversationPolicy
{
    internal static bool CanOfferFallenClanRecruitment(bool capabilityEnabled, bool actorHasKingdom,
        bool actorIsKingdomRuler, bool targetExists, bool targetHasNoKingdom, bool targetIsNotMinorFaction,
        bool targetIsFactionLeader, bool factionsAreAtPeace) => capabilityEnabled && actorHasKingdom &&
        actorIsKingdomRuler && targetExists && targetHasNoKingdom && targetIsNotMinorFaction &&
        targetIsFactionLeader && factionsAreAtPeace;
}

internal static class SeparatismCapabilityPolicy
{
    internal static bool AllowRecruitment(bool optionEnabled, bool routeReady) => optionEnabled && routeReady;
}
