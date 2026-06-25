using System.IO;
using System.Reflection.Metadata;
using Microsoft.CodeAnalysis;

namespace Elarion.Generators;

/// <summary>
/// Reads raw <c>[assembly: AssemblyMetadata(key, value)]</c> entries from a
/// <see cref="MetadataReference"/> without symbol binding, suitable for use in
/// <c>MetadataReferencesProvider</c> pipelines (cached per reference — a source edit re-reads nothing).
/// Both <see cref="ElarionManifest"/> and <c>EntityConfigurationManifest</c> delegate their reference reading here.
/// </summary>
internal static class AssemblyMetadataReader
{
    /// <summary>
    /// Returns every <c>[assembly: AssemblyMetadata(key, value)]</c> pair found in
    /// <paramref name="reference"/>, reading PE metadata directly for PE references and
    /// using symbol attributes for in-compilation references.
    /// Returns an empty list for unrecognised reference kinds.
    /// </summary>
    public static List<(string Key, string Value)> ReadRawEntries(MetadataReference reference, CancellationToken ct)
    {
        if (reference is CompilationReference compilationReference)
            return ReadCompilation(compilationReference.Compilation, ct);

        if (reference is PortableExecutableReference portable)
            return ReadPortableExecutable(portable, ct);

        return [];
    }

    private static List<(string Key, string Value)> ReadCompilation(Compilation compilation, CancellationToken ct)
    {
        var entries = new List<(string, string)>();
        foreach (var attribute in compilation.Assembly.GetAttributes())
        {
            ct.ThrowIfCancellationRequested();
            if (!IsAssemblyMetadataAttribute(attribute) || attribute.ConstructorArguments.Length != 2)
                continue;
            if (attribute.ConstructorArguments[0].Value is string key &&
                attribute.ConstructorArguments[1].Value is string value)
                entries.Add((key, value));
        }

        return entries;
    }

    private static List<(string Key, string Value)> ReadPortableExecutable(PortableExecutableReference portable, CancellationToken ct)
    {
        var entries = new List<(string, string)>();
        try
        {
            if (portable.GetMetadata() is not AssemblyMetadata metadata)
                return entries;

            foreach (var module in metadata.GetModules())
            {
                ct.ThrowIfCancellationRequested();
                var reader = module.GetMetadataReader();
                if (!reader.IsAssembly)
                    continue;

                var assemblyDefinition = reader.GetAssemblyDefinition();
                foreach (var attributeHandle in assemblyDefinition.GetCustomAttributes())
                {
                    ct.ThrowIfCancellationRequested();
                    var attribute = reader.GetCustomAttribute(attributeHandle);
                    if (TryReadAssemblyMetadata(reader, attribute, out var key, out var value))
                        entries.Add((key, value));
                }
            }
        }
        catch (BadImageFormatException) { }
        catch (IOException) { }

        return entries;
    }

    private static bool IsAssemblyMetadataAttribute(AttributeData attribute) =>
        attribute.AttributeClass is
        {
            Name: "AssemblyMetadataAttribute",
            ContainingNamespace:
            {
                Name: "Reflection",
                ContainingNamespace:
                {
                    Name: "System",
                    ContainingNamespace.IsGlobalNamespace: true
                }
            }
        };

    private static bool TryReadAssemblyMetadata(
        MetadataReader reader,
        CustomAttribute attribute,
        out string key,
        out string value)
    {
        key = string.Empty;
        value = string.Empty;

        if (!IsAssemblyMetadataAttribute(reader, attribute.Constructor))
            return false;

        var blob = reader.GetBlobReader(attribute.Value);
        if (blob.RemainingBytes < 2 || blob.ReadUInt16() != 1)
            return false;

        var metadataKey = blob.ReadSerializedString();
        var metadataValue = blob.ReadSerializedString();
        if (metadataKey is null || metadataValue is null)
            return false;

        key = metadataKey;
        value = metadataValue;
        return true;
    }

    private static bool IsAssemblyMetadataAttribute(MetadataReader reader, EntityHandle constructor)
    {
        EntityHandle typeHandle;
        switch (constructor.Kind)
        {
            case HandleKind.MemberReference:
                typeHandle = reader.GetMemberReference((MemberReferenceHandle)constructor).Parent;
                break;
            case HandleKind.MethodDefinition:
                typeHandle = reader.GetMethodDefinition((MethodDefinitionHandle)constructor).GetDeclaringType();
                break;
            default:
                return false;
        }

        return IsType(reader, typeHandle, "System.Reflection", "AssemblyMetadataAttribute");
    }

    private static bool IsType(MetadataReader reader, EntityHandle typeHandle, string ns, string name)
    {
        switch (typeHandle.Kind)
        {
            case HandleKind.TypeDefinition:
                var typeDefinition = reader.GetTypeDefinition((TypeDefinitionHandle)typeHandle);
                return reader.GetString(typeDefinition.Namespace) == ns &&
                       reader.GetString(typeDefinition.Name) == name;
            case HandleKind.TypeReference:
                var typeReference = reader.GetTypeReference((TypeReferenceHandle)typeHandle);
                return reader.GetString(typeReference.Namespace) == ns &&
                       reader.GetString(typeReference.Name) == name;
            default:
                return false;
        }
    }
}
