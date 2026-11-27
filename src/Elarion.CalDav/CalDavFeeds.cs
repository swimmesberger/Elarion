using Elarion.AppPasswords;
using Elarion.Calendars;
using Elarion.WebDav;
using Microsoft.Extensions.Options;

namespace Elarion.CalDav;

/// <summary>Feed tokens for published calendars (ADR-0111, spec 0006 FR-026).</summary>
public static class CalDavAppPasswordScopeExtensions {
    extension(AppPasswordScope) {
        /// <summary>The scope of a feed token bound to the calendar at <paramref name="address"/>.</summary>
        /// <example>
        /// <code>
        /// var feed = await appPasswords.CreateAsync(user.UserId, "Outlook", AppPasswordScope.CalendarFeed(address), ct);
        /// </code>
        /// </example>
        public static AppPasswordScope CalendarFeed(CalDavAddress address) {
            ArgumentNullException.ThrowIfNull(address);
            return AppPasswordScope.CalendarFeed(address.Source, address.Key);
        }
    }
}

/// <summary>
/// The URLs of feeds: <c>{prefix}/feeds/{token}.ics</c>, which Outlook and Google Calendar subscribe to without a password
/// (spec 0006 FR-026). Registered by <c>AddElarionCalDav</c>.
/// </summary>
/// <example>
/// <code>
/// var feed = await appPasswords.CreateAsync(user.UserId, "Outlook", AppPasswordScope.CalendarFeed(address), ct);
/// Uri url = links.FeedUrl(new Uri("https://app.example.com"), feed.Value);   // https://app.example.com/dav/feeds/elap_….ics
/// </code>
/// </example>
public sealed class CalDavLinks(IOptions<WebDavOptions> options) {
    /// <summary>The path of the feed of <paramref name="feed"/>, such as <c>/dav/feeds/elap_….ics</c>.</summary>
    /// <exception cref="ArgumentException">The app password is not a feed token.</exception>
    public string FeedPath(AppPasswordIssued feed) {
        ArgumentNullException.ThrowIfNull(feed);
        if (feed.Password.Scope.Kind != AppPasswordScopeKind.CalendarFeed)
            throw new ArgumentException("The app password is not a feed token; create it with AppPasswordScope.CalendarFeed(address).", nameof(feed));
        return options.Value.Prefix + "/feeds/" + feed.Secret + ".ics";
    }

    /// <summary>The absolute URL of the feed of <paramref name="feed"/> on the application at <paramref name="origin"/>.</summary>
    /// <param name="origin">The application's public origin and path base, such as <c>https://app.example.com</c>.</param>
    /// <param name="feed">The feed token, as <c>IAppPasswords.CreateAsync</c> returned it.</param>
    public Uri FeedUrl(Uri origin, AppPasswordIssued feed) {
        ArgumentNullException.ThrowIfNull(origin);
        return new Uri(origin.GetLeftPart(UriPartial.Path).TrimEnd('/') + FeedPath(feed));
    }
}

internal sealed partial class CalDavProtocol {
    // A feed: {prefix}/feeds/{token}.ics. The app-password scheme authenticated the token and put its scope in a claim; the
    // feed serves that calendar when its user still sees it through its source, else 404 (FR-026).
    private async ValueTask<DavResponse?> FeedAsync(DavContext context, IReadOnlyList<string> segments, CancellationToken cancellationToken) {
        if (segments.Count != 2 || segments[0] != "feeds" || !segments[1].EndsWith(".ics", StringComparison.OrdinalIgnoreCase)) return null;
        var settings = options.Value;
        if (!settings.FeedsEnabled) return DavResponse.Status(404);
        if (context.Request.Method.ToUpperInvariant() is not ("GET" or "HEAD")) {
            var refused = DavResponse.Status(405);
            refused.Headers["Allow"] = "GET, HEAD";
            return refused;
        }

        var scope = context.User.GetClaimValues(AppPasswordClaimTypes.Scope)
            .Select(static value => AppPasswordScope.TryParse(value, out var parsed) ? parsed : null)
            .FirstOrDefault(static parsed => parsed?.Kind == AppPasswordScopeKind.CalendarFeed);
        if (scope is null) return DavResponse.Status(404);

        var collections = await context.GetCollectionsAsync(this, cancellationToken).ConfigureAwait(false);
        var calendar = collections.OfType<CalDavCollection>()
            .FirstOrDefault(c => c.CalendarAddress.Source == scope.CalendarSource && c.CalendarAddress.Key == scope.CalendarKey);
        if (calendar is null) return DavResponse.Status(404);

        var state = (await context.Store.GetStatesAsync([calendar.Address], cancellationToken).ConfigureAwait(false))[0];
        // The feed's bytes follow from the collection's content, which the counter versions, and from what the source says
        // about the calendar; a subscriber that sends the ETag back gets 304 until either changes.
        var etag = DavETag.Compute(System.Text.Encoding.UTF8.GetBytes(state.SyncToken + "\n" + calendar.Info));
        if (DavETag.Matches(context.Request.Header("If-None-Match"), etag)) {
            var notModified = DavResponse.Status(304);
            notModified.Headers["ETag"] = etag;
            return notModified;
        }

        var header = new CalendarHeader {
            Name = calendar.Info.DisplayName,
            Description = calendar.Info.Description,
            Color = calendar.Info.Color,
            SuggestedTimeZone = calendar.Info.TimeZone,
        };
        var store = context.Store;
        var response = new DavResponse(200) {
            ContentType = CalDavObjects.ContentType,
            Body = async (stream, ct) => {
                var writer = CalendarWriter.Create(stream, header, new CalendarWriteOptions { ProductId = settings.ProductId });
                await using (writer.ConfigureAwait(false)) {
                    await foreach (var stored in store.GetCandidatesAsync(calendar.Address.Id, null, null, ct).ConfigureAwait(false)) {
                        using var input = new MemoryStream(stored.Content.ToArray(), false);
                        var read = await reader.ReadAsync(input, settings.CalendarRead, ct).ConfigureAwait(false);
                        if (read.Root is null) continue;
                        // Time zones first, so each VTIMEZONE is written once, as the object stored it (FR-027).
                        foreach (var component in read.Root.Components.Where(static c => c.Name == "VTIMEZONE"))
                            await writer.AddAsync(component, ct).ConfigureAwait(false);
                        foreach (var component in read.Root.Components.Where(static c => c.Name != "VTIMEZONE"))
                            await writer.AddAsync(component, ct).ConfigureAwait(false);
                    }
                }
            },
        };
        response.Headers["ETag"] = etag;
        response.Headers["Cache-Control"] = "private, no-cache";
        return response;
    }
}
