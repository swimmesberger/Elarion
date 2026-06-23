using Elarion.Generators;
using Microsoft.CodeAnalysis;

namespace Elarion.EntityFrameworkCore.Generators;

/// <summary>
/// Per-assembly metadata for cross-assembly <c>[DbEntity]</c> discovery, mirroring the
/// <c>ElarionManifest</c> pattern: a project advertises its entities/configurations as
/// <c>[assembly: AssemblyMetadata(key, value)]</c> attributes, and a DbContext in another assembly reads
/// them straight from reference metadata (via <see cref="MetadataReferencesProvider"/>) instead of walking
/// the referenced assembly's symbol tree on every edit. Reading is cached per reference, so an edit that
/// does not change a reference re-reads nothing.
/// </summary>
internal static class DbEntityManifest
{
    public const string EntityKey = "Elarion.Manifest.DbEntity.v1";
    public const string ConfigKey = "Elarion.Manifest.DbEntityConfig.v1";

    /// <summary>Encodes an entity as length-prefixed fields: name, full name, namespace, then each scope.</summary>
    public static string EncodeEntity(string name, string fullName, string ns, IReadOnlyList<string> scopes)
    {
        var fields = new string?[3 + scopes.Count];
        fields[0] = name;
        fields[1] = fullName;
        fields[2] = ns;
        for (var i = 0; i < scopes.Count; i++)
        {
            fields[3 + i] = scopes[i];
        }

        return EncodeFields(fields);
    }

    public static string EncodeConfig(string fullName, string ns, string entityTypeFqn) =>
        EncodeFields(fullName, ns, entityTypeFqn);

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
