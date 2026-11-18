namespace Elarion.Typst;

/// <summary>
/// A file a compilation writes to <c>/assets/&lt;name&gt;</c>: a logo, a photo, a signature, an attachment.
/// </summary>
/// <remarks>
/// <para>
/// The name is a plain file name of ASCII letters, digits, <c>.</c>, <c>-</c>, and <c>_</c>: no directories, no
/// leading dot, no <c>..</c>. It can never escape the workspace, and it is safe to splice into the generated main
/// file. Typst picks an image's format from its bytes, so the extension is a label, not a contract.
/// </para>
/// <para>
/// Typst aborts the whole document on a picture it cannot decode. For user-uploaded images, create the asset through
/// <see cref="TypstImage.TryCreateAsset"/>, which hands over only complete pictures in a format Typst reads, so the
/// template can print a placeholder instead of the document failing.
/// </para>
/// </remarks>
public sealed record TypstAsset {
    /// <summary>Creates an asset.</summary>
    /// <param name="name">The file name below <c>/assets/</c>, for example <c>logo.png</c>.</param>
    /// <param name="content">The file's bytes.</param>
    /// <exception cref="ArgumentException"><paramref name="name"/> is not a safe file name.</exception>
    public TypstAsset(string name, ReadOnlyMemory<byte> content) {
        if (!IsSafeName(name))
            throw new ArgumentException(
                $"'{name}' is not a valid Typst asset name: use a plain file name of ASCII letters, digits, '.', '-' and '_'.",
                nameof(name));
        Name = name;
        Content = content;
    }

    /// <summary>The file name below <c>/assets/</c>.</summary>
    public string Name { get; }

    /// <summary>The file's bytes.</summary>
    public ReadOnlyMemory<byte> Content { get; }

    internal static bool IsSafeName(string? name) {
        if (string.IsNullOrEmpty(name) || name.Length > 255 || name[0] == '.' || name.Contains("..", StringComparison.Ordinal))
            return false;

        foreach (var ch in name) {
            if (!char.IsAsciiLetterOrDigit(ch) && ch is not ('.' or '-' or '_'))
                return false;
        }

        return true;
    }
}
