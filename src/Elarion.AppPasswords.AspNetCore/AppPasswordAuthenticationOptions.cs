using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;

namespace Elarion.AppPasswords.AspNetCore;

/// <summary>The options of the app-password authentication scheme.</summary>
public sealed class AppPasswordAuthenticationOptions : AuthenticationSchemeOptions {
    /// <summary>Creates the options with the default events and the default feed-token reader.</summary>
    public AppPasswordAuthenticationOptions() {
        Events = new AppPasswordAuthenticationEvents();
    }

    /// <summary>The events of the scheme; see <see cref="AppPasswordAuthenticationEvents"/>.</summary>
    public new AppPasswordAuthenticationEvents Events {
        get => (AppPasswordAuthenticationEvents)base.Events!;
        set => base.Events = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>
    /// The realm of the <c>WWW-Authenticate: Basic realm="…", charset="UTF-8"</c> challenge. Default
    /// <see cref="AppPasswordAuthenticationDefaults.Realm"/>; printable ASCII without <c>"</c> or <c>\</c>.
    /// </summary>
    public string Realm { get; set; } = AppPasswordAuthenticationDefaults.Realm;

    /// <summary>
    /// Reads a feed token from a request, or returns <see langword="null"/> when the request is not for a feed. A
    /// request it returns a token for is authenticated by that token alone, and a refused one is answered
    /// <c>404</c> instead of a challenge. Default: <c>{/dav}/feeds/{token}.ics</c> through
    /// <see cref="AppPasswordFeedPaths.CreateReader"/>; set it to the reader of the DAV host's prefix, or to
    /// <see langword="null"/> to accept no feed tokens.
    /// </summary>
    public Func<HttpRequest, string?>? FeedTokenReader { get; set; } =
        AppPasswordFeedPaths.CreateReader(AppPasswordFeedPaths.DefaultPrefix);

    /// <inheritdoc />
    public override void Validate() {
        base.Validate();
        if (string.IsNullOrEmpty(Realm) || Realm.Any(c => c is < ' ' or > '~' or '"' or '\\'))
            throw new InvalidOperationException(
                "AppPasswordAuthenticationOptions.Realm must be printable ASCII without '\"' or '\\'.");
    }
}
