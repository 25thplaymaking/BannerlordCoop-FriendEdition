using System.Reflection;
using System.Reflection.Metadata;
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
            return new(item.ModuleId, item.RelativePath, item.Path, item.Included, item.Platform, hash, false, null, [], null);

        MetadataReader metadata = pe.GetMetadataReader();
        if (!metadata.IsAssembly)
            return new(item.ModuleId, item.RelativePath, item.Path, item.Included, item.Platform, hash, false, null, [], null);

        AssemblyDefinition definition = metadata.GetAssemblyDefinition();
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
        return new(item.ModuleId, item.RelativePath, item.Path, item.Included, item.Platform, hash, true, identity, references.ToArray(), null);
    }
    catch (BadImageFormatException)
    {
        return new(item.ModuleId, item.RelativePath, item.Path, item.Included, item.Platform, hash, false, null, [], null);
    }
    catch (Exception exception)
    {
        return new(item.ModuleId, item.RelativePath, item.Path, item.Included, item.Platform, hash, false, null, [], exception.Message);
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
    bool Managed, AssemblyIdentity? Identity, AssemblyIdentity[] References, string? Error);
internal sealed record AssemblyIdentity(string Name, string Version, string Culture, string PublicKeyToken, string FullName);
