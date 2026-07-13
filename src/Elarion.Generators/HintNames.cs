using System.Text;

namespace Elarion.Generators;

/// <summary>
/// Shared sanitization for AddSource hint names derived from a type's fully-qualified name.
/// </summary>
internal static class HintNames
{
    /// <summary>
    /// Maps a fully-qualified type name to a valid, collision-free hint name. Every non-identifier character
    /// becomes <c>_</c> (spaces are dropped), and when the input contains any character whose mapping is
    /// ambiguous with the <c>.</c> → <c>_</c> replacement (an original <c>_</c>, generic brackets, …) a short
    /// stable FNV-1a hash of the unsanitized name is appended — so <c>A.B_C.FooHandler</c> and
    /// <c>A.B.C.FooHandler</c> can never collide on one hint (a duplicate AddSource hint fails the whole
    /// generator). Plain dotted names — the overwhelmingly common case — keep their historical shape, the
    /// same pattern <c>AppModuleDiscoveryGenerator.ModuleMethodNamePart</c> uses for method names.
    /// </summary>
    public static string Sanitize(string fqn)
    {
        var value = fqn.StartsWith("global::", StringComparison.Ordinal) ? fqn.Substring(8) : fqn;
        var sb = new StringBuilder(value.Length);
        var ambiguous = false;
        foreach (var ch in value)
        {
            if (ch == '.')
            {
                sb.Append('_');
            }
            else if (ch == ' ')
            {
                ambiguous = true;
            }
            else if (ch != '_' && char.IsLetterOrDigit(ch))
            {
                sb.Append(ch);
            }
            else
            {
                sb.Append('_');
                ambiguous = true;
            }
        }

        if (!ambiguous)
            return sb.ToString();

        return $"{sb}_{StableHash(value)}";
    }

    private static string StableHash(string value)
    {
        var hash = 2166136261u;
        foreach (var ch in value)
        {
            hash ^= ch;
            hash *= 16777619u;
        }

        return hash.ToString("X8");
    }
}
