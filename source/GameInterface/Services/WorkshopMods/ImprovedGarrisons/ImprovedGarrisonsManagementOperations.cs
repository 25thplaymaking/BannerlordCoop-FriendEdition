using Common.Messaging;
using GameInterface.Configuration;
using GameInterface.Services.AuthorityRequests;
using GameInterface.Services.WorkshopMods.Core;
using ProtoBuf;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace GameInterface.Services.WorkshopMods.ImprovedGarrisons;

internal enum ImprovedGarrisonsOperation
{
    CopySettings = 1,
    CreateTransferParty = 2,
    CreateRecruiter = 3,
    ChangeRecruitmentCulture = 4,
    ReturnRecruiter = 5,
    CreateMobileGarrison = 6,
    SetMobileGarrisonEscort = 7,
    ReplaceTownTemplate = 8,
    RemoveTownTemplateTroop = 9,
    ApplyGlobalTemplate = 10,
    CreateGlobalTemplate = 11,
    RenameGlobalTemplate = 12,
    RemoveGlobalTemplate = 13,
    SetUpgradePaths = 14,
    OrderMobileGarrisonPatrol = 15,
    OrderMobileGarrisonReturn = 16,
    FortifyMobileGarrison = 17,
    StartHostileEncounter = 18,
    BoostBuildingReserve = 19,
}

[ProtoContract(SkipConstructor = true)]
internal sealed class ImprovedGarrisonsTroopSelection
{
    [ProtoMember(1)] public string TroopId { get; private set; }
    [ProtoMember(2)] public int Count { get; private set; }

    public ImprovedGarrisonsTroopSelection()
    {
    }

    public ImprovedGarrisonsTroopSelection(string troopId, int count)
    {
        TroopId = troopId;
        Count = count;
    }
}

[AuthorityRoute("workshop.improved-garrisons.management", AuthorityRouteKind.Command)]
[ProtoContract(SkipConstructor = true)]
internal sealed class NetworkRequestImprovedGarrisonsOperation : ICommand
{
    [ProtoMember(1)] public string SessionId { get; private set; }
    [ProtoMember(2)] public long RequestId { get; private set; }
    [ProtoMember(3)] public long ExpectedRevision { get; private set; }
    [ProtoMember(4)] public ImprovedGarrisonsOperation Operation { get; private set; }
    [ProtoMember(5)] public string TownId { get; private set; }
    [ProtoMember(6)] private string[] targetIds;
    [ProtoMember(7)] public string Value { get; private set; }
    [ProtoMember(8)] private ImprovedGarrisonsTroopSelection[] troops;
    [ProtoMember(9)] public int ConfigProtocolVersion { get; private set; }

    public string[] TargetIds => targetIds ?? Array.Empty<string>();
    public ImprovedGarrisonsTroopSelection[] Troops => troops ?? Array.Empty<ImprovedGarrisonsTroopSelection>();

    public NetworkRequestImprovedGarrisonsOperation()
    {
    }

    public NetworkRequestImprovedGarrisonsOperation(
        string sessionId,
        long requestId,
        long expectedRevision,
        ImprovedGarrisonsOperation operation,
        string townId,
        string[] targetIds,
        string value,
        ImprovedGarrisonsTroopSelection[] troops,
        int configProtocolVersion = 0)
    {
        SessionId = sessionId;
        RequestId = requestId;
        ExpectedRevision = expectedRevision;
        Operation = operation;
        TownId = townId;
        this.targetIds = targetIds ?? Array.Empty<string>();
        Value = value ?? string.Empty;
        this.troops = troops ?? Array.Empty<ImprovedGarrisonsTroopSelection>();
        ConfigProtocolVersion = configProtocolVersion;
    }

    public NetworkRequestImprovedGarrisonsOperation(
        AuthorityRequestHeader header, ImprovedGarrisonsOperation operation, string townId, string[] targetIds,
        string value, ImprovedGarrisonsTroopSelection[] troops)
        : this(header.SessionId, header.RequestId, header.ExpectedRevision, operation, townId, targetIds, value,
            troops, header.ProtocolVersion)
    {
    }

    public AuthorityRequestHeader Header =>
        new AuthorityRequestHeader(ConfigProtocolVersion, SessionId, RequestId, ExpectedRevision);
}

internal enum ImprovedGarrisonsOperationStatus
{
    Accepted = 1,
    Rejected = 2,
    StaleState = 3,
    StaleSession = 4,
    Failed = 5,
}

[ProtoContract(SkipConstructor = true)]
internal sealed class NetworkImprovedGarrisonsOperationResult : ICommand
{
    [ProtoMember(1)] public string SessionId { get; private set; }
    [ProtoMember(2)] public long RequestId { get; private set; }
    [ProtoMember(3)] public ImprovedGarrisonsOperation Operation { get; private set; }
    [ProtoMember(4)] public ImprovedGarrisonsOperationStatus Status { get; private set; }
    [ProtoMember(5)] public string TownId { get; private set; }
    [ProtoMember(6)] public string PartyId { get; private set; }
    [ProtoMember(7)] public AuthorityResultStatus AuthorityStatus { get; private set; }
    [ProtoMember(8)] public string ReasonCode { get; private set; }
    [ProtoMember(9)] public string CommandDigest { get; private set; }
    [ProtoMember(10)] public long CommittedRevision { get; private set; }
    [ProtoMember(11)] public string CanonicalHash { get; private set; }
    [ProtoMember(12)] public string PostState { get; private set; }

    public NetworkImprovedGarrisonsOperationResult()
    {
    }

    public NetworkImprovedGarrisonsOperationResult(
        string sessionId,
        long requestId,
        ImprovedGarrisonsOperation operation,
        ImprovedGarrisonsOperationStatus status,
        string townId,
        string partyId)
    {
        SessionId = sessionId;
        RequestId = requestId;
        Operation = operation;
        Status = status;
        TownId = townId;
        PartyId = partyId ?? string.Empty;
        AuthorityStatus = status == ImprovedGarrisonsOperationStatus.Accepted
            ? AuthorityResultStatus.Accepted : AuthorityResultStatus.Rejected;
        ReasonCode = status.ToString();
        CommandDigest = string.Empty;
        CommittedRevision = 0;
        CanonicalHash = string.Empty;
        PostState = string.Empty;
    }

    public NetworkImprovedGarrisonsOperationResult(
        AuthorityRequestHeader header, ImprovedGarrisonsOperation operation,
        ImprovedGarrisonsOperationStatus status, AuthorityResultStatus authorityStatus, string reasonCode,
        string commandDigest, string townId, string partyId, long committedRevision, string canonicalHash,
        string postState)
    {
        SessionId = header.SessionId;
        RequestId = header.RequestId;
        Operation = operation;
        Status = status;
        TownId = townId ?? string.Empty;
        PartyId = partyId ?? string.Empty;
        AuthorityStatus = authorityStatus;
        ReasonCode = reasonCode ?? string.Empty;
        CommandDigest = commandDigest ?? string.Empty;
        CommittedRevision = committedRevision;
        CanonicalHash = canonicalHash ?? string.Empty;
        PostState = postState ?? string.Empty;
    }

    public AuthorityResultHeader Header =>
        new AuthorityResultHeader(SessionId, RequestId, AuthorityStatus, CommittedRevision, ReasonCode);
}

internal static class ImprovedGarrisonsOperationProtocol
{
    public const int MaximumTargets = 64;
    public const int MaximumTroops = 512;
    public const int MaximumTroopCount = 10_000;

    public static bool IsRequestShapeValid(NetworkRequestImprovedGarrisonsOperation request)
    {
        if (request == null || request.SessionId == null || request.SessionId.Length != ModConfigSnapshot.SessionIdLength ||
            !Guid.TryParseExact(request.SessionId, "N", out _) || request.RequestId <= 0 ||
            request.ExpectedRevision < 0 || !Enum.IsDefined(typeof(ImprovedGarrisonsOperation), request.Operation) ||
            !IsText(request.TownId, 1, ImprovedGarrisonsCompatibilityHandler.MaxObjectIdLength) ||
            !IsText(request.Value, 0, ImprovedGarrisonsCompatibilityHandler.MaxRequestValueLength))
            return false;

        string[] targets = request.TargetIds ?? Array.Empty<string>();
        if (targets.Length > MaximumTargets || targets.Any(target =>
                !IsText(target, 1, ImprovedGarrisonsCompatibilityHandler.MaxObjectIdLength)) ||
            targets.Distinct(StringComparer.Ordinal).Count() != targets.Length)
            return false;

        ImprovedGarrisonsTroopSelection[] troops = request.Troops ?? Array.Empty<ImprovedGarrisonsTroopSelection>();
        if (troops.Length > MaximumTroops || troops.Any(troop => troop == null ||
                !IsText(troop.TroopId, 1, ImprovedGarrisonsCompatibilityHandler.MaxObjectIdLength) ||
                troop.Count < 0 || troop.Count > MaximumTroopCount) ||
            troops.Select(troop => troop.TroopId).Distinct(StringComparer.Ordinal).Count() != troops.Length)
            return false;

        return HasValidPayload(request, targets, troops);
    }

    public static bool IsResultShapeValid(NetworkImprovedGarrisonsOperationResult result) =>
        result != null && result.SessionId != null &&
        result.SessionId.Length == ModConfigSnapshot.SessionIdLength &&
        Guid.TryParseExact(result.SessionId, "N", out _) && result.RequestId > 0 &&
        Enum.IsDefined(typeof(ImprovedGarrisonsOperation), result.Operation) &&
        Enum.IsDefined(typeof(ImprovedGarrisonsOperationStatus), result.Status) &&
        IsText(result.TownId, 1, ImprovedGarrisonsCompatibilityHandler.MaxObjectIdLength) &&
        IsText(result.PartyId, 0, ImprovedGarrisonsCompatibilityHandler.MaxObjectIdLength);

    public static string CommandKey(NetworkRequestImprovedGarrisonsOperation request)
    {
        if (request == null) return "operation:null";
        var fields = new List<string>
        {
            request.SessionId,
            request.ExpectedRevision.ToString(CultureInfo.InvariantCulture),
            ((int)request.Operation).ToString(CultureInfo.InvariantCulture),
            request.TownId,
            request.Value,
            request.TargetIds.Length.ToString(CultureInfo.InvariantCulture),
        };
        fields.AddRange(request.TargetIds);
        fields.Add(request.Troops.Length.ToString(CultureInfo.InvariantCulture));
        foreach (ImprovedGarrisonsTroopSelection troop in request.Troops)
        {
            fields.Add(troop?.TroopId);
            fields.Add((troop?.Count ?? -1).ToString(CultureInfo.InvariantCulture));
        }
        return ImprovedGarrisonsCompatibilityHandler.BuildCommandKey("operation", fields.ToArray());
    }

    private static bool HasValidPayload(
        NetworkRequestImprovedGarrisonsOperation request,
        string[] targets,
        ImprovedGarrisonsTroopSelection[] troops)
    {
        switch (request.Operation)
        {
            case ImprovedGarrisonsOperation.CopySettings:
                return targets.Length > 0 && troops.Length == 0 &&
                       !targets.Contains(request.TownId, StringComparer.Ordinal);
            case ImprovedGarrisonsOperation.CreateTransferParty:
            case ImprovedGarrisonsOperation.SetMobileGarrisonEscort:
            case ImprovedGarrisonsOperation.FortifyMobileGarrison:
                return targets.Length == 1 && troops.Length == 0 && request.Value.Length == 0;
            case ImprovedGarrisonsOperation.CreateRecruiter:
                return targets.Length <= 1 && troops.Length == 0 &&
                       int.TryParse(request.Value, NumberStyles.None, CultureInfo.InvariantCulture, out int amount) &&
                       amount >= 1 && amount <= 150;
            case ImprovedGarrisonsOperation.ChangeRecruitmentCulture:
                return targets.Length <= 1 && troops.Length == 0;
            case ImprovedGarrisonsOperation.ReturnRecruiter:
            case ImprovedGarrisonsOperation.CreateMobileGarrison:
            case ImprovedGarrisonsOperation.OrderMobileGarrisonPatrol:
            case ImprovedGarrisonsOperation.OrderMobileGarrisonReturn:
                return targets.Length == 0 && troops.Length == 0 && request.Value.Length == 0;
            case ImprovedGarrisonsOperation.StartHostileEncounter:
                return targets.Length == 1 && troops.Length == 0 && request.Value.Length == 0;
            case ImprovedGarrisonsOperation.BoostBuildingReserve:
                return targets.Length == 0 && troops.Length == 0 &&
                       int.TryParse(request.Value, NumberStyles.None, CultureInfo.InvariantCulture, out int reserve) &&
                       reserve > 0;
            case ImprovedGarrisonsOperation.ReplaceTownTemplate:
                return targets.Length == 0 && request.Value.Length > 0;
            case ImprovedGarrisonsOperation.SetUpgradePaths:
                return targets.Length <= 3 && troops.Length == 0 && request.Value.Length == 0 &&
                       targets.All(target => target == "0" || target == "1" || target == "2");
            case ImprovedGarrisonsOperation.RemoveTownTemplateTroop:
                return targets.Length == 0 && request.Value.Length > 0 && troops.Length == 0;
            case ImprovedGarrisonsOperation.ApplyGlobalTemplate:
            case ImprovedGarrisonsOperation.RemoveGlobalTemplate:
                return targets.Length == 0 && troops.Length == 0 && request.Value.Length > 0;
            case ImprovedGarrisonsOperation.CreateGlobalTemplate:
                return targets.Length == 0 && request.Value.Length > 0;
            case ImprovedGarrisonsOperation.RenameGlobalTemplate:
                return targets.Length == 1 && troops.Length == 0 && request.Value.Length > 0;
            default:
                return false;
        }
    }

    private static bool IsText(string value, int minimum, int maximum) =>
        value != null && value.Length >= minimum && value.Length <= maximum &&
        value.All(character => !char.IsControl(character));
}

internal static class ImprovedGarrisonsBuildingAuthority
{
    public static bool TryPlan(
        int currentReserve,
        int actorGold,
        int requestedIncrease,
        out int newReserve,
        out int newGold)
    {
        newReserve = 0;
        newGold = 0;
        if (currentReserve < 0 || actorGold < requestedIncrease || requestedIncrease <= 0 ||
            currentReserve > int.MaxValue - requestedIncrease)
            return false;

        newReserve = currentReserve + requestedIncrease;
        newGold = actorGold - requestedIncrease;
        return true;
    }
}

internal static class ImprovedGarrisonsCanonicalOperations
{
    private const string TownScope = "town";
    private const string GlobalTemplateScope = "global-template";
    private const string TemplateName = "Template.Name";
    private const string TemplateTroopPrefix = "Template.Troop:";

    public static bool TryTransform(
        IReadOnlyCollection<ImprovedGarrisonsStateValue> current,
        NetworkRequestImprovedGarrisonsOperation request,
        IEnumerable<string> allowedOwnedTownIds,
        out ImprovedGarrisonsStateValue[] transformed,
        out string failure)
    {
        transformed = Array.Empty<ImprovedGarrisonsStateValue>();
        failure = null;
        if (current == null || current.Any(value => value == null) ||
            !ImprovedGarrisonsOperationProtocol.IsRequestShapeValid(request))
        {
            failure = "operation or canonical state is malformed";
            return false;
        }

        var owned = new HashSet<string>(allowedOwnedTownIds ?? Array.Empty<string>(), StringComparer.Ordinal);
        if (!owned.Contains(request.TownId))
        {
            failure = "source town is not owned by the requesting clan";
            return false;
        }

        var candidate = current.Select(Clone).ToList();
        switch (request.Operation)
        {
            case ImprovedGarrisonsOperation.CopySettings:
                if (request.TargetIds.Any(target => !owned.Contains(target)))
                {
                    failure = "a target town is not owned by the requesting clan";
                    return false;
                }
                ImprovedGarrisonsStateValue[] source = candidate
                    .Where(value => value.Scope == TownScope && value.TargetId == request.TownId)
                    .ToArray();
                if (source.Length == 0)
                {
                    failure = "source town has no canonical settings";
                    return false;
                }
                foreach (string target in request.TargetIds)
                {
                    candidate.RemoveAll(value => value.Scope == TownScope && value.TargetId == target);
                    candidate.AddRange(source.Select(value =>
                        new ImprovedGarrisonsStateValue(value.Scope, target, value.Property, value.Value)));
                }
                break;
            case ImprovedGarrisonsOperation.ReplaceTownTemplate:
                ReplaceTownTemplate(candidate, request.TownId, request.Value, request.Troops);
                break;
            case ImprovedGarrisonsOperation.SetUpgradePaths:
                ReplaceValue(
                    candidate,
                    TownScope,
                    request.TownId,
                    "TroopsToUpgradeTo",
                    string.Join(",", Enumerable.Range(0, 3)
                        .Select(index => request.TargetIds.Contains(
                            index.ToString(CultureInfo.InvariantCulture), StringComparer.Ordinal) ? "1" : "0")));
                break;
            case ImprovedGarrisonsOperation.RemoveTownTemplateTroop:
                candidate.RemoveAll(value => value.Scope == TownScope && value.TargetId == request.TownId &&
                    value.Property == TemplateTroopPrefix + EncodeId(request.Value));
                break;
            case ImprovedGarrisonsOperation.ChangeRecruitmentCulture:
                ReplaceValue(candidate, TownScope, request.TownId, "RecruiterCultureToRecruit",
                    request.TargetIds.FirstOrDefault() ?? string.Empty);
                break;
            case ImprovedGarrisonsOperation.CreateRecruiter:
                ReplaceValue(candidate, TownScope, request.TownId, "RecruiterCultureToRecruit",
                    request.TargetIds.FirstOrDefault() ?? string.Empty);
                ReplaceValue(candidate, TownScope, request.TownId, "RecruiterRecruitAmount", request.Value);
                ReplaceValue(candidate, TownScope, request.TownId, "RecruiterAutoSpawn", "False");
                break;
            case ImprovedGarrisonsOperation.ReturnRecruiter:
                ReplaceValue(candidate, TownScope, request.TownId, "RecruiterAutoSpawn", "False");
                break;
            case ImprovedGarrisonsOperation.ApplyGlobalTemplate:
                ImprovedGarrisonsStateValue[] template = candidate
                    .Where(value => value.Scope == GlobalTemplateScope && value.TargetId == request.Value)
                    .ToArray();
                if (template.Length == 0)
                {
                    failure = "selected global template does not exist";
                    return false;
                }
                candidate.RemoveAll(value => value.Scope == TownScope && value.TargetId == request.TownId &&
                    (value.Property == TemplateName || value.Property.StartsWith(TemplateTroopPrefix, StringComparison.Ordinal)));
                candidate.Add(new ImprovedGarrisonsStateValue(TownScope, request.TownId, TemplateName, request.Value));
                candidate.AddRange(template
                    .Where(value => value.Property.StartsWith(TemplateTroopPrefix, StringComparison.Ordinal))
                    .Select(value => new ImprovedGarrisonsStateValue(TownScope, request.TownId, value.Property, value.Value)));
                break;
            case ImprovedGarrisonsOperation.CreateGlobalTemplate:
                if (candidate.Any(value => value.Scope == GlobalTemplateScope && value.TargetId == request.Value))
                {
                    failure = "global template already exists";
                    return false;
                }
                candidate.Add(new ImprovedGarrisonsStateValue(GlobalTemplateScope, request.Value, "Name", request.Value));
                if (request.Troops.Length > 0)
                {
                    foreach (ImprovedGarrisonsTroopSelection troop in request.Troops)
                        candidate.Add(new ImprovedGarrisonsStateValue(
                            GlobalTemplateScope, request.Value, TemplateTroopPrefix + EncodeId(troop.TroopId),
                            troop.Count.ToString(CultureInfo.InvariantCulture)));
                }
                else
                {
                    candidate.AddRange(candidate
                        .Where(value => value.Scope == TownScope && value.TargetId == request.TownId &&
                                        value.Property.StartsWith(TemplateTroopPrefix, StringComparison.Ordinal))
                        .Select(value => new ImprovedGarrisonsStateValue(
                            GlobalTemplateScope, request.Value, value.Property, value.Value))
                        .ToArray());
                }
                break;
            case ImprovedGarrisonsOperation.RenameGlobalTemplate:
                string originalName = request.TargetIds[0];
                if (!candidate.Any(value => value.Scope == GlobalTemplateScope && value.TargetId == originalName) ||
                    candidate.Any(value => value.Scope == GlobalTemplateScope && value.TargetId == request.Value))
                {
                    failure = "global template rename is missing its source or collides with an existing template";
                    return false;
                }
                foreach (ImprovedGarrisonsStateValue value in candidate
                             .Where(value => value.Scope == GlobalTemplateScope && value.TargetId == originalName))
                    value.TargetId = request.Value;
                ReplaceValue(candidate, GlobalTemplateScope, request.Value, "Name", request.Value);
                break;
            case ImprovedGarrisonsOperation.RemoveGlobalTemplate:
                if (candidate.RemoveAll(value => value.Scope == GlobalTemplateScope && value.TargetId == request.Value) == 0)
                {
                    failure = "global template does not exist";
                    return false;
                }
                break;
            default:
                failure = "operation does not mutate canonical settings";
                return false;
        }

        transformed = candidate
            .OrderBy(value => value.Scope, StringComparer.Ordinal)
            .ThenBy(value => value.TargetId, StringComparer.Ordinal)
            .ThenBy(value => value.Property, StringComparer.Ordinal)
            .ToArray();
        return true;
    }

    private static void ReplaceTownTemplate(
        List<ImprovedGarrisonsStateValue> values,
        string townId,
        string name,
        IEnumerable<ImprovedGarrisonsTroopSelection> troops)
    {
        values.RemoveAll(value => value.Scope == TownScope && value.TargetId == townId &&
            (value.Property == TemplateName || value.Property.StartsWith(TemplateTroopPrefix, StringComparison.Ordinal)));
        values.Add(new ImprovedGarrisonsStateValue(TownScope, townId, TemplateName, name));
        foreach (ImprovedGarrisonsTroopSelection troop in troops)
            values.Add(new ImprovedGarrisonsStateValue(
                TownScope, townId, TemplateTroopPrefix + EncodeId(troop.TroopId),
                troop.Count.ToString(CultureInfo.InvariantCulture)));
    }

    private static void ReplaceValue(
        List<ImprovedGarrisonsStateValue> values,
        string scope,
        string targetId,
        string property,
        string replacement)
    {
        values.RemoveAll(value => value.Scope == scope && value.TargetId == targetId && value.Property == property);
        values.Add(new ImprovedGarrisonsStateValue(scope, targetId, property, replacement ?? string.Empty));
    }

    private static string EncodeId(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));

    private static ImprovedGarrisonsStateValue Clone(ImprovedGarrisonsStateValue value) =>
        new(value.Scope, value.TargetId, value.Property, value.Value);
}

internal static class ImprovedGarrisonsCapabilityPolicy
{
    public static bool IsEnabled(
        bool optionEnabled,
        bool pinnedModuleReady,
        bool settingRouteReady,
        bool managementRouteReady,
        bool snapshotRouteReady,
        bool snapshotCurrent) =>
        optionEnabled && pinnedModuleReady && settingRouteReady && managementRouteReady &&
        snapshotRouteReady && snapshotCurrent;
}

internal sealed class ImprovedGarrisonsCapabilitySource : IWorkshopCapabilitySource
{
    internal const string ModuleId = "ImprovedGarrisons";
    internal const string Operation = "Management";

    private readonly IModConfig modConfig;
    private readonly IModConfigAuthority configAuthority;
    private readonly IAuthorityRequestRouter authorityRequestRouter;
    private readonly ImprovedGarrisonsCompatibilityHandler compatibilityHandler;

    public ImprovedGarrisonsCapabilitySource(
        IModConfig modConfig,
        IModConfigAuthority configAuthority = null,
        IAuthorityRequestRouter authorityRequestRouter = null,
        ImprovedGarrisonsCompatibilityHandler compatibilityHandler = null)
    {
        this.modConfig = modConfig;
        this.configAuthority = configAuthority;
        this.authorityRequestRouter = authorityRequestRouter;
        this.compatibilityHandler = compatibilityHandler;
    }

    public IEnumerable<WorkshopCapability> CaptureCapabilities()
    {
        ModOptions options = modConfig.Data == null
            ? ModConfigProvider.ModOptions
            : new ModOptions(modConfig.Data.ModOptions ?? new ModOptionsData());
        bool optionEnabled = options.IsWorkshopModuleEnabled(ModuleId);
        bool settingRouteReady = authorityRequestRouter?.IsRegistered(
            "workshop.improved-garrisons.setting", AuthorityRouteKind.Command) == true;
        bool managementRouteReady = authorityRequestRouter?.IsRegistered(
            "workshop.improved-garrisons.management", AuthorityRouteKind.Command) == true;
        bool snapshotRouteReady = authorityRequestRouter?.IsRegistered(
            "workshop.improved-garrisons.snapshot", AuthorityRouteKind.BootstrapQuery) == true;
        bool snapshotCurrent = configAuthority != null && configAuthority.TryGetCurrent(out var config) &&
            compatibilityHandler?.IsSnapshotReadyFor(config.SessionId) == true;
        bool enabled = ImprovedGarrisonsCapabilityPolicy.IsEnabled(
            optionEnabled,
            compatibilityHandler?.IsCompatible == true,
            settingRouteReady,
            managementRouteReady,
            snapshotRouteReady,
            snapshotCurrent);
        yield return new WorkshopCapability(
            ModuleId,
            Operation,
            enabled,
            enabled ? string.Empty : optionEnabled
                ? "authority-command-route-unavailable"
                : "module-disabled");
    }
}
