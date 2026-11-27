using System.Globalization;
using Elarion.Abstractions;
using Elarion.Contacts.Reading;
using Elarion.ContentLines;

namespace Elarion.Contacts;

/// <summary>
/// Writes contacts as a vCard file, forward-only to a stream (ADR-0110, decision 8): vCard 3.0 by default and 4.0 on
/// request, UTF-8 without a byte-order mark, CRLF line ends, lines folded at 75 octets without splitting a character.
/// </summary>
/// <remarks>
/// <para>
/// Each contact is written to the destination as it is added, so the writer holds no more than one contact (FR-062).
/// The same contacts give byte-identical files (FR-064). A contact the writer cannot represent fails with a
/// <see cref="ContactWriteException"/> before any of it is written, and the writer takes further contacts. The
/// destination stays open and need not be seekable. An instance is not thread-safe.
/// </para>
/// <para>
/// Outlook classic imports only the first card of a file: for it, write each contact to a file of its own.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// await using (var writer = ContactWriter.Create(stream)) {
///     foreach (var contact in contacts) await writer.AddAsync(contact, ct);
/// }
/// </code>
/// </example>
public sealed class ContactWriter : IAsyncDisposable {
    /// <summary>The content type of a vCard file; files are served as <c>text/vcard; charset=utf-8</c>.</summary>
    public const string ContentType = "text/vcard";

    private const string FileContentType = ContentType + "; charset=utf-8";

    private readonly ContentLineWriter _lines;
    private readonly ContactFormatVersion _version;
    private int _count;
    private bool _disposed;

    private ContactWriter(Stream destination, ContactWriteOptions options) {
        _lines = new ContentLineWriter(destination);
        _version = options.Version;
    }

    /// <summary>Starts a writer over <paramref name="destination"/>; nothing is written until a contact is added.</summary>
    /// <param name="destination">A writable stream; it stays open, and need not be seekable.</param>
    /// <param name="options">The write options; <see langword="null"/> uses <see cref="ContactWriteOptions.Default"/>.</param>
    public static ContactWriter Create(Stream destination, ContactWriteOptions? options = null) {
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite) throw new ArgumentException("The destination stream is not writable.", nameof(destination));
        return new ContactWriter(destination, ValidateOptions(options));
    }

    /// <summary>Writes <paramref name="contacts"/> to <paramref name="destination"/>.</summary>
    /// <param name="destination">A writable stream; it stays open.</param>
    /// <param name="contacts">The contacts, one card each.</param>
    /// <param name="options">The write options.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    public static async ValueTask WriteAsync(
        Stream destination, IEnumerable<Contact> contacts, ContactWriteOptions? options = null, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(contacts);
        var writer = Create(destination, options);
        await using (writer.ConfigureAwait(false)) {
            foreach (var contact in contacts) await writer.AddAsync(contact, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Writes <paramref name="contacts"/> to <paramref name="destination"/> as they are produced.</summary>
    /// <param name="destination">A writable stream; it stays open.</param>
    /// <param name="contacts">The contacts, one card each, produced one at a time.</param>
    /// <param name="options">The write options.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    public static async ValueTask WriteAsync(
        Stream destination, IAsyncEnumerable<Contact> contacts, ContactWriteOptions? options = null, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(contacts);
        var writer = Create(destination, options);
        await using (writer.ConfigureAwait(false)) {
            await foreach (var contact in contacts.WithCancellation(cancellationToken).ConfigureAwait(false))
                await writer.AddAsync(contact, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Writes <paramref name="contacts"/> into memory and returns them as a <c>text/vcard; charset=utf-8</c> file, for a
    /// handler that returns a small export to any transport.
    /// </summary>
    /// <param name="contacts">The contacts.</param>
    /// <param name="fileName">The name the caller saves the file with, such as <c>contacts.vcf</c>.</param>
    /// <param name="options">The write options.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <remarks>
    /// The whole file is held in memory, as an <see cref="ElarionFile"/> always is; for a large export return
    /// <see cref="ToStreamedFile(IAsyncEnumerable{Contact}, string, ContactWriteOptions?)"/> from an HTTP endpoint instead.
    /// </remarks>
    /// <example>
    /// <code>
    /// return await ContactWriter.ToFileAsync(contacts, "contacts.vcf", cancellationToken: ct);
    /// </code>
    /// </example>
    public static async ValueTask<ElarionFile> ToFileAsync(
        IEnumerable<Contact> contacts, string fileName, ContactWriteOptions? options = null, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(contacts);
        ArgumentException.ThrowIfNullOrEmpty(fileName);

        using var stream = new MemoryStream();
        await WriteAsync(stream, contacts, options, cancellationToken).ConfigureAwait(false);
        return ToFile(stream, fileName);
    }

    /// <summary>
    /// Writes <paramref name="contacts"/> into memory and returns them as a <c>text/vcard; charset=utf-8</c> file.
    /// </summary>
    /// <param name="contacts">The contacts, produced one at a time.</param>
    /// <param name="fileName">The name the caller saves the file with, such as <c>contacts.vcf</c>.</param>
    /// <param name="options">The write options.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    public static async ValueTask<ElarionFile> ToFileAsync(
        IAsyncEnumerable<Contact> contacts, string fileName, ContactWriteOptions? options = null, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(contacts);
        ArgumentException.ThrowIfNullOrEmpty(fileName);

        using var stream = new MemoryStream();
        await WriteAsync(stream, contacts, options, cancellationToken).ConfigureAwait(false);
        return ToFile(stream, fileName);
    }

    /// <summary>
    /// A streamed <c>text/vcard; charset=utf-8</c> file of <paramref name="contacts"/>, written to the response body as
    /// they come, for an HTTP endpoint that exports more than fits a buffered <see cref="ElarionFile"/>.
    /// </summary>
    /// <param name="contacts">
    /// The contacts, enumerated once when the response is written: after the handler's pipeline and transaction, so they
    /// must not depend on a scope that has ended by then.
    /// </param>
    /// <param name="fileName">The name the client saves the file with, such as <c>contacts.vcf</c>.</param>
    /// <param name="options">The write options.</param>
    /// <example>
    /// <code>
    /// return ContactWriter.ToStreamedFile(db.People.AsNoTracking().Select(…).AsAsyncEnumerable(), "contacts.vcf");
    /// </code>
    /// </example>
    public static ElarionStreamedFile ToStreamedFile(IAsyncEnumerable<Contact> contacts, string fileName, ContactWriteOptions? options = null) {
        ArgumentNullException.ThrowIfNull(contacts);
        ArgumentException.ThrowIfNullOrEmpty(fileName);
        options = ValidateOptions(options);

        return ElarionStreamedFile.FromWriter(
            (body, cancellationToken) => WriteAsync(body, contacts, options, cancellationToken).AsTask(),
            FileContentType) with { FileName = fileName };
    }

    /// <summary>
    /// A streamed <c>text/vcard; charset=utf-8</c> file whose contacts <paramref name="write"/> adds, written to the
    /// response body as they come.
    /// </summary>
    /// <param name="fileName">The name the client saves the file with, such as <c>contacts.vcf</c>.</param>
    /// <param name="write">
    /// Adds the contacts or cards; it runs when the response is written, after the handler's pipeline and transaction.
    /// The writer is completed after it returns; do not dispose it.
    /// </param>
    /// <param name="options">The write options.</param>
    /// <example>
    /// <code>
    /// return ContactWriter.ToStreamedFile("contacts.vcf", async (writer, ct) => {
    ///     await foreach (var person in people.WithCancellation(ct)) await writer.AddAsync(ToContact(person), ct);
    /// });
    /// </code>
    /// </example>
    public static ElarionStreamedFile ToStreamedFile(
        string fileName, Func<ContactWriter, CancellationToken, ValueTask> write, ContactWriteOptions? options = null) {
        ArgumentException.ThrowIfNullOrEmpty(fileName);
        ArgumentNullException.ThrowIfNull(write);
        options = ValidateOptions(options);

        return ElarionStreamedFile.FromWriter(
            async (body, cancellationToken) => {
                var writer = new ContactWriter(body, options);
                await using (writer.ConfigureAwait(false)) {
                    await write(writer, cancellationToken).ConfigureAwait(false);
                }
            },
            FileContentType) with { FileName = fileName };
    }

    /// <summary>Writes <paramref name="contact"/> as one card, in the version of the write options.</summary>
    /// <param name="contact">The contact.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <exception cref="ContactWriteException">The contact cannot be represented; nothing of it was written.</exception>
    public ValueTask AddAsync(Contact contact, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(contact);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var index = _count + 1;
        IReadOnlyList<ContactProperty> properties;
        try {
            properties = contact.ToProperties(_version);
        }
        catch (ContactWriteException exception) {
            throw new ContactWriteException($"Contact {index.ToString(CultureInfo.InvariantCulture)}: {exception.Message}", index, exception.Property);
        }

        return WriteCardAsync(properties, index, allowVersion: false, cancellationToken);
    }

    /// <summary>
    /// Writes a property list as one card, in the order it holds (FR-067): an entry's <see cref="ContactEntry.Properties"/>
    /// writes the card as it was read. A <c>VERSION</c> property is replaced by the writer's version; <c>CHARSET</c> and
    /// a quoted-printable <c>ENCODING</c> are left out, because the values are decoded and written as UTF-8.
    /// </summary>
    /// <param name="card">The properties, without <c>BEGIN</c> and <c>END</c>.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <exception cref="ContactWriteException">A property cannot be represented; nothing of the card was written.</exception>
    public ValueTask AddAsync(IReadOnlyList<ContactProperty> card, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(card);
        ObjectDisposedException.ThrowIf(_disposed, this);
        return WriteCardAsync(card, _count + 1, allowVersion: true, cancellationToken);
    }

    /// <summary>Writes what is still buffered and releases the writer's buffers; the destination stays open.</summary>
    public async ValueTask DisposeAsync() {
        if (_disposed) return;
        _disposed = true;
        try {
            await _lines.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally {
            _lines.Dispose();
        }
    }

    private async ValueTask WriteCardAsync(IReadOnlyList<ContactProperty> properties, int index, bool allowVersion, CancellationToken cancellationToken) {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var property in properties) Validate(property, index, allowVersion);

        _lines.WriteLine("BEGIN:VCARD");
        _lines.WriteLine(_version == ContactFormatVersion.V4_0 ? "VERSION:4.0" : "VERSION:3.0");
        foreach (var property in properties) {
            if (string.Equals(property.Name, "VERSION", StringComparison.OrdinalIgnoreCase)) continue;
            var written = ContactMapper.Normalize(property);
            _lines.WriteProperty(written.Group, written.Name, written.Parameters.Select(static p => (p.Name, p.Values)), written.Value);
        }

        _lines.WriteLine("END:VCARD");
        _count = index;
        await _lines.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void Validate(ContactProperty property, int index, bool allowVersion) {
        if (property is null) throw new ContactWriteException($"Contact {Index(index)}: a property is null.", index, null);
        var name = property.Name;
        if (!ContentLineParser.IsValidName(name ?? ""))
            throw Invalid(index, name, $"the property name '{name}' is not a valid vCard name");
        if (name!.Equals("BEGIN", StringComparison.OrdinalIgnoreCase) || name.Equals("END", StringComparison.OrdinalIgnoreCase)
            || (!allowVersion && name.Equals("VERSION", StringComparison.OrdinalIgnoreCase)))
            throw Invalid(index, name, $"the property {name} is written by the writer itself");
        if (property.Group is { } group && !ContentLineParser.IsValidName(group))
            throw Invalid(index, name, $"the group '{group}' of {name} is not a valid vCard name");
        foreach (var parameter in property.Parameters) {
            if (!ContentLineParser.IsValidName(parameter.Name ?? ""))
                throw Invalid(index, name, $"the parameter name '{parameter.Name}' of {name} is not a valid vCard name");
        }

        if (property.Value is null || property.Value.AsSpan().IndexOfAny('\r', '\n') >= 0)
            throw Invalid(index, name, $"the value of {name} holds a line break; escape it as \\n");
    }

    private static ContactWriteException Invalid(int index, string? property, string reason) =>
        new($"Contact {Index(index)}: {reason}.", index, property);

    private static string Index(int index) => index.ToString(CultureInfo.InvariantCulture);

    private static ContactWriteOptions ValidateOptions(ContactWriteOptions? options) {
        options ??= ContactWriteOptions.Default;
        if (!Enum.IsDefined(options.Version))
            throw new ArgumentOutOfRangeException(nameof(options), options.Version, "The vCard version to write is not known.");
        return options;
    }

    // The buffer itself, not a copy: the stream is not used again.
    private static ElarionFile ToFile(MemoryStream stream, string fileName) =>
        new(stream.GetBuffer().AsMemory(0, (int)stream.Length), FileContentType) { FileName = fileName };
}
