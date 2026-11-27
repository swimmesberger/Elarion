using System.Runtime.CompilerServices;
using Elarion.Contacts;
using Elarion.WebDav;

namespace Elarion.CardDav;

/// <summary>
/// Publishes contacts into the published-collection store (ADR-0113, spec 0007 FR-005–FR-010), in the caller's unit of
/// work: a published contact is visible to contacts apps exactly when the handler's transaction commits.
/// </summary>
/// <remarks>
/// A contact is stored as vCard 3.0 written by the <c>Elarion.Contacts</c> writer; its strong ETag is a hash of the bytes.
/// The writer writes no clock value, so equal contacts write equal bytes, and publishing equal bytes again changes
/// nothing, not even the sync token. Publishing into a source that is not registered, a contact without a
/// <see cref="Contact.Uid"/> or without a <see cref="Contact.DisplayName"/> (Apple drops a card without <c>FN</c>), or a
/// contact the writer refuses throws: it is the application's mistake.
/// </remarks>
/// <example>
/// <code>
/// public sealed class CreateCustomer(AppDbContext db, ICardDavPublisher addressBooks) : IHandler&lt;CreateCustomer.Command, Result&lt;Guid&gt;&gt; {
///     public async ValueTask&lt;Result&lt;Guid&gt;&gt; HandleAsync(Command command, CancellationToken ct) {
///         // ... write the customer
///         await addressBooks.PublishAsync(new CardDavAddress("customers", "all"), ToContact(customer), ct);
///         return customer.Id;
///     }
/// }
/// </code>
/// </example>
public interface ICardDavPublisher {
    /// <summary>Adds or replaces the contact of its UID in the address book at <paramref name="address"/>.</summary>
    /// <returns>Whether the address book changed: <see langword="false"/> when the written bytes equal the stored ones.</returns>
    ValueTask<bool> PublishAsync(CardDavAddress address, Contact contact, CancellationToken cancellationToken = default);

    /// <summary>Removes the contact of <paramref name="uid"/>; <see langword="false"/> when the address book holds none.</summary>
    ValueTask<bool> RemoveAsync(CardDavAddress address, string uid, CancellationToken cancellationToken = default);

    /// <summary>
    /// Makes <paramref name="contacts"/> the address book's whole content: adds what is new, replaces what changed and
    /// removes what the set lacks, recording each change — the repair for an address book that drifted, or a nightly
    /// re-projection.
    /// </summary>
    /// <exception cref="ArgumentException">Two contacts of the set have the same UID.</exception>
    ValueTask<DavReplaceOutcome> ReplaceAsync(CardDavAddress address, IAsyncEnumerable<Contact> contacts,
        CancellationToken cancellationToken = default);

    /// <summary>The synchronous-set overload of <see cref="ReplaceAsync(CardDavAddress, IAsyncEnumerable{Contact}, CancellationToken)"/>.</summary>
    ValueTask<DavReplaceOutcome> ReplaceAsync(CardDavAddress address, IEnumerable<Contact> contacts,
        CancellationToken cancellationToken = default);
}

/// <summary>The default <see cref="ICardDavPublisher"/> over the scoped <see cref="IDavCollectionStore"/>.</summary>
internal sealed class CardDavPublisher(IDavCollectionStore store, DavSourceRegistry<CardDavSourceListing> sources, Microsoft.Extensions.Options.IOptions<CardDavOptions> options)
    : ICardDavPublisher {
    public async ValueTask<bool> PublishAsync(CardDavAddress address, Contact contact, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(contact);
        Guard(address);
        var content = await CardDavObjects.ToContentAsync(contact, options.Value.ContactRead, cancellationToken).ConfigureAwait(false);
        return await store.PublishAsync(address.ToCollectionAddress(), content, cancellationToken).ConfigureAwait(false) != DavPublishOutcome.Unchanged;
    }

    public ValueTask<bool> RemoveAsync(CardDavAddress address, string uid, CancellationToken cancellationToken = default) {
        ArgumentException.ThrowIfNullOrEmpty(uid);
        Guard(address);
        return store.RemoveAsync(address.ToCollectionAddress(), CardDavObjects.NameOf(uid), cancellationToken);
    }

    public ValueTask<DavReplaceOutcome> ReplaceAsync(CardDavAddress address, IAsyncEnumerable<Contact> contacts,
        CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(contacts);
        Guard(address);
        return ReplaceCoreAsync(address, contacts, cancellationToken);
    }

    public ValueTask<DavReplaceOutcome> ReplaceAsync(CardDavAddress address, IEnumerable<Contact> contacts,
        CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(contacts);
        return ReplaceAsync(address, contacts.ToAsyncEnumerable(), cancellationToken);
    }

    // The set is written in full before the store applies anything: a duplicate UID would otherwise fail inside the store,
    // half applied, on its unique index.
    private async ValueTask<DavReplaceOutcome> ReplaceCoreAsync(CardDavAddress address, IAsyncEnumerable<Contact> contacts,
        CancellationToken cancellationToken) {
        var contents = new List<DavObjectContent>();
        var uids = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var contact in contacts.WithCancellation(cancellationToken).ConfigureAwait(false)) {
            ArgumentNullException.ThrowIfNull(contact);
            var content = await CardDavObjects.ToContentAsync(contact, options.Value.ContactRead, cancellationToken).ConfigureAwait(false);
            if (!uids.Add(content.Uid))
                throw new ArgumentException($"Two contacts of the set have the UID '{content.Uid}'; a UID is unique within an address book.", nameof(contacts));
            contents.Add(content);
        }

        return await store.ReplaceAsync(address.ToCollectionAddress(), Enumerate(contents, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    private static async IAsyncEnumerable<DavObjectContent> Enumerate(List<DavObjectContent> contents,
        [EnumeratorCancellation] CancellationToken cancellationToken) {
        foreach (var content in contents) {
            cancellationToken.ThrowIfCancellationRequested();
            yield return content;
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    private void Guard(CardDavAddress address) {
        ArgumentNullException.ThrowIfNull(address);
        if (!sources.Contains(address.Source))
            throw new InvalidOperationException(
                $"No address-book source named '{address.Source}' is registered; call AddElarionCardDavSource<TQuery>() in the module's ConfigureServices.");
    }
}
