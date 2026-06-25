using Elarion.Generators;
using Microsoft.CodeAnalysis;

namespace Elarion.EntityFrameworkCore.Generators;

/// <summary>
/// Per-assembly metadata for cross-assembly <c>[EntityConfiguration]</c> discovery, mirroring the
/// <c>ElarionManifest</c> pattern: a project advertises each configuration (its configured entities and
/// scopes) as an <c>[assembly: AssemblyMetadata(key, value)]</c> attribute, and a DbContext in another
/// assembly reads them straight from reference metadata (via <see cref="MetadataReferencesProvider"/>)
/// instead of walking the referenced assembly's symbol tree on every edit. Reading is cached per
/// reference, so an edit that does not change a reference re-reads nothing. The entity set is derived
/// from the configurations — there is no separate entity entry.
/// </summary>
internal static class EntityConfigurationManifest
{
    public const string ConfigKey = "Elarion.Manifest.EntityConfiguration.v1";

    /// <summary>
    /// Encodes a configuration as length-prefixed fields: full name, namespace, scope count, each scope,
    /// entity count, then a (name, full name, namespace) triple per configured entity.
    /// </summary>
    public static string EncodeConfig(
        string fullName,
        string ns,
        IReadOnlyList<string> scopes,
        IReadOnlyList<(string Name, string FullName, string Namespace)> entities)
    {
        var fields = new string?[3 + scopes.Count + 1 + (entities.Count * 3)];
        var index = 0;
        fields[index++] = fullName;
        fields[index++] = ns;
        fields[index++] = scopes.Count.ToString();
        foreach (var scope in scopes)
        {
            fields[index++] = scope;
        }

        fields[index++] = entities.Count.ToString();
        foreach (var entity in entities)
        {
            fields[index++] = entity.Name;
            fields[index++] = entity.FullName;
            fields[index++] = entity.Namespace;
        }

        return EncodeFields(fields);
    }

    /// <summary>
    /// Decodes a configuration entry produced by <see cref="EncodeConfig"/>. Returns <c>false</c> for any
    /// malformed payload so a corrupt or version-mismatched manifest entry is skipped rather than crashing
    /// the generator.
    /// </summary>
    public static bool TryDecodeConfig(
        string value,
        out string fullName,
        out string ns,
        out IReadOnlyList<string> scopes,
        out IReadOnlyList<(string Name, string FullName, string Namespace)> entities)
    {
        fullName = string.Empty;
        ns = string.Empty;
        scopes = [];
        entities = [];

        if (!TryDecodeFields(value, out var fields) || fields.Count < 4)
        {
            return false;
        }

        if (fields[0] is not { } decodedFullName ||
            fields[1] is not { } decodedNamespace ||
            fields[2] is not { } scopeCountText ||
            !int.TryParse(scopeCountText, out var scopeCount) ||
            scopeCount < 0)
        {
            return false;
        }

        var index = 3;
        var decodedScopes = new List<string>(scopeCount);
        for (var i = 0; i < scopeCount; i++)
        {
            if (index >= fields.Count || fields[index] is not { } scope)
            {
                return false;
            }

            decodedScopes.Add(scope);
            index++;
        }

        if (index >= fields.Count ||
            fields[index] is not { } entityCountText ||
            !int.TryParse(entityCountText, out var entityCount) ||
            entityCount < 0)
        {
            return false;
        }

        index++;
        var decodedEntities = new List<(string, string, string)>(entityCount);
        for (var i = 0; i < entityCount; i++)
        {
            if (index + 2 >= fields.Count ||
                fields[index] is not { } name ||
                fields[index + 1] is not { } entityFullName ||
                fields[index + 2] is not { } entityNamespace)
            {
                return false;
            }

            decodedEntities.Add((name, entityFullName, entityNamespace));
            index += 3;
        }

        fullName = decodedFullName;
        ns = decodedNamespace;
        scopes = decodedScopes;
        entities = decodedEntities;
        return true;
    }

    /// <summary>Reads every <c>[assembly: AssemblyMetadata]</c> key/value pair from a reference, no symbols required.</summary>
    public static List<(string Key, string Value)> ReadEntries(MetadataReference reference, CancellationToken ct) =>
        AssemblyMetadataReader.ReadRawEntries(reference, ct);

    public static string EncodeFields(params string?[] fields)
    {
        var result = new System.Text.StringBuilder();
        foreach (var field in fields)
        {
            if (field is null)
            {
                result.Append("-1:");
                continue;
            }

            result.Append(field.Length);
            result.Append(':');
            result.Append(field);
        }

        return result.ToString();
    }

    public static bool TryDecodeFields(string value, out IReadOnlyList<string?> fields)
    {
        var result = new List<string?>();
        var index = 0;
        while (index < value.Length)
        {
            var lengthStart = index;
            if (value[index] == '-')
            {
                index++;
            }

            while (index < value.Length && char.IsDigit(value[index]))
            {
                index++;
            }

            if (index == lengthStart || index >= value.Length || value[index] != ':')
            {
                fields = [];
                return false;
            }

            if (!int.TryParse(value.Substring(lengthStart, index - lengthStart), out var length))
            {
                fields = [];
                return false;
            }

            index++;
            if (length == -1)
            {
                result.Add(null);
                continue;
            }

            if (length < 0 || index + length > value.Length)
            {
                fields = [];
                return false;
            }

            result.Add(value.Substring(index, length));
            index += length;
        }

        fields = result;
        return true;
    }
}
