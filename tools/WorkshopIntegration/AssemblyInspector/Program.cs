using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: FriendEdition.WorkshopAssemblyInspector <request.json> <result.json>");
    return 2;
}

var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
var request = JsonSerializer.Deserialize<InspectionRequest>(await File.ReadAllTextAsync(args[0]), options)
    ?? throw new InvalidDataException("Inspection request is empty.");
var results = new List<AssemblyInspection>(request.Files.Length);
foreach (var item in request.Files)
    results.Add(Inspect(item));
await File.WriteAllTextAsync(args[1], JsonSerializer.Serialize(new InspectionResult(results.ToArray()), options));
return 0;

static AssemblyInspection Inspect(InspectionFile item)
{
    string hash;
    using (var stream = File.OpenRead(item.Path))
        hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();

    try
    {
        using var stream = File.OpenRead(item.Path);
        using var pe = new PEReader(stream, PEStreamOptions.LeaveOpen);
        if (!pe.HasMetadata)
            return new(item.ModuleId, item.RelativePath, item.Path, item.Included, item.Platform, hash, false, null, [], [], null);

        MetadataReader metadata = pe.GetMetadataReader();
        if (!metadata.IsAssembly)
            return new(item.ModuleId, item.RelativePath, item.Path, item.Included, item.Platform, hash, false, null, [], [], null);

        AssemblyDefinition definition = metadata.GetAssemblyDefinition();
        IReadOnlyDictionary<int, AuthorityEvidence> authorityEvidence = AuthoritySignalScanner.Scan(pe, metadata);
        var identity = Identity(
            metadata.GetString(definition.Name), definition.Version,
            definition.Culture.IsNil ? null : metadata.GetString(definition.Culture),
            Token(metadata, definition.PublicKey, hasFullKey: true));

        var references = new List<AssemblyIdentity>();
        foreach (AssemblyReferenceHandle handle in metadata.AssemblyReferences)
        {
            AssemblyReference reference = metadata.GetAssemblyReference(handle);
            bool fullKey = (reference.Flags & AssemblyFlags.PublicKey) != 0;
            references.Add(Identity(
                metadata.GetString(reference.Name), reference.Version,
                reference.Culture.IsNil ? null : metadata.GetString(reference.Culture),
                Token(metadata, reference.PublicKeyOrToken, fullKey)));
        }
        references.Sort((left, right) => StringComparer.OrdinalIgnoreCase.Compare(left.FullName, right.FullName));

        var methods = new List<MethodInspection>();
        var typeProvider = new MetadataTypeNameProvider();
        foreach (TypeDefinitionHandle typeHandle in metadata.TypeDefinitions)
        {
            TypeDefinition type = metadata.GetTypeDefinition(typeHandle);
            string declaringType = typeProvider.GetTypeFromDefinition(metadata, typeHandle, 0);
            foreach (MethodDefinitionHandle methodHandle in type.GetMethods())
            {
                MethodDefinition method = metadata.GetMethodDefinition(methodHandle);
                MethodSignature<string> signature = method.DecodeSignature(typeProvider, genericContext: null);
                methods.Add(new MethodInspection(
                    declaringType,
                    metadata.GetString(method.Name),
                    signature.ReturnType,
                    signature.ParameterTypes.ToArray(),
                    method.GetGenericParameters().Count,
                    method.Attributes.ToString(),
                    method.ImplAttributes.ToString(),
                    $"0x{MetadataTokens.GetToken(methodHandle):X8}",
                    method.RelativeVirtualAddress,
                    authorityEvidence[MetadataTokens.GetToken(methodHandle)]));
            }
        }
        methods.Sort((left, right) =>
        {
            int typeOrder = StringComparer.Ordinal.Compare(left.DeclaringType, right.DeclaringType);
            return typeOrder != 0
                ? typeOrder
                : StringComparer.Ordinal.Compare(left.MetadataToken, right.MetadataToken);
        });

        return new(item.ModuleId, item.RelativePath, item.Path, item.Included, item.Platform, hash, true,
            identity, references.ToArray(), methods.ToArray(), null);
    }
    catch (BadImageFormatException)
    {
        return new(item.ModuleId, item.RelativePath, item.Path, item.Included, item.Platform, hash, false, null, [], [], null);
    }
    catch (Exception exception)
    {
        return new(item.ModuleId, item.RelativePath, item.Path, item.Included, item.Platform, hash, false, null, [], [], exception.Message);
    }
}

static AssemblyIdentity Identity(string name, Version version, string? culture, string? token)
{
    string normalizedCulture = string.IsNullOrWhiteSpace(culture) ? "neutral" : culture;
    string normalizedToken = string.IsNullOrWhiteSpace(token) ? "null" : token.ToLowerInvariant();
    return new(name, version.ToString(), normalizedCulture, normalizedToken,
        $"{name}, Version={version}, Culture={normalizedCulture}, PublicKeyToken={normalizedToken}");
}

static string? Token(MetadataReader metadata, BlobHandle handle, bool hasFullKey)
{
    if (handle.IsNil) return null;
    byte[] bytes = metadata.GetBlobBytes(handle);
    if (bytes.Length == 0) return null;
    if (!hasFullKey) return Convert.ToHexString(bytes).ToLowerInvariant();
    byte[] digest = SHA1.HashData(bytes);
    byte[] token = digest[^8..];
    Array.Reverse(token);
    return Convert.ToHexString(token).ToLowerInvariant();
}

internal sealed record InspectionRequest(InspectionFile[] Files);
internal sealed record InspectionFile(string ModuleId, string RelativePath, string Path, bool Included, string Platform);
internal sealed record InspectionResult(AssemblyInspection[] Files);
internal sealed record AssemblyInspection(
    string ModuleId, string RelativePath, string Path, bool Included, string Platform, string Sha256,
    bool Managed, AssemblyIdentity? Identity, AssemblyIdentity[] References, MethodInspection[] Methods, string? Error);
internal sealed record AssemblyIdentity(string Name, string Version, string Culture, string PublicKeyToken, string FullName);
internal sealed record MethodInspection(
    string DeclaringType,
    string Name,
    string ReturnType,
    string[] ParameterTypes,
    int GenericArity,
    string Attributes,
    string ImplementationAttributes,
    string MetadataToken,
    int RelativeVirtualAddress,
    AuthorityEvidence AuthorityEvidence);

internal sealed class MetadataTypeNameProvider : ISignatureTypeProvider<string, object?>
{
    public string GetArrayType(string elementType, ArrayShape shape)
        => $"{elementType}[{new string(',', Math.Max(0, shape.Rank - 1))}]";

    public string GetByReferenceType(string elementType) => elementType + "&";

    public string GetFunctionPointerType(MethodSignature<string> signature)
        => $"delegate*<{string.Join(",", signature.ParameterTypes.Append(signature.ReturnType))}>";

    public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments)
        => $"{genericType}<{string.Join(",", typeArguments)}>";

    public string GetGenericMethodParameter(object? genericContext, int index) => $"!!{index}";

    public string GetGenericTypeParameter(object? genericContext, int index) => $"!{index}";

    public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired)
        => $"{unmodifiedType} {(isRequired ? "modreq" : "modopt")}({modifier})";

    public string GetPinnedType(string elementType) => elementType + " pinned";

    public string GetPointerType(string elementType) => elementType + "*";

    public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode switch
    {
        PrimitiveTypeCode.Boolean => "System.Boolean",
        PrimitiveTypeCode.Byte => "System.Byte",
        PrimitiveTypeCode.Char => "System.Char",
        PrimitiveTypeCode.Double => "System.Double",
        PrimitiveTypeCode.Int16 => "System.Int16",
        PrimitiveTypeCode.Int32 => "System.Int32",
        PrimitiveTypeCode.Int64 => "System.Int64",
        PrimitiveTypeCode.IntPtr => "System.IntPtr",
        PrimitiveTypeCode.Object => "System.Object",
        PrimitiveTypeCode.SByte => "System.SByte",
        PrimitiveTypeCode.Single => "System.Single",
        PrimitiveTypeCode.String => "System.String",
        PrimitiveTypeCode.TypedReference => "System.TypedReference",
        PrimitiveTypeCode.UInt16 => "System.UInt16",
        PrimitiveTypeCode.UInt32 => "System.UInt32",
        PrimitiveTypeCode.UInt64 => "System.UInt64",
        PrimitiveTypeCode.UIntPtr => "System.UIntPtr",
        PrimitiveTypeCode.Void => "System.Void",
        _ => typeCode.ToString(),
    };

    public string GetSZArrayType(string elementType) => elementType + "[]";

    public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
    {
        TypeDefinition definition = reader.GetTypeDefinition(handle);
        string name = reader.GetString(definition.Name);
        TypeDefinitionHandle declaringType = definition.GetDeclaringType();
        if (!declaringType.IsNil)
            return GetTypeFromDefinition(reader, declaringType, rawTypeKind) + "+" + name;
        string @namespace = reader.GetString(definition.Namespace);
        return string.IsNullOrEmpty(@namespace) ? name : @namespace + "." + name;
    }

    public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
    {
        TypeReference reference = reader.GetTypeReference(handle);
        string name = reader.GetString(reference.Name);
        EntityHandle scope = reference.ResolutionScope;
        if (scope.Kind == HandleKind.TypeReference)
            return GetTypeFromReference(reader, (TypeReferenceHandle)scope, rawTypeKind) + "+" + name;
        string @namespace = reader.GetString(reference.Namespace);
        return string.IsNullOrEmpty(@namespace) ? name : @namespace + "." + name;
    }

    public string GetTypeFromSpecification(
        MetadataReader reader,
        object? genericContext,
        TypeSpecificationHandle handle,
        byte rawTypeKind)
        => reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);
}
