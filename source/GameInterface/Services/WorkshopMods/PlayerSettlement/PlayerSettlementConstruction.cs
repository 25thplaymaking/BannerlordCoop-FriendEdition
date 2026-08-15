using Common.Messaging;
using ProtoBuf;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Library;
using TaleWorlds.ObjectSystem;

namespace GameInterface.Services.WorkshopMods.PlayerSettlement;

internal enum PlayerSettlementConstructionOperation
{
    BuildTown = 1,
    BuildCastle = 2,
    BuildVillage = 3,
    Rebuild = 4,
    Overwrite = 5,
}

internal enum PlayerSettlementConstructionStatus
{
    Accepted = 1,
    Rejected = 2,
    StaleState = 3,
    Failed = 4,
}

[ProtoContract(SkipConstructor = true)]
internal sealed class PlayerSettlementBitTransform
{
    internal const int FrameBitCount = 13;
    internal const int DeepEditBitCount = 17;

    [ProtoMember(1)] public int[] Bits { get; private set; }

    private PlayerSettlementBitTransform()
    {
    }

    internal PlayerSettlementBitTransform(IEnumerable<int> bits) =>
        Bits = bits?.ToArray() ?? Array.Empty<int>();
}

[ProtoContract(SkipConstructor = true)]
internal sealed class PlayerSettlementDeepEditIntent
{
    [ProtoMember(1)] public int Index { get; private set; }
    [ProtoMember(2)] public string Name { get; private set; }
    [ProtoMember(3)] public bool IsDeleted { get; private set; }
    [ProtoMember(4)] public PlayerSettlementBitTransform Transform { get; private set; }

    private PlayerSettlementDeepEditIntent()
    {
    }

    internal PlayerSettlementDeepEditIntent(
        int index,
        string name,
        bool isDeleted,
        PlayerSettlementBitTransform transform)
    {
        Index = index;
        Name = name ?? string.Empty;
        IsDeleted = isDeleted;
        Transform = transform;
    }
}

[ProtoContract(SkipConstructor = true)]
internal sealed class NetworkRequestPlayerSettlementConstruction : ICommand
{
    [ProtoMember(1)] public string AdapterVersion { get; private set; }
    [ProtoMember(2)] public long RequestId { get; private set; }
    [ProtoMember(3)] public long ExpectedRevision { get; private set; }
    [ProtoMember(4)] public PlayerSettlementConstructionOperation Operation { get; private set; }
    [ProtoMember(5)] public string TargetId { get; private set; }
    [ProtoMember(6)] public string BoundId { get; private set; }
    [ProtoMember(7)] public string BoundTargetId { get; private set; }
    [ProtoMember(8)] public string SettlementName { get; private set; }
    [ProtoMember(9)] public string CultureId { get; private set; }
    [ProtoMember(10)] public string TemplateId { get; private set; }
    [ProtoMember(11)] public string VillageType { get; private set; }
    [ProtoMember(12)] public int VillageNumber { get; private set; }
    [ProtoMember(13)] public PlayerSettlementBitTransform SettlementFrame { get; private set; }
    [ProtoMember(14)] public PlayerSettlementBitTransform GateFrame { get; private set; }
    [ProtoMember(15)] private PlayerSettlementDeepEditIntent[] deepEdits;

    internal PlayerSettlementDeepEditIntent[] DeepEdits => deepEdits ?? Array.Empty<PlayerSettlementDeepEditIntent>();

    private NetworkRequestPlayerSettlementConstruction()
    {
    }

    internal NetworkRequestPlayerSettlementConstruction(
        long requestId,
        long expectedRevision,
        PlayerSettlementConstructionOperation operation,
        string targetId,
        string boundId,
        string boundTargetId,
        string settlementName,
        string cultureId,
        string templateId,
        string villageType,
        int villageNumber,
        PlayerSettlementBitTransform settlementFrame,
        PlayerSettlementBitTransform gateFrame,
        PlayerSettlementDeepEditIntent[] deepEdits)
    {
        AdapterVersion = PlayerSettlementCompatibilityManifest.AdapterVersion;
        RequestId = requestId;
        ExpectedRevision = expectedRevision;
        Operation = operation;
        TargetId = targetId ?? string.Empty;
        BoundId = boundId ?? string.Empty;
        BoundTargetId = boundTargetId ?? string.Empty;
        SettlementName = settlementName ?? string.Empty;
        CultureId = cultureId ?? string.Empty;
        TemplateId = templateId ?? string.Empty;
        VillageType = villageType ?? string.Empty;
        VillageNumber = villageNumber;
        SettlementFrame = settlementFrame;
        GateFrame = gateFrame;
        this.deepEdits = deepEdits ?? Array.Empty<PlayerSettlementDeepEditIntent>();
    }
}

[ProtoContract(SkipConstructor = true)]
internal sealed class NetworkPlayerSettlementConstructionResult : ICommand
{
    [ProtoMember(1)] public long RequestId { get; private set; }
    [ProtoMember(2)] public PlayerSettlementConstructionStatus Status { get; private set; }
    [ProtoMember(3)] public long Revision { get; private set; }
    [ProtoMember(4)] public string Message { get; private set; }

    private NetworkPlayerSettlementConstructionResult()
    {
    }

    internal NetworkPlayerSettlementConstructionResult(
        long requestId,
        PlayerSettlementConstructionStatus status,
        long revision,
        string message)
    {
        RequestId = requestId;
        Status = status;
        Revision = revision;
        Message = message ?? string.Empty;
    }
}

internal static class PlayerSettlementConstructionProtocol
{
    internal const int MaximumNameLength = 128;
    internal const int MaximumIdLength = 192;
    internal const int MaximumVillageTypeLength = 64;
    internal const int MaximumDeepEdits = 1024;
    internal const int MaximumDeepEditNameLength = 256;

    internal static bool TryValidate(
        NetworkRequestPlayerSettlementConstruction request,
        out string failure)
    {
        if (request == null ||
            !string.Equals(request.AdapterVersion, PlayerSettlementCompatibilityManifest.AdapterVersion,
                StringComparison.Ordinal) ||
            request.RequestId <= 0 || request.ExpectedRevision < 0 ||
            !Enum.IsDefined(typeof(PlayerSettlementConstructionOperation), request.Operation) ||
            !Bounded(request.SettlementName, MaximumNameLength, allowEmpty: false) ||
            !Bounded(request.CultureId, MaximumIdLength, allowEmpty: false) ||
            !Bounded(request.TemplateId, MaximumIdLength, allowEmpty: false) ||
            !Bounded(request.TargetId, MaximumIdLength, allowEmpty: true) ||
            !Bounded(request.BoundId, MaximumIdLength, allowEmpty: true) ||
            !Bounded(request.BoundTargetId, MaximumIdLength, allowEmpty: true) ||
            !Bounded(request.VillageType, MaximumVillageTypeLength, allowEmpty: true) ||
            request.VillageNumber < -1 || request.VillageNumber > 255 ||
            !ValidTransform(request.SettlementFrame, PlayerSettlementBitTransform.FrameBitCount, required: false) ||
            !ValidTransform(request.GateFrame, PlayerSettlementBitTransform.FrameBitCount, required: false) ||
            request.DeepEdits.Length > MaximumDeepEdits)
        {
            failure = "construction intent has an invalid or oversized common shape";
            return false;
        }

        foreach (var edit in request.DeepEdits)
        {
            if (edit == null || edit.Index < -1 ||
                !Bounded(edit.Name, MaximumDeepEditNameLength, allowEmpty: true) ||
                !ValidTransform(edit.Transform, PlayerSettlementBitTransform.DeepEditBitCount, required: !edit.IsDeleted))
            {
                failure = "construction intent contains an invalid deep transform edit";
                return false;
            }
        }

        var targetRequired = request.Operation == PlayerSettlementConstructionOperation.Rebuild ||
                             request.Operation == PlayerSettlementConstructionOperation.Overwrite;
        var village = request.Operation == PlayerSettlementConstructionOperation.BuildVillage;
        if (targetRequired != !string.IsNullOrEmpty(request.TargetId) ||
            village != !string.IsNullOrEmpty(request.BoundId) ||
            (!village && (!string.IsNullOrEmpty(request.BoundTargetId) ||
                          !string.IsNullOrEmpty(request.VillageType))) ||
            (village && string.IsNullOrEmpty(request.VillageType)))
        {
            failure = "construction intent does not match its operation-specific target shape";
            return false;
        }

        failure = null;
        return true;
    }

    internal static string CommandKey(NetworkRequestPlayerSettlementConstruction request)
    {
        var builder = new StringBuilder();
        Append(builder, request?.AdapterVersion);
        Append(builder, request?.ExpectedRevision.ToString(CultureInfo.InvariantCulture));
        Append(builder, ((int)(request?.Operation ?? 0)).ToString(CultureInfo.InvariantCulture));
        Append(builder, request?.TargetId);
        Append(builder, request?.BoundId);
        Append(builder, request?.BoundTargetId);
        Append(builder, request?.SettlementName);
        Append(builder, request?.CultureId);
        Append(builder, request?.TemplateId);
        Append(builder, request?.VillageType);
        Append(builder, request?.VillageNumber.ToString(CultureInfo.InvariantCulture));
        AppendBits(builder, request?.SettlementFrame?.Bits);
        AppendBits(builder, request?.GateFrame?.Bits);
        foreach (var edit in request?.DeepEdits ?? Array.Empty<PlayerSettlementDeepEditIntent>())
        {
            Append(builder, edit.Index.ToString(CultureInfo.InvariantCulture));
            Append(builder, edit.Name);
            Append(builder, edit.IsDeleted ? "1" : "0");
            AppendBits(builder, edit.Transform?.Bits);
        }
        return builder.ToString();
    }

    private static bool ValidTransform(PlayerSettlementBitTransform transform, int count, bool required)
    {
        if (transform == null) return !required;
        if (transform.Bits == null || transform.Bits.Length != count) return false;
        foreach (var bits in transform.Bits)
        {
            var value = BitConverter.ToSingle(BitConverter.GetBytes(bits), 0);
            if (float.IsNaN(value) || float.IsInfinity(value)) return false;
        }
        return true;
    }

    private static bool Bounded(string value, int maximum, bool allowEmpty) =>
        value != null && value.Length <= maximum && (allowEmpty || value.Length > 0);

    private static void Append(StringBuilder builder, string value)
    {
        value ??= string.Empty;
        builder.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value).Append(';');
    }

    private static void AppendBits(StringBuilder builder, IEnumerable<int> bits)
    {
        foreach (var value in bits ?? Array.Empty<int>())
            Append(builder, value.ToString(CultureInfo.InvariantCulture));
        Append(builder, "|");
    }
}

/// <summary>
/// Reflection bridge pinned to Player Settlement 7.5.0. It captures only the creator's final
/// ApplyPlaced boundary on the client and invokes that same boundary on the host after authority
/// checks, so creator object creation, economy, notable, garrison, and completion hooks stay intact.
/// </summary>
internal sealed class PlayerSettlementConstructionBridge
{
    private readonly Assembly assembly;
    private readonly Type behaviorType;
    private readonly Type deepEditType;
    private readonly Type transformType;
    private readonly Type mat3Type;
    private readonly Type vec3Type;
    private readonly Type playerSettlementInfoType;
    private readonly Type metadataType;
    private readonly IReadOnlyDictionary<PlayerSettlementConstructionOperation, MethodInfo> methods;

    internal PlayerSettlementConstructionBridge(
        Assembly assembly,
        Type behaviorType,
        IEnumerable<MethodInfo> constructionMethods)
    {
        this.assembly = assembly ?? throw new ArgumentNullException(nameof(assembly));
        this.behaviorType = behaviorType ?? throw new ArgumentNullException(nameof(behaviorType));
        deepEditType = RequiredType("BannerlordPlayerSettlement.Saves.DeepTransformEdit");
        transformType = RequiredType("BannerlordPlayerSettlement.Saves.TransformSaveable");
        mat3Type = RequiredType("BannerlordPlayerSettlement.Saves.Mat3Saveable");
        vec3Type = RequiredType("BannerlordPlayerSettlement.Saves.Vec3Saveable");
        playerSettlementInfoType = RequiredType("BannerlordPlayerSettlement.Saves.PlayerSettlementInfo");
        metadataType = RequiredType("BannerlordPlayerSettlement.Saves.MetaV3");

        methods = (constructionMethods ?? Array.Empty<MethodInfo>())
            .ToDictionary(MethodOperation, method => method);
        if (methods.Count != 5)
            throw new InvalidOperationException("Player Settlement construction bridge requires all five ApplyPlaced methods");
        ValidateFields();
    }

    internal bool TryCapture(
        object owner,
        MethodBase original,
        object[] arguments,
        long requestId,
        long expectedRevision,
        Func<object, string> objectId,
        out NetworkRequestPlayerSettlementConstruction request,
        out string failure)
    {
        request = null;
        if (owner == null || original == null || arguments == null || objectId == null)
        {
            failure = "construction commit context is incomplete";
            return false;
        }

        PlayerSettlementConstructionOperation operation;
        try { operation = MethodOperation((MethodInfo)original); }
        catch (Exception exception) { failure = exception.Message; return false; }

        var behavior = behaviorType.IsInstanceOfType(owner) ? owner : ReadField(owner, "<>4__this");
        if (behavior == null || !behaviorType.IsInstanceOfType(behavior))
        {
            failure = "construction commit does not reference the creator behavior";
            return false;
        }

        var culture = arguments.Length > 1 ? arguments[1] as CultureObject : null;
        var name = arguments.Length > 0 ? arguments[0] as string : null;
        var villageType = arguments.Length > 2 ? arguments[2] as string : string.Empty;
        var available = ReadField(behavior, "availableModels") as IList;
        var index = Convert.ToInt32(ReadField(behavior, "currentModelOptionIdx"));
        if (culture == null || string.IsNullOrWhiteSpace(name) || available == null ||
            index < 0 || index >= available.Count || available[index] == null)
        {
            failure = "construction selection is incomplete";
            return false;
        }

        var templateId = ReadStringMember(available[index], "Id");
        var target = operation == PlayerSettlementConstructionOperation.Overwrite
            ? ReadField(owner, "target") as Settlement
            : operation == PlayerSettlementConstructionOperation.Rebuild
                ? ReadSettlementFromItem(ReadField(owner, "target"))
                : null;
        var bound = operation == PlayerSettlementConstructionOperation.BuildVillage
            ? ReadField(owner, "bound") as Settlement
            : null;
        var boundTarget = operation == PlayerSettlementConstructionOperation.BuildVillage
            ? ReadSettlementFromItem(ReadField(owner, "boundTarget"))
            : null;
        var villageNumber = operation == PlayerSettlementConstructionOperation.BuildVillage
            ? Convert.ToInt32(ReadField(owner, "villageNumber"))
            : -1;

        request = new NetworkRequestPlayerSettlementConstruction(
            requestId,
            expectedRevision,
            operation,
            target == null ? string.Empty : objectId(target),
            bound == null ? string.Empty : objectId(bound),
            boundTarget == null ? string.Empty : objectId(boundTarget),
            name,
            ((MBObjectBase)culture).StringId,
            templateId,
            villageType,
            villageNumber,
            CaptureFrame(ReadField(behavior, "settlementPlacementFrame")),
            CaptureFrame(ReadField(behavior, "gatePlacementFrame")),
            CaptureDeepEdits(ReadField(behavior, "deepTransformEdits") as IEnumerable));
        return PlayerSettlementConstructionProtocol.TryValidate(request, out failure);
    }

    internal bool TryExecute(
        object behavior,
        Hero actor,
        MobileParty actorParty,
        NetworkRequestPlayerSettlementConstruction request,
        Func<string, Settlement> settlementById,
        Func<string, CultureObject> cultureById,
        out string failure)
    {
        if (behavior == null || !behaviorType.IsInstanceOfType(behavior) || actor == null ||
            actorParty == null || request == null)
        {
            failure = "authoritative construction context is incomplete";
            return false;
        }

        var culture = cultureById(request.CultureId);
        if (culture == null)
        {
            failure = "requested culture is not registered";
            return false;
        }

        var target = string.IsNullOrEmpty(request.TargetId) ? null : settlementById(request.TargetId);
        var bound = string.IsNullOrEmpty(request.BoundId) ? null : settlementById(request.BoundId);
        var boundTargetSettlement = string.IsNullOrEmpty(request.BoundTargetId)
            ? null
            : settlementById(request.BoundTargetId);
        if ((request.Operation == PlayerSettlementConstructionOperation.Rebuild ||
             request.Operation == PlayerSettlementConstructionOperation.Overwrite) && target == null)
        {
            failure = "requested construction target is not registered";
            return false;
        }
        if (request.Operation == PlayerSettlementConstructionOperation.BuildVillage && bound == null)
        {
            failure = "requested village bound settlement is not registered";
            return false;
        }
        if ((target != null && target.OwnerClan != actor.Clan) ||
            (bound != null && bound.OwnerClan != actor.Clan))
        {
            failure = "requested settlement is not owned by the authenticated controller's clan";
            return false;
        }
        if (!TryAuthorize(behavior, actor, request.Operation, target, out failure))
            return false;

        var template = FindTemplate(behavior, request.TemplateId, request.Operation, target, bound);
        if (template == null || !TemplateMatches(template, request.Operation, request.CultureId, target))
        {
            failure = "requested template is not an admitted culture/type template";
            return false;
        }

        SetField(behavior, "availableModels", CreateSingleItemList(template));
        SetField(behavior, "currentModelOptionIdx", 0);
        SetField(behavior, "settlementVisualPrefab", request.TemplateId);
        SetField(behavior, "deepEditPrefab", request.TemplateId);
        SetField(behavior, "settlementPlacementFrame", RestoreFrame(request.SettlementFrame));
        SetField(behavior, "gatePlacementFrame", RestoreFrame(request.GateFrame));
        SetField(behavior, "deepTransformEdits", RestoreDeepEdits(request.DeepEdits));

        object owner = behavior;
        if (request.Operation == PlayerSettlementConstructionOperation.Overwrite)
        {
            owner = CreateClosure(methods[request.Operation].DeclaringType, behavior);
            SetField(owner, "target", target);
            SetField(owner, "settlementType", SettlementType(target));
        }
        else if (request.Operation == PlayerSettlementConstructionOperation.Rebuild)
        {
            owner = CreateClosure(methods[request.Operation].DeclaringType, behavior);
            var item = FindMetadataItem(behavior, target);
            if (item == null)
            {
                failure = "requested generated settlement metadata item is unavailable";
                return false;
            }
            SetField(owner, "target", item);
            SetField(owner, "settlementType", SettlementType(target));
        }
        else if (request.Operation == PlayerSettlementConstructionOperation.BuildVillage)
        {
            owner = CreateClosure(methods[request.Operation].DeclaringType, behavior);
            SetField(owner, "bound", bound);
            SetField(owner, "boundTarget", boundTargetSettlement == null
                ? null
                : FindMetadataItem(behavior, boundTargetSettlement));
            SetField(owner, "villageNumber", request.VillageNumber);
        }

        var invokeArguments = request.Operation == PlayerSettlementConstructionOperation.BuildVillage ||
                              request.Operation == PlayerSettlementConstructionOperation.Rebuild ||
                              request.Operation == PlayerSettlementConstructionOperation.Overwrite
            ? new object[] { request.SettlementName, culture, request.VillageType }
            : new object[] { request.SettlementName, culture };
        try
        {
            methods[request.Operation].Invoke(owner, invokeArguments);
            RefreshMetadata(behavior);
            failure = null;
            return true;
        }
        catch (TargetInvocationException exception)
        {
            throw new InvalidOperationException(
                "creator ApplyPlaced transaction failed after admission",
                exception.InnerException ?? exception);
        }
    }

    private object FindTemplate(
        object behavior,
        string templateId,
        PlayerSettlementConstructionOperation operation,
        Settlement target,
        Settlement bound)
    {
        var mainType = RequiredType("BannerlordPlayerSettlement.Main");
        var submodule = ReadStaticMember(mainType, "Submodule");
        var cultures = submodule == null ? null : ReadMember(submodule, "CultureTemplates") as IDictionary;
        if (cultures == null) return null;
        foreach (DictionaryEntry culture in cultures)
        {
            if (culture.Value is not IEnumerable templates) continue;
            foreach (var group in templates)
            {
                var requestedType = operation switch
                {
                    PlayerSettlementConstructionOperation.BuildTown => 1,
                    PlayerSettlementConstructionOperation.BuildCastle => 3,
                    PlayerSettlementConstructionOperation.BuildVillage => 2,
                    _ when target?.IsVillage == true => 2,
                    _ when target?.IsCastle == true => 3,
                    _ => 1,
                };
                var selectorName = requestedType == 2
                    ? "SelectVillageTemplates"
                    : requestedType == 3 ? "SelectCastleTemplates" : "SelectTownTemplates";
                var selector = behaviorType.GetMethod(
                    selectorName,
                    BindingFlags.Instance | BindingFlags.NonPublic) ??
                    throw new InvalidOperationException("Player Settlement template selector is unavailable: " + selectorName);
                var selectorArguments = requestedType == 2
                    ? new[] { group, (object)(bound?.IsCastle == true || target?.Village?.Bound?.IsCastle == true) }
                    : new[] { group };
                if (selector.Invoke(behavior, selectorArguments) is not IEnumerable items) continue;
                foreach (var item in items)
                    if (string.Equals(ReadStringMember(item, "Id"), templateId, StringComparison.Ordinal))
                        return item;
            }
        }
        return null;
    }

    private bool TryAuthorize(
        object behavior,
        Hero actor,
        PlayerSettlementConstructionOperation operation,
        Settlement target,
        out string failure)
    {
        var mainType = RequiredType("BannerlordPlayerSettlement.Main");
        var settings = ReadStaticMember(mainType, "Settings");
        var info = ReadStaticMember(playerSettlementInfoType, "Instance") ??
                   ReadField(behavior, "_playerSettlementInfo");
        if (settings == null || info == null || !ReadBool(settings, "Enabled"))
        {
            failure = "Player Settlement is not enabled on the host";
            return false;
        }
        if (ReadBool(settings, "RequireClanTier") &&
            actor.Clan.Tier < ReadInt(settings, "RequiredClanTier"))
        {
            failure = "authenticated clan tier is below the host requirement";
            return false;
        }

        if (operation == PlayerSettlementConstructionOperation.BuildTown &&
            Count(ReadMember(info, "Towns")) >= ReadInt(settings, "MaxTowns"))
        {
            failure = "the host town limit has been reached";
            return false;
        }
        if (operation == PlayerSettlementConstructionOperation.BuildCastle &&
            Count(ReadMember(info, "Castles")) >= ReadInt(settings, "MaxCastles"))
        {
            failure = "the host castle limit has been reached";
            return false;
        }
        if ((operation == PlayerSettlementConstructionOperation.BuildTown ||
             operation == PlayerSettlementConstructionOperation.BuildCastle ||
             operation == PlayerSettlementConstructionOperation.BuildVillage) &&
            ReadBool(settings, "SingleConstruction") && HasConstructionInProgress(info))
        {
            failure = "another Player Settlement construction is still in progress";
            return false;
        }

        var type = operation switch
        {
            PlayerSettlementConstructionOperation.BuildVillage => 2,
            PlayerSettlementConstructionOperation.BuildCastle => 3,
            PlayerSettlementConstructionOperation.BuildTown => 1,
            _ when target?.IsVillage == true => 2,
            _ when target?.IsCastle == true => 3,
            _ => 1,
        };
        var rebuilding = operation == PlayerSettlementConstructionOperation.Rebuild ||
                         operation == PlayerSettlementConstructionOperation.Overwrite;
        var requireName = type == 2 ? "RequireVillageGold" : type == 3 ? "RequireCastleGold" : "RequireGold";
        var amountName = rebuilding
            ? type == 2 ? "RebuildVillageRequiredGold" : type == 3 ? "RebuildCastleRequiredGold" : "RebuildTownRequiredGold"
            : type == 2 ? "RequiredVillageGold" : type == 3 ? "RequiredCastleGold" : "RequiredGold";
        var availableGold = rebuilding ? actor.Clan.Gold : actor.Gold;
        if (ReadBool(settings, requireName) && availableGold < ReadInt(settings, amountName))
        {
            failure = "authenticated controller does not meet the host construction cost";
            return false;
        }

        failure = null;
        return true;
    }

    private static bool HasConstructionInProgress(object info)
    {
        foreach (var listName in new[] { "Towns", "Castles", "PlayerVillages" })
        {
            if (ReadMember(info, listName) is not IEnumerable items) continue;
            foreach (var item in items)
            {
                if (IsFuture(item)) return true;
                if (ReadMember(item, "Villages") is not IEnumerable villages) continue;
                foreach (var village in villages)
                    if (IsFuture(village)) return true;
            }
        }
        return false;
    }

    private static bool IsFuture(object item) =>
        ReadMember(item, "BuildEnd") is CampaignTime end && end.IsFuture;

    private static int Count(object value) => value is ICollection collection ? collection.Count : 0;
    private static bool ReadBool(object owner, string name) => Convert.ToBoolean(ReadMember(owner, name));
    private static int ReadInt(object owner, string name) => Convert.ToInt32(ReadMember(owner, name));

    private static bool TemplateMatches(
        object template,
        PlayerSettlementConstructionOperation operation,
        string cultureId,
        Settlement target)
    {
        var templateCulture = ReadStringMember(template, "Culture");
        if (!string.IsNullOrEmpty(templateCulture) &&
            !string.Equals(templateCulture, cultureId, StringComparison.Ordinal)) return false;
        var type = Convert.ToInt32(ReadMember(template, "Type"));
        var expected = operation switch
        {
            PlayerSettlementConstructionOperation.BuildTown => 1,
            PlayerSettlementConstructionOperation.BuildCastle => 3,
            PlayerSettlementConstructionOperation.BuildVillage => 2,
            _ when target?.IsVillage == true => 2,
            _ when target?.IsCastle == true => 3,
            _ => 1,
        };
        return type == expected;
    }

    private object FindMetadataItem(object behavior, Settlement settlement)
    {
        if (settlement == null) return null;
        var info = ReadStaticMember(playerSettlementInfoType, "Instance") ??
                   ReadField(behavior, "_playerSettlementInfo");
        if (info == null) return null;
        foreach (var listName in new[] { "Towns", "Castles", "PlayerVillages", "OverwriteSettlements" })
        {
            if (ReadMember(info, listName) is not IEnumerable items) continue;
            foreach (var item in items)
            {
                if (ReferenceEquals(ReadSettlementFromItem(item), settlement)) return item;
                if (ReadMember(item, "Villages") is not IEnumerable villages) continue;
                foreach (var village in villages)
                    if (ReferenceEquals(ReadSettlementFromItem(village), settlement)) return village;
            }
        }
        return null;
    }

    private void RefreshMetadata(object behavior)
    {
        var info = ReadStaticMember(playerSettlementInfoType, "Instance") ??
                   ReadField(behavior, "_playerSettlementInfo");
        var create = metadataType.GetMethod(
            "Create",
            BindingFlags.Static | BindingFlags.Public,
            binder: null,
            types: new[] { playerSettlementInfoType },
            modifiers: null) ??
            throw new InvalidOperationException("Player Settlement MetaV3.Create contract is unavailable");
        var metadata = create.Invoke(null, new[] { info }) ??
                       throw new InvalidOperationException("Player Settlement MetaV3.Create returned null after construction");
        SetField(behavior, "_metaV3", metadata);
    }

    private IList CreateSingleItemList(object template)
    {
        var listType = typeof(List<>).MakeGenericType(template.GetType());
        var list = (IList)Activator.CreateInstance(listType);
        list.Add(template);
        return list;
    }

    private object CreateClosure(Type type, object behavior)
    {
        var owner = Activator.CreateInstance(type);
        SetField(owner, "<>4__this", behavior);
        return owner;
    }

    private PlayerSettlementDeepEditIntent[] CaptureDeepEdits(IEnumerable edits)
    {
        if (edits == null) return Array.Empty<PlayerSettlementDeepEditIntent>();
        var result = new List<PlayerSettlementDeepEditIntent>();
        foreach (var edit in edits)
        {
            if (edit == null) continue;
            var deleted = Convert.ToBoolean(ReadField(edit, "IsDeleted"));
            result.Add(new PlayerSettlementDeepEditIntent(
                Convert.ToInt32(ReadField(edit, "Index")),
                ReadField(edit, "Name") as string,
                deleted,
                deleted ? null : CaptureSavedTransform(ReadField(edit, "Transform"))));
        }
        return result.ToArray();
    }

    private PlayerSettlementBitTransform CaptureSavedTransform(object transform)
    {
        if (transform == null) return null;
        var rotation = ReadField(transform, "RotationScale");
        var position = ReadField(transform, "Position");
        var offsets = ReadField(transform, "Offsets");
        if (rotation == null || position == null || offsets == null) return null;
        var values = new List<int>(PlayerSettlementBitTransform.DeepEditBitCount);
        foreach (var name in new[] { "sx", "sy", "sz", "fx", "fy", "fz", "ux", "uy", "uz" })
            values.Add(FloatBits(ReadField(rotation, name)));
        foreach (var vector in new[] { position, offsets })
            foreach (var name in new[] { "x", "y", "z", "w" }) values.Add(FloatBits(ReadField(vector, name)));
        return new PlayerSettlementBitTransform(values);
    }

    private IList RestoreDeepEdits(IEnumerable<PlayerSettlementDeepEditIntent> intents)
    {
        var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(deepEditType));
        foreach (var intent in intents ?? Array.Empty<PlayerSettlementDeepEditIntent>())
        {
            var edit = Activator.CreateInstance(deepEditType);
            SetField(edit, "Index", intent.Index);
            SetField(edit, "Name", intent.Name);
            SetField(edit, "IsDeleted", intent.IsDeleted);
            SetField(edit, "Transform", intent.IsDeleted ? null : RestoreSavedTransform(intent.Transform));
            list.Add(edit);
        }
        return list;
    }

    private object RestoreSavedTransform(PlayerSettlementBitTransform data)
    {
        var bits = data.Bits;
        var transform = Activator.CreateInstance(transformType);
        var rotation = Activator.CreateInstance(mat3Type);
        var position = Activator.CreateInstance(vec3Type);
        var offsets = Activator.CreateInstance(vec3Type);
        var index = 0;
        foreach (var name in new[] { "sx", "sy", "sz", "fx", "fy", "fz", "ux", "uy", "uz" })
            SetField(rotation, name, BitsFloat(bits[index++]));
        foreach (var vector in new[] { position, offsets })
            foreach (var name in new[] { "x", "y", "z", "w" }) SetField(vector, name, BitsFloat(bits[index++]));
        SetField(transform, "RotationScale", rotation);
        SetField(transform, "Position", position);
        SetField(transform, "Offsets", offsets);
        return transform;
    }

    private static PlayerSettlementBitTransform CaptureFrame(object value)
    {
        if (value == null) return null;
        var frame = (MatrixFrame)value;
        return new PlayerSettlementBitTransform(new[]
        {
            FloatBits(frame.rotation.s.x), FloatBits(frame.rotation.s.y), FloatBits(frame.rotation.s.z),
            FloatBits(frame.rotation.f.x), FloatBits(frame.rotation.f.y), FloatBits(frame.rotation.f.z),
            FloatBits(frame.rotation.u.x), FloatBits(frame.rotation.u.y), FloatBits(frame.rotation.u.z),
            FloatBits(frame.origin.x), FloatBits(frame.origin.y), FloatBits(frame.origin.z), FloatBits(frame.origin.w),
        });
    }

    private static object RestoreFrame(PlayerSettlementBitTransform data)
    {
        if (data == null) return null;
        var bits = data.Bits;
        return new MatrixFrame(
            new Mat3(
                BitsFloat(bits[0]), BitsFloat(bits[1]), BitsFloat(bits[2]),
                BitsFloat(bits[3]), BitsFloat(bits[4]), BitsFloat(bits[5]),
                BitsFloat(bits[6]), BitsFloat(bits[7]), BitsFloat(bits[8])),
            new Vec3(BitsFloat(bits[9]), BitsFloat(bits[10]), BitsFloat(bits[11]), BitsFloat(bits[12])));
    }

    private void ValidateFields()
    {
        foreach (var field in new[]
                 {
                     "availableModels", "currentModelOptionIdx", "settlementPlacementFrame",
                     "gatePlacementFrame", "settlementVisualPrefab", "deepEditPrefab", "deepTransformEdits", "_metaV3",
                     "_playerSettlementInfo",
                 })
            if (behaviorType.GetField(field, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) == null)
                throw new InvalidOperationException("Player Settlement construction field shape changed: " + field);
    }

    private Type RequiredType(string name) => assembly.GetType(name, throwOnError: true, ignoreCase: false);

    private static PlayerSettlementConstructionOperation MethodOperation(MethodInfo method)
    {
        var name = method?.Name ?? string.Empty;
        if (name.IndexOf("BuildTown", StringComparison.Ordinal) >= 0) return PlayerSettlementConstructionOperation.BuildTown;
        if (name.IndexOf("BuildCastle", StringComparison.Ordinal) >= 0) return PlayerSettlementConstructionOperation.BuildCastle;
        if (name.IndexOf("BuildVillageFor", StringComparison.Ordinal) >= 0) return PlayerSettlementConstructionOperation.BuildVillage;
        if (name.IndexOf("Rebuild", StringComparison.Ordinal) >= 0) return PlayerSettlementConstructionOperation.Rebuild;
        if (name.IndexOf("Overwrite", StringComparison.Ordinal) >= 0) return PlayerSettlementConstructionOperation.Overwrite;
        throw new InvalidOperationException("unknown Player Settlement ApplyPlaced method " + name);
    }

    private static string SettlementType(Settlement settlement) =>
        settlement.IsVillage ? "Village" : settlement.IsCastle ? "Castle" : settlement.IsTown ? "Town" : "None";

    private static Settlement ReadSettlementFromItem(object item) =>
        ReadMember(item, "Settlement") as Settlement ?? ReadMember(item, "settlement") as Settlement;

    private static object ReadStaticMember(Type type, string name) =>
        type.GetProperty(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null) ??
        type.GetField(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null);

    private static object ReadMember(object owner, string name)
    {
        if (owner == null) return null;
        return owner.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(owner) ??
               owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(owner);
    }

    private static string ReadStringMember(object owner, string name) => ReadMember(owner, name) as string ?? string.Empty;

    private static object ReadField(object owner, string name)
    {
        if (owner == null) return null;
        var field = owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (field == null) throw new InvalidOperationException($"Player Settlement field {owner.GetType().FullName}.{name} is unavailable");
        return field.GetValue(owner);
    }

    private static void SetField(object owner, string name, object value)
    {
        var field = owner?.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) ??
                    throw new InvalidOperationException($"Player Settlement field {owner?.GetType().FullName}.{name} is unavailable");
        if (field.FieldType.IsEnum && value is string enumName)
            value = Enum.Parse(field.FieldType, enumName, ignoreCase: false);
        field.SetValue(owner, value);
    }

    private static int FloatBits(object value) => FloatBits(Convert.ToSingle(value, CultureInfo.InvariantCulture));
    private static int FloatBits(float value) => BitConverter.ToInt32(BitConverter.GetBytes(value), 0);
    private static float BitsFloat(int value) => BitConverter.ToSingle(BitConverter.GetBytes(value), 0);
}
