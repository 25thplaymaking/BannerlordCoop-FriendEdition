using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

internal sealed record AuthorityEvidence(
    string[] DirectSignals,
    string[] TransitiveSignals,
    string[] CalledMembers);

internal static class AuthoritySignalScanner
{
    private static readonly IReadOnlyDictionary<ushort, OpCode> OpCodesByValue =
        typeof(OpCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.FieldType == typeof(OpCode))
            .Select(field => (OpCode)field.GetValue(null)!)
            .ToDictionary(opCode => unchecked((ushort)opCode.Value));

    internal static IReadOnlyDictionary<int, AuthorityEvidence> Scan(
        PEReader pe,
        MetadataReader metadata)
    {
        var typeProvider = new MetadataTypeNameProvider();
        var scans = new Dictionary<int, MethodScan>();

        foreach (MethodDefinitionHandle methodHandle in metadata.MethodDefinitions)
        {
            int token = MetadataTokens.GetToken(methodHandle);
            MethodDefinition method = metadata.GetMethodDefinition(methodHandle);
            string methodName = metadata.GetString(method.Name);
            bool isStaticConstructor = methodName == ".cctor";
            var scan = new MethodScan();
            scans.Add(token, scan);

            if (method.RelativeVirtualAddress == 0)
                continue;

            MethodBodyBlock body = pe.GetMethodBody(method.RelativeVirtualAddress);
            BlobReader reader = body.GetILReader();
            while (reader.RemainingBytes > 0)
            {
                OpCode opCode = ReadOpCode(ref reader);
                EntityHandle operand = ReadOperand(ref reader, opCode.OperandType);
                if (operand.IsNil)
                    continue;

                EntityHandle resolvedOperand = UnwrapMethodSpecification(metadata, operand);
                string? member = ResolveMemberName(metadata, typeProvider, resolvedOperand);
                if (member is not null)
                {
                    scan.CalledMembers.Add(member);
                    string? signal = Classify(member);
                    if (signal is not null)
                        scan.DirectSignals.Add(signal);
                }

                if (resolvedOperand.Kind == HandleKind.FieldDefinition)
                {
                    FieldDefinitionHandle fieldHandle = (FieldDefinitionHandle)resolvedOperand;
                    FieldDefinition field = metadata.GetFieldDefinition(fieldHandle);
                    if ((field.Attributes & FieldAttributes.Static) != 0 &&
                        !IsCompilerCacheField(metadata, field))
                    {
                        if (opCode == OpCodes.Stsfld && !isStaticConstructor && member is not null &&
                            !IsSingletonCacheWrite(methodName, metadata.GetString(field.Name)))
                            scan.DirectSignals.Add($"shared-state-write:{member}");

                        string fieldType = field.DecodeSignature(typeProvider, null);
                        if (IsMutableCollectionType(fieldType) && member is not null)
                            scan.MutableStaticFields.Add(member);
                    }
                }

                if (IsCall(opCode) && member is not null && IsCollectionMutation(member))
                    scan.HasCollectionMutation = true;

                if (IsCall(opCode) && resolvedOperand.Kind == HandleKind.MethodDefinition)
                    scan.Callees.Add(MetadataTokens.GetToken((MethodDefinitionHandle)resolvedOperand));
            }

            if (scan.HasCollectionMutation)
            {
                foreach (string field in scan.MutableStaticFields)
                    scan.DirectSignals.Add($"shared-state-mutation:{field}");
            }
        }

        bool changed;
        do
        {
            changed = false;
            foreach ((int _, MethodScan scan) in scans)
            {
                foreach (int calleeToken in scan.Callees)
                {
                    if (!scans.TryGetValue(calleeToken, out MethodScan? callee) ||
                        (callee.DirectSignals.Count == 0 && callee.TransitiveSignals.Count == 0))
                    {
                        continue;
                    }

                    MethodDefinitionHandle calleeHandle = MetadataTokens.MethodDefinitionHandle(calleeToken & 0x00FFFFFF);
                    string calleeName = ResolveMethodDefinitionName(metadata, typeProvider, calleeHandle);
                    if (scan.TransitiveSignals.Add($"calls-authority-sensitive:{calleeName}"))
                        changed = true;
                }
            }
        }
        while (changed);

        return scans.ToDictionary(
            pair => pair.Key,
            pair => new AuthorityEvidence(
                pair.Value.DirectSignals.Order(StringComparer.Ordinal).ToArray(),
                pair.Value.TransitiveSignals.Order(StringComparer.Ordinal).ToArray(),
                pair.Value.CalledMembers.Order(StringComparer.Ordinal).ToArray()));
    }

    private static OpCode ReadOpCode(ref BlobReader reader)
    {
        ushort value = reader.ReadByte();
        if (value == 0xFE)
            value = (ushort)(0xFE00 | reader.ReadByte());
        if (!OpCodesByValue.TryGetValue(value, out OpCode opCode))
            throw new BadImageFormatException($"Unknown IL opcode 0x{value:X4}.");
        return opCode;
    }

    private static EntityHandle ReadOperand(ref BlobReader reader, OperandType operandType)
    {
        switch (operandType)
        {
            case OperandType.InlineField:
            case OperandType.InlineMethod:
            case OperandType.InlineTok:
            case OperandType.InlineType:
                return MetadataTokens.EntityHandle(reader.ReadInt32());
            case OperandType.InlineBrTarget:
            case OperandType.InlineI:
            case OperandType.InlineSig:
            case OperandType.InlineString:
                reader.ReadInt32();
                break;
            case OperandType.InlineI8:
            case OperandType.InlineR:
                reader.ReadInt64();
                break;
            case OperandType.InlineSwitch:
                int count = reader.ReadInt32();
                if (count < 0 || count > reader.RemainingBytes / sizeof(int))
                    throw new BadImageFormatException("Invalid IL switch operand.");
                for (int index = 0; index < count; index++)
                    reader.ReadInt32();
                break;
            case OperandType.InlineVar:
                reader.ReadUInt16();
                break;
            case OperandType.ShortInlineBrTarget:
            case OperandType.ShortInlineI:
            case OperandType.ShortInlineVar:
                reader.ReadByte();
                break;
            case OperandType.ShortInlineR:
                reader.ReadSingle();
                break;
            case OperandType.InlineNone:
                break;
            default:
                throw new BadImageFormatException($"Unsupported IL operand type {operandType}.");
        }

        return default;
    }

    private static EntityHandle UnwrapMethodSpecification(MetadataReader metadata, EntityHandle handle)
        => handle.Kind == HandleKind.MethodSpecification
            ? metadata.GetMethodSpecification((MethodSpecificationHandle)handle).Method
            : handle;

    private static bool IsCall(OpCode opCode)
        => opCode == OpCodes.Call || opCode == OpCodes.Callvirt || opCode == OpCodes.Newobj;

    private static bool IsMutableCollectionType(string type)
        => type.StartsWith("System.Collections.Generic.Dictionary`", StringComparison.Ordinal) ||
           type.StartsWith("System.Collections.Generic.HashSet`", StringComparison.Ordinal) ||
           type.StartsWith("System.Collections.Generic.List`", StringComparison.Ordinal) ||
           type.StartsWith("System.Collections.Concurrent.ConcurrentDictionary`", StringComparison.Ordinal) ||
           type.StartsWith("System.Collections.IDictionary", StringComparison.Ordinal) ||
           type.StartsWith("System.Collections.IList", StringComparison.Ordinal);

    private static bool IsCompilerCacheField(MetadataReader metadata, FieldDefinition field)
    {
        string fieldName = metadata.GetString(field.Name);
        if (fieldName.StartsWith("<>9", StringComparison.Ordinal) ||
            fieldName.StartsWith("<>f__am$cache", StringComparison.Ordinal))
        {
            return true;
        }

        TypeDefinition declaringType = metadata.GetTypeDefinition(field.GetDeclaringType());
        return metadata.GetString(declaringType.Name).StartsWith("<>O", StringComparison.Ordinal);
    }

    private static bool IsSingletonCacheWrite(string methodName, string fieldName)
        => (methodName == "get_Instance" || methodName == "set_Instance") &&
           (fieldName.Equals("_instance", StringComparison.OrdinalIgnoreCase) ||
            fieldName.Equals("instance", StringComparison.OrdinalIgnoreCase));

    private static bool IsCollectionMutation(string member)
    {
        int separator = member.LastIndexOf('.');
        string method = separator < 0 ? member : member[(separator + 1)..];
        return method is "Add" or "AddRange" or "Clear" or "Dequeue" or "Enqueue" or
            "ExceptWith" or "GetOrAdd" or "IntersectWith" or "Pop" or "Push" or
            "Remove" or "RemoveAll" or "RemoveAt" or "SymmetricExceptWith" or
            "TryAdd" or "TryRemove" or "UnionWith" or "set_Item";
    }

    private static string? ResolveMemberName(
        MetadataReader metadata,
        MetadataTypeNameProvider typeProvider,
        EntityHandle handle)
        => handle.Kind switch
        {
            HandleKind.MethodDefinition => ResolveMethodDefinitionName(metadata, typeProvider, (MethodDefinitionHandle)handle),
            HandleKind.MemberReference => ResolveMemberReferenceName(metadata, typeProvider, (MemberReferenceHandle)handle),
            HandleKind.FieldDefinition => ResolveFieldDefinitionName(metadata, typeProvider, (FieldDefinitionHandle)handle),
            HandleKind.TypeDefinition => typeProvider.GetTypeFromDefinition(metadata, (TypeDefinitionHandle)handle, 0),
            HandleKind.TypeReference => typeProvider.GetTypeFromReference(metadata, (TypeReferenceHandle)handle, 0),
            HandleKind.TypeSpecification => typeProvider.GetTypeFromSpecification(metadata, null, (TypeSpecificationHandle)handle, 0),
            _ => null,
        };

    private static string ResolveMethodDefinitionName(
        MetadataReader metadata,
        MetadataTypeNameProvider typeProvider,
        MethodDefinitionHandle handle)
    {
        MethodDefinition method = metadata.GetMethodDefinition(handle);
        string type = typeProvider.GetTypeFromDefinition(metadata, method.GetDeclaringType(), 0);
        return $"{type}.{metadata.GetString(method.Name)}";
    }

    private static string ResolveFieldDefinitionName(
        MetadataReader metadata,
        MetadataTypeNameProvider typeProvider,
        FieldDefinitionHandle handle)
    {
        FieldDefinition field = metadata.GetFieldDefinition(handle);
        string type = typeProvider.GetTypeFromDefinition(metadata, field.GetDeclaringType(), 0);
        return $"{type}.{metadata.GetString(field.Name)}";
    }

    private static string ResolveMemberReferenceName(
        MetadataReader metadata,
        MetadataTypeNameProvider typeProvider,
        MemberReferenceHandle handle)
    {
        MemberReference reference = metadata.GetMemberReference(handle);
        string parent = ResolveMemberParentName(metadata, typeProvider, reference.Parent);
        return $"{parent}.{metadata.GetString(reference.Name)}";
    }

    private static string ResolveMemberParentName(
        MetadataReader metadata,
        MetadataTypeNameProvider typeProvider,
        EntityHandle parent)
        => parent.Kind switch
        {
            HandleKind.TypeDefinition => typeProvider.GetTypeFromDefinition(metadata, (TypeDefinitionHandle)parent, 0),
            HandleKind.TypeReference => typeProvider.GetTypeFromReference(metadata, (TypeReferenceHandle)parent, 0),
            HandleKind.TypeSpecification => typeProvider.GetTypeFromSpecification(metadata, null, (TypeSpecificationHandle)parent, 0),
            HandleKind.MethodDefinition => ResolveMethodDefinitionName(metadata, typeProvider, (MethodDefinitionHandle)parent),
            HandleKind.ModuleReference => metadata.GetString(metadata.GetModuleReference((ModuleReferenceHandle)parent).Name),
            _ => parent.Kind.ToString(),
        };

    private static string? Classify(string member)
    {
        if (ContainsAny(member,
                "TaleWorlds.CampaignSystem.Hero.get_MainHero",
                "TaleWorlds.CampaignSystem.MobileParty.get_MainParty",
                "TaleWorlds.CampaignSystem.Clan.get_PlayerClan",
                "TaleWorlds.MountAndBlade.Agent.get_Main"))
        {
            return $"global-player:{member}";
        }

        if (member.Contains(".Actions.", StringComparison.Ordinal) ||
            member.Contains("MBObjectManager.RegisterObject", StringComparison.Ordinal) ||
            member.EndsWith(".AddBehavior", StringComparison.Ordinal) ||
            member.EndsWith(".AddModel", StringComparison.Ordinal))
        {
            return $"campaign-mutation:{member}";
        }

        if (member.Contains("IDataStore.SyncData", StringComparison.Ordinal) ||
            member.StartsWith("System.IO.File.", StringComparison.Ordinal) ||
            member.StartsWith("Newtonsoft.Json.", StringComparison.Ordinal))
        {
            return $"persistence:{member}";
        }

        if (member.StartsWith("System.Random.", StringComparison.Ordinal) ||
            member == "System.Guid.NewGuid" ||
            member == "System.DateTime.get_Now" ||
            member == "System.DateTime.get_UtcNow")
        {
            return $"randomness:{member}";
        }

        return null;
    }

    private static bool ContainsAny(string value, params string[] candidates)
        => candidates.Any(candidate => value.Contains(candidate, StringComparison.Ordinal));

    private sealed class MethodScan
    {
        internal HashSet<string> DirectSignals { get; } = new(StringComparer.Ordinal);
        internal HashSet<string> TransitiveSignals { get; } = new(StringComparer.Ordinal);
        internal HashSet<string> CalledMembers { get; } = new(StringComparer.Ordinal);
        internal HashSet<int> Callees { get; } = [];
        internal HashSet<string> MutableStaticFields { get; } = new(StringComparer.Ordinal);
        internal bool HasCollectionMutation { get; set; }
    }
}
