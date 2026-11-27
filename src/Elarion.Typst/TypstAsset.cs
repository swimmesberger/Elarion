using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

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
    /// <exception cref="ArgumentException"><paramref name="name"/> is not a valid asset name (<see cref="IsValidName"/>).</exception>
    public TypstAsset(string name, ReadOnlyMemory<byte> content) {
        if (!IsValidName(name)) throw InvalidName(name);
        Name = name;
        Content = content;
    }

    /// <summary>The file name below <c>/assets/</c>.</summary>
    public string Name { get; }

    /// <summary>The file's bytes.</summary>
    public ReadOnlyMemory<byte> Content { get; }

    /// <summary>
    /// Creates an asset, or returns <see langword="false"/> when <paramref name="name"/> is not a valid asset name, so a
    /// name from user input can be rejected with the application's own error instead of an exception.
    /// </summary>
    /// <example>
    /// <code>
    /// if (!TypstAsset.TryCreate(upload.FileName, upload.Content, out var attachment))
    ///     return AppError.Validation("The file name cannot be used in a document.", code: "document.attachment-name");
    /// </code>
    /// </example>
    public static bool TryCreate(string? name, ReadOnlyMemory<byte> content, [NotNullWhen(true)] out TypstAsset? asset) {
        asset = IsValidName(name) ? new TypstAsset(name, content) : null;
        return asset is not null;
    }

    /// <summary>
    /// Serializes <paramref name="value"/> with <paramref name="typeInfo"/> into a UTF-8 JSON asset: a second model
    /// beside the request's <see cref="TypstCompileRequest.Data"/>, such as a letterhead every document receives. The
    /// template parses it with <c>json(assets.at("letterhead.json"))</c>.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="name"/> is not a valid asset name (<see cref="IsValidName"/>).</exception>
    /// <example>
    /// <code>
    /// Assets = [TypstAsset.FromJson("letterhead.json", letterhead, DocumentJsonContext.Default.Letterhead)]
    /// </code>
    /// </example>
    public static TypstAsset FromJson<T>(string name, T value, JsonTypeInfo<T> typeInfo) {
        ArgumentNullException.ThrowIfNull(typeInfo);
        // Checked before serializing, so a bad name does not cost a serialization.
        if (!IsValidName(name)) throw InvalidName(name);
        return new TypstAsset(name, JsonSerializer.SerializeToUtf8Bytes(value, typeInfo));
    }

    private static ArgumentException InvalidName(string? name) =>
        new($"'{name}' is not a valid Typst asset name: use a plain file name of ASCII letters, digits, '.', '-' and '_'.", nameof(name));

    /// <summary>
    /// Whether <paramref name="name"/> is a valid asset name: 1 to 255 ASCII letters, digits, <c>.</c>, <c>-</c>, and
    /// <c>_</c>, not starting with a dot and without <c>..</c>. The constructor throws for any other name.
    /// </summary>
    public static bool IsValidName([NotNullWhen(true)] string? name) {
        if (string.IsNullOrEmpty(name) || name.Length > 255 || name[0] == '.' || name.Contains("..", StringComparison.Ordinal))
            return false;

        foreach (var ch in name) {
            if (!char.IsAsciiLetterOrDigit(ch) && ch is not ('.' or '-' or '_'))
                return false;
        }

        return true;
    }
}
