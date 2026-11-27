using System.Runtime.CompilerServices;
using Elarion.Calendars;
using Elarion.WebDav;
using Microsoft.Extensions.Options;

namespace Elarion.CalDav;

/// <summary>
/// Publishes calendar objects into the published-collection store (ADR-0111, spec 0006 FR-004–FR-009), in the caller's
/// unit of work: a published object is visible to calendar apps exactly when the handler's transaction commits.
/// </summary>
/// <remarks>
/// An object is written with the <c>Elarion.Calendars</c> writer and one <c>VTIMEZONE</c> per <c>TZID</c>; its strong ETag
/// is a hash of the bytes. No clock value is passed to the writer, so an item needs a <c>DTSTAMP</c> or a
/// <c>LAST-MODIFIED</c> (a <see cref="CalendarWriteException"/> otherwise): equal input writes equal bytes, and publishing
/// equal bytes again changes nothing, not even the sync token. Publishing into a source that is not registered, an object
/// with a <c>METHOD</c> property, or components of different UIDs throws: it is the application's mistake.
/// </remarks>
/// <example>
/// <code>
/// public sealed class CreateShift(AppDbContext db, ICalDavPublisher calendars) : IHandler&lt;CreateShift.Command, Result&lt;Guid&gt;&gt; {
///     public async ValueTask&lt;Result&lt;Guid&gt;&gt; HandleAsync(Command command, CancellationToken ct) {
///         // ... write the shift
///         await calendars.PublishAsync(new CalDavAddress("shifts", $"u-{shift.UserId}"), ToEvent(shift), ct);
///         return shift.Id;
///     }
/// }
/// </code>
/// </example>
public interface ICalDavPublisher {
    /// <summary>Adds or replaces the object of its UID in the calendar at <paramref name="address"/>.</summary>
    /// <returns>Whether the calendar changed: <see langword="false"/> when the written bytes equal the stored ones.</returns>
    ValueTask<bool> PublishAsync(CalDavAddress address, CalendarObject calendarObject, CancellationToken cancellationToken = default);

    /// <summary>Removes the object of <paramref name="uid"/>; <see langword="false"/> when the calendar holds none.</summary>
    ValueTask<bool> RemoveAsync(CalDavAddress address, string uid, CancellationToken cancellationToken = default);

    /// <summary>
    /// Makes <paramref name="calendarObjects"/> the calendar's whole content: adds what is new, replaces what changed and
    /// removes what the set lacks, recording each change — the repair for a calendar that drifted, or a nightly
    /// re-projection.
    /// </summary>
    ValueTask<DavReplaceOutcome> ReplaceAsync(CalDavAddress address, IAsyncEnumerable<CalendarObject> calendarObjects,
        CancellationToken cancellationToken = default);

    /// <summary>The synchronous-set overload of <see cref="ReplaceAsync(CalDavAddress, IAsyncEnumerable{CalendarObject}, CancellationToken)"/>.</summary>
    ValueTask<DavReplaceOutcome> ReplaceAsync(CalDavAddress address, IEnumerable<CalendarObject> calendarObjects,
        CancellationToken cancellationToken = default);
}

/// <summary>The default <see cref="ICalDavPublisher"/> over the scoped <see cref="IDavCollectionStore"/>.</summary>
internal sealed class CalDavPublisher(IDavCollectionStore store, CalDavSourceRegistry sources, IOptions<CalDavOptions> options, CalendarReader reader)
    : ICalDavPublisher {
    public async ValueTask<bool> PublishAsync(CalDavAddress address, CalendarObject calendarObject, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(calendarObject);
        Guard(address);
        var content = await ToContentAsync(calendarObject, cancellationToken).ConfigureAwait(false);
        return await store.PublishAsync(address.ToCollectionAddress(), content, cancellationToken).ConfigureAwait(false) != DavPublishOutcome.Unchanged;
    }

    public ValueTask<bool> RemoveAsync(CalDavAddress address, string uid, CancellationToken cancellationToken = default) {
        ArgumentException.ThrowIfNullOrEmpty(uid);
        Guard(address);
        return store.RemoveAsync(address.ToCollectionAddress(), CalDavObjects.NameOf(uid), cancellationToken);
    }

    public ValueTask<DavReplaceOutcome> ReplaceAsync(CalDavAddress address, IAsyncEnumerable<CalendarObject> calendarObjects,
        CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(calendarObjects);
        Guard(address);
        return store.ReplaceAsync(address.ToCollectionAddress(), ToContents(calendarObjects, cancellationToken), cancellationToken);
    }

    public ValueTask<DavReplaceOutcome> ReplaceAsync(CalDavAddress address, IEnumerable<CalendarObject> calendarObjects,
        CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(calendarObjects);
        return ReplaceAsync(address, calendarObjects.ToAsyncEnumerable(), cancellationToken);
    }

    private void Guard(CalDavAddress address) {
        ArgumentNullException.ThrowIfNull(address);
        if (!sources.Contains(address.Source))
            throw new InvalidOperationException(
                $"No calendar source named '{address.Source}' is registered; call AddElarionCalDavSource<TQuery>() in the module's ConfigureServices.");
    }

    private async IAsyncEnumerable<DavObjectContent> ToContents(IAsyncEnumerable<CalendarObject> objects,
        [EnumeratorCancellation] CancellationToken cancellationToken) {
        await foreach (var calendarObject in objects.WithCancellation(cancellationToken).ConfigureAwait(false))
            yield return await ToContentAsync(calendarObject, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<DavObjectContent> ToContentAsync(CalendarObject calendarObject, CancellationToken cancellationToken) {
        var settings = options.Value;
        var bytes = await CalDavObjects.WriteAsync(calendarObject, settings.ProductId, cancellationToken).ConfigureAwait(false);
        using var stream = new MemoryStream(bytes, false);
        var read = await reader.ReadAsync(stream, settings.CalendarRead, cancellationToken).ConfigureAwait(false);
        var (start, end) = CalDavObjects.RangeOf(read, settings.CalendarExpand);
        return new DavObjectContent {
            Name = CalDavObjects.NameOf(calendarObject.Uid),
            Uid = calendarObject.Uid,
            Content = bytes,
            ContentType = CalDavObjects.ContentTypeOf(calendarObject),
            ETag = DavETag.Compute(bytes),
            RangeStart = start,
            RangeEnd = end,
        };
    }
}
