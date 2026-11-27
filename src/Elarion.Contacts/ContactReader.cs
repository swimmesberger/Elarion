using System.Globalization;
using System.Text;
using Elarion.Contacts.Reading;
using Elarion.ContentLines;
using Microsoft.Extensions.Options;

namespace Elarion.Contacts;

/// <summary>
/// Reads uploaded vCard files (<c>.vcf</c>) of vCard 2.1, 3.0 and 4.0, and files that mix them, into contacts
/// (ADR-0110). Registered by <c>AddElarionContacts</c> as a singleton; inject it into a handler.
/// </summary>
/// <remarks>
/// <para>
/// A file a user uploaded is input, not infrastructure: whatever the file holds, a read returns a result with every
/// vCard as an entry and every problem as a located, coded error, and throws only on cancellation and on
/// misconfiguration (FR-038).
/// </para>
/// <para>
/// The reader buffers the stream up to <see cref="ContactReadOptions.MaxBytes"/> and refuses a larger one without
/// holding more (FR-046). It never requests an address a file contains (FR-031) and never registers an encoding
/// provider process-wide (FR-016).
/// </para>
/// </remarks>
/// <example>
/// <code>
/// var result = await reader.ReadAsync(request.Contacts.OpenReadStream(), cancellationToken: ct);
/// if (result.FileErrors.Count > 0) return result.ToAppError("contacts");
/// foreach (var contact in result.Contacts)
///     db.People.Add(new Person { Name = contact.DisplayName, Email = contact.Emails.FirstOrDefault()?.Address });
/// </code>
/// </example>
public sealed class ContactReader {
    private readonly ContactReadOptions _defaults;
    private readonly Encoding _defaultFallback;

    /// <summary>Creates a reader; dependency injection supplies the registered options.</summary>
    /// <param name="options">The application's options; <see langword="null"/> uses the defaults.</param>
    /// <exception cref="InvalidOperationException">The default <see cref="ContactReadOptions.FallbackEncoding"/> is not known.</exception>
    public ContactReader(IOptions<ContactOptions>? options = null) {
        _defaults = options?.Value.ReadDefaults ?? new ContactReadOptions();
        _defaultFallback = _defaults.Validate();
    }

    /// <summary>Reads every vCard of <paramref name="source"/>.</summary>
    /// <param name="source">The uploaded file; read to its end or one byte past the limit, and left open.</param>
    /// <param name="options">The read options; <see langword="null"/> uses <see cref="ContactOptions.ReadDefaults"/>.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <exception cref="ArgumentOutOfRangeException">A limit of <paramref name="options"/> is not positive.</exception>
    /// <exception cref="InvalidOperationException">The <see cref="ContactReadOptions.FallbackEncoding"/> of <paramref name="options"/> is not known.</exception>
    public async ValueTask<ContactReadResult> ReadAsync(
        Stream source,
        ContactReadOptions? options = null,
        CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(source);
        var effective = options ?? _defaults;
        var fallback = options is null ? _defaultFallback : options.Validate();

        using var buffer = await ContentBuffer.ReadAsync(source, effective.MaxBytes, cancellationToken).ConfigureAwait(false);
        if (buffer.TooLarge) {
            return new ContactReadResult([], [
                new ContactError(
                    ContactErrorCodes.TooLarge,
                    $"The file is larger than {effective.MaxBytes.ToString(CultureInfo.InvariantCulture)} bytes.",
                    limit: effective.MaxBytes)
            ]);
        }

        return VCardFileReader.Read(buffer.Memory, effective, fallback, cancellationToken);
    }
}
