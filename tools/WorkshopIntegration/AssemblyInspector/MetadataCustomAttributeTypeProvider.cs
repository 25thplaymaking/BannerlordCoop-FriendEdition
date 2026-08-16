using System.Reflection.Metadata;

internal sealed class MetadataCustomAttributeTypeProvider : ICustomAttributeTypeProvider<string>
{
    private readonly MetadataTypeNameProvider typeNames;

    internal MetadataCustomAttributeTypeProvider(MetadataTypeNameProvider typeNames)
    {
        this.typeNames = typeNames;
    }

    public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeNames.GetPrimitiveType(typeCode);
    public string GetSystemType() => "System.Type";
    public string GetSZArrayType(string elementType) => elementType + "[]";
    public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) =>
        typeNames.GetTypeFromDefinition(reader, handle, rawTypeKind);
    public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) =>
        typeNames.GetTypeFromReference(reader, handle, rawTypeKind);
    public string GetTypeFromSerializedName(string name) => name;
    public PrimitiveTypeCode GetUnderlyingEnumType(string type) => PrimitiveTypeCode.Int32;
    public bool IsSystemType(string type) => string.Equals(type, "System.Type", StringComparison.Ordinal);

    internal string GetAttributeTypeName(MetadataReader reader, EntityHandle constructor)
    {
        EntityHandle type = constructor.Kind switch
        {
            HandleKind.MethodDefinition => reader.GetMethodDefinition((MethodDefinitionHandle)constructor).GetDeclaringType(),
            HandleKind.MemberReference => reader.GetMemberReference((MemberReferenceHandle)constructor).Parent,
            _ => throw new BadImageFormatException($"Unsupported attribute constructor {constructor.Kind}."),
        };
        return typeNames.GetTypeName(reader, type);
    }
}
